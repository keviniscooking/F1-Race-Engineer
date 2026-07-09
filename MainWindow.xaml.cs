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

        private static readonly SolidColorBrush ActiveTabBrush = new(Color.FromRgb(0x1C, 0x27, 0x33));
        private static readonly SolidColorBrush InactiveTabBrush = Brushes.Transparent;

        // Catalog widgets toggleable via the settings panel. Key order here is also
        // display order in both the settings panel and the adaptive grid.
        private Dictionary<string, FrameworkElement> _catalogWidgetsByKey = null!;
        private Dictionary<PresetType, ObservableCollection<WidgetToggle>> _togglesByPreset = null!;

        public MainWindow()
        {
            InitializeComponent();

            LapTiming.DataContext = _state;
            QualifyingLapTiming.DataContext = _state;
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

            // Qualifying's core (Position List + history-less Lap Timing) is always on;
            // the catalog widgets below default off there too, matching Practice, so the
            // qualifying view stays focused on the hotlap unless the user wants more.
            _togglesByPreset = new Dictionary<PresetType, ObservableCollection<WidgetToggle>>
            {
                [PresetType.Practice] = BuildToggleSet(defaultEnabled: false, historyDefault: true),
                [PresetType.Qualifying] = BuildToggleSet(defaultEnabled: false, historyDefault: null),
                [PresetType.Race] = BuildToggleSet(defaultEnabled: true, historyDefault: true),
                [PresetType.Unsupported] = BuildToggleSet(defaultEnabled: false, historyDefault: true)
            };

            _state.PropertyChanged += State_PropertyChanged;
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

        /// <summary>
        /// historyDefault is null for presets that don't use the toggleable Lap Timing
        /// instance (Qualifying always shows its own fixed history-less Lap Timing), so no
        /// "Lap History" checkbox is shown there at all.
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
        }

        private void UpdateWidgetVisibility()
        {
            // Per design: Qualifying shows the position list as its always-on core,
            // with a history-less Lap Timing widget on top so the driver still knows
            // where they are. Practice and Race show the full Lap Timing widget (with
            // history) instead.
            bool isQualifying = _state.CurrentPreset == PresetType.Qualifying;
            LapTiming.Visibility = isQualifying ? Visibility.Collapsed : Visibility.Visible;
            QualifyingLapTiming.Visibility = isQualifying ? Visibility.Visible : Visibility.Collapsed;
            PositionList.Visibility = isQualifying ? Visibility.Visible : Visibility.Collapsed;

            // Race-only: the tower needs live interval/gap-to-leader, which only means
            // anything mid-race. Its host column is Auto-width, so it collapses to zero
            // width (taking its own Margin with it) rather than leaving a dead gap.
            RaceTower.Visibility = _state.CurrentPreset == PresetType.Race ? Visibility.Visible : Visibility.Collapsed;

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

            ArrangeWidgets(WidgetGrid, _catalogWidgetsByKey.Values, visible);
        }

        /// <summary>
        /// Column count grows with the widget count so a typical "1 main + 4 others"
        /// Race layout stays a clean 2x2, while 5+ widgets fold into 3 columns instead of
        /// a taller 2-column stack - fewer rows means less risk of the page not fitting a
        /// second-screen viewport without scrolling.
        /// </summary>
        private static int ColumnsFor(int widgetCount) => widgetCount switch
        {
            <= 1 => 1,
            <= 4 => 2,
            _ => 3
        };

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

            int cols = ColumnsFor(n);
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
