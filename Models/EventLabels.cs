using System.Text.RegularExpressions;
using F1Game.UDP.Enums;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// Friendly text for the enums carried by EventDataPacket sub-events.
    /// </summary>
    public static class EventLabels
    {
        public static string LabelFor(PenaltyType type) => type switch
        {
            PenaltyType.DriveThrough => "Drive-through penalty",
            PenaltyType.StopGo => "Stop-go penalty",
            PenaltyType.GridPenalty => "Grid penalty",
            PenaltyType.PenaltyReminder => "Penalty reminder",
            PenaltyType.TimePenalty => "Time penalty",
            PenaltyType.Warning => "Warning",
            PenaltyType.Disqualified => "Disqualified",
            PenaltyType.RemovedFromFormationLap => "Removed from formation lap",
            PenaltyType.ParkedTooLongTimer => "Parked too long",
            PenaltyType.TyreRegulations => "Tyre regulations",
            PenaltyType.ThisLapInvalidated => "Lap invalidated",
            PenaltyType.ThisAndNextLapInvalidated => "This and next lap invalidated",
            PenaltyType.ThisLapInvalidatedWithoutReason => "Lap invalidated",
            PenaltyType.ThisAndNextLapInvalidatedWithoutReason => "This and next lap invalidated",
            _ => Humanize(type.ToString())
        };

        // Hand-labelled against real FIA terminology rather than the generic PascalCase
        // humanizer, sourced from the FIA's published F1 Penalty Guidelines and Driving
        // Standards Guidelines (e.g. "causing a collision" severity tiers, "leaving the
        // track and gaining a lasting advantage" for corner-cutting, "impeding" under
        // Article 37.5, "unsafe release", "parc fermé" breaches, safety car delta
        // infringements). A handful of these enum values are game-mechanic-only concepts
        // with no real FIA equivalent at all (flashback, reset-to-track, retry penalty,
        // league grid penalty) - those are labelled plainly instead of forcing fake FIA
        // phrasing onto something that was never a real infringement category. Falls back
        // to the generic humanizer only for any future value not yet reviewed here.
        public static string LabelFor(InfringementType type) => type switch
        {
            InfringementType.BlockingBySlowDriving => "Blocking by driving unnecessarily slowly",
            InfringementType.BlockingByWrongWayDriving => "Blocking by driving the wrong way",
            InfringementType.ReversingOffTheStartLine => "Reversing on the grid",
            InfringementType.BigCollision => "Causing a collision",
            InfringementType.SmallCollision => "Causing a minor collision",
            InfringementType.CollisionFailedToHandBackPositionSingle => "Causing a collision after failing to give back a position",
            InfringementType.CollisionFailedToHandBackPositionMultiple => "Causing multiple collisions after failing to give back a position",
            InfringementType.CornerCuttingGainedTime => "Leaving the track and gaining time",
            InfringementType.CornerCuttingOvertakeSingle => "Leaving the track and gaining a position",
            InfringementType.CornerCuttingOvertakeMultiple => "Leaving the track and gaining multiple positions",
            InfringementType.CrossedPitExitLane => "Crossing the pit exit line",
            InfringementType.IgnoringBlueFlags => "Ignoring blue flags",
            InfringementType.IgnoringYellowFlags => "Ignoring yellow flags",
            InfringementType.IgnoringDriveThrough => "Not serving a drive-through penalty",
            InfringementType.TooManyDriveThroughs => "Exceeding the drive-through penalty limit",
            InfringementType.DriveThroughReminderServeWithinNLaps => "Drive-through penalty - must be served within the set number of laps",
            InfringementType.DriveThroughReminderServeThisLap => "Drive-through penalty - must be served this lap",
            InfringementType.PitLaneSpeeding => "Speeding in the pit lane",
            InfringementType.ParkedForTooLong => "Parked for too long",
            InfringementType.IgnoringTyreRegulations => "Breach of tyre regulations",
            InfringementType.TooManyPenalties => "Too many penalties - disqualified",
            InfringementType.MultipleWarnings => "Multiple warnings - time penalty issued",
            InfringementType.ApproachingDisqualification => "Approaching disqualification",
            InfringementType.TyreRegulationsSelectSingle => "Incorrect tyre selection",
            InfringementType.TyreRegulationsSelectMultiple => "Incorrect tyre selection - multiple sets",
            InfringementType.LapInvalidatedCornerCutting => "Lap invalidated - track limits",
            InfringementType.LapInvalidatedRunningWide => "Lap invalidated - running wide",
            InfringementType.CornerCuttingRanWideGainedTimeMinor => "Running wide - minor time gain",
            InfringementType.CornerCuttingRanWideGainedTimeSignificant => "Running wide - significant time gain",
            InfringementType.CornerCuttingRanWideGainedTimeExtreme => "Running wide - extreme time gain",
            InfringementType.LapInvalidatedWallRiding => "Lap invalidated - wall riding",
            // Game-mechanic-only, no real FIA equivalent.
            InfringementType.LapInvalidatedFlashbackUsed => "Lap invalidated - flashback used",
            InfringementType.LapInvalidatedResetToTrack => "Lap invalidated - reset to track",
            InfringementType.BlockingThePitLane => "Blocking the pit lane",
            InfringementType.JumpStart => "Jump start",
            InfringementType.SafetyCarToCarCollision => "Collision with the safety car",
            InfringementType.SafetyCarIllegalOvertake => "Overtaking under safety car",
            InfringementType.SafetyCarExceedingAllowedPace => "Exceeding safety car delta",
            InfringementType.VirtualSafetyCarExceedingAllowedPace => "Exceeding virtual safety car delta",
            InfringementType.FormationLapBelowAllowedSpeed => "Formation lap - below minimum speed",
            InfringementType.FormationLapParking => "Formation lap - stopping on track",
            InfringementType.RetiredMechanicalFailure => "Retired - mechanical failure",
            InfringementType.RetiredTerminallyDamaged => "Retired - terminal damage",
            InfringementType.SafetyCarFallingTooFarBack => "Falling too far behind the safety car",
            InfringementType.BlackFlagTimer => "Black flag timer expired",
            InfringementType.UnservedStopGoPenalty => "Unserved stop-go penalty",
            InfringementType.UnservedDriveThroughPenalty => "Unserved drive-through penalty",
            InfringementType.EngineComponentChange => "Power unit component change",
            InfringementType.GearboxChange => "Gearbox change",
            InfringementType.ParcFerméChange => "Breach of parc fermé rules",
            // Game-mechanic-only (esports/league feature), no real FIA equivalent.
            InfringementType.LeagueGridPenalty => "League grid penalty",
            InfringementType.RetryPenalty => "Retry penalty",
            InfringementType.IllegalTimeGain => "Illegal time gain",
            InfringementType.MandatoryPitStop => "Mandatory pit stop not completed",
            InfringementType.AttributeAssigned => "Penalty attribute assigned",
            _ => Humanize(type.ToString())
        };

        public static string LabelFor(ResultReason reason) => reason switch
        {
            ResultReason.Retired => "Retired",
            ResultReason.TerminalDamage => "Terminal damage",
            ResultReason.Inactive => "Inactive",
            ResultReason.NotEnoughLapsCompleted => "Not enough laps completed",
            ResultReason.BlackFlagged => "Black flagged",
            ResultReason.MechanicalFailure => "Mechanical failure",
            ResultReason.RedFlagged => "Red flagged",
            ResultReason.SessionSkipped => "Session skipped",
            ResultReason.SessionSimulated => "Session simulated",
            _ => Humanize(reason.ToString())
        };

        private static string Humanize(string pascalCase) =>
            Regex.Replace(pascalCase, @"(?<=[\p{Ll}0-9])(?=[A-Z])", " ");
    }
}
