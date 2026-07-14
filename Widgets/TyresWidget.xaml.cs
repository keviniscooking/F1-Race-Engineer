using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using F1RaceEngineer.Telemetry;

namespace F1RaceEngineer.Widgets
{
    public partial class TyresWidget : UserControl
    {
        // Proportional-width segments can't be expressed cleanly in pure XAML data binding
        // (star-sized columns aren't per-item bindable), so the stint bar is rebuilt in
        // code-behind whenever TelemetryState.TyreStints changes - same approach as
        // MainWindow.ArrangeWidgets uses for the adaptive catalog grid. The layout itself
        // (ghost track, scaling to race length, lap/pit ticks) lives in StintStripRenderer,
        // shared with the History panel's detail bar so the two look identical.
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
            if (DataContext is not TelemetryState state)
            {
                StintBar.Children.Clear();
                StintBar.ColumnDefinitions.Clear();
                StintTicks.Children.Clear();
                return;
            }

            // Scale to the whole race so the bar (a ghost track behind) fills lap by lap; 3px
            // gaps / 3px corners to sit neatly in the compact widget slot.
            StintStripRenderer.Render(StintBar, StintTicks, state.TyreStints,
                state.RaceTotalLaps, showLetters: true, gap: 3, cornerRadius: 3);
        }
    }
}
