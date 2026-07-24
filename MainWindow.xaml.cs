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
        // System32-only search: dwmapi.dll is a Windows system library, so pinning the search
        // path stops a same-named DLL beside the .exe being loaded instead (DLL preloading).
        // The app installs per-user via Velopack, so its own directory is user-writable.
        [DllImport("dwmapi.dll", PreserveSig = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaCaptionColor = 35;
        private const int DwmwaTextColor = 36;

        private readonly UdpListenerService _listener = new();

        // True once ANY telemetry packet has arrived this run - see the subscription in the
        // constructor. Only read by the help card, to tell "nothing is being sent" apart from
        // "data is flowing but this session type has no layout".
        private bool _hasReceivedPacket;
        private readonly TelemetryState _state = new();
        private readonly AppStateStore _appState = new();

        // The UDP port, owned here rather than by a TextBox. It used to live in the top bar as
        // editable text, which made a control the source of truth and forced Settings to write
        // back into it; now Settings edits this and the top bar only displays it.
        private int _port = AppStateStore.DefaultPort;

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
                [PresetType.Practice] = BuildToggleSet(defaultEnabled: false, historyDefault: true, sessionDefault: true, conditionDefault: true),
                [PresetType.Qualifying] = BuildToggleSet(defaultEnabled: false, historyDefault: true, sessionDefault: true, conditionDefault: true),
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
                UpdateConnectionBarVisibility();
            });

            _listener.Stopped += () => Dispatcher.Invoke(() =>
            {
                StatusText.Text = "Not connected";
                StatusText.Foreground = Brushes.OrangeRed;
                UpdateConnectionBarVisibility();
            });

            _listener.ErrorOccurred += ex => Dispatcher.Invoke(() =>
            {
                SetError($"Error: {ex.Message}");
            });

            SettingsFlyoutContent.PreviewPresetRequested += _state.DebugForcePreset;
            // The waiting screen's "not receiving data?" link opens the help card, same as the ? icon.
            WaitingPlaceholder.HelpRequested += (_, _) => HelpButton.IsChecked = true;

            // The catalog's column count is width-derived, so re-evaluate it whenever the grid's
            // own width changes - not just on toggle changes. Watching the grid (rather than the
            // window) also catches the width shifting because the tower appeared/disappeared.
            WidgetGrid.SizeChanged += WidgetGrid_SizeChanged;

            _state.Attach(_listener);

            // Distinguishes "bound to the port but the game has never sent anything" from
            // "receiving fine, just not in a supported session type" - the help card gives those
            // two very different advice, and CurrentPreset alone can't tell them apart. Set once;
            // the handler unsubscribes itself so it costs nothing for the rest of the session.
            void FirstPacket(F1Game.UDP.Packets.UnionPacket _)
            {
                _hasReceivedPacket = true;
                _listener.PacketReceived -= FirstPacket;
                Dispatcher.Invoke(UpdateConnectionBarVisibility);
            }
            _listener.PacketReceived += FirstPacket;

            SettingsFlyoutContent.ConnectRequested += text =>
            {
                if (!int.TryParse(text?.Trim(), out int port) || port is < 1 or > 65535)
                {
                    SetError("Error: Port must be a number between 1 and 65535.");
                    return;
                }
                SetError("");
                _port = port;
                PortLabel.Text = $"Port {_port}";
                SaveAppState();          // persist immediately - a changed port that vanished on
                                         // restart would look like the app was broken, not forgetful
                if (_listener.IsRunning) _listener.Stop();
                Connect();
                SettingsFlyoutContent.ShowConnection(_port.ToString(), _listener.IsRunning, ConnectionSummary());
            };
            SettingsFlyoutContent.DisconnectRequested += () =>
            {
                if (_listener.IsRunning) _listener.Stop();
                SettingsFlyoutContent.ShowConnection(_port.ToString(), _listener.IsRunning, ConnectionSummary());
            };

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
            // an otherwise dark window. All three HRESULTs are deliberately ignored: these
            // are cosmetic-only attributes, unsupported on older Windows builds, where the
            // correct behaviour is to leave the default chrome rather than fail startup.
            var hwnd = new WindowInteropHelper(this).Handle;
            int useDarkMode = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDarkMode, sizeof(int));

            int captionColor = 0x0017110D; // 0x00BBGGRR for #0D1117
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColor, sizeof(int));

            int textColor = 0x00F3EDE6; // 0x00BBGGRR for #E6EDF3
            DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref textColor, sizeof(int));
        }

        /// <summary>
        /// Loads persisted state before the window is shown: the port (so the launch connect uses
        /// the remembered one, not the default) and the window placement.
        /// </summary>
        private void RestoreWindowPlacement()
        {
            var state = _appState.Load();
            _port = state.Port;
            PortLabel.Text = $"Port {_port}";

            var saved = state.Window;
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
            // Release the UDP port deterministically rather than relying on process teardown to
            // do it. Closing and immediately relaunching could otherwise hit the "another app is
            // already bound to this port" failure README troubleshoots - against itself. Placed
            // before the early return below so it runs on both paths; Stop() is idempotent, and
            // its Stopped handler marshals through the still-live dispatcher on this same thread.
            _listener.Stop();

            // RestoreBounds is the normal-state rect even when the window is maximized/minimized,
            // so un-maximizing next launch returns to a sensible size. Left/Top/Width/Height would
            // report the maximized frame instead, which isn't what we want to persist.
            var bounds = RestoreBounds;
            // Empty only if the window was never actually shown - don't clobber a good saved
            // placement with zeros in that case.
            if (bounds.IsEmpty) { base.OnClosing(e); return; }
            SaveAppState(new AppStateStore.WindowPlacement
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
        private ObservableCollection<WidgetToggle> BuildToggleSet(bool defaultEnabled, bool? historyDefault,
            bool? sessionDefault = null, bool? conditionDefault = null)
        {
            var toggles = new ObservableCollection<WidgetToggle>();

            if (historyDefault.HasValue)
                toggles.Add(new WidgetToggle { Key = "history", Label = "Lap History", IsEnabled = historyDefault.Value });

            // session and condition can opt out of the blanket defaultEnabled: Practice and Qualifying
            // turn them on (with Lap History) while leaving tyres/penalties off, per user preference.
            toggles.Add(new() { Key = "session", Label = "Session & Track", IsEnabled = sessionDefault ?? defaultEnabled });
            toggles.Add(new() { Key = "tyres", Label = "Tyres", IsEnabled = defaultEnabled });
            toggles.Add(new() { Key = "condition", Label = "Car Condition", IsEnabled = conditionDefault ?? defaultEnabled });
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
            bool wasVisible = WaitingPlaceholder.Visibility == Visibility.Visible;
            WaitingPlaceholder.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;

            // Start/stop the formation-lap animation with visibility - the hard rule is it must
            // NEVER tick while racing. Only act on an actual transition so a rebuild isn't kicked
            // off every LapData refresh.
            if (waiting && !wasVisible) WaitingPlaceholder.Start();
            else if (!waiting && wasVisible) WaitingPlaceholder.Stop();
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

            // Lap Timing's 6px bottom margin exists to separate it from the catalog widgets. With
            // every catalog widget toggled off there's nothing to separate from, but the margin
            // still reserved its 6px - so this column stopped 6px short of the Position board
            // beside it, which has no bottom margin and reaches the full height. Measured exactly:
            // board bottom 1384, lap timing 1378. Most visible on Qualifying and Practice, where
            // the catalog defaults to off.
            LapTiming.Margin = new Thickness(0, 0, 0, visible.Count > 0 ? 6 : 0);

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
        // still hidden, before it re-took its 436px) and lay out the wrong number of columns.
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

                // The last row carries no bottom margin so the widgets reach the column's
                // bottom edge and line up with the Position Tower to the left (which has no
                // bottom margin either and stretches full-height). Earlier rows keep the 6 so a
                // 6+6=12 gap sits between wrapped rows. The top 6 is uniform: on row 0 it pairs
                // with Lap Timing's own 6px bottom margin (12 again), on later rows with the 6
                // from the row above.
                double bottom = r == rows - 1 ? 0 : 6;
                for (int c = 0; c < countInRow; c++)
                {
                    var widget = visibleWidgetsInOrder[start + c];
                    Grid.SetColumn(widget, c);
                    double left = c == 0 ? 0 : 6;
                    double right = c == countInRow - 1 ? 0 : 6;
                    widget.Margin = new Thickness(left, 6, right, bottom);
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

        /// <summary>
        /// Esc closes whichever panel is open. Without it the only way out of the history overlay
        /// was the same icon that opened it, which several users won't find - the preset pills
        /// above look like tabs but are a read-only session indicator, so clicking them to "leave"
        /// does nothing and the app reads as stuck. Handled on the window (not the panel) so it
        /// works wherever focus happens to be, and only when something is actually open so Esc
        /// stays free otherwise.
        /// </summary>
        protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                // Unchecking routes through each button's Unchecked handler, so the icon
                // un-highlights too rather than being left looking active over a closed panel.
                if (HelpButton.IsChecked == true) { HelpButton.IsChecked = false; e.Handled = true; }
                else if (HistoryButton.IsChecked == true) { HistoryButton.IsChecked = false; e.Handled = true; }
            }
            base.OnPreviewKeyDown(e);
        }

        /// <summary>
        /// Hides the port / Connect / status group once the connection is proven, and shows it
        /// whenever it isn't. The top bar spent its whole left third on plumbing that is idle in
        /// the overwhelming majority of sessions - the app auto-connects on launch - so it now
        /// only appears when there is actually something to do about it.
        ///
        /// Safe to collapse with no layout consequence, and that's by design rather than luck:
        /// every element in this row carries an explicit Height="24", so the row can't change
        /// height, and this group sits in a STAR column, so the preset pills stay centred on the
        /// window and the icons stay pinned right regardless.
        ///
        /// The condition uses _hasReceivedPacket, which is once-true-forever on purpose: if the
        /// game is closed mid-session the data stops but the port is still proven good, and the
        /// controls flapping back in would be noise, not information.
        /// </summary>
        /// <summary>
        /// Writes the port plus the given placement. Called on close with the real geometry, and
        /// on a port change with null - which preserves whatever placement is already on disk, so
        /// saving a port mid-session can't wipe the remembered window position.
        /// </summary>
        private void SaveAppState(AppStateStore.WindowPlacement? placement = null)
        {
            var state = _appState.Load();
            state.Port = _port;
            if (placement != null) state.Window = placement;
            _appState.Save(state);
        }

        private void UpdateConnectionBarVisibility()
        {
            bool proven = _listener.IsRunning && _hasReceivedPacket;
            ConnectionBar.Visibility = proven ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---- telemetry help ----

        /// <summary>Hands the user straight to the port field rather than just naming where it is.</summary>
        private void HelpOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            HelpButton.IsChecked = false;    // one panel at a time; also closes the popup
            SettingsButton.IsChecked = true; // routes through Checked, so state is pushed in first
        }

        private void HelpButton_Checked(object sender, RoutedEventArgs e)
        {
            RefreshHelpCard();
            HelpPopup.IsOpen = true;
        }

        private void HelpButton_Unchecked(object sender, RoutedEventArgs e) => HelpPopup.IsOpen = false;

        // Click-away closes the Popup itself (StaysOpen=False); this puts the toggle back in sync
        // so the icon doesn't stay lit over a card that's gone.
        private void HelpPopup_Closed(object sender, EventArgs e) => HelpButton.IsChecked = false;

        /// <summary>
        /// Fills the help card from live state rather than static text. The port echoes the port
        /// box - printing a hardcoded 20777 would actively mislead anyone who changed it - and the
        /// status line names what the app can actually see, because "connected but no data" and
        /// "not listening at all" have completely different causes.
        /// </summary>
        private void RefreshHelpCard()
        {
            HelpPortValue.Text = _port.ToString();

            string text;
            string dot;
            if (!_listener.IsRunning)
            {
                dot = "#F85149";
                text = "Not listening. Press Connect above, then set the values below in the game.";
            }
            else if (_state.CurrentPreset == PresetType.Unsupported && !_hasReceivedPacket)
            {
                dot = "#E8C52E";
                text = "Listening, but no telemetry has arrived yet — the settings below are usually why.";
            }
            else
            {
                dot = "#3FB950";
                text = "Receiving telemetry. Everything is set up correctly.";
            }
            HelpStatusDot.Fill = (SolidColorBrush)new BrushConverter().ConvertFromString(dot)!;
            HelpStatusText.Text = text;
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
            // Push live connection state in each time it opens, rather than binding - the panel
            // holds no listener of its own, so this keeps one owner for the socket.
            SettingsFlyoutContent.ShowConnection(_port.ToString(), _listener.IsRunning, ConnectionSummary());
            SettingsPopup.IsOpen = true;
        }

        /// <summary>Shared wording for the settings panel and the help card, so they can't drift.</summary>
        private string ConnectionSummary()
        {
            if (!_listener.IsRunning) return "Not listening.";
            return _hasReceivedPacket
                ? "Receiving telemetry."
                : "Listening — no telemetry has arrived yet.";
        }

        private void SettingsButton_Unchecked(object sender, RoutedEventArgs e)
        {
            SettingsPopup.IsOpen = false;
        }

        private void SettingsPopup_Closed(object? sender, EventArgs e)
        {
            SettingsButton.IsChecked = false;
        }

        /// <summary>
        /// The top-bar status line is a shortcut to the controls that can fix it - it's only ever
        /// visible when the connection needs attention, so the click that follows reading it
        /// should land somewhere useful rather than doing nothing.
        /// </summary>
        private void ConnectionStatus_Click(object sender, RoutedEventArgs e) => SettingsButton.IsChecked = true;

        private void Connect()
        {
            int port = _port;
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
