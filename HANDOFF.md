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
  normal WPF startup: `App.xaml` no longer has `StartupUri` (removed), and it is built as
  a `Page` instead of the default `ApplicationDefinition` - via
  `<EnableDefaultApplicationDefinition>false</EnableDefaultApplicationDefinition>` in the
  `.csproj`, **not** by removing the item after the fact (see §4.7 - doing it the other way
  caused intermittent CS0017 build failures) - because
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
  silently on launch, no manual re-download needed. Unsigned (no code-signing
  certificate), so Windows SmartScreen will likely warn on first run; a known,
  accepted tradeoff for unsigned indie software, not a bug.
  **Full round-trip confirmed live end to end**: `v1.0.0` installed and ran correctly
  via `Setup.exe`, then after `v1.0.1` was published, the installed `v1.0.0` copy
  silently detected it, downloaded it, applied it, and restarted into `v1.0.1` on its
  own next launch, with the new version visible in the settings panel confirming it -
  no manual redownload, no prompt. Auto-update can now be treated as reliable. One
  install-time gotcha hit and fixed along the way: Velopack's default install
  directory is `%LocalAppData%\F1RaceEngineer` - the exact same path the now-removed
  Track Map feature used for its `trackmaps\{Track}.json` cache (see §7 "Track Map -
  built, then removed"). A leftover cache file from old testing made that folder
  already exist before the first install, so `Setup.exe` initially reported "already
  installed" (a false positive - no registry Uninstall entry existed, confirmed via a
  real registry check) instead of doing a fresh install. Fixed by deleting the stray
  folder before re-running `Setup.exe`.
  **CORRECTION (§5 fortieth round) - the "not expected to recur" note that used to sit
  here was wrong, and stayed wrong for many rounds.** It claimed nothing writes to that
  path anymore once Track Map was removed. Three things do, all added later:
  `RaceHistoryStore` (`history\`), `WindowStateStore` (`window.json`) and the pit
  diagnostic (`pit-debug.log`) - all under `%LocalAppData%\F1RaceEngineer\`, i.e. inside
  Velopack's own install root. Consequences, in order of how likely they are to bite:
  - **The "already installed" false positive CAN recur**, and most easily for the project
    owner: a `dotnet run` dev session creates that folder before any install has happened.
    Clear it before a first-time `Setup.exe` on a machine that has only ever run from source.
  - **Updates are safe** - Velopack replaces `current\` and leaves sibling folders alone.
    This is empirically proven, not assumed: saved races survived every release from v1.0.x
    to v1.3.3 and only ever disappeared when the user deliberately cleared them.
  - **Uninstall removes the app directory, and takes the saved race history with it.**
    Anyone wanting to keep their races across an uninstall must copy `history\` out first,
    or export the races to HTML. Not currently surfaced anywhere in the UI.
  Moving app data to its own root (e.g. `%LocalAppData%\F1RaceEngineer.Data\`) would
  decouple it from the installer, at the cost of a one-time migration for existing saves -
  deferred, not done, and deliberately recorded here rather than silently fixed.
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
- **Lap History** — toggleable (see Settings panel below), every lap of the race kept, in
  an **elastic viewport** that takes whatever height the window leaves over (§5
  twenty-ninth round - it was a fixed 12-row viewport until then). Shows lap #, an IN/OUT tag
  for in-laps/out-laps (its own dedicated column so Time's start position never shifts),
  a "PIT" column showing the stop time on the IN row (patched in retroactively once the
  stop finishes - see §5 thirteenth round) and total pit-lane time on the OUT row
  (forward-stored, no ambiguity), per-sector times each with a colour-coded underline,
  lap time, and delta, one lap per row. Confirmed rendering correctly, including the
  IN/OUT classification itself — **confirmed live**. Both PIT column values are
  **confirmed live across 7 real stops** (pit-log review, §5 twenty-fifth round): the
  IN row's stationary box time and the OUT row's total pit-lane time. Note the review
  overturned this section's previous claim that the IN-row time only worked on
  "normal" tracks: **every stop logged so far straddles the S/F line** (that is the
  normal case, not a Monaco quirk) and all 7 tagged and patched correctly. The
  still-unobserved failing geometry is the line being crossed *after* the stop - see
  the pit-tag entry in §6.
- **Alert banner** — Safety Car / VSC / Red Flag / Chequered Flag (see caveats in §6).
  Confirmed working live, including VSC.
- **Practice/Qualifying timing board** (`PositionListWidget`) — full field, livery
  colour swatch, driver name + team name (from `ParticipantData.Team`, mapped to a
  display label in `Models/TeamNames.cs`), best lap, gap to fastest. Sorted and numbered
  by best lap (§5 twentieth round - the game's `CarPosition` isn't reliable best-lap
  order in Practice). Shown in both Practice and Qualifying since the nineteenth round's
  layout unification.
- **Uniform per-preset layout** (§5 nineteenth round) — all three presets share one
  layout: the full-field view in the left column (Race tower in Race; the timing board
  in Practice/Qualifying), and the single Lap Timing widget *with* history in the right
  column on every preset. The old separate history-less Qualifying Lap Timing instance
  was removed.
- **Widget catalog built**: Tyres (compound badge + per-corner wear, compact
  "+"-divided car-layout diagram), Car Condition (quiet checkmark when clean / yellow
  itemized list when actually damaged), Penalties & Flags (same pattern + a live flag
  chip), Session & Track (weather badge + a single "next forecast change" callout, or
  "Stable - no change expected"). All toggleable via the gear-icon **settings panel**
  (`Widgets/SettingsPanel.xaml`), with per-preset default state: everything on for
  Race; only the core (Lap Timing / Position List) for Practice and Qualifying.
  Confirmed live in a real race session (see fixes in §6 that came out of that test).
- **Race Position Tower** (`Widgets/RacePositionTowerWidget.xaml`) — a "LAP X / Y"
  banner (tracks the race leader, §5 thirteenth round) and a purple "FASTEST LAP"
  strip (§5 twenty-first round) above a full-field tower shown only in Race, to the left
  of everything else: position (with a ▲/▼ places-gained-vs-grid delta beside it, §5
  thirtieth round), livery swatch, driver + team, Interval (gap to car ahead), Gap (to
  leader), tyre compound letter paired with its age in laps (§5 thirtieth round), a "PIT"
  indicator while a car is being serviced (§5 nineteenth round, confirmed §5 twentieth),
  and a shared badge slot for a pending-penalty "!" or the fastest-lap stopwatch. Reads
  `LapData.DeltaToCarInFrontInMS`/`DeltaToRaceLeaderInMS` directly per car - no
  cross-referencing needed, unlike the old Gaps & Position widget it replaced (see §8 for
  why that one was removed). Retired/DNF/DSQ/not-classified cars show a dimmed row and a
  single "Out" instead of a stale interval/gap (§5, tenth and thirteenth rounds).
- **Adaptive grid layout** (`MainWindow.xaml.cs` `ArrangeWidgets`) rebuilds the catalog
  widgets' row/column definitions to fit exactly the currently-visible set on every
  toggle change, so remaining widgets grow to fill freed space with no blank gaps.
  Widget key order is grouped by content density (denser widgets grouped together,
  compact ones grouped together) so rows sharing a card height don't mismatch as badly.
  Column count is derived from the available **width** (~400px per card), not the widget
  count - see §5 twenty-ninth round.
- **Race History** (`Widgets/HistoryPanel.xaml`, `Models/SavedRaceView.cs`,
  `Models/HistoryGroups.cs`) — every finished race is auto-saved (§5 twenty-second round)
  and browsable from the history icon: result-tiered cards (win/podium/points/DNF), the
  player's team livery, vector country flags (`Models/FlagPalette.cs`), per-season
  sections with summary stats, Sprint+Race weekends merged into one card, and a
  save switcher. Opening a card shows the full classification, lap-by-lap, tyre strategy
  and penalties; it can be exported to a self-contained HTML page. See §5 rounds
  twenty-sixth to twenty-eighth.

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
first try (see §5/§6 for the full history); the twenty-fifth round's pit-log review has
since confirmed that third attempt DOES work, on 7 of 7 real stops. Still open: the
final-lap registration fix (§5 round five), the session-restart fix (§5 round nine),
the new lap counter banner, and the FIA-aligned penalty text (both §5 round thirteen) -
none independently re-verified live yet - plus Car Condition's
damage-threshold decision (§8). A fourteenth round (§5) merged the connection bar and
preset tabs into one row and stopped the error-text row from reserving space when
empty - pure window-chrome layout, verified via screenshot rather than live game data
(no telemetry-dependent behaviour involved). A fifteenth round (§5) increased Lap
History's depth, capped and severity-sorted the Car Condition/Penalties & Flags issue
lists, and fixed a catalog-grid outer-edge alignment bug - also pure layout/UI work,
verified via screenshot and temporary debug scaffolding rather than live game data.
A sixteenth round (§5) added the app version to the settings panel - verified via UI
Automation rather than a screenshot, since the settings panel is a `Popup` (separate
top-level window) and this environment's screen capture can't see this app's window
at all. A seventeenth round (§5) made Lap History retain the whole race in a
self-scrolling viewport (then a fixed 12 rows; later made elastic in the twenty-ninth
round, so it now grows with the window) with an app-wide themed scrollbar - pure
UI/data-retention work, verified via UI Automation + screenshot with temporary
seeded-lap scaffolding. An eighteenth round (§5) raised the
issue-list cap to 6 entries, collapsed duplicate warnings into an "(xN)" multiplier,
and turned the alert banner into a floating overlay (so it no longer pushes the widgets
below around) - all verified without a live game. A nineteenth round (§5), out of a
full-repo audit + F1 authenticity check, added the position/timing board to the
Practice tab, a "PIT" indicator to the Race tower, and unified all three presets to
one layout (full-field view in the left column, Lap Timing with history in the right)
- Qualifying gained lap history and the separate history-less instance was removed.
A twentieth round (§5) fixed the timing board to sort/number by best lap (not the
game's `CarPosition`), confirmed the tower "PIT" indicator and Car Condition damage as
working, and confirmed the OUT-row pit-lane time via the Barcelona log. A twenty-first
round (§5) added the cold-start "Waiting for telemetry" placeholder, redrew the tyre
compound marker as a broadcast-style vector ring (removing now-dead
`TyreCompoundForeground`/`CompoundPalette.ForegroundFor`), and added the race
fastest-lap strip to the tower - all verified without a live game except the fastest-lap
strip, which needs a race to confirm it populates.

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
7. **Opt App.xaml out of `ApplicationDefinition` with the *property*, never by removing
   the item.** The `.csproj` sets
   `<EnableDefaultApplicationDefinition>false</EnableDefaultApplicationDefinition>` so
   App.xaml is never made the ApplicationDefinition, and the SDK's default `Page` glob
   (`**/*.xaml`) then picks it up on its own — do NOT also add `<Page Include="App.xaml"/>`,
   that duplicates it and trips `CheckForDuplicatePageItems`.
   The project previously did this the intuitive way instead — let the SDK add the default
   and then `<ApplicationDefinition Remove="App.xaml" />` + `<Page Include="App.xaml" />` —
   which built fine *most* of the time but intermittently failed with
   **CS0017 "Program has more than one entry point defined"** pointing at `App.xaml.cs`,
   "fixable" only by wiping `obj/`. Why: WPF markup-compile pass 2 runs whenever any XAML
   references a type from this same assembly (ours do — `widgets:HistoryPanel` etc.), and it
   re-evaluates a *clone* of this project written to the project directory as
   `F1RaceEngineer_<hash>_wpftmp.csproj` (the source of the stray `*_wpftmp*` files). An
   item `Remove` in the project body isn't reliably replayed in that clone, so App.xaml
   became the ApplicationDefinition again and PresentationBuildTasks generated an
   `App.g.cs` containing its own `Main`, colliding with the Velopack `Main` in
   `App.xaml.cs`. (`<StartupObject>` would normally make CS0017 impossible by passing
   `/main:` — that it fired at all is the tell that the temp project doesn't inherit it
   either.) The SDK adds the item in its `.props` purely on the property's value, and
   properties do flow to the clone, so the property opt-out closes the hole for good: with
   the item never created, only one `Main` exists in every configuration.
   Verified via `dotnet msbuild -getItem:ApplicationDefinition` (empty) and `-getItem:Page`
   (App.xaml present exactly once). Because the SDK only drops XAML from `None` when the
   default ApplicationDefinition is enabled, the `.csproj` now also does
   `<None Remove="**/*.xaml" />` itself.
8. **Don't wrap the widget area in a `ScrollViewer` again, and don't put a `MinHeight` on
   a child of a star row.** Both look harmless and both silently break the layout:
   - A `ScrollViewer` measures its child with **infinite** height. Star (`*`) rows then have
     no total to divide, so "take the leftover space" means "take everything": Lap History
     grows without bound and pushes the catalog widgets off the bottom of the window
     (observed exactly this). The window's `MinWidth`/`MinHeight` are what make dropping the
     scroll safe - the layout is guaranteed a floor to lay out in.
   - `MinHeight` on an element *inside* a star row reserves nothing. The row still gets only
     its share; the child just overflows and clips. A 295px "6-row floor" on the Lap History
     viewport read as a guarantee while actually rendering **zero** rows at a small window. A
     real floor has to go on the `RowDefinition` - and then that space comes straight out of
     the catalog, which is the failure above. Hence: no floor; history yields, widgets stay.

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
  **Annotation (§5 thirty-ninth round): that field no longer exists.** Rows now take the
  game's own lap number instead of an app-side counter, which removes this whole class of
  bug rather than resetting around it - don't go looking for `_lapNumberForHistory` in the
  current code.
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
  earlier cloud-sync file-lock failure that escaped `obj/` into the project root. Normal
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
  `_warningReasons` list, and showing one itemized line per warning *kind* (e.g.
  "Warning - Ignoring Blue Flags") instead of the generic count. Duplicate warnings of
  the same kind collapse into a single line with an "(xN)" multiplier (e.g. "Warning -
  Track limits (x3)") rather than repeating the identical row and eating list slots
  (`RefreshPenalties` groups `_warningReasons` by reason - added later than the original
  itemization). Falls back to a generic "+N more warning(s)" line for any gap between
  the event-based list and `TotalWarnings` (e.g. warnings that happened before the app
  connected this session), so the total shown always still reconciles with the game's
  own authoritative count.
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
  Tower widened 384px → 400px to fit (later 436, §5 thirtieth round). Not yet tested
  against a real pending penalty.

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
  `TelemetryState.cs`; that cap was later removed entirely in the seventeenth round). No
  XAML changes needed at the time - the widget had no fixed height, it just grew and
  the outer `ScrollViewer` picked up the extra rows (the seventeenth round changed this
  to a fixed-height, self-scrolling viewport that keeps every lap).
- **Car Condition and Penalties & Flags issue lists capped.** Both lists are otherwise
  unbounded (a wrecked car or a heavily-penalized session could list a dozen items),
  which would blow past the catalog grid's shared row height. New shared
  `CapIssueList()` helper in `TelemetryState.cs`, applied after computing
  `CarConditionIsOk`/`PenaltiesIsOk` from the *un*truncated count (so the OK/issue state
  itself is never affected by the cap) but before assigning into the bound
  `ObservableCollection`. When truncated, the last visible slot becomes a `"+N more"`
  summary rather than silently dropping items. The cap constant counts **entries, not
  rows** (both widgets lay entries out in a 2-column `UniformGrid`): started at
  `IssueListMaxRows = 5` (a misleading name - 5 entries render as 3 rows: 2+2+1), later
  renamed `IssueListMaxEntries` and raised to **6** so the third row fills evenly (2+2+2)
  and 7+ issues show the 5 most-severe plus `"+N more"` in the 6th slot. Verified via
  temporary debug scaffolding (removed before commit) seeding 8 fake issues into each
  list and confirming 6 entries / 3 full rows with the overflow summary.
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

### Sixteenth round (version number, no live game needed to verify)
- **App version now shown in the settings panel** (`Widgets/SettingsPanel.xaml(.cs)`)
  - small muted text at the bottom of the gear-icon flyout, e.g. "v1.0.0". Deliberately
  NOT on the main dashboard (settings panel is the one surface that's fine to spend a
  little space on, since it's only open when deliberately checking something, not
  during actual racing). Read from the assembly's `AssemblyInformationalVersionAttribute`
  (not `AssemblyVersion`) since that preserves the exact `<Version>` string from the
  `.csproj` (e.g. "1.0.0") rather than a zero-padded 4-part `AssemblyVersion` (e.g.
  "1.0.0.0"). One bug caught before shipping: the .NET SDK automatically appends
  `+{git commit sha}` to `InformationalVersion` for traceable builds (e.g.
  "1.0.0+7848c2a0907493c7fee7e7a4b4d8fbd68e724ec3") - far too much detail for a small
  settings label, so the displayed text is truncated at the first `+`. Verified via UI
  Automation (`AutomationElement`/`TogglePattern`/`AutomationIdProperty`, driven from
  PowerShell) rather than a screenshot - the settings panel is a WPF `Popup`, which
  renders as a separate top-level OS window that a `PrintWindow` capture of the main
  window's `hwnd` can't see, and this test environment's screen capture doesn't show
  this app's window at all (it's not on the interactive/capturable desktop surface),
  so physical mouse-coordinate clicking was unreliable too. UI Automation sidesteps
  both problems - it invoked the real `SettingsButton` `ToggleButton` directly via its
  `TogglePattern`, and read the real bound `VersionText.Text` value directly by
  `AutomationId`, confirming the actual runtime value ("v1.0.0") rather than eyeballing
  a rendered image.

