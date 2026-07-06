using System;
using System.Windows.Media;

namespace F1RaceEngineer.Models
{
    public class CarStanding : IEquatable<CarStanding>
    {
        public int Position { get; set; }
        public string DriverName { get; set; } = "";
        public string TeamName { get; set; } = "";
        public SolidColorBrush LiveryBrush { get; set; } = TimingColorPalette.NeutralText;
        public string BestLapText { get; set; } = "-";
        public string GapText { get; set; } = "-";
        public bool IsPlayer { get; set; }

        // LiveryBrush compares by reference (not value) deliberately - brushes are cached
        // per participant and frozen, so a real change always means a different instance.
        public bool Equals(CarStanding? other) => other != null &&
            Position == other.Position &&
            DriverName == other.DriverName &&
            TeamName == other.TeamName &&
            BestLapText == other.BestLapText &&
            GapText == other.GapText &&
            IsPlayer == other.IsPlayer &&
            ReferenceEquals(LiveryBrush, other.LiveryBrush);
    }
}
