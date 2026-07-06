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

        // Only populated on the IN-tagged row (see TelemetryState.RegisterLapTime) - the
        // pit stop's box time is captured at the same tick that row is created, so there's
        // no cross-lap staleness risk like there would be reading it later on the OUT row.
        public string PitStopTimeText { get; set; } = "";

        public string Sector1Text { get; set; } = "";
        public SolidColorBrush Sector1Brush { get; set; } = TimingColorPalette.NeutralText;
        public string Sector2Text { get; set; } = "";
        public SolidColorBrush Sector2Brush { get; set; } = TimingColorPalette.NeutralText;
        public string Sector3Text { get; set; } = "";
        public SolidColorBrush Sector3Brush { get; set; } = TimingColorPalette.NeutralText;
    }
}
