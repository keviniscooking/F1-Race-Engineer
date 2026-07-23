using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using F1Game.UDP.Data;
using F1Game.UDP.Enums;
using F1Game.UDP.Events;
using F1Game.UDP.Packets;
using F1RaceEngineer.Models;

namespace F1RaceEngineer.Telemetry
{
    /// <summary>
    /// Central live state for the whole app. One instance, fed by UdpListenerService,
    /// exposed to widgets via data binding.
    ///
    /// DESIGN NOTE on provisional lap coloring: the current-lap timer's color updates
    /// at each completed sector boundary (comparing that sector's time against personal
    /// best / session best), not continuously interpolated within a sector. Confirmed
    /// working against live driving data as of the Lap Timing widget build.
    ///
    /// DESIGN NOTE on alert banner clearing: Safety Car / VSC are driven directly and
    /// continuously by SessionDataPacket.SafetyCarStatus, so they self-clear correctly.
    /// Red Flag and Chequered Flag have no confirmed "cleared" event in the API, so they
    /// are shown on their trigger event and auto-cleared after a timeout (15s / 8s) as a
    /// pragmatic heuristic - this has not been validated against a real red flag or
    /// session end yet and may need tuning.
    /// </summary>
    public class TelemetryState : ObservableObject
    {
        private int _playerCarIndex = -1;

        // ---- Best-time tracking (player) ----
        private readonly uint?[] _sessionBestSectorMs = new uint?[3];
        private readonly uint?[] _personalBestSectorMs = new uint?[3];
        private uint? _sessionBestLapMs;
        private uint? _personalBestLapMs;
        private TimingColor _lastCompletedSectorColorThisLap = TimingColor.Neutral;

        // Cumulative time through S1 / S1+S2 / full lap, taken from the player's actual
        // best lap (not a theoretical combination of separate laps' best sectors). Used
        // to update Delta vs Personal Best live at each sector boundary, not just at the
        // end of the lap.
        private uint[]? _personalBestLapCumulativeSplitsMs;
        private uint _cumulativeThisLapMs;

        // ---- Best-lap per car (whole field) ---- fed from the game's SessionHistory packet
        // (HandleSessionHistory), which is authoritative and excludes invalidated laps - the same
        // best lap the in-game timing screen ranks by. Drives the Practice/Qualifying board sort
        // (and its gap-to-fastest) and the Race tower's fastest-lap marker.
        private readonly Dictionary<int, uint?> _carBestLapMs = new();

        // ---- Participant identity cache ----
        private readonly Dictionary<int, string> _participantNames = new();
        private readonly Dictionary<int, SolidColorBrush> _participantLivery = new();
        private readonly Dictionary<int, string> _participantTeams = new();

        // Car indices the game reports as NOT AI-controlled, rebuilt from every Participants
        // packet. Two of these plus GameMode.Career25Online is what switches on the two-player
        // head-to-head feature (see CareerNames.IsTwoPlayer) - a multiplayer lobby has many
        // humans and deliberately keeps the ordinary "highlight my own car only" behaviour.
        private readonly HashSet<int> _humanCarIndices = new();

        // Full lap-by-lap (raw ms + sectors) for the two humans of a two-player career, keyed by
        // car index and refreshed from every SessionHistory packet. Only ever holds two entries,
        // and only in that one mode - see HandleSessionHistory.
        private readonly Dictionary<int, List<SavedH2HLap>> _h2hLapHistory = new();

        // Completed pit stops for the two humans, keyed by car index. Unlike the lap history
        // above (re-sent whole by the game every tick), stops are one-shot events, so this
        // accumulates across the race and must be cleared on session reset.
        private readonly Dictionary<int, List<SavedH2HStop>> _h2hStops = new();

        // Tyre compound per car, from CarStatusData (a separate packet from LapData) -
        // cached the same way as participant name/team above so RefreshRaceStandings
        // (driven by LapData) can still read each car's current compound inline.
        private readonly Dictionary<int, VisualCompound> _carTyreCompounds = new();
        private readonly Dictionary<int, int> _carTyreAge = new();

        // ---- Live player tyre stints (Race only) - drives the Tyres widget's stint bar.
        // A new stint is pushed when the player's compound changes or their tyre age drops
        // (fresh set fitted). StartLap is derived as currentLap - age, so the bar reflects
        // when the tyres were actually fitted even if the app joined the race mid-session.
        private readonly List<(VisualCompound Compound, int StartLap)> _liveStints = new();
        private VisualCompound? _liveStintCompound;
        private int _liveStintAge;
        private int _playerCurrentLap;

        // ---- Per-lap events (SC/VSC, red flag, chequered, genuine penalties) for the
        // lap-by-lap EVENTS chips. _lapEventSafetyCar is the caution active during the
        // current lap (re-set each session packet while SC/VSC persists, reset when the lap
        // completes); _pendingLapEvents accrues event-driven items until the lap completes
        // and consumes them.
        private SafetyCarType _lapEventSafetyCar = SafetyCarType.NoSafetyCar;
        private readonly List<LapEvent> _pendingLapEvents = new();

        // A single lap can pick up several penalties (e.g. a stop-go plus two 3s time penalties for
        // the same infringement), which overflowed the lap-by-lap EVENTS cell. Only the most severe
        // penalty is kept as that lap's chip - the full list still lives in the penalties tab. This
        // tracks the severity of the penalty currently held for the open lap (0 = none yet); reset
        // when the lap's events are consumed (BuildLapEvents) and on session change.
        private int _pendingPenaltySeverity;

        // ---- Race history capture ----
        // Persists a completed race (Final Classification + the player's lap-by-lap) to disk.
        // Raised after a NEW race is saved so the UI can refresh its list. _currentTrack is
        // cached from Session packets (Final Classification carries no track); the saved-uid
        // guard stops us rebuilding + re-saving every frame while the game re-sends the packet.
        private readonly RaceHistoryStore _historyStore = new();
        public event Action<SavedRace>? RaceSaved;
        private Track _currentTrack;
        private ulong? _savedClassificationUid;

        // Populated from individual PenaltyIssued events (which carry a specific
        // InfringementType), not from LapData's TotalWarnings (a bare running count
        // with no reason) - lets the Penalties & Flags widget say what each warning was
        // actually for.
        private readonly List<string> _warningReasons = new();

        // Every penalty the player was ISSUED this session, accumulated as it happens (see
        // HandleEvent) - a permanent historical record, unlike the live Penalties & Flags list
        // which shows only what's currently *outstanding* (unserved pens, pending time). That
        // live list is empty by the finish line once everything's been served, so the saved race
        // reads from THIS instead, giving the history a penalties tab that matches the lap-by-lap
        // (a served drive-through/stop-go/time penalty is history the moment it's issued). Each
        // carries a severity so the saved list can be ranked most-severe-first and capped exactly
        // like the live widget (see BuildIncurredPenalties).
        private readonly List<(int Severity, string Text, bool IsPenalty)> _penaltiesIncurred = new();

        private class CarLapTracker
        {
            public byte LastSeenLapNum;
            public ushort LastSeenSector1Ms;
            public ushort LastSeenSector2Ms;

            // Watched independently of LastSeenLapNum - see the comment at its use site
            // in HandleLapData for why a lap can complete without CurrentLapNum advancing.
            public uint LastSeenLastLapTimeMs;

            // Deliberately the PREVIOUS tick's DriverStatus, not the current one: at the
            // exact frame a lap completes, DriverStatus may have already flipped to
            // reflect the lap that's just starting (e.g. OutLap -> OnTrack the instant you
            // cross the line) - using the prior tick's value reliably attributes the
            // in/out-lap classification to the lap that just ended instead.
            public DriverStatus LastKnownDriverStatus;

            // True if DriverStatus was InLap at ANY point during the lap now in progress.
            // The previous-tick status alone is not enough to spot an in-lap: where the pit
            // exit lies BEFORE the start/finish line, the whole stop (entry, box, rejoin)
            // fits inside one lap and the status flips InLap -> OutLap seconds before the
            // line - so that lap read as OutLap and got tagged "OUT", producing the
            // long-standing double-OUT with no IN row (confirmed from the pit log: a stop
            // finished at 21:58:10 and the lap only completed at 21:58:15). Because the IN
            // row never existed, PatchMostRecentInRowPitTime also silently dropped the
            // stationary stop time. Latching "saw InLap this lap" fixes both, and leaves the
            // straddling-pit-lane case (already correct) unchanged. Cleared per lap.
            public bool SawInLapThisLap;

            // PitStopTimerInMS (the stationary box time) goes stale by the time the IN
            // lap's row would normally be created at the line - confirmed live, it reads
            // back as 0. Testing the theory that on some tracks the pit lane sits inside
            // the NEXT lap, not the IN lap itself - meaning the IN row already exists
            // (built a full lap earlier) by the time the stop finishes, so the only way to
            // get the duration in is to patch that row retroactively (see
            // PatchMostRecentInRowPitTime) rather than pass it in at row-creation time.
            public PitStatus LastKnownPitStatus;
            public uint MaxPitStopTimerMsThisStop;

            // PitLaneTimeInLaneInMS (the whole pit lane traversal, entry to exit) has no
            // such ambiguity: the pit lane is always exited before the OUT lap even starts
            // being driven, let alone completes - so by the time PitLaneTimerActive drops
            // back to false, the OUT row can never already exist yet. Straightforward
            // forward-store: latch here, RegisterLapTime consumes it when that row is built.
            public bool LastKnownPitLaneTimerActive;
            public uint MaxPitLaneTimeInLaneMsThisStop;
            public uint PendingPitLaneTimeMs;
        }
        private readonly Dictionary<int, CarLapTracker> _carTrackers = new();

        // TEMP diagnostic (see HANDOFF §6, Monaco pit-tag investigation): logs the player's
        // pit-in/out DriverStatus + PitStatus timeline to a file so the "double OUT / missing
        // stationary stop time" pit-tagging bug (a pit stop that straddles the S/F line) can
        // be fixed from real per-tick data across tracks, rather than guessed. Only writes on
        // pit-relevant state changes / lap completions (nothing during green-flag laps), so
        // the file stays tiny. REMOVE once pit in/out tagging is fixed + confirmed live.
        private static readonly string PitLogPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "F1RaceEngineer", "pit-debug.log");

