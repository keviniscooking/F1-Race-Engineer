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
  of the same base packets).
- **Build/run:** `dotnet build` / `dotnet run` from the project root.
- **Version control:** in git as of this point, with a **private** GitHub remote
  (`https://github.com/keviniscooking/F1-Race-Engineer`). `bin/`, `obj/`, and
  `.claude/settings.local.json` are gitignored - the last one is this machine's local
  Claude Code permission settings, not project content.
- **Launching without VS Code:** `dotnet build -c Release` produces
  `bin\Release\net10.0-windows\F1RaceEngineer.exe`; a desktop shortcut points at it.
  Re-run that command after future changes to refresh it in place - the shortcut
  doesn't need to change. `AppIcon.ico` (project root) is a custom-drawn icon, not a
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
  for in-laps/out-laps, per-sector times each with a colour-coded underline, lap time,
  and delta, one lap per row. Confirmed rendering correctly, including the IN/OUT
  classification itself — **confirmed live**.
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
- **Race Position Tower** (`Widgets/RacePositionTowerWidget.xaml`) — full-field tower
  shown only in Race, to the left of everything else: position, livery swatch, driver +
  team, Interval (gap to car ahead), Gap (to leader). Reads `LapData.DeltaToCarInFrontInMS`/
  `DeltaToRaceLeaderInMS` directly per car - no cross-referencing needed, unlike the
  old Gaps & Position widget it replaced (see §8 for why that one was removed).
- **Adaptive grid layout** (`MainWindow.xaml.cs` `ArrangeWidgets`) rebuilds the catalog
  widgets' row/column definitions to fit exactly the currently-visible set on every
  toggle change, so remaining widgets grow to fill freed space with no blank gaps.
  Widget key order is grouped by content density (denser widgets grouped together,
  compact ones grouped together) so rows sharing a card height don't mismatch as badly.

Confirmed working in-game across seven full live-testing rounds by this point (see §5
for the complete log of what each round found and fixed): lap/sector timing and
colouring, preset auto-switch, both position displays (Qualifying's list and the Race
tower) with real names/livery/team, live delta, personal best, the alert banner
(including VSC and, as of round seven, Chequered Flag's timing), the full catalog
widget set, and, as of round eight, Lap History's IN/OUT tagging. Still open: the
final-lap registration fix (§5 round five - reasoned from a symptom, not independently
re-verified), and Car Condition's damage-threshold decision (§8).

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
  `_lastForecastSamples` cache + `SequenceEqual` guard.
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

## 6. Not yet trustworthy / unvalidated

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
- **Final-lap registration fix (§5, fifth round) is reasoned from the reported symptom,
  not yet re-confirmed live.** The fix assumes `LastLapTimeInMS` reliably changes value
  on the final lap even though `CurrentLapNum` doesn't advance - consistent with what
  was observed, but not independently verified against a second real race/sprint
  finish. Watch the next session's final lap specifically.
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
  damage field" is no longer a safe assumption on its own. See the open decision in §8
  (Car Condition damage threshold).

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
- Lap Timing's history list always renders exactly `LapHistoryDepth` (10) rows (blank
  placeholders before 10 laps exist) rather than growing from 0 — otherwise the
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
- Full-field tower to the left of everything else in Race: position, livery swatch,
  driver + team, Interval (gap to car ahead), Gap (to leader). See §8 for why this
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
  widget taller than the viewport - see §5. Background engine-component *wear* (as
  opposed to damage) is deliberately excluded - see §6; per-corner tyre *damage* is
  also excluded now (see §8) since the Tyres widget already shows per-corner wear and
  the two read as duplicated, not distinct, information.
- **Penalties & Flags** — same quiet/yellow-itemised pattern as Car Condition; also
  shows the player's own current flag (`VehicleFiaFlags`: None/Green/Blue/Yellow) as a
  coloured chip with label (e.g. blue = let car through).
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
- `Models/EventLabels.cs` formats `PenaltyType`/`ResultReason` with hand-written labels
  (small fixed enums) and `InfringementType` (50+ values) with a generic
  PascalCase-to-words humanizer, rather than hand-labelling all of them.
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
  gap in each Lap History row (between the sector columns and the Time/Delta columns)
  and asked to log this idea rather than build it yet. Modelled on the real F1
  broadcast tyre-strategy graphic (per-driver horizontal bar, colour-coded by compound,
  circular compound-letter markers at each pit stop). Scoped-down version analyzed and
  confirmed feasible with existing data:
  - `SessionDataPacket.TotalLaps` gives race distance at session start, solving the
    user's "shorter races need different axis scaling" concern with no guessing.
  - Stint-change detection: `CarStatusData.TyresAgeLaps` resetting (more robust than
    watching `VisualTyreCompound` alone, since it also catches a same-compound re-fit).
  - Colours: reuse `Models/CompoundPalette.cs` as-is.
  - Scope: player's own car only (one bar, not a per-driver grid like the broadcast
    version) - the Race Position Tower (§7) is now the one place full-field rival data
    is shown; this stint timeline should stay player-only rather than duplicating that.
    Race preset only - `TotalLaps` isn't meaningful in Practice/Qualifying.
  - Layout: Lap History always renders exactly `LapHistoryDepth` (10) rows including
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
