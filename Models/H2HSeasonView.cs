using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// The season-long head-to-head against the other human in a two-player career: the running
    /// scoreboard for the rivalry, shown as split bars under the season summary.
    ///
    /// Aggregates only what each saved race already captured, so it needs no new telemetry - a
    /// season becomes comparable the moment two of its races were two-player.
    /// </summary>
    public class H2HSeasonView
    {
        public string RivalName { get; }
        public string RivalTeam { get; }
        public bool HasRivalTeam { get; }
        public List<H2HSeasonRow> Rows { get; }

        private H2HSeasonView(string rivalName, string rivalTeam, List<H2HSeasonRow> rows)
        {
            RivalName = rivalName;
            RivalTeam = rivalTeam;
            HasRivalTeam = rivalTeam.Length > 0;
            Rows = rows;
        }

        /// <summary>
        /// Builds the strip, or null when this season has no two-player races to compare.
        ///
        /// The counting rules deliberately MIRROR the summary strip directly above this one
        /// (SeasonGroupView): race-style counts come from MAIN RACES ONLY, while points INCLUDE
        /// sprint points. Inventing a different rule here - counting sprints as races, say - would
        /// put two contradictory numbers on the same screen.
        /// </summary>
        public static H2HSeasonView? Build(List<WeekendCardView> weekends)
        {
            // Main races that captured a head-to-head. Weekends are newest-first, so the first
            // match also names the rival - a career save can't swap opponents mid-season.
            var mainH2H = weekends
                .Select(w => w.Race.Source.HeadToHead)
                .Where(h => h != null)
                .Cast<SavedHeadToHead>()
                .ToList();
            if (mainH2H.Count == 0) return null;

            int youRaces = 0, rivalRaces = 0, youPodiums = 0, rivalPodiums = 0, youFl = 0, rivalFl = 0;
            foreach (var h in mainH2H)
            {
                // Ahead is decided the same way the race page decides it: a retired car always
                // loses to a classified one, whatever the position numbers say.
                bool youOut = h.You.IsOut, rivalOut = h.Rival.IsOut;
                if (youOut != rivalOut)
                {
                    if (rivalOut) youRaces++; else rivalRaces++;
                }
                else if (!youOut && h.You.FinishPosition != h.Rival.FinishPosition)
                {
                    if (h.You.FinishPosition < h.Rival.FinishPosition) youRaces++; else rivalRaces++;
                }

                if (!youOut && h.You.FinishPosition is >= 1 and <= 3) youPodiums++;
                if (!rivalOut && h.Rival.FinishPosition is >= 1 and <= 3) rivalPodiums++;
                if (h.You.HasFastestLap) youFl++;
                if (h.Rival.HasFastestLap) rivalFl++;
            }

            // Points DO include the sprint, matching the summary strip. Taken from each session's
            // own head-to-head so the rival's tally is theirs, not inferred from the player's.
            int youPts = 0, rivalPts = 0;
            foreach (var w in weekends)
                foreach (var s in w.Sessions)
                    if (s.Source.HeadToHead is SavedHeadToHead h)
                    {
                        youPts += h.You.Points;
                        rivalPts += h.Rival.Points;
                    }

            var newest = mainH2H[0];
            var rows = new List<H2HSeasonRow>
            {
                new("RACES WON", youRaces, rivalRaces),
                new("POINTS", youPts, rivalPts),
                new("FASTEST LAPS", youFl, rivalFl),
                new("PODIUMS", youPodiums, rivalPodiums)
            };
            return new H2HSeasonView(newest.Rival.Name, newest.Rival.Team, rows);
        }
    }

    /// <summary>One comparison row: two totals and the bar split between them.</summary>
    public class H2HSeasonRow
    {
        public string Label { get; }
        public string YouText { get; }
        public string RivalText { get; }

        // Star widths for the two bar halves. A GridLength pair is used rather than percentages
        // so the bar scales with the card at any window width without code-behind.
        public System.Windows.GridLength YouWidth { get; }
        public System.Windows.GridLength RivalWidth { get; }

        public SolidColorBrush YouBrush => YouLeads ? You : YouDim;
        public SolidColorBrush RivalBrush => RivalLeads ? Rival : RivalDim;
        public bool YouLeads { get; }
        public bool RivalLeads { get; }

        // Same two accents the position tower uses for these two people - player blue, rival
        // cyan - so the pairing needs no legend anywhere it appears.
        private static readonly SolidColorBrush You = Frozen(0x1F, 0x6F, 0xEB);
        private static readonly SolidColorBrush Rival = Frozen(0x37, 0xBE, 0xDD);
        private static readonly SolidColorBrush YouDim = Frozen(0x1B, 0x3F, 0x77);
        private static readonly SolidColorBrush RivalDim = Frozen(0x23, 0x5E, 0x6D);

        public H2HSeasonRow(string label, int you, int rival)
        {
            Label = label;
            YouText = you.ToString();
            RivalText = rival.ToString();
            YouLeads = you > rival;
            RivalLeads = rival > you;

            // 0-0 would collapse both halves to nothing and render as an empty track, so an even
            // split stands in for "neither has scored yet".
            int total = you + rival;
            if (total == 0) { YouWidth = new(1, System.Windows.GridUnitType.Star); RivalWidth = new(1, System.Windows.GridUnitType.Star); }
            else { YouWidth = new(you, System.Windows.GridUnitType.Star); RivalWidth = new(rival, System.Windows.GridUnitType.Star); }
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var s = new SolidColorBrush(Color.FromRgb(r, g, b));
            s.Freeze();
            return s;
        }
    }
}