        private static void LogPitEvent(int lapNum, DriverStatus ds, PitStatus ps, bool laneActive,
            uint stopTimerMs, uint laneTimeMs, uint lastLapMs, string note)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PitLogPath)!);
                System.IO.File.AppendAllText(PitLogPath,
                    $"{DateTime.Now:HH:mm:ss.fff} lap={lapNum} driver={ds} pit={ps} lane={laneActive} " +
                    $"stopT={stopTimerMs} laneT={laneTimeMs} lastLap={lastLapMs} {note}\n");
            }
            catch { /* diagnostic only - never let logging affect the app */ }
        }

        // ---- Alert banner state ----
        private bool _isRedFlagActive;

        // Set when a red flag is shown, cleared by the lap that resumes racing. A red-flag
        // restart releases the whole field from the pit lane, so the game legitimately reports
        // DriverStatus.OutLap for that lap - which would otherwise tag it "OUT" in the PIT
        // column exactly like a pit stop, implying a stop that never happened. Confirmed from
        // the Chinese GP sprint (driver=OutLap held for the entire restart lap).
        private bool _awaitingRedFlagRestart;
        private bool _isChequeredFlagActive;
        private SafetyCarType _safetyCarStatus = SafetyCarType.NoSafetyCar;
        private readonly DispatcherTimer _redFlagTimer = new() { Interval = TimeSpan.FromSeconds(15) };
        private readonly DispatcherTimer _chequeredFlagTimer = new() { Interval = TimeSpan.FromSeconds(8) };

        // ---- Safety Car event transitions (layered on the _safetyCarStatus poll above) ----
        // The poll gives reliable steady state (deployed/active, and it self-clears - confirmed
        // live). What a poll can't express are the two most actionable moments of any SC period -
        // the car coming in and the restart - so the dedicated SafetyCarEvent supplies those.
        // "Returning" is sticky (the SC can take a whole lap to come in, and the peel-off warning
        // should stay up that whole time); "Returned"/"ResumeRace" are brief timed transients.
        private bool _scReturning;
        private SafetyCarType _scReturningType = SafetyCarType.NoSafetyCar;
        private bool _scReturnedActive;
        private bool _scResumeActive;
        private readonly DispatcherTimer _scReturnedTimer = new() { Interval = TimeSpan.FromSeconds(5) };
        private readonly DispatcherTimer _scResumeTimer = new() { Interval = TimeSpan.FromSeconds(4) };

        // Retirement ends the player's session, so there's nothing further to reset it -
        // it just stays up (no timer) until the next session change.
        private bool _isRetirementActive;
        private string _retirementBannerText = "";

        private bool _isPenaltyActive;
        private string _penaltyBannerText = "";
        private readonly DispatcherTimer _penaltyTimer = new() { Interval = TimeSpan.FromSeconds(8) };

        private bool _isTeamMateInPitsActive;
        private string _teamMateInPitsBannerText = "";
        private readonly DispatcherTimer _teamMateInPitsTimer = new() { Interval = TimeSpan.FromSeconds(6) };

        public TelemetryState()
        {
            _redFlagTimer.Tick += (_, _) => { _redFlagTimer.Stop(); _isRedFlagActive = false; RefreshAlertBanner(); };
            _chequeredFlagTimer.Tick += (_, _) => { _chequeredFlagTimer.Stop(); _isChequeredFlagActive = false; RefreshAlertBanner(); };
            _penaltyTimer.Tick += (_, _) => { _penaltyTimer.Stop(); _isPenaltyActive = false; RefreshAlertBanner(); };
            _teamMateInPitsTimer.Tick += (_, _) => { _teamMateInPitsTimer.Stop(); _isTeamMateInPitsActive = false; RefreshAlertBanner(); };
            _scReturnedTimer.Tick += (_, _) => { _scReturnedTimer.Stop(); _scReturnedActive = false; RefreshAlertBanner(); };
            _scResumeTimer.Tick += (_, _) => { _scResumeTimer.Stop(); _scResumeActive = false; RefreshAlertBanner(); };
        }

        // ---- Session / preset ----
        private PresetType _currentPreset = PresetType.Unsupported;
        public PresetType CurrentPreset { get => _currentPreset; private set => SetProperty(ref _currentPreset, value); }

        // True only for an actual Time Trial session. Time Trial maps to the Unsupported
        // preset (no field to rank) but IS a real drivable session where the Lap Timing
        // widget is useful - so the "waiting for a session" placeholder must step aside for
        // it, while still covering every OTHER Unsupported state (cold start AND the game
        // sitting in its menus/lobby, which streams data with an unmapped session type and
        // would otherwise show a bare Lap-Timing-only layout).
        private bool _isTimeTrial;
        public bool IsTimeTrial { get => _isTimeTrial; private set => SetProperty(ref _isTimeTrial, value); }

        /// <summary>
        /// Preview mode hook: lets the UI be reviewed preset-by-preset without a live
        /// game connection (presets are otherwise only ever driven by real session data).
        /// Wired to the small "PREVIEW" buttons in MainWindow; never called from
        /// packet-handling code, and a real session packet will always override it.
        /// </summary>
        public void DebugForcePreset(PresetType preset) => CurrentPreset = preset;


        // ---- Alert banner (bindable) ----
        private bool _alertVisible;
        public bool AlertVisible { get => _alertVisible; private set => SetProperty(ref _alertVisible, value); }

        private string _alertText = "";
        public string AlertText { get => _alertText; private set => SetProperty(ref _alertText, value); }

        private SolidColorBrush _alertBackgroundBrush = TimingColorPalette.AlertNeutralBg;
        public SolidColorBrush AlertBackgroundBrush { get => _alertBackgroundBrush; private set => SetProperty(ref _alertBackgroundBrush, value); }

        private SolidColorBrush _alertTextBrush = TimingColorPalette.AlertNeutralText;
        public SolidColorBrush AlertTextBrush { get => _alertTextBrush; private set => SetProperty(ref _alertTextBrush, value); }

        // ---- Qualifying position list (bindable) ----
        public ObservableCollection<CarStanding> PositionList { get; } = new();

        // ---- Race position tower (bindable) ----
        public ObservableCollection<RaceStanding> RaceStandings { get; } = new();

        // Race's overall lap count, matching the real broadcast's "LAP X / Y" banner -
        // tracks the leader (P1), not necessarily the player, since a lapped player's own
        // CurrentLapNum can trail behind where the race as a whole actually stands.
        // TotalLaps comes from SessionDataPacket (cached here, set in HandleSession);
        // the leader's current lap comes from LapData and is refreshed every tick in
        // RefreshRaceStandings, which already loops over the whole field.
        private byte _totalLaps;
        private string _lapCounterText = "-";
        public string LapCounterText { get => _lapCounterText; private set => SetProperty(ref _lapCounterText, value); }

        // Which Grand Prix this is, shown above the lap counter in the Race tower so the race
        // identifies itself at a glance (the tower is Race-only, so this rides with it). Name and
        // flag come from the cached Track via TrackNames/FlagPalette - the same pair the saved-race
        // history cards use, so the two views name a race identically. HasTrackFlag gates the flag:
        // FlagPalette only has vector flags for the countries drawn so far, and a missing one should
        // simply show the name alone rather than an empty box.
        private string _grandPrixName = "";
        public string GrandPrixName
        {
            get => _grandPrixName;
            private set { if (SetProperty(ref _grandPrixName, value)) Raise(nameof(HasGrandPrix)); }
        }

        // Derived, so the XAML can hide the whole name+flag row before a session has named the
        // track without needing a string-to-visibility converter (the app only has BoolToVis).
        public bool HasGrandPrix => !string.IsNullOrEmpty(_grandPrixName);

        private Brush? _trackFlagBrush;
        public Brush? TrackFlagBrush { get => _trackFlagBrush; private set => SetProperty(ref _trackFlagBrush, value); }

        private bool _hasTrackFlag;
        public bool HasTrackFlag { get => _hasTrackFlag; private set => SetProperty(ref _hasTrackFlag, value); }

        // Grouping keys from SessionDataPacket, cached here and stamped onto the SavedRace so
        // the history can tie a weekend's sessions together and separate season/career saves.
        private uint _seasonLinkId;
        private uint _weekendLinkId;
        private uint _sessionLinkId;
        private GameMode _gameMode;

        // Read by the live Tyres widget to scale its tyre-strategy bar to the whole race
        // distance (a ghost full-length track that the coloured stints fill lap by lap),
        // rather than the elapsed distance. _totalLaps is the race length, _playerCurrentLap
        // the laps run so far. Both are already maintained above for other uses.
        public int RaceTotalLaps => _totalLaps;
        public int PlayerCurrentLap => _playerCurrentLap;

        // Session's fastest lap, shown as the broadcast's purple "FASTEST LAP" strip in the
        // race tower. Sourced from _carBestLapMs (already maintained per car), so no extra
        // tracking - RefreshRaceStandings sets these while it's already finding the holder
        // for the per-row purple badge. HasRaceFastestLap gates the strip's visibility (no
        // lap has been set yet at the very start of a race).
        private bool _hasRaceFastestLap;
        public bool HasRaceFastestLap { get => _hasRaceFastestLap; private set => SetProperty(ref _hasRaceFastestLap, value); }

        private string _fastestLapDriver = "";
        public string FastestLapDriver { get => _fastestLapDriver; private set => SetProperty(ref _fastestLapDriver, value); }

        private string _fastestLapTimeText = "";
        public string FastestLapTimeText { get => _fastestLapTimeText; private set => SetProperty(ref _fastestLapTimeText, value); }

        // ---- Lap timing (bindable) ----
        private string _currentLapTimeText = "-:--.---";
        public string CurrentLapTimeText { get => _currentLapTimeText; private set => SetProperty(ref _currentLapTimeText, value); }

        private SolidColorBrush _currentLapColorBrush = TimingColorPalette.NeutralText;
        public SolidColorBrush CurrentLapColorBrush { get => _currentLapColorBrush; private set => SetProperty(ref _currentLapColorBrush, value); }

        private string _lastLapTimeText = "-:--.---";
        public string LastLapTimeText { get => _lastLapTimeText; private set => SetProperty(ref _lastLapTimeText, value); }

        private SolidColorBrush _lastLapColorBrush = TimingColorPalette.NeutralText;
        public SolidColorBrush LastLapColorBrush { get => _lastLapColorBrush; private set => SetProperty(ref _lastLapColorBrush, value); }

        private string _deltaToPbText = "-";
        public string DeltaToPbText { get => _deltaToPbText; private set => SetProperty(ref _deltaToPbText, value); }

        private SolidColorBrush _deltaToPbColorBrush = TimingColorPalette.NeutralText;
        public SolidColorBrush DeltaToPbColorBrush { get => _deltaToPbColorBrush; private set => SetProperty(ref _deltaToPbColorBrush, value); }

        private string _personalBestLapText = "-:--.---";
        public string PersonalBestLapText { get => _personalBestLapText; private set => SetProperty(ref _personalBestLapText, value); }

        private SolidColorBrush _personalBestLapColorBrush = TimingColorPalette.NeutralText;
        public SolidColorBrush PersonalBestLapColorBrush { get => _personalBestLapColorBrush; private set => SetProperty(ref _personalBestLapColorBrush, value); }

        private bool _isCurrentLapInvalid;
        public bool IsCurrentLapInvalid { get => _isCurrentLapInvalid; private set => SetProperty(ref _isCurrentLapInvalid, value); }

        private string _sector1Text = "";
        public string Sector1Text { get => _sector1Text; private set => SetProperty(ref _sector1Text, value); }
        private SolidColorBrush _sector1TextBrush = TimingColorPalette.NeutralText;
        public SolidColorBrush Sector1TextBrush { get => _sector1TextBrush; private set => SetProperty(ref _sector1TextBrush, value); }
        private SolidColorBrush _sector1BackgroundBrush = TimingColorPalette.NeutralBg;
        public SolidColorBrush Sector1BackgroundBrush { get => _sector1BackgroundBrush; private set => SetProperty(ref _sector1BackgroundBrush, value); }

        private string _sector2Text = "";
        public string Sector2Text { get => _sector2Text; private set => SetProperty(ref _sector2Text, value); }
        private SolidColorBrush _sector2TextBrush = TimingColorPalette.NeutralText;
        public SolidColorBrush Sector2TextBrush { get => _sector2TextBrush; private set => SetProperty(ref _sector2TextBrush, value); }
        private SolidColorBrush _sector2BackgroundBrush = TimingColorPalette.NeutralBg;
        public SolidColorBrush Sector2BackgroundBrush { get => _sector2BackgroundBrush; private set => SetProperty(ref _sector2BackgroundBrush, value); }

        private string _sector3Text = "";
        public string Sector3Text { get => _sector3Text; private set => SetProperty(ref _sector3Text, value); }
        private SolidColorBrush _sector3TextBrush = TimingColorPalette.NeutralText;
        public SolidColorBrush Sector3TextBrush { get => _sector3TextBrush; private set => SetProperty(ref _sector3TextBrush, value); }
        private SolidColorBrush _sector3BackgroundBrush = TimingColorPalette.NeutralBg;
        public SolidColorBrush Sector3BackgroundBrush { get => _sector3BackgroundBrush; private set => SetProperty(ref _sector3BackgroundBrush, value); }

        // Every completed lap is kept here (the whole race). The lap-history widget shows
        // them in an elastic viewport that grows with the window (a star-sized row, see
        // LapTimingWidget.xaml and HANDOFF §5 twenty-ninth round) and scrolls once there are
        // more laps than fit. Nothing is ever dropped, so a full race can be scrolled back
        // through end to end.
        public ObservableCollection<LapHistoryEntry> LapHistory { get; } = new();

        // Lap number of the newest row, so a jump in the game's own lap counter can be spotted.
        // A red flag can advance it by more than one (a real race went 9 -> 11), and without this
        // the missing lap simply vanished from the list with nothing to show it had.
        private int _lastHistoryLapNum;

        // ---- Tyres (bindable) ----
        private string _tyreCompoundLetter = "?";
        public string TyreCompoundLetter { get => _tyreCompoundLetter; private set => SetProperty(ref _tyreCompoundLetter, value); }

        private SolidColorBrush _tyreCompoundBrush = CompoundPalette.Unknown;
        public SolidColorBrush TyreCompoundBrush { get => _tyreCompoundBrush; private set => SetProperty(ref _tyreCompoundBrush, value); }

        private string _tyreAgeLapsText = "-";
        public string TyreAgeLapsText { get => _tyreAgeLapsText; private set => SetProperty(ref _tyreAgeLapsText, value); }

        // The player's tyre-strategy bar (broadcast-style), rebuilt from _liveStints. Empty
        // outside a race. The Tyres widget lays these out as proportional coloured segments.
        public ObservableCollection<TyreStintSegment> TyreStints { get; } = new();

        private bool _hasTyreStints;
        public bool HasTyreStints { get => _hasTyreStints; private set => SetProperty(ref _hasTyreStints, value); }

        private string _tyreWearFrontLeftText = "-";
        public string TyreWearFrontLeftText { get => _tyreWearFrontLeftText; private set => SetProperty(ref _tyreWearFrontLeftText, value); }

        private string _tyreWearFrontRightText = "-";
        public string TyreWearFrontRightText { get => _tyreWearFrontRightText; private set => SetProperty(ref _tyreWearFrontRightText, value); }

        private string _tyreWearRearLeftText = "-";
        public string TyreWearRearLeftText { get => _tyreWearRearLeftText; private set => SetProperty(ref _tyreWearRearLeftText, value); }

        private string _tyreWearRearRightText = "-";
        public string TyreWearRearRightText { get => _tyreWearRearRightText; private set => SetProperty(ref _tyreWearRearRightText, value); }

        // ---- Car Condition (bindable) ----
        // Shared cap for Car Condition and Penalties & Flags - both are unbounded lists
        // in principle (a wrecked car or a heavily-penalized session could list a dozen
        // items), so both get truncated to keep the widget's height predictable next to
        // its catalog-grid siblings, with a "+N more" entry so nothing is silently
        // dropped. This counts ENTRIES, not rows: both widgets lay entries out in a
        // 2-column UniformGrid, so 6 entries fills exactly 3 rows (2+2+2); 7+ shows the
        // 5 most-severe plus a "+N more" summary in the 6th slot.
        private const int IssueListMaxEntries = 6;

        // Penalties & Flags also renders a flag banner ABOVE its list, sized to exactly one grid
        // row (see PenaltiesFlagsWidget.xaml) - i.e. it consumes 2 of the 6 entry slots. Budgeting
        // for it here (list capped to 4 while a flag is up) is what keeps that card the same height
        // as Car Condition instead of growing a row taller and stretching the whole catalog row
        // with it. It's transient: RefreshPenalties runs on every LapData tick, so the list returns
        // to the full 6 as soon as the flag clears. Severity ordering means the entries dropped to
        // make room are always the least important ones.
        private const int FlagBannerEntryCost = 2;

        private bool _carConditionIsOk = true;
        public bool CarConditionIsOk { get => _carConditionIsOk; private set => SetProperty(ref _carConditionIsOk, value); }

        public ObservableCollection<string> CarConditionIssues { get; } = new();

        // ---- Penalties & Flags (bindable) ----
        private bool _penaltiesIsOk = true;
        public bool PenaltiesIsOk { get => _penaltiesIsOk; private set => SetProperty(ref _penaltiesIsOk, value); }

        // Entries rather than bare strings so each line can carry its category (penalty vs warning)
        // through to the UI, which colours them red vs amber - see PenaltyEntry.
        public ObservableCollection<PenaltyEntry> PenaltiesIssues { get; } = new();

        private bool _playerFlagVisible;
        public bool PlayerFlagVisible { get => _playerFlagVisible; private set => SetProperty(ref _playerFlagVisible, value); }

        private string _playerFlagText = "";
        public string PlayerFlagText { get => _playerFlagText; private set => SetProperty(ref _playerFlagText, value); }

        private SolidColorBrush _playerFlagBrush = TimingColorPalette.NeutralText;
        public SolidColorBrush PlayerFlagBrush { get => _playerFlagBrush; private set => SetProperty(ref _playerFlagBrush, value); }

        // ---- Session & Track (bindable) ----
        private string _weatherLabel = "-";
        public string WeatherLabel { get => _weatherLabel; private set => SetProperty(ref _weatherLabel, value); }

        private Geometry _weatherGlyphGeometry = WeatherPalette.GeometryFor(WeatherGlyphKind.Cloud);
        public Geometry WeatherGlyphGeometry { get => _weatherGlyphGeometry; private set => SetProperty(ref _weatherGlyphGeometry, value); }

        private SolidColorBrush _weatherBackgroundBrush = WeatherPalette.CloudBg;
        public SolidColorBrush WeatherBackgroundBrush { get => _weatherBackgroundBrush; private set => SetProperty(ref _weatherBackgroundBrush, value); }

        private string _trackTempText = "-";
        public string TrackTempText { get => _trackTempText; private set => SetProperty(ref _trackTempText, value); }

        private string _airTempText = "-";
        public string AirTempText { get => _airTempText; private set => SetProperty(ref _airTempText, value); }

        private string _currentRainPercentText = "-";
        public string CurrentRainPercentText { get => _currentRainPercentText; private set => SetProperty(ref _currentRainPercentText, value); }

        // A scrollable strip of near-identical forecast cards was hard to read at a
        // glance mid-race and often showed no useful information at all (every card
        // the same when conditions are stable). Collapsed to a single callout for the
        // next meaningfully different sample, or a plain "stable" message if nothing
        // in the forecast horizon differs from current conditions.
        private bool _forecastChangeExpected;
        public bool ForecastChangeExpected
        {
            get => _forecastChangeExpected;
            private set { if (SetProperty(ref _forecastChangeExpected, value)) Raise(nameof(ForecastIsStable)); }
        }

        // Derived, not independently settable - exists only so the XAML can bind the
        // "stable" caption's visibility without a value-inverting converter.
        public bool ForecastIsStable => !_forecastChangeExpected;

        private string _forecastChangeTimeText = "-";
        public string ForecastChangeTimeText { get => _forecastChangeTimeText; private set => SetProperty(ref _forecastChangeTimeText, value); }

        private string _forecastChangeWeatherText = "-";
        public string ForecastChangeWeatherText { get => _forecastChangeWeatherText; private set => SetProperty(ref _forecastChangeWeatherText, value); }

        private Geometry _forecastChangeGlyphGeometry = WeatherPalette.GeometryFor(WeatherGlyphKind.Cloud);
        public Geometry ForecastChangeGlyphGeometry { get => _forecastChangeGlyphGeometry; private set => SetProperty(ref _forecastChangeGlyphGeometry, value); }

        private SolidColorBrush _forecastChangeBackgroundBrush = WeatherPalette.CloudBg;
        public SolidColorBrush ForecastChangeBackgroundBrush { get => _forecastChangeBackgroundBrush; private set => SetProperty(ref _forecastChangeBackgroundBrush, value); }

        private string _forecastChangeRainText = "-";
        public string ForecastChangeRainText { get => _forecastChangeRainText; private set => SetProperty(ref _forecastChangeRainText, value); }

        // ---- Wiring ----
        public void Attach(UdpListenerService listener)
        {
            listener.PacketReceived += OnPacketReceived;
        }

        private void OnPacketReceived(UnionPacket packet)
        {
            // All mutation below touches WPF-bound properties and ObservableCollections,
            // both of which require the UI thread. UDP packets arrive on a background
            // thread, so every packet's handling is marshaled here.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                switch (packet.PacketType)
                {
                    case PacketType.Session when packet.TryGetSessionDataPacket(out SessionDataPacket session):
                        HandleSession(session);
                        break;

                    case PacketType.LapData when packet.TryGetLapDataPacket(out LapDataPacket lapData):
                        HandleLapData(lapData);
                        break;

                    case PacketType.Participants when packet.TryGetParticipantsDataPacket(out ParticipantsDataPacket participants):
                        HandleParticipants(participants);
                        break;

                    case PacketType.Event when packet.TryGetEventDataPacket(out EventDataPacket evt):
                        HandleEvent(evt);
                        break;

                    case PacketType.CarStatus when packet.TryGetCarStatusDataPacket(out CarStatusDataPacket carStatus):
                        HandleCarStatus(carStatus);
                        break;

                    case PacketType.CarDamage when packet.TryGetCarDamageDataPacket(out CarDamageDataPacket carDamage):
                        HandleCarDamage(carDamage);
                        break;

                    case PacketType.SessionHistory when packet.TryGetSessionHistoryDataPacket(out SessionHistoryDataPacket history):
                        HandleSessionHistory(history);
                        break;

                    case PacketType.FinalClassification when packet.TryGetFinalClassificationDataPacket(out FinalClassificationDataPacket finalClass):
                        HandleFinalClassification(finalClass);
                        break;
                }
            });
        }

        private SessionType? _lastSeenSessionType;
        private ulong? _lastSeenSessionUID;

        private void HandleSession(SessionDataPacket session)
        {
            // SessionType alone misses a same-type session restart (e.g. "Restart
            // Session" on a Race while still a Race) - confirmed via live testing: Lap
            // History and other session-scoped state carried over from the previous
            // attempt, producing a misaligned lap count. SessionUID uniquely identifies
            // each session *instance*, so it changes on a restart even when SessionType
            // doesn't - checking both catches a restart a SessionType-only check misses.
            bool sessionChanged = _lastSeenSessionType != session.SessionType || _lastSeenSessionUID != session.Header.SessionUID;
            if (sessionChanged)
            {
                ResetSessionScopedState();
                _lastSeenSessionType = session.SessionType;
                _lastSeenSessionUID = session.Header.SessionUID;
            }

            var preset = PresetMapper.FromSessionType(session.SessionType);

            // TEMP diagnostic (remove with the pit log): capture the raw SessionType F1 25 sends for
            // each session, since the enum's intended semantics don't match observed data - the
            // sprint race came through as SessionType.Race, not Race2 (see SavedRaceView), and this
            // is the likely reason Sprint Qualifying isn't ranking by lap time (it may map to a
            // non-Qualifying preset). One line per session change tells us exactly what to map.
            if (sessionChanged)
                LogPitEvent(0, DriverStatus.InGarage, PitStatus.None, false, 0, 0, session.TotalLaps,
                    // GameMode is logged as its NUMBER as well as its name. F1Game.UDP 26.0.0 only
                    // names the 2025 career modes (27-30), so a 2026 career arrives unnamed - a
                    // real race logged 78, which is DriverCareer25 (28) + 50. Logging the raw
                    // value is how the 2026 set gets learned from real sessions instead of guessed
                    // at from that offset. Human count is logged for the same reason: it's the
                    // other half of the two-player gate and has never been seen live.
                    $"SESSION type={session.SessionType} preset={preset} totalLaps={session.TotalLaps} " +
                    $"gameMode={(int)session.GameMode}({session.GameMode}) humans={_humanCarIndices.Count}");
            if (preset != CurrentPreset)
            {
                CurrentPreset = preset;
            }

            if (session.SafetyCarStatus != _safetyCarStatus)
            {
                _safetyCarStatus = session.SafetyCarStatus;
                // Poll says the SC period is over - drop any sticky "returning" phase that a
                // SafetyCarEvent left up, in case no Returned/ResumeRace event arrived to clear it.
                if (_safetyCarStatus == SafetyCarType.NoSafetyCar) _scReturning = false;
                RefreshAlertBanner();
            }

            // Remember a caution seen at any point during the current lap so the lap's EVENTS
            // chip shows it even if the SC/VSC started and ended mid-lap.
            if (session.SafetyCarStatus is SafetyCarType.FullSafetyCar or SafetyCarType.VirtualSafetyCar)
                _lapEventSafetyCar = session.SafetyCarStatus;

            _totalLaps = session.TotalLaps;
            _currentTrack = session.Track;
            GrandPrixName = TrackNames.GrandPrixFor(_currentTrack);
            TrackFlagBrush = FlagPalette.BrushFor(TrackNames.CountryCodeFor(_currentTrack));
            HasTrackFlag = TrackFlagBrush != null;
            _seasonLinkId = session.SeasonLinkIdentifier;
            _weekendLinkId = session.WeekendLinkIdentifier;
            _sessionLinkId = session.SessionLinkIdentifier;
            // Gates the head-to-head feature as well as labelling the career in history - see
            // CareerNames. Cached here (not read at save time) because Final Classification
            // carries no game mode, exactly like _currentTrack above.
            _gameMode = session.GameMode;
            IsTimeTrial = session.SessionType == SessionType.TimeTrial;

            RefreshSessionAndTrack(session);
        }

        private void RefreshSessionAndTrack(SessionDataPacket session)
        {
            WeatherLabel = WeatherPalette.LabelFor(session.Weather);
            WeatherGlyphGeometry = WeatherPalette.GeometryFor(WeatherPalette.GlyphFor(session.Weather));
            WeatherBackgroundBrush = WeatherPalette.BackgroundFor(session.Weather);
            TrackTempText = $"{session.TrackTemperature}°C";
            AirTempText = $"{session.AirTemperature}°C";

            var samples = session.WeatherForecastSamples.AsSpan();
            int count = Math.Min(session.NumWeatherForecastSamples, samples.Length);

            string? currentRainText = null;
            byte currentRainValue = 0;
            var relevant = new List<WeatherForecastSample>();
            for (int i = 0; i < count; i++)
            {
                var s = samples[i];
                if (s.SessionType != session.SessionType) continue;
                relevant.Add(s);
                if (s.TimeOffset == 0)
                {
                    currentRainText = $"{s.RainPercentage}%";
                    currentRainValue = s.RainPercentage;
                }
            }
            relevant.Sort((a, b) => a.TimeOffset.CompareTo(b.TimeOffset));

            CurrentRainPercentText = currentRainText ?? "-";

            // Only surface the forecast when it actually differs from now - a strip of
            // near-identical cards (same weather, same rain %) when conditions are stable
            // was hard to read at a glance mid-race and told the driver nothing useful.
            // Find the first upcoming sample that's a meaningfully different, and show
            // just that one; otherwise the forecast is treated as "stable".
            const int RainChangeThresholdPercent = 15;
            WeatherForecastSample? nextChange = null;
            foreach (var s in relevant)
            {
                if (s.TimeOffset == 0) continue;
                bool weatherChanged = s.Weather != session.Weather;
                bool rainChanged = Math.Abs(s.RainPercentage - currentRainValue) >= RainChangeThresholdPercent;
                if (weatherChanged || rainChanged)
                {
                    nextChange = s;
                    break;
                }
            }

            ForecastChangeExpected = nextChange.HasValue;
            if (nextChange is { } change)
            {
                ForecastChangeTimeText = $"In {change.TimeOffset} min";
                ForecastChangeWeatherText = WeatherPalette.LabelFor(change.Weather);
                ForecastChangeGlyphGeometry = WeatherPalette.GeometryFor(WeatherPalette.GlyphFor(change.Weather));
                ForecastChangeBackgroundBrush = WeatherPalette.BackgroundFor(change.Weather);
                ForecastChangeRainText = $"{change.RainPercentage}% rain";
            }
        }

        /// <summary>
        /// Clears everything that should not carry over from one session to the next -
        /// this is what fixes practice lap times leaking into a fresh qualifying session's
        /// "Best" column. Triggered on any SessionType change, which also correctly resets
        /// between Q1/Q2/Q3 segments, matching real F1 timing behavior.
        /// </summary>
        private void ResetSessionScopedState()
        {
            // TEMP pit diagnostic: mark session boundaries so races are separable in the log.
            LogPitEvent(0, DriverStatus.InGarage, PitStatus.None, false, 0, 0, 0, "===== SESSION RESET =====");

            _sessionBestSectorMs[0] = _sessionBestSectorMs[1] = _sessionBestSectorMs[2] = null;
            _personalBestSectorMs[0] = _personalBestSectorMs[1] = _personalBestSectorMs[2] = null;
            _sessionBestLapMs = null;
            _personalBestLapMs = null;
            _personalBestLapCumulativeSplitsMs = null;
            _cumulativeThisLapMs = 0;
            _lastCompletedSectorColorThisLap = TimingColor.Neutral;

            _carBestLapMs.Clear();
            _h2hLapHistory.Clear(); // a previous session's laps would otherwise be saved as this race's head-to-head
            _h2hStops.Clear();      // accumulated, not re-sent by the game - would carry into the next race otherwise
            _carTrackers.Clear(); // stale baselines from the old session would cause spurious sector/lap detections otherwise
            _carTyreCompounds.Clear();
            _carTyreAge.Clear();

            // Empty at the start of a session; the widget's viewport (LapTimingWidget.xaml)
            // reserves its own space in the layout, so there's no need to pad with placeholder
            // rows - real laps just fill in from the top as they complete.
            LapHistory.Clear();
            _lastHistoryLapNum = 0;

            PositionList.Clear();
            RaceStandings.Clear();
            _totalLaps = 0;
            LapCounterText = "-";
            // Cleared with the rest of the tower's race-scoped state so the purple
            // "FASTEST LAP" strip doesn't linger from the previous session until the new
            // session's first LapData tick recomputes it.
            HasRaceFastestLap = false;
            FastestLapDriver = "";
            FastestLapTimeText = "";
            ResetSectorDisplayForNewLap();

            CurrentLapTimeText = "-:--.---";
            CurrentLapColorBrush = TimingColorPalette.NeutralText;
            LastLapTimeText = "-:--.---";
            LastLapColorBrush = TimingColorPalette.NeutralText;
            DeltaToPbText = "-";
            DeltaToPbColorBrush = TimingColorPalette.NeutralText;
            PersonalBestLapText = "-:--.---";
            PersonalBestLapColorBrush = TimingColorPalette.NeutralText;
            IsCurrentLapInvalid = false;

            // Red Flag and Chequered Flag are deliberately NOT reset here, unlike every
            // other banner below. Confirmed via live testing: the game's SessionType
            // changes within a few seconds of crossing the finish line (session
            // transitioning toward results), which was killing the Chequered Flag banner
            // at exactly the moment it exists to mark - cutting its 8s display down to
            // ~3s every time. Both are self-clearing via their own DispatcherTimer
            // regardless, so leaving them alone here doesn't risk a banner sticking
            // around forever - it just lets it finish its own countdown across the
            // session boundary it's specifically announcing. Penalty/Retirement/
            // Team-mate-in-pits are still reset immediately: those are tied to a specific
            // incident in the OLD session and would be genuinely stale carried into a new
            // one, unlike a flag that's inherently about the session-ending moment itself.
            _isRetirementActive = false;
            _isPenaltyActive = false;
            _isTeamMateInPitsActive = false;
            _penaltyTimer.Stop();
            _teamMateInPitsTimer.Stop();
            // Safety Car event transitions are per-session too - don't let a peel-off / resume
            // banner from the old session carry over. (_safetyCarStatus itself is re-established
            // by the next Session poll, so it isn't reset here.)
            _scReturning = false;
            _scReturnedActive = false;
            _scResumeActive = false;
            _scReturnedTimer.Stop();
            _scResumeTimer.Stop();
            RefreshAlertBanner();

            TyreCompoundLetter = "?";
            TyreCompoundBrush = CompoundPalette.Unknown;
            TyreAgeLapsText = "-";
            TyreWearFrontLeftText = TyreWearFrontRightText = TyreWearRearLeftText = TyreWearRearRightText = "-";

            _liveStints.Clear();
            _liveStintCompound = null;
            _liveStintAge = 0;
            _playerCurrentLap = 0;
            TyreStints.Clear();
            HasTyreStints = false;
            _savedClassificationUid = null;
            _pendingLapEvents.Clear();
            _pendingPenaltySeverity = 0;
            _lapEventSafetyCar = SafetyCarType.NoSafetyCar;
            // A red flag that never got its restart lap (session abandoned, or the game handed
            // us a new SessionUID at the restart) must not carry into the next session.
            _awaitingRedFlagRestart = false;

            CarConditionIssues.Clear();
            CarConditionIsOk = true;

            PenaltiesIssues.Clear();
            PenaltiesIsOk = true;
            _warningReasons.Clear();
            _penaltiesIncurred.Clear();
            PlayerFlagVisible = false;
            PlayerFlagText = "";

            WeatherLabel = "-";
            WeatherGlyphGeometry = WeatherPalette.GeometryFor(WeatherGlyphKind.Cloud);
            WeatherBackgroundBrush = WeatherPalette.CloudBg;
            TrackTempText = "-";
            AirTempText = "-";
            CurrentRainPercentText = "-";
            ForecastChangeExpected = false;
            ForecastChangeTimeText = "-";
            ForecastChangeWeatherText = "-";
            ForecastChangeGlyphGeometry = WeatherPalette.GeometryFor(WeatherGlyphKind.Cloud);
            ForecastChangeBackgroundBrush = WeatherPalette.CloudBg;
            ForecastChangeRainText = "-";
        }

        private void HandleParticipants(ParticipantsDataPacket packet)
        {
            var span = packet.Participants.AsSpan();
            int count = Math.Min(packet.NumActiveCars, span.Length);

            _humanCarIndices.Clear();
            for (int i = 0; i < count; i++)
            {
                var p = span[i];
                if (!p.IsAiControlled) _humanCarIndices.Add(i);
                _participantNames[i] = string.IsNullOrWhiteSpace(p.Name) ? $"Car {i + 1}" : p.Name;
                _participantTeams[i] = TeamNames.LabelFor(p.Team);

                var liveryArray = p.LiveryColors.AsSpan();
                if (liveryArray.Length > 0)
                {
                    var c = liveryArray[0];
                    var brush = new SolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue));
                    brush.Freeze();
                    _participantLivery[i] = brush;
                }
            }
        }

        private void HandleEvent(EventDataPacket packet)
        {
            var eventType = packet.EventDetails.EventType;
            int playerIdx = packet.Header.PlayerCarIndex;

            if (eventType == EventType.RedFlag)
            {
                _isRedFlagActive = true;
                _awaitingRedFlagRestart = true;
                _redFlagTimer.Stop();
                _redFlagTimer.Start();
                AddPendingLapEvent(new LapEvent(LapEventKind.RedFlag, "Red Flag"));
                RefreshAlertBanner();
            }
            else if (eventType == EventType.ChequeredFlag)
            {
                _isChequeredFlagActive = true;
                _chequeredFlagTimer.Stop();
                _chequeredFlagTimer.Start();
                AddPendingLapEvent(new LapEvent(LapEventKind.Chequered, "Chequered"));
                RefreshAlertBanner();
            }
            else if (eventType == EventType.PenaltyIssued && packet.EventDetails.TryGetPenaltyEvent(out var penalty))
            {
                if (penalty.VehicleIdx != playerIdx) return;

                string infringement = EventLabels.LabelFor(penalty.InfringementType);
                _penaltyBannerText = penalty.PenaltyType == PenaltyType.TimePenalty
                    ? $"{penalty.Time}s time penalty - {infringement}"
                    : $"{EventLabels.LabelFor(penalty.PenaltyType)} - {infringement}";

                // Captured here (not derivable from LapData's TotalWarnings, which is
                // only ever a running count with no reason) so the persistent Penalties
                // & Flags list can show what each warning was actually for, not just how
                // many - the banner alone is easy to miss mid-race.
                if (penalty.PenaltyType == PenaltyType.Warning)
                {
                    _warningReasons.Add(infringement);
                }

                // Permanent record for the saved race's penalties tab (see _penaltiesIncurred):
                // capture the meaningful penalties as issued, so served ones survive to the history
                // instead of vanishing from the live list once the race is over.
                var incurredLine = PenaltyHistoryLine(penalty.PenaltyType, penalty.Time, infringement);
                if (incurredLine.HasValue) _penaltiesIncurred.Add(incurredLine.Value);

                // Genuine time-costing penalties (not warnings/lap-invalidations) get a chip on the
                // lap they were issued - but only ONE, the most severe: several penalties on a lap
                // (stop-go + time penalties) used to stack up and overflow the EVENTS cell. The full
                // set is still in the penalties tab.
                var penaltyEvent = PenaltyToLapEvent(penalty.PenaltyType, penalty.Time, infringement);
                if (penaltyEvent != null)
                {
                    int severity = PenaltyLapEventSeverity(penalty.PenaltyType);
                    if (severity > _pendingPenaltySeverity)
                    {
                        _pendingLapEvents.RemoveAll(e => e.Kind is LapEventKind.Penalty or LapEventKind.Warning);
                        _pendingLapEvents.Add(penaltyEvent);
                        _pendingPenaltySeverity = severity;
                    }
                }

                _isPenaltyActive = true;
                _penaltyTimer.Stop();
                _penaltyTimer.Start();
                RefreshAlertBanner();
            }
            else if (eventType == EventType.Retirement && packet.EventDetails.TryGetRetirementEvent(out var retirement))
            {
                if (retirement.VehicleIdx != playerIdx) return;

                _retirementBannerText = $"Retired - {EventLabels.LabelFor(retirement.Reason)}";
                _isRetirementActive = true;
                RefreshAlertBanner();
            }
            else if (eventType == EventType.TeamMateInPits && packet.EventDetails.TryGetTeamMateInPitsEvent(out var teamMate))
            {
                _participantNames.TryGetValue(teamMate.VehicleIdx, out var teamMateName);
                _teamMateInPitsBannerText = teamMateName != null ? $"Team-mate {teamMateName} is in the pits" : "Team-mate is in the pits";

                _isTeamMateInPitsActive = true;
                _teamMateInPitsTimer.Stop();
                _teamMateInPitsTimer.Start();
                RefreshAlertBanner();
            }
            else if (eventType == EventType.SafetyCar && packet.EventDetails.TryGetSafetyCarEvent(out var safetyCar))
            {
                HandleSafetyCarEvent(safetyCar);
            }
        }

        /// <summary>
        /// The dedicated SafetyCarEvent transitions, layered on top of the steady _safetyCarStatus
        /// poll (which still drives the "deployed" banner). Deployed just clears any stale transient;
        /// Returning raises the sticky peel-off warning; Returned and ResumeRace are brief timed
        /// banners. Confirmed live: a real safety car fired the full Deployed → Returning → Returned
        /// → ResumeRace sequence and drove the banners as intended.
        /// </summary>
        private void HandleSafetyCarEvent(SafetyCarEvent sc)
        {
            switch (sc.EventType)
            {
                case SafetyCarEventType.Deployed:
                    _scReturning = false; _scReturnedActive = false; _scResumeActive = false;
                    break;
                case SafetyCarEventType.Returning:
                    _scReturning = true; _scReturningType = sc.SafetyCarType;
                    _scReturnedActive = false; _scResumeActive = false;
                    break;
                case SafetyCarEventType.Returned:
                    _scReturning = false; _scResumeActive = false;
                    _scReturnedActive = true; _scReturnedTimer.Stop(); _scReturnedTimer.Start();
                    break;
                case SafetyCarEventType.ResumeRace:
                    _scReturning = false; _scReturnedActive = false;
                    _scResumeActive = true; _scResumeTimer.Stop(); _scResumeTimer.Start();
                    break;
            }
            RefreshAlertBanner();
        }

        // Red flag / chequered fire once but the event can repeat - only keep one per lap.
        private void AddPendingLapEvent(LapEvent e)
        {
            if (_pendingLapEvents.Any(x => x.Kind == e.Kind)) return;
            _pendingLapEvents.Add(e);
        }

        // Infringement chips for the lap they happened on: the three time-costing penalties (red)
        // and warnings (amber, LapEventKind.Warning). Lap-invalidations are still excluded. The
        // chip colour says which it is, so - as in the Penalties & Flags list - the text carries
        // only the infringement, with no "Warning"/"Penalty" prefix.
        private static LapEvent? PenaltyToLapEvent(PenaltyType type, int timeSeconds, string infringement) => type switch
        {
            PenaltyType.TimePenalty => new LapEvent(LapEventKind.Penalty, $"{timeSeconds}s · {infringement}"),
            PenaltyType.DriveThrough => new LapEvent(LapEventKind.Penalty, $"Drive-through · {infringement}"),
            PenaltyType.StopGo => new LapEvent(LapEventKind.Penalty, $"Stop-go · {infringement}"),
            PenaltyType.Warning => new LapEvent(LapEventKind.Warning, infringement),
            _ => null
        };

        // Ranks the infringement kinds that get a lap chip, so a lap keeps only its most severe one
        // (stop-go > drive-through > time penalty > warning) - same order the penalties widget/tab
        // use. One chip per lap keeps the EVENTS cell from overflowing, which is what several
        // penalties for a single incident used to do; the full set is still in the penalties tab.
        private static int PenaltyLapEventSeverity(PenaltyType type) => type switch
        {
            PenaltyType.StopGo => 4,
            PenaltyType.DriveThrough => 3,
            PenaltyType.TimePenalty => 2,
            PenaltyType.Warning => 1,
            _ => 0
        };

        // One entry for the saved race's penalties tab. Covers the meaningful penalties - the three
        // time-costing ones the lap-by-lap also chips, plus warnings. Everything else (lap-time
        // invalidations etc.) is deliberately skipped, same "real penalties only" call
        // PenaltyToLapEvent makes. Severities mirror the live Penalties & Flags widget
        // (RefreshPenalties) so the saved list ranks the same way: stop-go > drive-through > time
        // penalty > warning. IsPenalty drives the red-vs-amber colour; because the colour carries
        // the category the warning line is just the infringement, with no "Warning - " prefix.
        private static (int Severity, string Text, bool IsPenalty)? PenaltyHistoryLine(PenaltyType type, int timeSeconds, string infringement) => type switch
        {
            PenaltyType.StopGo => (100, $"Stop-go - {infringement}", true),
            PenaltyType.DriveThrough => (90, $"Drive-through - {infringement}", true),
            PenaltyType.TimePenalty => (80, $"+{timeSeconds}s - {infringement}", true),
            PenaltyType.Warning => (50, infringement, false),
            _ => null
        };

        // The saved race's penalties list: every penalty issued this race, ranked and capped to
        // match the live Penalties & Flags widget exactly. Identical penalties collapse into an
        // "(xN)" multiplier (so three track-limit warnings read as one line), the list is ordered
        // most-severe-first, then capped to IssueListMaxEntries with a "+N more" overflow so a
        // heavily-penalised race can't outgrow the card - the same CapIssueList the live widget uses.
        private List<SavedPenalty> BuildIncurredPenalties()
        {
            var ranked = _penaltiesIncurred
                .GroupBy(p => p.Text)
                .Select(g => (Severity: g.Max(x => x.Severity), Count: g.Count(), Text: g.Key, IsPenalty: g.First().IsPenalty))
                .OrderByDescending(g => g.Severity)
                .Select(g => new SavedPenalty
                {
                    Text = g.Count > 1 ? $"{g.Text} (x{g.Count})" : g.Text,
                    IsPenalty = g.IsPenalty
                })
                .ToList();
            return CapIssueList(ranked, IssueListMaxEntries,
                n => new SavedPenalty { Text = $"+{n} more", IsPenalty = false });
        }

        private void RefreshAlertBanner()
        {
            if (_isRedFlagActive)
            {
                AlertVisible = true;
                AlertText = "Red flag - session stopped";
                AlertBackgroundBrush = TimingColorPalette.AlertRedBg;
                AlertTextBrush = TimingColorPalette.AlertRedText;
            }
            else if (_isRetirementActive)
            {
                AlertVisible = true;
                AlertText = _retirementBannerText;
                AlertBackgroundBrush = TimingColorPalette.AlertRedBg;
                AlertTextBrush = TimingColorPalette.AlertRedText;
            }
            else if (_isPenaltyActive)
            {
                AlertVisible = true;
                AlertText = _penaltyBannerText;
                AlertBackgroundBrush = TimingColorPalette.AlertAmberBg;
                AlertTextBrush = TimingColorPalette.AlertAmberText;
            }
            // Safety Car group, in lifecycle order so the most time-critical moment wins: the green
            // "racing resumes" go-signal, then the peel-off warning while it's coming in, then the
            // brief "in the pits", then the steady deployed state from the poll. Resume/Returning/
            // Returned come from the SafetyCarEvent; the two Deployed states from the poll.
            else if (_scResumeActive)
            {
                AlertVisible = true;
                AlertText = "Racing resumes";
                AlertBackgroundBrush = TimingColorPalette.AlertGreenBg;
                AlertTextBrush = TimingColorPalette.AlertGreenText;
            }
            else if (_scReturning)
            {
                AlertVisible = true;
                AlertText = _scReturningType == SafetyCarType.VirtualSafetyCar
                    ? "Virtual safety car ending"
                    : "Safety car in this lap - peel off";
                AlertBackgroundBrush = TimingColorPalette.AlertAmberBg;
                AlertTextBrush = TimingColorPalette.AlertAmberText;
            }
            else if (_scReturnedActive)
            {
                AlertVisible = true;
                AlertText = "Safety car in the pits";
                AlertBackgroundBrush = TimingColorPalette.AlertAmberBg;
                AlertTextBrush = TimingColorPalette.AlertAmberText;
            }
            else if (_safetyCarStatus == SafetyCarType.FullSafetyCar)
            {
                AlertVisible = true;
                AlertText = "Safety car deployed - no overtaking";
                AlertBackgroundBrush = TimingColorPalette.AlertAmberBg;
                AlertTextBrush = TimingColorPalette.AlertAmberText;
            }
            else if (_safetyCarStatus == SafetyCarType.VirtualSafetyCar)
            {
                AlertVisible = true;
                AlertText = "Virtual safety car - hold your delta";
                AlertBackgroundBrush = TimingColorPalette.AlertAmberBg;
                AlertTextBrush = TimingColorPalette.AlertAmberText;
            }
            else if (_isTeamMateInPitsActive)
            {
                AlertVisible = true;
                AlertText = _teamMateInPitsBannerText;
                AlertBackgroundBrush = TimingColorPalette.AlertNeutralBg;
                AlertTextBrush = TimingColorPalette.AlertNeutralText;
            }
            else if (_isChequeredFlagActive)
            {
                AlertVisible = true;
                AlertText = "Chequered flag";
                AlertBackgroundBrush = TimingColorPalette.AlertNeutralBg;
                AlertTextBrush = TimingColorPalette.AlertNeutralText;
            }
            else
            {
                AlertVisible = false;
                AlertText = "";
            }
        }

        private void HandleCarStatus(CarStatusDataPacket packet)
        {
            int playerIdx = packet.Header.PlayerCarIndex;
            var span = packet.CarStatusData.AsSpan();
            if (playerIdx < 0 || playerIdx >= span.Length) return;

            for (int i = 0; i < span.Length; i++)
            {
                _carTyreCompounds[i] = span[i].VisualTyreCompound;
                _carTyreAge[i] = span[i].TyresAgeLaps;
            }

            var car = span[playerIdx];

            TyreCompoundLetter = CompoundPalette.LetterFor(car.VisualTyreCompound);
            TyreCompoundBrush = CompoundPalette.BrushFor(car.VisualTyreCompound);
            TyreAgeLapsText = car.TyresAgeLaps.ToString();

            UpdateLiveStints(car.VisualTyreCompound, car.TyresAgeLaps);

            RefreshPlayerFlag(car.VehicleFiaFlags);
        }

        /// <summary>
        /// Pushes a new stint onto the live tyre-strategy bar when the player's compound
        /// changes or their tyre age drops (a fresh set fitted). Race only - the bar is
        /// meaningless in Practice/Qualifying where the player does short unrelated runs.
        /// </summary>
        private void UpdateLiveStints(VisualCompound compound, int age)
        {
            if (CurrentPreset != PresetType.Race) return;

            bool first = _liveStints.Count == 0;
            bool compoundChanged = _liveStintCompound.HasValue && compound != _liveStintCompound.Value;
            bool ageReset = _liveStintCompound.HasValue && age < _liveStintAge; // fresh tyres

            if (first || compoundChanged || ageReset)
            {
                // Age counts laps already run on the tyre, so currentLap - age is the lap it was
                // fitted on - accurate even if the app joined the race mid-session. For a stint
                // that follows a PIT STOP the new stint starts the lap AFTER that: you drove the
                // pit lap itself on the old tyres until the stop, so it belongs to the outgoing
                // stint. That's what puts the bar's pit marker on the lap you actually pitted,
                // matching the lap-by-lap "IN" tag and the saved-race bar (AlignStintsToInLaps),
                // instead of a lap early. The opening stint is exempt - it starts on lap 1.
                // A LATER stint starts on the lap after the change was OBSERVED - not on a lap
                // derived from tyre age. Age arithmetic (currentLap - age) assumes the game resets
                // TyresAgeLaps when tyres are fitted, and after a RED-FLAG tyre change it doesn't:
                // a real Chinese GP showed fresh hards still reporting ~10 laps of age, which put
                // the stint boundary at lap 1 and drew a pit stop on the opening lap that never
                // happened. The observed change moment needs no such assumption. Age is still used
                // for the FIRST stint, which is the one case it's needed for - it's how a mid-race
                // connect works out when the opening set went on.
                int startLap = first
                    ? Math.Max(1, _playerCurrentLap - age)
                    : Math.Max(1, _playerCurrentLap + 1);
                var last = _liveStints.Count > 0 ? _liveStints[^1] : default;
                if (_liveStints.Count == 0 || last.StartLap != startLap || last.Compound != compound)
                {
                    _liveStints.Add((compound, startLap));
                    RebuildTyreStints();
                }
            }

            _liveStintCompound = compound;
            _liveStintAge = age;
        }

        /// <summary>
        /// Rebuilds the bindable <see cref="TyreStints"/> segments from <see cref="_liveStints"/>.
        /// Each stint's lap count is the gap to the next stint's start (the last stint runs to
        /// the current lap), which is what drives its proportional width in the bar.
        /// </summary>
        private void RebuildTyreStints()
        {
            TyreStints.Clear();
            for (int i = 0; i < _liveStints.Count; i++)
            {
                int start = _liveStints[i].StartLap;
                int nextStart = i + 1 < _liveStints.Count ? _liveStints[i + 1].StartLap : _playerCurrentLap + 1;
                var c = _liveStints[i].Compound;
                TyreStints.Add(new TyreStintSegment
                {
                    LapCount = Math.Max(1, nextStart - start),
                    Letter = CompoundPalette.LetterFor(c),
                    Brush = CompoundPalette.BrushFor(c),
                    TextBrush = CompoundPalette.ForegroundFor(c)
                });
            }
            HasTyreStints = TyreStints.Count > 0;
        }

        private void RefreshPlayerFlag(FiaFlag flag)
        {
            switch (flag)
            {
                case FiaFlag.Blue:
                    PlayerFlagVisible = true;
                    PlayerFlagText = "BLUE - let car through";
                    PlayerFlagBrush = TimingColorPalette.BlueText;
                    break;
                case FiaFlag.Yellow:
                    PlayerFlagVisible = true;
                    PlayerFlagText = "YELLOW - caution";
                    PlayerFlagBrush = TimingColorPalette.YellowText;
                    break;
                case FiaFlag.Green:
                    PlayerFlagVisible = true;
                    PlayerFlagText = "GREEN - clear";
                    PlayerFlagBrush = TimingColorPalette.GreenText;
                    break;
                default:
                    PlayerFlagVisible = false;
                    PlayerFlagText = "";
                    break;
            }
        }

        private void HandleCarDamage(CarDamageDataPacket packet)
        {
            int playerIdx = packet.Header.PlayerCarIndex;
            var span = packet.CarDamageData.AsSpan();
            if (playerIdx < 0 || playerIdx >= span.Length) return;
            var car = span[playerIdx];

            TyreWearFrontLeftText = $"{car.TyresWear.FrontLeft:F0}%";
            TyreWearFrontRightText = $"{car.TyresWear.FrontRight:F0}%";
            TyreWearRearLeftText = $"{car.TyresWear.RearLeft:F0}%";
            TyreWearRearRightText = $"{car.TyresWear.RearRight:F0}%";

            // Severity-ranked so the most urgent issue always sorts to the top: a car
            // that can't drive (engine blown/seized) or has a broken system (DRS/ERS
            // fault) matters more than any amount of cosmetic wing/floor damage, so
            // those get a fixed severity above the 0-100 damage-percentage range;
            // percentage items then rank each other by how damaged they actually are.
            var issues = new List<(int Severity, string Text)>();
            AddIssueIfDamaged(issues, car.FrontLeftWingDamage, "Front left wing");
            AddIssueIfDamaged(issues, car.FrontRightWingDamage, "Front right wing");
            AddIssueIfDamaged(issues, car.RearWingDamage, "Rear wing");
            AddIssueIfDamaged(issues, car.FloorDamage, "Floor");
            AddIssueIfDamaged(issues, car.DiffuserDamage, "Diffuser");
            AddIssueIfDamaged(issues, car.SidepodDamage, "Sidepod");
            AddIssueIfDamaged(issues, car.GearBoxDamage, "Gearbox");
            AddIssueIfDamaged(issues, car.EngineDamage, "Engine");
            AddIssueIfDamaged(issues, car.BrakesDamage.FrontLeft, "Front left brake");
            AddIssueIfDamaged(issues, car.BrakesDamage.FrontRight, "Front right brake");
            AddIssueIfDamaged(issues, car.BrakesDamage.RearLeft, "Rear left brake");
            AddIssueIfDamaged(issues, car.BrakesDamage.RearRight, "Rear right brake");
            // TyresDamage deliberately excluded - the Tyres widget already shows a
            // per-corner wear readout, and this widget's own damage% was near-identical
            // to it in practice, reading as duplicated rather than distinct information.
            AddIssueIfDamaged(issues, car.TyresBlisters.FrontLeft, "Front left blister");
            AddIssueIfDamaged(issues, car.TyresBlisters.FrontRight, "Front right blister");
            AddIssueIfDamaged(issues, car.TyresBlisters.RearLeft, "Rear left blister");
            AddIssueIfDamaged(issues, car.TyresBlisters.RearRight, "Rear right blister");
            // Deliberately NOT included: EngineMGUHWear/ESWear/CEWear/ICEWear/MGUKWear/
            // TCWear. Confirmed via live testing that these are background engine-component
            // wear that accumulates every race regardless of incidents (the game's part-
            // degradation/grid-penalty mechanic), not incident damage - including them made
            // this widget show "damage" on every single lap of every race.
            if (car.DrsFault) issues.Add((101, "DRS fault"));
            if (car.ErsFault) issues.Add((101, "ERS fault"));
            if (car.EngineBlown) issues.Add((102, "Engine blown"));
            if (car.EngineSeized) issues.Add((102, "Engine seized"));

            CarConditionIsOk = issues.Count == 0;
            // No banner on this widget, so it always gets the full entry budget.
            var ordered = CapIssueList(
                issues.OrderByDescending(i => i.Severity).Select(i => i.Text).ToList(),
                IssueListMaxEntries,
                n => $"+{n} more");
            if (!CollectionUnchanged(CarConditionIssues, ordered))
            {
                CarConditionIssues.Clear();
                foreach (var issue in ordered) CarConditionIssues.Add(issue);
            }
        }

        /// <summary>
        /// Truncates to <paramref name="maxEntries"/>, replacing the last visible slot with a
        /// "+N more" summary so an overflowing list never silently drops information.
        /// Expects the list to already be sorted most-severe-first. Generic over the entry type so
        /// Car Condition (plain strings) and Penalties (PenaltyEntry, which carries its colour
        /// category) can share it; <paramref name="makeOverflow"/> builds that last summary entry.
        /// </summary>
        private static List<T> CapIssueList<T>(List<T> issues, int maxEntries, Func<int, T> makeOverflow)
        {
            if (issues.Count <= maxEntries) return issues;
            int overflow = issues.Count - (maxEntries - 1);
            var capped = issues.Take(maxEntries - 1).ToList();
            capped.Add(makeOverflow(overflow));
            return capped;
        }

        private static void AddIssueIfDamaged(List<(int Severity, string Text)> issues, byte value, string label)
        {
            if (value > 0) issues.Add((value, $"{label}: {value}%"));
        }

        private void HandleLapData(LapDataPacket packet)
        {
            _playerCarIndex = packet.Header.PlayerCarIndex;
            var span = packet.LapData.AsSpan();

            for (int i = 0; i < span.Length; i++)
            {
                var car = span[i];

                if (!_carTrackers.TryGetValue(i, out var tracker))
                {
                    tracker = new CarLapTracker
                    {
                        LastSeenLapNum = car.CurrentLapNum,
                        LastSeenSector1Ms = car.Sector1TimeInMS,
                        LastSeenSector2Ms = car.Sector2TimeInMS,
                        LastSeenLastLapTimeMs = car.LastLapTimeInMS,
                        LastKnownDriverStatus = car.DriverStatus
                    };
                    _carTrackers[i] = tracker;
                    continue; // first sighting - establish baseline only
                }

                bool isPlayer = i == _playerCarIndex;

                // TEMP pit diagnostic: snapshot previous-tick pit state so a change this tick
                // can be detected and logged at the end of the iteration (see LogPitEvent).
                DriverStatus prevDriverStatus = tracker.LastKnownDriverStatus;
                PitStatus prevPitStatus = tracker.LastKnownPitStatus;
                bool prevPitLaneActive = tracker.LastKnownPitLaneTimerActive;
                string? loggedLapTag = null;

                if (car.Sector1TimeInMS != 0 && car.Sector1TimeInMS != tracker.LastSeenSector1Ms)
                {
                    RegisterSectorTime(1, car.Sector1TimeInMS, isPlayer);
                    tracker.LastSeenSector1Ms = car.Sector1TimeInMS;
                }

                if (car.Sector2TimeInMS != 0 && car.Sector2TimeInMS != tracker.LastSeenSector2Ms)
                {
                    RegisterSectorTime(2, car.Sector2TimeInMS, isPlayer);
                    tracker.LastSeenSector2Ms = car.Sector2TimeInMS;
                }

                // Latch the in-lap for THIS lap the moment it's seen, rather than relying on the
                // status still reading InLap at the line - see CarLapTracker.SawInLapThisLap.
                if (car.DriverStatus == DriverStatus.InLap) tracker.SawInLapThisLap = true;

                // Track the peak PitStopTimerInMS while actually in the pits. On exit,
                // try to patch it straight into an already-existing IN row (see
                // PatchMostRecentInRowPitTime) - only meaningful for the player, since
                // Lap History only ever tracks the player's own laps.
                if (car.PitStatus != PitStatus.None)
                {
                    if (tracker.LastKnownPitStatus == PitStatus.None)
                    {
                        tracker.MaxPitStopTimerMsThisStop = 0; // entering a fresh stop
                    }
                    if (car.PitStopTimerInMS > tracker.MaxPitStopTimerMsThisStop)
                    {
                        tracker.MaxPitStopTimerMsThisStop = car.PitStopTimerInMS;
                    }
                }
                else if (tracker.LastKnownPitStatus != PitStatus.None && isPlayer && tracker.MaxPitStopTimerMsThisStop > 0)
                {
                    PatchMostRecentInRowPitTime(tracker.MaxPitStopTimerMsThisStop);
                    LogPitEvent(car.CurrentLapNum, car.DriverStatus, car.PitStatus, car.PitLaneTimerActive,
                        tracker.MaxPitStopTimerMsThisStop, car.PitLaneTimeInLaneInMS, car.LastLapTimeInMS, "PATCH_STOP_TIME (pit finished)");
                }
                tracker.LastKnownPitStatus = car.PitStatus;

                // Total pit-lane time (entry to exit) - unlike the stop time above, this
                // always finishes before the OUT row it belongs to can possibly exist (the
                // pit lane is exited before the OUT lap even starts), so a plain
                // forward-store into RegisterLapTime is all that's needed - no patching.
                if (car.PitLaneTimerActive)
                {
                    if (!tracker.LastKnownPitLaneTimerActive)
                    {
                        tracker.MaxPitLaneTimeInLaneMsThisStop = 0; // entering a fresh pit lane visit
                    }
                    if (car.PitLaneTimeInLaneInMS > tracker.MaxPitLaneTimeInLaneMsThisStop)
                    {
                        tracker.MaxPitLaneTimeInLaneMsThisStop = car.PitLaneTimeInLaneInMS;
                    }
                }
                else if (tracker.LastKnownPitLaneTimerActive)
                {
                    tracker.PendingPitLaneTimeMs = tracker.MaxPitLaneTimeInLaneMsThisStop;

                    // Record the completed stop for the two humans. Done at pit-LANE exit rather
                    // than when the car leaves its box, because that's the later of the two events
                    // - both the stationary peak and the lane peak are final here, and the
                    // stationary value isn't cleared until the next stop begins. A drive-through
                    // or a lane visit with no service has no stationary time, so it's skipped
                    // rather than recorded as a 0.00s "stop".
                    if (IsTwoPlayerCareer && tracker.MaxPitStopTimerMsThisStop > 0
                        && (i == _playerCarIndex || i == RivalCarIndex))
                    {
                        if (!_h2hStops.TryGetValue(i, out var stopList))
                            _h2hStops[i] = stopList = new List<SavedH2HStop>();
                        stopList.Add(new SavedH2HStop
                        {
                            Lap = car.CurrentLapNum,
                            StationaryMs = tracker.MaxPitStopTimerMsThisStop,
                            LaneMs = tracker.MaxPitLaneTimeInLaneMsThisStop
                        });
                    }

                    if (isPlayer)
                        LogPitEvent(car.CurrentLapNum, car.DriverStatus, car.PitStatus, car.PitLaneTimerActive,
                            car.PitStopTimerInMS, tracker.MaxPitLaneTimeInLaneMsThisStop, car.LastLapTimeInMS, "STORE_LANE_TIME (pit lane exited)");
                }
                tracker.LastKnownPitLaneTimerActive = car.PitLaneTimerActive;

                // A completed lap is normally signalled by CurrentLapNum advancing, but
                // that never happens after the FINAL lap of a race/sprint - there's no
                // next lap for it to advance to. Confirmed via live testing: that left S3
                // permanently blank and the final lap never reached history, because both
                // lived inside this now-unreachable branch. LastLapTimeInMS changing is
                // watched independently so the final lap still registers even with no
                // lap-number change to key off.
                bool lapNumberAdvanced = car.CurrentLapNum != tracker.LastSeenLapNum;
                bool lapJustCompleted = car.LastLapTimeInMS > 0 && car.LastLapTimeInMS != tracker.LastSeenLastLapTimeMs;

                if (lapJustCompleted)
                {
                    uint sector1Ms = tracker.LastSeenSector1Ms;
                    uint sector2Ms = tracker.LastSeenSector2Ms;
                    uint sumFirstTwo = sector1Ms + sector2Ms;
                    uint sector3Ms = car.LastLapTimeInMS > sumFirstTwo ? car.LastLapTimeInMS - sumFirstTwo : 0;

                    if (sector3Ms > 0 && sector3Ms <= ushort.MaxValue)
                    {
                        RegisterSectorTime(3, sector3Ms, isPlayer);
                    }

                    string lapTag = isPlayer ? ClassifyLapTag(tracker.LastKnownDriverStatus, tracker.SawInLapThisLap) : "";
                    tracker.SawInLapThisLap = false; // consumed - the next lap starts clean

                    // The lap that resumes racing after a red flag is an out-lap in the game's
                    // eyes, but not a pit stop - so it gets a green "Restart" chip in EVENTS
                    // (where race-control happenings already live, alongside the "Red Flag" chip
                    // on the lap above it) and the misleading PIT tag is dropped.
                    bool isRedFlagRestart = isPlayer && _awaitingRedFlagRestart && lapTag == "OUT";
                    if (isRedFlagRestart)
                    {
                        _awaitingRedFlagRestart = false;
                        lapTag = "";
                        AddPendingLapEvent(new LapEvent(LapEventKind.Restart, "Restart"));
                    }
                    loggedLapTag = isRedFlagRestart ? "RESTART" : lapTag; // TEMP pit diagnostic
                    // LastSeenLapNum still holds the lap that was in progress, which is the
                    // one that just finished - read it BEFORE the lapNumberAdvanced block
                    // below rolls it forward. This is correct in both cases without a
                    // special case: mid-race CurrentLapNum has already advanced past the
                    // completed lap, while on the final lap it never advances at all (see
                    // the lapJustCompleted comment above) and LastSeenLapNum is already the
                    // right number.
                    int completedLapNum = Math.Max(1, (int)tracker.LastSeenLapNum);
                    RegisterLapTime(car.LastLapTimeInMS, sector1Ms, sector2Ms, sector3Ms, isPlayer, lapTag, tracker.PendingPitLaneTimeMs, completedLapNum);
                    tracker.PendingPitLaneTimeMs = 0; // consumed - don't let it leak onto a later unrelated OUT lap

                    // Per-car best lap is no longer accumulated here from LastLapTimeInMS - it now
                    // comes straight from the game's SessionHistory packet (HandleSessionHistory),
                    // which is authoritative and, unlike this, excludes invalidated laps.

                    tracker.LastSeenLastLapTimeMs = car.LastLapTimeInMS;
                }

                // Separate from lapJustCompleted: only meaningful when there's actually a
                // next lap to prep tracking for - skipped on the final lap, which is fine
                // since there's nothing left to display sectors for afterward.
                if (lapNumberAdvanced)
                {
                    tracker.LastSeenLapNum = car.CurrentLapNum;
                    tracker.LastSeenSector1Ms = 0;
                    tracker.LastSeenSector2Ms = 0;

                    if (isPlayer)
                    {
                        ResetSectorDisplayForNewLap();
                    }
                }

                // TEMP pit diagnostic: log a line whenever the player's pit-relevant state
                // changed this tick, or a lap just completed (so every lap's IN/OUT tag is
                // recorded). Green-flag laps (no state change) only log once, at completion.
                if (isPlayer)
                {
                    bool stateChanged = car.DriverStatus != prevDriverStatus
                        || car.PitStatus != prevPitStatus || car.PitLaneTimerActive != prevPitLaneActive;
                    if (stateChanged || loggedLapTag != null)
                    {
                        string note = loggedLapTag != null
                            ? $"LAP_COMPLETE tag={(string.IsNullOrEmpty(loggedLapTag) ? "-" : loggedLapTag)}"
                            : "state-change";
                        LogPitEvent(car.CurrentLapNum, car.DriverStatus, car.PitStatus, car.PitLaneTimerActive,
                            car.PitStopTimerInMS, car.PitLaneTimeInLaneInMS, car.LastLapTimeInMS, note);
                    }
                }

                tracker.LastKnownDriverStatus = car.DriverStatus;
            }

            if (_playerCarIndex >= 0 && _playerCarIndex < span.Length)
            {
                var playerLap = span[_playerCarIndex];
                CurrentLapTimeText = FormatTime(playerLap.CurrentLapTimeInMS);
                CurrentLapColorBrush = TimingColorPalette.TextBrush(_lastCompletedSectorColorThisLap);
                IsCurrentLapInvalid = playerLap.IsCurrentLapInvalid;

                RefreshPenalties(playerLap);

                // Keep the current lap fresh (used for stint start-lap maths) and grow the
                // last stint segment once per lap as the race progresses.
                if (playerLap.CurrentLapNum != _playerCurrentLap)
                {
                    _playerCurrentLap = playerLap.CurrentLapNum;
                    if (_liveStints.Count > 0) RebuildTyreStints();
                }
            }

            // Only rebuild the leaderboard that's actually on screen for this preset - the tower in
            // Race, the timing board in Practice/Qualifying (they're mutually exclusive, see
            // MainWindow.UpdateWidgetVisibility). Building the hidden one every tick was a full
            // 22-car list + sort + allocations thrown straight away. Both draw on _carBestLapMs,
            // which is maintained in the loop above regardless, so gating here changes nothing but
            // the wasted work. Neither runs for Unsupported (Time Trial / menus): no field to rank.
            if (CurrentPreset == PresetType.Race)
                RefreshRaceStandings(span);
            else if (CurrentPreset is PresetType.Practice or PresetType.Qualifying)
                RefreshPositionList(span);
        }

        /// <summary>
        /// Full-field race tower (position, interval, gap to leader) - unlike the
        /// qualifying PositionList, this needs no cross-referencing: every car already
        /// carries its own DeltaToCarInFrontInMS/DeltaToRaceLeaderInMS directly.
        /// </summary>
        private void RefreshRaceStandings(Span<LapData> span)
        {
            // Whoever currently holds the session's fastest lap - _carBestLapMs is
            // already maintained per car (also used by RefreshPositionList's qualifying
            // gap-to-leader calculation), so no new tracking is needed here.
            // Resolved once per refresh, not per car: this method runs on every LapData tick
            // (20-60Hz) and RivalCarIndex walks a set, so evaluating it inside the row loop would
            // repeat that work ~22 times a tick for a value that can't change mid-loop. -1 in
            // every mode except a two-player career, where no row matches and nothing highlights.
            int rivalCarIndex = RivalCarIndex;

            int fastestLapCarIndex = -1;
            uint? fastestLapMs = null;
            foreach (var kvp in _carBestLapMs)
            {
                if (kvp.Value.HasValue && (fastestLapMs == null || kvp.Value.Value < fastestLapMs.Value))
                {
                    fastestLapMs = kvp.Value.Value;
                    fastestLapCarIndex = kvp.Key;
                }
            }

            if (fastestLapCarIndex >= 0 && fastestLapMs.HasValue)
            {
                GetParticipantInfo(fastestLapCarIndex, out var flName, out _, out _);
                FastestLapDriver = flName ?? $"Car {fastestLapCarIndex + 1}";
                FastestLapTimeText = FormatTime(fastestLapMs.Value);
                HasRaceFastestLap = true;
            }
            else
            {
                HasRaceFastestLap = false;
            }

            var rows = new List<RaceStanding>();
            byte leaderCurrentLap = 0;
            for (int i = 0; i < span.Length; i++)
            {
                var car = span[i];
                if (car.CarPosition == 0) continue; // not an active car this session

                GetParticipantInfo(i, out var name, out var livery, out var team);

                bool isLeader = car.CarPosition <= 1;
                if (isLeader) leaderCurrentLap = car.CurrentLapNum;
                long intervalMs = car.DeltaToCarInFrontInMinutes * 60000L + car.DeltaToCarInFrontInMS;
                long gapMs = car.DeltaToRaceLeaderInMinutes * 60000L + car.DeltaToRaceLeaderInMS;

                // A retired/DNF/DSQ/not-classified car's deltas go stale at 0 rather than
                // holding a meaningful last-known gap, which reads as "tied with the
                // leader" - matches the real broadcast's "Out" treatment instead.
                bool isOut = car.ResultStatus is ResultStatus.Retired or ResultStatus.DidNotFinish
                    or ResultStatus.Disqualified or ResultStatus.NotClassified;

                // In the pit lane (entry to exit) - the interval/gap is unreliable while
                // stopped/crawling, so show "PIT" in its place, reverting automatically the
                // moment PitStatus returns to None (back on track). IsOut takes priority.
                bool isPitting = !isOut && car.PitStatus != PitStatus.None;

                // Blank for an out car, matching the real broadcast graphic (no compound
                // shown once a driver has retired).
                string tyreLetter = "";
                SolidColorBrush tyreBrush = CompoundPalette.Unknown;
                if (!isOut && _carTyreCompounds.TryGetValue(i, out var compound))
                {
                    tyreLetter = CompoundPalette.LetterFor(compound);
                    tyreBrush = CompoundPalette.BrushFor(compound);
                }
                // Age rides with the letter (blank for an out car, same as the letter).
                string tyreAge = !isOut && _carTyreAge.TryGetValue(i, out int age) ? age.ToString() : "";

                // Places gained/lost since the start. GridPosition is 0 when the game hasn't set
                // one, so treat that as "unknown" and render nothing rather than a bogus "▲0" -
                // the field is confirmed to exist but NOT yet confirmed to be populated sensibly
                // mid-race (see HANDOFF §8), and this degrades to blank if it isn't.
                string posDeltaText = "";
                SolidColorBrush posDeltaBrush = TimingColorPalette.NeutralText;
                if (!isOut && car.GridPosition > 0 && car.CarPosition > 0)
                {
                    int gained = car.GridPosition - car.CarPosition; // positive = moved up the order
                    if (gained > 0) { posDeltaText = $"▲{gained}"; posDeltaBrush = TimingColorPalette.GapClosing; }
                    else if (gained < 0) { posDeltaText = $"▼{-gained}"; posDeltaBrush = TimingColorPalette.GapOpening; }
                    else { posDeltaText = "–"; posDeltaBrush = TimingColorPalette.MutedText; }
                }

                // Same fields already used for the player's own Penalties & Flags list
                // (RefreshPenalties), just generalized to every car here instead of just
                // the player. False for an out car - nothing left to serve.
                bool hasPendingPenalty = !isOut && (car.Penalties > 0
                    || car.NumUnservedDriveThroughPens > 0 || car.NumUnservedStopGoPens > 0);

                // Shares its badge slot with the pending-penalty badge above - a pending
                // penalty is actionable and wins the same way the alert banner already
                // prioritizes penalty/warning states over purely informational ones.
                bool isFastestLap = !isOut && !hasPendingPenalty && i == fastestLapCarIndex;

                rows.Add(new RaceStanding
                {
                    Position = car.CarPosition,
                    DriverName = name ?? $"Car {i + 1}",
                    TeamName = team ?? "",
                    LiveryBrush = livery ?? TimingColorPalette.NeutralText,
                    IsPlayer = i == _playerCarIndex,
                    IsRival = i == rivalCarIndex,
                    IsOut = isOut,
                    IsPitting = isPitting,
                    TyreLetter = tyreLetter,
                    TyreBrush = tyreBrush,
                    TyreAgeText = tyreAge,
                    PositionDeltaText = posDeltaText,
                    PositionDeltaBrush = posDeltaBrush,
                    IsPenaltyPending = hasPendingPenalty,
                    IsFastestLap = isFastestLap,
                    // 2 decimals here specifically (not the app-wide 3) - frees up column
                    // width in the tower to run the font size up, and 2 decimals is still
                    // plenty precise for a live gap glanced at mid-race.
                    // "Out"/"PIT" only shown once (Gap column) - showing a status word in
                    // both Int and Gap read as "Out Out" side by side; the real broadcast
                    // shows a single label. IsOut wins over IsPitting (a retired car sitting
                    // in the pit lane is Out, not pitting for a stop).
                    IntervalText = isOut || isPitting ? "" : isLeader ? "-" : FormatDelta(intervalMs, 2),
                    GapText = isOut ? "Out" : isPitting ? "PIT" : isLeader ? "Leader" : FormatDelta(gapMs, 2)
                });
            }

            rows.Sort((a, b) => a.Position.CompareTo(b.Position));

            if (!CollectionUnchanged(RaceStandings, rows))
            {
                RaceStandings.Clear();
                foreach (var row in rows) RaceStandings.Add(row);
            }

            LapCounterText = _totalLaps > 0 && leaderCurrentLap > 0 ? $"LAP {leaderCurrentLap} / {_totalLaps}" : "-";
        }

        private void GetParticipantInfo(int carIndex, out string? name, out SolidColorBrush? livery, out string? team)
        {
            _participantNames.TryGetValue(carIndex, out name);
            _participantLivery.TryGetValue(carIndex, out livery);
            _participantTeams.TryGetValue(carIndex, out team);
        }

        /// <summary>
        /// The gate for every head-to-head feature. BOTH signals are required, deliberately: the
        /// game mode alone would let a mislabelled session switch this on (F1 25 has form here -
        /// it reports a sprint as SessionType.Race), and a human count alone would fire in a
        /// multiplayer lobby, which must keep the ordinary "highlight my own car only" behaviour.
        /// </summary>
        private bool IsTwoPlayerCareer => CareerNames.IsTwoPlayer(_gameMode) && _humanCarIndices.Count == 2;

        /// <summary>
        /// The other human's car index in a two-player career, or -1 when there isn't exactly one.
        /// Returns -1 outside a two-player career so callers can't accidentally treat a random AI
        /// (or a lobby full of humans) as "the rival".
        /// </summary>
        private int RivalCarIndex
        {
            get
            {
                if (!IsTwoPlayerCareer || _playerCarIndex < 0) return -1;
                foreach (int idx in _humanCarIndices)
                    if (idx != _playerCarIndex) return idx;
                return -1;
            }
        }

        /// <summary>
        /// Shared by RaceStandings and PositionList - both rebuild a full List every
        /// LapData tick (far more often than the data actually changes) and only want to
        /// touch the bound ObservableCollection when something real changed, since
        /// Clear()+Add() forces the bound ItemsControl to tear down and rebuild every
        /// row's visual tree (a Reset notification, not a per-item update).
        /// </summary>
        private static bool CollectionUnchanged<T>(ObservableCollection<T> current, List<T> next) where T : IEquatable<T>
        {
            if (current.Count != next.Count) return false;
            for (int i = 0; i < next.Count; i++)
            {
                if (!current[i].Equals(next[i])) return false;
            }
            return true;
        }

        private void RefreshPenalties(LapData playerLap)
        {
            // Severity-ranked, most urgent first: unserved pens are mandatory and still
            // owed (stop-go costs more time than drive-through, so it ranks above it),
            // ahead of already-applied time penalties, ahead of individual warnings,
            // ahead of the generic untracked-warnings overflow line.
            //
            // Wording note: the chip's COLOUR says which category it is (red = penalty, amber =
            // warning - see PenaltyEntry), so the text no longer repeats it. "Warning - Track
            // limits" is now just "Track limits"; a time penalty is just "+5s". What's kept is the
            // part that actually informs - the infringement, the magnitude, and "unserved" (still
            // owed is the actionable bit, not a restatement of the category).
            var issues = new List<(int Severity, PenaltyEntry Entry)>();
            if (playerLap.NumUnservedStopGoPens > 0)
                issues.Add((100, new PenaltyEntry { Text = $"Stop-go unserved ({playerLap.NumUnservedStopGoPens})", IsPenalty = true }));
            if (playerLap.NumUnservedDriveThroughPens > 0)
                issues.Add((90, new PenaltyEntry { Text = $"Drive-through unserved ({playerLap.NumUnservedDriveThroughPens})", IsPenalty = true }));
            if (playerLap.Penalties > 0)
                issues.Add((80, new PenaltyEntry { Text = $"+{playerLap.Penalties}s", IsPenalty = true }));

            // One line per specific warning *kind* (from PenaltyIssued events), not just a
            // bare count - and duplicates of the same kind collapse into a single line with
            // an "(xN)" multiplier (e.g. "Track limits (x3)") rather than
            // repeating the identical row and eating list slots. GroupBy preserves
            // first-occurrence order. Falls back to a generic "+N more" for whatever the
            // event-based list hasn't accounted for (e.g. warnings from before the app
            // connected this session), so the total always still matches LapData's own count.
            foreach (var group in _warningReasons.GroupBy(r => r))
            {
                int count = group.Count();
                issues.Add((50, new PenaltyEntry { Text = count > 1 ? $"{group.Key} (x{count})" : group.Key, IsPenalty = false }));
            }
            int untrackedWarnings = playerLap.TotalWarnings - _warningReasons.Count;
            if (untrackedWarnings > 0)
                issues.Add((40, new PenaltyEntry { Text = $"+{untrackedWarnings} more", IsPenalty = false }));

            PenaltiesIsOk = issues.Count == 0;
            // The flag banner, when shown, sits above this list and eats one grid row (2 entries'
            // worth), so the list's share of the shared budget shrinks to match - that's what keeps
            // this card level with Car Condition instead of a row taller. Read live rather than
            // cached: this runs every LapData tick, so it tracks the flag appearing/clearing on its
            // own. The list is already severity-sorted, so the entries squeezed out are the mildest.
            int entryBudget = PlayerFlagVisible ? IssueListMaxEntries - FlagBannerEntryCost : IssueListMaxEntries;
            var ordered = CapIssueList(
                issues.OrderByDescending(i => i.Severity).Select(i => i.Entry).ToList(),
                entryBudget,
                n => new PenaltyEntry { Text = $"+{n} more", IsPenalty = false });
            if (!CollectionUnchanged(PenaltiesIssues, ordered))
            {
                PenaltiesIssues.Clear();
                foreach (var issue in ordered) PenaltiesIssues.Add(issue);
            }
        }

        /// <summary>
        /// Records one car's session-best lap from the game's SessionHistory packet (sent per car,
        /// round-robin). <see cref="SessionHistoryDataPacket.BestLapTimeLapNum"/> is the game's own
        /// pick of that car's best lap - a 1-based index into its lap history (0 = no lap set yet) -
        /// so it excludes invalidated laps and needs no accumulation on our side. This is the sole
        /// feed for <see cref="_carBestLapMs"/>; see that field. Cleared per session in
        /// ResetSessionScopedState, then repopulated as each car's history packet arrives.
        /// </summary>
        private void HandleSessionHistory(SessionHistoryDataPacket packet)
        {
            // Snapshot the two humans' full lap-by-lap for the saved head-to-head. Done BEFORE
            // the best-lap early-return below, which bails when no lap has been set yet - that's
            // fine for a best lap but would skip this. The game re-sends this packet for every
            // car continuously, so each snapshot simply overwrites the last and what's held at
            // the flag is the complete race.
            if (IsTwoPlayerCareer && (packet.CarIndex == _playerCarIndex || packet.CarIndex == RivalCarIndex))
                _h2hLapHistory[packet.CarIndex] = SnapshotHistoryLaps(packet);

            byte bestLapNum = packet.BestLapTimeLapNum;
            if (bestLapNum == 0) return; // no lap set yet this session

            var laps = packet.LapHistoryData.AsSpan();
            int idx = bestLapNum - 1;
            if (idx < 0 || idx >= laps.Length) return;

            uint bestMs = laps[idx].LapTimeInMS;
            if (bestMs > 0) _carBestLapMs[packet.CarIndex] = bestMs;
        }

        /// <summary>
        /// Copies one car's lap history out of the packet into plain storage. Only laps with a
        /// real time are kept - the array is fixed-length (100) and mostly empty early on, and a
        /// zero-time entry means "not run yet", not "a zero-second lap".
        /// </summary>
        private static List<SavedH2HLap> SnapshotHistoryLaps(SessionHistoryDataPacket packet)
        {
            var span = packet.LapHistoryData.AsSpan();
            int count = Math.Min(packet.NumLaps, span.Length);
            var laps = new List<SavedH2HLap>(count);

            for (int i = 0; i < count; i++)
            {
                var l = span[i];
                if (l.LapTimeInMS == 0) continue;
                laps.Add(new SavedH2HLap
                {
                    LapNumber = i + 1,
                    LapTimeMs = l.LapTimeInMS,
                    // Sector fields are split into a minutes byte plus a ms ushort, so a sector
                    // over a minute (rain, a safety-car lap) would otherwise wrap and read as a
                    // wildly fast sector. Recombined here rather than at every point of use.
                    S1Ms = (uint)(l.Sector1TimeMinutes * 60000) + l.Sector1TimeInMS,
                    S2Ms = (uint)(l.Sector2TimeMinutes * 60000) + l.Sector2TimeInMS,
                    S3Ms = (uint)(l.Sector3TimeMinutes * 60000) + l.Sector3TimeInMS,
                    IsValid = l.LapValidBitFlags.HasFlag(LapValid.LapValid)
                });
            }
            return laps;
        }

        private void RefreshPositionList(Span<LapData> span)
        {
            uint? leaderBestMs = _carBestLapMs.Values.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty().Min();
            if (leaderBestMs == 0) leaderBestMs = null;

            var built = new List<(CarStanding Row, uint SortKey, byte Tiebreak)>();
            for (int i = 0; i < span.Length; i++)
            {
                var car = span[i];
                if (car.CarPosition == 0) continue; // not an active car this session

                _carBestLapMs.TryGetValue(i, out var bestMs);
                GetParticipantInfo(i, out var name, out var livery, out var team);

                string bestText = bestMs.HasValue ? FormatTime(bestMs.Value) : "-";
                string gapText = "-";
                if (bestMs.HasValue && leaderBestMs.HasValue)
                {
                    gapText = bestMs.Value == leaderBestMs.Value ? "-" : FormatDelta((long)bestMs.Value - (long)leaderBestMs.Value);
                }

                built.Add((new CarStanding
                {
                    DriverName = name ?? $"Car {i + 1}",
                    TeamName = team ?? "",
                    LiveryBrush = livery ?? TimingColorPalette.NeutralText,
                    BestLapText = bestText,
                    GapText = gapText,
                    IsPlayer = i == _playerCarIndex
                },
                bestMs ?? uint.MaxValue, // cars without a lap yet sink to the bottom
                car.CarPosition));       // stable tiebreak among no-lap cars
            }

            // Sort by best lap (fastest first) and number by that rank. This is a TIMING
            // board, but the game's CarPosition is NOT reliably best-lap order in a
            // practice session (it can reflect track position), which showed drivers out of
            // timing order even though their best-lap times/gaps were correct. Cars with no
            // lap yet fall to the bottom in the game's own position order.
            var sorted = built.OrderBy(x => x.SortKey).ThenBy(x => x.Tiebreak).ToList();
            var rows = new List<CarStanding>(sorted.Count);
            for (int r = 0; r < sorted.Count; r++)
            {
                sorted[r].Row.Position = r + 1;
                rows.Add(sorted[r].Row);
            }

            if (!CollectionUnchanged(PositionList, rows))
            {
                PositionList.Clear();
                foreach (var row in rows) PositionList.Add(row);
            }
        }

        /// <summary>
        /// Captures a completed race from the Final Classification packet - the full-field
        /// result plus a snapshot of the player's own lap-by-lap - and persists it via
        /// RaceHistoryStore. Races/sprints only; deduped per session so the game re-sending
        /// the packet doesn't re-save. Never confirmed live yet (no real race finish has been
        /// captured through it) - the field reads are all against the documented API.
        /// </summary>
        private void HandleFinalClassification(FinalClassificationDataPacket packet)
        {
            if (CurrentPreset != PresetType.Race) return; // only sessions with a finishing order

            ulong uid = packet.Header.SessionUID;
            if (_savedClassificationUid == uid) return; // already captured this session

            int playerIdx = packet.Header.PlayerCarIndex;
            var span = packet.ClassificationData.AsSpan();
            if (playerIdx < 0 || playerIdx >= span.Length) return;
            int numCars = Math.Min(packet.NumCars, span.Length);

            // Fastest lap of the race = the smallest best-lap across classified cars.
            uint fastestMs = uint.MaxValue;
            int fastestIdx = -1;
            for (int i = 0; i < numCars; i++)
            {
                var c = span[i];
                if (c.Position == 0) continue;
                if (c.BestLapTimeInMS > 0 && c.BestLapTimeInMS < fastestMs) { fastestMs = c.BestLapTimeInMS; fastestIdx = i; }
            }

            var player = span[playerIdx];
            bool playerOut = IsOutStatus(player.ResultStatus);

            var race = new SavedRace
            {
                SessionUid = uid,
                SavedAtUtc = DateTime.UtcNow,
                GrandPrix = TrackNames.GrandPrixFor(_currentTrack),
                Circuit = TrackNames.CircuitFor(_currentTrack),
                Country = TrackNames.CountryCodeFor(_currentTrack),
                // Always "Race" at capture: the session TYPE cannot tell a sprint from a feature
                // race in F1 25, which reports the sprint as SessionType.Race and the feature race
                // as Race2 - the inverse of the obvious reading. Mapping Race2 to "Sprint" (as this
                // did) labelled a real 20-lap Chinese GP feature race, logged as type=Race2, a
                // sprint. The reliable signal is LAP COUNT and HistoryGroups already applies it:
                // a sprint weekend saves two race-type sessions under one WeekendLinkId and the
                // longer is the feature race, with ApplyWeekendRole stamping the right word on
                // each. So a full weekend is always labelled correctly, and a lone saved session
                // reads "Race" - right in the overwhelming majority, and never confidently wrong
                // the way guessing from the type was.
                SessionLabel = "Race",
                SessionTypeName = _lastSeenSessionType?.ToString() ?? "",
                TotalLaps = _totalLaps > 0 ? _totalLaps : player.NumLaps,
                SeasonLinkId = _seasonLinkId,
                WeekendLinkId = _weekendLinkId,
                SessionLinkId = _sessionLinkId,
                GameMode = _gameMode.ToString(),
                GridPosition = player.GridPosition,
                FinishPosition = player.Position,
                Points = player.Points,
                PitStops = player.NumPitStops,
                ResultStatus = ResultStatusLabelFor(player.ResultStatus),
                ResultReason = playerOut ? Humanize(player.ResultReason.ToString()) : "",
                RetiredOnLap = playerOut ? player.NumLaps : null,
                BestLapMs = player.BestLapTimeInMS,
                TotalRaceTimeSeconds = player.TotalRaceTime,
                PenaltiesTimeSeconds = player.PenaltiesTime,
                PlayerHasFastestLap = fastestIdx == playerIdx,
                PlayerStints = BuildStints(player),
                Penalties = BuildIncurredPenalties(),
                Classification = BuildClassification(span, numCars, playerIdx, fastestIdx),
                PlayerLaps = SnapshotPlayerLaps(),
                HeadToHead = BuildHeadToHead(span, numCars, playerIdx, fastestIdx)
            };

            _savedClassificationUid = uid;
            if (_historyStore.Save(race))
                RaceSaved?.Invoke(race);
        }

        private static bool IsOutStatus(ResultStatus status) => status is ResultStatus.Retired
            or ResultStatus.DidNotFinish or ResultStatus.Disqualified or ResultStatus.NotClassified;


        private static string ResultStatusLabelFor(ResultStatus status) => status switch
        {
            ResultStatus.Finished => "Finished",
            ResultStatus.Disqualified => "DSQ",
            ResultStatus.NotClassified => "Not classified",
            ResultStatus.Retired or ResultStatus.DidNotFinish => "DNF",
            _ => "Finished"
        };

        // "TerminalDamage" -> "Terminal damage" - a light humaniser for the retirement reason.
        private static string Humanize(string pascal)
        {
            if (string.IsNullOrEmpty(pascal)) return "";
            string result = pascal[0].ToString();
            for (int i = 1; i < pascal.Length; i++)
            {
                char ch = pascal[i];
                result += char.IsUpper(ch) ? " " + char.ToLower(ch) : ch.ToString();
            }
            return result;
        }

        /// <summary>
        /// Assembles the two-player head-to-head, or null if this race isn't one. Returns null
        /// rather than an empty object so the history panel's "does this race have a H2H?" check
        /// is a plain null test with no half-populated middle state to reason about.
        /// Per-driver facts come from Final Classification (which is per car, so the rival's grid
        /// slot, points, stops and stints need no extra tracking); the lap-by-lap comes from the
        /// SessionHistory snapshots accumulated during the race.
        /// </summary>
        private SavedHeadToHead? BuildHeadToHead(Span<FinalClassificationData> span, int numCars, int playerIdx, int fastestIdx)
        {
            int rivalIdx = RivalCarIndex;
            if (rivalIdx < 0 || playerIdx < 0) return null;
            if (playerIdx >= numCars || rivalIdx >= numCars) return null;

            // Both sides must have real lap data, otherwise the comparison would render as a
            // half-empty page. Better to save no head-to-head than a misleading one.
            if (!_h2hLapHistory.TryGetValue(playerIdx, out var playerLaps) || playerLaps.Count == 0) return null;
            if (!_h2hLapHistory.TryGetValue(rivalIdx, out var rivalLaps) || rivalLaps.Count == 0) return null;

            return new SavedHeadToHead
            {
                You = BuildH2HDriver(span[playerIdx], playerIdx, playerLaps, fastestIdx),
                Rival = BuildH2HDriver(span[rivalIdx], rivalIdx, rivalLaps, fastestIdx)
            };
        }

        private SavedH2HDriver BuildH2HDriver(FinalClassificationData c, int carIndex, List<SavedH2HLap> laps, int fastestIdx)
        {
            GetParticipantInfo(carIndex, out var name, out var livery, out var team);
            return new SavedH2HDriver
            {
                Name = name ?? $"Car {carIndex + 1}",
                Team = team ?? "",
                LiveryHex = ToHex(livery),
                GridPosition = c.GridPosition,
                FinishPosition = c.Position,
                Points = c.Points,
                PitStops = c.NumPitStops,
                PenaltiesTimeSeconds = c.PenaltiesTime,
                IsOut = IsOutStatus(c.ResultStatus),
                HasFastestLap = carIndex == fastestIdx,
                TotalRaceTimeSeconds = c.TotalRaceTime,
                NumLaps = c.NumLaps,
                Laps = laps,
                Stints = BuildStints(c),
                Stops = _h2hStops.TryGetValue(carIndex, out var stops) ? stops : new List<SavedH2HStop>()
            };
        }

        private static List<SavedStint> BuildStints(FinalClassificationData c)
        {
            var stints = new List<SavedStint>();
            var visual = c.TyreStintsVisual.AsSpan();
            var endLaps = c.TyreStintsEndLaps.AsSpan();
            int n = Math.Min(c.NumTyreStints, Math.Min(visual.Length, endLaps.Length));
            for (int i = 0; i < n; i++)
            {
                // The game reports the final stint's end lap as 255 (a "no end yet" sentinel);
                // clamp to the car's finished lap count so the stint ends at the flag, not lap 255.
                int end = c.NumLaps > 0 ? Math.Min(endLaps[i], c.NumLaps) : endLaps[i];
                stints.Add(new SavedStint { Compound = CompoundPalette.LetterFor(visual[i]), EndLap = end });
            }
            return stints;
        }

        private List<SavedClassificationRow> BuildClassification(Span<FinalClassificationData> span, int numCars, int playerIdx, int fastestIdx)
        {
            var rows = new List<SavedClassificationRow>();
            for (int i = 0; i < numCars; i++)
            {
                var c = span[i];
                if (c.Position == 0) continue;
                GetParticipantInfo(i, out var name, out var livery, out var team);
                rows.Add(new SavedClassificationRow
                {
                    Position = c.Position,
                    DriverName = name ?? $"Car {i + 1}",
                    TeamName = team ?? "",
                    LiveryHex = ToHex(livery),
                    BestLapMs = c.BestLapTimeInMS,
                    PitStops = c.NumPitStops,
                    IsPlayer = i == playerIdx,
                    IsOut = IsOutStatus(c.ResultStatus),
                    HasFastestLap = i == fastestIdx,
                    Stints = BuildStints(c),
                    TotalRaceTimeSeconds = c.TotalRaceTime,
                    NumLaps = c.NumLaps
                });
            }
            rows.Sort((a, b) => a.Position.CompareTo(b.Position));
            return rows;
        }

        private List<SavedLapRow> SnapshotPlayerLaps()
        {
            // LapHistory is newest-first; save oldest-first so a saved race reads top-to-bottom.
            var laps = new List<SavedLapRow>();
            for (int i = LapHistory.Count - 1; i >= 0; i--)
            {
                var e = LapHistory[i];
                laps.Add(new SavedLapRow
                {
                    LapNumber = ParseLapNumber(e.LapNumberText),
                    LapTimeText = e.LapTimeText,
                    LapColorHex = ToHex(e.ColorBrush),
                    DeltaText = e.DeltaText,
                    Tag = e.LapTagText,
                    PitTimeText = e.PitStopTimeText,
                    S1Text = e.Sector1Text, S1Hex = ToHex(e.Sector1Brush),
                    S2Text = e.Sector2Text, S2Hex = ToHex(e.Sector2Brush),
                    S3Text = e.Sector3Text, S3Hex = ToHex(e.Sector3Brush),
                    Events = e.Events.Select(ev => new SavedLapEvent { Kind = ev.Kind.ToString(), Text = ev.Text }).ToList()
                });
            }
            return laps;
        }

        /// <summary>
        /// Fills in placeholder rows for lap numbers the game skipped. The lap counter can jump -
        /// a red-flagged Chinese GP went straight from 9 to 11 - and those laps have no time
        /// because they were never run as timed laps. Showing them as numbered blanks keeps the
        /// list continuous, so a gap reads as "the game skipped this" rather than as the app
        /// having lost a lap. Nothing is invented: no time, no sectors, no events.
        /// </summary>
        private void InsertSkippedLapRows(int completedLapNum)
        {
            if (_lastHistoryLapNum <= 0) return;                 // first lap of the session
            if (completedLapNum <= _lastHistoryLapNum + 1) return; // no gap
            // Guard against a nonsense jump (a corrupt packet) filling the list with hundreds of
            // blanks - beyond a handful this isn't a skipped lap, it's bad data.
            if (completedLapNum - _lastHistoryLapNum > 10) return;

            for (int lap = _lastHistoryLapNum + 1; lap < completedLapNum; lap++)
                LapHistory.Insert(0, new LapHistoryEntry
                {
                    LapNumberText = $"Lap {lap}",
                    LapTimeText = "—",
                    DeltaText = "",
                    ColorBrush = TimingColorPalette.NeutralText
                });
        }

        private static int ParseLapNumber(string lapText)
        {
            var digits = new string(lapText.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out int n) ? n : 0;
        }

        private static string ToHex(SolidColorBrush? brush)
        {
            if (brush == null) return "#6B7684";
            var c = brush.Color;
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        private void RegisterSectorTime(int sectorNumber, uint ms, bool isPlayer)
        {
            int idx = sectorNumber - 1;
            bool newSessionBest = _sessionBestSectorMs[idx] == null || ms < _sessionBestSectorMs[idx];
            if (newSessionBest) _sessionBestSectorMs[idx] = ms;

            if (!isPlayer) return;

            bool newPersonalBest = _personalBestSectorMs[idx] == null || ms < _personalBestSectorMs[idx];
            TimingColor color = newSessionBest ? TimingColor.Purple
                : newPersonalBest ? TimingColor.Green
                : TimingColor.Yellow;

            if (newPersonalBest) _personalBestSectorMs[idx] = ms;

            SetSectorDisplay(idx, ms, color);
            _lastCompletedSectorColorThisLap = color;

            // Live delta update: compare cumulative time so far this lap against the
            // same cumulative point in the player's actual best lap. Updates at each
            // sector boundary rather than only at the end of the lap.
            _cumulativeThisLapMs += ms;
            UpdateLiveDeltaToPersonalBest(idx);
        }

        private void UpdateLiveDeltaToPersonalBest(int completedSectorIdx)
        {
            if (_personalBestLapCumulativeSplitsMs == null)
            {
                DeltaToPbText = "-";
                DeltaToPbColorBrush = TimingColorPalette.NeutralText;
                return;
            }

            uint referenceMs = _personalBestLapCumulativeSplitsMs[completedSectorIdx];
            long deltaMs = (long)_cumulativeThisLapMs - referenceMs;
            DeltaToPbText = FormatDelta(deltaMs);
            DeltaToPbColorBrush = deltaMs <= 0 ? TimingColorPalette.GreenText : TimingColorPalette.YellowText;
        }

        /// <summary>
        /// Tags the lap that just ended as the pit IN lap, the OUT lap, or neither.
        /// <paramref name="sawInLap"/> wins over <paramref name="status"/>: a lap the car spent any
        /// part of driving to the pits IS the in-lap, even if it left the box before the line. That
        /// case (pit exit before start/finish, so the whole stop fits in one lap) is exactly what
        /// made both laps read OutLap and produced the double-OUT bug - see CarLapTracker.
        /// <paramref name="status"/> is the tracker's PREVIOUS-tick value, so the out-lap is still
        /// attributed to the lap that just ended rather than the one starting.
        /// </summary>
        private static string ClassifyLapTag(DriverStatus status, bool sawInLap)
        {
            if (sawInLap) return "IN";
            return status == DriverStatus.OutLap ? "OUT" : "";
        }

        private void RegisterLapTime(uint ms, uint sector1Ms, uint sector2Ms, uint sector3Ms, bool isPlayer, string lapTag, uint pitLaneTimeMs, int completedLapNum)
        {
            // A lap the car never actually ran: no sector was ever timed, yet the game still
            // reports a "lap time" because its clock ran through a stoppage. A red-flagged lap
            // arrives as 4:08.315 with three blank sectors - that number is the length of the
            // interruption, not a lap, and letting it through would poison the session best, the
            // personal best and every delta after it. Excluded from all of those and shown with no
            // time at all, which is what actually happened.
            bool abandoned = sector1Ms == 0 && sector2Ms == 0;

            bool newSessionBest = !abandoned && (_sessionBestLapMs == null || ms < _sessionBestLapMs);
            if (newSessionBest) _sessionBestLapMs = ms;

            if (!isPlayer) return;

            if (abandoned)
            {
                InsertSkippedLapRows(completedLapNum);
                LapHistory.Insert(0, new LapHistoryEntry
                {
                    LapNumberText = $"Lap {completedLapNum}",
                    LapTimeText = "—",
                    DeltaText = "",
                    ColorBrush = TimingColorPalette.NeutralText,
                    Events = BuildLapEvents()   // the Red Flag chip still belongs on this row
                });
                _lastHistoryLapNum = completedLapNum;
                return;
            }

            bool newPersonalBest = _personalBestLapMs == null || ms < _personalBestLapMs;
            TimingColor color = newSessionBest ? TimingColor.Purple
                : newPersonalBest ? TimingColor.Green
                : TimingColor.Neutral;

            string deltaText;
            SolidColorBrush deltaBrush;

            if (newPersonalBest)
            {
                deltaText = _personalBestLapMs == null ? "-" : "best";
                deltaBrush = TimingColorPalette.PurpleText;

                // This lap is the new reference for live delta comparisons on future laps.
                _personalBestLapCumulativeSplitsMs = new[]
                {
                    sector1Ms,
                    sector1Ms + sector2Ms,
                    ms
                };
            }
            else
            {
                long deltaMs = (long)ms - (long)_personalBestLapMs!.Value;
                deltaText = FormatDelta(deltaMs);
                deltaBrush = deltaMs <= 0 ? TimingColorPalette.GreenText : TimingColorPalette.YellowText;
            }

            if (newPersonalBest)
            {
                _personalBestLapMs = ms;
                PersonalBestLapText = FormatTime(ms);
                PersonalBestLapColorBrush = TimingColorPalette.GreenText;
            }

            LastLapTimeText = FormatTime(ms);
            LastLapColorBrush = TimingColorPalette.TextBrush(color);
            DeltaToPbText = deltaText;
            DeltaToPbColorBrush = deltaBrush;

            InsertSkippedLapRows(completedLapNum);
            _lastHistoryLapNum = completedLapNum;
            LapHistory.Insert(0, new LapHistoryEntry
            {
                // The GAME's lap number, not a count of laps this app happened to witness.
                // A self-incrementing counter silently renumbered from 1 whenever the app
                // didn't see the start of a race - a red-flag restart issues a fresh
                // SessionUID mid-race, so the history restarted at "Lap 1" while the game
                // (and the tower's "LAP X / Y") was already on lap 4. Confirmed from the
                // Chinese GP sprint: first lap the app recorded logged as lap=4 yet
                // displayed as "Lap 1", leaving a 7-lap sprint showing a 5-row history
                // ending at "Lap 5". Sourcing the number from telemetry makes the rows
                // agree with the tower and any genuinely missed laps show up as an honest
                // gap instead of shifting every later lap.
                LapNumberText = $"Lap {completedLapNum}",
                LapTimeText = FormatTime(ms),
                DeltaText = deltaText,
                ColorBrush = TimingColorPalette.TextBrush(color),
                LapTagText = lapTag,
                HasLapTag = !string.IsNullOrEmpty(lapTag),
                // IN's stop time isn't passed in here at all - it's patched retroactively
                // by PatchMostRecentInRowPitTime once the stop actually finishes (see
                // CarLapTracker). OUT's total pit-lane time has no such ambiguity, so it's
                // populated directly at row-creation time like everything else here.
                PitStopTimeText = lapTag == "OUT" && pitLaneTimeMs > 0 ? FormatSector(pitLaneTimeMs) : "",
                // Captured here (not passed in) because at this point in HandleLapData the
                // live Sector1/2/3 Text/Brush properties still hold the lap that just
                // finished - ResetSectorDisplayForNewLap() only runs after this returns.
                Sector1Text = Sector1Text,
                Sector1Brush = Sector1TextBrush,
                Sector2Text = Sector2Text,
                Sector2Brush = Sector2TextBrush,
                Sector3Text = Sector3Text,
                Sector3Brush = Sector3TextBrush,
                Events = BuildLapEvents()
            });
            // No cap: every lap is kept and stays reachable by scrolling the viewport (a
            // race is <=~78 laps, negligible to hold in memory / render un-virtualized).
        }

        /// <summary>
        /// Assembles the completed lap's EVENTS chips - the caution active during it (if any)
        /// plus the event-driven items accrued since the last lap - then clears that per-lap
        /// state. An ongoing SC/VSC re-marks the next lap via HandleSession.
        /// </summary>
        private List<LapEvent> BuildLapEvents()
        {
            var events = new List<LapEvent>();
            if (_lapEventSafetyCar == SafetyCarType.FullSafetyCar)
                events.Add(new LapEvent(LapEventKind.SafetyCar, "Safety Car"));
            else if (_lapEventSafetyCar == SafetyCarType.VirtualSafetyCar)
                events.Add(new LapEvent(LapEventKind.VirtualSafetyCar, "Virtual SC"));
            events.AddRange(_pendingLapEvents);

            _pendingLapEvents.Clear();
            _pendingPenaltySeverity = 0; // next lap starts with no penalty held
            _lapEventSafetyCar = SafetyCarType.NoSafetyCar;
            return events;
        }

        /// <summary>
        /// Testing the hypothesis that on some tracks the pit lane sits entirely within
        /// the NEXT lap after the one tagged IN, not the IN lap itself - if so, the IN row
        /// already exists (built a full lap earlier) by the time the stop actually
        /// finishes, so the only way to get the duration into it is to patch it
        /// retroactively. LapHistoryEntry isn't a live-bindable object (no per-property
        /// change notification), so "patch" means replacing the entry at index 0 - the
        /// newest lap is always inserted there - so the bound ItemsControl updates just
        /// that one row instead of rebuilding the whole list.
        /// </summary>
        private void PatchMostRecentInRowPitTime(uint durationMs)
        {
            if (LapHistory.Count == 0) return;
            var mostRecent = LapHistory[0];
            if (!mostRecent.HasLapTag || mostRecent.LapTagText != "IN" || !string.IsNullOrEmpty(mostRecent.PitStopTimeText)) return;

            LapHistory[0] = new LapHistoryEntry
            {
                LapNumberText = mostRecent.LapNumberText,
                LapTimeText = mostRecent.LapTimeText,
                DeltaText = mostRecent.DeltaText,
                ColorBrush = mostRecent.ColorBrush,
                LapTagText = mostRecent.LapTagText,
                HasLapTag = mostRecent.HasLapTag,
                PitStopTimeText = FormatSector(durationMs),
                Sector1Text = mostRecent.Sector1Text,
                Sector1Brush = mostRecent.Sector1Brush,
                Sector2Text = mostRecent.Sector2Text,
                Sector2Brush = mostRecent.Sector2Brush,
                Sector3Text = mostRecent.Sector3Text,
                Sector3Brush = mostRecent.Sector3Brush,
                Events = mostRecent.Events
            };
        }

        private void SetSectorDisplay(int sectorIdx, uint ms, TimingColor color)
        {
            string text = FormatSector(ms);
            var textBrush = TimingColorPalette.TextBrush(color);
            var bgBrush = TimingColorPalette.BackgroundBrush(color);

            switch (sectorIdx)
            {
                case 0:
                    Sector1Text = text; Sector1TextBrush = textBrush; Sector1BackgroundBrush = bgBrush;
                    break;
                case 1:
                    Sector2Text = text; Sector2TextBrush = textBrush; Sector2BackgroundBrush = bgBrush;
                    break;
                case 2:
                    Sector3Text = text; Sector3TextBrush = textBrush; Sector3BackgroundBrush = bgBrush;
                    break;
            }
        }

        private void ResetSectorDisplayForNewLap()
        {
            Sector1Text = ""; Sector1TextBrush = TimingColorPalette.NeutralText; Sector1BackgroundBrush = TimingColorPalette.NeutralBg;
            Sector2Text = ""; Sector2TextBrush = TimingColorPalette.NeutralText; Sector2BackgroundBrush = TimingColorPalette.NeutralBg;
            Sector3Text = ""; Sector3TextBrush = TimingColorPalette.NeutralText; Sector3BackgroundBrush = TimingColorPalette.NeutralBg;
            _lastCompletedSectorColorThisLap = TimingColor.Neutral;
            _cumulativeThisLapMs = 0;
            DeltaToPbText = "-";
            DeltaToPbColorBrush = TimingColorPalette.NeutralText;
        }

        private static string FormatTime(uint ms)
        {
            if (ms == 0) return "-:--.---";
            var span = TimeSpan.FromMilliseconds(ms);
            return $"{(int)span.TotalMinutes}:{span.Seconds:D2}.{span.Milliseconds:D3}";
        }

        private static string FormatSector(uint ms)
        {
            var span = TimeSpan.FromMilliseconds(ms);
            return span.TotalMinutes >= 1
                ? $"{(int)span.TotalMinutes}:{span.Seconds:D2}.{span.Milliseconds:D3}"
                : $"{span.Seconds}.{span.Milliseconds:D3}";
        }

        private static string FormatDelta(long ms, int decimals = 3)
        {
            double seconds = ms / 1000.0;
            string formatted = seconds.ToString($"F{decimals}");
            return seconds >= 0 ? $"+{formatted}" : formatted;
        }
    }
}
