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

        public static string Export(SavedRaceView v)
        {
            var r = v.Source;
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append($"<title>{Enc(r.GrandPrix)} — Race History</title>");
            sb.Append("<style>").Append(Css()).Append("</style></head><body><div class=\"wrap\">");

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

            sb.Append("<div class=\"foot\">Exported from F1 Race Engineer</div>");
            sb.Append("</div></body></html>");
            return sb.ToString();
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
";
    }
}
