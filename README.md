# F1 Race Engineer

A Windows desktop "second-screen" race-engineer dashboard for **F1 25** (including its
**2026 Season Pack** DLC). It reads the game's live UDP telemetry and shows
engineer-relevant data on a separate screen while you race — built so you never need
to alt-tab out of the game mid-session.

## Features

- **Lap Timing** — current lap (live-coloured), personal best, delta vs personal best
  (updates every sector, not just at the line), three sector boxes, and a scrollable
  full-race lap history (its viewport grows with the window; scroll back for earlier laps)
  with per-sector colour coding, IN/OUT lap tags, pit stop/pit lane timing, and inline
  per-lap event markers (Safety Car / VSC, red flag, a green restart when racing resumes,
  chequered flag, car faults — amber when a DRS/ERS/engine fault appears, green when it clears —
  and infringements, red for a penalty and amber for a warning). Laps are
  numbered by the game's own lap count, so they line up with the tower's "LAP X / Y" even
  if the app joins a session late or after a red-flag restart.
- **Race Position Tower** — the Grand Prix name and country flag above a "LAP X / Y"
  counter and a broadcast-style purple "fastest lap" strip (driver + time), over the full
  field: position with a ▲/▼ places-gained-vs-grid delta, interval and gap to leader, tyre
  compound paired with its age in laps, a "PIT" tag while a car is being serviced
  (auto-reverting to the live gap when it rejoins), and a shared badge slot for a pending
  penalty or the session's fastest lap. Race only.
- **Field timing board** — full field ranked by best lap with the gap to fastest, shown in
  both Practice and Qualifying (both are timed, leaderboard sessions). Best laps come
  straight from the game's own session history, so the order matches the in-game timing
  screen exactly (invalidated laps excluded).
- **Alert banner** — Safety Car / VSC (including "in this lap — peel off" and a green
  "racing resumes" at the restart), Red Flag, Chequered Flag, penalties, retirement,
  and team-mate-in-pits notifications.
- **Tyres, Car Condition, Penalties & Flags, Session & Track** — toggleable widgets via
  the gear-icon settings panel, with sensible per-preset defaults. Penalties & Flags
  reasons are labelled against real FIA terminology, not raw game field names, and are
  colour-coded — red for a penalty you've been given, amber for a warning. The Tyres
  widget also shows a live, broadcast-style **tyre-strategy bar** (your stints so far,
  colour-coded by compound, with a marker on each lap you pitted).
- **Race History** — a clock icon (next to the gear) opens your saved races. Every race
  is captured automatically when it finishes: a card per Grand Prix (finish, best lap,
  points, stint bar) opening into a full detail view — final classification, your
  lap-by-lap, and a tyre-strategy graphic with pit-lap markers. Seasons are labelled by
  career type ("2026 SEASON · TWO-PLAYER CAREER"), so two saves from the same year are
  told apart at a glance. Delete any race, or **export a whole weekend to a
  self-contained HTML file** — race, sprint and head-to-head in one document. All stored
  locally on your PC.
- **Two-player career head-to-head** — in the game's two-player career, both drivers are
  highlighted in the position tower (you in blue, your rival in cyan), and each saved race
  gains a **HEAD TO HEAD** tab beside RESULT:
  - a verdict banner — who finished ahead and by how much;
  - a **tale of the tape** — grid, finish, positions gained, best lap, **ideal lap** (your
    best sectors combined) with a per-sector breakdown, race pace, consistency, stops,
    total time stationary in the pits, penalties and laps spent ahead;
  - a **gap evolution** chart — the gap between the two of you lap by lap, green where
    you're ahead and red where you aren't, with each driver's pit stops marked;
  - both **tyre strategies** side by side, and every pit stop with its box and pit-lane time.

  Nothing needs sharing between machines: in a two-player career both cars are in the same
  session, so your copy of the app already receives everything it needs.
- Automatically switches between **Practice / Qualifying / Race** layouts based on the
  live session type — no manual tab clicking required.
- **Waiting-screen formation lap** — while there's no live session, the empty screen comes alive:
  a formation lap of cars circulating a real, accurate circuit (drawn from survey geometry, with
  named corners and its length and first-GP year), running in your last saved race's finishing
  order and liveries — plus rotating F1 trivia. The animation stops the instant real telemetry
  arrives, so it costs nothing while you're racing.
- **Built-in setup help** — a **?** icon opens a card with the exact in-game telemetry
  settings, your current port, what the app is receiving right now, and what to check if
  nothing arrives. The "Waiting for a session" screen links to it too, so the answer is
  where the problem is.

## Getting the app

Download the latest installer (`Setup.exe`) from this repo's
[Releases](https://github.com/keviniscooking/F1-Race-Engineer/releases) page and run
it once. After that first install, the app checks for updates on every launch and
silently updates itself when a new version is published - no need to re-download.

## In-game setup (F1 25 / F1 25: 2026 Season Pack)

The app has this same checklist built in - press **?** in the top-right at any time, and
it shows your actual port alongside what it's currently receiving.

1. Launch the game.
2. Go to **Options → Settings → Telemetry Settings**.
3. Set **UDP Telemetry** to On.
4. Set **UDP IP Address** to `127.0.0.1` (the app runs on the same PC as the game).
5. Set **UDP Port** to `20777` (the app's default). To use a different one, change it in
   the game and in the app's **⚙ Settings → Connection** - the app remembers your choice
   between launches.
6. Set **UDP Send Rate** to `20Hz` or `60Hz`.
7. Set **UDP Format** to `2025` or `2026` - both work (`F1Game.UDP` 26.0.0 parses
   either schema; confirmed live against a real `2026` setting).
8. Leave **UDP Broadcast Mode** off.
9. Back out of the menu so settings save.

## Running the app

**For development** (in VS Code or any terminal):
```
dotnet run
```
The app connects automatically on launch using its remembered port - if the game's
already running and pointed at it, no further action needed, and the connection controls
stay out of the way entirely. If telemetry isn't arriving, a status line appears in the
top-left; **⚙ Settings → Connection** has the port, Reconnect and Disconnect.

**Without VS Code**, see "Getting the app" above - install once via `Setup.exe` from
Releases, then it stays current on its own. See `HANDOFF.md`'s "Cutting a release"
steps if you're the one publishing a new version, not just running the app.

## Project docs

`HANDOFF.md` is the full design log - every decision made, why, what's been confirmed
against live gameplay, what's still open, and what's deliberately deferred. Read it
before making further changes.

`docs/F1GameUDP_API_reference.txt` is a full reflection dump of every type/member in
the exact `F1Game.UDP` 26.0.0 DLL this project uses - the authoritative source for
exact property names when extending telemetry handling, instead of guessing from
online docs.

## Troubleshooting

- **Won't connect / no data**: confirm Windows Firewall isn't blocking the app (it may
  prompt on first run - allow it), confirm the game is actively in a driving session
  (not a menu or replay), and confirm no other telemetry tool is already bound to the
  same UDP port - only one app can bind a given port at a time.

  The same checklist is built into the app: the **?** button in the top-right opens a
  card with the exact in-game settings, your current port, and what the app is
  receiving right now. The "Waiting for a session" screen links to it too.

## License

[MIT](LICENSE)
