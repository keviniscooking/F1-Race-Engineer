using F1Game.UDP.Enums;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// The Team enum carries separate values per season variant (e.g. Mercedes vs
    /// Mercedes24) for liveries used in MyTeam/historic modes - collapse those down to
    /// one display label per real-world team. Anything outside the current F1 grid
    /// (junior categories, custom teams) falls back to the raw enum name rather than
    /// guessing a formatted label for values that don't need one in this app.
    /// </summary>
    public static class TeamNames
    {
        public static string LabelFor(Team team) => team switch
        {
            Team.Mercedes or Team.Mercedes24 or Team.Mercedes26 => "Mercedes",
            Team.Ferrari or Team.Ferrari24 or Team.Ferrari26 => "Ferrari",
            Team.RedBullRacing or Team.RedBullRacing24 or Team.RedBullRacing26 => "Red Bull Racing",
            Team.Williams or Team.Williams24 or Team.Williams26 => "Williams",
            Team.AstonMartin or Team.AstonMartin24 or Team.AstonMartin26 => "Aston Martin",
            Team.Alpine or Team.Alpine24 or Team.Alpine26 => "Alpine",
            Team.RacingBulls or Team.RacingBulls24 or Team.RacingBulls26 => "Racing Bulls",
            Team.Haas or Team.Haas24 or Team.Haas26 => "Haas",
            Team.McLaren or Team.McLaren24 or Team.McLaren26 => "McLaren",
            // Sauber rebrands to Audi for the 2026 season - no Sauber26 value exists at all,
            // just a new Audi26 field, so this isn't a same-team season-variant like the rest.
            Team.Sauber or Team.Sauber24 => "Sauber",
            Team.Audi26 => "Audi",
            // New for the 2026 season pack - no prior-season equivalent value.
            Team.Cadillac26 => "Cadillac",
            Team.F1CustomTeam => "My Team",
            _ => team.ToString()
        };
    }
}