### Seventeenth round (scrollable full-race Lap History, no live game needed to verify)
- **Lap History now retains the entire race and scrolls, instead of dropping laps past
  12.** Previously `LapHistoryDepth` (12) was a hard cap - `RegisterLapTime` did
  `while (Count > 12) RemoveAt(last)`, so from lap 13 on the oldest lap was deleted and
  lost. Now nothing is dropped: the cap is gone (`RegisterLapTime` only inserts, never
  removes), the widget shows a **fixed-height viewport of 12 rows**, and past 12 laps
  the extra laps are reachable by scrolling. A prior measurement exercise (see the
  tower-height analysis) established this also keeps the catalog widgets from being
  pushed down as the race goes long - the viewport height is now constant regardless of
  lap count.
- **Data (`TelemetryState`):** the `LapHistory` collection now just grows (a race is
  <=~78 laps, negligible to hold / render un-virtualized); reset clears it and it fills
  from empty. The old "always keep exactly 12 rows via blank placeholder entries"
  scheme is **gone** - an earlier version of this round used placeholder rows to hold
  the height, but they rendered as faint ghost rows (empty sector underlines +
  separators) that looked wrong on a short race (e.g. a <12-lap sprint), so the
  fixed-height viewport (below) holds the height instead and no placeholder rows exist.
- **View (`LapTimingWidget.xaml`):** the lap-row `ItemsControl` is wrapped in a
  `ScrollViewer` with a **fixed `Height="589"`** (= 12 * per-row height; tied to the row
  template, so re-measure if that template's sizing changes). Fixed `Height` (not
  `MaxHeight`) is what keeps the widgets below from ever shifting - a short race shows
  its real laps with blank space beneath (no ghost rows), a full race fills up and
  scrolls. The column-header row stays *outside* the ScrollViewer so headers don't
  scroll. Scrollbar visibility is `Visible` (not `Auto`) *purely to reserve the gutter
  permanently* so the right-aligned DELTA column never reflows when the thumb appears;
  the ScrollViewer has `Padding="0,0,6,0"` (6px between the row content and the 8px
  scrollbar so the deltas don't touch it) and the header carries a matching 14px right
  margin (6 + 8), keeping header and rows the same width. The themed scrollbar (below)
  hides its own thumb when there's nothing to scroll, so it still only visibly appears
  past 12 laps.
- **Themed scrollbar (`App.xaml`, app-wide):** a custom `ScrollBar` style - thin (8px),
  no arrow buttons, transparent track, muted rounded thumb (`#30363D`, brighter on
  hover/drag) matching the app's border palette. Applied globally, so it also replaces
  the main window scroll area's default chunky light-Windows bar (an existing
  inconsistency) in one go. A `Trigger` on `IsEnabled=False` collapses the thumb when
  the content fits - that's what makes the always-reserved-gutter scrollbar read as
  "no scrollbar" until there's actually something to scroll.
- Verified via UI Automation + screenshot with temporary seeded-lap scaffolding
  (removed before commit): at 20 laps, exactly 12 rows visible (newest on top, Lap
  20->9), the scrollbar present and `IsEnabled=True`, and all 20 laps retained; at 12
  laps the scrollbar is `IsEnabled=False` (thumb hidden) - confirming the `Height`
  boundary is tuned so 12 shows no bar and 13+ does; and at 8 laps the widget shows 8
  real rows + blank space (no ghost rows) with the deltas clearing the scrollbar.

### Eighteenth round (issue-list tuning + floating alert banner, no live game needed)
- **Issue-list cap raised 5 -> 6 entries and the constant renamed** `IssueListMaxRows`
  -> `IssueListMaxEntries` (it always counted entries, not rows - misleading name). 6
  entries fills exactly 3 rows in the 2-column grid (2+2+2) instead of 2+2+1; 7+ still
  shows the 5 most-severe + "+N more". See §5 fifteenth round (updated in place).
- **Duplicate warnings collapse into an "(xN)" multiplier** in Penalties & Flags -
  `RefreshPenalties` now `GroupBy`s `_warningReasons`, so three "Track limits" warnings
  render as one "Warning - Track limits (x3)" line instead of three identical rows
  eating slots. See §5 ninth round (updated in place). Verified via the real
  `RefreshPenalties` path (seeded duplicate reasons through a settable `LapData`).
- **Alert banner converted from an inline element to a floating overlay** so it no
  longer pushes the widgets below up/down when it appears/disappears. Was the first
  child of the right-column `StackPanel`; now it's a layered sibling on top of that
  column's content (`Grid` + `VerticalAlignment="Top"` in `MainWindow.xaml`,
  higher Z-order), so it overlays the top of the current-lap block while the position
  tower and everything below stay exactly where they are. Modelled on the real F1
  broadcast "status bar across the top of the screen" convention (researched against
  Formula1.com - flags/race-control overlay the picture, they don't reflow the
  graphics). Restyled (FontSize 28 -> 22) with a drop shadow so it reads as floating.
  Scope decision (confirmed with the user): right-column-only overlay rather than full
  window width, so the position tower / leaders are never covered. Because the banner's
  background is opaque, it's sized (`MinHeight="72"`) to **fully cover** the current-lap
  / PB / delta block underneath (measured 70 DIP from the top of the timing widget to
  the bottom of the big lap-time number) - an earlier slim version left the lap-time
  numbers peeking out below it, which read as unclean; the taller opaque strip hides
  them entirely and stops just shy of the SECTORS row (~84 DIP down). Confirmed with the
  user before this refinement. Verified via UI Automation: `LapTiming.Top` is identical
  (75) whether the alert is shown or hidden - i.e. genuinely zero layout shift - plus
  screenshots confirming the banner floats over and fully covers the current-lap block
  with the full 22-car tower still visible. (Note: the overlay lives inside the outer `ScrollViewer`,
  so it's pinned to the top of the right-column content, not the viewport - on a screen
  where everything fits, that's the top of the page; if scrolled down on a smaller
  screen it scrolls with the content rather than staying fixed. Acceptable for the
  common all-fits case; a truly fixed status bar would need it hoisted out of the
  ScrollViewer with a preset-aware left offset to stay right-column-aligned.)
  - **Superseded on two points (§5 twenty-ninth round), both still true in spirit:**
    (1) `MinHeight="72"` is gone - the banner binds to `LapTimingWidget.HeaderExtent`, the
    *measured* distance to the bottom of that block, so the "re-measure if the font sizes
    change" caveat no longer applies. It still covers exactly the same block.
    (2) The outer `ScrollViewer` no longer exists (the window has a MinHeight instead), so
    the scrolling caveat is moot - the banner is always pinned to the top of the viewport.

### Nineteenth round (per-session realism, from a full-repo audit + F1 research)
Came out of a full line-by-line code review + authenticity check against real F1
sources. The review found the codebase clean (no dead code, no stray debug scaffolding
beyond the permanent `DebugForcePreset`, builds clean, docs current) and every existing
feature authentic (timing colours, compound colours, flag semantics, FIA terminology,
2026 grid all match real F1). Two per-session gaps were found and fixed:
- **Practice now shows the position/timing board**, and **all three presets now share a
  uniform layout** (a follow-up the user asked for after seeing the board added). Real
  F1 practice is a timed leaderboard session (all drivers ranked by best lap, gap to
  fastest), confirmed against Formula1.com live-timing coverage - Practice previously
  showed only the player's own Lap Timing. The uniform layout: the **full-field view
  lives in the LEFT column on every preset** (the Race Position Tower in Race, the
  `PositionListWidget` board in Practice/Qualifying - same 436px column, mutually
  exclusive), with the **same Lap Timing widget filling the RIGHT column everywhere**.
  Previously the board was stacked *below* Lap Timing in the right column on
  Practice/Qualifying; moving it to the left column matches the tower's placement and
  also fits 1080p better (two ~850px columns side by side instead of one very tall
  stacked column). The separate history-less `QualifyingLapTiming` instance was
  **removed** - Qualifying now uses the single `LapTiming` (with history, toggleable,
  on by default), dropping the old "hotlap-focused, no history" rule for uniformity.
  Verified via screenshot on all three presets (board left + history right on
  Practice/Qualifying; tower unaffected on Race).
