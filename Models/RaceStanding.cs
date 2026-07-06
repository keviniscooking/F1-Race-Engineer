using System;
using System.Windows.Media;

namespace F1RaceEngineer.Models
{
    public class RaceStanding : IEquatable<RaceStanding>
    {
        public int Position { get; set; }
        public string DriverName { get; set; } = "";
        public string TeamName { get; set; } = "";
        public SolidColorBrush LiveryBrush { get; set; } = TimingColorPalette.NeutralText;
        public bool IsPlayer { get; set; }
        public string IntervalText { get; set; } = "-";
        public string GapText { get; set; } = "-";

        // LiveryBrush compares by reference (not value) deliberately - brushes are cached
        // per participant and frozen, so a real change always means a different instance.
        public bool Equals(RaceStanding? other) => other != null &&
            Position == other.Position &&
            DriverName == other.DriverName &&
            TeamName == other.TeamName &&
            IsPlayer == other.IsPlayer &&
            IntervalText == other.IntervalText &&
            GapText == other.GapText &&
            ReferenceEquals(LiveryBrush, other.LiveryBrush);
    }
}
