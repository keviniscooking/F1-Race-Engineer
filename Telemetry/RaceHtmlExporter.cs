using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Windows.Media;
using F1RaceEngineer.Models;

namespace F1RaceEngineer.Telemetry
{
    /// <summary>
    /// Renders a saved race to a single self-contained HTML file (all CSS inline, no external
    /// assets) styled in the app's dark theme, so it opens in any browser and can be shared as
    /// one file.
    ///
    /// It renders the SAME <see cref="SavedRaceView"/> the in-app history detail binds to - not
    /// the raw SavedRace - so every computed value (gap-to-winner, DNF flags, positions gained,
    /// the penalties list, clamped stint segments) and every colour comes from one place and the
    /// two views can't drift apart. Brushes are converted straight to hex; the layout below
    /// mirrors the panel's: classification + penalties on the left, lap-by-lap + tyre strategy
    /// on the right.
    /// </summary>
    public static class RaceHtmlExporter
    {
        private static string Enc(string s) => WebUtility.HtmlEncode(s ?? "");
        private static string Hex(SolidColorBrush b) => $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
        private static string Pct(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>
        /// Exports a whole WEEKEND: the race, the sprint if there was one, and each session's
        /// head-to-head. Previously this took a single session, so a sprint weekend silently
        /// exported only the feature race and the head-to-head never left the app at all - the
        /// file didn't contain what the user had been looking at.
        /// </summary>
        public static string Export(WeekendCardView w)
        {
            var head = w.Race;
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append($"<title>{Enc(head.Source.GrandPrix)} — Race History</title>");
            sb.Append("<style>").Append(Css()).Append("</style></head><body><div class=\"wrap\">");

            foreach (var v in w.Sessions)
                sb.Append(SessionBlock(v, w.Sessions.Count > 1));

            sb.Append("<div class=\"foot\">Exported from F1 Race Engineer</div>");
            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        /// <summary>One session: its header, result, classification, laps, strategy and H2H.</summary>
        private static string SessionBlock(SavedRaceView v, bool labelSession)
        {
            var r = v.Source;
            var sb = new StringBuilder();

            // ---- header: name + subtitle, then Finish / Grid / Gained / Points ----
            sb.Append("<div class=\"head\"><div><div class=\"gp\">");
            if (v.HasCountry) sb.Append("<span class=\"cc\">").Append(Enc(v.Country)).Append("</span>");
            sb.Append(Enc(v.GrandPrix)).Append("</div>");
            sb.Append("<div class=\"sub\">").Append(Enc(v.DetailSubtitle)).Append(" · ")
              .Append(Enc(r.SavedAtUtc.ToLocalTime().ToString("d MMM yyyy"))).Append("</div></div>");

            sb.Append("<div class=\"result\">");
            sb.Append(Stat("Finish", Enc(v.FinishText), Hex(v.FinishBrush)));
            sb.Append(Stat("Grid", Enc(v.GridText), "#E6EDF3"));
            sb.Append(Stat("Gained", Enc(v.DeltaShort), Hex(v.DeltaShortBrush)));
            sb.Append(Stat("Points", Enc(v.PointsText), "#E6EDF3"));
            sb.Append("</div></div>");

            if (v.HasDnfDetail)
                sb.Append("<div class=\"dnf\"><b>Did not finish</b> — ").Append(Enc(v.DnfDetail)).Append("</div>");

            // ---- body: [classification + penalties] | [lap-by-lap + tyre strategy] ----
            sb.Append("<div class=\"grid2\"><div class=\"col\">");
            sb.Append(Classification(v));
            sb.Append(Penalties(v));
            sb.Append("</div><div class=\"col\">");
            sb.Append(Laps(v));
            sb.Append(Strategy(v));
            sb.Append("</div></div>");

            sb.Append(HeadToHead(v));
            return sb.ToString();
        }

        /// <summary>
        /// The head-to-head, or nothing when this session isn't a two-player career. Mirrors the
        /// in-app page: verdict, tale of the tape, gap evolution and pit stops - so the exported
        /// file shows what was on screen rather than a subset of it.
        /// </summary>
        private static string HeadToHead(SavedRaceView v)
        {
            var h = v.BuildHeadToHead();
            if (h == null) return "";

            var sb = new StringBuilder();
            sb.Append("<div class=\"card h2h\"><div class=\"h\">Head to head</div>");

            // verdict
            sb.Append("<div class=\"verdict\">");
            sb.Append("<div class=\"side\"><div class=\"nm\">").Append(Enc(h.You.Name)).Append("</div>")
              .Append("<div class=\"pos\">").Append(Enc(h.You.PositionText)).Append(" <span class=\"pts\">")
              .Append(Enc(h.You.PointsText)).Append("</span></div></div>");
            sb.Append("<div class=\"mid\"><div class=\"vd\" style=\"color:").Append(Hex(h.VerdictBrush)).Append("\">")
              .Append(Enc(h.VerdictText)).Append("</div><div class=\"mg\">").Append(Enc(h.MarginText)).Append("</div></div>");
            sb.Append("<div class=\"side r\"><div class=\"nm\">").Append(Enc(h.Rival.Name)).Append("</div>")
              .Append("<div class=\"pos\"><span class=\"pts\">").Append(Enc(h.Rival.PointsText)).Append("</span> ")
              .Append(Enc(h.Rival.PositionText)).Append("</div></div>");
            sb.Append("</div>");

            // tale of the tape - values on their own sides, metric centred, matching the app
            sb.Append("<table class=\"tape\"><tbody>");
            foreach (var row in h.Rows)
            {
                sb.Append("<tr><td class=\"you\" style=\"color:").Append(Hex(row.YouBrush)).Append("\">")
                  .Append(Enc(row.YouText)).Append("</td>");
                sb.Append("<td class=\"lbl\">").Append(Enc(row.Label));
                if (row.DeltaText.Length > 0) sb.Append("<span class=\"dl\">").Append(Enc(row.DeltaText)).Append("</span>");
                sb.Append("</td>");
                sb.Append("<td class=\"riv\" style=\"color:").Append(Hex(row.RivalBrush)).Append("\">")
                  .Append(Enc(row.RivalText)).Append("</td></tr>");
            }
            sb.Append("</tbody></table>");

            sb.Append(GapChartSvg(h));

            // pit stops, one column per driver
            sb.Append("<div class=\"grid2\">");
            foreach (var side in new[] { h.You, h.Rival })
            {
                sb.Append("<div class=\"col\"><div class=\"stops\"><div class=\"sh\">").Append(Enc(side.Name))
                  .Append("<span class=\"hint\">lap · box · lane</span></div>");
                if (side.HasStops)
                {
                    sb.Append("<table class=\"stoptbl\"><tbody>");
                    foreach (var s in side.Stops)
                        sb.Append("<tr><td class=\"lp\">").Append(Enc(s.LapText)).Append("</td><td class=\"bx\">")
                          .Append(Enc(s.BoxText)).Append("</td><td class=\"ln\">").Append(Enc(s.LaneText)).Append("</td></tr>");
                    sb.Append("</tbody></table>");
                }
                else sb.Append("<div class=\"none\">No stops</div>");
                sb.Append("<div class=\"tot\"><span>Total box</span><b>").Append(Enc(side.StopTotalText)).Append("</b></div>");
                sb.Append("</div></div>");
            }
            sb.Append("</div></div>");
            return sb.ToString();
        }

        /// <summary>
        /// The gap trace as inline SVG - vector, self-contained, no script or external file, so it
        /// survives being emailed around. Same conventions as the in-app chart: above the zero line
        /// is the player ahead, the fill switches green/red across it via a gradient with a hard
        /// stop, and pit stops are dashed verticals.
        /// </summary>
        private static string GapChartSvg(HeadToHeadView h)
        {
            if (!h.HasGapSeries) return "";
            const double W = 900, H = 190, Pad = 42, Mid = H / 2;
            var pts = h.GapSeries;
            double plotW = W - Pad - 14;
            double scale = (H / 2 - 10) / h.GapMaxAbsSeconds;
            double X(int i) => pts.Count == 1 ? Pad + plotW / 2 : Pad + plotW * i / (pts.Count - 1.0);
            double Y(double g) => Mid - g * scale;

            var sb = new StringBuilder();
            sb.Append("<div class=\"card\"><div class=\"h\">Gap evolution <span class=\"hint\">above = ")
              .Append(Enc(h.You.Name)).Append(" ahead</span></div>");
            sb.Append($"<svg class=\"gap\" viewBox=\"0 0 {Pct(W)} {Pct(H)}\" preserveAspectRatio=\"none\" role=\"img\">");
            sb.Append("<defs><linearGradient id=\"gf\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\">")
              .Append("<stop offset=\"0\" stop-color=\"#97C459\" stop-opacity=\".24\"/>")
              .Append("<stop offset=\"50%\" stop-color=\"#97C459\" stop-opacity=\".24\"/>")
              .Append("<stop offset=\"50%\" stop-color=\"#E12E2E\" stop-opacity=\".24\"/>")
              .Append("<stop offset=\"1\" stop-color=\"#E12E2E\" stop-opacity=\".24\"/></linearGradient></defs>");

            // gridlines on the same nice step the app uses
            double major = NiceStep(h.GapMaxAbsSeconds, 3);
            for (double val = major; val <= h.GapMaxAbsSeconds + 0.001; val += major)
                foreach (double s in new[] { val, -val })
                {
                    double y = Y(s);
                    if (y < 2 || y > H - 2) continue;
                    sb.Append($"<line x1=\"{Pct(Pad)}\" x2=\"{Pct(W - 14)}\" y1=\"{Pct(y)}\" y2=\"{Pct(y)}\" stroke=\"#232B35\"/>");
                    sb.Append($"<text x=\"2\" y=\"{Pct(y + 3)}\" class=\"ax\">{(s > 0 ? "+" : "")}{Pct(s)}</text>");
                }
            sb.Append($"<line x1=\"{Pct(Pad)}\" x2=\"{Pct(W - 14)}\" y1=\"{Pct(Mid)}\" y2=\"{Pct(Mid)}\" stroke=\"#4A5460\"/>");

            foreach (var (laps, colour) in new[] { (h.You.PitLaps, "#1F6FEB"), (h.Rival.PitLaps, "#37BEDD") })
                foreach (int lap in laps)
                {
                    int i = lap - pts[0].Lap;
                    if (i < 0 || i >= pts.Count) continue;
                    sb.Append($"<line x1=\"{Pct(X(i))}\" x2=\"{Pct(X(i))}\" y1=\"0\" y2=\"{Pct(H)}\" stroke=\"{colour}\" stroke-dasharray=\"3 3\" opacity=\".6\"/>");
                }

            var poly = new StringBuilder();
            for (int i = 0; i < pts.Count; i++) poly.Append(Pct(X(i))).Append(',').Append(Pct(Y(pts[i].GapSeconds))).Append(' ');
            sb.Append($"<polygon points=\"{Pct(X(0))},{Pct(Mid)} {poly}{Pct(X(pts.Count - 1))},{Pct(Mid)}\" fill=\"url(#gf)\"/>");
            sb.Append($"<polyline points=\"{poly}\" fill=\"none\" stroke=\"#79C0FF\" stroke-width=\"2\"/>");

            sb.Append($"<text x=\"{Pct(Pad)}\" y=\"{Pct(H - 2)}\" class=\"ax\">L{pts[0].Lap}</text>");
            sb.Append($"<text x=\"{Pct(W - 40)}\" y=\"{Pct(H - 2)}\" class=\"ax\">L{pts[^1].Lap}</text>");
            sb.Append("</svg></div>");
            return sb.ToString();
        }

        // Mirrors the in-app axis stepping so the exported chart is gridded identically.
        private static double NiceStep(double range, int target)
        {
            if (range <= 0 || target <= 0) return 1;
            double raw = range / target;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double norm = raw / mag;
            return (norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10) * mag;
        }

        private static string Stat(string label, string value, string hex) =>
            $"<div class=\"stat\"><div class=\"k\">{label}</div><div class=\"v\" style=\"color:{hex}\">{value}</div></div>";

        private static string Classification(SavedRaceView v)
        {
            var sb = new StringBuilder("<div class=\"card\"><h3>Final classification</h3><table>");
            sb.Append("<thead><tr><th class=\"c-pos\">#</th><th>Driver</th><th class=\"r c-gap\">Gap</th>");
            sb.Append("<th class=\"r c-best\">Best</th><th class=\"c-stint\">Stints</th></tr></thead><tbody>");

            foreach (var row in v.Classification)
            {
                sb.Append($"<tr class=\"{(row.IsPlayer ? "me" : "")}\">");
                sb.Append($"<td class=\"p\" style=\"color:{Hex(row.PositionBrush)}\">{Enc(row.PositionText)}</td>");

                sb.Append("<td><span class=\"sw\" style=\"background:").Append(Hex(row.LiveryBrush)).Append("\"></span>");
                sb.Append($"<span style=\"color:{Hex(row.NameBrush)}\">{Enc(row.DriverName)}</span>");
                if (row.HasFastestLap) sb.Append("<span class=\"fl\">◷</span>");
                if (row.HasTeam) sb.Append($"<span class=\"team\" style=\"color:{Hex(row.TeamBrush)}\">{Enc(row.TeamName)}</span>");
                sb.Append("</td>");

                sb.Append($"<td class=\"r num\" style=\"color:{Hex(row.GapBrush)}\">{Enc(row.GapText)}</td>");
                sb.Append($"<td class=\"r num\" style=\"color:{Hex(row.BestLapBrush)}\">{Enc(row.BestLapText)}</td>");

                sb.Append("<td><span class=\"chips\">");
                foreach (var c in row.StintChips)
                    sb.Append($"<span class=\"chip\" style=\"background:{Hex(c.Brush)};color:{Hex(c.TextBrush)}\">{Enc(c.Letter)}</span>");
                sb.Append("</span></td></tr>");
            }
            return sb.Append("</tbody></table></div>").ToString();
        }

        private static string Penalties(SavedRaceView v)
        {
            var sb = new StringBuilder("<div class=\"card short\"><h3>Penalties</h3>");
            if (v.NoPenalties)
                sb.Append("<div class=\"ok\"><span class=\"tick\">✓</span>No penalties</div>");
            else
            {
                sb.Append("<div class=\"pens\">");
                // Same red-vs-amber split as in-app: a real penalty reads red, a warning amber.
                foreach (var p in v.Penalties)
                    sb.Append($"<div class=\"pen{(p.IsPenalty ? " hard" : "")}\">").Append(Enc(p.Text)).Append("</div>");
                sb.Append("</div>");
            }
            return sb.Append("</div>").ToString();
        }

        private static string Laps(SavedRaceView v)
        {
            var sb = new StringBuilder("<div class=\"card\"><h3>Your lap-by-lap</h3><table class=\"laps\">");
            sb.Append("<thead><tr><th>Lap</th><th class=\"r\">S1</th><th class=\"r\">S2</th><th class=\"r\">S3</th>");
            sb.Append("<th>Events</th><th class=\"r\">Pit</th><th class=\"r\">Time</th><th class=\"r\">Delta</th></tr></thead><tbody>");

            foreach (var lap in v.Laps)
            {
                sb.Append("<tr>");
                sb.Append($"<td class=\"num\">{Enc(lap.LapText)}</td>");
                sb.Append($"<td class=\"r num\" style=\"color:{Hex(lap.S1Brush)}\">{Enc(lap.S1Text)}</td>");
                sb.Append($"<td class=\"r num\" style=\"color:{Hex(lap.S2Brush)}\">{Enc(lap.S2Text)}</td>");
                sb.Append($"<td class=\"r num\" style=\"color:{Hex(lap.S3Brush)}\">{Enc(lap.S3Text)}</td>");

                sb.Append("<td class=\"ev\">");
                foreach (var ev in lap.Events)
                    sb.Append($"<span class=\"evchip\" style=\"color:{Hex(ev.TextBrush)}\">{Glyph(ev)} {Enc(ev.Text)}</span>");
                sb.Append("</td>");

                sb.Append("<td class=\"r num pit\">");
                sb.Append(Enc(lap.PitText));
                if (lap.HasTag) sb.Append($" <span class=\"tag\">{Enc(lap.Tag)}</span>");
                sb.Append("</td>");

                sb.Append($"<td class=\"r num\" style=\"color:{Hex(lap.LapTimeBrush)}\">{Enc(lap.LapTimeText)}</td>");
                sb.Append($"<td class=\"r num\" style=\"color:#6B7684\">{Enc(lap.DeltaText)}</td></tr>");
            }
            return sb.Append("</tbody></table></div>").ToString();
        }

        private static string Glyph(LapEvent ev) => ev.Kind switch
        {
            LapEventKind.Chequered => "▦",
            LapEventKind.Penalty or LapEventKind.Warning => "!",
            _ => "⚑"
        };

        /// <summary>
        /// The tyre-strategy strip: compound segments over a full-race-length ghost track, with a
        /// lap axis below (labelled majors every 5, faint minors, accent pit markers) - the same
        /// scheme StintStripRenderer draws in-app, expressed as flex weights + %-positioned ticks.
        /// </summary>
        private static string Strategy(SavedRaceView v)
        {
            var segs = v.StintSegments;
            var sb = new StringBuilder("<div class=\"card short\"><h3>Your tyre strategy</h3>");
            if (segs.Count == 0) return sb.Append("<div class=\"mut\">No stint data.</div></div>").ToString();

            int drawn = 0;
            foreach (var s in segs) drawn += s.LapCount < 1 ? 1 : s.LapCount;
            int denom = System.Math.Max(System.Math.Max(v.TotalLaps, drawn), 1);

            sb.Append("<div class=\"bar\">");
            foreach (var s in segs)
                sb.Append($"<div class=\"seg\" style=\"flex:{(s.LapCount < 1 ? 1 : s.LapCount)};background:{Hex(s.Brush)};color:{Hex(s.TextBrush)}\">{Enc(s.Letter)}</div>");
            int remaining = denom - drawn;
            if (remaining > 0) sb.Append($"<div class=\"rem\" style=\"flex:{remaining}\"></div>");
            sb.Append("</div>");

            // Pit laps = cumulative stint ends, excluding the final segment's end (the flag).
            var pits = new List<int>();
            int acc = 0;
            for (int i = 0; i < segs.Count - 1; i++) { acc += segs[i].LapCount < 1 ? 1 : segs[i].LapCount; pits.Add(acc); }

            sb.Append("<div class=\"axis\">");
            for (int lap = 1; lap < denom; lap++)
            {
                if (lap % 5 == 0 || pits.Contains(lap)) continue;
                sb.Append($"<div class=\"tk min\" style=\"left:{Pct(lap * 100.0 / denom)}%\"><i></i></div>");
            }
            for (int lap = 5; lap < denom; lap += 5)
            {
                if (pits.Contains(lap)) continue;
                sb.Append($"<div class=\"tk\" style=\"left:{Pct(lap * 100.0 / denom)}%\"><i></i><span>{lap}</span></div>");
            }
            foreach (int lap in pits)
                sb.Append($"<div class=\"tk pit\" style=\"left:{Pct(lap * 100.0 / denom)}%\"><i></i><span>L{lap}</span></div>");
            sb.Append("</div>");

            return sb.Append("</div>").ToString();
        }

        private static string Css() => @"
:root{color-scheme:dark}
*{box-sizing:border-box}
body{margin:0;background:#0D1117;color:#E6EDF3;font-family:'Segoe UI',system-ui,sans-serif;-webkit-font-smoothing:antialiased}
.wrap{max-width:1600px;margin:0 auto;padding:32px 24px 60px}
.num,.v,.p{font-family:Consolas,ui-monospace,monospace}
.head{display:flex;align-items:flex-start;gap:16px;border-bottom:1px solid #232B35;padding-bottom:18px;margin-bottom:18px}
.gp{font-size:28px;font-weight:800}
.cc{font-family:Consolas,monospace;font-size:15px;font-weight:700;color:#9BA7B4;background:#1C2733;border-radius:5px;padding:3px 8px;margin-right:12px;vertical-align:middle}
.sub{font-size:13px;color:#6B7684;margin-top:6px}
.result{margin-left:auto;display:flex;gap:30px;text-align:right}
.stat .k{font-size:10px;letter-spacing:.05em;text-transform:uppercase;color:#4A5460}
.stat .v{font-size:24px;font-weight:800;margin-top:3px;white-space:nowrap}
.mut{color:#6B7684}
.dnf{background:#1A1113;border:1px solid #5A2323;border-radius:10px;padding:12px 14px;font-size:13px;color:#9BA7B4;margin-bottom:18px}
.dnf b{color:#FF8080}
.grid2{display:grid;grid-template-columns:1fr 1fr;gap:16px;align-items:stretch}
.col{display:flex;flex-direction:column;gap:16px}
.col .card:first-child{flex:1}
.card{background:#161B22;border:1px solid #232B35;border-radius:10px;padding:16px}
.card.short{min-height:156px}
.card h3{font-size:12px;letter-spacing:.06em;text-transform:uppercase;color:#6B7684;margin:0 0 14px}
@media(max-width:900px){.grid2{grid-template-columns:1fr}.result{gap:16px}}
table{width:100%;border-collapse:collapse;font-size:14px}
th{font-size:11px;letter-spacing:.04em;text-transform:uppercase;color:#4A5460;font-weight:500;text-align:left;padding:0 6px 9px}
th.r,td.r{text-align:right}
td{padding:9px 6px;border-top:1px solid #1C2733}
.c-pos{width:28px}.c-gap{width:96px}.c-best{width:86px}.c-stint{width:80px}
tr.me td{background:#1B2A42}
tr.me td:first-child{border-top-left-radius:6px;border-bottom-left-radius:6px}
tr.me td:last-child{border-top-right-radius:6px;border-bottom-right-radius:6px}
.p{font-weight:700}
.sw{display:inline-block;width:8px;height:8px;border-radius:2px;margin-right:8px;vertical-align:middle}
.fl{color:#AFA9EC;margin-left:5px}
.team{font-size:12px;margin-left:8px}
.chips{display:inline-flex;gap:3px}
.chip{display:inline-flex;width:16px;height:16px;border-radius:8px;align-items:center;justify-content:center;font-family:Consolas,monospace;font-size:9px;font-weight:800}
.laps .tag{background:#1C2733;color:#9BA7B4;border-radius:4px;padding:1px 5px;font-size:10.5px;font-weight:700;font-family:'Segoe UI',sans-serif}
.pit{color:#79C0FF;font-weight:700;white-space:nowrap}
.ev .evchip{font-size:11px;font-weight:700;white-space:nowrap;margin-right:8px}
.ok{font-size:14px;color:#9BA7B4}
.ok .tick{color:#97C459;font-weight:700;margin-right:9px}
.pens{display:grid;grid-template-columns:1fr 1fr;gap:6px}
.pen{background:#412402;color:#EF9F27;border-radius:6px;padding:9px 11px;font-size:13.5px}
.pen.hard{background:#4A1519;color:#FF8A8A}
.bar{display:flex;height:26px;gap:3px;background:#1C2733;border-radius:4px}
.bar .seg{display:flex;align-items:center;justify-content:center;border-radius:4px;font-family:Consolas,monospace;font-weight:800;font-size:12px}
.bar .rem{background:transparent}
.axis{position:relative;height:18px;margin-top:5px}
.axis .tk{position:absolute;top:0;transform:translateX(-50%);text-align:center}
.axis .tk i{display:block;width:1px;height:5px;background:#4A5460;margin:0 auto}
.axis .tk span{display:block;font-family:Consolas,monospace;font-size:9px;color:#6B7684;margin-top:2px}
.axis .tk.min i{height:3px;background:#2A313B}
.axis .tk.pit i{width:2px;height:6px;background:#79C0FF}
.axis .tk.pit span{color:#79C0FF;font-weight:700}
.foot{text-align:center;color:#4A5460;font-size:11px;margin-top:24px}

/* ---- head to head ---- */
.h2h{margin-top:16px}
.hint{float:right;color:#4A5460;font-size:10px;font-weight:400;text-transform:none}
.verdict{display:flex;align-items:center;justify-content:space-between;gap:16px;padding:4px 0 14px}
.verdict .side{min-width:0}
.verdict .side.r{text-align:right}
.verdict .nm{font-size:13px;font-weight:700;color:#E6EDF3}
.verdict .pos{font-family:Consolas,monospace;font-size:17px;font-weight:700;color:#E6EDF3;margin-top:3px}
.verdict .pts{font-size:11px;color:#6B7684;font-family:inherit;font-weight:400}
.verdict .mid{text-align:center;white-space:nowrap}
.verdict .vd{font-size:15px;font-weight:700}
.verdict .mg{font-size:11px;color:#6B7684;margin-top:4px}
/* Values on their own side, metric centred - the same structure as the in-app tape. */
table.tape{width:100%;border-collapse:collapse;max-width:620px;margin:0 auto}
table.tape td{padding:4px 0;font-family:Consolas,monospace;font-size:13px}
table.tape td.you{text-align:right;width:34%}
table.tape td.riv{text-align:left;width:34%}
table.tape td.lbl{text-align:center;width:32%;font-family:inherit;font-size:11px;color:#6B7684}
table.tape td.lbl .dl{display:block;font-family:Consolas,monospace;font-size:10px;color:#8B97A4}
svg.gap{width:100%;height:190px;display:block}
svg.gap .ax{fill:#6B7684;font-size:9px;font-family:Consolas,monospace}
.stops .sh{font-size:11px;font-weight:700;color:#8B97A4;margin-bottom:6px}
table.stoptbl{width:100%;border-collapse:collapse;font-family:Consolas,monospace}
table.stoptbl td{padding:3px 0;font-size:12px}
table.stoptbl td.lp{color:#6B7684;width:22%}
table.stoptbl td.bx{color:#79C0FF;text-align:right;width:39%}
table.stoptbl td.ln{color:#6B7684;text-align:right;width:39%}
.stops .none{color:#4A5460;font-size:11px}
.stops .tot{display:flex;justify-content:space-between;border-top:1px solid #232B35;margin-top:8px;padding-top:6px;font-size:10px;color:#4A5460}
.stops .tot b{font-family:Consolas,monospace;font-size:12px;color:#E6EDF3}
";
    }
}
