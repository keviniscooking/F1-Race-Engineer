# Waiting-screen "formation lap" — reference assets

Design work for a future feature: replace the plain "Waiting for a session…" placeholder with a
formation lap of F1 cars circulating a **real, accurate circuit**, plus rotating F1 trivia. Fully
designed and proven in a mockup; **not built into the app yet**. The full rationale and build steps
are in `HANDOFF.md` §8 ("Waiting-screen formation lap"). This folder holds the concrete assets so
the build doesn't have to re-fetch or re-derive anything.

## Live mockup
https://claude.ai/code/artifact/1537f249-2ea9-4e57-889c-9234649713a1
(`mockup.html` is the same thing, standalone — open it in a browser.)

## Files
- **`geojson/`** — 24 raw circuit outlines, one per 2026-calendar round. Source: the MIT-licensed
  `bacinger/f1-circuits` GitHub repo, itself OpenStreetMap-derived. Each is a GeoJSON `LineString`
  of `[lon, lat]` survey points (94–172 per track) plus metadata (name, length). **We draw our own
  outline from these coordinates — we do not use F1's map artwork**, so there is no licensing issue.
- **`tracks.json`** — the same 24, already projected to a 0–1000 SVG box (lat-cosine corrected,
  aspect preserved, centred). Keyed by file id (e.g. `it-1922`). Ready to render directly.
- **`corners.json`** — named corners as fraction-of-lap from start/finish. 4 of 24 done; see the
  file's own note for how the fractions were validated and how to add the rest.
- **`trivia.json`** — 110 curated F1 Q&A, evergreen-biased. Refreshed by hand (like a content
  bundle); no live source needed.
- **`mockup.html`** — the standalone visual reference (data inlined).

## Track id → game `Track` enum (for wiring at runtime)
Melbourne→au-1953 · Shanghai→cn-2004 · Bahrain→bh-2002 · Catalunya→es-1991 · Monaco→mc-1929 ·
Montreal→ca-1978 · Silverstone→gb-1948 · Hungaroring→hu-1986 · Spa→be-1925 · Monza→it-1922 ·
Singapore→sg-2008 · Suzuka→jp-1962 · AbuDhabi→ae-2009 · Texas→us-2012 · Brazil→br-1940 ·
Austria→at-1969 · Mexico→mx-1962 · Azerbaijan→az-2016 · Zandvoort→nl-1948 · Jeddah→sa-2021 ·
Miami→us-2022 · LasVegas→us-2023 · Qatar→qa-2004 · Madrid→es-2026

**Not fetched yet:** Imola (`it-1953`) — the game supports it (it's in the `Track` enum) but it's
off the 2026 calendar, so it wasn't in the 24. Fetch it the same way before shipping, or the
Emilia Romagna round shows no map. Reverse layouts reuse the base track's geometry drawn backwards.

## Projection method (to port to WPF)
Per track: find the bbox, multiply longitude by `cos(midLatitude)` for aspect, scale to fit the box
with padding, centre, flip Y for screen. Draw the closed point loop as a smooth path (Catmull-Rom →
cubic béziers). In WPF: `PathGeometry` + `GetPointAtFractionLength` for the cars, animated on
`CompositionTarget.Rendering`. **The render loop MUST be stopped whenever the waiting placeholder
isn't visible** — that's the one hard performance requirement (proven in the mockup).
