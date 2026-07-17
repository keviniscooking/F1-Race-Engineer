using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using F1RaceEngineer.Models;
using F1RaceEngineer.Telemetry;

namespace F1RaceEngineer
{
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaCaptionColor = 35;
        private const int DwmwaTextColor = 36;

        private readonly UdpListenerService _listener = new();
        private readonly TelemetryState _state = new();
        private readonly WindowStateStore _windowState = new();

        private static readonly SolidColorBrush ActiveTabBrush = new(Color.FromRgb(0x1C, 0x27, 0x33));
        private static readonly SolidColorBrush InactiveTabBrush = Brushes.Transparent;

        // Catalog widgets toggleable via the settings panel. Key order here is also
        // display order in both the settings panel and the adaptive grid.
        private Dictionary<string, FrameworkElement> _catalogWidgetsByKey = null!;
        private Dictionary<PresetType, ObservableCollection<WidgetToggle>> _togglesByPreset = null!;

        public MainWindow()
        {
            InitializeComponent();

            // Restore the remembered geometry before the window is shown, so it opens where the
            // user last left it rather than flashing at the default size first. Falls back to the
            // XAML defaults if there's nothing saved or the saved spot is now off-screen.
            RestoreWindowPlacement();

            LapTiming.DataContext = _state;
            PositionList.DataContext = _state;
            Alert.DataContext = _state;
            RaceTower.DataContext = _state;

            TyresCtl.DataContext = _state;
            CarConditionCtl.DataContext = _state;
            PenaltiesFlagsCtl.DataContext = _state;
            SessionTrackCtl.DataContext = _state;

            _catalogWidgetsByKey = new Dictionary<string, FrameworkElement>
            {
                ["session"] = SessionTrackCtl,
                ["tyres"] = TyresCtl,
                ["condition"] = CarConditionCtl,
                ["penalties"] = PenaltiesFlagsCtl
            };

            // Lap History is now on-by-default and toggleable on ALL presets (Qualifying
            // included, for uniformity), so every preset passes historyDefault: true. The
            // catalog widgets default off outside Race, keeping Practice/Qualifying focused
            // on the timing board + lap timing unless the user toggles more on.
            _togglesByPreset = new Dictionary<PresetType, ObservableCollection<WidgetToggle>>
            {
                [PresetType.Practice] = BuildToggleSet(defaultEnabled: false, historyDefault: true),
                [PresetType.Qualifying] = BuildToggleSet(defaultEnabled: false, historyDefault: true),
                [PresetType.Race] = BuildToggleSet(defaultEnabled: true, historyDefault: true),
                [PresetType.Unsupported] = BuildToggleSet(defaultEnabled: false, historyDefault: true)
            };

            _state.PropertyChanged += State_PropertyChanged;
            // If a race is auto-saved while the history overlay happens to be open, refresh it
            // live; otherwise it'll pick the new race up next time it's opened (Reload on show).
            _state.RaceSaved += _ => Dispatcher.Invoke(() =>
            {
                if (HistoryButton.IsChecked == true) History.Reload();
            });
            UpdatePresetTabs();
            UpdateWidgetVisibility();

            _listener.Started += () => Dispatcher.Invoke(() =>
            {
                StatusText.Text = "Connected, waiting for data...";
                StatusText.Foreground = Brushes.LimeGreen;
                ConnectButton.Content = "Disconnect";
            });

            _listener.Stopped += () => Dispatcher.Invoke(() =>
            {
                StatusText.Text = "Not connected";
                StatusText.Foreground = Brushes.OrangeRed;
                ConnectButton.Content = "Connect";
            });

            _listener.ErrorOccurred += ex => Dispatcher.Invoke(() =>
            {
                SetError($"Error: {ex.Message}");
            });

            SettingsFlyoutContent.PreviewPresetRequested += _state.DebugForcePreset;

            // The catalog's column count is width-derived, so re-evaluate it whenever the grid's
            // own width changes - not just on toggle changes. Watching the grid (rather than the
            // window) also catches the width shifting because the tower appeared/disappeared.
            WidgetGrid.SizeChanged += WidgetGrid_SizeChanged;

            _state.Attach(_listener);

            // Try once on launch so the common case (game already configured to send to
            // the default port) needs no manual click. Deliberately no retry loop if
            // this fails (e.g. port already in use) - the Connect button is still right
            // there for a manual attempt, same as if this line didn't exist.
            Connect();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // The native window chrome defaults to light-mode regardless of the app's own
            // dark theme; without this the title bar is a jarring light-pink strip above
            // an otherwise dark window.
            var hwnd = new WindowInteropHelper(this).Handle;
            int useDarkMode = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDarkMode, sizeof(int));

