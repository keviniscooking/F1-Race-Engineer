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

        public string BestLapText { get; }
        public SolidColorBrush BestLapBrush { get; }
        public string StopsText { get; }
        public string PointsText { get; }
        public bool HasFastestLap { get; }

        public string GridText { get; }          // "P7"
        public bool HasDnfDetail { get; }
        public string DnfDetail { get; }          // "Retired on lap 61 — Terminal damage"

        public int TotalLaps { get; }
        public List<TyreStintSegment> StintSegments { get; }
        public List<SavedStintTick> StintTicks { get; }
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
            if (delta < 0) { DeltaText = $"▲ {-delta} place{(delta == -1 ? "" : "s")}"; DeltaBrush = Green; }
            else if (delta > 0) { DeltaText = $"▼ {delta} place{(delta == 1 ? "" : "s")}"; DeltaBrush = Red; }
            else { DeltaText = $"— held P{r.GridPosition}"; DeltaBrush = Muted; }

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

            StintSegments = BuildSegments(r.PlayerStints);
            StintTicks = BuildTicks(r.PlayerStints, r.TotalLaps);
            Classification = new List<ClassRowView>();
            foreach (var row in r.Classification) Classification.Add(new ClassRowView(row));
            Laps = new List<LapRowView>();
            foreach (var lap in r.PlayerLaps) Laps.Add(new LapRowView(lap));
        }

        // Stint end-laps -> proportional segments (laps = gap from the previous end-lap).
        internal static List<TyreStintSegment> BuildSegments(List<SavedStint> stints)
        {
            var segs = new List<TyreStintSegment>();
            int prevEnd = 0;
            foreach (var s in stints)
            {
                segs.Add(new TyreStintSegment
                {
                    LapCount = Math.Max(1, s.EndLap - prevEnd),
                    Letter = s.Compound,
                    Brush = CompoundPalette.BrushForLetter(s.Compound),
                    TextBrush = CompoundPalette.ForegroundForLetter(s.Compound)
                });
                prevEnd = s.EndLap;
            }
            return segs;
        }

        // A tick at each stint boundary (the lap a pit happened), positioned as a fraction of
        // the total race distance. The final stint's end (the flag) gets no tick.
        internal static List<SavedStintTick> BuildTicks(List<SavedStint> stints, int totalLaps)
        {
            var ticks = new List<SavedStintTick>();
            if (totalLaps <= 0) return ticks;
            for (int i = 0; i < stints.Count - 1; i++)
                ticks.Add(new SavedStintTick { Lap = stints[i].EndLap, Fraction = Math.Clamp(stints[i].EndLap / (double)totalLaps, 0, 1) });
            return ticks;
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

    public class SavedStintTick
    {
        public int Lap { get; set; }
        public double Fraction { get; set; } // 0..1 position along the bar
    }

    public class ClassRowView
    {
        public int Position { get; }
        public string PositionText { get; }
        public string DriverName { get; }
        public string TeamName { get; }
        public SolidColorBrush LiveryBrush { get; }
        public string BestLapText { get; }
        public SolidColorBrush BestLapBrush { get; }
        public string PitsText { get; }
        public bool IsPlayer { get; }
        public bool HasFastestLap { get; }
        public List<TyreStintSegment> StintChips { get; }

        private static readonly SolidColorBrush Purple = SavedRaceView.BrushFromHex("#AFA9EC");
        private static readonly SolidColorBrush Ink = SavedRaceView.BrushFromHex("#E6EDF3");

        public ClassRowView(SavedClassificationRow r)
        {
            Position = r.Position;
            PositionText = r.IsOut ? "—" : r.Position.ToString();
            DriverName = r.DriverName;
            TeamName = r.TeamName;
            LiveryBrush = SavedRaceView.BrushFromHex(r.LiveryHex);
            BestLapText = SavedRaceView.FormatTime(r.BestLapMs);
            BestLapBrush = r.HasFastestLap ? Purple : Ink;
            PitsText = r.IsOut ? "—" : r.PitStops.ToString();
            IsPlayer = r.IsPlayer;
            HasFastestLap = r.HasFastestLap;
            StintChips = SavedRaceView.BuildSegments(r.Stints);
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
        }
    }
}
