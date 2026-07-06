using System.Windows.Media;
using F1Game.UDP.Enums;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// FIA tyre-compound colour convention (soft=red, medium=yellow, hard=white,
    /// intermediate=green, wet=blue). Rendered as a coloured badge + letter, not a
    /// captured image asset - same "drawn not captured" approach as the rest of the app.
    /// </summary>
    public static class CompoundPalette
    {
        public static readonly SolidColorBrush Soft = Freeze(0xE1, 0x2E, 0x2E);
        public static readonly SolidColorBrush Medium = Freeze(0xE8, 0xC5, 0x2E);
        public static readonly SolidColorBrush Hard = Freeze(0xE6, 0xED, 0xF3);
        public static readonly SolidColorBrush Intermediate = Freeze(0x3F, 0xA6, 0x4A);
        public static readonly SolidColorBrush Wet = Freeze(0x2E, 0x6F, 0xE1);
        public static readonly SolidColorBrush Unknown = Freeze(0x6B, 0x76, 0x84);

        // Real FIA badges use white lettering on the saturated compounds (soft/inter/wet)
        // and dark lettering only on the light ones (medium/hard) - a single dark-text
        // color for every compound (as this app first shipped with) reads fine on
        // yellow/white but is low-contrast and wrong on red/green/blue.
        public static readonly SolidColorBrush DarkForeground = Freeze(0x0D, 0x11, 0x17);
        public static readonly SolidColorBrush LightForeground = Freeze(0xF5, 0xF7, 0xFA);

        private static SolidColorBrush Freeze(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public static SolidColorBrush BrushFor(VisualCompound compound) => compound switch
        {
            VisualCompound.F1Soft or VisualCompound.F2SuperSoft => Soft,
            VisualCompound.F1Medium or VisualCompound.F2Medium => Medium,
            VisualCompound.F1Hard or VisualCompound.F2Hard => Hard,
            VisualCompound.F1Inter => Intermediate,
            VisualCompound.F1Wet or VisualCompound.F2Wet => Wet,
            VisualCompound.F1ClassicDry => Hard,
            VisualCompound.F1ClassicWet => Wet,
            VisualCompound.F2Soft => Soft,
            _ => Unknown
        };

        public static string LetterFor(VisualCompound compound) => compound switch
        {
            VisualCompound.F1Soft or VisualCompound.F2SuperSoft or VisualCompound.F2Soft => "S",
            VisualCompound.F1Medium or VisualCompound.F2Medium => "M",
            VisualCompound.F1Hard or VisualCompound.F2Hard or VisualCompound.F1ClassicDry => "H",
            VisualCompound.F1Inter => "I",
            VisualCompound.F1Wet or VisualCompound.F2Wet or VisualCompound.F1ClassicWet => "W",
            _ => "?"
        };

        public static SolidColorBrush ForegroundFor(VisualCompound compound) => compound switch
        {
            VisualCompound.F1Medium or VisualCompound.F2Medium => DarkForeground,
            VisualCompound.F1Hard or VisualCompound.F2Hard or VisualCompound.F1ClassicDry => DarkForeground,
            _ => LightForeground // Soft/Inter/Wet (saturated compounds) and unknown all read best in light text
        };
    }
}
