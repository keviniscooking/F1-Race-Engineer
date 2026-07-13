using System.Collections.Generic;
using System.Net;
using System.Text;
using F1RaceEngineer.Models;

namespace F1RaceEngineer.Telemetry
{
    /// <summary>
    /// Renders a <see cref="SavedRace"/> to a single self-contained HTML file (all CSS inline,
    /// no external assets) styled in the app's dark theme, so it opens in any browser and can
    /// be shared as one file. Reads the same SavedRace the in-app history panel does, so the
    /// exported page and the panel stay consistent.
    /// </summary>
    public static class RaceHtmlExporter
    {
        private static readonly Dictionary<string, string> CompoundHex = new()
        {
            ["S"] = "#E12E2E", ["M"] = "#E8C52E", ["H"] = "#E6EDF3", ["I"] = "#3FA64A", ["W"] = "#2E6FE1"
        };

        private static string CHex(string letter) => CompoundHex.TryGetValue(letter, out var h) ? h : "#6B7684";
        private static string CInk(string letter) => letter is "M" or "H" ? "#0D1117" : "#F5F7FA";
        private static string Enc(string s) => WebUtility.HtmlEncode(s ?? "");

        public static string Export(SavedRace r)
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append($"<title>{Enc(r.GrandPrix)} — Race History</title>");
            sb.Append("<style>").Append(Css()).Append("</style></head><body><div class=\"wrap\">");

            // ---- header ----
            bool dnf = r.ResultStatus != "Finished";
            string finish = dnf ? Enc(r.ResultStatus) : $"P{r.FinishPosition}";
            int delta = r.FinishPosition - r.GridPosition;
            string deltaHtml = dnf ? "" :
                delta < 0 ? $"<span class=\"up\">▲ {-delta}</span>"
                : delta > 0 ? $"<span class=\"down\">▼ {delta}</span>" : "<span class=\"mut\">—</span>";
            string sub = string.IsNullOrEmpty(r.Circuit) ? $"{r.SessionLabel} · {r.TotalLaps} laps"
                : $"{Enc(r.Circuit)} · {r.SessionLabel} · {r.TotalLaps} laps";

