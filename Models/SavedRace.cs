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

        // The game's raw SessionType NAME (stored by name, like GameMode). Kept because it is the
        // EXACT signal for which session of a sprint weekend this is: F1 25 uses Race2 only for
        // the feature race, while Race covers both a sprint and an ordinary grand prix. Lap count
        // resolves a weekend correctly but only relatively, and would tie-break arbitrarily if two
        // sessions ever came out the same length - the type doesn't. Empty on races saved before
        // this was captured, which is why HistoryGroups still falls back to lap count.
        public string SessionTypeName { get; set; } = "";
        public int TotalLaps { get; set; }

        // Persistent grouping keys the game generates so external apps can link sessions:
        // WeekendLinkId ties a Sprint and its Race together (one weekend card, two tabs);
        // SeasonLinkId separates one career/season save from another (per-season sections +
        // summary). Zero for older saved races (captured only from this version on) and for
        // one-off/online sessions the game doesn't link - those fall into catch-all buckets.
        public uint SeasonLinkId { get; set; }
        public uint WeekendLinkId { get; set; }
        public uint SessionLinkId { get; set; }

        // The game's GameMode enum NAME (not its ordinal), e.g. "Career25Online" - stored by name
        // for the same reason SavedLapEvent stores its kind by name: members can be added or
        // reordered by the library without silently re-labelling every already-saved race.
        // Drives the career-type label in the season header AND identifies a two-player career,
        // which is what makes a saved race eligible for the head-to-head view.
        public string GameMode { get; set; } = "";

        // Populated only for a two-player career (GameMode.Career25Online with exactly two
        // non-AI cars); null everywhere else, so the history panel gates the head-to-head view
        // on this being non-null rather than re-deriving eligibility.
        public SavedHeadToHead? HeadToHead { get; set; }

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

        // The player's penalties for this race, each carrying whether it's a real penalty (red) or
        // a warning (amber) so the history card colours them the way the live widget does. Empty
        // for a clean race.
        public List<SavedPenalty> Penalties { get; set; } = new();

        // ---- Full-field result + the player's own lap detail ----
        public List<SavedClassificationRow> Classification { get; set; } = new();
        public List<SavedLapRow> PlayerLaps { get; set; } = new();

        // Path of the file this race was loaded from - set by RaceHistoryStore on load so
        // Delete can find it. Not serialized (it's an artifact of where the file lives, not
        // part of the race data).
        [JsonIgnore]
        public string? FilePath { get; set; }
    }

    /// <summary>
    /// A penalty as stored in a saved race: the display text plus its category, so the history
    /// card can colour it red (penalty) or amber (warning) exactly like the live widget. Kept a
    /// plain serializable DTO - the brushes live on <see cref="PenaltyEntry"/>, the display model.
    /// </summary>
    public class SavedPenalty
    {
        public string Text { get; set; } = "";
        public bool IsPenalty { get; set; }
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
        // Can legitimately be zero if the game didn't report a time for a car, in which case
        // that row shows no gap rather than a bogus one.
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

    /// <summary>
    /// The two humans of a two-player career, captured so the saved race can be replayed as a
    /// head-to-head. Null on every other race, which is the overwhelming majority - a solo career
    /// pays nothing for this.
    ///
    /// Deliberately stored as RAW MILLISECONDS rather than reusing <see cref="SavedLapRow"/>.
    /// That type is display-oriented (formatted "1:37.355" strings and hex colours) because it
    /// mirrors what the live lap-history widget already rendered; the head-to-head has to
    /// *compute* - sector deltas, ideal laps, median pace, consistency, and a lap-by-lap gap
    /// chart from cumulative times. Parsing formatted text back into numbers to do arithmetic
    /// would be both lossy and absurd, so the numeric series is stored once, properly.
    /// </summary>
    public class SavedHeadToHead
    {
        public SavedH2HDriver You { get; set; } = new();
        public SavedH2HDriver Rival { get; set; } = new();
    }

    public class SavedH2HDriver
    {
        public string Name { get; set; } = "";
        public string Team { get; set; } = "";
        public string LiveryHex { get; set; } = "#9BA7B4";

        public int GridPosition { get; set; }
        public int FinishPosition { get; set; }
        public int Points { get; set; }
        public int PitStops { get; set; }
        public int PenaltiesTimeSeconds { get; set; }
        public bool IsOut { get; set; }
        public bool HasFastestLap { get; set; }
        public double TotalRaceTimeSeconds { get; set; }
        public int NumLaps { get; set; }

        public List<SavedH2HLap> Laps { get; set; } = new();
        public List<SavedStint> Stints { get; set; } = new();
        public List<SavedH2HStop> Stops { get; set; } = new();
    }

    /// <summary>
    /// One pit stop, for either driver. Both timings are captured because they answer different
    /// questions: StationaryMs is the crew's time with the car on jacks - the bit that goes wrong
    /// and loses a position - while LaneMs is the whole pit-lane traversal, which is mostly a
    /// property of the circuit and similar for everyone.
    /// </summary>
    public class SavedH2HStop
    {
        public int Lap { get; set; }
        public uint StationaryMs { get; set; }
        public uint LaneMs { get; set; }
    }

    public class SavedH2HLap
    {
        public int LapNumber { get; set; }
        public uint LapTimeMs { get; set; }
        public uint S1Ms { get; set; }
        public uint S2Ms { get; set; }
        public uint S3Ms { get; set; }
        // The game's own validity flag. Invalidated laps must be excluded from best-lap and
        // ideal-lap maths, matching how the live timing board already sources best laps.
        public bool IsValid { get; set; }
    }
}
