using System.Windows.Media;

namespace F1RaceEngineer.Models
{
    // SavedLapEvent persists the kind by NAME (Kind.ToString()), not by ordinal, so members can
    // be added or reordered freely - but a member must never be RENAMED without migrating, or
    // every already-saved race carrying it fails to parse back.
    public enum LapEventKind { SafetyCar, VirtualSafetyCar, RedFlag, Chequered, Penalty, Warning, Restart, Fault, FaultFixed }

    /// <summary>
    /// A notable thing that happened on one lap - a Safety Car / VSC caution, a red flag, the
    /// chequered flag, or a genuine penalty - shown as a small icon+text chip in the lap-by-lap
    /// "EVENTS" gap (see LapEventChip). One render model shared by the live Lap History widget
    /// and the saved-race history view; the icon and colours are derived from <see cref="Kind"/>,
    /// so the serialised form (SavedLapEvent) only needs to store the kind + text.
    /// </summary>
    public class LapEvent
    {
        public LapEventKind Kind { get; }
        public string Text { get; }

        public LapEvent(LapEventKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }

        public bool IsFlag => Kind is LapEventKind.SafetyCar or LapEventKind.VirtualSafetyCar or LapEventKind.RedFlag or LapEventKind.Restart;
        public bool IsChequered => Kind == LapEventKind.Chequered;
        // Warnings reuse the penalty chip's "!" marker - the colour separates them (red penalty vs
        // amber warning), the same convention the Penalties & Flags list now uses.
        public bool IsPenalty => Kind is LapEventKind.Penalty or LapEventKind.Warning;

        /// <summary>
        /// A car fault appearing or clearing (DRS, ERS, engine). Gets its own warning-triangle
        /// glyph rather than reusing the "!" badge, which means an infringement everywhere else in
        /// the app - a DRS fault is something that happened TO the car, not something you did.
        /// </summary>
        public bool IsFault => Kind is LapEventKind.Fault or LapEventKind.FaultFixed;

        // Waved-flag fill: yellow for a caution (SC/VSC), red for a red flag, green for the
        // restart that ends one.
        public SolidColorBrush IconBrush => Kind switch
        {
            LapEventKind.RedFlag => Red,
            LapEventKind.Restart => Green,
            _ => Yellow
        };

        public SolidColorBrush TextBrush => Kind switch
        {
            LapEventKind.SafetyCar or LapEventKind.VirtualSafetyCar => Yellow,
            LapEventKind.RedFlag => Red,
            LapEventKind.Restart => Green,
            // Red = a real penalty, amber = a warning - matching the Penalties & Flags chips, so
            // the same colour means the same thing wherever an infringement is shown.
            LapEventKind.Penalty => PenaltyRed,
            LapEventKind.Warning => Amber,
            // Amber when a fault appears, green when it clears - the same "problem" / "resolved"
            // pairing the Red Flag and Restart chips already use.
            LapEventKind.Fault => Amber,
            LapEventKind.FaultFixed => Green,
            _ => Neutral
        };

        private static readonly SolidColorBrush Yellow = Frozen(0xE8, 0xC5, 0x2E);
        private static readonly SolidColorBrush Red = Frozen(0xE1, 0x2E, 0x2E);
        // Same green as the alert banner's "Racing resumes" (TimingColorPalette.AlertGreenText):
        // one restart colour wherever the app says racing is back on.
        private static readonly SolidColorBrush Green = Frozen(0x9B, 0xE0, 0xA5);
        private static readonly SolidColorBrush PenaltyRed = Frozen(0xFF, 0x8A, 0x8A);
        private static readonly SolidColorBrush Amber = Frozen(0xF0, 0x88, 0x3E);
        private static readonly SolidColorBrush Neutral = Frozen(0x9B, 0xA7, 0xB4);

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var s = new SolidColorBrush(Color.FromRgb(r, g, b));
            s.Freeze();
            return s;
        }
    }
}
