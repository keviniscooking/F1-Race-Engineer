using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// A completed race captured from the game's Final Classification packet, plus a snapshot
    /// of the player's own lap-by-lap history, persisted to disk as one JSON file (see
    /// RaceHistoryStore). Deliberately a plain, self-contained data object with no WPF types
    /// (colours are stored as "#RRGGBB" hex strings, not brushes) so it round-trips cleanly
    /// through System.Text.Json and can be re-rendered anywhere - the history panel and the
    /// exported HTML both read from this same shape.
    /// </summary>
    public class SavedRace
    {
        // Uniquely identifies the session instance - used to dedupe (the game re-sends Final
        // Classification, and we only ever want one saved file per race).
        public ulong SessionUid { get; set; }
        public DateTime SavedAtUtc { get; set; }

        public string GrandPrix { get; set; } = "Grand Prix";
        public string Circuit { get; set; } = "";
        public string Country { get; set; } = ""; // FIA 3-letter code, shown before the GP name
        public string SessionLabel { get; set; } = "Race"; // Race / Sprint
        public int TotalLaps { get; set; }

        // Persistent grouping keys the game generates so external apps can link sessions:
        // WeekendLinkId ties a Sprint and its Race together (one weekend card, two tabs);
        // SeasonLinkId separates one career/season save from another (per-season sections +
        // summary). Zero for older saved races (captured only from this version on) and for
        // one-off/online sessions the game doesn't link - those fall into catch-all buckets.
        public uint SeasonLinkId { get; set; }
        public uint WeekendLinkId { get; set; }
        public uint SessionLinkId { get; set; }

        // ---- Player result ----
        public int GridPosition { get; set; }
        public int FinishPosition { get; set; }
        public int Points { get; set; }
        public int PitStops { get; set; }
        public string ResultStatus { get; set; } = "Finished"; // Finished / DNF / DSQ / DNC
        public string ResultReason { get; set; } = "";
        public int? RetiredOnLap { get; set; }
        public uint BestLapMs { get; set; }
        public double TotalRaceTimeSeconds { get; set; }
        public int PenaltiesTimeSeconds { get; set; }
        public bool PlayerHasFastestLap { get; set; }
        public List<SavedStint> PlayerStints { get; set; } = new();

        // Snapshot of the player's final Penalties &amp; Flags list (the same strings the live
        // widget showed), so the history detail can render a matching penalties card. Empty
        // for a clean race and for races saved before this was captured.
        public List<string> Penalties { get; set; } = new();

        // ---- Full-field result + the player's own lap detail ----
        public List<SavedClassificationRow> Classification { get; set; } = new();
        public List<SavedLapRow> PlayerLaps { get; set; } = new();

        // Path of the file this race was loaded from - set by RaceHistoryStore on load so
        // Delete can find it. Not serialized (it's an artifact of where the file lives, not
        // part of the race data).
        [JsonIgnore]
        public string? FilePath { get; set; }
    }

    public class SavedStint
    {
        public string Compound { get; set; } = ""; // S / M / H / I / W
        public int EndLap { get; set; }
    }

    public class SavedClassificationRow
    {
        public int Position { get; set; }
        public string DriverName { get; set; } = "";
        public string TeamName { get; set; } = "";
        public string LiveryHex { get; set; } = "#6B7684";
        public uint BestLapMs { get; set; }
        public int PitStops { get; set; }
        public bool IsPlayer { get; set; }
        public bool IsOut { get; set; } // retired / DNF / DSQ / not classified
        public bool HasFastestLap { get; set; }
        public List<SavedStint> Stints { get; set; } = new();

        // Used to compute the gap-to-winner column shown in the history classification.
        // TotalRaceTime is seconds of racing (excludes pit/penalty adjustments the game
        // already folds into finishing order); NumLaps distinguishes lapped cars ("+1 LAP").
        // Zero on races saved before this was captured - those show no gap.
        public double TotalRaceTimeSeconds { get; set; }
        public int NumLaps { get; set; }
    }

    public class SavedLapRow
    {
        public int LapNumber { get; set; }
        public string LapTimeText { get; set; } = "";
        public string LapColorHex { get; set; } = "#E6EDF3";
        public string DeltaText { get; set; } = "";
        public string Tag { get; set; } = "";        // IN / OUT / ""
        public string PitTimeText { get; set; } = "";

        public string S1Text { get; set; } = "";
        public string S1Hex { get; set; } = "#E6EDF3";
        public string S2Text { get; set; } = "";
        public string S2Hex { get; set; } = "#E6EDF3";
        public string S3Text { get; set; } = "";
        public string S3Hex { get; set; } = "#E6EDF3";

        // Notable events on this lap (SC/VSC, red flag, chequered, genuine penalties).
        public List<SavedLapEvent> Events { get; set; } = new();
    }

    public class SavedLapEvent
    {
        public string Kind { get; set; } = ""; // matches LapEventKind names
        public string Text { get; set; } = "";
    }
}
