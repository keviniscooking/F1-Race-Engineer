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

        // Only CollectionUnchanged<T> compares these today, and its IEquatable<T> constraint
        // binds the typed overload above - but without these two, any hash-based or
        // non-generic path (Distinct, HashSet, object.Equals) would silently fall back to
        // reference identity and disagree with Equals. Same trio as PenaltyEntry. The hash
        // uses a subset of the equality fields, which is valid: equal instances agree on
        // them, so equal instances always hash equal.
        public override bool Equals(object? obj) => Equals(obj as CarStanding);
        public override int GetHashCode() => HashCode.Combine(Position, DriverName, TeamName);
    }
}
