using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// Country flags for the history cards, drawn as vector <see cref="DrawingBrush"/>es in a 60x40
    /// (3:2) space and returned frozen, so a small Rectangle renders one crisply at any size. Vector,
    /// not bundled images - matching this project's "drawn, not captured" convention (the tyre
    /// marker, app icon and flag-pole glyph are all vector). Flag *designs* are public-domain, and at
    /// the ~20px the cards use, the simplified detail (stars, emblems, script) reads correctly by
    /// layout + colour. Keyed by the FIA/IOC 3-letter code stored on the saved race; an unknown code
    /// returns null and the card falls back to the text badge.
    /// </summary>
    public static class FlagPalette
    {
        private static readonly Dictionary<string, DrawingBrush> _cache = new();

        public static DrawingBrush? BrushFor(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            if (_cache.TryGetValue(code, out var cached)) return cached;
            var g = new DrawingGroup();
            try { if (!Compose(code, g)) return null; }
            catch { return null; }
            var b = new DrawingBrush(g) { Stretch = Stretch.Fill };
            b.Freeze();
            _cache[code] = b;
            return b;
        }

        // ---- flag definitions (60 wide x 40 tall) ----
        private static bool Compose(string code, DrawingGroup g)
        {
            switch (code)
            {
                case "NED": HBands(g, "#AE1C28", "#FFFFFF", "#21468B"); return true;
                case "HUN": HBands(g, "#CD2A3E", "#FFFFFF", "#436F4D"); return true;
                case "AUT": HBands(g, "#C8102E", "#FFFFFF", "#C8102E"); return true;
                case "ITA": VBands(g, "#009246", "#FFFFFF", "#CE2B37"); return true;
                case "BEL": VBands(g, "#000000", "#FDDA24", "#EF3340"); return true;
                case "MON": HBands(g, "#CE1126", "#FFFFFF"); return true;

                case "JPN":
                    Fill(g, R(0, 0, 60, 40), "#FFFFFF");
                    Add(g, new EllipseGeometry(new Point(30, 20), 10, 10), "#BC002D");
                    return true;

                case "ESP":
                    Fill(g, R(0, 0, 60, 40), "#AA151B");
                    Fill(g, R(0, 10, 60, 20), "#F1BF00");
                    return true;

                case "MEX":
                    VBands(g, "#006847", "#FFFFFF", "#CE1126");
                    Add(g, new EllipseGeometry(new Point(30, 20), 3, 3.6), "#7A4B27"); // eagle emblem hint
                    return true;

                case "UAE":
                    Fill(g, R(15, 0, 45, 13.33), "#00732F");
                    Fill(g, R(15, 13.33, 45, 13.34), "#FFFFFF");
                    Fill(g, R(15, 26.67, 45, 13.33), "#000000");
                    Fill(g, R(0, 0, 15, 40), "#FF0000");
                    return true;

                case "CHN":
                    Fill(g, R(0, 0, 60, 40), "#DE2910");
                    Add(g, Star(13, 11, 6), "#FFDE00");
                    Add(g, Star(24, 5, 2), "#FFDE00");
                    Add(g, Star(27.5, 9, 2), "#FFDE00");
                    Add(g, Star(27.5, 14, 2), "#FFDE00");
                    Add(g, Star(24, 18, 2), "#FFDE00");
                    return true;

                case "BRA":
                    Fill(g, R(0, 0, 60, 40), "#009C3B");
                    Add(g, Poly((30, 4), (56, 20), (30, 36), (4, 20)), "#FFDF00");
                    Add(g, new EllipseGeometry(new Point(30, 20), 9, 9), "#002776");
                    return true;

                case "SGP":
                    Fill(g, R(0, 0, 60, 20), "#EF3340");
                    Fill(g, R(0, 20, 60, 20), "#FFFFFF");
                    Add(g, new EllipseGeometry(new Point(13, 10), 6, 6), "#FFFFFF");
                    Add(g, new EllipseGeometry(new Point(16, 10), 5, 5), "#EF3340"); // carve crescent
                    for (int i = 0; i < 5; i++)
                    {
                        double a = -90 + i * 72;
                        Add(g, Star(22 + 4.2 * Math.Cos(a * Math.PI / 180), 10 + 4.2 * Math.Sin(a * Math.PI / 180), 1.4), "#FFFFFF");
                    }
                    return true;

                case "AZE":
                    HBands(g, "#0092BC", "#E4002B", "#00AE65");
                    Add(g, new EllipseGeometry(new Point(29, 20), 4, 4), "#FFFFFF");
                    Add(g, new EllipseGeometry(new Point(31, 20), 3.2, 3.2), "#E4002B"); // crescent
                    Add(g, Star(37, 20, 2), "#FFFFFF");
                    return true;

                case "BHR": Serrated(g, "#FFFFFF", "#CE1126", 5); return true;
                case "QAT": Serrated(g, "#FFFFFF", "#8A1538", 9); return true;

                case "KSA":
                    Fill(g, R(0, 0, 60, 40), "#006C35");
                    Fill(g, R(12, 22, 36, 2.2), "#FFFFFF");                    // sword
                    Fill(g, R(16, 13, 28, 3.5), "#FFFFFF");                    // shahada (suggestion)
                    return true;

                case "CAN":
                    Fill(g, R(0, 0, 15, 40), "#D52B1E");
                    Fill(g, R(15, 0, 30, 40), "#FFFFFF");
                    Fill(g, R(45, 0, 15, 40), "#D52B1E");
                    Add(g, MapleLeaf(), "#D52B1E");
                    return true;

                case "GBR": UnionJack(g, 0, 0, 60, 40); return true;

                case "USA":
                    for (int i = 0; i < 13; i++) Fill(g, R(0, i * 40.0 / 13, 60, 40.0 / 13), i % 2 == 0 ? "#B22234" : "#FFFFFF");
                    Fill(g, R(0, 0, 24, 40.0 * 7 / 13), "#3C3B6E");
                    for (int r = 0; r < 4; r++)
                        for (int c = 0; c < 5; c++)
                            Add(g, new EllipseGeometry(new Point(3 + c * 4.5, 3 + r * 4.8), 0.9, 0.9), "#FFFFFF");
                    return true;

                case "AUS":
                    Fill(g, R(0, 0, 60, 40), "#00247D");
                    UnionJack(g, 0, 0, 30, 20);
                    Add(g, Star(15, 30, 3.4), "#FFFFFF");                       // Commonwealth star
                    Add(g, Star(44, 9, 1.7), "#FFFFFF");
                    Add(g, Star(52, 16, 1.7), "#FFFFFF");
                    Add(g, Star(45, 24, 1.7), "#FFFFFF");
                    Add(g, Star(38, 18, 1.7), "#FFFFFF");
                    Add(g, Star(48, 30, 1.2), "#FFFFFF");
                    return true;

                default: return false;
            }
        }

        // ---- helpers ----
        private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        private static RectangleGeometry R(double x, double y, double w, double h) => new(new Rect(x, y, w, h));
        private static void Add(DrawingGroup g, Geometry geo, string hex) => g.Children.Add(new GeometryDrawing(new SolidColorBrush(C(hex)), null, geo));
        private static void Fill(DrawingGroup g, Geometry geo, string hex) => Add(g, geo, hex);

        private static void HBands(DrawingGroup g, params string[] hex)
        {
            double h = 40.0 / hex.Length;
            for (int i = 0; i < hex.Length; i++) Fill(g, R(0, i * h, 60, h), hex[i]);
        }

        private static void VBands(DrawingGroup g, params string[] hex)
        {
            double w = 60.0 / hex.Length;
            for (int i = 0; i < hex.Length; i++) Fill(g, R(i * w, 0, w, 40), hex[i]);
        }

        private static Geometry Poly(params (double X, double Y)[] pts)
        {
            var fig = new PathFigure { StartPoint = new Point(pts[0].X, pts[0].Y), IsClosed = true };
            for (int i = 1; i < pts.Length; i++) fig.Segments.Add(new LineSegment(new Point(pts[i].X, pts[i].Y), true));
            var pg = new PathGeometry();
            pg.Figures.Add(fig);
            return pg;
        }

        private static Geometry Star(double cx, double cy, double r)
        {
            var pts = new (double, double)[10];
            for (int i = 0; i < 10; i++)
            {
                double ang = (-90 + i * 36) * Math.PI / 180;
                double rad = i % 2 == 0 ? r : r * 0.382;
                pts[i] = (cx + rad * Math.Cos(ang), cy + rad * Math.Sin(ang));
            }
            return Poly(pts);
        }

        // A right-hand coloured field with a serrated (triangular) hoist edge - Bahrain (5 points,
        // horizontal) and Qatar (9 points, drawn maroon). White ground fills the rest.
        private static void Serrated(DrawingGroup g, string groundHex, string fieldHex, int points)
        {
            Fill(g, R(0, 0, 60, 40), groundHex);
            double baseX = points >= 9 ? 20 : 22, tipX = points >= 9 ? 14 : 15;
            var pts = new List<(double, double)> { (60, 0), (baseX, 0) };
            double step = 40.0 / points;
            for (int i = 0; i < points; i++)
            {
                pts.Add((tipX, i * step + step / 2));
                pts.Add((baseX, (i + 1) * step));
            }
            pts.Add((60, 40));
            Add(g, Poly(pts.ToArray()), fieldHex);
        }

        private static void UnionJack(DrawingGroup g, double x, double y, double w, double h)
        {
            Fill(g, R(x, y, w, h), "#012169");
            var white = new Pen(new SolidColorBrush(C("#FFFFFF")), h * 0.22);
            var red = new Pen(new SolidColorBrush(C("#C8102E")), h * 0.08);
            // diagonals
            var d1 = new LineGeometry(new Point(x, y), new Point(x + w, y + h));
            var d2 = new LineGeometry(new Point(x + w, y), new Point(x, y + h));
            g.Children.Add(new GeometryDrawing(null, white, d1));
            g.Children.Add(new GeometryDrawing(null, white, d2));
            g.Children.Add(new GeometryDrawing(null, red, d1));
            g.Children.Add(new GeometryDrawing(null, red, d2));
            // upright cross
            Fill(g, R(x + w * 0.40, y, w * 0.20, h), "#FFFFFF");
            Fill(g, R(x, y + h * 0.35, w, h * 0.30), "#FFFFFF");
            Fill(g, R(x + w * 0.44, y, w * 0.12, h), "#C8102E");
            Fill(g, R(x, y + h * 0.41, w, h * 0.18), "#C8102E");
        }

        // A compact, symmetric maple-leaf silhouette centred in the 60x40 space (Canada).
        private static Geometry MapleLeaf() => Poly(
            (30, 7), (32.4, 14), (37, 12.4), (35.4, 17.4), (41, 17), (37.6, 21.4), (43, 24),
            (34, 23.6), (34.6, 30), (30.8, 27.6), (30, 33), (29.2, 27.6), (25.4, 30), (26, 23.6),
            (17, 24), (22.4, 21.4), (19, 17), (24.6, 17.4), (23, 12.4), (27.6, 14));
    }
}
