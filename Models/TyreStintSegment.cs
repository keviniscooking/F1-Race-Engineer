using System.Windows.Media;

namespace F1RaceEngineer.Models
{
    /// <summary>
    /// One compound stint in the player's live tyre-strategy bar (the broadcast-style strip
    /// in the Tyres widget). Rendered as a proportional coloured segment - <see cref="LapCount"/>
    /// drives the segment's share of the bar width - with the compound letter on top. Built by
    /// TelemetryState from the player's compound changes during a race; see the Tyres widget's
    /// code-behind for how the bar is laid out.
    /// </summary>
    public class TyreStintSegment
    {
        public int LapCount { get; set; }
        public string Letter { get; set; } = "";
        public SolidColorBrush Brush { get; set; } = CompoundPalette.Unknown;

        // Text colour for the letter drawn on the segment - dark on the light compounds
        // (medium/hard), light on the saturated ones, so it stays legible on any band.
        public SolidColorBrush TextBrush { get; set; } = TimingColorPalette.NeutralText;
    }
}
