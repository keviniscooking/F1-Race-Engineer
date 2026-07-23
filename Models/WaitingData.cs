using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// One circuit outline for the waiting screen: the smoothed point loop (already projected to a
    /// 0-1000 box in the bundled data) plus its name and length. See docs/waiting-screen for how
    /// the data was produced (real OSM-derived survey coordinates, projected).
    /// </summary>
    public class TrackOutline
    {
        public string Id { get; init; } = "";
        public string Gp { get; init; } = "";
        public string Circuit { get; init; } = "";
        public int Len { get; init; }
        public int FirstGp { get; init; }
        public IReadOnlyList<Point> Points { get; init; } = Array.Empty<Point>();
    }

    public readonly record struct NamedCorner(string Name, double Fraction);

    /// <summary>
    /// Loads and holds the waiting-screen's bundled reference data - circuit outlines, named
    /// corners, and the trivia bank - from embedded JSON resources. Static and load-once: this is
    /// read-only content that never changes at runtime. All parsing is defensive; a missing or
    /// malformed resource leaves the relevant collection empty rather than throwing, so the waiting
    /// screen degrades to "no track / no trivia" instead of crashing the app on launch.
    /// </summary>
    public static class WaitingData
    {
        public static IReadOnlyDictionary<string, TrackOutline> Tracks { get; }
        public static IReadOnlyDictionary<string, IReadOnlyList<NamedCorner>> Corners { get; }
        public static IReadOnlyList<(string Question, string Answer)> Trivia { get; }

        // App circuit name (TrackNames.CircuitFor) -> bundled geojson file id. Kept here, next to
        // the data, so the one place that resolves "which outline for this saved race" is obvious.
        // Imola (it-1953) is intentionally absent: it's off the 2026 calendar and wasn't bundled,
        // so an Emilia Romagna race falls back to a random track.
        public static readonly IReadOnlyDictionary<string, string> CircuitToId = new Dictionary<string, string>
        {
            ["Melbourne"] = "au-1953", ["Shanghai"] = "cn-2004", ["Sakhir"] = "bh-2002",
            ["Barcelona"] = "es-1991", ["Monte Carlo"] = "mc-1929", ["Montreal"] = "ca-1978",
            ["Silverstone"] = "gb-1948", ["Budapest"] = "hu-1986", ["Spa-Francorchamps"] = "be-1925",
            ["Monza"] = "it-1922", ["Singapore"] = "sg-2008", ["Suzuka"] = "jp-1962",
            ["Yas Marina"] = "ae-2009", ["Austin"] = "us-2012", ["Interlagos"] = "br-1940",
            ["Red Bull Ring"] = "at-1969", ["Mexico City"] = "mx-1962", ["Baku"] = "az-2016",
            ["Zandvoort"] = "nl-1948", ["Jeddah"] = "sa-2021", ["Miami"] = "us-2022",
            ["Las Vegas"] = "us-2023", ["Lusail"] = "qa-2004", ["Madrid"] = "es-2026",
        };

        static WaitingData()
        {
            Tracks = LoadTracks();
            Corners = LoadCorners();
            Trivia = LoadTrivia();
        }

        private static string? ReadResource(string logicalName)
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var s = asm.GetManifestResourceStream(logicalName);
                if (s == null) return null;
                using var r = new StreamReader(s);
                return r.ReadToEnd();
            }
            catch { return null; }
        }

        private static Dictionary<string, TrackOutline> LoadTracks()
        {
            var result = new Dictionary<string, TrackOutline>();
            var json = ReadResource("tracks.json");
            if (json == null) return result;
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var o = prop.Value;
                    var pts = new List<Point>();
                    foreach (var pair in o.GetProperty("pts").EnumerateArray())
                        pts.Add(new Point(pair[0].GetDouble(), pair[1].GetDouble()));
                    result[prop.Name] = new TrackOutline
                    {
                        Id = prop.Name,
                        Gp = o.GetProperty("gp").GetString() ?? "",
                        Circuit = o.GetProperty("circuit").GetString() ?? "",
                        Len = o.TryGetProperty("len", out var l) ? l.GetInt32() : 0,
                        FirstGp = o.TryGetProperty("firstgp", out var f) ? f.GetInt32() : 0,
                        Points = pts
                    };
                }
            }
            catch { /* leave whatever parsed */ }
            return result;
        }

        private static Dictionary<string, IReadOnlyList<NamedCorner>> LoadCorners()
        {
            var result = new Dictionary<string, IReadOnlyList<NamedCorner>>();
            var json = ReadResource("corners.json");
            if (json == null) return result;
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name.StartsWith("_")) continue; // the "_comment" note in the file
                    if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                    var list = new List<NamedCorner>();
                    foreach (var c in prop.Value.EnumerateArray())
                        list.Add(new NamedCorner(c[0].GetString() ?? "", c[1].GetDouble()));
                    result[prop.Name] = list;
                }
            }
            catch { }
            return result;
        }

        private static List<(string, string)> LoadTrivia()
        {
            var result = new List<(string, string)>();
            var json = ReadResource("trivia.json");
            if (json == null) return result;
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var qa in doc.RootElement.EnumerateArray())
                    if (qa.GetArrayLength() >= 2)
                        result.Add((qa[0].GetString() ?? "", qa[1].GetString() ?? ""));
            }
            catch { }
            return result;
        }
    }
}
