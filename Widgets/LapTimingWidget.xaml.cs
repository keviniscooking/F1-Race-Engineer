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

        /// <summary>
        /// Distance from this widget's top edge to the bottom of the current-lap / PB / delta
        /// block, measured live. The Alert banner binds its MinHeight to this so it always covers
        /// exactly that block (a deliberate design decision - during a Safety Car / VSC / red flag
        /// the lap times are irrelevant, so the banner takes their place). This used to be a
        /// hand-measured constant on the banner itself with a "re-measure if the font sizes
        /// change" caveat; measuring it here keeps the two in sync automatically.
        /// </summary>
        public static readonly DependencyProperty HeaderExtentProperty =
            DependencyProperty.Register(nameof(HeaderExtent), typeof(double), typeof(LapTimingWidget), new PropertyMetadata(0.0));

        public double HeaderExtent
        {
            get => (double)GetValue(HeaderExtentProperty);
            private set => SetValue(HeaderExtentProperty, value);
        }

        public LapTimingWidget()
        {
            InitializeComponent();
            HeaderBlock.SizeChanged += (_, _) => RefreshHeaderExtent();
            Loaded += (_, _) => RefreshHeaderExtent();
        }

        // Translating the block's bottom-left into this control's space accounts for the card's
        // own padding/border above it, so the banner covers the block itself rather than a
        // constant that only happens to line up today.
        private void RefreshHeaderExtent()
        {
            if (!HeaderBlock.IsArrangeValid || !IsArrangeValid) return;
            HeaderExtent = HeaderBlock.TranslatePoint(new Point(0, HeaderBlock.ActualHeight), this).Y;
        }
    }
}
