using F1Game.UDP.Enums;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// Maps the game's <see cref="GameMode"/> to the label shown beside a season in the history
    /// ("2026 SEASON · TWO-PLAYER CAREER"). This exists for two reasons, and the second is the
    /// important one:
    ///  1. Two careers played in the same calendar year used to collide on the year-only section
    ///     label, and <c>SeasonGroupView</c> fell back to appending a bare "(2)". The career type
    ///     is the actual reason they differ, so it replaces a meaningless disambiguator.
    ///  2. <see cref="IsTwoPlayer"/> is the GATE for the whole head-to-head feature - it decides
    ///     whether the tower highlights one car or two - so this is foundational, not cosmetic.
    /// Only modes that can produce a saved race get a label; anything else falls through to "".
    /// </summary>
    public static class CareerNames
    {
        /// <summary>
        /// True only for the game's online two-player career. Deliberately NOT true for
        /// <see cref="GameMode.SplitScreen"/>, which also has exactly two humans: split-screen is
        /// one machine with a single PlayerCarIndex, so player 2 would be "some other car that
        /// happens to be human" - a second code path to reason about, for a mode the user has
        /// said they never play. Callers must additionally require exactly two non-AI cars, so a
        /// mode value alone can never switch the feature on (the same belt-and-braces approach as
        /// the sprint/race lap-count check, after F1 25 was caught mislabelling session types).
        /// </summary>
        public static bool IsTwoPlayer(GameMode mode) => mode == GameMode.Career25Online;

        public static string LabelFor(GameMode mode) => mode switch
        {
            GameMode.Career25Online => "Two-Player Career",
            GameMode.DriverCareer25 => "Driver Career",
            GameMode.MyTeamCareer25 => "My Team",
            GameMode.ChallengeCareer25 => "Challenge Career",
            GameMode.SplitScreen => "Split Screen",
            GameMode.OnlineCustom => "Online",
            GameMode.OnlineWeeklyEvent => "Weekly Event",
            GameMode.StoryMode or GameMode.StoryModeApxGp => "Story Mode",
            GameMode.GrandPrix23 => "Grand Prix",
            GameMode.TimeTrial => "Time Trial",
            GameMode.Benchmark => "Benchmark",
            _ => ""
        };
    }
}
