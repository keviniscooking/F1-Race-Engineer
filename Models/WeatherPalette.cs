using System.Windows.Media;
using F1Game.UDP.Enums;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// Drawn (not captured) weather badge: a coloured circle plus a short label.
    /// The widget picks which simple glyph shape to overlay based on WeatherGlyphKind.
    /// </summary>
    public enum WeatherGlyphKind
    {
        Sun,
        Cloud,
        Rain,
        Storm
    }

    public static class WeatherPalette
    {
        public static readonly SolidColorBrush ClearBg = Freeze(0x8A, 0x6D, 0x1F);
        public static readonly SolidColorBrush CloudBg = Freeze(0x4A, 0x54, 0x60);
        public static readonly SolidColorBrush RainBg = Freeze(0x1F, 0x4A, 0x6D);
        public static readonly SolidColorBrush StormBg = Freeze(0x4A, 0x1F, 0x6D);

        private static SolidColorBrush Freeze(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public static string LabelFor(Weather weather) => weather switch
        {
            Weather.Clear => "Clear",
            Weather.LightCloud => "Light cloud",
            Weather.Overcast => "Overcast",
            Weather.LightRain => "Light rain",
            Weather.HeavyRain => "Heavy rain",
            Weather.Storm => "Storm",
            _ => "Unknown"
        };

        public static WeatherGlyphKind GlyphFor(Weather weather) => weather switch
        {
            Weather.Clear => WeatherGlyphKind.Sun,
            Weather.LightCloud or Weather.Overcast => WeatherGlyphKind.Cloud,
            Weather.LightRain or Weather.HeavyRain => WeatherGlyphKind.Rain,
            Weather.Storm => WeatherGlyphKind.Storm,
            _ => WeatherGlyphKind.Cloud
        };

        // Drawn (stroked/filled vector) glyphs on a 24x24 canvas - deliberately simple line
        // icons in the same "geometric badge" style as the tyre compound letter, rather
        // than colour emoji (which render inconsistently and clash with the app's flat,
        // monochrome-plus-semantic-color look).
        private static readonly Geometry SunGeometry = Freeze(
            "M7,12 A5,5 0 1 1 17,12 A5,5 0 1 1 7,12 Z " +
            "M12,1 L12,3 M12,21 L12,23 M1,12 L3,12 M21,12 L23,12 " +
            "M4.22,4.22 L5.64,5.64 M18.36,18.36 L19.78,19.78 " +
            "M4.22,19.78 L5.64,18.36 M18.36,5.64 L19.78,4.22");

        private static readonly Geometry CloudGeometry = Freeze(
            "M18,10 L16.74,10 A8,8 0 1 0 9,20 L18,20 A5,5 0 0 0 18,10 Z");

        private static readonly Geometry RainGeometry = Freeze(
            "M17,8 L15.9,8 A6.5,6.5 0 1 0 10,17 L17,17 A4,4 0 0 0 17,8 Z " +
            "M9,19 L8,22 M13,19 L12,22 M17,19 L16,22");

        private static readonly Geometry StormGeometry = Freeze(
            "M17,6 L15.9,6 A6.5,6.5 0 1 0 10,15 L17,15 A4,4 0 0 0 17,6 Z " +
            "M13,15 L10,19 L12,19 L10,22 L15,17 L12,17 Z");

        private static Geometry Freeze(string data)
        {
            var geometry = Geometry.Parse(data);
            geometry.Freeze();
            return geometry;
        }

        public static Geometry GeometryFor(WeatherGlyphKind kind) => kind switch
        {
            WeatherGlyphKind.Sun => SunGeometry,
            WeatherGlyphKind.Cloud => CloudGeometry,
            WeatherGlyphKind.Rain => RainGeometry,
            WeatherGlyphKind.Storm => StormGeometry,
            _ => CloudGeometry
        };

        public static SolidColorBrush BackgroundFor(Weather weather) => weather switch
        {
            Weather.Clear => ClearBg,
            Weather.LightCloud or Weather.Overcast => CloudBg,
            Weather.LightRain or Weather.HeavyRain => RainBg,
            Weather.Storm => StormBg,
            _ => CloudBg
        };
    }
}
