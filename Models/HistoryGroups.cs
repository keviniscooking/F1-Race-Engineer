using System;
using System.Collections.Generic;
using System.Linq;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// One card in the history list = one race weekend. A Sprint weekend collapses its Sprint
    /// and Race (linked by the game's WeekendLinkId) into this single card, with the Race as the
    /// headline and the Sprint reachable via a tab in the detail view. A normal weekend (or any
    /// race saved before link IDs were captured) is just the one session. The card DataTemplate
    /// binds its headline straight through <see cref="Race"/>; only the sprint-aware bits live here.
    /// </summary>
    public class WeekendCardView
    {
        public SavedRaceView Race { get; }        // headline session (the main Grand Prix)
        public SavedRaceView? Sprint { get; }     // optional Sprint from the same weekend
        public bool HasSprint => Sprint != null;

        // Race first, Sprint second - the order the detail view's tabs present them.
        public IReadOnlyList<SavedRaceView> Sessions { get; }

        // Weekend points = Race + Sprint, so a sprint weekend's card reflects the full haul.
        public string WeekendPointsText { get; }

        public WeekendCardView(SavedRaceView race, SavedRaceView? sprint)
        {
            Race = race;
            Sprint = sprint;
            Sessions = sprint != null ? new[] { race, sprint } : new[] { race };

            int pts = race.Source.Points + (sprint?.Source.Points ?? 0);
            WeekendPointsText = race.IsDnf && sprint == null ? "0" : $"+{pts}";
        }
    }

    /// <summary>
    /// One section of the history list: all weekends from a single season/career save (grouped by
    /// the game's SeasonLinkId), with a summary strip aggregated across them. Races saved before
    /// link IDs existed - or one-off/online sessions the game didn't link - fall into a catch-all
    /// "Earlier races" section, which still gets a summary since finishing data is always present.
    /// </summary>
    public class SeasonGroupView
    {
        public string Label { get; }
        public List<WeekendCardView> Weekends { get; }

        // Which career save this section is: the team + driver you ran, livery-coloured. This is
        // what tells two same-year saves apart (a Ferrari season vs a Williams season). Hidden for
        // the "Earlier races" catch-all, which can mix races from several pre-link-id saves.
        public bool HasIdentity { get; }
        public string IdentityText { get; }              // "Haas · Bearman"
        public System.Windows.Media.SolidColorBrush IdentityBrush { get; }

        public string RacesText { get; }
        public string WinsText { get; }
        public string PodiumsText { get; }
        public string PointsText { get; }
        public string BestText { get; }
        public string DnfsText { get; }
        public string AvgText { get; }

        private SeasonGroupView(string label, List<WeekendCardView> weekends, bool isCatchAll)
        {
            Label = label;
            Weekends = weekends;

            var mains = weekends.Select(w => w.Race).ToList();

            // Identity from the most recent race in the section (weekends are newest-first). A
            // real career save has one team/driver per season, so the newest is representative.
            var newest = mains[0];
            IdentityBrush = newest.TeamLiveryBrush;
            string team = newest.PlayerTeam, driver = newest.PlayerName;
            IdentityText = string.Join(" · ", new[] { team, driver }.Where(s => !string.IsNullOrWhiteSpace(s)));
            HasIdentity = !isCatchAll && IdentityText.Length > 0;

            var classified = mains.Where(m => !m.IsDnf).ToList();
            int totalPoints = weekends.Sum(w => w.Race.Source.Points + (w.Sprint?.Source.Points ?? 0));

            RacesText = mains.Count.ToString();
            WinsText = mains.Count(m => !m.IsDnf && m.Source.FinishPosition == 1).ToString();
            PodiumsText = mains.Count(m => !m.IsDnf && m.Source.FinishPosition is >= 1 and <= 3).ToString();
            PointsText = totalPoints.ToString();
            DnfsText = mains.Count(m => m.IsDnf).ToString();
            BestText = classified.Count > 0 ? "P" + classified.Min(m => m.Source.FinishPosition) : "—";
            AvgText = classified.Count > 0 ? classified.Average(m => m.Source.FinishPosition).ToString("0.0") : "—";
        }

        /// <summary>
        /// Groups saved races into weekend cards, then into season sections. Newest first, with the
        /// "Earlier races" catch-all always last.
        /// </summary>
        public static List<SeasonGroupView> Build(IEnumerable<SavedRace> races)
        {
            var views = races.Select(r => new SavedRaceView(r)).ToList();

            // --- weekends: link real weekends by WeekendLinkId; everything else stands alone. ---
            var weekends = new List<WeekendCardView>();
            foreach (var g in views.GroupBy(v => v.Source.WeekendLinkId != 0
                ? ("W", (ulong)v.Source.WeekendLinkId)
                : ("U", v.Source.SessionUid)))
            {
                var sessions = g.ToList();
                var main = sessions.FirstOrDefault(s => s.Source.SessionLabel == "Race")
                           ?? sessions.OrderByDescending(s => s.Source.TotalLaps).First();
                var sprint = sessions.FirstOrDefault(s => s != main && s.Source.SessionLabel == "Sprint");
                weekends.Add(new WeekendCardView(main, sprint));
            }

            // --- seasons: group weekends by the main race's SeasonLinkId (0 = catch-all),
            //     ordered newest-first with the catch-all pinned last. Labels are resolved
            //     (and de-duplicated) before construction since Label is get-only. ---
            var groups = weekends
                .GroupBy(w => w.Race.Source.SeasonLinkId)
                .Select(g => new
                {
                    g.Key,
                    Weekends = g.OrderByDescending(w => w.Race.Source.SavedAtUtc).ToList(),
                    Latest = g.Max(w => w.Race.Source.SavedAtUtc)
                })
                .OrderBy(g => g.Key == 0)          // real seasons first, catch-all (0) last
                .ThenByDescending(g => g.Latest)
                .ToList();

            var usedLabels = new Dictionary<string, int>();
            var result = new List<SeasonGroupView>();
            foreach (var g in groups)
            {
                string label = g.Key == 0 ? "EARLIER RACES" : YearLabel(g.Weekends);
                if (usedLabels.TryGetValue(label, out int n))
                {
                    usedLabels[label] = n + 1;
                    label = $"{label} ({n + 1})"; // two saves in the same year - keep them distinct
                }
                else usedLabels[label] = 1;
                result.Add(new SeasonGroupView(label, g.Weekends, g.Key == 0));
            }
            return result;
        }

        // A season is labelled by the calendar year its races fall in (the game gives no season
        // year, only an opaque id), e.g. "2026 SEASON". Spanning a New Year shows both: "2025-26 SEASON".
        private static string YearLabel(List<WeekendCardView> weekends)
        {
            var years = weekends.Select(w => w.Race.Source.SavedAtUtc.ToLocalTime().Year).Distinct().OrderBy(y => y).ToList();
            string span = years.Count == 1 ? years[0].ToString() : $"{years.First()}-{years.Last() % 100:D2}";
            return $"{span} SEASON";
        }
    }
}
