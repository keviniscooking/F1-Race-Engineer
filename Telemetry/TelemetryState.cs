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

        // ---- Best-lap tracking (whole field, for the qualifying position list) ----
        private readonly Dictionary<int, uint?> _carBestLapMs = new();

        // ---- Participant identity cache ----
        private readonly Dictionary<int, string> _participantNames = new();
        private readonly Dictionary<int, SolidColorBrush> _participantLivery = new();
        private readonly Dictionary<int, string> _participantTeams = new();

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
        private bool _isChequeredFlagActive;
        private SafetyCarType _safetyCarStatus = SafetyCarType.NoSafetyCar;
        private readonly DispatcherTimer _redFlagTimer = new() { Interval = TimeSpan.FromSeconds(15) };
        private readonly DispatcherTimer _chequeredFlagTimer = new() { Interval = TimeSpan.FromSeconds(8) };

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

        // Grouping keys from SessionDataPacket, cached here and stamped onto the SavedRace so
        // the history can tie a weekend's sessions together and separate season/career saves.
        private uint _seasonLinkId;
        private uint _weekendLinkId;
        private uint _sessionLinkId;

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
        // them in a fixed-height viewport (12 rows) and scrolls once there are more than
        // fit - see LapTimingWidget.xaml. Nothing is ever dropped, so a full race can be
        // scrolled back through end to end.
        public ObservableCollection<LapHistoryEntry> LapHistory { get; } = new();

        private int _lapNumberForHistory = 0;

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

        public ObservableCollection<string> PenaltiesIssues { get; } = new();

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
            if (_lastSeenSessionType != session.SessionType || _lastSeenSessionUID != session.Header.SessionUID)
            {
                ResetSessionScopedState();
                _lastSeenSessionType = session.SessionType;
                _lastSeenSessionUID = session.Header.SessionUID;
            }

            var preset = PresetMapper.FromSessionType(session.SessionType);
            if (preset != CurrentPreset)
            {
                CurrentPreset = preset;
            }

            if (session.SafetyCarStatus != _safetyCarStatus)
            {
                _safetyCarStatus = session.SafetyCarStatus;
                RefreshAlertBanner();
            }

            // Remember a caution seen at any point during the current lap so the lap's EVENTS
            // chip shows it even if the SC/VSC started and ended mid-lap.
            if (session.SafetyCarStatus is SafetyCarType.FullSafetyCar or SafetyCarType.VirtualSafetyCar)
                _lapEventSafetyCar = session.SafetyCarStatus;

            _totalLaps = session.TotalLaps;
            _currentTrack = session.Track;
            _seasonLinkId = session.SeasonLinkIdentifier;
            _weekendLinkId = session.WeekendLinkIdentifier;
            _sessionLinkId = session.SessionLinkIdentifier;
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
            _lapNumberForHistory = 0;

            _carBestLapMs.Clear();
            _carTrackers.Clear(); // stale baselines from the old session would cause spurious sector/lap detections otherwise
            _carTyreCompounds.Clear();
            _carTyreAge.Clear();

            // Empty at the start of a session; the widget's fixed-height viewport
            // (LapTimingWidget.xaml) holds the height steady, so there's no need to pad
            // with placeholder rows - real laps just fill in from the top as they complete.
            LapHistory.Clear();

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
            _lapEventSafetyCar = SafetyCarType.NoSafetyCar;

            CarConditionIssues.Clear();
            CarConditionIsOk = true;

            PenaltiesIssues.Clear();
            PenaltiesIsOk = true;
            _warningReasons.Clear();
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

            for (int i = 0; i < count; i++)
            {
                var p = span[i];
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

                // Genuine time-costing penalties (not warnings/lap-invalidations) get a
                // chip on the lap they were issued.
                var penaltyEvent = PenaltyToLapEvent(penalty.PenaltyType, penalty.Time, infringement);
                if (penaltyEvent != null) _pendingLapEvents.Add(penaltyEvent);

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
        }

        // Red flag / chequered fire once but the event can repeat - only keep one per lap.
        private void AddPendingLapEvent(LapEvent e)
        {
            if (_pendingLapEvents.Any(x => x.Kind == e.Kind)) return;
            _pendingLapEvents.Add(e);
        }

        // Only genuine time-costing penalties get a lap chip; warnings and lap-invalidations
        // are deliberately excluded (per the design discussion).
        private static LapEvent? PenaltyToLapEvent(PenaltyType type, int timeSeconds, string infringement) => type switch
        {
            PenaltyType.TimePenalty => new LapEvent(LapEventKind.Penalty, $"{timeSeconds}s · {infringement}"),
            PenaltyType.DriveThrough => new LapEvent(LapEventKind.Penalty, $"Drive-through · {infringement}"),
            PenaltyType.StopGo => new LapEvent(LapEventKind.Penalty, $"Stop-go · {infringement}"),
            _ => null
        };

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
                // Age counts laps already run on the tyre, so currentLap - age is when it was
                // fitted - accurate even if the app joined the race mid-session.
                int startLap = Math.Max(1, _playerCurrentLap - age);
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
            var ordered = CapIssueList(issues.OrderByDescending(i => i.Severity).Select(i => i.Text).ToList(), IssueListMaxEntries);
            if (!CollectionUnchanged(CarConditionIssues, ordered))
            {
                CarConditionIssues.Clear();
                foreach (var issue in ordered) CarConditionIssues.Add(issue);
            }
        }

        /// <summary>
        /// Truncates to <paramref name="maxEntries"/>, replacing the last visible slot with a
        /// "+N more" summary so an overflowing list never silently drops information.
        /// Expects the list to already be sorted most-severe-first.
        /// </summary>
        private static List<string> CapIssueList(List<string> issues, int maxEntries)
        {
            if (issues.Count <= maxEntries) return issues;
            int overflow = issues.Count - (maxEntries - 1);
            var capped = issues.Take(maxEntries - 1).ToList();
            capped.Add($"+{overflow} more");
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

                    string lapTag = isPlayer ? ClassifyLapTag(tracker.LastKnownDriverStatus) : "";
                    loggedLapTag = lapTag; // TEMP pit diagnostic
                    RegisterLapTime(car.LastLapTimeInMS, sector1Ms, sector2Ms, sector3Ms, isPlayer, lapTag, tracker.PendingPitLaneTimeMs);
                    tracker.PendingPitLaneTimeMs = 0; // consumed - don't let it leak onto a later unrelated OUT lap

                    // Track best lap per car (whole field) for the qualifying position list
                    if (!_carBestLapMs.TryGetValue(i, out var best) || best == null || car.LastLapTimeInMS < best)
                    {
                        _carBestLapMs[i] = car.LastLapTimeInMS;
                    }

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

            RefreshRaceStandings(span);
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
            var issues = new List<(int Severity, string Text)>();
            if (playerLap.NumUnservedStopGoPens > 0) issues.Add((100, $"Unserved stop-go: {playerLap.NumUnservedStopGoPens}"));
            if (playerLap.NumUnservedDriveThroughPens > 0) issues.Add((90, $"Unserved drive-through: {playerLap.NumUnservedDriveThroughPens}"));
            if (playerLap.Penalties > 0) issues.Add((80, $"Time penalties: +{playerLap.Penalties}s"));

            // One line per specific warning *kind* (from PenaltyIssued events), not just a
            // bare count - and duplicates of the same kind collapse into a single line with
            // an "(xN)" multiplier (e.g. "Warning - Track limits (x3)") rather than
            // repeating the identical row and eating list slots. GroupBy preserves
            // first-occurrence order. Falls back to a generic "+N more" for whatever the
            // event-based list hasn't accounted for (e.g. warnings from before the app
            // connected this session), so the total always still matches LapData's own count.
            foreach (var group in _warningReasons.GroupBy(r => r))
            {
                int count = group.Count();
                issues.Add((50, count > 1 ? $"Warning - {group.Key} (x{count})" : $"Warning - {group.Key}"));
            }
            int untrackedWarnings = playerLap.TotalWarnings - _warningReasons.Count;
            if (untrackedWarnings > 0) issues.Add((40, $"+{untrackedWarnings} more warning(s)"));

            PenaltiesIsOk = issues.Count == 0;
            // The flag banner, when shown, sits above this list and eats one grid row (2 entries'
            // worth), so the list's share of the shared budget shrinks to match - that's what keeps
            // this card level with Car Condition instead of a row taller. Read live rather than
            // cached: this runs every LapData tick, so it tracks the flag appearing/clearing on its
            // own. The list is already severity-sorted, so the entries squeezed out are the mildest.
            int entryBudget = PlayerFlagVisible ? IssueListMaxEntries - FlagBannerEntryCost : IssueListMaxEntries;
            var ordered = CapIssueList(issues.OrderByDescending(i => i.Severity).Select(i => i.Text).ToList(), entryBudget);
            if (!CollectionUnchanged(PenaltiesIssues, ordered))
            {
                PenaltiesIssues.Clear();
                foreach (var issue in ordered) PenaltiesIssues.Add(issue);
            }
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
                SessionLabel = SessionLabelFor(_lastSeenSessionType),
                TotalLaps = _totalLaps > 0 ? _totalLaps : player.NumLaps,
                SeasonLinkId = _seasonLinkId,
                WeekendLinkId = _weekendLinkId,
                SessionLinkId = _sessionLinkId,
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
                Penalties = PenaltiesIssues.ToList(),
                Classification = BuildClassification(span, numCars, playerIdx, fastestIdx),
                PlayerLaps = SnapshotPlayerLaps()
            };

            _savedClassificationUid = uid;
            if (_historyStore.Save(race))
                RaceSaved?.Invoke(race);
        }

        private static bool IsOutStatus(ResultStatus status) => status is ResultStatus.Retired
            or ResultStatus.DidNotFinish or ResultStatus.Disqualified or ResultStatus.NotClassified;

        private static string SessionLabelFor(SessionType? type) => type switch
        {
            SessionType.Race2 or SessionType.Race3 => "Sprint",
            _ => "Race"
        };

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
        /// Assumes DriverStatus holds InLap/OutLap for the whole lap in question and only
        /// flips at (or after) the timing line, which is why the caller passes the
        /// tracker's previous-tick value. Confirmed correct via live testing (HANDOFF §5,
        /// eighth round).
        /// </summary>
        private static string ClassifyLapTag(DriverStatus status) => status switch
        {
            DriverStatus.InLap => "IN",
            DriverStatus.OutLap => "OUT",
            _ => ""
        };

        private void RegisterLapTime(uint ms, uint sector1Ms, uint sector2Ms, uint sector3Ms, bool isPlayer, string lapTag, uint pitLaneTimeMs)
        {
            bool newSessionBest = _sessionBestLapMs == null || ms < _sessionBestLapMs;
            if (newSessionBest) _sessionBestLapMs = ms;

            if (!isPlayer) return;

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

            _lapNumberForHistory++;
            LapHistory.Insert(0, new LapHistoryEntry
            {
                LapNumberText = $"Lap {_lapNumberForHistory}",
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
