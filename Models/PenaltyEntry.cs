using System;
using System.Windows.Media;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// One line in the Penalties &amp; Flags list (live widget and saved-race card alike).
    ///
    /// The colour carries the category, so the text doesn't have to: a RED chip is a real penalty
    /// (stop-go / drive-through / time), an AMBER one is a warning. That's why the wording drops the
    /// redundant "Warning - " prefix and never says "Penalty" - what survives is the part that
    /// actually informs (the infringement, and for a penalty its magnitude, e.g. "+5s - Track
    /// limits"). Amber is deliberately the same amber Car Condition uses for damage; red is what now
    /// separates an actionable penalty from everything else at a glance.
    /// </summary>
    public class PenaltyEntry : IEquatable<PenaltyEntry>
    {
        public string Text { get; init; } = "";

        /// <summary>True = a genuine penalty (red). False = a warning, or the "+N more" overflow line (amber).</summary>
        public bool IsPenalty { get; init; }

        public Brush Background => IsPenalty ? PenaltyBg : WarningBg;
        public Brush Foreground => IsPenalty ? PenaltyInk : WarningInk;

        private static readonly SolidColorBrush WarningBg = Frozen(0x41, 0x24, 0x02);
        private static readonly SolidColorBrush WarningInk = Frozen(0xEF, 0x9F, 0x27);
        private static readonly SolidColorBrush PenaltyBg = Frozen(0x4A, 0x15, 0x19);
        private static readonly SolidColorBrush PenaltyInk = Frozen(0xFF, 0x8A, 0x8A);

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        // Brushes are static and frozen, so value equality on the two real fields is enough - this
        // feeds CollectionUnchanged, which only wants to know if the rendered list actually changed.
        public bool Equals(PenaltyEntry? other) =>
            other != null && Text == other.Text && IsPenalty == other.IsPenalty;

        public override bool Equals(object? obj) => Equals(obj as PenaltyEntry);
        public override int GetHashCode() => HashCode.Combine(Text, IsPenalty);
    }
}
