using System.Windows.Media;

namespace F1RaceEngineer.Models
{
    public enum LapEventKind { SafetyCar, VirtualSafetyCar, RedFlag, Chequered, Penalty }

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

        public bool IsFlag => Kind is LapEventKind.SafetyCar or LapEventKind.VirtualSafetyCar or LapEventKind.RedFlag;
        public bool IsChequered => Kind == LapEventKind.Chequered;
        public bool IsPenalty => Kind == LapEventKind.Penalty;

        // Waved-flag fill: yellow for a caution (SC/VSC), red for a red flag.
        public SolidColorBrush IconBrush => Kind switch
        {
            LapEventKind.RedFlag => Red,
            _ => Yellow
        };

        public SolidColorBrush TextBrush => Kind switch
        {
            LapEventKind.SafetyCar or LapEventKind.VirtualSafetyCar => Yellow,
            LapEventKind.RedFlag => Red,
            LapEventKind.Penalty => Amber,
            _ => Neutral
        };

        private static readonly SolidColorBrush Yellow = Frozen(0xE8, 0xC5, 0x2E);
        private static readonly SolidColorBrush Red = Frozen(0xE1, 0x2E, 0x2E);
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
