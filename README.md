# F1 Race Engineer

A Windows desktop "second-screen" race-engineer dashboard for **F1 25** (including its
**2026 Season Pack** DLC). It reads the game's live UDP telemetry and shows
engineer-relevant data on a separate screen while you race — built so you never need
to alt-tab out of the game mid-session.

## Features

- **Lap Timing** — current lap (live-coloured), personal best, delta vs personal best
  (updates every sector, not just at the line), three sector boxes, and a 10-lap history
  with per-sector colour coding.
- **Race Position Tower** — full-field position, interval, and gap to leader, shown
  only in Race.
- **Qualifying position list** — full field with best lap and gap to pole.
- **Alert banner** — Safety Car / VSC, Red Flag, Chequered Flag, penalties, retirement,
  and team-mate-in-pits notifications.
- **Tyres, Car Condition, Penalties & Flags, Session & Track** — toggleable widgets via
  the gear-icon settings panel, with sensible per-preset defaults.
- Automatically switches between **Practice / Qualifying / Race** layouts based on the
  live session type — no manual tab clicking required.

## In-game setup (F1 25 / F1 25: 2026 Season Pack)

1. Launch the game.
2. Go to **Options → Settings → Telemetry Settings**.
3. Set **UDP Telemetry** to On.
4. Set **UDP IP Address** to `127.0.0.1` (the app runs on the same PC as the game).
5. Set **UDP Port** to `20777` (the app's default - change both together if you ever
   need a different port).
6. Set **UDP Send Rate** to `20Hz` or `60Hz`.
7. Set **UDP Format** to `2025`.
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

**Without VS Code**, once you're happy with a build:
```
dotnet build -c Release
```
This produces `F1RaceEngineer.exe` in `bin\Release\net10.0-windows\` - launch it
directly (double-click, desktop shortcut, pin to Start/Taskbar) like any other app, no
terminal needed. You'll need to re-run this command after future code changes to pick
them up; it won't happen automatically.

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
