# F1 Race Engineer

A Windows desktop "second-screen" race-engineer dashboard for **F1 25** (including its
**2026 Season Pack** DLC). It reads the game's live UDP telemetry and shows
engineer-relevant data on a separate screen while you race — built so you never need
to alt-tab out of the game mid-session.

## Features

- **Lap Timing** — current lap (live-coloured), personal best, delta vs personal best
  (updates every sector, not just at the line), three sector boxes, a scrollable
  full-race lap history (12 laps visible, scroll back for earlier laps)
  with per-sector colour coding, IN/OUT lap tags, pit stop/pit lane timing, and inline
  per-lap event markers (Safety Car / VSC, red flag, chequered flag, penalties).
- **Race Position Tower** — a "LAP X / Y" counter and a broadcast-style purple "fastest
  lap" strip (driver + time) above the full field: position, interval and gap to leader,
  tyre compound, a "PIT" tag while a car is being serviced (auto-reverting to the live
  gap when it rejoins), and a shared badge slot for a pending penalty or the session's
  fastest lap. Your own interval also shows a trend caret (green = catching the car ahead,
  red = dropping back). Race only.
- **Field timing board** — full field with best lap and gap to fastest, shown in both
  Practice and Qualifying (both are timed, leaderboard sessions).
- **Alert banner** — Safety Car / VSC, Red Flag, Chequered Flag, penalties, retirement,
  and team-mate-in-pits notifications.
- **Tyres, Car Condition, Penalties & Flags, Session & Track** — toggleable widgets via
  the gear-icon settings panel, with sensible per-preset defaults. Penalties & Flags
  reasons are labelled against real FIA terminology, not raw game field names. The Tyres
  widget also shows a live, broadcast-style **tyre-strategy bar** (your stints so far,
  colour-coded by compound).
- **Race History** — a clock icon (next to the gear) opens your saved races. Every race
  is captured automatically when it finishes: a card per Grand Prix (finish, best lap,
  points, stint bar) opening into a full detail view — final classification, your
  lap-by-lap, and a tyre-strategy graphic with pit-lap markers. Delete any race, or
  **export one to a self-contained HTML file** to share. All stored locally on your PC.
- Automatically switches between **Practice / Qualifying / Race** layouts based on the
  live session type — no manual tab clicking required.

## Getting the app

Download the latest installer (`Setup.exe`) from this repo's
[Releases](https://github.com/keviniscooking/F1-Race-Engineer/releases) page and run
it once. After that first install, the app checks for updates on every launch and
silently updates itself when a new version is published - no need to re-download.

## In-game setup (F1 25 / F1 25: 2026 Season Pack)

1. Launch the game.
2. Go to **Options → Settings → Telemetry Settings**.
3. Set **UDP Telemetry** to On.
4. Set **UDP IP Address** to `127.0.0.1` (the app runs on the same PC as the game).
5. Set **UDP Port** to `20777` (the app's default - change both together if you ever
   need a different port).
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
The app tries to connect automatically on launch using the port above - if the game's
already running and pointed at it, no further action needed. The Connect/Disconnect
button in the top bar is there for a manual attempt if that first try fails (e.g. wrong
port, or another telemetry tool already bound to it).

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
  (not a menu or replay), and confirm no other telemetry tool (SimHub, RaceLab,
  CrewChief, etc.) is already bound to the same UDP port - only one app can bind a
  given port at a time.

## License

[MIT](LICENSE)