            int captionColor = 0x0017110D; // 0x00BBGGRR for #0D1117
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColor, sizeof(int));

            int textColor = 0x00F3EDE6; // 0x00BBGGRR for #E6EDF3
            DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref textColor, sizeof(int));
        }

        private void RestoreWindowPlacement()
        {
            var saved = _windowState.Load();
            if (saved == null) return;

            // Never trust the saved size below the XAML minimums - a corrupt or hand-edited file
            // shouldn't be able to open the window smaller than the layout can survive.
            if (saved.Width < MinWidth || saved.Height < MinHeight) return;

            // Guard against a placement saved on a monitor that's since been unplugged: only honour
            // it if the window's rect still meaningfully overlaps the current virtual desktop.
            // Otherwise the window would open completely off-screen with no easy way to reach it.
            var windowRect = new Rect(saved.Left, saved.Top, saved.Width, saved.Height);
            var virtualScreen = new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var overlap = Rect.Intersect(windowRect, virtualScreen);
            if (overlap.IsEmpty || overlap.Width < 100 || overlap.Height < 100) return;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.Left;
            Top = saved.Top;
            Width = saved.Width;
            Height = saved.Height;
            // Apply Maximized after the normal-state bounds are set, so the system remembers those
            // bounds as the restore size when the user un-maximizes.
            if (saved.Maximized) WindowState = WindowState.Maximized;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // RestoreBounds is the normal-state rect even when the window is maximized/minimized,
            // so un-maximizing next launch returns to a sensible size. Left/Top/Width/Height would
            // report the maximized frame instead, which isn't what we want to persist.
            var bounds = RestoreBounds;
            // Empty only if the window was never actually shown - don't clobber a good saved
            // placement with zeros in that case.
            if (bounds.IsEmpty) { base.OnClosing(e); return; }
            _windowState.Save(new WindowStateStore.WindowPlacement
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = WindowState == WindowState.Maximized
            });

            base.OnClosing(e);
        }

        /// <summary>
        /// historyDefault is nullable so a preset could omit the "Lap History" checkbox
        /// entirely (null) - currently every preset passes true (history on, toggleable),
        /// but the nullable path is kept for a future preset that shouldn't offer history.
        /// </summary>
        private ObservableCollection<WidgetToggle> BuildToggleSet(bool defaultEnabled, bool? historyDefault)
        {
            var toggles = new ObservableCollection<WidgetToggle>();

            if (historyDefault.HasValue)
                toggles.Add(new WidgetToggle { Key = "history", Label = "Lap History", IsEnabled = historyDefault.Value });

            toggles.Add(new() { Key = "session", Label = "Session & Track", IsEnabled = defaultEnabled });
            toggles.Add(new() { Key = "tyres", Label = "Tyres", IsEnabled = defaultEnabled });
            toggles.Add(new() { Key = "condition", Label = "Car Condition", IsEnabled = defaultEnabled });
            toggles.Add(new() { Key = "penalties", Label = "Penalties & Flags", IsEnabled = defaultEnabled });

            foreach (var toggle in toggles)
            {
                toggle.PropertyChanged += (_, _) => ApplyCatalogWidgetLayout();
            }

            return toggles;
        }

        private void State_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // No Dispatcher.Invoke needed here: TelemetryState only ever raises
            // PropertyChanged from the UI thread already (packet handling is marshaled
            // in OnPacketReceived, and DebugForcePreset is only called from a UI event).
            if (e.PropertyName == nameof(TelemetryState.CurrentPreset))
            {
                UpdatePresetTabs();
                UpdateWidgetVisibility();
            }
            else if (e.PropertyName == nameof(TelemetryState.IsTimeTrial))
            {
                // A session-type change (e.g. menu -> Time Trial) can leave the preset at
                // Unsupported yet needs the placeholder re-evaluated.
                UpdateWaitingPlaceholder();
            }
        }

        // "Waiting for a session" empty state (see WaitingPlaceholder in XAML): shown for any
        // Unsupported-preset state - cold start (no game) AND the game sitting in its menus
        // (which streams data with an unmapped session type) - EXCEPT a live Time Trial, which
        // is a real drivable session where the Lap Timing layout is useful. A settings-menu
        // preview forces a concrete preset, which also drops the placeholder.
        private void UpdateWaitingPlaceholder()
        {
            bool waiting = _state.CurrentPreset == PresetType.Unsupported && !_state.IsTimeTrial;
            WaitingPlaceholder.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateWidgetVisibility()
        {
            // Uniform layout across all three presets: the same Lap Timing widget (with
            // history) fills the right column everywhere, and the LEFT column holds the
            // full-field view - the Race Position Tower in Race, the position/timing board
            // in Practice & Qualifying (both timed leaderboard sessions where the driver
            // wants to see where they rank). The two left-column widgets share one column
            // and are mutually exclusive per preset.
            var preset = _state.CurrentPreset;
            bool boardPreset = preset is PresetType.Practice or PresetType.Qualifying;
            LapTiming.Visibility = Visibility.Visible;
            // Practice/Qualifying only - NOT Unsupported (Time Trial), where the board would
            // just be empty (no field to rank).
            PositionList.Visibility = boardPreset ? Visibility.Visible : Visibility.Collapsed;
            RaceTower.Visibility = preset == PresetType.Race ? Visibility.Visible : Visibility.Collapsed;

            UpdateWaitingPlaceholder();
            ApplyCatalogWidgetLayout();
        }

        private void ApplyCatalogWidgetLayout()
        {
            var toggles = _togglesByPreset[_state.CurrentPreset];
            SettingsFlyoutContent.DataContext = toggles;

            var visible = new List<FrameworkElement>();
            foreach (var toggle in toggles)
            {
                if (toggle.Key == "history")
                {
                    LapTiming.ShowHistory = toggle.IsEnabled;
                    continue;
                }

                var widget = _catalogWidgetsByKey[toggle.Key];
                widget.Visibility = toggle.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
                if (toggle.IsEnabled) visible.Add(widget);
            }

            _lastCatalogColumns = ColumnsFor(visible.Count, WidgetGrid.ActualWidth);
            ArrangeWidgets(WidgetGrid, _catalogWidgetsByKey.Values, visible);
        }

        /// <summary>
        /// Column count comes from the available WIDTH, not the widget count: a count-based split
        /// left each card stretched to ~800px at a maximised window with its content filling barely
        /// half, while the same 2x2 was cramped on a small one. ~400px per card keeps them at a
        /// sensible size at any window - 4 across maximised, 2x2 around the default width - and
        /// taking one row instead of two hands the freed height to Lap History above.
        /// </summary>
        // Minimum comfortable card width. Chosen so a card is wide enough for its issue chips to
        // read without truncating ("Warning - Track limits (x2)" rather than "Warning - Track li..."):
        // a 1920x1080 window lands on a roomy 2x2 rather than a cramped 4-across. Lower it and more
        // columns fit at mid widths, at the cost of clipped chip text.
        private const double TargetCatalogCardWidth = 400;

        // Last column count actually applied, so the WidgetGrid's own SizeChanged can re-arrange
        // only when the answer really changes. Without reacting to the grid's width specifically,
        // the arrange can run against a stale ActualWidth (e.g. measured while the Race tower was
        // still hidden, before it re-took its 400px) and lay out the wrong number of columns.
        private int _lastCatalogColumns = -1;

        private void WidgetGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            int visibleCount = 0;
            foreach (var w in _catalogWidgetsByKey.Values) if (w.Visibility == Visibility.Visible) visibleCount++;
            if (visibleCount == 0) return;
            if (ColumnsFor(visibleCount, e.NewSize.Width) != _lastCatalogColumns) ApplyCatalogWidgetLayout();
        }

        // Never fewer than 2 columns: a single column is the TALLEST possible arrangement, so on a
        // narrow window - exactly where vertical space is scarcest - it would stack every card and
        // push the last one off the bottom. Two columns halves that height and still leaves each
        // card ~360px at the minimum width.
        private const int MinCatalogColumns = 2;

        private static int ColumnsFor(int widgetCount, double hostWidth)
        {
            if (hostWidth <= 0) return Math.Min(widgetCount, MinCatalogColumns); // pre-layout: sane default
            int byWidth = (int)(hostWidth / TargetCatalogCardWidth);
            int cols = Math.Min(widgetCount, Math.Max(MinCatalogColumns, byWidth));

            // Never strand a single widget alone on the last row. ArrangeWidgets stretches a row's
            // widgets across the full width, so a lone leftover renders as one giant card beside a
            // row of normal ones (4 widgets in 3 columns = 3 tidy cards + 1 stretched to ~1460px).
            // Dropping a column re-balances it (3+1 -> 2+2) at the cost of one extra row.
            while (cols > MinCatalogColumns && widgetCount % cols == 1) cols--;
            return cols;
        }

        /// <summary>
        /// Rebuilds the widget area as a stack of row-Grids, each containing only the
        /// widgets that belong in that row and sized with exactly that many equal-width
        /// star columns. This is what makes the LAST row (which may have fewer items than
        /// the target column count) stretch evenly across the full width instead of
        /// leaving a dead gap - a fixed global column count can't do that, since every row
        /// would share the same column boundaries regardless of how many items are in it.
        /// Rows are Auto-height (not star), so a short widget (e.g. a one-line "OK" badge)
        /// doesn't get stretched into a tall empty card next to a denser neighbour.
        ///
        /// Margin is assigned here per-widget (not in XAML) so the whole grid's OUTER edges
        /// line up exactly with Lap Timing/Position List above it (which carry no horizontal
        /// margin of their own, i.e. span the full column width): 0 on a widget's outer-
        /// facing sides, 6 on sides facing a neighbour (6+6=12, matching the gap used
        /// everywhere else in the app). A static per-widget XAML margin can't do this
        /// because which widget ends up in the leftmost/rightmost column is dynamic,
        /// depending on which widgets are currently toggled on.
        /// </summary>
        private static void ArrangeWidgets(Grid host, IEnumerable<FrameworkElement> allCatalogWidgets, IReadOnlyList<FrameworkElement> visibleWidgetsInOrder)
        {
            foreach (var widget in allCatalogWidgets)
            {
                if (widget.Parent is Panel oldParent) oldParent.Children.Remove(widget);
            }

            host.Children.Clear();
            host.RowDefinitions.Clear();
            host.ColumnDefinitions.Clear();

            int n = visibleWidgetsInOrder.Count;
            if (n == 0) return;

            host.ColumnDefinitions.Add(new ColumnDefinition());

            int cols = ColumnsFor(n, host.ActualWidth);
            int rows = (int)Math.Ceiling(n / (double)cols);
            for (int r = 0; r < rows; r++)
                host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int r = 0; r < rows; r++)
            {
                int start = r * cols;
                int countInRow = Math.Min(cols, n - start);

                var rowGrid = new Grid();
                for (int c = 0; c < countInRow; c++)
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                for (int c = 0; c < countInRow; c++)
                {
                    var widget = visibleWidgetsInOrder[start + c];
                    Grid.SetColumn(widget, c);
                    double left = c == 0 ? 0 : 6;
                    double right = c == countInRow - 1 ? 0 : 6;
                    widget.Margin = new Thickness(left, 6, right, 6);
                    rowGrid.Children.Add(widget);
                }

                Grid.SetRow(rowGrid, r);
                host.Children.Add(rowGrid);
            }
        }

        private void UpdatePresetTabs()
        {
            PracticeTab.Background = _state.CurrentPreset == PresetType.Practice ? ActiveTabBrush : InactiveTabBrush;
            QualifyingTab.Background = _state.CurrentPreset == PresetType.Qualifying ? ActiveTabBrush : InactiveTabBrush;
            RaceTab.Background = _state.CurrentPreset == PresetType.Race ? ActiveTabBrush : InactiveTabBrush;
        }

        private void HistoryButton_Checked(object sender, RoutedEventArgs e)
        {
            History.Reload(); // pull in any newly-saved races each time it opens
            History.Visibility = Visibility.Visible;
        }

        private void HistoryButton_Unchecked(object sender, RoutedEventArgs e)
        {
            History.Visibility = Visibility.Collapsed;
        }

        private void SettingsButton_Checked(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = true;
        }

        private void SettingsButton_Unchecked(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;
        }

        private void SettingsPopup_Closed(object? sender, EventArgs e)
        {
            SettingsButton.IsChecked = false;
        }

        private void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            SetError("");

            if (_listener.IsRunning)
            {
                _listener.Stop();
                return;
            }

            Connect();
        }

        private void Connect()
        {
            if (!int.TryParse(PortTextBox.Text, out int port))
            {
                SetError("Error: Port must be a number.");
                return;
            }

            try
            {
                _listener.Start(port);
            }
            catch (Exception ex)
            {
                SetError($"Error: could not bind to port {port}: {ex.Message}");
            }
        }

        // ErrorText is Collapsed by default in XAML so it only reserves vertical space
        // when there's actually an error to show, instead of a permanently blank row.
        private void SetError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
