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

        // Populated on both pit-related rows, by two different mechanisms:
        //  - OUT row: the total pit-lane time (entry to exit), set at row-creation time in
        //    RegisterLapTime - it's always finished by the time the OUT lap starts.
        //  - IN row: the stationary box time, patched in retroactively by
        //    PatchMostRecentInRowPitTime once the stop actually finishes (it isn't known
        //    yet when the IN row is first created on line-straddling tracks).
        public string PitStopTimeText { get; set; } = "";

        public string Sector1Text { get; set; } = "";
        public SolidColorBrush Sector1Brush { get; set; } = TimingColorPalette.NeutralText;
        public string Sector2Text { get; set; } = "";
        public SolidColorBrush Sector2Brush { get; set; } = TimingColorPalette.NeutralText;
        public string Sector3Text { get; set; } = "";
        public SolidColorBrush Sector3Brush { get; set; } = TimingColorPalette.NeutralText;
    }
}
