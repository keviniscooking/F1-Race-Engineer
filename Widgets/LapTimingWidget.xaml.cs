using System.Windows;
using System.Windows.Controls;

namespace F1RaceEngineer.Widgets
{
    public partial class LapTimingWidget : UserControl
    {
        public static readonly DependencyProperty ShowHistoryProperty =
            DependencyProperty.Register(nameof(ShowHistory), typeof(bool), typeof(LapTimingWidget), new PropertyMetadata(true));

        public bool ShowHistory
        {
            get => (bool)GetValue(ShowHistoryProperty);
            set => SetValue(ShowHistoryProperty, value);
        }

        public LapTimingWidget()
        {
            InitializeComponent();
        }
    }
}
