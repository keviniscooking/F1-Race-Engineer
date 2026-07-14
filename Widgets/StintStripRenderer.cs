using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using F1RaceEngineer.Models;

namespace F1RaceEngineer.Widgets
{
    /// <summary>
    /// Shared renderer for the broadcast-style tyre-strategy bar, used both live (Tyres widget)
    /// and in the saved-race detail (History panel) so the two look and scale identically:
    /// coloured stint segments over a full-race-length "ghost" track, with a lap axis below
    /// (labelled majors every 5 laps, faint minors when there's room, and accent pit markers at
    /// each stop).
    ///
    /// The bar is a star-column Grid weighted by each stint's lap count, plus a trailing
    /// transparent "remaining" column (totalLaps - laps drawn) so the ghost shows through - which
    /// is what makes a live race fill lap by lap instead of always looking full. The axis is a
    /// Canvas positioned by fraction of its own ActualWidth (re-laid on resize), so ticks align
    /// exactly to the segment boundaries regardless of the bar's final width.
    /// </summary>
    public static class StintStripRenderer
    {
        private static readonly FontFamily MonoFont = new("Consolas");
        private static readonly SolidColorBrush MajorTick = Frozen("#4A5460");
        private static readonly SolidColorBrush MinorTick = Frozen("#2A313B");
        private static readonly SolidColorBrush MajorText = Frozen("#6B7684");
        private static readonly SolidColorBrush PitTick = Frozen("#79C0FF");

        /// <param name="ticks">Axis host; pass null for a bare bar (e.g. the tiny list-card strips).</param>
        public static void Render(Grid bar, Canvas? ticks, IReadOnlyList<TyreStintSegment> segs,
            int totalLaps, bool showLetters, double gap = 3, double cornerRadius = 4)
        {
            bar.ColumnDefinitions.Clear();
            bar.Children.Clear();

            int drawn = 0;
            foreach (var s in segs) drawn += Math.Max(1, s.LapCount);
            // Denominator = the whole race, so the bar scales to race length; fall back to the
            // laps actually drawn when the total is unknown (very old saves / non-race sessions).
            int denom = Math.Max(Math.Max(totalLaps, drawn), 1);

            for (int i = 0; i < segs.Count; i++)
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1, segs[i].LapCount), GridUnitType.Star) });

            int remaining = denom - drawn;
            if (remaining > 0)
                bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(remaining, GridUnitType.Star) });

            for (int i = 0; i < segs.Count; i++)
            {
                var seg = segs[i];
                var border = new Border
                {
                    Background = seg.Brush,
                    CornerRadius = new CornerRadius(cornerRadius),
                    Margin = new Thickness(i == 0 ? 0 : gap, 0, 0, 0)
                };
                if (showLetters)
                    border.Child = new TextBlock
                    {
                        Text = seg.Letter, Foreground = seg.TextBrush, FontWeight = FontWeights.Bold,
                        FontSize = 12, FontFamily = MonoFont,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    };
                Grid.SetColumn(border, i);
                bar.Children.Add(border);
            }

            if (ticks == null) return;

            // Pit laps = cumulative stint ends, excluding the final segment's end (which is the
            // chequered flag when finished, or the live leading edge - neither is a pit stop).
            var pitLaps = new List<int>();
            int acc = 0;
            for (int i = 0; i < segs.Count - 1; i++) { acc += Math.Max(1, segs[i].LapCount); pitLaps.Add(acc); }

            var data = new AxisData(denom, pitLaps);
            ticks.Tag = data;
            ticks.SizeChanged -= OnTicksSizeChanged; // idempotent: same static handler, so -= then += keeps a single subscription
            ticks.SizeChanged += OnTicksSizeChanged;
            DrawAxis(ticks, data);
        }

        private static void OnTicksSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is Canvas c && c.Tag is AxisData d) DrawAxis(c, d);
        }

        private static void DrawAxis(Canvas ticks, AxisData d)
        {
            ticks.Children.Clear();
            double w = ticks.ActualWidth;
            if (w <= 0 || d.Denom <= 0) return; // not laid out yet - SizeChanged will redraw
            double perLap = w / d.Denom;

            // Faint minor tick every lap, but only when they'd be far enough apart to read;
            // on a long race in a narrow bar they'd collapse into a grey smear, so drop them.
            if (perLap >= 5)
                for (int lap = 1; lap < d.Denom; lap++)
                {
                    if (lap % 5 == 0 || d.PitLaps.Contains(lap)) continue;
                    AddTick(ticks, lap * perLap, 3, MinorTick, 1);
                }

            // Labelled majors every 5 laps, skipped where a pit marker already claims the spot.
            for (int lap = 5; lap < d.Denom; lap += 5)
            {
                if (d.PitLaps.Contains(lap)) continue;
                double x = lap * perLap;
                AddTick(ticks, x, 5, MajorTick, 1);
                AddLabel(ticks, x, lap.ToString(), MajorText, false);
            }

            // Accent pit markers with the exact pit lap - the reason for the axis (item 1).
            foreach (int lap in d.PitLaps)
            {
                double x = lap * perLap;
                AddTick(ticks, x, 6, PitTick, 1.5);
                AddLabel(ticks, x, "L" + lap, PitTick, true);
            }
        }

        private static void AddTick(Canvas c, double x, double height, Brush brush, double thickness)
        {
            var line = new Border { Width = thickness, Height = height, Background = brush };
            Canvas.SetLeft(line, x - thickness / 2);
            Canvas.SetTop(line, 0);
            c.Children.Add(line);
        }

        private static void AddLabel(Canvas c, double x, string text, Brush brush, bool bold)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = 9, FontFamily = MonoFont, Foreground = brush,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, x - tb.DesiredSize.Width / 2);
            Canvas.SetTop(tb, 7);
            c.Children.Add(tb);
        }

        private sealed record AxisData(int Denom, List<int> PitLaps);

        private static SolidColorBrush Frozen(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
    }
}
