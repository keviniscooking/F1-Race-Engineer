using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// Display-ready wrapper over a <see cref="SavedRace"/> for the history panel: brushes
    /// instead of hex strings, formatted times, and computed positions-gained. Keeps the
    /// history XAML free of value converters - it binds straight to these properties. The
    /// exported HTML reads the same underlying SavedRace, so the two views stay consistent.
    /// </summary>
    public class SavedRaceView
    {
        public SavedRace Source { get; }

        public string GrandPrix { get; }
        public string Country { get; }
        public bool HasCountry { get; }
        public string CardSubtitle { get; }     // "Barcelona · 13 Jul 2026"
        public string DetailSubtitle { get; }   // "Barcelona · Race · 66 laps"
        public bool IsDnf { get; }

        public string FinishText { get; }        // "P4" / "DNF"
        public SolidColorBrush FinishBrush { get; }
        public bool HasDelta { get; }
        public string DeltaText { get; }         // "▲ 2 places" / "▼ 1 place" / "— held P7"
        public SolidColorBrush DeltaBrush { get; }
        public string DeltaShort { get; }        // compact for the detail header: "▲ 8" / "▼ 1" / "—"
        public SolidColorBrush DeltaShortBrush { get; }

        public string BestLapText { get; }
        public SolidColorBrush BestLapBrush { get; }
        public string StopsText { get; }
        public string PointsText { get; }
        public bool HasFastestLap { get; }

        public string GridText { get; }          // "P7"
        public bool HasDnfDetail { get; }
        public string DnfDetail { get; }          // "Retired on lap 61 — Terminal damage"

        public int TotalLaps { get; }
        public List<string> Penalties { get; }   // mirrors the live Penalties & Flags list
        public bool HasPenalties { get; }
        public bool NoPenalties { get; }          // drives the quiet "No penalties" state without an inverse converter
        public List<TyreStintSegment> StintSegments { get; }
        public List<ClassRowView> Classification { get; }
        public List<LapRowView> Laps { get; }

        private static readonly SolidColorBrush Ink = Frozen(0xE6, 0xED, 0xF3);
        private static readonly SolidColorBrush Muted = Frozen(0x6B, 0x76, 0x84);
        private static readonly SolidColorBrush Green = Frozen(0x97, 0xC4, 0x59);
        private static readonly SolidColorBrush Red = Frozen(0xE1, 0x2E, 0x2E);
        private static readonly SolidColorBrush Purple = Frozen(0xAF, 0xA9, 0xEC);

        public SavedRaceView(SavedRace r)
        {
            Source = r;
            GrandPrix = r.GrandPrix;
            Country = r.Country;
            HasCountry = !string.IsNullOrEmpty(r.Country);
            string date = r.SavedAtUtc.ToLocalTime().ToString("d MMM yyyy");
            CardSubtitle = string.IsNullOrEmpty(r.Circuit) ? date : $"{r.Circuit}  ·  {date}";
            DetailSubtitle = string.IsNullOrEmpty(r.Circuit)
                ? $"{r.SessionLabel} · {r.TotalLaps} laps"
                : $"{r.Circuit}  ·  {r.SessionLabel} · {r.TotalLaps} laps";
            TotalLaps = r.TotalLaps;

            IsDnf = r.ResultStatus != "Finished";
            FinishText = IsDnf ? r.ResultStatus : $"P{r.FinishPosition}";
            FinishBrush = IsDnf ? Red : Ink;

            int delta = r.FinishPosition - r.GridPosition; // negative = gained
            HasDelta = !IsDnf;
            if (delta < 0) { DeltaText = $"▲ {-delta} place{(delta == -1 ? "" : "s")}"; DeltaBrush = Green; DeltaShort = $"▲ {-delta}"; }
            else if (delta > 0) { DeltaText = $"▼ {delta} place{(delta == 1 ? "" : "s")}"; DeltaBrush = Red; DeltaShort = $"▼ {delta}"; }
            else { DeltaText = $"— held P{r.GridPosition}"; DeltaBrush = Muted; DeltaShort = "—"; }
            // The header stat is blank/neutral for a DNF (grid-to-finish delta is meaningless).
            if (IsDnf) DeltaShort = "—";
            DeltaShortBrush = HasDelta ? DeltaBrush : Muted;

            HasFastestLap = r.PlayerHasFastestLap;
            BestLapText = FormatTime(r.BestLapMs);
            BestLapBrush = r.PlayerHasFastestLap ? Purple : Ink;
            StopsText = r.PitStops.ToString();
            PointsText = IsDnf ? "0" : $"+{r.Points}";

            GridText = $"P{r.GridPosition}";
            HasDnfDetail = IsDnf && r.RetiredOnLap.HasValue;
            DnfDetail = HasDnfDetail
                ? $"Retired on lap {r.RetiredOnLap} of {r.TotalLaps}" + (string.IsNullOrEmpty(r.ResultReason) ? "" : $" — {r.ResultReason}")
                : "";

            StintSegments = BuildSegments(r.PlayerStints, r.TotalLaps);

            // Penalties card: the snapshot the live widget showed. For older saves with no
            // snapshot, fall back to the stored total penalty time so the card still says
            // something rather than wrongly reading "No penalties".
            Penalties = new List<string>(r.Penalties);
            if (Penalties.Count == 0 && r.PenaltiesTimeSeconds > 0)
                Penalties.Add($"Time penalties: +{r.PenaltiesTimeSeconds}s");
            HasPenalties = Penalties.Count > 0;
            NoPenalties = !HasPenalties;

            // Winner = the classified P1 (rows are position-sorted). Its total race time and lap
            // count are the reference every other row's gap is measured against.
            SavedClassificationRow? winner = null;
            foreach (var row in r.Classification)
                if (!row.IsOut && row.Position >= 1) { winner = row; break; }
            double winnerTime = winner?.TotalRaceTimeSeconds ?? 0;
            int winnerLaps = winner?.NumLaps ?? 0;

            Classification = new List<ClassRowView>();
            foreach (var row in r.Classification) Classification.Add(new ClassRowView(row, winnerTime, winnerLaps));
            Laps = new List<LapRowView>();
            foreach (var lap in r.PlayerLaps) Laps.Add(new LapRowView(lap));
        }

        // Stint end-laps -> proportional segments (laps = gap from the previous end-lap). When
        // totalLaps is known, end-laps are clamped to it: the game reports the final stint's end
        // as 255 (a "still running / no end" sentinel), which would otherwise render as a giant
        // phantom stint. Clamping pins the last stint to the finish so the bar fills correctly.
        internal static List<TyreStintSegment> BuildSegments(List<SavedStint> stints, int totalLaps = 0)
        {
            var segs = new List<TyreStintSegment>();
            int prevEnd = 0;
            foreach (var s in stints)
            {
                int end = totalLaps > 0 ? Math.Min(s.EndLap, totalLaps) : s.EndLap;
                segs.Add(new TyreStintSegment
                {
                    LapCount = Math.Max(1, end - prevEnd),
                    Letter = s.Compound,
                    Brush = CompoundPalette.BrushForLetter(s.Compound),
                    TextBrush = CompoundPalette.ForegroundForLetter(s.Compound)
                });
                prevEnd = end;
            }
            return segs;
        }

        internal static string FormatTime(uint ms)
        {
            if (ms == 0) return "—";
            var span = TimeSpan.FromMilliseconds(ms);
            return $"{(int)span.TotalMinutes}:{span.Seconds:D2}.{span.Milliseconds:D3}";
        }

        internal static SolidColorBrush BrushFromHex(string hex)
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
            catch { return Muted; }
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    public class ClassRowView
    {
        public int Position { get; }
        public string PositionText { get; }
        public string DriverName { get; }
        public string TeamName { get; }
        public bool HasTeam { get; }
        public SolidColorBrush LiveryBrush { get; }
        public string BestLapText { get; }
        public SolidColorBrush BestLapBrush { get; }
        public string PitsText { get; }
        public string GapText { get; }          // winner's total time / "+12.345" / "+1 LAP" / "DNF"
        public SolidColorBrush GapBrush { get; }
        public bool IsOut { get; }
        public bool IsPlayer { get; }
        public bool HasFastestLap { get; }
        public List<TyreStintSegment> StintChips { get; }

        // Retired cars are dimmed across the row and flagged "DNF" in the gap column, mirroring
        // how the live race tower greys out and marks a driver who's out.
        public SolidColorBrush PositionBrush { get; }
        public SolidColorBrush NameBrush { get; }
        public SolidColorBrush TeamBrush { get; }

        private static readonly SolidColorBrush Purple = SavedRaceView.BrushFromHex("#AFA9EC");
        private static readonly SolidColorBrush Ink = SavedRaceView.BrushFromHex("#E6EDF3");
        private static readonly SolidColorBrush Muted = SavedRaceView.BrushFromHex("#6B7684");
        private static readonly SolidColorBrush Dim = SavedRaceView.BrushFromHex("#4A5460");     // out: position/name (matches the tower)
        private static readonly SolidColorBrush DimTeam = SavedRaceView.BrushFromHex("#3A424C"); // out: team
        private static readonly SolidColorBrush DnfRed = SavedRaceView.BrushFromHex("#E06C6C");

        public ClassRowView(SavedClassificationRow r, double winnerTime, int winnerLaps)
        {
            Position = r.Position;
            PositionText = r.IsOut ? "—" : r.Position.ToString();
            DriverName = r.DriverName;
            TeamName = r.TeamName;
            HasTeam = !string.IsNullOrEmpty(r.TeamName);
            LiveryBrush = r.IsOut ? Dim : SavedRaceView.BrushFromHex(r.LiveryHex);
            BestLapText = SavedRaceView.FormatTime(r.BestLapMs);
            BestLapBrush = r.IsOut ? Dim : r.HasFastestLap ? Purple : Ink;
            PitsText = r.IsOut ? "—" : r.PitStops.ToString();
            PositionBrush = r.IsOut ? Dim : Ink;
            NameBrush = r.IsOut ? Dim : Ink;
            TeamBrush = r.IsOut ? DimTeam : Muted;
            (GapText, GapBrush) = BuildGap(r, winnerTime, winnerLaps);
            IsOut = r.IsOut;
            IsPlayer = r.IsPlayer;
            HasFastestLap = r.HasFastestLap;
            StintChips = SavedRaceView.BuildSegments(r.Stints);
        }

        // Gap column, F1-classification style: the winner shows their total race time; runners
        // on the lead lap show "+SS.sss" (or "+M:SS.sss"); lapped cars "+N LAP(S)"; retired
        // cars a "DNF" flag (like the race tower). Empty when the data wasn't captured (races
        // saved before this version have winnerTime == 0), so old races show nothing for the
        // running cars rather than a wrong gap - but DNFs are still marked, since that's known.
        private static (string, SolidColorBrush) BuildGap(SavedClassificationRow r, double winnerTime, int winnerLaps)
        {
            if (r.IsOut) return ("DNF", DnfRed);
            if (winnerTime <= 0) return ("", Muted); // not captured for this saved race

            if (r.Position == 1 || r.TotalRaceTimeSeconds <= winnerTime)
                return (FormatRaceTime(r.TotalRaceTimeSeconds > 0 ? r.TotalRaceTimeSeconds : winnerTime), Ink);

            int lapsDown = winnerLaps - r.NumLaps;
            if (lapsDown > 0) return ($"+{lapsDown} LAP{(lapsDown == 1 ? "" : "S")}", Muted);

            return ("+" + FormatGap(r.TotalRaceTimeSeconds - winnerTime), Muted);
        }

        private static string FormatRaceTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.Hours > 0
                ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}"
                : $"{t.Minutes}:{t.Seconds:D2}.{t.Milliseconds:D3}";
        }

        private static string FormatGap(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalMinutes >= 1
                ? $"{(int)t.TotalMinutes}:{t.Seconds:D2}.{t.Milliseconds:D3}"
                : $"{t.Seconds}.{t.Milliseconds:D3}";
        }
    }

    public class LapRowView
    {
        public string LapText { get; }
        public string S1Text { get; } public SolidColorBrush S1Brush { get; }
        public string S2Text { get; } public SolidColorBrush S2Brush { get; }
        public string S3Text { get; } public SolidColorBrush S3Brush { get; }
        public bool HasTag { get; } public string Tag { get; }
        public string PitText { get; }
        public string LapTimeText { get; } public SolidColorBrush LapTimeBrush { get; }
        public string DeltaText { get; }
        public List<LapEvent> Events { get; }

        public LapRowView(SavedLapRow r)
        {
            LapText = r.LapNumber > 0 ? $"Lap {r.LapNumber}" : "Lap";
            S1Text = string.IsNullOrEmpty(r.S1Text) ? "—" : r.S1Text; S1Brush = SavedRaceView.BrushFromHex(r.S1Hex);
            S2Text = string.IsNullOrEmpty(r.S2Text) ? "—" : r.S2Text; S2Brush = SavedRaceView.BrushFromHex(r.S2Hex);
            S3Text = string.IsNullOrEmpty(r.S3Text) ? "—" : r.S3Text; S3Brush = SavedRaceView.BrushFromHex(r.S3Hex);
            HasTag = !string.IsNullOrEmpty(r.Tag); Tag = r.Tag;
            PitText = r.PitTimeText;
            LapTimeText = string.IsNullOrEmpty(r.LapTimeText) ? "—" : r.LapTimeText;
            LapTimeBrush = SavedRaceView.BrushFromHex(r.LapColorHex);
            DeltaText = r.DeltaText;

            Events = new List<LapEvent>();
            foreach (var ev in r.Events)
                if (System.Enum.TryParse<LapEventKind>(ev.Kind, out var kind))
                    Events.Add(new LapEvent(kind, ev.Text));
        }
    }
}
