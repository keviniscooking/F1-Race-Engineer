using F1Game.UDP.Enums;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// Maps the game's <see cref="Track"/> enum to the real-world Grand Prix name (used to
    /// title a saved race) and a short circuit/location label (used as the subtitle). The
    /// telemetry carries no "round number" for the calendar, so races are disambiguated by
    /// date, not round. Reverse-layout variants keep the base GP name with a "(Reverse)"
    /// suffix; the 2026 Madrid round is its own GP, distinct from Barcelona's Spanish GP.
    /// </summary>
    public static class TrackNames
    {
        public static string GrandPrixFor(Track track) => track switch
        {
            Track.Melbourne => "Australian Grand Prix",
            Track.Shanghai => "Chinese Grand Prix",
            Track.Bahrain => "Bahrain Grand Prix",
            Track.Catalunya => "Spanish Grand Prix",
            Track.Monaco => "Monaco Grand Prix",
            Track.Montreal => "Canadian Grand Prix",
            Track.Silverstone => "British Grand Prix",
            Track.Hungaroring => "Hungarian Grand Prix",
            Track.Spa => "Belgian Grand Prix",
            Track.Monza => "Italian Grand Prix",
            Track.Singapore => "Singapore Grand Prix",
            Track.Suzuka => "Japanese Grand Prix",
            Track.AbuDhabi => "Abu Dhabi Grand Prix",
            Track.Texas => "United States Grand Prix",
            Track.Brazil => "São Paulo Grand Prix",
            Track.Austria => "Austrian Grand Prix",
            Track.Mexico => "Mexico City Grand Prix",
            Track.Azerbaijan => "Azerbaijan Grand Prix",
            Track.Zandvoort => "Dutch Grand Prix",
            Track.Imola => "Emilia Romagna Grand Prix",
            Track.Jeddah => "Saudi Arabian Grand Prix",
            Track.Miami => "Miami Grand Prix",
            Track.LasVegas => "Las Vegas Grand Prix",
            Track.Qatar => "Qatar Grand Prix",
            Track.Madrid => "Madrid Grand Prix",
            Track.SilverstoneReverse => "British Grand Prix (Reverse)",
            Track.AustriaReverse => "Austrian Grand Prix (Reverse)",
            Track.ZandvoortReverse => "Dutch Grand Prix (Reverse)",
            _ => "Grand Prix"
        };

        // FIA/IOC 3-letter country code, shown as a small badge before the GP name (F1's own
        // timing-screen convention). A code, not a flag graphic, so nothing needs bundling.
        public static string CountryCodeFor(Track track) => track switch
        {
            Track.Melbourne => "AUS",
            Track.Shanghai => "CHN",
            Track.Bahrain => "BHR",
            Track.Catalunya => "ESP",
            Track.Monaco => "MON",
            Track.Montreal => "CAN",
            Track.Silverstone or Track.SilverstoneReverse => "GBR",
            Track.Hungaroring => "HUN",
            Track.Spa => "BEL",
            Track.Monza => "ITA",
            Track.Singapore => "SGP",
            Track.Suzuka => "JPN",
            Track.AbuDhabi => "UAE",
            Track.Texas or Track.Miami or Track.LasVegas => "USA",
            Track.Brazil => "BRA",
            Track.Austria or Track.AustriaReverse => "AUT",
            Track.Mexico => "MEX",
            Track.Azerbaijan => "AZE",
            Track.Zandvoort or Track.ZandvoortReverse => "NED",
            Track.Imola => "ITA",
            Track.Jeddah => "KSA",
            Track.Qatar => "QAT",
            Track.Madrid => "ESP",
            _ => ""
        };

        public static string CircuitFor(Track track) => track switch
        {
            Track.Melbourne => "Melbourne",
            Track.Shanghai => "Shanghai",
            Track.Bahrain => "Sakhir",
            Track.Catalunya => "Barcelona",
            Track.Monaco => "Monte Carlo",
            Track.Montreal => "Montreal",
            Track.Silverstone or Track.SilverstoneReverse => "Silverstone",
            Track.Hungaroring => "Budapest",
            Track.Spa => "Spa-Francorchamps",
            Track.Monza => "Monza",
            Track.Singapore => "Singapore",
            Track.Suzuka => "Suzuka",
            Track.AbuDhabi => "Yas Marina",
            Track.Texas => "Austin",
            Track.Brazil => "Interlagos",
            Track.Austria or Track.AustriaReverse => "Red Bull Ring",
            Track.Mexico => "Mexico City",
            Track.Azerbaijan => "Baku",
            Track.Zandvoort or Track.ZandvoortReverse => "Zandvoort",
            Track.Imola => "Imola",
            Track.Jeddah => "Jeddah",
            Track.Miami => "Miami",
            Track.LasVegas => "Las Vegas",
            Track.Qatar => "Lusail",
            Track.Madrid => "Madrid",
            _ => ""
        };
    }
}
