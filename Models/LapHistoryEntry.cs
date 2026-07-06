using System.Windows.Media;

namespace F1RaceEngineer.Models
{
    public class LapHistoryEntry
    {
        public string LapNumberText { get; set; } = "";
        public string LapTimeText { get; set; } = "";
        public string DeltaText { get; set; } = "";
        public SolidColorBrush ColorBrush { get; set; } = TimingColorPalette.NeutralText;
        public string LapTagText { get; set; } = "";
        public bool HasLapTag { get; set; }

        public string Sector1Text { get; set; } = "";
        public SolidColorBrush Sector1Brush { get; set; } = TimingColorPalette.NeutralText;
        public string Sector2Text { get; set; } = "";
        public SolidColorBrush Sector2Brush { get; set; } = TimingColorPalette.NeutralText;
        public string Sector3Text { get; set; } = "";
        public SolidColorBrush Sector3Brush { get; set; } = TimingColorPalette.NeutralText;
    }
}
