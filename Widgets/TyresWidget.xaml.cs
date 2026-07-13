using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using F1RaceEngineer.Telemetry;

namespace F1RaceEngineer.Widgets
{
    public partial class TyresWidget : UserControl
    {
        // Proportional-width segments can't be expressed cleanly in pure XAML data binding
        // (star-sized columns aren't per-item bindable), so the stint bar is rebuilt in
        // code-behind whenever TelemetryState.TyreStints changes - same approach as
        // MainWindow.ArrangeWidgets uses for the adaptive catalog grid.
        private static readonly FontFamily MonoFont = new("Consolas");
        private INotifyCollectionChanged? _stintsSubscription;

        public TyresWidget()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_stintsSubscription != null)
                _stintsSubscription.CollectionChanged -= OnStintsChanged;

            if (DataContext is TelemetryState state)
            {
                _stintsSubscription = state.TyreStints;
                _stintsSubscription.CollectionChanged += OnStintsChanged;
            }

            RebuildStintBar();
        }

        private void OnStintsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildStintBar();

        private void RebuildStintBar()
        {
            StintBar.ColumnDefinitions.Clear();
            StintBar.Children.Clear();

            if (DataContext is not TelemetryState state) return;
            var stints = state.TyreStints;

            for (int i = 0; i < stints.Count; i++)
                StintBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(stints[i].LapCount, GridUnitType.Star) });

            for (int i = 0; i < stints.Count; i++)
            {
                var seg = stints[i];
                var border = new Border
                {
                    Background = seg.Brush,
                    CornerRadius = new CornerRadius(3),
                    // 2px gap between segments (none before the first), matching the mockup.
                    Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                    Child = new TextBlock
                    {
                        Text = seg.Letter,
                        Foreground = seg.TextBrush,
                        FontWeight = FontWeights.Bold,
                        FontSize = 12,
                        FontFamily = MonoFont,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                Grid.SetColumn(border, i);
                StintBar.Children.Add(border);
            }
        }
    }
}