            sb.Append("<div class=\"head\"><div><div class=\"gp\">");
            if (!string.IsNullOrEmpty(r.Country)) sb.Append("<span class=\"cc\">").Append(Enc(r.Country)).Append("</span>");
            sb.Append(Enc(r.GrandPrix)).Append("</div>");
            sb.Append("<div class=\"sub\">").Append(sub).Append(" · ").Append(r.SavedAtUtc.ToLocalTime().ToString("d MMM yyyy")).Append("</div></div>");
            sb.Append("<div class=\"result\">");
            sb.Append($"<div class=\"stat\"><div class=\"k\">Finish</div><div class=\"v\" style=\"color:{(dnf ? "#E12E2E" : "#E6EDF3")}\">{finish} {deltaHtml}</div></div>");
            sb.Append($"<div class=\"stat\"><div class=\"k\">Grid</div><div class=\"v\">P{r.GridPosition}</div></div>");
            sb.Append($"<div class=\"stat\"><div class=\"k\">Points</div><div class=\"v\">{(dnf ? "0" : "+" + r.Points)}</div></div>");
            sb.Append("</div></div>");

            if (dnf && r.RetiredOnLap.HasValue)
            {
                sb.Append("<div class=\"dnf\"><b>Did not finish</b> — retired on lap ").Append(r.RetiredOnLap).Append(" of ").Append(r.TotalLaps);
                if (!string.IsNullOrEmpty(r.ResultReason)) sb.Append(" — ").Append(Enc(r.ResultReason));
                sb.Append(".</div>");
            }

            // ---- tyre strategy ----
            sb.Append("<div class=\"card\"><h3>Your tyre strategy</h3>");
            sb.Append(StintBar(r));
            sb.Append("</div>");

            sb.Append("<div class=\"grid2\">");

            // ---- classification ----
            sb.Append("<div class=\"card\"><h3>Final classification</h3><table><thead><tr><th>#</th><th>Driver</th><th class=\"r\">Best lap</th><th class=\"r\">Pits</th><th>Stints</th></tr></thead><tbody>");
            foreach (var row in r.Classification)
            {
                string pos = row.IsOut ? "—" : row.Position.ToString();
                string chips = "";
                int prev = 0;
                foreach (var st in row.Stints)
                {
                    chips += $"<span class=\"chip\" style=\"background:{CHex(st.Compound)};color:{CInk(st.Compound)}\">{Enc(st.Compound)}</span>";
                    prev = st.EndLap;
                }
                string best = row.BestLapMs > 0 ? SavedRaceView.FormatTime(row.BestLapMs) : "—";
                string bestStyle = row.HasFastestLap ? "color:#AFA9EC" : "";
                string fl = row.HasFastestLap ? " <span style=\"color:#AFA9EC\">◷</span>" : "";
                sb.Append($"<tr class=\"{(row.IsPlayer ? "me" : "")}\"><td class=\"p\">{pos}</td>");
                sb.Append($"<td><span class=\"sw\" style=\"background:{Enc(row.LiveryHex)}\"></span>{Enc(row.DriverName)}<span class=\"team\">{Enc(row.TeamName)}</span>{fl}</td>");
                sb.Append($"<td class=\"r num\" style=\"{bestStyle}\">{best}</td><td class=\"r num\">{(row.IsOut ? "—" : row.PitStops.ToString())}</td><td><span class=\"chips\">{chips}</span></td></tr>");
            }
            sb.Append("</tbody></table></div>");

            // ---- lap-by-lap ----
            sb.Append("<div class=\"card\"><h3>Your lap-by-lap</h3><table class=\"laps\"><thead><tr><th>Lap</th><th class=\"r\">S1</th><th class=\"r\">S2</th><th class=\"r\">S3</th><th class=\"r\">Pit</th><th class=\"r\">Time</th></tr></thead><tbody>");
            foreach (var lap in r.PlayerLaps)
            {
                string tagHtml = string.IsNullOrEmpty(lap.Tag) ? "" : $"<span class=\"tag\">{Enc(lap.Tag)}</span> ";
                sb.Append("<tr>");
                sb.Append($"<td class=\"num\">{(lap.LapNumber > 0 ? "Lap " + lap.LapNumber : "")}</td>");
                sb.Append($"<td class=\"r num\" style=\"color:{Enc(lap.S1Hex)}\">{Enc(EmDash(lap.S1Text))}</td>");
                sb.Append($"<td class=\"r num\" style=\"color:{Enc(lap.S2Hex)}\">{Enc(EmDash(lap.S2Text))}</td>");
                sb.Append($"<td class=\"r num\" style=\"color:{Enc(lap.S3Hex)}\">{Enc(EmDash(lap.S3Text))}</td>");
                sb.Append($"<td class=\"r num pit\">{tagHtml}{Enc(lap.PitTimeText)}</td>");
                sb.Append($"<td class=\"r num\" style=\"color:{Enc(lap.LapColorHex)}\">{Enc(EmDash(lap.LapTimeText))}</td></tr>");
            }
            sb.Append("</tbody></table></div>");

            sb.Append("</div>"); // grid2
            sb.Append("<div class=\"foot\">Exported from F1 Race Engineer</div>");
            sb.Append("</div></body></html>");
            return sb.ToString();
        }

        private static string EmDash(string s) => string.IsNullOrEmpty(s) ? "—" : s;

        private static string StintBar(SavedRace r)
        {
            if (r.PlayerStints.Count == 0) return "<div class=\"mut\">No stint data.</div>";
            int total = r.TotalLaps > 0 ? r.TotalLaps : LastEnd(r.PlayerStints);
            var bar = new StringBuilder("<div class=\"stintbar\">");
            int prev = 0;
            foreach (var s in r.PlayerStints)
            {
                int laps = s.EndLap - prev; if (laps < 1) laps = 1; prev = s.EndLap;
                bar.Append($"<div class=\"seg\" style=\"flex:{laps};background:{CHex(s.Compound)};color:{CInk(s.Compound)}\">{Enc(s.Compound)}</div>");
            }
            bar.Append("</div>");

            // tick row: lap number at each boundary except the last stint's end
            var ticks = new StringBuilder("<div class=\"ticks\">");
            prev = 0;
            for (int i = 0; i < r.PlayerStints.Count; i++)
            {
                int laps = r.PlayerStints[i].EndLap - prev; if (laps < 1) laps = 1; prev = r.PlayerStints[i].EndLap;
                string label = i < r.PlayerStints.Count - 1 ? $"L{r.PlayerStints[i].EndLap}" : "";
                ticks.Append($"<div class=\"tk\" style=\"flex:{laps}\"><span>{label}</span></div>");
            }
            ticks.Append("</div>");
            _ = total;
            return bar.ToString() + ticks.ToString();
        }

