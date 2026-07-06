using System.Windows.Media;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// Matches the real F1 timing-screen convention, confirmed via research during design:
    /// Purple = fastest of the session (any driver), Green = personal best,
    /// Yellow = slower than this driver's own personal best, Neutral = not yet set.
    /// </summary>
    public enum TimingColor
    {
        Neutral,
        Green,
        Purple,
        Yellow
    }

    public static class TimingColorPalette
    {
        public static readonly SolidColorBrush NeutralText = Freeze(0xE6, 0xED, 0xF3);
        public static readonly SolidColorBrush GreenText = Freeze(0x97, 0xC4, 0x59);
        public static readonly SolidColorBrush PurpleText = Freeze(0xAF, 0xA9, 0xEC);
        public static readonly SolidColorBrush YellowText = Freeze(0xEF, 0x9F, 0x27);

        public static readonly SolidColorBrush NeutralBg = Freeze(0x1C, 0x27, 0x33);
        public static readonly SolidColorBrush GreenBg = Freeze(0x17, 0x34, 0x04);
        public static readonly SolidColorBrush PurpleBg = Freeze(0x26, 0x21, 0x5C);
        public static readonly SolidColorBrush YellowBg = Freeze(0x41, 0x24, 0x02);

        // Alert banner palette: red flag (red), safety car / VSC (amber - matches the
        // real yellow-flag convention confirmed during design), chequered flag (neutral).
        public static readonly SolidColorBrush AlertRedBg = Freeze(0x50, 0x13, 0x13);
        public static readonly SolidColorBrush AlertRedText = Freeze(0xF0, 0x99, 0x95);
        public static readonly SolidColorBrush AlertAmberBg = Freeze(0x41, 0x24, 0x02);
        public static readonly SolidColorBrush AlertAmberText = Freeze(0xFA, 0xC7, 0x75);
        public static readonly SolidColorBrush AlertNeutralBg = Freeze(0x1C, 0x27, 0x33);
        public static readonly SolidColorBrush AlertNeutralText = Freeze(0xE6, 0xED, 0xF3);

        // Blue flag ("let a faster car through") - genuinely blue, not a shade of amber.
        // No other alert state uses blue, so it stays visually unambiguous from Safety
        // Car / VSC amber and Yellow caution. Used directly as the flag chip's own
        // background (dark text on top), same "saturated brush as fill" pattern as the
        // tyre compound badges - no separate Bg/Text pairing needed for this one.
        public static readonly SolidColorBrush BlueText = Freeze(0x79, 0xC0, 0xFF);

        private static SolidColorBrush Freeze(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public static SolidColorBrush TextBrush(TimingColor color) => color switch
        {
            TimingColor.Purple => PurpleText,
            TimingColor.Green => GreenText,
            TimingColor.Yellow => YellowText,
            _ => NeutralText
        };

        public static SolidColorBrush BackgroundBrush(TimingColor color) => color switch
        {
            TimingColor.Purple => PurpleBg,
            TimingColor.Green => GreenBg,
            TimingColor.Yellow => YellowBg,
            _ => NeutralBg
        };
    }
}
