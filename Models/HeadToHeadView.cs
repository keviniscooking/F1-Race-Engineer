using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// Display-ready head-to-head for one saved two-player race: the verdict, the "tale of the
    /// tape" rows, the lap-by-lap gap series, and both strategy bars. Built from
    /// <see cref="SavedHeadToHead"/>'s raw millisecond series, so all the arithmetic lives here
    /// rather than in the XAML or the panel code-behind - the same split
    /// <see cref="SavedRaceView"/> already uses.
    /// </summary>
    public class HeadToHeadView
    {
        public string SessionLabel { get; }          // "Chinese Grand Prix · Sprint" - names the session, so the page is never ambiguous about which one it describes
        public H2HSide You { get; }
        public H2HSide Rival { get; }

        public string VerdictText { get; }           // "AHEAD" / "BEHIND" / "LEVEL"
        public SolidColorBrush VerdictBrush { get; }
        public string MarginText { get; }            // "finished 12.481s ahead" / "finished 1 lap behind"

        public List<H2HRow> Rows { get; }            // the tale of the tape
        public List<H2HGapPoint> GapSeries { get; }  // lap-by-lap gap, + = you ahead
        public bool HasGapSeries { get; }
        public double GapMaxAbsSeconds { get; }      // symmetric axis bound, so the zero line sits centred

        private static readonly SolidColorBrush Ink = Frozen(0xE6, 0xED, 0xF3);
        private static readonly SolidColorBrush Green = Frozen(0x97, 0xC4, 0x59);
        private static readonly SolidColorBrush Red = Frozen(0xE1, 0x2E, 0x2E);
        private static readonly SolidColorBrush Muted = Frozen(0x6B, 0x76, 0x84);

        public HeadToHeadView(SavedHeadToHead h2h, string sessionLabel)
        {
            SessionLabel = sessionLabel;
            You = new H2HSide(h2h.You);
            Rival = new H2HSide(h2h.Rival);

            // The verdict is finishing position, not lap time - a race is won on the road. A
            // retired car always loses to a classified one regardless of position number.
            bool youOut = h2h.You.IsOut, rivalOut = h2h.Rival.IsOut;
            int cmp = youOut != rivalOut
                ? (youOut ? 1 : -1)
                : h2h.You.FinishPosition.CompareTo(h2h.Rival.FinishPosition);

            VerdictText = cmp < 0 ? "AHEAD" : cmp > 0 ? "BEHIND" : "LEVEL";
            VerdictBrush = cmp < 0 ? Green : cmp > 0 ? Red : Muted;
            MarginText = BuildMargin(h2h, cmp);

            Rows = BuildRows(h2h);
            GapSeries = BuildGapSeries(h2h);
            HasGapSeries = GapSeries.Count > 0;
            GapMaxAbsSeconds = HasGapSeries ? Math.Max(1.0, GapSeries.Max(p => Math.Abs(p.GapSeconds))) : 1.0;
        }

        private static string BuildMargin(SavedHeadToHead h, int cmp)
        {
            if (cmp == 0) return "same position";
            if (h.You.IsOut && h.Rival.IsOut) return "both retired";
            if (h.You.IsOut) return "you retired";
            if (h.Rival.IsOut) return $"{h.Rival.Name} retired";

            // A lap down makes a seconds gap meaningless - the game's TotalRaceTime doesn't
            // account for the extra distance, so report laps instead.
            int lapDiff = Math.Abs(h.You.NumLaps - h.Rival.NumLaps);
            if (lapDiff > 0)
                return $"finished {lapDiff} lap{(lapDiff == 1 ? "" : "s")} {(cmp < 0 ? "ahead" : "behind")}";

            double secs = Math.Abs(h.You.TotalRaceTimeSeconds - h.Rival.TotalRaceTimeSeconds);
            if (secs <= 0) return cmp < 0 ? "finished ahead" : "finished behind";
            return $"finished {secs:0.000}s {(cmp < 0 ? "ahead" : "behind")}";
        }

        private static List<H2HRow> BuildRows(SavedHeadToHead h)
        {
            var y = h.You;
            var r = h.Rival;
            var rows = new List<H2HRow>
            {
                // Grid comes free from Final Classification, so "who out-qualified whom" is
                // answerable without capturing qualifying sessions at all.
                H2HRow.Positions("GRID", y.GridPosition, r.GridPosition),
                H2HRow.Positions("FINISH", y.FinishPosition, r.FinishPosition, y.IsOut, r.IsOut),
                H2HRow.Gained("POSITIONS", y.GridPosition, y.FinishPosition, r.GridPosition, r.FinishPosition, y.IsOut, r.IsOut),
                H2HRow.Times("BEST LAP", BestLapMs(y), BestLapMs(r)),
                H2HRow.Times("IDEAL LAP", IdealLapMs(y), IdealLapMs(r)),
                H2HRow.Sector("S1", BestSectorMs(y, 1), BestSectorMs(r, 1)),
                H2HRow.Sector("S2", BestSectorMs(y, 2), BestSectorMs(r, 2)),
                H2HRow.Sector("S3", BestSectorMs(y, 3), BestSectorMs(r, 3)),
                H2HRow.Times("RACE PACE", MedianPaceMs(y), MedianPaceMs(r)),
                H2HRow.Spread("CONSISTENCY", PaceSpreadMs(y), PaceSpreadMs(r)),
                // Counted from the captured stops, not the classification's NumPitStops, so this
                // row, PIT TIME and the pit-stops card can never contradict each other on screen.
                // Falls back to the game's count only when nothing was captured (e.g. the app
                // joined mid-race), where showing the game's number beats showing zero.
                H2HRow.Counts("STOPS", StopCount(y), StopCount(r)),
                // Total stationary time across all stops - the crew's contribution, separated from
                // pit-lane traversal which is mostly a property of the circuit. This is what shows
                // a position was lost on the jacks rather than on track.
                H2HRow.Times("PIT TIME", TotalStationaryMs(y), TotalStationaryMs(r)),
                H2HRow.Penalties("PENALTIES", y.PenaltiesTimeSeconds, r.PenaltiesTimeSeconds),
            };

            var (youAhead, rivalAhead) = LapsAhead(h);
            rows.Add(H2HRow.Counts("LAPS AHEAD", youAhead, rivalAhead, higherIsBetter: true));
            return rows;
        }

        // ---- per-driver derived values (all exclude invalidated laps, matching how the live
        //      timing board sources best laps from the game's own session history) ----

        private static int StopCount(SavedH2HDriver d) => d.Stops.Count > 0 ? d.Stops.Count : d.PitStops;

        private static uint TotalStationaryMs(SavedH2HDriver d)
        {
            uint total = 0;
            foreach (var s in d.Stops) total += s.StationaryMs;
            return total;
        }

        private static uint BestLapMs(SavedH2HDriver d)
        {
            uint best = 0;
            foreach (var l in d.Laps)
                if (l.IsValid && l.LapTimeMs > 0 && (best == 0 || l.LapTimeMs < best)) best = l.LapTimeMs;
            return best;
        }

        private static uint BestSectorMs(SavedH2HDriver d, int sector)
        {
            uint best = 0;
            foreach (var l in d.Laps)
            {
                if (!l.IsValid) continue;
                uint v = sector switch { 1 => l.S1Ms, 2 => l.S2Ms, _ => l.S3Ms };
                if (v > 0 && (best == 0 || v < best)) best = v;
            }
            return best;
        }

        /// <summary>
        /// Best S1 + best S2 + best S3, which may come from three different laps - the lap that
        /// was theoretically available. This is the row that makes the sector data worth storing:
        /// it shows *where* the time is, not merely who was quicker overall.
        /// </summary>
        private static uint IdealLapMs(SavedH2HDriver d)
        {
            uint s1 = BestSectorMs(d, 1), s2 = BestSectorMs(d, 2), s3 = BestSectorMs(d, 3);
            return s1 > 0 && s2 > 0 && s3 > 0 ? s1 + s2 + s3 : 0;
        }

        /// <summary>
        /// Race pace as the MEDIAN valid lap, not the mean - one pit stop or safety-car lap drags
        /// an average far enough to invert the comparison between two evenly matched drivers.
        /// No "is this a green lap" cutoff is applied on purpose: a median already ignores the
        /// tails by construction, and a threshold would be both arbitrary and wrong. (A trim at
        /// 1.3x the driver's best doesn't even catch a pit lap - a ~20s pit loss on a 90s lap is
        /// only 1.22x - so it would have filtered nothing while looking rigorous.)
        /// </summary>
        private static uint MedianPaceMs(SavedH2HDriver d)
        {
            var laps = ValidLaps(d);
            if (laps.Count == 0) return 0;
            laps.Sort();
            return Median(laps);
        }

        /// <summary>
        /// Consistency as the median absolute deviation about the median lap: the typical amount a
        /// lap differed from this driver's normal pace. Deliberately a robust statistic rather than
        /// a standard deviation or mean deviation, both of which are dominated by the two or three
        /// pit/SC laps in any race and would rank a metronomic driver as erratic.
        /// </summary>
        private static uint PaceSpreadMs(SavedH2HDriver d)
        {
            var laps = ValidLaps(d);
            if (laps.Count < 2) return 0;
            laps.Sort();
            uint med = Median(laps);
            var deviations = laps.Select(v => (uint)Math.Abs((long)v - med)).ToList();
            deviations.Sort();
            return Median(deviations);
        }

        /// <summary>
        /// Laps that count as race pace: valid, timed, and not compromised by a pit stop.
        /// Pit laps are excluded BY IDENTITY rather than by any statistical threshold - the game
        /// tells us exactly which laps they were, so guessing would be inexcusable. A trim on
        /// "anything slower than Nx the best" cannot work anyway: a ~20s pit loss on a 90s lap is
        /// only 1.22x, well inside any threshold loose enough not to also discard genuinely slow
        /// racing laps. The median still does the rest of the work for safety-car and traffic laps,
        /// which carry no equivalent per-lap marker.
        /// </summary>
        private static List<uint> ValidLaps(SavedH2HDriver d)
        {
            var pitLaps = PitAffectedLaps(d);
            return d.Laps.Where(l => l.IsValid && l.LapTimeMs > 0 && !pitLaps.Contains(l.LapNumber))
                         .Select(l => l.LapTimeMs).ToList();
        }

        /// <summary>
        /// The laps a pit stop touched, derived from the stint boundaries the game reports.
        /// Each stint's end lap IS the lap the car pitted on (the in-lap keeps the pit lap, the
        /// convention the tyre-strategy bar and the lap-by-lap IN tag already follow), and the
        /// lap after it is the out-lap on cold tyres. Both are excluded. The FINAL stint is
        /// skipped deliberately - its end lap is the chequered flag, not a stop.
        /// </summary>
        private static HashSet<int> PitAffectedLaps(SavedH2HDriver d)
        {
            var laps = new HashSet<int>();
            for (int i = 0; i < d.Stints.Count - 1; i++)
            {
                int end = d.Stints[i].EndLap;
                if (end <= 0) continue;
                laps.Add(end);      // in-lap: carries the pit-lane entry
                laps.Add(end + 1);  // out-lap: cold tyres, not representative
            }
            return laps;
        }

        /// <summary>Median of an already-sorted list; averages the middle pair on an even count.</summary>
        private static uint Median(List<uint> sorted)
        {
            int n = sorted.Count;
            if (n == 0) return 0;
            return n % 2 == 1 ? sorted[n / 2] : (uint)(((long)sorted[n / 2 - 1] + sorted[n / 2]) / 2);
        }

        /// <summary>
        /// How many laps each driver spent ahead on cumulative race time. Uses elapsed time rather
        /// than the overtake events the app doesn't handle, so it's exact and needs no extra data.
        /// </summary>
        private static (int You, int Rival) LapsAhead(SavedHeadToHead h)
        {
            var a = Cumulative(h.You.Laps);
            var b = Cumulative(h.Rival.Laps);
            int n = Math.Min(a.Count, b.Count), you = 0, rival = 0;
            for (int i = 0; i < n; i++)
            {
                if (a[i] < b[i]) you++;
                else if (b[i] < a[i]) rival++;
            }
            return (you, rival);
        }

        private static List<double> Cumulative(List<SavedH2HLap> laps)
        {
            var running = new List<double>(laps.Count);
            double total = 0;
            foreach (var l in laps)
            {
                total += l.LapTimeMs / 1000.0;
                running.Add(total);
            }
            return running;
        }

        /// <summary>
        /// The gap between the two cars at the end of each lap, positive when you're ahead.
        /// Derived purely from cumulative lap times, which is why this survived the decision to
        /// drop overtake/collision handling - it needs no events at all.
        /// </summary>
        private static List<H2HGapPoint> BuildGapSeries(SavedHeadToHead h)
        {
            var you = Cumulative(h.You.Laps);
            var rival = Cumulative(h.Rival.Laps);
            int n = Math.Min(you.Count, rival.Count);
            var series = new List<H2HGapPoint>(n);
            for (int i = 0; i < n; i++)
                series.Add(new H2HGapPoint { Lap = i + 1, GapSeconds = rival[i] - you[i] });
            return series;
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var s = new SolidColorBrush(Color.FromRgb(r, g, b));
            s.Freeze();
            return s;
        }
    }

    /// <summary>One driver's identity for the H2H header.</summary>
    public class H2HSide
    {
        public string Name { get; }
        public string Team { get; }
        public SolidColorBrush LiveryBrush { get; }
        public string PositionText { get; }
        public string PointsText { get; }
        public List<TyreStintSegment> StintSegments { get; }

        // Per-stop detail: which lap, how long stationary, and the full pit-lane time. Both
        // numbers are shown because they attribute the loss differently - a long box time is the
        // crew, a long lane time is the circuit (or a pit-lane penalty).
        //
        // A LIST rather than one formatted line: a single line has to be trimmed to fit, so a
        // three-stop race would silently lose its last stop. As rows in a height-bounded card
        // they can't overflow the page no matter how many stops a wet race produces.
        public List<H2HStopRow> Stops { get; }
        public bool HasStops { get; }
        public string StopTotalText { get; }
        // Stop laps as plain numbers, for marking the gap chart - the big swings in that trace
        // ARE the pit stops, and without markers the reader has to cross-reference the list.
        public List<int> PitLaps { get; }

        public H2HSide(SavedH2HDriver d)
        {
            Name = d.Name;
            Team = d.Team;
            LiveryBrush = SavedRaceView.BrushFromHex(d.LiveryHex);
            PositionText = d.IsOut ? "DNF" : "P" + d.FinishPosition;
            PointsText = d.Points > 0 ? $"{d.Points} pts" : "-";
            // Reuses the same segment builder the result view's strategy bar uses, so both bars
            // are drawn from identical logic and can't drift apart. No AlignStintsToInLaps pass
            // here: that corrects stint boundaries against the player's IN-tagged laps, which
            // only exist for the player - the raw game boundaries are what both drivers share.
            StintSegments = SavedRaceView.BuildSegments(d.Stints, d.NumLaps);

            HasStops = d.Stops.Count > 0;
            Stops = d.Stops.Select(s => new H2HStopRow
            {
                LapText = "L" + s.Lap,
                BoxText = $"{s.StationaryMs / 1000.0:0.00}s",
                LaneText = $"{s.LaneMs / 1000.0:0.0}s"
            }).ToList();

            uint totalBox = 0;
            foreach (var s in d.Stops) totalBox += s.StationaryMs;
            StopTotalText = HasStops ? $"{totalBox / 1000.0:0.00}s" : "—";
            PitLaps = d.Stops.Select(s => s.Lap).ToList();
        }
    }

    /// <summary>One pit stop as a display row: lap, stationary (box) time, pit-lane time.</summary>
    public class H2HStopRow
    {
        public string LapText { get; set; } = "";
        public string BoxText { get; set; } = "";
        public string LaneText { get; set; } = "";
    }

    public class H2HGapPoint
    {
        public int Lap { get; set; }
        public double GapSeconds { get; set; }   // + = you ahead
    }

    /// <summary>
    /// One row of the tale of the tape. Each factory decides both the formatting and which side
    /// "wins", since better means smaller for a lap time and larger for laps ahead - keeping that
    /// judgement here stops every consumer re-deriving it.
    /// </summary>
    public class H2HRow
    {
        public string Label { get; set; } = "";
        public string YouText { get; set; } = "-";
        public string RivalText { get; set; } = "-";
        public string DeltaText { get; set; } = "";
        public bool YouBetter { get; set; }
        public bool RivalBetter { get; set; }

        // Sector rows are a breakdown of the IDEAL LAP row above them, so they're indented to
        // read as subordinate rather than as three more top-level comparisons.
        public System.Windows.Thickness LabelIndent { get; set; }

        private static readonly SolidColorBrush Ink = Freeze(0xE6, 0xED, 0xF3);
        private static readonly SolidColorBrush Dim = Freeze(0x8B, 0x97, 0xA4);
        private static readonly SolidColorBrush Good = Freeze(0x97, 0xC4, 0x59);

        public SolidColorBrush YouBrush => YouBetter ? Good : Ink;
        public SolidColorBrush RivalBrush => RivalBetter ? Good : Ink;
        public SolidColorBrush DeltaBrush => Dim;

        public static H2HRow Positions(string label, int you, int rival, bool youOut = false, bool rivalOut = false)
        {
            bool yb = !youOut && (rivalOut || (you > 0 && rival > 0 && you < rival));
            bool rb = !rivalOut && (youOut || (you > 0 && rival > 0 && rival < you));
            return new H2HRow
            {
                Label = label,
                YouText = youOut ? "DNF" : you > 0 ? "P" + you : "-",
                RivalText = rivalOut ? "DNF" : rival > 0 ? "P" + rival : "-",
                YouBetter = yb,
                RivalBetter = rb
            };
        }

        public static H2HRow Gained(string label, int youGrid, int youFin, int rivalGrid, int rivalFin, bool youOut, bool rivalOut)
        {
            int y = youOut || youGrid <= 0 || youFin <= 0 ? int.MinValue : youGrid - youFin;
            int r = rivalOut || rivalGrid <= 0 || rivalFin <= 0 ? int.MinValue : rivalGrid - rivalFin;
            return new H2HRow
            {
                Label = label,
                YouText = y == int.MinValue ? "-" : Signed(y),
                RivalText = r == int.MinValue ? "-" : Signed(r),
                YouBetter = y != int.MinValue && r != int.MinValue && y > r,
                RivalBetter = y != int.MinValue && r != int.MinValue && r > y
            };
        }

        public static H2HRow Times(string label, uint you, uint rival) => FromMs(label, you, rival, SavedRaceView.FormatTime);

        // Sectors read as plain seconds ("26.304"), not m:ss.mmm - the same convention the
        // lap-by-lap sector columns already use.
        public static H2HRow Sector(string label, uint you, uint rival)
        {
            var row = FromMs(label, you, rival, ms => $"{ms / 1000.0:0.000}");
            row.LabelIndent = new System.Windows.Thickness(14, 0, 0, 0);
            return row;
        }

        private static H2HRow FromMs(string label, uint you, uint rival, Func<uint, string> fmt)
        {
            var row = new H2HRow
            {
                Label = label,
                YouText = you > 0 ? fmt(you) : "-",
                RivalText = rival > 0 ? fmt(rival) : "-",
                YouBetter = you > 0 && rival > 0 && you < rival,
                RivalBetter = you > 0 && rival > 0 && rival < you
            };
            if (you > 0 && rival > 0 && you != rival)
                row.DeltaText = $"{Math.Abs((long)you - rival) / 1000.0:0.000}";
            return row;
        }

        public static H2HRow Spread(string label, uint you, uint rival) => new()
        {
            Label = label,
            YouText = you > 0 ? $"±{you / 1000.0:0.00}" : "-",
            RivalText = rival > 0 ? $"±{rival / 1000.0:0.00}" : "-",
            YouBetter = you > 0 && rival > 0 && you < rival,
            RivalBetter = you > 0 && rival > 0 && rival < you
        };

        public static H2HRow Counts(string label, int you, int rival, bool higherIsBetter = false) => new()
        {
            Label = label,
            YouText = you.ToString(),
            RivalText = rival.ToString(),
            YouBetter = higherIsBetter ? you > rival : false,
            RivalBetter = higherIsBetter ? rival > you : false
        };

        public static H2HRow Penalties(string label, int you, int rival) => new()
        {
            Label = label,
            YouText = you > 0 ? $"+{you}s" : "none",
            RivalText = rival > 0 ? $"+{rival}s" : "none",
            YouBetter = you < rival,
            RivalBetter = rival < you
        };

        private static string Signed(int v) => v > 0 ? $"▲ {v}" : v < 0 ? $"▼ {-v}" : "—";

        private static SolidColorBrush Freeze(byte r, byte g, byte b)
        {
            var s = new SolidColorBrush(Color.FromRgb(r, g, b));
            s.Freeze();
            return s;
        }
    }
}
