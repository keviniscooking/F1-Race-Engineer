# F1 Race Engineer — Project Handoff

This document exists so the project can be continued (e.g. in Claude Code) without
re-deriving design decisions or repeating solved problems. The source code shows
*what* was built; this document records *why*, *what's deferred*, and *what's not
yet trustworthy*. Read this first, then `docs/F1GameUDP_API_reference.txt`.

---

## 1. What this project is

A Windows desktop "second-screen" race-engineer dashboard for the game **F1 25**
(including its **2026 Season Pack** DLC). It reads live UDP telemetry the game
broadcasts and displays engineer-relevant data on a separate screen while the
user races — the user cannot alt-tab out of the game, so the app must be glanceable
and must switch context automatically.

- **Stack:** C# / WPF, targeting `net10.0-windows`.
- **Telemetry parsing:** `F1Game.UDP` NuGet package, version **26.0.0** (parses both
  F1 25 and the 2026 Season Pack; the 2026 pack adds a `CarTelemetry2` packet on top
  of the same base packets). The in-game **UDP Format** setting (`2025` or `2026`)
  is independent of this - **confirmed live that both settings work**, not just
  `2025` as previously assumed/documented.
- **Build/run:** `dotnet build` / `dotnet run` from the project root.
- **Version control:** in git, with a **public** GitHub remote
  (`https://github.com/keviniscooking/F1-Race-Engineer`) - made public so a friend can
  download installers from Releases and the auto-updater (below) can read them without
  embedding a token in the shipped app. Confirmed via a full repo scan (current tree,
  full history including anything ever added-then-deleted, and the icon binary's
  metadata) before going public: no secrets, credentials, tokens, or personal info
  anywhere. `bin/`, `obj/`, `publish/`, `Releases/`, and `.claude/settings.local.json`
  are gitignored - the last one is this machine's local Claude Code permission
  settings, not project content; `publish/`/`Releases/` are `vpk`'s local packaging
  output (see "Cutting a release" below), regenerated every release, never committed.
  Licensed **MIT** (`LICENSE`) - added going public since without a license the
  default is "all rights reserved" even on a publicly visible repo.
- **Auto-update via Velopack** (`Velopack` NuGet package). The app checks GitHub
  Releases once at every launch and silently downloads + applies + restarts if a
  newer version is published - no UI, no prompt. This required restructuring the
  normal WPF startup: `App.xaml` no longer has `StartupUri` (removed), and its build
  action is `Page` instead of the default `ApplicationDefinition` (`.csproj`), because
  `App.xaml.cs` now defines its own `Main` that calls `VelopackApp.Build().Run()`
  *before* anything else - Velopack briefly re-launches the exe with special
  arguments during install/update/uninstall and needs to intercept that first. Update
  checking itself lives in `App.OnStartup` (guarded by `UpdateManager.IsInstalled`, so
  it's a no-op when running an un-packaged build straight from `bin/Debug` or
  `bin/Release` - which is how this app is routinely launched/verified during
  development and must keep working unchanged) and is wrapped in try/catch (no
  internet or GitHub being unreachable must never block the app from opening).
  `<Version>` in the `.csproj` is what Velopack compares to detect an update - bump it
  every release (see below).
