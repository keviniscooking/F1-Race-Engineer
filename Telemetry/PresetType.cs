using F1Game.UDP.Enums;

namespace F1RaceEngineer.Telemetry
{
    public enum PresetType
    {
        Practice,
        Qualifying,
        Race,
        Unsupported // Time Trial and unknown session types - not built yet, per design decision
    }

    public static class PresetMapper
    {
        public static PresetType FromSessionType(SessionType sessionType) => sessionType switch
        {
            SessionType.Practice1 => PresetType.Practice,
            SessionType.Practice2 => PresetType.Practice,
            SessionType.Practice3 => PresetType.Practice,
            SessionType.ShortPractice => PresetType.Practice,

            SessionType.Qualifying1 => PresetType.Qualifying,
            SessionType.Qualifying2 => PresetType.Qualifying,
            SessionType.Qualifying3 => PresetType.Qualifying,
            SessionType.ShortQualifying => PresetType.Qualifying,
            SessionType.OneShotQualifying => PresetType.Qualifying,
            SessionType.SprintShootout1 => PresetType.Qualifying,
            SessionType.SprintShootout2 => PresetType.Qualifying,
            SessionType.SprintShootout3 => PresetType.Qualifying,
            SessionType.ShortSprintShootout => PresetType.Qualifying,
            SessionType.OneShotSprintShootout => PresetType.Qualifying,

            SessionType.Race => PresetType.Race,
            SessionType.Race2 => PresetType.Race,
            SessionType.Race3 => PresetType.Race,

            _ => PresetType.Unsupported // Unknown, TimeTrial
        };
    }
}
