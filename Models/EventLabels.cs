using System.Text.RegularExpressions;
using F1Game.UDP.Enums;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// Friendly text for the enums carried by EventDataPacket sub-events. PenaltyType and
    /// ResultReason are small, fixed sets worth hand-labelling; InfringementType has 50+
    /// values so it's humanized generically (PascalCase -> spaced words) instead.
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

        public static string LabelFor(InfringementType type) => Humanize(type.ToString());

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