- **Race tower now shows "PIT" for cars in the pit lane.** Real broadcast towers show
  an IN PIT / PIT indicator while a car is being serviced; the tower previously showed
  nothing for a pitting car (only "Out" for retired). Added `IsPitting` to
  `RaceStanding` (from `LapData.PitStatus != None`, computed in `RefreshRaceStandings`),
  which replaces the interval/gap with a blue bold "PIT" in the Gap column - same
  mechanism as "Out" but NOT dimmed (the driver is still racing) and it reverts to the
  live gap automatically the moment `PitStatus` returns to None (back on track), no
  timers needed. "Out" takes priority over "PIT". Blue (`#79C0FF`) matches the pit-time
  colour already used in Lap History. Verified via screenshot with temporary mock rows
  (removed before commit): a pitting row shows bold blue "PIT" undimmed with its tyre
  letter still visible, a retired row shows dimmed "Out" with no tyre, normal rows
  unaffected; and the Practice board renders below Lap Timing.
- Deferred from the same audit (not built, no strong need per the user): rival tyre age
  in the tower, a DRS indicator, a tyre-compound column on the qualifying board, and
  re-adding Fuel & ERS - all technically feasible (`CarStatusData.TyresAgeLaps`/
  `DrsAllowed` per car exist) but declined for now.

