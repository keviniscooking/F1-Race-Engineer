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

        // Retired/DNF/DSQ/not-classified cars report stale zeroed deltas rather than a
        // meaningful gap (confirmed live: a retired car showed "+0.00" for both interval
        // and gap, which reads as "right next to the leader") - IsOut drives the "Out"
        // treatment (dimmed row, "Out" in place of Int/Gap) instead of showing that.
        public bool IsOut { get; set; }

        // Tyre compound letter, from CarStatusData (a separate packet from LapData) -
        // cached per car index in TelemetryState so this row-building method can still
        // read it inline. Blank for IsOut rows, matching the real broadcast graphic.
        // Shown as a plain colored letter (TyreBrush doubles as its Foreground) - no
        // background badge, matching the real broadcast's plain-letter treatment.
        public string TyreLetter { get; set; } = "";
        public SolidColorBrush TyreBrush { get; set; } = CompoundPalette.Unknown;

        // Real F1 broadcast convention: a red "!" badge for a driver with a penalty not
        // yet served (LapData.Penalties = pending time penalty, or an unserved
        // drive-through/stop-go). False for IsOut rows - a retired car has nothing left
        // to serve a penalty during.
        public bool IsPenaltyPending { get; set; }

        // Purple stopwatch badge for whoever currently holds the session's fastest lap -
        // reuses the existing purple = "fastest of the session" convention (Lap Timing
        // colours). Shares the same badge slot as IsPenaltyPending; deliberately set
        // false at the source (TelemetryState.RefreshRaceStandings) whenever
        // IsPenaltyPending is true for the same row, rather than resolving the conflict
        // here in the view - a pending penalty is actionable and wins, matching the
        // priority order the alert banner already uses elsewhere in this app. Also false
        // for IsOut rows, same reasoning as the tyre letter and penalty badge above.
        public bool IsFastestLap { get; set; }

        // LiveryBrush/TyreBrush compare by reference (not value) deliberately - brushes
        // are cached per participant and frozen, so a real change always means a
        // different instance.
        public bool Equals(RaceStanding? other) => other != null &&
            Position == other.Position &&
            DriverName == other.DriverName &&
            TeamName == other.TeamName &&
            IsPlayer == other.IsPlayer &&
            IntervalText == other.IntervalText &&
            GapText == other.GapText &&
            IsOut == other.IsOut &&
            TyreLetter == other.TyreLetter &&
            IsPenaltyPending == other.IsPenaltyPending &&
            IsFastestLap == other.IsFastestLap &&
            ReferenceEquals(LiveryBrush, other.LiveryBrush) &&
            ReferenceEquals(TyreBrush, other.TyreBrush);
    }
}