        private static int LastEnd(List<SavedStint> stints) => stints.Count == 0 ? 0 : stints[^1].EndLap;

        private static string Css() => @"
:root{color-scheme:dark}
*{box-sizing:border-box}
body{margin:0;background:#0D1117;color:#E6EDF3;font-family:'Segoe UI',system-ui,sans-serif;-webkit-font-smoothing:antialiased}
.wrap{max-width:1000px;margin:0 auto;padding:32px 20px 60px}
.num,.v,.p{font-family:Consolas,ui-monospace,monospace}
.head{display:flex;align-items:flex-start;gap:16px;border-bottom:1px solid #232B35;padding-bottom:18px;margin-bottom:18px}
.gp{font-size:28px;font-weight:800}
.cc{font-family:Consolas,monospace;font-size:15px;font-weight:700;color:#9BA7B4;background:#1C2733;border-radius:5px;padding:3px 8px;margin-right:12px;vertical-align:middle}
.sub{font-size:13px;color:#6B7684;margin-top:6px}
.result{margin-left:auto;display:flex;gap:26px;text-align:right}
.stat .k{font-size:10px;letter-spacing:.05em;text-transform:uppercase;color:#4A5460}
.stat .v{font-size:24px;font-weight:800;margin-top:3px}
.up{color:#97C459}.down{color:#E12E2E}.mut{color:#6B7684}
.dnf{background:#1A1113;border:1px solid #5A2323;border-radius:10px;padding:12px 14px;font-size:13px;color:#9BA7B4;margin-bottom:18px}
.dnf b{color:#FF8080}
.card{background:#161B22;border:1px solid #232B35;border-radius:10px;padding:16px;margin-bottom:16px}
.card h3{font-size:11px;letter-spacing:.06em;text-transform:uppercase;color:#6B7684;margin:0 0 14px}
.grid2{display:grid;grid-template-columns:1fr 1fr;gap:16px}
@media(max-width:760px){.grid2{grid-template-columns:1fr}.result{gap:16px}}
.stintbar{display:flex;height:26px;border-radius:6px;overflow:hidden;gap:3px}
.stintbar .seg{display:flex;align-items:center;justify-content:center;border-radius:4px;font-family:Consolas,monospace;font-weight:800;font-size:12px}
.ticks{display:flex;margin-top:6px}
.ticks .tk{display:flex;justify-content:flex-end}
.ticks .tk span{font-family:Consolas,monospace;font-size:10px;color:#6B7684;border-right:1px solid #4A5460;padding-right:4px}
.ticks .tk:last-child span{border-right:none}
table{width:100%;border-collapse:collapse;font-size:13px}
th{font-size:10px;letter-spacing:.04em;text-transform:uppercase;color:#4A5460;font-weight:500;text-align:left;padding:0 6px 8px}
th.r,td.r{text-align:right}
td{padding:7px 6px;border-top:1px solid #1C2733}
tr.me td{background:#1B2A42}
tr.me td:first-child{border-top-left-radius:6px;border-bottom-left-radius:6px}
tr.me td:last-child{border-top-right-radius:6px;border-bottom-right-radius:6px}
.p{font-weight:700}
.sw{display:inline-block;width:8px;height:8px;border-radius:2px;margin-right:8px;vertical-align:middle}
.team{color:#6B7684;font-size:11px;margin-left:8px}
.chips{display:inline-flex;gap:3px}
.chip{display:inline-flex;width:16px;height:16px;border-radius:8px;align-items:center;justify-content:center;font-family:Consolas,monospace;font-size:9px;font-weight:800}
.laps .tag{background:#1C2733;color:#9BA7B4;border-radius:4px;padding:1px 5px;font-size:11px;font-weight:700;font-family:'Segoe UI',sans-serif}
.pit{color:#79C0FF;font-weight:700}
.foot{text-align:center;color:#4A5460;font-size:11px;margin-top:24px}
";
    }
}
