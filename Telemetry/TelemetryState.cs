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

        private const int LapHistoryDepth = 12;
        public ObservableCollection<LapHistoryEntry> LapHistory { get; } = new();

        private int _lapNumberForHistory = 0;

        // ---- Tyres (bindable) ----
        private string _tyreCompoundLetter = "?";
        public string TyreCompoundLetter { get => _tyreCompoundLetter; private set => SetProperty(ref _tyreCompoundLetter, value); }

        private SolidColorBrush _tyreCompoundBrush = CompoundPalette.Unknown;
        public SolidColorBrush TyreCompoundBrush { get => _tyreCompoundBrush; private set => SetProperty(ref _tyreCompoundBrush, value); }

        private SolidColorBrush _tyreCompoundForeground = CompoundPalette.LightForeground;
        public SolidColorBrush TyreCompoundForeground { get => _tyreCompoundForeground; private set => SetProperty(ref _tyreCompoundForeground, value); }

        private string _tyreAgeLapsText = "-";
        public string TyreAgeLapsText { get => _tyreAgeLapsText; private set => SetProperty(ref _tyreAgeLapsText, value); }

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
        // its catalog-grid siblings, with a "+N more" row so nothing is silently dropped.
        private const int IssueListMaxRows = 5;

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

            _totalLaps = session.TotalLaps;

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

            // Always exactly LapHistoryDepth rows (blank placeholders until real laps come
            // in) so the widget's height - and everything stacked below it - never shifts
            // as a race progresses from lap 1 to lap 9+.
            LapHistory.Clear();
            for (int i = 0; i < LapHistoryDepth; i++) LapHistory.Add(new LapHistoryEntry());

            PositionList.Clear();
            RaceStandings.Clear();
            _totalLaps = 0;
            LapCounterText = "-";
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
            TyreCompoundForeground = CompoundPalette.LightForeground;
            TyreAgeLapsText = "-";
            TyreWearFrontLeftText = TyreWearFrontRightText = TyreWearRearLeftText = TyreWearRearRightText = "-";

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
                RefreshAlertBanner();
            }
            else if (eventType == EventType.ChequeredFlag)
            {
                _isChequeredFlagActive = true;
                _chequeredFlagTimer.Stop();
                _chequeredFlagTimer.Start();
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
            }

            var car = span[playerIdx];

            TyreCompoundLetter = CompoundPalette.LetterFor(car.VisualTyreCompound);
            TyreCompoundBrush = CompoundPalette.BrushFor(car.VisualTyreCompound);
            TyreCompoundForeground = CompoundPalette.ForegroundFor(car.VisualTyreCompound);
            TyreAgeLapsText = car.TyresAgeLaps.ToString();

            RefreshPlayerFlag(car.VehicleFiaFlags);
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
            var ordered = CapIssueList(issues.OrderByDescending(i => i.Severity).Select(i => i.Text).ToList());
            if (!CollectionUnchanged(CarConditionIssues, ordered))
            {
                CarConditionIssues.Clear();
                foreach (var issue in ordered) CarConditionIssues.Add(issue);
            }
        }

        /// <summary>
        /// Truncates to IssueListMaxRows, replacing the last visible slot with a
        /// "+N more" summary so an overflowing list never silently drops information.
        /// Expects the list to already be sorted most-severe-first.
        /// </summary>
        private static List<string> CapIssueList(List<string> issues)
        {
            if (issues.Count <= IssueListMaxRows) return issues;
            int overflow = issues.Count - (IssueListMaxRows - 1);
            var capped = issues.Take(IssueListMaxRows - 1).ToList();
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

                tracker.LastKnownDriverStatus = car.DriverStatus;
            }

            if (_playerCarIndex >= 0 && _playerCarIndex < span.Length)
            {
                var playerLap = span[_playerCarIndex];
                CurrentLapTimeText = FormatTime(playerLap.CurrentLapTimeInMS);
                CurrentLapColorBrush = TimingColorPalette.TextBrush(_lastCompletedSectorColorThisLap);
                IsCurrentLapInvalid = playerLap.IsCurrentLapInvalid;

                RefreshPenalties(playerLap);
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

                // Blank for an out car, matching the real broadcast graphic (no compound
                // shown once a driver has retired).
                string tyreLetter = "";
                SolidColorBrush tyreBrush = CompoundPalette.Unknown;
                if (!isOut && _carTyreCompounds.TryGetValue(i, out var compound))
                {
                    tyreLetter = CompoundPalette.LetterFor(compound);
                    tyreBrush = CompoundPalette.BrushFor(compound);
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
                    TyreLetter = tyreLetter,
                    TyreBrush = tyreBrush,
                    IsPenaltyPending = hasPendingPenalty,
                    IsFastestLap = isFastestLap,
                    // 2 decimals here specifically (not the app-wide 3) - frees up column
                    // width in the tower to run the font size up, and 2 decimals is still
                    // plenty precise for a live gap glanced at mid-race.
                    // "Out" only shown once (Gap column) - showing it in both Int and Gap
                    // read as "Out Out" side by side, matching the real broadcast's single
                    // label instead.
                    IntervalText = isOut ? "" : isLeader ? "-" : FormatDelta(intervalMs, 2),
                    GapText = isOut ? "Out" : isLeader ? "Leader" : FormatDelta(gapMs, 2)
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

            // One line per specific warning (from PenaltyIssued events), not just a bare
            // count - falls back to a generic "+N more" only for whatever the event-based
            // list hasn't accounted for (e.g. warnings from before the app connected this
            // session), so the total always still matches LapData's own running count.
            foreach (var reason in _warningReasons) issues.Add((50, $"Warning - {reason}"));
            int untrackedWarnings = playerLap.TotalWarnings - _warningReasons.Count;
            if (untrackedWarnings > 0) issues.Add((40, $"+{untrackedWarnings} more warning(s)"));

            PenaltiesIsOk = issues.Count == 0;
            var ordered = CapIssueList(issues.OrderByDescending(i => i.Severity).Select(i => i.Text).ToList());
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

            var rows = new List<CarStanding>();
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

                rows.Add(new CarStanding
                {
                    Position = car.CarPosition,
                    DriverName = name ?? $"Car {i + 1}",
                    TeamName = team ?? "",
                    LiveryBrush = livery ?? TimingColorPalette.NeutralText,
                    BestLapText = bestText,
                    GapText = gapText,
                    IsPlayer = i == _playerCarIndex
                });
            }

            rows.Sort((a, b) => a.Position.CompareTo(b.Position));

            if (!CollectionUnchanged(PositionList, rows))
            {
                PositionList.Clear();
                foreach (var row in rows) PositionList.Add(row);
            }
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
                Sector3Brush = Sector3TextBrush
            });
            while (LapHistory.Count > LapHistoryDepth) LapHistory.RemoveAt(LapHistory.Count - 1);
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
                Sector3Brush = mostRecent.Sector3Brush
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