- **Cutting a release** (replaces the old "build Release + point a desktop shortcut
  at the exe" flow - Velopack now owns install/shortcuts):
  1. Bump `<Version>` in `F1RaceEngineer.csproj`.
  2. `dotnet publish F1RaceEngineer.csproj -c Release -r win-x64 --self-contained -o publish`
  3. `dotnet tool install -g vpk` (one-time, if not already installed)
  4. `vpk pack --packId F1RaceEngineer --packVersion <version> --packDir publish --mainExe F1RaceEngineer.exe`
  5. `vpk upload github --repoUrl https://github.com/keviniscooking/F1-Race-Engineer --publish --tag v<version>` (needs a GitHub token with repo write access via `--token` or the `GITHUB_TOKEN` env var)
  First-time install (both for the project owner and a friend): download and run the
  `Setup.exe` from the latest GitHub Release once - it installs the app and creates
  its own Start Menu/desktop shortcuts. Every release after that self-updates
  silently on launch, no manual re-download needed. **`v1.0.0` cut and published this
  way** - unsigned (no code-signing certificate), so Windows SmartScreen will likely
  warn on first run; a known, accepted tradeoff for unsigned indie software, not a bug.
  `AppIcon.ico` (project root) is a custom-drawn icon, not a
  stock image - a rounded blue-badge (`#1F6FEB`, the app's own accent colour) around
  the exact flag pole/pennant glyph already used in-app for alerts (`AlertBanner`,
  Penalties & Flags), generated programmatically via `System.Drawing` rather than
  external image tools, consistent with this project's "drawn, not captured" icon
  convention. Wired via `<ApplicationIcon>` in the `.csproj` (the .exe file's own
  icon) and `Window.Icon` in `MainWindow.xaml` (the running window/taskbar icon) -
  both needed, they're separate things.

## 2. Current state (built and confirmed working against live game data)

- **UDP listener** (`UdpListenerService`) — binds to a port (default 20777), parses
  packets on a background thread, raises a single `PacketReceived` event with the
  raw `UnionPacket`. `MainWindow` calls `Connect()` once at startup so the common case
  (game already pointed at the default port) needs no manual click - deliberately no
  retry loop if that first attempt fails (e.g. port already in use); the
  Connect/Disconnect button is still there for a manual attempt either way.
- **Central state** (`TelemetryState`) — one instance, subscribes to the listener,
  maintains all live state, exposes bindable properties to widgets.
- **Preset auto-switching** — reads `SessionDataPacket.SessionType`, maps to one of
  three presets (Practice / Qualifying / Race), highlights the active tab. No manual
  tab clicking.
- **Lap Timing widget** — current lap time with live provisional colouring, personal
  best time, delta vs personal best (updates at each sector boundary AND lap end),
  three sector boxes (time + colour), and a lap history list. Colour convention
  confirmed working live.
- **Lap History** — toggleable (see Settings panel below), shows lap #, an IN/OUT tag
  for in-laps/out-laps (its own dedicated column so Time's start position never shifts),
  a "PIT" column showing the stop time on the IN row (patched in retroactively once the
  stop finishes - see §5 thirteenth round) and total pit-lane time on the OUT row
  (forward-stored, no ambiguity), per-sector times each with a colour-coded underline,
  lap time, and delta, one lap per row. Confirmed rendering correctly, including the
  IN/OUT classification itself — **confirmed live**. The PIT column's OUT-row value is
  new and not yet live-confirmed; the IN-row value has gone through two rounds of live
  testing that each ruled out a prior theory (see §6) - still not confirmed working.
- **Alert banner** — Safety Car / VSC / Red Flag / Chequered Flag (see caveats in §6).
  Confirmed working live, including VSC.
- **Qualifying position list** — full field, livery colour swatch, driver name +
  team name (from `ParticipantData.Team`, mapped to a display label in
  `Models/TeamNames.cs`), best lap, gap to leader.
- **Qualifying also shows** a second Lap Timing widget instance (history hidden, via
  the `ShowHistory` dependency property) stacked above the position list.
- **Widget catalog built**: Tyres (compound badge + per-corner wear, compact
  "+"-divided car-layout diagram), Car Condition (quiet checkmark when clean / yellow
  itemized list when actually damaged), Penalties & Flags (same pattern + a live flag
  chip), Session & Track (weather badge + a single "next forecast change" callout, or
  "Stable - no change expected"). All toggleable via the gear-icon **settings panel**
  (`Widgets/SettingsPanel.xaml`), with per-preset default state: everything on for
  Race; only the core (Lap Timing / Position List) for Practice and Qualifying.
  Confirmed live in a real race session (see fixes in §6 that came out of that test).
- **Race Position Tower** (`Widgets/RacePositionTowerWidget.xaml`) — a "LAP X / Y"
  banner (tracks the race leader, §5 thirteenth round) above a full-field tower shown
  only in Race, to the left of everything else: position, livery swatch, driver + team,
  Interval (gap to car ahead), Gap (to leader), tyre compound letter, and a pending-
  penalty badge. Reads `LapData.DeltaToCarInFrontInMS`/`DeltaToRaceLeaderInMS` directly
  per car - no cross-referencing needed, unlike the old Gaps & Position widget it
  replaced (see §8 for why that one was removed). Retired/DNF/DSQ/not-classified cars
  show a dimmed row and a single "Out" instead of a stale interval/gap (§5, tenth and
  thirteenth rounds).
- **Adaptive grid layout** (`MainWindow.xaml.cs` `ArrangeWidgets`) rebuilds the catalog
  widgets' row/column definitions to fit exactly the currently-visible set on every
  toggle change, so remaining widgets grow to fill freed space with no blank gaps.
  Widget key order is grouped by content density (denser widgets grouped together,
  compact ones grouped together) so rows sharing a card height don't mismatch as badly.

Confirmed working in-game across thirteen full live-testing rounds by this point (see
§5 for the complete log of what each round found and fixed): lap/sector timing and
colouring, preset auto-switch, both position displays (Qualifying's list and the Race
tower) with real names/livery/team, live delta, personal best, the alert banner
(including VSC and, as of round seven, Chequered Flag's timing), the full catalog
widget set, Lap History's IN/OUT tagging (round eight), and Car Condition looking
correct overall (round nine, though see the caveat on race-start creep in §6). Rounds
eleven and thirteen each confirmed a prior visual fix (tyre letter spacing, "Out Out")
while also catching that the PIT column's IN-row value was STILL broken two rounds
running - each round ruled out one specific theory rather than finding the fix on the
first try (see §5/§6 for the full history). Still open: the final-lap registration fix
(§5 round five), the session-restart fix (§5 round nine), the PIT column's IN-row
retroactive-patch fix (§5 round thirteen - now on its third attempt), the new OUT-row
pit-lane time, the new lap counter banner, and the FIA-aligned penalty text (all §5
round thirteen) - none independently re-verified live yet - plus Car Condition's
damage-threshold decision (§8). A fourteenth round (§5) merged the connection bar and
preset tabs into one row and stopped the error-text row from reserving space when
empty - pure window-chrome layout, verified via screenshot rather than live game data
(no telemetry-dependent behaviour involved). A fifteenth round (§5) increased Lap
History's depth, capped and severity-sorted the Car Condition/Penalties & Flags issue
lists, and fixed a catalog-grid outer-edge alignment bug - also pure layout/UI work,
verified via screenshot and temporary debug scaffolding rather than live game data.

## 3. Architecture

```
F1 25 game ──UDP──> UdpListenerService (background thread)
                          │  raises PacketReceived(UnionPacket)
                          ▼
                    TelemetryState  ── marshals every packet onto the UI thread
                          │            via Application.Current.Dispatcher.BeginInvoke
                          │  exposes bindable properties + ObservableCollections
                          ▼
                    Widgets (data-bound UserControls) hosted in MainWindow
```

- **One `TelemetryState` instance** is the single source of truth. All widgets bind
  to it. Adding a widget = add bindable properties to `TelemetryState` + a new
  `UserControl`; no new telemetry plumbing needed.
- **Preset visibility** is handled in `MainWindow.UpdateWidgetVisibility()` by
  show/collapse, layered with the settings panel's per-widget toggles
  (`ApplyCatalogWidgetLayout`) — the settings panel is built, not deferred.
- **Colour palette** is centralised in `Models/TimingColor.cs` (`TimingColorPalette`),
  frozen `SolidColorBrush` instances. Reuse these rather than making new brushes.

## 4. Critical technical gotchas (each of these cost real debugging time)

1. **Target framework MUST be `net10.0-windows`.** `net8.0-windows` causes type
   resolution failures because the package ships only `net10.0` assets.
2. **Namespaces are nested.** Types live under `F1Game.UDP.Packets`,
   `F1Game.UDP.Enums`, `F1Game.UDP.Data`, `F1Game.UDP.Events` — NOT the root
   `F1Game.UDP`. `using F1Game.UDP;` alone resolves almost nothing. This caused
   three rounds of CS0246 errors early on.
3. **`ToPacket` is a static method on `PacketReader`**, called as
   `PacketReader.ToPacket(buffer)` — not an extension method (extension-method
   syntax `buffer.ToPacket()` failed to compile).
4. **Per-car arrays are `Array24<T>`**, not plain arrays — no indexer; use
   `.AsSpan()[index]`.
5. **PowerShell cannot reflect the net10.0 assembly** (ReflectionTypeLoadException
   under Windows PowerShell / .NET Framework). The API reference was generated by a
   small standalone `net10.0` console app that loads the DLL by file path and dumps
   every public type/member. That output is `docs/F1GameUDP_API_reference.txt` and is
   the authoritative source for exact type/property names — check it before writing
   any new packet-reading code rather than guessing from online docs.
6. **All `TelemetryState` mutation is marshalled onto the UI thread.** UDP packets
   arrive on a background thread; WPF bound properties and `ObservableCollection`
   require the UI thread. Every packet handler runs inside
   `Application.Current.Dispatcher.BeginInvoke`. Do not bypass this.

## 5. Bugs found via live testing, and their fixes (don't reintroduce these)

- **Cross-thread `ObservableCollection` crash** on crossing the finish line — fixed
  by the UI-thread marshalling in §4.6. Any new `ObservableCollection` mutation must
  stay on the UI thread.
- **Practice lap times leaked into Qualifying's "Best" column** — there was no
  session-boundary reset. Fixed by `ResetSessionScopedState()`, triggered on any
  `SessionType` change (which also correctly resets between Q1/Q2/Q3). Any new
  session-scoped state added later MUST be cleared there too, or the same class of
  leak returns.
- **Delta vs personal best only updated at lap end** — now also updates at each
  sector boundary, comparing cumulative time this lap against the cumulative splits
  of the player's *actual* best lap (snapshotted when a new PB is set), not a
  theoretical best-sectors combination.

### Self-audit pass (senior-engineer code review, no live-game changes)
- **`RefreshPositionList` churned every LapData tick** — `PositionList.Clear()` +
  re-`Add()` fired even when nothing in the standings actually changed, forcing the
  bound `ItemsControl` to fully tear down and rebuild every row's visual tree (a
  `Reset` notification, not a per-item update) many times a second. Fixed with a
  `PositionListUnchanged()` guard that compares the freshly computed rows against the
  current collection before touching it.
- **`RefreshSessionAndTrack` rebuilt `ForecastEntries` every Session packet** (~2Hz)
  even though forecast samples barely change tick-to-tick. Fixed with a
  `_lastForecastSamples` cache + `SequenceEqual` guard. **Superseded, not current
  code**: the second live-testing round below replaced the whole multi-card
  `ForecastEntries` collection this guarded with a single "next change" callout
  (scalar bindable properties, no collection to churn) - `_lastForecastSamples` no
  longer exists in the codebase. Left here as a historical record of the reasoning,
  not something to go looking for.
- **`ResetSessionScopedState()` was missing the Session & Track fields** (weather
  label/glyph/background, track/air temp, current rain %, forecast entries) — a real
  gap against this project's own rule (above) that all session-scoped state must be
  cleared there. Low practical impact since Session packets refresh this ~2Hz anyway,
  but fixed for correctness.
- **`CompoundPalette.ForegroundFor` had three redundant explicit branches** (Soft,
  Inter, Wet) that all resolved to the same value as the `_ => LightForeground`
  default case. Collapsed to two explicit cases (Medium, Hard/ClassicDry) + the
  default, no behaviour change.

### Second live-testing round
- **Lap History numbering carried over from the previous session** (e.g. a driver's
  first Race lap displayed as "Lap 7" because they'd done 6 Practice laps beforehand).
  Root cause: `_lapNumberForHistory` was never cleared in `ResetSessionScopedState()` -
  the exact class of bug that rule exists to prevent (see the Practice→Qualifying leak
  above), just on a field added later that missed it. Fixed by resetting it there too.
- **Lap History text was still too small to read at a glance** even after the first
  size pass - increased lap number/sector/time/delta font sizes again
  (`LapTimingWidget.xaml`) and widened the sector/time columns to match.
- **Position list only showed driver name, not team** — added `TeamName` to
  `CarStanding`, sourced from `ParticipantData.Team` via new `Models/TeamNames.cs`
  (collapses the enum's per-season variants like `Mercedes`/`Mercedes24` to one label),
  shown as a small caption under the driver name. Nationality and race number are also
  available on `ParticipantData` if more columns are wanted later.
- **Forecast strip was a wall of near-identical cards** — with stable weather every
  card showed the same icon/rain%, which was "not useful... not working at a glance"
  per feedback. Replaced the whole multi-card `ForecastEntries` strip with a single
  "next meaningful change" callout: scans upcoming samples for the first one whose
  weather differs from now or whose rain % differs by ≥15 points, and shows just that
  one (or "Stable — no change expected" if nothing in the forecast horizon qualifies).
  Removed `Models/ForecastEntry.cs` and the `ForecastEntries` collection entirely -
  superseded, not deprecated-but-kept.
- **Car Condition widget could grow taller than the viewport** (visible scrollbar) when
  many components were damaged at once, since issues rendered one per row. Fixed by
  switching the `ItemsControl` to a 2-column `UniformGrid` panel.
- **Car Condition showing non-zero damage on wings/floor/gearbox/engine/tyres with no
  visible crash** — raised by the user, then confirmed with certainty: these fields
  fill in at race start with zero collisions, so they carry some background-creep
  component too, not just genuine incident damage as the field names imply. NOT fixed
  yet - see the open decision in §8 (Car Condition damage threshold).
- **Purple sector repeating across consecutive laps (asked as a question, not a bug)**:
  confirmed working as intended - purple means "fastest of the session by anyone"
  (§4/§7 colour convention), so it's expected to stay purple across laps whenever
  nobody (including the player's own later laps) beats that sector time again.

### Third live-testing round (full-race test)
- **Car Condition's per-corner tyre damage removed** — the user pointed out it
  duplicates the Tyres widget's own per-corner wear readout (the two numbers were
  close enough in practice to read as the same information twice, even though
  `TyresDamage`/`TyresWear` are technically different fields). `TyresBlisters` was
  kept since blister info isn't shown anywhere else.
- **Gaps & Position and Fuel & ERS widgets removed entirely** — on all presets, not
  just Race. Deleted `Widgets/GapsPositionWidget.xaml(.cs)` and
  `Widgets/FuelErsWidget.xaml(.cs)`, their `TelemetryState` properties
  (`PlayerPositionText`/`GapAheadText`/`GapBehindText`, `FuelInTankText`/
  `FuelRemainingLapsText`/`FuelMixText`/`ErsStoreEnergyKjText`/`ErsDeployModeText`),
  and their settings-panel toggle entries. Gaps & Position was judged redundant once
  the Race Position Tower (below) covers the same information for the whole field, not
  just the player; Fuel & ERS was dropped by user request in the same pass.
- **New: Race Position Tower** (`Widgets/RacePositionTowerWidget.xaml(.cs)`,
  `Models/RaceStanding.cs`) — a full-field tower shown only in Race, to the left of
  everything else. Shows Interval (gap to car ahead) *and* Gap (to leader) - an early
  mockup dropped Interval to save width, but the user asked for both back, so the
  final version keeps both and the layout was widened instead (300px) rather than
  cutting a column.
  - Backed by `RefreshRaceStandings()`, reading `LapData.DeltaToCarInFrontInMS` and
    `DeltaToRaceLeaderInMS` (+ their `...Minutes` overflow fields) per car directly -
    no cross-referencing needed, unlike the old Gaps & Position widget which had to
    look up the car behind's own delta to work out the player's "gap behind".
  - Same change-detection-before-mutate guard as `PositionList` (now the shared generic
    `CollectionUnchanged<T>()` - see the audit pass below) to avoid `ObservableCollection`
    churn on every LapData tick.
  - Layout: `MainWindow.xaml`'s widget area is now a 2-column `Grid` (was a single
    `StackPanel`) - column 0 is the tower at a fixed `Width="300"` with the *column*
    itself `Width="Auto"`, so it collapses to zero width (taking its own `Margin` with
    it) when the tower is `Collapsed` outside Race, with no code-behind column-width
    juggling needed. Column 1 holds everything that used to be the whole widget area
    (Alert banner, Lap Timing, Position List, catalog grid).
  - Verified by temporarily wiring mock standings data into a debug-only method,
    screenshotting, and removing the scaffolding afterward (same pattern as the
    alert-banner verification earlier) - confirmed alignment, row highlighting for the
    player's row, and that driver/team names don't truncate at 300px. First attempt at
    260px did truncate longer names ("Racing Bulls", "Bortoleto") - this is why 300px
    was chosen over the mockup's original 250-260px range.
  - With only 4 catalog widgets left (Tyres, Car Condition, Penalties & Flags,
    Session & Track), `ColumnsFor()` naturally gives a clean 2×2 grid under Lap Timing
    with no further layout changes needed - confirmed live, matches the user's
    expectation going in.

### Full-codebase audit pass (redundancy/cleanup, no live-game changes)
- **Deleted a stray root-level `F1RaceEngineer_*_wpftmp.csproj`** - build debris from an
  earlier OneDrive file-lock failure that escaped `obj/` into the project root. Normal
  `obj/**/*wpftmp*` churn is expected and left alone; only the one in the root was wrong.
- **`RefreshRaceStandings`/`RefreshPositionList` had duplicated participant lookup** -
  both had an identical 3-line block resolving driver name/livery/team by car index.
  Extracted to a shared `GetParticipantInfo()`.
- **`PositionListUnchanged`/`RaceStandingsUnchanged` were near-identical comparison
  methods** checking different field sets on `CarStanding`/`RaceStanding`. Moved
  equality onto the models themselves (`IEquatable<T>`, `LiveryBrush` still compared by
  reference deliberately - brushes are cached/frozen so a real change is always a new
  instance) and replaced both with one generic `CollectionUnchanged<T>()`.
- **`CarConditionIssues`/`PenaltiesIssues` never had the change-detection fix applied**
  - they still did `Clear()` + rebuild on every single CarDamage/LapData tick regardless
    of whether anything changed, the same wasteful pattern already fixed for
  `PositionList` in an earlier audit but never extended here. Since `string` already
  implements `IEquatable<string>`, `CollectionUnchanged<T>()` applied for free once both
  methods were restructured to build into a temp `List<string>` first, then diff.

### Fourth live-testing round
- **Team names rendering as unspaced garbage** ("Ferrari26", "RedBullRacing26",
  "Audi26", "Cadillac26") — the 2026 Season Pack sends `Team` enum values with a "26"
  suffix (`Team.Ferrari26`, `Team.Mercedes26`, etc.) that `Models/TeamNames.cs` didn't
  recognize, falling through to the raw enum name. Also surfaced two teams new for
  2026 with no prior-season equivalent at all: `Team.Audi26` (Sauber's rebrand - there
  is no `Sauber26` value, just a new `Audi26` field) and `Team.Cadillac26` (brand new
  entrant). Fixed by adding all `*26` variants plus these two to the label map.
- **Player's own row in the Race Position Tower was too subtle to find at a glance**
  among 22 tightly-packed rows - just a faint background tint. Changed to a left-edge
  accent stripe (`#1F6FEB`, the same blue as the app's plain-action buttons, not used
  as a timing/status colour anywhere in this widget) plus a brighter blue-tinted
  background and bold driver name - confirmed via the same temp-debug-data +
  screenshot + remove-scaffolding pattern as the tower's original build.

### Fifth live-testing round (Sprint finish)
- **Final lap of a race/sprint never reached Lap History, and the SECTORS bar's S3
  stayed permanently blank after crossing the line** — root cause: lap completion was
  detected entirely via `LapData.CurrentLapNum` advancing, which never happens after
  the final lap (there's no next lap for it to advance to), so the one `if` block
  containing both the S3 registration and the `RegisterLapTime()` call for the
  just-finished lap was simply unreachable at the end of a session. S1/S2 populated
  correctly because those are driven by their own independent checks, not gated behind
  the lap-number branch - only S3 and the lap-history entry were affected.
  Fixed in `HandleLapData` by decoupling the two concerns: a new
  `CarLapTracker.LastSeenLastLapTimeMs` field is watched independently, so
  `LastLapTimeInMS` changing (which still happens on the final lap) triggers
  registration on its own, regardless of whether `CurrentLapNum` moved. The
  lap-number-advanced branch now only handles resetting sector tracking for the *next*
  lap - which is correctly skipped when there is no next lap.

### Sixth round (legibility feedback, no live game needed to verify)
- **Lap History sector times read as one long string at a glance** - the S1/S2/S3
  columns were 76px wide with no gap between them and the underline bar's own right
  margin was the only inset, so adjacent sector numbers sat almost flush against each
  other. Widened each sector column to 90px and moved the spacing onto the sector
  block itself (`Margin="0,0,16,0"` on the StackPanel, not just its underline) in both
  `LapTimingWidget.xaml`'s header row and per-lap row template.
- **Race Position Tower text too small to read mid-race.** Interval/Gap now format to
  2 decimals instead of the app-wide 3 (`TelemetryState.FormatDelta` gained an optional
  `decimals` parameter, defaulting to 3 everywhere else - only the tower's two call
  sites pass 2) - still precise enough for a glanced-at gap, and frees up column width
  to increase font size: Position/Driver name/Interval to 13px, Team name to 9.5px, Gap
  to 12.5px (kept a touch smaller than Interval, consistent with its existing muted/
  secondary treatment). Tower widened 300px → 320px to match. Verified via the same
  temp-debug-data + screenshot + remove-scaffolding pattern used for the tower's
  original build.
- **Qualifying's Position List (full 22-car field) forced the whole page to scroll**,
  pushing the player's own row (in this report, P22) below the fold entirely - the
  original row style (`Padding="6,5"`, 15px text, two full text lines per row) was
  spacious enough that 22 rows regularly exceeded the visible window height. Compacted
  to the same density as the Race Position Tower (`Padding="6,3"`, 13px driver/best/gap
  text, 9.5px team name, smaller livery swatch and `#` column) - the identical 22-row
  problem the tower already solved. Verified with a temp full-grid mock (all 22 real
  driver names, player row at P22) at the same maximized window size as the user's
  screenshot - no scrollbar, Bearman's row fully visible at the bottom.

### Seventh round (Chequered Flag cut short)
- **Chequered Flag banner appeared right on the finish line as designed, but only
  stayed visible ~3s instead of its coded 8s** - confirmed live, resolving the earlier
  open question about whether it fired at all (it did). Root cause: `HandleSession`
  calls `ResetSessionScopedState()` on any `SessionType` change, and that method
  unconditionally cleared `_isChequeredFlagActive` (and `_isRedFlagActive`) along with
  every other banner flag. The game's reported `SessionType` evidently moves on within
  a few seconds of the finish line (session transitioning toward results), so the
  reset was killing the banner almost immediately after it appeared - a rule meant to
  stop stale lap/tyre/sector data leaking across a session boundary was also erasing a
  banner whose entire purpose is to mark that exact boundary. Fixed by excluding Red
  Flag and Chequered Flag from `ResetSessionScopedState()` - both still self-clear via
  their own `DispatcherTimer` regardless, so this doesn't risk either sticking around
  indefinitely, it just lets them finish their own countdown across the transition.
  Penalty/Retirement/Team-mate-in-pits are deliberately still reset immediately there,
  since those are tied to a specific incident in the old session and would be
  genuinely stale carried into a new one - unlike a flag that's inherently about the
  session-ending moment itself.

### Eighth round (confirmations)
- **In Lap / Out Lap tagging confirmed correct** — `TelemetryState.ClassifyLapTag`'s
  approach (using the *previous* tick's `DriverStatus` rather than the current one, so
  the classification lands on the lap that just ended rather than the one just
  starting) had only ever been reasoned through from field semantics, never verified
  in-game. User confirmed the IN/OUT tags in Lap History are correct.

### Ninth round
- **Car Condition confirmed looking correct overall** across a full race (see the
  updated note above - doesn't specifically re-test the earlier race-start-creep
  finding, so the damage-threshold decision in §8 stays open).
- **Warnings only showed a bare count, not what they were for** - `TotalWarnings` in
  `LapData` is just a running total with no reason attached, so "Warnings: 2" told the
  driver nothing actionable if they missed the (transient, 8s) Penalty banner at the
  time. Fixed by capturing each warning's `InfringementType` from the `PenaltyIssued`
  event itself (already flowing in for the banner) into a new session-scoped
  `_warningReasons` list, and showing one itemized line per warning (e.g. "Warning -
  Ignoring Blue Flags") instead of the generic count. Falls back to a generic "+N more
  warning(s)" line for any gap between the event-based list and `TotalWarnings` (e.g.
  warnings that happened before the app connected this session), so the total shown
  always still reconciles with the game's own authoritative count.
- **Restarting a race (same session type) left Lap History and other session-scoped
  state from the previous attempt in place, producing a misaligned lap count** - root
  cause: `ResetSessionScopedState()` only fired on a `SessionType` *change*
  (`HandleSession`), so restarting a Race while still a Race never triggered it at all.
  Fixed by also comparing `PacketHeader.SessionUID` (a per-session-instance unique ID
  that changes on a restart even when `SessionType` doesn't) alongside `SessionType` -
  either changing now triggers the reset.

### Tenth round (visual polish, from a post-race screenshot review)
- **Position tower now shows "Out" for retired/DNF/DSQ/not-classified cars** instead of
  a stale zeroed interval/gap (`LapData.ResultStatus`) - matches the real broadcast's
  dimmed-row treatment (position, livery swatch, driver/team name all dim; no tyre
  letter shown).
- **Position tower now shows each driver's tyre compound** as a plain colored letter
  (no background badge, matching the real broadcast) - `CarStatusData.VisualTyreCompound`
  cached per car index (`_carTyreCompounds` in `TelemetryState`, populated for every
  car, not just the player, mirroring the existing participant name/team cache pattern)
  since it arrives on a different packet than the tower's own `LapData`-driven refresh.
  Tower widened 320px → 360px to fit.
- **Penalties & Flags now uses the same 2-column issue layout as Car Condition**
  (`UniformGrid Columns="2"`), for consistency.
- **Lap History**: S1/S2/S3 spaced further apart (was reading as one string); the
  IN/OUT tag moved off the sector times and given its own dedicated column right next
  to Time (previously shared Time's column, which shifted Time's start position on
  tagged rows only, breaking vertical alignment with every other row); tag badges made
  a consistent fixed size with centered text; a new "PIT" column shows `PitStopTimerInMS`
  on the IN row only. `PitLaneTimeInLaneInMS` on the OUT row was deliberately left out
  of scope - it would need to be captured a lap late (relative to when the pit lane
  traversal actually happened) and cached forward, and the official field comment
  ("if active, the current time...") left it unclear whether the value even survives
  that long. Capturing `PitStopTimerInMS` at the IN row's own creation tick avoids the
  whole question, since it's read at the same instant the row is built.

### Eleventh round (live race, first test of the tenth round's changes)
- **Tyre compound letter in the tower sat too close to the Gap column** - the letter
  was centered in its own narrow column immediately after Gap's right-aligned text,
  leaving barely any visual gap. Fixed with a left margin + left-alignment instead of
  centering, giving a consistent, deliberate gap.
- **PIT column showed `0.000` instead of the actual pit stop duration** - confirmed
  the tenth round's assumption wrong: `PitStopTimerInMS` does not survive from pit
  exit to the IN lap's line-crossing, it reads back as 0 well before then. Fixed by
  latching the peak value at the moment `PitStatus` transitions back to `None` (pit
  exit) instead of reading it later at lap completion - see §6 for the updated,
  still-unconfirmed status of this fix.

### Twelfth round (further tower polish)
- **Int-Gap-Tyre spacing still looked uneven** even after the eleventh round's fix -
  the Int-Gap gap came from Gap's own right-alignment slack (variable, depends on that
  row's text width), while the Gap-Tyre gap was a fixed margin, so the two never
  actually matched. Fixed by removing reliance on alignment slack entirely: two real,
  equal-width (16px) spacer columns now sit between Int-Gap and Gap-Tyre, with Int and
  Gap's own columns tightened to a snug fit (58px each, still fits a 3-digit-second gap
  like "+199.90" with no clipping). Tower widened 360px → 384px to fit.
- **New: pending-penalty badge** - a small red "!" badge next to the tyre letter for
  any driver with a penalty not yet served, matching the real broadcast convention
  (reference screenshot provided by the user). Driven by the same fields
  `RefreshPenalties` already reads for the player's own Penalties & Flags list
  (`LapData.Penalties`, `NumUnservedDriveThroughPens`, `NumUnservedStopGoPens`),
  generalized to every car in `RefreshRaceStandings` instead of just the player. False
  for `IsOut` rows - a retired car has nothing left to serve. Uses the tower's reserved
  column (see eighth/tenth round comments) plus one more real spacer column matching
  the Int-Gap-Tyre spacing, so there's no longer any reserved buffer left - see below.
  Tower widened 384px → 400px to fit. Not yet tested against a real pending penalty.

### Thirteenth round (live race - PIT column still showed nothing)
- **PIT column was still blank on the IN row despite the eleventh round's fix** - the
  user's own diagnosis nailed it: on at least some tracks the pit lane sits entirely
  within the NEXT lap (the OUT lap), not the IN lap - so by the time the stop actually
  finishes, the IN row was already built a full lap earlier, blank, with nothing left
  to latch a forward-stored value into. Confirmed a better fit than the eleventh
  round's same-tick race-condition theory precisely because it explains 100% failure
  rather than an intermittent one. Fixed by testing this theory in isolation: the IN
  row's stop time (`PitStopTimerInMS`) is no longer passed in at row-creation time at
  all - `PatchMostRecentInRowPitTime` retroactively replaces `LapHistory[0]` once the
  stop actually finishes, if it's still the blank IN row (see §6 for what this doesn't
  yet rule out).
- **New: OUT row now shows total pit-lane time** (`PitLaneTimeInLaneInMS`, entry to
  exit - a bigger number than the IN row's stationary box time) in the same "PIT"
  column - a row is never both IN and OUT, so no new column was needed. Unlike the IN
  row, this one has no timing ambiguity to test: the pit lane is always exited before
  the OUT lap even starts being driven, so the value is always ready well before that
  row can possibly exist. Plain forward-store via `CarLapTracker.PendingPitLaneTimeMs`,
  consumed the same way the IN row's stop time originally was before this round's fix.
- **New: lap counter banner** ("LAP 41 / 53") added to the top of the position tower,
  its own distinct bar above "POSITION" (matching the real broadcast's layered graphic
  more closely than folding it into an existing label row). Tracks the race leader's
  `LapData.CurrentLapNum` (captured in `RefreshRaceStandings`, which already loops the
  whole field), not the player's own - a lapped player's own lap count can trail behind
  where the race as a whole stands. `SessionDataPacket.TotalLaps` cached in
  `HandleSession`.
- **Retired cars showed "Out Out"** - both `IntervalText` and `GapText` independently
  resolved to `"Out"`, rendering as two separate labels side by side. Fixed by leaving
  `IntervalText` blank and keeping the single `"Out"` in `GapText` only, matching the
  real broadcast.
- **Penalty/warning text rewritten against real FIA terminology** - `InfringementType`
  (55 values) was previously auto-generated by splitting the enum's PascalCase name
  into words, which was readable but not necessarily how the FIA actually phrases
  things. Hand-labelled all 55 in `Models/EventLabels.cs`, sourced from the FIA's
  published F1 Penalty Guidelines and Driving Standards Guidelines (collision severity
  tiers, "leaving the track and gaining a lasting advantage" for corner-cutting,
  "impeding" under Article 37.5, "unsafe release", "parc fermé" breaches, safety car
  delta infringements). A handful of values are game-mechanic-only concepts with no
  real FIA equivalent at all (flashback used, reset-to-track, retry penalty, league
  grid penalty) - those are labelled plainly rather than forcing fake FIA phrasing onto
  something that was never a real infringement category. Falls back to the old generic
  humanizer only for any future value not yet reviewed.
- **New: fastest-lap badge** - a purple circular stopwatch badge (drawn, not captured,
  matching the icon convention used elsewhere) for whoever currently holds the
  session's fastest lap, reusing the existing purple = "fastest of the session"
  convention (Lap Timing colours). Determined from `_carBestLapMs` (already maintained
  per car for the qualifying position list's gap-to-leader calculation - no new
  tracking needed) by finding whichever car index has the minimum value. Shares the
  tower's pending-penalty badge column rather than adding a new one - the two are
  mutually exclusive **by design decision, confirmed with the user**: a pending penalty
  wins over fastest lap when a driver has both, matching the priority order the alert
  banner already uses elsewhere (actionable/penalty states rank above purely
  informational ones). The exclusivity is resolved at the data source
  (`RefreshRaceStandings` sets `IsFastestLap = false` whenever `IsPenaltyPending` is
  true for that row), not in the view - the two badge Borders in XAML just have
  independent `Visibility` bindings and never actually collide.

### Fourteenth round (window chrome, no live game needed to verify)
- **Connection bar and preset tabs merged into one row** (`MainWindow.xaml`), reclaiming
  vertical space for the widget area without touching any widget's own layout. Was two
  stacked `Auto` rows (~99.6 DIP total, calculated from the actual XAML padding/margin/
  font-size values and cross-checked against a real pixel measurement of an unrelated
  element - see the tenth-round-era measurement work). Now one row: connection controls
  (Port/Connect/Status) pinned left, preset tabs *truly centered on the whole row* (not
  just centered in whatever space is left over) via a 3-column grid with matching
  `Width="*"` on the outer two columns and `Width="Auto"` on the middle - the settings
  gear pinned right. Reclaims ~58px in the common case.
- **`ErrorText` no longer permanently reserves a row of blank space.** It defaulted to
  `Visibility="Visible"` with empty text, which is the common case (errors are rare) -
  meaning ~23 DIP was wasted on nothing almost all the time. Now starts `Collapsed` in
  XAML; a new `MainWindow.SetError(string)` helper toggles `Visibility` alongside
  `Text` at all three call sites that used to set `ErrorText.Text` directly
  (`ConnectButton_Click`, `Connect()`'s two failure paths, and the listener's
  `ErrorOccurred` handler) - only reserves space when there's an actual error to show.
- **Port textbox and Connect/Disconnect button looked taller than the preset tabs** -
  `TextBox`/`Button` both carry their own template chrome (focus rings, borders) that
  renders taller than a plain `Border`+`TextBlock` tab even with visually-similar
  `Padding`. Fixed by giving every interactive element in the row the same explicit
  `Height="24"` (PortTextBox, ConnectButton, the three preset tab Borders) rather than
  relying on Padding alone to coincidentally match across different control types. The
  settings gear (`IconToggleButtonStyle` in `App.xaml`) was resized to match too -
  24x24 (`CornerRadius="12"` to stay circular), down from its original 32x32.
- **Vertical spacing between widgets was inconsistent** - four different gap values
  were in play (10px header-to-widgets, 14px tower-to-right-column, 12px between most
  widgets, and an unintended 18px between Lap Timing/Position List and the catalog
  widget grid, caused by Lap Timing/Position List's own one-sided `Margin="0,0,0,12"`
  compounding with the catalog widgets' `Margin="6"` on all sides: 12+6=18 instead of
  matching). Standardized everything to 12px: header row and `ErrorText` margins
  changed `...,10` to `...,12`; the tower's right margin changed `0,0,14,0` to
  `0,0,12,0`; Lap Timing/QualifyingLapTiming's bottom margin reduced to `0,0,0,6` and
  Position List given a new top margin `0,6,0,6`, so paired 6+6 gaps match the 12px
  used everywhere else without disturbing the invariant that whichever widget is
  visually first in the right column (Alert or Lap Timing) still sits flush with no
  top margin. Verified via screenshot on both the Race and Qualifying presets.

### Fifteenth round (row-count limits, no live game needed to verify)
- **Lap History depth increased from 10 to 12 rows** (`LapHistoryDepth` in
  `TelemetryState.cs`). No XAML changes needed - the widget has no fixed height, it
  just grows and the outer `ScrollViewer` picks up the extra rows.
- **Car Condition and Penalties & Flags issue lists capped at 5 rows.** Both lists are
  otherwise unbounded (a wrecked car or a heavily-penalized session could list a
  dozen items), which would blow past the catalog grid's shared row height. New
  shared `IssueListMaxRows = 5` constant and `CapIssueList()` helper in
  `TelemetryState.cs`, applied after computing `CarConditionIsOk`/`PenaltiesIsOk` from
  the *un*truncated count (so the OK/issue state itself is never affected by the cap)
  but before assigning into the bound `ObservableCollection`. When truncated, the last
  visible slot becomes a `"+N more"` summary rather than silently dropping items.
  Verified via temporary debug scaffolding (removed before commit) seeding 6-7 fake
  issues into each list and confirming the 5-row-plus-overflow rendering.
- **Both capped lists are now severity-sorted, most urgent first**, instead of a fixed
  source-code order. Each issue is built as a `(Severity, Text)` tuple and the list is
  `OrderByDescending(Severity)` before the cap above is applied - so if a car has more
  than 5 things wrong, the 5 that survive the cap are the *worst* 5, not just whichever
  happened to be checked first in code.
  - Car Condition: percentage-damage items rank by their own damage value (0-100);
    DRS/ERS fault (101) and engine blown/seized (102) are fixed above the damage range
    since a broken system or a dead engine matters more than any amount of cosmetic
    wing/floor damage.
  - Penalties & Flags: unserved stop-go (100) > unserved drive-through (90, costs
    less time than stop-go) > already-applied time penalties (80) > individual
    tracked warnings (50) > the generic untracked-warnings overflow line (40).
  Verified via temporary debug scaffolding (removed before commit) seeding
  deliberately out-of-order fake issues into both lists and confirming they rendered
  in the expected severity order.
- **Fixed the catalog grid's OUTER edges not lining up with Lap Timing/Position List
  above it.** Each catalog widget (Session & Track, Tyres, Car Condition, Penalties &
  Flags) carried a static `Margin="6"` on all four sides in XAML. That 6px is correct
  for the gap BETWEEN two widgets sharing a row (6+6=12, matching the gap used
  everywhere else), but it also applied to the OUTER left/right edges of the whole
  2x2 grid - which Lap Timing/Position List don't have (they carry no horizontal
  margin at all, so they span the full column width). Net effect: the combined width
  of the 2-column catalog grid was 12px narrower than Lap Timing's width, inset 6px
  from each side, reading as visibly misaligned rather than sharing the same left/right
  edges. Fixed by moving margin assignment out of XAML (removed `Margin="6"` from all
  four widget tags in `MainWindow.xaml`) and into `ArrangeWidgets`
  (`MainWindow.xaml.cs`), which now sets each widget's `Margin` per-instance based on
  its actual column position each time the grid is rebuilt: 0 on a widget's outer-
  facing side (leftmost widget's left edge, rightmost widget's right edge), 6 on the
  side facing a neighbour. A static XAML value couldn't do this because which widget
  ends up leftmost/rightmost is dynamic (depends on which widgets are currently
  toggled on). Top/bottom margins are untouched (still a constant 6 top and bottom on
  every widget, which was already correct - see the Fourteenth round spacing fix).
  Verified by pixel-measuring the Sectors row (representing Lap Timing's full width)
  against the catalog grid's row - both now span the exact same x-range, confirmed
  before and after via temporary debug scaffolding (removed before commit) to force
  the Race preset with all 4 widgets visible.

## 6. Not yet trustworthy / unvalidated

- **Auto-update (Velopack): install itself confirmed live, self-update round-trip
  still isn't.** `v1.0.0`'s `Setup.exe` installed and ran correctly on the project
  owner's own machine. `v1.0.0` is necessarily the FIRST release though, so this
  can't test the actual update path - there's nothing older to update FROM yet. True
  test: cut a `v1.0.1`+ release, then confirm an already-installed `v1.0.0` copy
  detects it, silently downloads, applies, and restarts on its own next launch. Do
  that once and confirm live before treating auto-update as reliable for a friend to
  depend on. One install-time gotcha hit and fixed: Velopack's default install
  directory is `%LocalAppData%\F1RaceEngineer` - the exact same path the now-removed
  Track Map feature used for its `trackmaps\{Track}.json` cache (see §7 "Track Map -
  built, then removed"). A leftover cache file from old testing made that folder
  already exist, so `Setup.exe` reported "already installed" (a false positive - no
  registry Uninstall entry existed, confirmed via a real registry check) instead of
  doing a fresh install. Fixed by deleting the stray folder before re-running
  `Setup.exe`. Not expected to recur (nothing writes to that path anymore since Track
  Map was removed), but worth knowing if a similarly-named local cache folder ever
  reappears under a future feature.
- **Red Flag auto-clear is an untested heuristic.** There is no confirmed "flag
  cleared" event in the API, so it shows on its trigger event and auto-hides after a
  15s timeout. NOT yet tested against a real red flag. Safety Car / VSC, by contrast,
  are driven continuously by `SessionDataPacket.SafetyCarStatus` and self-clear
  correctly — **confirmed live** (VSC triggered and displayed correctly in a real
  race). Chequered Flag's own 8s timeout **is now confirmed correct** (see §5, seventh
  round) - it was firing right on the finish line all along, just getting cut short by
  an unrelated bug, now fixed. The same fix was applied to Red Flag pre-emptively
  since it shares the identical mechanism, but that half is still unconfirmed against
  a real red flag specifically.
- **`CarPosition == 0` is used to filter inactive car slots** in the position list —
  a reasonable inference from the API's general pattern, not explicitly documented.
- **Tower's pending-penalty badge (§5, twelfth round) is reasoned from field semantics,
  not yet re-confirmed live.** `Penalties > 0` / unserved drive-through / unserved
  stop-go are read directly from `LapData` and already used elsewhere for the player's
  own Penalties & Flags widget, but generalizing them to every car in the tower hasn't
  been checked against a real pending penalty on a rival car. Watch the next penalty
  situation specifically.
- **Final-lap registration fix (§5, fifth round) is reasoned from the reported symptom,
  not yet re-confirmed live.** The fix assumes `LastLapTimeInMS` reliably changes value
  on the final lap even though `CurrentLapNum` doesn't advance - consistent with what
  was observed, but not independently verified against a second real race/sprint
  finish. Watch the next session's final lap specifically.
- **Session-restart fix (§5, ninth round) is reasoned from the reported symptom and a
  confirmed field (`SessionUID`), not yet re-confirmed live.** Comparing `SessionUID`
  alongside `SessionType` should catch a same-type restart that a `SessionType`-only
  check misses, but hasn't been independently re-tested against a second deliberate
  restart. Watch the next session restart specifically.
- **Lap History's IN-row stop time (§5, thirteenth round) tests one specific theory,
  not yet re-confirmed live, and by design does NOT cover every track.** Two rounds of
  live testing (eleventh, thirteenth) each ruled out a prior theory for why the PIT
  column stayed blank on the IN row - first that `PitStopTimerInMS` survives to the
  line-crossing tick (it doesn't), then that the same-tick race condition was the whole
  story (still blank after that fix too). Currently live: retroactively patching
  `LapHistory[0]` when the stop finishes, on the theory that the pit lane sits within
  the OUT lap rather than the IN lap on at least some tracks. This is deliberately an
  isolated test of ONE theory (per explicit user request) - if some tracks turn out to
  have the pit lane genuinely within the IN lap after all (the original tenth-round
  assumption), this patch-only version would need a forward-store path added back
  alongside it to cover both. Watch the next pit stop specifically - if it's still
  blank, this theory is also ruled out and the search continues.
- **OUT row's total pit-lane time (§5, thirteenth round) has no theory to test - not
  yet re-confirmed live, but for a much more mundane reason (just hasn't been watched
  yet).** Unlike the IN row, there's no timing ambiguity here: the pit lane is always
  exited before the OUT lap starts, so the value is always ready in time. Simple
  forward-store, same shape as the original (superseded) IN-row design. Watch the next
  pit stop to confirm the number itself is plausible (matches what the game/broadcast
  would show for total pit lane time, not just that a number appears at all).
- **Lap counter banner (§5, thirteenth round) is reasoned from confirmed fields
  (`SessionDataPacket.TotalLaps`, the leader's `LapData.CurrentLapNum`), not yet
  re-confirmed live.** Should track the race's overall lap count correctly regardless
  of whether the player is lapped, but hasn't been watched against a real race to
  confirm the leader-tracking logic picks the right car every tick, especially around
  a lead change.
- **FIA-aligned penalty text (§5, thirteenth round) is sourced from published FIA
  guidelines and news coverage summarizing them, not a line-by-line official glossary
  cross-referenced against the game's exact 55 `InfringementType` values.** The
  general-purpose FIA categories (collisions, corner-cutting, safety car, unsafe
  release, parc fermé) are grounded in real quoted terminology; the more granular
  game-specific buckets (e.g. the three-tier "ran wide, gained time: minor/significant/
  extreme") are phrased consistently with that terminology but aren't themselves
  quoted from an FIA source verbatim. Worth a read-through against real in-game
  warning/penalty text next time one fires, to catch anything that reads oddly.
- **Fastest-lap badge (§5, thirteenth round) is reasoned from a field already confirmed
  correct elsewhere (`_carBestLapMs`, used by the qualifying position list), not yet
  re-confirmed live for this specific use.** Should correctly track whoever currently
  holds the session's fastest lap, including switching rows as the record changes
  hands mid-race, but hasn't been watched against a real fastest-lap swap. The
  penalty-wins-over-fastest-lap priority rule is a confirmed design decision (asked and
  answered with the user), not an assumption - only the underlying data/tracking needs
  live confirmation, not the priority behaviour itself.
- **Car Condition previously showed background engine wear as if it were crash
  damage — fixed after live testing.** `EngineMGUHWear`/`EngineESWear`/`EngineCEWear`/
  `EngineICEWear`/`EngineMGUKWear`/`EngineTCWear` are a *different* concept from
  `EngineDamage`/`GearBoxDamage`/wing/floor/diffuser/sidepod damage: they're the game's
  gradual engine-component wear that accumulates over every race as a strategic
  part-degradation mechanic (eventually forcing a grid penalty for new parts), not
  damage from an incident. Including them meant the widget showed "damage" on every
  single lap of every race regardless of what actually happened. They've been removed
  from the issue list entirely; only genuine incident-type damage fields remain. The
  genuine damage fields (e.g. both front wings reading 100%) matched a real opening-lap
  wing-destroying incident alongside a VSC in one live test — but a *second* live test
  found these same fields climbing from race start with zero collisions, so "genuine
  damage field" is no longer a safe assumption on its own. A *third* live test (see §5
  ninth round) reported the widget looking correct overall across a full race - doesn't
  specifically contradict the race-start-creep finding (that scenario wasn't retested),
  so the open decision in §8 (Car Condition damage threshold) stays open, just with one
  more data point in its favour.

## 7. Full agreed design spec

### Presets & switching
- 3 presets: Practice, Qualifying, Race — auto-selected from `SessionType`.
- Sprint Qualifying → Qualifying preset; Sprint Race → Race preset.
- Time Trial → intentionally unsupported for now (not a bug).
- Layout is fixed per preset; individual widgets toggle on/off within a preset via a
  gear-icon **settings panel** (built — `Widgets/SettingsPanel.xaml`, a `Popup` off the
  gear `ToggleButton` in the connection bar). Main racing view stays clean until the
  panel is opened.
- When a widget is toggled off it is removed and remaining widgets **grow to fill the
  freed space** (no blank gaps) — implemented in `MainWindow.xaml.cs` `ArrangeWidgets`
  as a row-based reflow (not a single fixed-column grid): widgets are packed into rows,
  and the column count *within each row* equals however many widgets are actually in
  that row, so a partial last row stretches evenly across the full width instead of
  leaving a dead gap. Column count itself scales with total widget count
  (`ColumnsFor`: 1 → 1 col, 2–4 → 2 cols, 5+ → 3 cols) so the common "Lap Timing + 4
  others" Race case stays a clean 2×2, while enabling 5+ widgets folds into 3 columns
  rather than growing a 2-column stack ever taller — keeping row count low so the page
  stays glanceable without scrolling on a second-screen viewport. Rows are auto-height
  (not stretched), so a short one-line widget (e.g. Car Condition when "OK") hugs its
  own content instead of stretching into an empty-looking card next to a taller
  neighbour.
- Lap Timing's history list always renders exactly `LapHistoryDepth` (12) rows (blank
  placeholders before 12 laps exist) rather than growing from 0 — otherwise the
  widget's height, and everything stacked below it, would visibly shift down
  lap-by-lap during a race.
- Catalog widgets default **on** for Race only. Practice and Qualifying default to just
  their core (Lap Timing for Practice; history-less Lap Timing + Position List for
  Qualifying) with everything else off, toggleable if wanted. Lap History itself is now
  a toggle too (on by default for Practice/Race; not applicable to Qualifying, which
  always uses the fixed history-less Lap Timing instance).

### Always-on core (differs per preset)
- Practice / Race: **Lap Timing** (with history).
- Qualifying: **Position list** as the core, with a history-less **Lap Timing**
  widget stacked on top. This is settled, not a placeholder: the settings panel is
  built, but Qualifying's Lap Timing was deliberately left non-toggleable so the view
  always stays focused on the hotlap - only the 4 catalog widgets (and Lap History,
  for Practice/Race) are toggleable.

### Lap Timing colour convention (real F1 timing-screen convention, confirmed)
- **Purple** = fastest of the session (any driver).
- **Green** = personal best.
- **Yellow** = slower than this driver's own personal best.
- **Neutral/white** = not yet set.
- The running current-lap timer is also live-coloured provisionally, updating at each
  completed sector boundary (not continuously interpolated within a sector — a
  deliberate simplification, looks slightly "stepped").

### Race Position Tower (Race-only, not part of the toggleable catalog)
- A "LAP X / Y" banner (§5 thirteenth round - tracks the race leader's `CurrentLapNum`
  and `SessionDataPacket.TotalLaps`, not the player's own lap count) sits above the
  full-field tower shown to the left of everything else in Race: position, livery
  swatch, driver + team, Interval (gap to car ahead), Gap (to leader), tyre compound
  letter (plain colored text, no background badge - see §5 tenth round), and one shared
  badge slot for either a red "!" pending-penalty badge (§5 twelfth round) or a purple
  stopwatch fastest-lap badge (§5 thirteenth round) - mutually exclusive by design, a
  pending penalty always wins if a driver has both (confirmed with the user, matches
  the alert banner's own actionable-beats-informational priority elsewhere in the app).
  Int-Gap and Gap-Tyre/Badge gaps are real fixed-width spacer columns, not alignment
  slack, so they stay visually identical regardless of content width (§5 twelfth round)
  - no reserved/unused column left in the tower after this. Retired/DNF/DSQ/
  not-classified cars show a dimmed row and a single "Out" instead of a stale
  interval/gap (`LapData.ResultStatus`, §5 tenth and thirteenth rounds - the field was
  originally set on both Int and Gap, rendering as "Out Out"). See §8 for why this
  replaced the old Gaps & Position widget (removed on all presets, not just Race).

### Widget catalog (toggleable unless noted)
- **Tyres** — rendered compound icon (FIA colour convention, drawn not captured),
  tyre age, four-corner wear diagram. NOTE: wear is genuinely per-corner
  (`FrontLeft/FrontRight/RearLeft/RearRight` differ) — must not be collapsed to one
  number.
- **Car Condition** — quiet checkmark + "No damage" when undamaged (deliberately NOT
  a loud coloured banner - see §6 fix); otherwise each damaged component listed in
  yellow (wings, floor, diffuser, sidepod, gearbox, engine, per-corner brakes,
  per-corner tyre blisters, DRS/ERS faults, engine blown/seized), laid out as a
  2-column grid (`UniformGrid Columns="2"`) so a long issue list doesn't force the
  widget taller than the viewport - see §5. Capped at `IssueListMaxRows` (5) rows,
  most-severe-first (a car's genuinely worst 5 problems survive the cap, not just
  whichever were checked first in code), with a `"+N more"` summary row if truncated
  - see §5 fifteenth round. Background engine-component *wear* (as
  opposed to damage) is deliberately excluded - see §6; per-corner tyre *damage* is
  also excluded now (see §8) since the Tyres widget already shows per-corner wear and
  the two read as duplicated, not distinct, information.
- **Penalties & Flags** — same quiet/yellow-itemised, capped-and-severity-sorted
  pattern as Car Condition (same `IssueListMaxRows`/2-column grid, see §5 tenth and
  fifteenth rounds); also shows the player's own current flag (`VehicleFiaFlags`:
  None/Green/Blue/Yellow) as a coloured chip with label (e.g. blue = let car through).
  Warnings show one itemised line per specific reason (from `PenaltyIssued` events'
  `InfringementType`), not a bare count - see §5 ninth round. Reason text is
  hand-labelled against real FIA terminology (`Models/EventLabels.cs`), not the
  generic PascalCase humanizer - see §5 thirteenth round and the caveat in §6.
- **Session & Track** — rendered weather icon (from the 6-value `Weather` enum, drawn
  not captured), track/air temp, current rain %, PLUS a single **forecast change**
  callout (see §5 - replaced the original multi-card forecast strip).

### Track Map — built, then removed
A self-recorded track outline (from the player's own `WorldPositionX/Z`) was built,
matching the original design intent (the UDP protocol has no track geometry of any
kind, confirmed against the full API reference). After a live test the user found it
"not working as intended" and deemed it useless, so it was removed entirely —
`Widgets/TrackMapWidget.xaml(.cs)`, `Telemetry/TrackMapRecorder.cs`, and
`Models/MarshalZoneDisplay.cs` are deleted. It was fully self-contained (its own
`Motion`/`Session`/`LapData` subscriptions via `Attach(listener)`, no shared state with
`TelemetryState`), so removing it did not touch any other widget's telemetry. Not
re-attempted without a clearer idea of what "not working as intended" specifically
meant (the recording never completing? an inaccurate outline? something else) — ask
before rebuilding a version of this.

### Alert banner (persistent, not a toggleable widget)
- Safety Car / VSC: amber/yellow, differentiated by label and icon (real flag
  convention uses yellow for both — do not invent a separate VSC colour).
- Red Flag: red. Chequered Flag: neutral black/white, informational.
- Penalty (own car only, via `EventType.PenaltyIssued`): amber, e.g. "5s time penalty -
  Corner Cutting Overtake Single". Timed, clears after 8s (no "penalty acknowledged"
  event exists to clear it on).
- Retirement (own car only, via `EventType.Retirement`): red, e.g. "Retired - Mechanical
  failure". NOT timed - persists until the next session change, since nothing further
  happens for a retired player this session.
- Team-mate in pits (via `EventType.TeamMateInPits`): neutral (same `AlertNeutralBg`/
  `AlertNeutralText` as Chequered Flag), e.g. "Team-mate Sainz is in the pits". Timed,
  clears after 6s.
- Priority when multiple are true at once (highest first): Red Flag → Retirement →
  Penalty → Safety Car/VSC → Team-mate in pits → Chequered Flag.
- `Models/EventLabels.cs` hand-labels `PenaltyType`, `ResultReason`, and (as of §5
  thirteenth round) all 55 `InfringementType` values - the last of these was originally
  a generic PascalCase-to-words humanizer, replaced with FIA-aligned wording (see §5/§6
  for sourcing and confidence). The humanizer still exists as a fallback for any future
  enum value not yet reviewed, not as the primary path anymore.
- **`Collision` was implemented then removed** — the user judged it only matters when
  it results in a penalty, which the Penalty banner already covers on its own. Don't
  reintroduce it as a separate banner.
- Of the 21 `EventType` values, only 5 drive a banner so far (Red Flag, Chequered Flag,
  Penalty, Retirement, Team-mate in pits). Most of the rest were reviewed and left
  unwired -
  see §8 (Proposed alert banners, not yet implemented) for the full draft-text pass.

### Identity in the position list
- Livery colour swatch (RGB straight from `ParticipantData.LiveryColors`), chosen
  over nationality flags (which would need an external flag-icon asset set) and over
  team logos (trademarked, unavailable). This was an explicit user choice.

## 8. Deferred work (agreed, not yet built)

- **Optimal pit lap** — was in the original ask; needs a tyre-degradation model, not
  just a data readout. Consciously deferred.
- **Tyre stint timeline in Lap History** — the user pointed out the empty horizontal
  gap in each Lap History row (originally between the sector columns and the Time/Delta
  columns) and asked to log this idea rather than build it yet. **Note: that gap is
  smaller now** - §5's tenth round added a dedicated tag column and a "PIT" column in
  what used to be open space, so this would need a fresh look at where an overlaid bar
  could actually sit before building it. Modelled on the real F1 broadcast tyre-strategy
  graphic (per-driver horizontal bar, colour-coded by compound, circular compound-letter
  markers at each pit stop). Scoped-down version analyzed and confirmed feasible with
  existing data:
  - `SessionDataPacket.TotalLaps` gives race distance at session start, solving the
    user's "shorter races need different axis scaling" concern with no guessing.
  - Stint-change detection: `CarStatusData.TyresAgeLaps` resetting (more robust than
    watching `VisualTyreCompound` alone, since it also catches a same-compound re-fit).
  - Colours: reuse `Models/CompoundPalette.cs` as-is.
  - Scope: player's own car only (one bar, not a per-driver grid like the broadcast
    version) - the Race Position Tower (§7) is now the one place full-field rival data
    is shown; this stint timeline should stay player-only rather than duplicating that.
    Race preset only - `TotalLaps` isn't meaningful in Practice/Qualifying.
  - Layout: Lap History always renders exactly `LapHistoryDepth` (12) rows including
    blank placeholders specifically so the widget's height never shifts - that fixed
    height is what makes a single overlaid bar (not duplicated per row) realistic,
    placed as a sibling element in the same grid cell as the lap rows.
  - Open questions before building: whether to include axis gridlines/lap-number
    labels like the real graphic or keep it to just the coloured bar + letter markers,
    and whether the open/current stint needs an explicit "current lap" tick mark.
- **Car Condition damage threshold** — confirmed via live testing that damage fields
  (wings/floor/gearbox/engine) fill in at race start with zero collisions (§5, §6), so
  low values on their own aren't reliable evidence of a real incident. (Per-corner tyre
  damage was also in this bucket but is now moot - removed entirely, see §5 third
  round.) Proposed fix: only surface a component once it crosses a minimum threshold
  (~10% suggested starting point) instead of the instant it reads non-zero. Two open
  questions before building: (1) the exact threshold value, (2) whether it applies
  uniformly across all components or per-component (e.g. wings/floor might creep
  faster than gearbox).
- **Proposed alert banners, not yet implemented** — of the 21 `EventType` values, 16
  remain unwired (5 are live - see §7). Draft text/colour/clear-behaviour for each,
  reviewed but not committed to:
  - **Safety Car event state machine** (would replace the current coarse
    `SessionDataPacket.SafetyCarStatus` poll with the dedicated `SafetyCarEvent`,
    which has a real Deployed/Returning/Returned/ResumeRace sequence): Deployed+Full →
    "Safety car deployed - no overtaking" (event-based); Deployed+Virtual → "Virtual
    safety car - hold your delta" (event-based); Returning+Full → "Safety car in this
    lap - peel off" (event-based); Returning+Virtual → "Virtual safety car ending"
    (event-based); Returned (either) → "Safety car in the pits" (timed, 5s);
    ResumeRace → "Racing resumes" (timed, 4s).
  - **DRS Disabled** (`DrsDisabledReason`): WetTrack → "DRS disabled - wet track"
    (event-based, pairs with `DRSEnabled`); SafetyCarDeployed / RedFlag → likely
    redundant, those banners already show; MinLapNotReached → "DRS disabled - minimum
    lap not reached" (event-based).
  - **Start Lights / Lights Out**: NumLights 1→5 → a filling row of dots (event-based
    sequence); LightsOut → "Lights out - go go go!" (timed, 2s).
  - **Overtake**: made → "Overtake - passed {name}" (timed, 4s); lost → "Position lost
    - passed by {name}" (timed, 4s).
  - **Fastest Lap**: you → "Fastest lap - {time}" (timed, 6s); rival → "Fastest lap -
    {name} {time}" (timed, 6s).
  - **Speed Trap**: session best → "Speed trap - {speed} km/h - session best" (timed,
    5s); personal best → "Speed trap - {speed} km/h - personal best" (timed, 5s);
    ordinary → not shown, too frequent to be useful.
  - **Race Winner** → "{name} wins the race" (timed, 10s).
  - **Drive-Through / Stop-Go Served** → "Drive-through penalty served" / "Stop-go
    penalty served ({time}s)" (timed, 5s each).
  - **Not recommended**: Flashback (self-inflicted, driver already knows) and Button
    Status (raw controller bitmask, not a discrete banner-shaped event) - reviewed and
    rejected, don't reconsider without new reasoning.
  - **Not actually assessed until this doc pass** (found while re-verifying the count
    above against the API reference - the earlier "20 values" figure had silently
    dropped these three): `DRSEnabled` doesn't need its own banner - it's already the
    implicit clear-signal for the `DRSDisabled` draft above, not a notable moment on
    its own. `SessionStarted` is low-value - Start Lights/Lights Out already covers
    the moment that matters (race about to begin) more specifically. `SessionEnded`
    is the one worth a real look later: unlike Chequered Flag (which may be
    Race-specific), this might fire for Practice/Qualifying too, and neither currently
    has any "session's over" signal at all - untested, not drafted yet.

## 9. Non-negotiable technical requirements

- Responsive/proportional WPF layout: relative sizing (Grid star-columns, Viewbox
  where appropriate), DPI-aware, no hardcoded pixel dimensions — must scale cleanly
  on 2K/4K monitors. (Not yet audited across all widgets.)
- Motion packet is currently unused (it only fed the removed Track Map's
  `WorldPositionX/Z`). Its G-force / suspension / velocity fields remain unused too.
- Keep the full data layer comprehensive even where the UI doesn't yet show
  everything.

## 10. How the user wants to work (applies to continuation too)

- **Ask for explicit confirmation before packaging/committing a build** — the user
  often has more feedback before a build.
- **Do a self-check pass before delivering**: every XAML namespace prefix declared,
  every `{Binding}` path matches a real property, every `{StaticResource}` key
  defined, every type reference has a `using` and matches the API reference dump.
- **Be direct and factual. Never assume — flag anything that can't be confirmed.**
- The user tests against the live game and reports back; runtime (in-game) bugs can't
  be caught without them. Design for that loop.