### Twentieth round (follow-ups from live testing the new per-session layout)
- **Position/timing board was sorted by the game's `CarPosition`, which isn't reliably
  best-lap order in Practice** (it can reflect track position), so drivers showed up out
  of timing order even though their best-lap times/gaps were correct (the gaps come from
  `_carBestLapMs`, the sort didn't). Fixed in `RefreshPositionList`: it now sorts by best
  lap (fastest first) and numbers rows by that rank, with cars that haven't set a lap
  falling to the bottom in the game's own position order. Only affects the Practice/
  Qualifying board (`PositionListWidget`), not the Race tower (which correctly uses
  `CarPosition` for race order). Reported live in Practice; the fix needs a live re-check.
- **Confirmations from live racing:** the Race tower's "PIT" indicator works as intended
  (moved out of §6); Car Condition damage reads correctly with no threshold needed
  (§8 decision closed); and the Barcelona pit log confirmed IN/OUT tagging and the
  OUT-row pit-lane time both work. (This round called the pit-tag bug "Monaco-specific";
  the twenty-fifth round's fuller log review showed that framing was wrong - every track
  straddles the line - see §6.)

### Twenty-first round (cold-start polish, realistic tyre marker, race fastest lap)
- **Cold-start "Waiting for telemetry" placeholder.** Before any packet arrives the app
  sits at the default `Unsupported` preset, which collapses both the tower and the board
  and defaults the catalog widgets off - leaving only the always-on Lap Timing card, a
  bare layout that happened to look like the *old* (pre-board) Practice tab. Added an
  opaque overlay (`WaitingPlaceholder`, `MainWindow.xaml`, on Grid.Row 2 over the
  ScrollViewer so it centres in the visible viewport, not the taller scroll content).
  **Gating (revised, §5 twenty-second round):** shown for any `Unsupported`-preset state
  EXCEPT a live **Time Trial** - i.e. `CurrentPreset == Unsupported && !IsTimeTrial`.
  Originally this keyed off a "has any packet arrived" latch, but that was wrong: when the
  game sits in its **menus/lobby** it streams data with an unmapped session type
  (`Unsupported`), so the latch flipped and the placeholder hid, leaving a bare
  Lap-Timing-only layout (looked like an "old Practice tab"). Keying off `IsTimeTrial`
  instead keeps the placeholder up in the menus (where there's nothing to show) while
  still stepping aside for Time Trial (a real drivable session) and for a settings-menu
  preview (which forces a concrete preset). `IsTimeTrial` is set in `HandleSession`.
  Verified centred at cold start; the menu case needs a live confirm.
- **Tyre compound marker is now the broadcast-style ring**, not a flat filled disc:
  coloured compound band (outer ring) around a dark tyre body with the letter in the
  band colour (`TyresWidget.xaml`, all vector shapes). Deliberately **not** the real
  Pirelli/F1 tyre artwork - that's trademarked and can't ship in a public distributed
  app; the vector lookalike carries no such risk and scales/theme-adapts cleanly. This
  made `TelemetryState.TyreCompoundForeground` and `CompoundPalette.ForegroundFor`/
  `DarkForeground`/`LightForeground` dead (the letter now uses the band colour on a dark
  centre, so no per-compound contrast foreground is needed) - all removed.
- **Race fastest-lap strip in the tower (audit item #7).** A purple "FASTEST LAP -
  {driver} {time}" strip under the LAP counter banner (`RacePositionTowerWidget.xaml`),
  reusing the same purple `#AFA9EC` + stopwatch glyph as the per-row fastest-lap badge.
  Driven by new `HasRaceFastestLap`/`FastestLapDriver`/`FastestLapTimeText` on
  `TelemetryState`, set in `RefreshRaceStandings` from the `_carBestLapMs` holder it
  already computes for the badge (no new tracking). Hidden until someone sets a lap.
  Builds clean; **needs a live race to confirm the strip populates** (can't show without
  race data - it's Collapsed at cold start / in preview).

### Twenty-second round (Race History + live tyre-strategy bar)
Designed against an interactive mockup first (approved by the user), then built. Large,
mostly-new feature; verified in-app with seeded data (screenshots), but the live capture
path hasn't seen a real race finish yet.
- **Live tyre-strategy bar in the Tyres widget** - broadcast-style proportional stint
  segments (compound-coloured, letter on each), sitting to the RIGHT of the wear grid so
  the widget keeps its original height (`TyresWidget.xaml` + code-behind builds the
  proportional `Grid` from `TelemetryState.TyreStints`, same approach as `ArrangeWidgets`).
  Stints tracked in `UpdateLiveStints` (Race only): a new stint on a compound change or a
  tyre-age drop, `StartLap = currentLap - age` so it's right even if the app joined
  mid-race. Re-added `CompoundPalette.ForegroundFor`/`DarkForeground`/`LightForeground`
  (removed as dead in the 21st round) plus new `BrushForLetter`/`ForegroundForLetter` -
  the stint letters sit on the coloured bands and need the contrast foreground again.
- **Race capture** - `TelemetryState.HandleFinalClassification` handles the previously-
  unused `FinalClassification` packet (races/sprints only, deduped per `SessionUID`),
  building a `SavedRace` (full-field classification incl. tyre stints, plus a snapshot of
  the player's `LapHistory`) and persisting it via `RaceHistoryStore`. Fires a `RaceSaved`
  event so an open history panel refreshes. **Not yet confirmed against a real race
  finish** - all field reads are against the documented API.
- **Persistence** - `RaceHistoryStore`: one JSON file per race under
  `%LocalAppData%\F1RaceEngineer\history\` (not the project dir, which may be cloud-synced -
  no sync churn;
  same root as the pit log). All IO defensive (a history feature must never crash the app).
  `SavedRace`/`SavedRaceView` are plain DTOs (hex strings, not brushes) so they round-trip
  through `System.Text.Json` and re-render identically in the panel and the HTML export.
- **History panel** (`HistoryPanel.xaml`, toggled by a new clock icon left of the gear) -
  a full-viewport overlay (same pattern as the waiting placeholder). List of GP cards
  (finish + places gained, best lap, points, stint mini-bar, hover export/delete) opening
  a detail view: final classification, the player's lap-by-lap, and a tyre-strategy bar
  with **lap-number tick marks at each pit boundary**. Country code (FIA 3-letter, e.g.
  `GBR`) shown as a badge **before** the GP name (`TrackNames.CountryCodeFor`) - a code,
  not a flag graphic, so nothing needs bundling. Delete has an in-panel confirm; export
  writes a self-contained HTML file (`RaceHtmlExporter`, inline CSS, app dark theme).
  Detail tables use fixed column widths + headers so columns align, sectors/times
  right-aligned; the IN/OUT tag sits with the pit time (tag kept rightmost so IN and OUT
  line up), matching the main Lap History widget's pit → tag → time order.

### Twenty-third round (lap-by-lap EVENTS: SC/VSC, red flag, chequered, penalties + DELTA)
Designed with the user first (chose inline in the row's empty gap, genuine penalties only,
one yellow flag icon for SC vs VSC with text distinguishing them). Verified with seeded
data in both views; the live capture path still needs a real race to confirm.
- **EVENTS chips** in the lap-by-lap's normally-empty middle gap, in BOTH the live Race
  Lap History (`LapTimingWidget`) and the saved-race detail (`HistoryPanel`) for
  consistency. `LapEventChip` (reusable UserControl) renders one `LapEvent`: a coloured
  waved flag (reusing App.xaml's `FlagPoleGeometry`/`FlagPennantGeometry`) for SC/VSC
  (yellow) and Red Flag (red), a mini chequered flag for the finish, or an amber "!" badge
  for a penalty, plus text.
- **Capture** (`TelemetryState`): SC/VSC from `SafetyCarStatus` (marks every lap the state
  was active - `_lapEventSafetyCar`, reset per lap, re-set while it persists); Red Flag and
  Chequered from their events (single lap); genuine penalties from `PenaltyIssued`
  (`TimePenalty`/`DriveThrough`/`StopGo` only - warnings and lap-invalidations excluded, per
  the design call). Event-driven items accrue in `_pendingLapEvents` and are consumed when
  the lap completes (`BuildLapEvents` in `RegisterLapTime`). Snapshotted into
  `SavedLapRow.Events` (kind+text strings) and re-hydrated in `LapRowView`.
- **Design notes:** single vs double yellow was deliberately NOT used to distinguish
  SC/VSC - it's not the real-F1 meaning (double yellow is a local hazard flag) and the
  telemetry can't distinguish single/double anyway (`VehicleFiaFlags` is just `Yellow`).
  Red Flag is a point event (one lap), SC/VSC a spanning state (every lap). Events are
  pushed clear of the sector columns (left margin) per user feedback.
- **DELTA column** added to the history detail lap-by-lap (the live widget already had it),
  for full parity with the Race tab.

### Twenty-fourth round (interval-trend accent on the tower — first of the "engineer" ideas)
> **REMOVED in the thirty-first round.** The player-only interval caret described below was
> taken out once the thirtieth round added a whole-field ▲/▼ places-gained-vs-grid delta in the
> position column: two different ▲/▼ meanings a few columns apart (one "gap trend", one "places
> gained") were redundant and easy to confuse, and the user asked to drop the older one. The
> `IntervalCaret`/`IntervalBrush` members, the `UpdatePlayerIntervalTrend` maths, and the
> `IntervalTrend` state are all gone; the interval number itself stays (now a plain brighter-
> than-Gap column). `GapClosing`/`GapOpening` survive - the position delta reuses them. The rest
> of this entry is kept for the design reasoning.

Out of a "scrutinise it as a race engineer" discussion: the app shows *state* well but a
real engineer works from *trend*, so the highest-value cheap add is showing whether the
player's gap to the car ahead is closing or opening. Kept deliberately minimal after the
user flagged clutter risk.
- **Player-only interval trend** on the Race tower: the player's Interval gets a caret +
  colour - green **▲** closing (catching), red **▼** opening (dropping back), nothing when
  steady. Every other row is untouched. Caret sits INSIDE the fixed 58px Int column (left
  of the right-aligned number) so nothing widens or misaligns. New `IntervalCaret`/
  `IntervalBrush` on `RaceStanding` (+ `TimingColorPalette.GapClosing`/`GapOpening`).
- **Trend maths** (`UpdatePlayerIntervalTrend`): sampled once per lap (no tick jitter), a
  0.15s/lap deadband so normal fluctuation reads as steady, and a position-change guard so
  an overtake (car ahead is now a different car) doesn't produce a garbage arrow. Player
  only, per the design call - the whole field would be clutter; your own battle is what a
  driver acts on. Needs a live race to confirm it reads sensibly at speed.
- Convention chosen: **▲ = gaining ground** (momentum reading), not "gap number down".

### Twenty-fifth round (pit-log review — reading the collected data, no live game needed)
Read the whole of `%LocalAppData%\F1RaceEngineer\pit-debug.log` (373 lines, 33 sessions,
**7 real pit stops**) that §6's pit-tag investigation added in v1.0.3, instead of
continuing to wait on a Monaco race. It overturned two things this document asserted:
- **Straddling the S/F line is the normal case on every track logged, not a Monaco
  quirk.** All 7 stops: enter the pit lane on lap N, cross the line 0.9-6.5s later while
  still in the lane, stop on lap N+1. §2, §5's twentieth round and §6 all described
  straddling as the exceptional/Monaco case - each is corrected in place.
- **The IN-row stop time is confirmed working, 7/7** (box times 2.370-2.456s), as is the
  OUT-row lane time. So §5 round thirteen's third attempt
  (`PatchMostRecentInRowPitTime`) is proven, not still pending as §2 claimed. It works
  *because* the line is crossed before the stop: `DriverStatus` is still `InLap` at the
  line, so the tag is IN, and the IN row already exists when the stop finishes, so the
  patch has a row to land on.
- **The Monaco ordering (stop first, status flips to `OutLap`, then the line) appears
  nowhere in the log**, so that bug stays unreproduced. Don't rewrite `ClassifyLapTag`
  from theory - the working 7/7 path is exactly what a guessed fix would regress. Keep
  the logging until a Monaco stop is captured.
- Two 7.6s stops (vs the usual ~2.4s) were **front-wing changes after damage** -
  user-confirmed, expected, not a bug. That also explains the near-identical box times
  (7.630 / 7.601 - a fixed-duration animation) and why those two stops alone report an
  identical total lane time (26.966s) to the millisecond while every other stop differs.
- Garage in/outs in Practice correctly log `lane=False` and produce no pit rows - no
  false positives.
- Traced end to end against the exported Monza race: the log shows entry lap 7 -> line ->
  box 2.399 on lap 8 -> lane 24.365, and the saved race renders `Lap 7 IN 2.399` /
  `Lap 8 OUT 24.365`.

### Twenty-sixth round (Race History overhaul + live tyre-strategy bar — shipped v1.1.0)
From an 8-point user review of the history feature and the live tyre bar.
- **Live tyre-strategy bar now scales to the whole race.** It previously normalised to the
  laps *run so far*, so it always looked full - at lights-out the whole bar was already the
  starting compound. It now draws the compound segments over a full-race-length "ghost"
  track and fills lap by lap, with a lap axis (labelled majors every 5, faint minors when
  they'd be readable, accent pit markers). Extracted to `Widgets/StintStripRenderer.cs` and
  shared with the history detail bar so the two are guaranteed to look and scale identically
  (the user's actual complaint was that they didn't).
- **`EndLap = 255` sentinel.** The game reports the final stint's end lap as 255 ("no end
  yet"). The old star-column maths normalised it away by luck; scaling to race length
  rendered it literally as a 245-lap phantom stint. Clamped at both the view layer
  (`SavedRaceView.BuildSegments`, fixes existing saves' display) and at capture
  (`BuildStints`, fixes stored data).
- **Capture gained** `SeasonLinkId` / `WeekendLinkId` / `SessionLinkId` (the game's own
  persistent grouping IDs - see the twenty-eighth round), per-car `TotalRaceTimeSeconds` +
  `NumLaps` (for the gap column), and a penalties snapshot.
- **History detail**: gap-to-winner column, team names, DNF rows dimmed + red "DNF" matching
  the race tower, a penalties card, and a reorganised layout (penalties under the
  classification, tyre strategy under lap-by-lap, matched card heights).
- **Settings gear** redrawn as a vector icon so it matches the history icon exactly (it was
  the `⚙` text glyph, which renders at a different size/weight and sits off-centre).
- Only races saved from this version on carry the new fields; older saves show a blank gap
  column and land in an "Earlier races" bucket. Unavoidable - the data was never captured.

### Twenty-seventh round (HTML export parity — shipped v1.1.1)
- **The exporter had silently drifted a whole version behind.** It re-derived everything from
  the raw `SavedRace` while the panel rendered `SavedRaceView`, so every history improvement
  landed in the panel only. It now renders **the same `SavedRaceView`** (brushes converted to
  hex), which is what stops the two diverging again - the root cause, not the symptom.
- Brought the export to parity: panel layout, gap column, DNF flags, penalties card, positions
  gained, the lap axis, and a new Events column (SC/red flag/chequered/penalties) it never had.
- Writes **UTF-8 with a BOM** - exported pages were decoding as mojibake (`Â·`, `â`).

### Twenty-eighth round (history card redesign — released in v1.2.0)
- **Result-tiered cards**: win = gold (+ star), podium = silver/bronze, points = teal,
  out-of-points = muted, DNF = red - as a filled medal badge, a coloured left rail, and a
  soft glow behind the result corner. Driven from the view model, so it themes automatically.
- **Team livery** as an identity accent (dot + a wash across the card header).
- **Country flags**: new `Models/FlagPalette.cs` draws all 21 calendar countries as vector
  `DrawingBrush`es in a 60x40 space, replacing the `ITA`/`NED` text badge (still the fallback
  for any undrawn country). Vector, not bundled artwork - the project's "drawn, not captured"
  convention; at ~20px the simplified detail reads correctly.
- **Multi-save support.** Grouping by `SeasonLinkId` already separated saves correctly; the
  gap was that every section just said the year. Season headers now carry a livery dot +
  team·driver, and with the livery wash a whole save reads as one colour - so two same-year
  careers are tellable apart at a glance. Plus an ALL-default save switcher.
- **Responsive card columns** (2 at the default window, up to 4 maximised). A `RelativeSource`
  binding to the panel does **not** resolve from inside an `ItemsPanelTemplate` - the column
  count is pushed from code instead.
- Stat row made consistent (label-on-top/value-below); fastest lap in the classification now
  uses the race tower's purple stopwatch badge instead of a `◷` glyph.

### Twenty-ninth round (app-wide UI audit + layout overhaul — no live game needed)
A full end-to-end visual audit, driven by temporarily seeding mock telemetry so every widget
rendered (scaffolding removed afterwards). Findings and fixes:
- **The default window couldn't render the app.** At the shipped 720x640 the Race tab was
  broken: the alert clipped mid-word, sector values cut mid-digit, Lap History showed only
  LAP + S1, and every catalog widget sat below the fold. Every screenshot taken during
  development had been maximised, which hid it. Now **1500x1000** with a **1150x720 floor**.
- **Dead space at large sizes** (the inverse problem): the bottom ~30% was empty because Lap
  History was pinned to a fixed 589px/12-row viewport and nothing else grew. Now elastic
  (~17 laps maximised). Safe because the catalog's issue lists are already capped
  (`IssueListMaxEntries`), so its worst-case height is **bounded and known** - it reserves
  that, history absorbs the rest, and nothing shifts as a race fills up.
- **Outer `ScrollViewer` removed** - it made star sizing meaningless. See §4.8; it and the
  window MinHeight are a package, neither works alone.
- **Catalog columns are now width-derived** (~400px/card) rather than count-derived. Three
  bugs found by measuring real element bounds via UI Automation rather than eyeballing
  screenshots: a **stale `ActualWidth`** (arranged while the tower was still hidden, so it
  used the wrong width), a **1-column collapse** at narrow widths (the *tallest* possible
  shape, exactly where height is scarcest - it pushed a widget off-screen), and a **lone
  stretched card** at 1920x1080 (3 tidy cards + one at 1460px). Hence the 2-column floor and
  the no-orphan rule.
- **Alert banner** no longer carries a hand-measured `MinHeight="72"`; it binds to
  `LapTimingWidget.HeaderExtent` (see the eighteenth round's superseded note).
- **The flag banner is now budgeted against the shared 6-entry cap.** Both widgets always
  capped at 6, but Penalties rendered its flag banner *on top* of those 6, so it stood a row
  taller and stretched the whole catalog row. The banner's padding/margin now match an issue
  chip exactly (one grid row = 2 entries) and the list caps to 4 while a flag is up.
  Verified: both cards 170px, flag on **and** off.
- Withdrawn during the audit: the banner covering the lap times during a Safety Car is
  **deliberate** (they're meaningless when you're delta-limited), and a tyre-wear colour ramp
  would have **hijacked the game's own tyre colours**, which mean *temperature*, not wear.
  Tyre temperature colouring was then dropped outright: no tyre-state field exists in the
  telemetry, the thresholds aren't documented, community figures contradict each other, and
  they shift with patches - so it could never be guaranteed to match the game.

### Thirtieth round — the four decided-but-unbuilt items, built (previously §8)
All four had been agreed and mocked in prior rounds; this round built them. They moved out of
§8 ("agreed, not yet built") into here.
- **Tyre age paired with the compound letter in the Race tower.** `CarStatusData.TyresAgeLaps`
  is now cached per car in `_carTyreAge` inside the same `HandleCarStatus` loop that already
  caches `_carTyreCompounds` (free - no extra iteration), surfaced on `RaceStanding.TyreAgeText`,
  and rendered *next to* the compound letter as one unit ("M 14"), matching the broadcast. Option
  B, as decided: the age is never hidden (unlike parking it in the shared far-right badge column,
  which vanishes on the fastest-lap/penalty car). The age is grey, not the compound colour - the
  colour already says *which* tyre, and colouring the number too would imply it's colour-coded.
  Blank for an out car, same as the letter. The tyre column grew 20 → 36px.
- **Position-vs-grid delta inside the position column.** `RaceStanding.PositionDeltaText`/`Brush`,
  computed in `RefreshRaceStandings` as `GridPosition - CarPosition`: "▲2" green (gained), "▼1"
  red (lost), "–" muted grey (held). Uses the `GapClosing`/`GapOpening` brushes (green/red).
  **Guarded**: `GridPosition`
  is confirmed to *exist* but was not confirmed populated mid-race, so a `GridPosition == 0` (or
  out) car renders a blank string - the column degrades to looking exactly as before rather than
  showing a bogus "▲0". Whole field, not player-only. The position column grew 24 → 44px (an
  inner 18px sub-column holds the number so the delta always starts at the same x). Added
  `TimingColorPalette.MutedText` (the existing `#6B7684` label grey, promoted to a frozen brush)
  for the "held" dash - it must read quieter than the ▲/▼ carets since most of the field is flat.
- **Tower width 400 → 436** (measured exactly via UI Automation, not eyeballed). Both the
  `RaceTower` and the `PositionList` share the left Auto-width column, so both were widened to
  436 to keep that column identical across all three preset tabs. The header row and the row
  `DataTemplate` still carry **byte-identical `ColumnDefinitions`** - the two are commented as a
  paired invariant, since the header only lines up with the rows because the lists match.
- **Sector boxes shrunk.** Padding 12 → 8 and the big number 24 → 21px (margin 4 → 2) on all
  three S1/S2/S3 boxes, ~14px per box reclaimed. Sectors are an `Auto` row directly above the
  `*` Lap History row, so every pixel saved becomes one more pixel of visible lap history before
  it scrolls. Confirmed safe in the audit: the boxes are not coupled to the alert banner (which
  covers the current-lap block *above* them).
- **Window size/position now persists** across launches (`Telemetry/WindowStateStore.cs`, same
  `%LocalAppData%\F1RaceEngineer\` root and same defensive best-effort IO as `RaceHistoryStore` -
  a corrupt/missing `window.json` just means "open at the default size", never a crash). This is
  the **first thing this app persists** besides saved races (widget toggles still don't persist).
  `RestoreWindowPlacement()` runs in the constructor before the window shows; `OnClosing` saves
  `RestoreBounds` (the normal-state rect, so un-maximizing next launch returns to a sane size) plus
  a `Maximized` flag. Two guards: the saved size is ignored if below the XAML `MinWidth`/`MinHeight`,
  and the placement is ignored if it no longer overlaps the virtual desktop (a monitor unplugged
  since it was saved) so the window can't open unreachable off-screen. Round-tripped via UI
  Automation: set to (150,90,1240,860), closed *gracefully* (a force-kill skips `OnClosing`), and
  it reopened at exactly those bounds.

### Thirty-first round — removed the player-only interval-trend caret
The whole-field position-vs-grid delta the thirtieth round added shows ▲/▼ per row in the
position column; the twenty-fourth round's player-only ▲/▼ on the *interval* column meant
something different ("your gap to the car ahead is closing/opening"). Two ▲/▼'s a few columns
apart with different meanings was redundant and easy to misread, and the user asked to drop the
older one. Removed `IntervalCaret`/`IntervalBrush` from `RaceStanding`, the `IntervalTrend` state
and `UpdatePlayerIntervalTrend` maths from `TelemetryState`. The interval **number** stays - now a
plain column, kept a touch brighter (`#E6EDF3`) than Gap-to-leader (`#9BA7B4`) since the gap to the
car directly ahead is the more actionable figure. `GapClosing`/`GapOpening` survive: the position
delta reuses them. The twenty-fourth round entry is annotated as removed but kept for its reasoning.

### Thirty-second round — bottom-align the catalog widgets with the Position Tower
A long-standing niggle the user finally pinned down: the bottom of the catalog widgets didn't line
up with the bottom of the Position Tower on any tab. **Measured** it with UI Automation (maximized):
tower bottom 1376, widgets bottom 1370 - a **6px** shortfall. Cause: `ArrangeWidgets` gave every
catalog widget a symmetric `Thickness(left, 6, right, 6)`. The top 6 pairs with Lap Timing's own 6px
bottom margin to make the standard 12px gap, but the bottom 6 had nothing below it to pair with - it
just held the widgets 6px above the column's bottom edge, while the tower (no bottom margin, stretched
full-height) reached all the way down. Fix: the **last** widget row now gets bottom margin 0 so it
meets the column's bottom edge and lines up with the tower; earlier rows keep the 6 so wrapped rows
still sit 12px apart. Verified both layouts via UI Automation: single row (2576px wide) - tower and
all four widgets bottom at 1376; two rows (1500px, widgets wrap 2×2) - tower and the last row bottom
at 1016, every inter-row/inter-widget gap a clean 12px.

### Thirty-third round — pre-release audit + one efficiency fix
A full line-by-line pass over the whole app (all C#, every XAML structure, both docs) before cutting
a release. **No correctness bugs, crash paths, or security issues surfaced** - the code was uniformly
defensive (range-guarded packet reads, best-effort IO, frozen brushes, change-diffed collections).
The audit's real yield was documentation drift, all corrected:
- README claimed the tower still shows the **interval trend caret** (removed in the thirty-first
  round) - the most important fix, it's the public-facing description; also updated the tower bullet
  to the grid-delta + tyre-age, and the "12 laps visible" lap history to "grows with the window".
- Two `TelemetryState` comments and one `MainWindow` comment still described the **fixed 12-row
  viewport** / **400px tower**; corrected to elastic / 436.
- HANDOFF §7 and a §2 narrative still presented the interval caret / fixed viewport as current;
  annotated.

The one code change - **gate the leaderboard rebuilds by preset.** `HandleLapData` was calling both
`RefreshRaceStandings` (the Race tower) and `RefreshPositionList` (the Practice/Qual board) every
tick, though they're mutually exclusive on screen - so the hidden one was built, sorted and thrown
away 20-60×/s. Now only the visible preset's board rebuilds (neither for Unsupported - Time Trial /
menus have no field to rank). Safe because both only ever draw on `_carBestLapMs` (maintained
independently - by `HandleSessionHistory` since the thirty-seventh round; was the per-car LapData
loop when this round was written) and their outputs (`RaceStandings`/`PositionList`/`LapCounterText`/the
fastest-lap strip) are bound *only* into their own preset's widget - verified by grepping every
consumer. Pure CPU/GC saving, no visible behaviour change.

### Thirty-fourth round — v1.3.0 batch (user-reported fixes + Safety Car banners)
A run of user-reported issues, all verified against real saved data / rendered output, plus the first
of the deferred alert banners.
- **History Race/Sprint swap fix.** A saved US GP weekend showed the *sprint* under the "Race" tab and
  vice-versa. Root cause proven from the saved JSON: the 7-lap session was labelled "Race" and the
  20-lap one "Sprint" - F1 25 reports the **sprint as `SessionType.Race` and the feature race as
  `Race2`**, inverting the naive type→label mapping (the same class of enum-semantics surprise as the
  sprint-qualifying one below). Fix: `HistoryGroups` now picks main-vs-sprint by **lap count** (the
  feature race is always longer), ignoring the unreliable label, and `SavedRaceView.ApplyWeekendRole`
  stamps the correct display word - which fixes already-saved races too. Verified live: the US "Race"
  tab now opens the 20-lap race.
- **Race/Sprint toggle centered** on its row in the detail view (was tucked top-right), only shown for
  sprint weekends.
- **Tower tweaks:** position-delta font 9.5→11 with a right margin so it clears the livery swatch;
  tyre-age font 10→12. Both kept under the 13px position number / tyre letter, so row (and tower)
  height is unchanged - tower still measures 436.
- **Penalties tab - two bugs.** (1) *Display was never wired*: the detail view is populated
  imperatively, but the penalties card is the one part driven by `{Binding}`, and nothing set its
  `DataContext` - so it **always** read "No penalties" regardless of the data (even stored warnings
  never showed). Fixed by setting `PenaltiesCard.DataContext` in `ShowSession`; verified live (Madrid's
  two warnings now render). (2) *Served penalties weren't captured*: the saved list was a finish-line
  snapshot of the live widget, which only holds what's still *outstanding* - empty once everything's
  served. Now every penalty is recorded **as issued** into `_penaltiesIncurred` and saved via
  `BuildIncurredPenalties`, which mirrors the live widget exactly: `(xN)` grouping, **severity-sorted**
  (stop-go > drive-through > time > warning), and **capped to `IssueListMaxEntries`** with a "+N more"
  overflow. Forward-only by the user's choice - no retroactive reconstruction. HTML export was
  unaffected (it read the data correctly all along; only the in-app card binding was broken).
- **Equal history/export columns.** The detail view and the HTML export used a `1.05 / 0.95` column
  split (left ~10% wider), which read as lopsided. Measured the export precisely (heights were always
  equal; only width differed - 701 vs 635), then changed both to `1 : 1`. Now all four cards are the
  same width (export 668×668 / 668×156, rendered and measured; app balanced, no name truncation).
- **Safety Car event state machine — first of the deferred alert banners (§8 Tier 1).** The dedicated
  `SafetyCarEvent` (Deployed/Returning/Returned/ResumeRace) now layers on the `SafetyCarStatus` poll:
  the poll still drives the steady "deployed" banner (reliable, self-clearing), while the event adds
  the two moments a poll can't express - **"Safety car in this lap - peel off"** / "Virtual safety car
  ending" (sticky until it returns / resumes / poll clears), **"Safety car in the pits"** (5s), and the
  green **"Racing resumes"** go-signal (4s, new `AlertGreen` palette, highest priority in the SC
  group). Priority slot unchanged (below Red Flag/Retirement/Penalty, above Team-mate/Chequered); all
  SC event state resets per session. **Not yet confirmed against a real safety car** - the raw event is
  logged (`SAFETYCAR type=… event=…`, TEMP with the pit log) so its firing can be verified live before
  it's trusted, same discipline the session-type mismatch taught us.
- **Session-type diagnostic (TEMP)** added in `HandleSession` (`SESSION type=… preset=… totalLaps=…`)
  to pin down why **Sprint Qualifying / Practice isn't ranked by lap time.** **Resolved in the
  thirty-seventh round:** the diagnostic proved the `PresetMapper` mapping was correct all along -
  the real fault was the best-lap *source* (an accumulator that missed AI laps and counted
  invalidated ones), now replaced by the `SessionHistory` packet. The diagnostic line is kept (cheap,
  and F1 25's session types have surprised us more than once), but this item is closed.

### Thirty-fifth round — v1.3.1 (history readability + lap penalty cap)
- **Lap-by-lap penalty cap.** A single lap can be issued several penalties for one infringement (a
  stop-go plus two 3s time penalties), which stacked as three chips and overflowed the EVENTS cell
  (see a real sprint export). Now only the **most severe** penalty is kept as that lap's chip
  (`_pendingPenaltySeverity` + `PenaltyLapEventSeverity`, ranked stop-go > drive-through > time,
  matching the penalties widget). The full set still lives in the penalties tab. Forward-only, like
  the penalties-tab capture - a race saved before this keeps its baked chips.
- **History readability pass.** The detail view (and the mirrored HTML export) had a lot of empty
  space on short races with small, dense tables. Both lists already scroll *internally* (the page
  never scrolls - the user's constraint), so the fix was purely legibility: classification and
  lap-by-lap fonts/row-heights bumped (~12→14, taller rows), section headings 11→12, penalties 12.5→
  13.5, export table 13→14 with `9px` row padding. Widened the classification **Best** column
  62→74 - the bigger font was clipping the leading minute digit ("1:37.236" → ":37.236"). Verified by
  rendering: the app fits one screen with no page scroll and no clipping; the export reads clearly and
  its short-race lap-by-lap simply ends after its rows (inherent - a 7-lap sprint has 7 rows).

### Thirty-sixth round — history tyre-marker alignment + widget-header alignment
- **Tyre-strategy pit marker realigned to the in-lap (history only).** The user saw the strategy
  bar's pit marker sit a lap earlier than the lap-by-lap "IN" tag for the same stop. Root cause
  confirmed from the pit-debug log: the two are different sources - the bar's marker comes from the
  game's `TyreStintsEndLaps` (the last *green-flag* lap on the compound, L6), while the IN/OUT tags
  come from live `DriverStatus` (the lap you drove into the pit lane, L7). Both correct, off by one
  by definition. **The live race-tab bar was already aligned** (it's built from live lap counts, not
  `TyreStintsEndLaps`) - the user confirmed only history differed, which is exactly the path fixed.
  `SavedRaceView.AlignStintsToInLaps` now moves each stint boundary onto the matching in-lap taken
  from the saved IN tags (data-driven, not a blind +1, so it survives pit-lane/start-line geometry),
  applied only when the IN-tag count matches the boundary count (else the game's end-laps are left
  as-is). Feeds both the in-app bar and the HTML export. Verified: Madrid's two stops now mark L7 and
  L19, matching the IN tags (were L6/L18).
- **All widget headers pinned to the top.** The catalog widgets (Tyres, Car Condition, Penalties &
  Flags, Session & Track) and the two short history cards (Penalties, Your Tyre Strategy) wrapped
  everything in a `VerticalAlignment="Center"` StackPanel, so on a tall shared-height card the *title*
  floated to the middle. Restructured each to a two-row Grid: header in an `Auto` row at the top, the
  readout in a centered `*` row below - so every card's title lines up while its content stays
  vertically centred. (The classification / lap-by-lap history cards already used a header-on-top
  grid.) Verified by rendering the Race catalog and the history detail.

### Thirty-seventh round — best-lap source switched to SessionHistory; Safety Car confirmed
Two long-running open items closed off the same debug log the user sent back.
- **Safety Car banners CONFIRMED live.** The `SAFETYCAR` diagnostic captured a real caution firing
  the full sequence: `VirtualSafetyCar Deployed` → `FullSafetyCar Deployed` → `Returning` →
  `Returned` → `ResumeRace`. All four `SafetyCarEventType` values fire as documented, so the state
  machine (§8) is trusted and the temp `SAFETYCAR` log line is removed.
- **Practice/Qualifying board sort - root cause found and fixed.** The user reported the timing board
  not ranking by fastest lap (first "sprint qualifying", now practice too). The `SESSION` diagnostic
  **cleared the preset theory**: `Practice1→Practice`, `ShortSprintShootout→Qualifying`,
  `ShortQualifying→Qualifying` - the mapping was right all along. The real fault was the **best-lap
  source**: `_carBestLapMs` was accumulated from each car's `LapData.LastLapTimeInMS`, which (a)
  depended on catching every AI car's lap boundary and (b) **included invalidated laps**, so the app
  could rank a car by an off-track lap the game's own screen discards. Switched to the game's
  `SessionHistoryDataPacket` (`BestLapTimeLapNum` → the lap history's best `LapTimeInMS`) - the exact
  per-car best the in-game timing screen uses, sent round-robin, needing no accumulation and
  respecting lap validity. Now the sole feed for `_carBestLapMs`; benefits the Practice board, both
  Qualifying boards, and the Race tower's fastest-lap marker. The `LapData` accumulator is removed.
  **Not yet re-verified live** (needs a session with a full field) - but it's the authoritative
  source, so the board should now match the game's timing screen exactly.

### Thirty-eighth round — the double-OUT pit bug SOLVED, plus penalty colours
The long-running §6 pit-tagging bug, finally caught with hard evidence from a user screenshot plus
the pit log, and fixed at the root - along with several things that turned out to be the same bug.

- **Root cause of the double-OUT.** A race showed both pit stops tagged **OUT / OUT** with no IN row.
  The log gave the exact mechanism: the whole stop fits inside ONE lap where the pit exit lies
  *before* the start/finish line, and `DriverStatus` flips `InLap → OutLap` **seconds before the lap
  completes** (stop finished 21:58:10, lap completed 21:58:15). `ClassifyLapTag` only ever looked at
  the previous tick, saw `OutLap`, and tagged the in-lap "OUT"; the following out-lap was then "OUT"
  too. It's **track-dependent**, which is why it hid for so long - where the pit lane straddles the
  line the status is still `InLap` at the crossing and tagging was correct.
- **Fix:** `CarLapTracker.SawInLapThisLap` latches the moment `DriverStatus` reads `InLap` anywhere in
  the lap; `ClassifyLapTag(status, sawInLap)` prefers that latch. Correct for both pit-lane geometries.
- **Two symptoms fell out of the same fix.** (1) The **missing stationary stop times** -
  `PatchMostRecentInRowPitTime` looks for a row tagged "IN" and silently bailed when none existed, so
  the 7.601s / 2.427s stops were dropped (the times that *did* show were the pit-LANE traversal,
  which attaches to the OUT lap by a different path). The patch runs *after* the row is created, so a
  correctly-tagged IN row now receives them. (2) The saved-race **tyre-marker alignment**
  (`AlignStintsToInLaps`) needs IN tags and had been silently falling back.
- **Live tyre-bar marker (separate bug, same symptom).** The live bar marked the stint *boundary*
  (L4 / L13) rather than the lap pitted (L5 / L14). A stint following a pit now starts the lap AFTER
  the fitted lap, so the pit lap belongs to the outgoing stint - matching the lap-by-lap IN tag and
  the saved-race bar. The opening stint is exempt (it starts on lap 1) and the bar still sums to race
  distance.
- **Grand Prix name + country flag** added above the lap counter in the tower - small and muted so
  "LAP X / Y" stays the hero. Uses the same `TrackNames`/`FlagPalette` pair as the history cards, and
  hides until a session packet has named the track.
- **Penalties: colour now carries the category.** Red = a real penalty, amber = a warning, applied
  consistently across the live widget, the history card, the HTML export **and** the lap-by-lap "!"
  badge. Because the colour says which it is, the wording drops the redundancy: `Warning - Track
  limits` → `Track limits`, `Time penalties: +6s` → `+6s` (unserved ones keep the word "unserved" -
  that's the actionable part, not a category restatement). This needed the list to carry its category
  to the UI, so `PenaltiesIssues` went from bare strings to `PenaltyEntry` (text + IsPenalty + the
  two brushes); `CapIssueList` became generic so Car Condition still shares it.
- **Warnings now appear in the lap history** (new `LapEventKind.Warning`), joining the same
  one-chip-per-lap severity cap as penalties (stop-go > drive-through > time > warning), so the
  EVENTS cell still can't overflow the way stacked penalties once did.
- **No compatibility layer.** A category-carrying penalty list can't be read from the old text-only
  saves, so this was briefly built with a legacy field + fallback. The user then cleared the race
  history outright, which removed the reason for it: the legacy field, the fallback, and every
  "races saved before…" comment are gone, and the field took its natural name (`Penalties`). The
  penalties path is now single-source: capture → `SavedPenalty` → `PenaltyEntry`.
- **Verified:** builds clean, the app and history render correctly, and old-save loading was checked
  before the history was cleared. **Everything in this round still needs one live race** - the IN/OUT
  tags, recovered stop times, tyre-bar marker, GP name/flag and the red/amber chips all only exercise
  with real telemetry. With the history now empty, the next race is a clean first data point.

### Thirty-ninth round — lap numbers come from the game; red-flag restarts get their own chip
Triggered by the first live sprint after v1.3.3 (Chinese GP, 7 laps, red-flagged). The user reported
three symptoms and correctly diagnosed two of them; the log settled all three.

- **Partial validation of §38 (from the same screenshot).** The **GP name + country flag** render
  correctly, and the **amber warning chip** appears in both the lap-by-lap and the Penalties & Flags
  widget. The pit-tag work is still *unvalidated* - this sprint had no pit stop, so `SawInLapThisLap`,
  the recovered stationary stop times and the tyre-bar marker were never exercised. Monaco still owed.
- **Symptom: "laps are missing and the totals don't line up".** A 7-lap sprint showed a 5-row history
  ending at "Lap 5" while the tower read "LAP 7 / 7". **Not** a mid-race wipe, which was the obvious
  suspect (and the author's first theory) - the log has exactly one session reset, at the sprint's
  start, and none after. The real cause: history rows were numbered by a private counter,
  `_lapNumberForHistory++`, that had no connection to the game's lap number. The red-flag restart
  handed the app a fresh `SessionUID` with the race already at lap 4, so the first lap it ever saw
  (`lap=4` in the log, a 4:07.669 lap - the stoppage counted into it) displayed as "Lap 1" and every
  later lap inherited the offset. The tower reads telemetry directly, so the two disagreed.
- **Fix:** rows take the game's number - `Math.Max(1, (int)tracker.LastSeenLapNum)`, read *before* the
  `lapNumberAdvanced` block rolls it forward. `LastSeenLapNum` is the lap that was in progress, which
  is correct in both cases with no special-casing: mid-race `CurrentLapNum` has already advanced past
  the completed lap, and on the final lap it never advances at all (the final-lap registration
  mechanism from §5's fifth round, confirmed live by this same session - see §6).
  `_lapNumberForHistory` is gone. Genuinely missed laps now show as an honest gap instead of silently
  renumbering everything after them.
- **This also fixes tyre-bar alignment on any late join.** `AlignStintsToInLaps` matches IN laps
  against `TyreStintsEndLaps`, which are *game* lap numbers - it had been comparing them to the app
  counter, so alignment silently failed whenever the app didn't witness lap 1. Same scale now.
- **Symptom: the restart lap tagged "OUT".** Confirmed as the user guessed - a red-flag restart
  releases the field from the pit lane, so the game really does report `DriverStatus.OutLap` for that
  whole lap (`23:24:50 driver=OutLap` → `23:26:34 LAP_COMPLETE tag=OUT`). The tag was *literally* true
  but implied a pit stop that never happened.
- **Fix - deliberately NOT a new PIT tag.** The first design was a "RESTART"/"RED" badge in the PIT
  column; that was rejected on review. That column means "you pitted", so any word in it keeps
  asserting a stop, and its two fixed-width badges (38px live, 30px history) force an abbreviation.
  The EVENTS gap already holds precisely this category - race control acting on the session (SC, VSC,
  Red Flag, Chequered) - and is variable-width. So the restart lap gets a green **⚑ Restart** chip
  (new `LapEventKind.Restart`) and the misleading tag is dropped. It sits directly under the red
  **⚑ Red Flag** chip on the lap above, so the pair reads as one story.
  Green is `#9BE0A5` - not a new colour, but exactly the alert banner's "Racing resumes"
  (`TimingColorPalette.AlertGreenText`), whose own comment already calls green the universal restart
  colour.
- **Detection:** `_awaitingRedFlagRestart` latches on the `RedFlag` event and is consumed by the first
  lap that would have been tagged "OUT"; cleared on session reset. Safe against the red-flagged lap
  itself consuming it - that lap completed as `driver=OnTrack` in the log. The pit-debug log records
  `tag=RESTART`, so Monaco validation still reads cleanly.
- **Free across all three surfaces.** Everything routes through the shared `LapEvent`, so the history
  panel and HTML export needed no changes - the exporter already takes `ev.TextBrush` for colour and
  falls through to `⚑` for the glyph. `SavedLapEvent` stores the kind by **name** (`Kind.ToString()` /
  `Enum.TryParse`), not ordinal, so members may be added or reordered but must never be renamed
  without migration. (An earlier comment in `LapEvent.cs` claimed the opposite and was corrected.)
- **Backfill declined.** `SessionHistory` carries times for the two laps the app never saw, so they
  could be reconstructed - but events and IN/OUT tags can't be, so they'd be bare rows. The user chose
  not to; the honest gap is preferred.
- **Verified:** builds clean. Needs a live red-flagged session to confirm the Restart chip end to end.

### Fortieth round — strict full-repo audit (dead code, mappings, privacy) before release
A deliberately adversarial pass over all 58 tracked files: analyzer-driven rather than by eye, with
every "finding" verified before being acted on. Several suspicious-looking results turned out to be
correct code and were left alone - recorded here so the same false alarms aren't re-investigated.

- **Method.** Ran the full .NET analyzer set (`-p:AnalysisMode=All -p:EnforceCodeStyleInBuild=true`),
  which is far stricter than the normal build. Most of its ~650 hits are library-design rules that
  don't apply to a single-assembly WPF app (CA1515 make-types-internal, CA1305 culture, CA1002
  don't-expose-`List<T>`, CA2007 ConfigureAwait) and were dismissed as a class, not individually.
- **REAL BUG - leaked `CancellationTokenSource` per reconnect.** `UdpListenerService.Stop()` cancelled
  the CTS but never disposed or nulled it, and `Start()` overwrites the field - so every
  Disconnect→Connect cycle abandoned a live one holding a kernel wait handle. Now disposed and nulled.
- **REAL BUG - leaked CTS per failed connect (found while fixing the above).** `Start()` created the
  CTS *before* `new UdpClient(port)`, which is the line that actually fails when another telemetry
  tool holds the port. The throw left the CTS unreachable - one leaked per retry, and retrying a busy
  port is exactly what the Connect button invites. `Start()` now builds both locally and only
  publishes to the fields once both succeed, disposing the CTS on the bind failure before rethrowing.
- **UDP port now released on window close.** `Stop()` was only ever wired to the Disconnect button, so
  closing the app left the socket bound until process teardown. Harmless in isolation, but a fast
  close-and-relaunch could hit the "another app is already bound to this port" failure the README
  troubleshoots - against itself. `OnClosing` now stops it, before the existing early-return so it
  runs on both paths, and `Stop()` only raises `Stopped` if it was actually running so a defensive
  call can't emit a spurious "disconnected" to the UI.
- **Equality contract inconsistency (CA1067).** `CarStanding` and `RaceStanding` implemented
  `IEquatable<T>` with no `Equals(object)` / `GetHashCode`. **Not an active bug** - the only consumer,
  `CollectionUnchanged<T>`, is constrained `where T : IEquatable<T>`, so the typed overload binds. But
  any hash-based or non-generic path (`Distinct`, `HashSet`, `object.Equals`) would silently have used
  reference identity and disagreed with `Equals`. `PenaltyEntry` already implemented the full correct
  trio, so the other two were brought in line with it rather than the reverse.
- **P/Invoke hardened (CA5392).** `DwmSetWindowAttribute` now pins `DllImportSearchPath.System32`, so
  a same-named `dwmapi.dll` beside the .exe can't be preloaded instead - relevant because Velopack
  installs per-user into a user-writable directory. Its three ignored HRESULTs (CA1806) are
  **deliberate and now documented in-code**: cosmetic dark-chrome attributes, unsupported on older
  Windows builds, where the right behaviour is default chrome rather than a failed startup. This is
  the only analyzer hit knowingly left standing.
- **Mappings verified exhaustively against the API dump, not by eye.** All complete, no staleness:
  `Track` 28/28 real members covered by *all three* switches (`GrandPrixFor`, `CountryCodeFor`,
  `CircuitFor`); `SessionType` 17/17 mapped with `Unknown`/`TimeTrial` falling to `Unsupported`;
  `InfringementType` all 55 labelled. The `Track`→3-letter-code→`FlagPalette` chain is exactly **1:1
  in both directions** - 21 codes emitted, 21 drawn, no code without a flag and no flag never emitted.
- **Two false alarms, both correct code - do not "fix".** (1) `TeamNames` doesn't map the F2 teams,
  APXGP or `F1Generic`; that's the documented `_ => team.ToString()` policy for anything off the F1
  grid, not a gap. (2) `ClassRowView`/`LapRowView` appear unreferenced outside their own file because
  they're only ever element types of the `Classification`/`Laps` collections, reached through
  DataTemplates and the exporter - the type names legitimately never appear elsewhere.
- **Dead-code sweep came back empty.** No unreferenced `x:Key` resources, no `TelemetryState` public
  member unused by XAML or code-behind, no unreferenced types, no commented-out code blocks. The only
  `TEMP` markers are the pit diagnostic, all cross-referenced to §6 and deliberately still shipping
  until Monaco.
- **Documentation audit — mechanical, not by eye.** Every code identifier the docs reference in
  backticks (private fields, `Method()` names, `*.cs`/`*.xaml` files) was extracted and checked to
  still exist in the codebase. Three didn't: `_lastForecastSamples` and `PositionListUnchanged` both
  turned out to be already-handled (the first carries an explicit "superseded, no longer exists"
  annotation; the second's replacement by `CollectionUnchanged<T>()` is recorded in the audit round
  that did it) - the convention working as intended. `_lapNumberForHistory` was the one genuine
  miss, left dangling by the thirty-ninth round; now annotated at its original mention rather than
  rewritten, per this file's rule about historical rounds.
- **The install-directory note was materially WRONG and is corrected in place** (see §2's release
  steps). It claimed nothing writes to `%LocalAppData%\F1RaceEngineer\` once Track Map was removed;
  in fact `RaceHistoryStore`, `WindowStateStore` and the pit log all do, inside Velopack's own
  install root. Updates are empirically safe, but **uninstall takes the saved race history with it**,
  and the "already installed" false positive can recur - most easily for the project owner, since a
  `dotnet run` dev session creates that folder before any install exists. Recorded, with a possible
  fix (a separate data root + migration) explicitly deferred rather than quietly applied mid-audit.
- **README re-verified claim by claim against the code**, not assumed: the alert banner's full set
  (red flag, retirement, penalty, SC/VSC incl. "Racing resumes" and the peel-off/in-the-pits states,
  team-mate-in-pits, chequered), the tower's shared penalty/fastest-lap badge slot, and the timing
  board's Practice+Qualifying scope all match what the code actually does. Updated for this batch:
  the green restart marker and game-sourced lap numbering.
- **Privacy pass.** No tokens, credentials, email addresses or absolute machine paths in any tracked
  file (the GitHub PAT used for releases is only ever passed inline to `vpk upload`, never written to
  disk). Four references to the author's own cloud-sync folder were genericised to "the project
  folder, which may be cloud-synced" - the *reason* (don't write app data into a synced directory) is
  the part worth keeping, the vendor name isn't. `%LocalAppData%` / `%USERPROFILE%` were already
  correctly used as variables throughout, including in the API dump's header. `LICENSE` keeps the
  author's public GitHub handle, which is intentional - MIT needs a named copyright holder.

## 6. Known caveats — built, but not yet trustworthy

Everything in this section is shipped and *looks* right, but has either not been verified
against live game data or is known to have an open edge case. Referenced throughout as "§6".

- **Red Flag auto-clear is an untested heuristic.** There is no confirmed "flag
  cleared" event in the API, so it shows on its trigger event and auto-hides after a
  15s timeout. NOT yet tested against a real red flag. Safety Car / VSC, by contrast,
  are driven continuously by `SessionDataPacket.SafetyCarStatus` and self-clear
  correctly — **confirmed live** (VSC triggered and displayed correctly in a real
  race). Chequered Flag's own 8s timeout **is now confirmed correct** (see §5, seventh
  round) - it was firing right on the finish line all along, just getting cut short by
  an unrelated bug, now fixed. The same fix was applied to Red Flag pre-emptively
  since it shares the identical mechanism, but that half is still unconfirmed against
  a real red flag specifically. **Partially closed (§5, thirty-ninth round):** a real
  red flag has now occurred (Chinese GP sprint) and the `RedFlag` event *does* fire and
  correctly mark its lap with the chip. The banner was no longer showing by the time the
  session was reviewed, so it did clear - but nothing captured *when*, so the 15s timeout
  itself is still unmeasured. Watch the banner directly at the next red flag.
- **`CarPosition == 0` is used to filter inactive car slots** in the position list —
  a reasonable inference from the API's general pattern, not explicitly documented.
- **Tower's pending-penalty badge (§5, twelfth round) is reasoned from field semantics,
  not yet re-confirmed live.** `Penalties > 0` / unserved drive-through / unserved
  stop-go are read directly from `LapData` and already used elsewhere for the player's
  own Penalties & Flags widget, but generalizing them to every car in the tower hasn't
  been checked against a real pending penalty on a rival car. Watch the next penalty
  situation specifically.
- **Final-lap registration fix (§5, fifth round) — CONFIRMED LIVE (§5, thirty-ninth
  round).** The fix assumed `LastLapTimeInMS` reliably changes on the final lap even
  though `CurrentLapNum` doesn't advance. The Chinese GP sprint log shows exactly that
  mechanism firing: two consecutive completions both at `lap=7` (`23:29:50 lastLap=97801`
  then `23:31:30 lastLap=100841`), the lap number never advancing past 7, and the final
  lap still reaching history with its full time and all three sectors plus the Chequered
  chip. Independently verified against a second real session finish - no longer an
  assumption.
- **Session-restart fix (§5, ninth round) is reasoned from the reported symptom and a
  confirmed field (`SessionUID`), not yet re-confirmed live.** Comparing `SessionUID`
  alongside `SessionType` should catch a same-type restart that a `SessionType`-only
  check misses, but hasn't been independently re-tested against a second deliberate
  restart. Watch the next session restart specifically.
- **Lap History pit in/out tagging + stop time — ROOT CAUSE FOUND AND FIXED (§5
  thirty-eighth round); awaiting one live race to confirm.** The bug was finally captured
  outside Monaco with a screenshot plus the pit log, and the mechanism is now proven rather
  than theorised: where the pit exit lies BEFORE the start/finish line the whole stop fits
  inside one lap and `DriverStatus` flips `InLap → OutLap` seconds before that lap completes,
  so the previous-tick read tagged the in-lap "OUT". `CarLapTracker.SawInLapThisLap` now
  latches the in-lap the moment it's seen anywhere in the lap, which also restores the lost
  stationary stop time (the patch had nothing tagged "IN" to write into). **Keep the pit
  logging until a Monaco stop confirms the fix on the other pit-lane geometry**, then remove
  it. The historical analysis below is retained for context.

  Reported live at Monaco: a pit stop produced a **double "OUT"**
  (both the in-lap and the out-lap tagged OUT, no "IN") and the **stationary stop time
  was missing** (only the total pit-lane time on the OUT row appeared). Root cause: the
  lap tag is read from the DriverStatus one tick before the lap completes
  (`ClassifyLapTag`), which is correct only when the pit entry is AFTER the line (the
  in-lap completes as `InLap`). At Monaco the pit straddles the line, so the in-lap
  completes as `OutLap` (status already flipped during the stop) -> tagged "OUT". And
  the stop-time patch (`PatchMostRecentInRowPitTime`) fires when the pit finishes, which
  at Monaco is BEFORE the in-lap row even exists -> nothing to patch -> stop time lost.
  The hard part: a Monaco in-lap and a normal-track out-lap can look identical (both end
  as `OutLap`, both contain a full pit stop); the only distinguisher is the exact
  DriverStatus/PitStatus timeline relative to the line, which varies by track and hasn't
  been observed. So rather than guess a tagging fix (high regression risk on the
  currently-working normal-track case), **temporary per-tick pit logging was added**
  (`LogPitEvent` -> `%LocalAppData%\F1RaceEngineer\pit-debug.log`, shipping in v1.0.3):
  it records the player's DriverStatus/PitStatus/PitLane/lap-tag timeline on every
  pit-relevant state change + lap completion, so a provably-correct fix (robust to the
  line falling before OR after the stop) can be built from real data across several
  tracks. REMOVE the logging once the fix lands and is confirmed live. The prior
  patch-based theory (below) is superseded by this finding - a forward-store for the
  stop time is now known to be needed for the Monaco case.
  - **CORRECTION (§5 twenty-fifth round, from reviewing the collected log - 7 real
    stops).** Two things above are wrong and cost the reader a false mental model:
    1. **Straddling the S/F line is the NORMAL case, not a Monaco quirk.** All 7 logged
       stops straddle it: the car enters the pit lane on lap N, crosses the line 0.9-6.5s
       later *while still in the lane*, and does the stop on lap N+1. There is no
       observed "normal track" where the pit is entered after the line.
    2. **The IN-row stop time is therefore NOT broken on straddling tracks** - it is
       confirmed working on 7 of 7 of them (box times 2.370-2.456s, plus two 7.6s stops
       that were front-wing changes; the OUT-row lane time landed every time too).
    So the real distinguisher is **not** where the pit entry sits relative to the line -
    it is **whether the line is crossed BEFORE or AFTER the stationary stop**. Every
    logged stop is "line first, then stop", which is exactly why it works: `DriverStatus`
    is still `InLap` at the line (so `ClassifyLapTag` tags IN correctly), and the IN row
    already exists by the time the stop finishes (so `PatchMostRecentInRowPitTime` has a
    row to patch). The Monaco report implies the opposite order - stop first, status
    flips to `OutLap`, *then* the line - which produces the double-OUT. **That ordering
    appears nowhere in the log**, so the bug is still unreproduced and any fix would be
    guesswork. Capture a Monaco stop before touching `ClassifyLapTag`; the currently
    working 7/7 path is the regression risk.
- **OUT row's total pit-lane time (§5, thirteenth round) - CONFIRMED working** on a
  normal track. The Barcelona pit log (§5 twentieth round) showed the forward-store fire
  correctly (`STORE_LANE_TIME laneT=19307`), and the OUT row displayed it. Unlike the IN
  row there's no timing ambiguity - the pit lane is always exited before the OUT lap
  starts, so the forward-store is always ready in time. (The IN-row stationary stop time
  is **also confirmed** as of the twenty-fifth round's log review - 7 of 7 stops - so
  this is no longer the "one still broken" case; see the correction in the pit-tag entry
  above for what actually remains unproven.)
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
  leaving a dead gap. Column count is derived from the available **width** (`ColumnsFor`:
  ~400px per card, floored at 2 columns, never leaving a single widget stranded alone on
  the last row) — **superseded** the original count-based rule (1 → 1 col, 2–4 → 2, 5+ → 3),
  which sized cards by how many widgets were on rather than how much room they had: at a
  maximised window that stretched each card to ~800px with its content filling barely half.
  See §5 twenty-ninth round. Rows are auto-height
  (not stretched), so a short one-line widget (e.g. Car Condition when "OK") hugs its
  own content instead of stretching into an empty-looking card next to a taller
  neighbour.
- Lap Timing's history list lives in an **elastic viewport** (a star row; §5 twenty-ninth
  round **superseded** the original fixed 12-row `Height`). It takes whatever height the
  window has left after the catalog reserves its own, so a maximised window shows ~17 laps
  instead of leaving dead space, and a short one yields rather than pushing the widgets
  below off-screen. It still never sizes to the lap *count* - the viewport scrolls (a
  themed scrollbar - see §5 seventeenth round) - so the original reason for the fixed
  height still holds: nothing below it shifts as a race goes long. The full race history
  is retained; no lap is ever dropped. There is deliberately **no MinHeight** on it - see
  the twenty-ninth round for why a floor can't work in a star row.
- Catalog widgets default **on** for Race only. Practice and Qualifying default to just
  their core (the field board + Lap Timing) with everything else off, toggleable if
  wanted. The position board (`PositionListWidget`) shows in both Practice and
  Qualifying - both are timed leaderboard sessions where the driver wants to see where
  they rank, matching real F1 practice/quali timing screens (§5 nineteenth round). Lap
  History is a toggle on **every** preset now (on by default everywhere, Qualifying
  included - §5 nineteenth round unified this).

### Always-on core (UNIFORM across presets since §5 nineteenth round)
Every preset has the same two-part structure: a **full-field view in the LEFT column**
(fixed 436px, §5 thirtieth round - was 400 before the tower's tyre-age and position-delta
columns), and the **same Lap Timing widget (with history) filling the RIGHT
column**. Only the left-column occupant differs:
- **Race**: the **Race Position Tower** (live interval/gap, tyres, PIT/Out, badges).
- **Practice / Qualifying**: the **position/timing board** (`PositionListWidget` - best
  lap + gap to fastest), in the exact same place/width as the tower.
- The tower and the board share one Auto-width column and are mutually exclusive per
  preset (`MainWindow.UpdateWidgetVisibility`). There is no longer a separate
  history-less Qualifying Lap Timing instance - the single `LapTiming` widget is used
  everywhere, with the Lap History toggle on by default (Qualifying's old
  "hotlap-focused, no history" rule was dropped for uniformity at the user's request).
- **Cold start**: before the first packet arrives (default `Unsupported` preset, no data
  yet) a "Waiting for telemetry" placeholder overlays the whole widget area, so launch
  doesn't show a bare half-populated layout (§5 twenty-first round). It's gated on
  `Unsupported preset AND !HasReceivedData`, so it steps aside for a settings-menu
  preview (forces a concrete preset) and for a live Time Trial (Unsupported but sending).

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
  full-field tower shown to the left of everything else in Race. Directly under it, a
  broadcast-style purple "FASTEST LAP - {driver} {time}" strip (§5 twenty-first round -
  `HasRaceFastestLap`/`FastestLapDriver`/`FastestLapTimeText`, sourced from the same
  `_carBestLapMs` holder the per-row badge uses; hidden until a lap is set). The tower
  rows themselves show: position (with a ▲/▼ places-gained-vs-grid delta beside it, §5
  thirtieth round), livery swatch, driver + team, Interval (gap to car ahead), Gap (to
  leader), tyre compound letter paired with its age in laps (§5 thirtieth round; the letter
  is plain colored text, no background badge - see §5 tenth round), and one shared
  badge slot for either a red "!" pending-penalty badge (§5 twelfth round) or a purple
  stopwatch fastest-lap badge (§5 thirteenth round) - mutually exclusive by design, a
  pending penalty always wins if a driver has both (confirmed with the user, matches
  the alert banner's own actionable-beats-informational priority elsewhere in the app).
  Int-Gap and Gap-Tyre/Badge gaps are real fixed-width spacer columns, not alignment
  slack, so they stay visually identical regardless of content width (§5 twelfth round)
  - no reserved/unused column left in the tower after this. Retired/DNF/DSQ/
  not-classified cars show a dimmed row and a single "Out" instead of a stale
  interval/gap (`LapData.ResultStatus`, §5 tenth and thirteenth rounds - the field was
  originally set on both Int and Gap, rendering as "Out Out"). A car currently in the
  pit lane (`LapData.PitStatus != None`) shows a blue "PIT" in the Gap column in place
  of the (unreliable-while-stopped) interval/gap, reverting to the live gap
  automatically the moment it rejoins the track - NOT dimmed like "Out", since the
  driver is still racing (§5 nineteenth round). "Out" wins over "PIT". See §8 for why
  this replaced the old Gaps & Position widget (removed on all presets, not just Race).

### Widget catalog (toggleable unless noted)
- **Tyres** — compound marker drawn as the broadcast-style tyre ring (coloured compound
  band around a dark tyre body, letter in the band colour; FIA colour convention, all
  vector - no trademarked Pirelli/F1 artwork, see §5 twenty-first round), tyre age,
  four-corner wear diagram. NOTE: wear is genuinely per-corner
  (`FrontLeft/FrontRight/RearLeft/RearRight` differ) — must not be collapsed to one
  number.
- **Car Condition** — quiet checkmark + "No damage" when undamaged (deliberately NOT
  a loud coloured banner - see §6 fix); otherwise each damaged component listed in
  yellow (wings, floor, diffuser, sidepod, gearbox, engine, per-corner brakes,
  per-corner tyre blisters, DRS/ERS faults, engine blown/seized), laid out as a
  2-column grid (`UniformGrid Columns="2"`) so a long issue list doesn't force the
  widget taller than the viewport - see §5. Capped at `IssueListMaxEntries` (6)
  entries = 3 full rows, most-severe-first (a car's genuinely worst problems survive
  the cap, not just whichever were checked first in code), with a `"+N more"` summary
  in the last slot if truncated - see §5 fifteenth round. Background engine-component
  *wear* (as
  opposed to damage) is deliberately excluded - see §6; per-corner tyre *damage* is
  also excluded now (see §8) since the Tyres widget already shows per-corner wear and
  the two read as duplicated, not distinct, information.
- **Penalties & Flags** — same quiet/yellow-itemised, capped-and-severity-sorted
  pattern as Car Condition (same `IssueListMaxEntries`/2-column grid, see §5 tenth and
  fifteenth rounds); also shows the player's own current flag (`VehicleFiaFlags`:
  None/Green/Blue/Yellow) as a coloured chip with label (e.g. blue = let car through).
  Warnings show one itemised line per specific reason (from `PenaltyIssued` events'
  `InfringementType`), not a bare count, with duplicates of the same reason collapsed
  into an "(xN)" multiplier line - see §5 ninth round. Reason text is
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
- **Floating overlay, not an inline element** (§5 eighteenth round). It floats over the
  TOP of the right column (layered on top in a `Grid`, `VerticalAlignment="Top"`, in
  `MainWindow.xaml`) so showing/hiding it never pushes the widgets below up or down -
  it only overlays the current-lap block, and the position tower to the left stays
  fully visible. Modelled on the real F1 broadcast "status bar across the top of the
  screen" convention (Formula1.com), which overlays the picture rather than reflowing
  the graphics. Has a drop shadow so it reads as floating above the content.
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
- Of the 21 `EventType` values, 6 drive a banner (Red Flag, Chequered Flag, Penalty,
  Retirement, Team-mate in pits, and Safety Car - the last via the event state machine added
  in the thirty-fourth round, on top of the `SafetyCarStatus` poll). Most of the rest were
  reviewed and left unwired -
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
  - Layout: **re-check this before building - the premise changed.** This was written when
    Lap History had a fixed 12-row viewport, and leaned on that fixed height to make a
    single overlaid bar (not duplicated per row) realistic. §5's twenty-ninth round made the
    viewport elastic, so its height now varies with the window.
  - **Also note the user has since rejected a per-lap "pace bar" in this gap** (§5
    twenty-ninth round), so an overlaid graphic here should not be assumed welcome. The
    alternatives raised and not yet decided were data columns: position that lap, gap to
    the leader that lap, compound + age, and tyre wear.
  - Open questions before building: whether to include axis gridlines/lap-number
    labels like the real graphic or keep it to just the coloured bar + letter markers,
    and whether the open/current stint needs an explicit "current lap" tick mark.
- **Tyre age in the tower, position-vs-grid delta, tower 400→436, sector-box shrink, and
  window persistence — all BUILT (§5 thirtieth round).** These five were decided/mocked in
  earlier rounds and lived here as "agreed, not yet built"; the thirtieth round built them.
  The design rationale that used to sit in this list (option B for tyre age and for the
  position delta, the ⚠️ note that B+age would land the tower near 438, the "sector boxes
  aren't coupled to the banner" safety check) is preserved in that round entry. The one
  measured surprise: the exact width came out at **436**, not the ~438 estimated here.
- **Pit-stop window widget — agreed in principle, BLOCKED on live data.** `SessionDataPacket`
  carries `PitStopWindowIdealLap`, `PitStopWindowLatestLap` and `PitStopRejoinPosition`, all
  currently unused - the most race-engineer data available ("box this lap, rejoin P7"), and a
  natural 5th catalog widget (a pit-window bar reusing the `StintStripRenderer` idea, plus
  rejoin position as the hero number). **Do not build before logging these for a real race**:
  it's unverified that the game populates them outside certain modes, and this project has a
  history of theories that didn't survive contact with live data (see the pit-tag saga, §6).
- **DRS indicator — proposed, undecided.** `DrsAllowed`, `DrsActivationDistance` and
  `DrsFault` are all received and unused.
- **Weather forecast timeline — proposed, undecided.** Currently a single "next change"
  callout; a small timeline across the forecast samples would read like a pit-wall rain radar.
- **Two-player career support — AGREED IN PRINCIPLE, design conversation paused mid-flight
  (resumes next session). Do not build ahead of that conversation.** The user plays two-player
  career with friends often and explicitly does not want a solution that relies on exchanging
  files between machines.
  - **The enabling discovery: no file exchange is needed at all.** In a two-player career both
    humans are in the *same session*, and `SessionHistoryDataPacket` is **per car** (`CarIndex`) -
    it carries the full lap history (up to 100 laps) with lap time, **all three sector times**,
    validity flags, best-sector lap numbers **and** tyre stint history. The app already receives
    and handles this packet but currently mines it only for best laps (§5 thirty-seventh round).
    So the other player's complete lap-by-lap and strategy is already arriving and being discarded.
  - **Detecting the mode, two independent ways.** `SessionDataPacket.GameMode` has explicit values
    `Career25Online` (two-player career) and `SplitScreen` (local), alongside `DriverCareer25` /
    `MyTeamCareer25` / `ChallengeCareer25` and the online/TimeTrial/StoryMode entries. Independently,
    `ParticipantData.IsAiControlled` is per-car, so counting non-AI entries identifies the humans
    without trusting the enum - the same belt-and-braces approach the sprint/race lap-count check
    uses after F1 25 mislabelled session types.
  - **AGREED to build:** (1) **highlight both human players in the Race Position Tower** - today only
    the player's own row is marked, so a second human is indistinguishable from 19 AI cars, and this
    gates everything else; (2) **store the other human's lap-by-lap and stints in `SavedRace`**
    alongside `PlayerLaps` (roughly doubles a race file - negligible, the whole history is ~24KB).
    The user restated the highlight requirement under the history item too, which reads as wanting
    both humans marked in the **saved race's classification view** as well as the live tower -
    confirm which surfaces next session.
  - **AGREED in principle but PRESENTATION UNDECIDED - specify before building:** a head-to-head tab
    in the saved-race detail (sector deltas, median race pace, consistency, the two strategy bars
    stacked via `StintStripRenderer`), and a **season head-to-head** strip ("You 7 - 5 Alex" across
    race finishes, plus qualifying and best-lap H2H, in the existing `SeasonGroupView`). The user
    wants to work out exactly how these read before any of it is built.
  - **DEFERRED, "maybe later":** a pinned always-visible gap to the other human (independent of
    position), and a live during-the-race sector head-to-head.
  - **Open question to settle:** with 3+ humans, is there one pinned "rival" or are all tracked?
  - **Build nothing before one logged two-player session confirms the theory** - specifically that
    `IsAiControlled` marks both humans and that `SessionHistory` populates for the other player's
    car. All of the above is reasoned from the API dump, and this project has been burned by exactly
    that before (the pit-tag saga, §6, and the sprint/race session-type inversion).
  - **Superseded idea, recorded so it isn't re-proposed:** exporting/importing race JSON between
    friends, plus capturing the "fairness fingerprint" (`AIDifficulty`, the ten assist flags,
    `RuleSet`, `IsNetworkGame`) needed to make cross-machine comparisons honest. That solves the
    *general* case - two different races on the same track, different difficulty and assists - which
    the same-session case makes unnecessary. Note those fields are still captured nowhere, and
    assists are session-level (they describe *your own* settings, not per-driver), so they remain the
    only route if a general comparison is ever wanted.
- **Uninstall-time race-history rescue — investigated and REJECTED by the user, don't rebuild
  without new information.** Uninstalling deletes the saved race history (see §2's release-steps
  correction). Several shapes were designed and all were declined: a tickbox during uninstall, an
  in-app pre-warning, and an unconditional export to the Desktop. The blocking technical facts, so
  this isn't re-derived from scratch:
  - Velopack 1.2.0 **does** expose `OnBeforeUninstallFastCallback` (confirmed by inspecting the
    shipped DLL), so running code at uninstall time is possible.
  - But it **cannot cancel or abort the uninstall** - the callback returns nothing and Velopack has
    no cancellation API for it (the only `cancel` symbols in the assembly are generic
    `CancellationToken` plumbing and `UserCancelled` for update downloads). A *warning* there is
    therefore unactionable: it announces the data loss at the moment nothing can be done about it.
  - It is a **"Fast" callback**, spawned and awaited with a `WaitForExit` timeout, so blocking on a
    human is contraindicated - an unanswered dialog gets the process killed and the history deleted
    unexported, failing in exactly the case the feature would exist for.
  - Anything built here is also near-untestable without repeated real install/uninstall cycles
    against a throwaway pack ID.
  The consequence stands and is documented in §2 rather than mitigated in code: **uninstalling
  removes the saved races**, and HTML export is the way to keep any of them.
- **Tyre temperature — investigated and REJECTED, don't revisit without new information.**
  The idea was to colour the tyre corners the way the game's own MFD does (blue = cold, green
  = in the window, red = overheating). `TyresSurfaceTemperature` / `TyresInnerTemperature` and
  `ActualTyreCompound` are all received and unused, so the *numbers* are available exactly.
  The *colour mapping* is not: no tyre-state/colour field exists anywhere in the telemetry,
  the thresholds aren't officially documented, community figures contradict each other
  (C3 ≈ 80-90°C vs slicks ≈ 90-105°C), and they shift with patches. The user's rule was
  "exact or not at all", so this is closed - a guessed ramp would show green while the game
  showed amber. A wear-based colour ramp is **also** rejected, and worse: it would hijack the
  game's own colours, which mean temperature, not wear.
- **Car Condition damage threshold — CLOSED, working as intended.** Early on it looked
  like damage fields (wings/floor/gearbox/engine) creep up from race start with no
  collisions, which suggested a minimum-threshold filter was needed. After more live
  racing the user confirmed the widget reads correctly as-is (the earlier "creep" was
  the background engine-component *wear* fields, which are already excluded - see §5
  third round / §6), so **no threshold is being added**; a component surfaces the moment
  it reports non-zero incident damage, which is the intended behaviour.
- **Proposed alert banners, not yet implemented** — of the 21 `EventType` values, 15
  remain unwired (6 are live - see §7, now including Safety Car). Draft text/colour/clear-behaviour
  for each, reviewed but not committed to (the Safety Car entry below is now BUILT):
  - **Safety Car event state machine — BUILT (§5 thirty-fourth round).** Implemented as a layer
    over the `SafetyCarStatus` poll (not a replacement - the poll keeps driving the steady
    "deployed" state, the `SafetyCarEvent` adds the transitions): Returning+Full → "Safety car in
    this lap - peel off" and Returning+Virtual → "Virtual safety car ending" (both sticky);
    Returned → "Safety car in the pits" (5s); ResumeRace → "Racing resumes" (green, 4s). **CONFIRMED
    live (§5 thirty-seventh round):** a real safety car fired the full Deployed → Returning → Returned
    → ResumeRace sequence exactly as designed; the temp `SAFETYCAR` diagnostic has been removed.
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
