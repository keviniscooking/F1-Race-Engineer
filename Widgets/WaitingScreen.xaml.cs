using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using F1RaceEngineer.Models;
using F1RaceEngineer.Telemetry;

namespace F1RaceEngineer.Widgets
{
    /// <summary>
    /// The "waiting for a session" screen: a formation lap of cars circulating a real circuit, plus
    /// rotating trivia. The cars run in the order of your last saved race (randomised if there is
    /// none); the circuit is that race's track (a random one otherwise). See docs/waiting-screen and
    /// HANDOFF §8 for the full design.
    ///
    /// The animation runs on CompositionTarget.Rendering and is started/stopped by the host via
    /// <see cref="Start"/> / <see cref="Stop"/>. It MUST be stopped whenever this screen isn't
    /// visible - that is the one hard performance rule, so the loop never runs while you're racing.
    /// </summary>
    public partial class WaitingScreen : UserControl
    {
        /// <summary>Raised by the "not receiving data?" link, so the host can open the help card.</summary>
        public event EventHandler? HelpRequested;

        private const double CarLen = 42;         // car length in the 1000-unit canvas
        private static readonly Brush Dark = Frozen(0x0C, 0x0F, 0x14);
        private static readonly string[] FallbackLiveries =
            { "#3671C6", "#E8002D", "#27F4D2", "#FF8000", "#229971", "#64C4FF", "#FF87BC", "#6692FF", "#B6BABD", "#52E252" };

        private PathGeometry? _geometry;
        private double _approxLength;
        private readonly List<(FrameworkElement Car, TextBlock Label, double Offset)> _cars = new();

        private bool _running;
        private double _progress;                 // fraction of a lap [0,1)
        private TimeSpan _lastRenderTime;

        private readonly DispatcherTimer _triviaTimer = new() { Interval = TimeSpan.FromSeconds(12) };
        private List<(string Q, string A)> _trivia = new();
        private int _triviaIndex = -1;
        private (string Q, string A) _pendingTrivia;

        public WaitingScreen()
        {
            InitializeComponent();
            _triviaTimer.Tick += (_, _) => NextTrivia();
        }

        /// <summary>Builds the scene fresh and begins animating. Idempotent - a second call while
        /// already running is ignored.</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;

            BuildScene();

            _trivia = WaitingData.Trivia.ToList();
            Shuffle(_trivia);
            _triviaIndex = -1;
            NextTrivia();
            _triviaTimer.Start();

            _lastRenderTime = TimeSpan.Zero;
            CompositionTarget.Rendering += OnRendering;
        }

        /// <summary>Halts the animation and trivia. The one hard rule: called whenever the screen
        /// stops being visible, so nothing ticks while racing.</summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;
            CompositionTarget.Rendering -= OnRendering;
            _triviaTimer.Stop();
        }

        // ---- scene ----

        private void BuildScene()
        {
            TrackCanvas.Children.Clear();
            _cars.Clear();

            var (track, order, showNames) = ResolveScene();
            if (track == null || track.Points.Count < 4) return;

            _geometry = BuildGeometry(track.Points);
            _approxLength = ChordLength(track.Points);

            TrackName.Text = track.Circuit.Split(new[] { " - " }, StringSplitOptions.None)[0];
            string km = (track.Len / 1000.0).ToString("0.000") + " km";
            TrackLen.Text = track.FirstGp > 0 ? $"{km}   ·   first Grand Prix {track.FirstGp}" : km;

            // Show the country flag; fall back to the 3-letter code only if that country has no
            // flag design (all 24 tracks currently do, but the fallback keeps a new one from blanking).
            string cc = CountryFromId(track.Id);
            var flag = FlagPalette.BrushFor(cc);
            if (flag != null)
            {
                TrackFlag.Fill = flag;
                TrackFlagBorder.Visibility = Visibility.Visible;
                TrackCc.Visibility = Visibility.Collapsed;
            }
            else
            {
                TrackCc.Text = cc;
                TrackCc.Visibility = Visibility.Visible;
                TrackFlagBorder.Visibility = Visibility.Collapsed;
            }

            // track ribbon + dashed centre line
            var ribbon = new Path { Data = _geometry, Stroke = Frozen(0x2F, 0x3A, 0x48), StrokeThickness = 17,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            TrackCanvas.Children.Add(ribbon);
            var centre = new Path { Data = _geometry, Stroke = Frozen(0x3E, 0x4B, 0x5C), StrokeThickness = 1.4,
                StrokeDashArray = new DoubleCollection { 2, 9 }, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0.65 };
            TrackCanvas.Children.Add(centre);

            AddStartFinish();
            AddCorners(track.Id);

            // cars ~two car lengths apart (the original, roomier spacing), leader at the front. A
            // larger offset sits further along the path in the direction of travel (i.e. ahead), so
            // the classification leader (order[0]) needs the LARGEST offset, not zero - otherwise the
            // field runs backwards, P10 leading the leader.
            double gap = _approxLength > 0 ? (2 * CarLen) / _approxLength : 0.03;
            int n = order.Count;
            // Safety net for the full field on a short circuit: never let the train wrap past ~85% of
            // the lap and overlap its own tail. On a normal-length track this never binds, so the
            // spacing above is preserved exactly.
            if (n > 1) gap = Math.Min(gap, 0.85 / (n - 1));
            for (int i = 0; i < n; i++)
            {
                var (code, brush) = order[i];
                var car = BuildCar(brush);
                TrackCanvas.Children.Add(car);
                var label = new TextBlock { Text = code, FontFamily = new FontFamily("Consolas"), FontSize = 15,
                    FontWeight = FontWeights.Bold, Foreground = Frozen(0x58, 0xA6, 0xFF),
                    Visibility = showNames && code.Length > 0 ? Visibility.Visible : Visibility.Collapsed };
                TrackCanvas.Children.Add(label);
                _cars.Add((car, label, (n - 1 - i) * gap));
            }
            _progress = 0;
            PlaceCars();
        }

        /// <summary>
        /// Picks the circuit and the car order. Newest saved race drives both - its track and its
        /// FULL finishing order (however many cars it holds - 20 in F1 25, 22 in F1 26), with driver
        /// codes. No saved race: a random circuit, and a full grid's worth of cars in a shuffled
        /// fallback set of team colours with no names (we have no drivers to name).
        /// </summary>
        private (TrackOutline? Track, List<(string Code, Brush Brush)> Order, bool ShowNames) ResolveScene()
        {
            var tracks = WaitingData.Tracks;
            if (tracks.Count == 0) return (null, new(), false);

            var races = new RaceHistoryStore().LoadAll();   // newest first
            var last = races.Count > 0 ? races[0] : null;

            TrackOutline? track = null;
            if (last != null && WaitingData.CircuitToId.TryGetValue(last.Circuit, out var id))
                tracks.TryGetValue(id, out track);
            track ??= tracks.Values.ElementAt(Random.Shared.Next(tracks.Count));

            var order = new List<(string, Brush)>();
            if (last != null && last.Classification.Count > 0)
            {
                // The whole field, in finishing order - no cap. The count is however many the saved
                // race captured, so it tracks the game's grid size automatically.
                foreach (var row in last.Classification.OrderBy(r => r.Position))
                    order.Add((DriverCode(row.DriverName), SavedRaceView.BrushFromHex(row.LiveryHex)));
                return (track, order, true);
            }

            // Cold start: no saved race to size or colour the grid, so mock a full 20-car field -
            // each of the ten team colours twice (two cars per team) - and shuffle it.
            var shuffled = new List<string>(FallbackLiveries);
            shuffled.AddRange(FallbackLiveries);
            Shuffle(shuffled);
            foreach (var hex in shuffled) order.Add(("", SavedRaceView.BrushFromHex(hex)));
            return (track, order, false);
        }

        // ---- geometry ----

        private static PathGeometry BuildGeometry(IReadOnlyList<Point> p)
        {
            int n = p.Count;
            var fig = new PathFigure { StartPoint = p[0], IsClosed = true, IsFilled = false };
            for (int i = 0; i < n; i++)
            {
                Point a = p[(i - 1 + n) % n], b = p[i], c = p[(i + 1) % n], d = p[(i + 2) % n];
                var c1 = new Point(b.X + (c.X - a.X) / 6, b.Y + (c.Y - a.Y) / 6);
                var c2 = new Point(c.X - (d.X - b.X) / 6, c.Y - (d.Y - b.Y) / 6);
                fig.Segments.Add(new BezierSegment(c1, c2, c, true));
            }
            var g = new PathGeometry();
            g.Figures.Add(fig);
            return g;
        }

        private static double ChordLength(IReadOnlyList<Point> p)
        {
            double len = 0;
            for (int i = 0; i < p.Count; i++)
            {
                var a = p[i]; var b = p[(i + 1) % p.Count];
                len += Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            }
            return len;
        }

        private void AddStartFinish()
        {
            if (_geometry == null) return;
            _geometry.GetPointAtFractionLength(0.002, out var p, out var tan);
            double ang = Math.Atan2(tan.Y, tan.X) * 180 / Math.PI;
            var g = new Canvas();
            g.Children.Add(new Rectangle { Width = 5.2, Height = 28, Fill = Frozen(0x0D, 0x11, 0x17), Opacity = 0.55,
                RenderTransform = new TranslateTransform(-2.6, -14) });
            for (int i = 0; i < 6; i++)
                g.Children.Add(new Rectangle { Width = 3.2, Height = 2.5,
                    Fill = i % 2 == 0 ? Frozen(0xE6, 0xED, 0xF3) : Frozen(0x0D, 0x11, 0x17),
                    RenderTransform = new TranslateTransform(-1.6, -13 + i * 4.3) });
            g.RenderTransform = new TransformGroup { Children = { new RotateTransform(ang), new TranslateTransform(p.X, p.Y) } };
            TrackCanvas.Children.Add(g);
        }

        private void AddCorners(string trackId)
        {
            if (_geometry == null || !WaitingData.Corners.TryGetValue(trackId, out var corners)) return;
            double cx = 500, cy = 500;   // canvas centre; labels pushed outward from here
            foreach (var corner in corners)
            {
                _geometry.GetPointAtFractionLength(corner.Fraction, out var pt, out _);
                double dx = pt.X - cx, dy = pt.Y - cy, m = Math.Sqrt(dx * dx + dy * dy);
                if (m < 1) m = 1;
                dx /= m; dy /= m;
                TrackCanvas.Children.Add(new Line { X1 = pt.X, Y1 = pt.Y, X2 = pt.X + dx * 22, Y2 = pt.Y + dy * 22,
                    Stroke = Frozen(0x3E, 0x4B, 0x5C), StrokeThickness = 1.2 });
                TrackCanvas.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = Frozen(0x0D, 0x11, 0x17),
                    Stroke = Frozen(0x58, 0xA6, 0xFF), StrokeThickness = 2,
                    RenderTransform = new TranslateTransform(pt.X - 4.5, pt.Y - 4.5) });
                var label = new TextBlock { Text = corner.Name, FontFamily = new FontFamily("Consolas"), FontSize = 20,
                    Foreground = Frozen(0x8B, 0x97, 0xA4) };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double lx = pt.X + dx * 40, ly = pt.Y + dy * 40;
                // anchor: nudge left-side labels left by their width so text doesn't run off the track
                if (dx < -0.25) lx -= label.DesiredSize.Width;
                else if (dx <= 0.25) lx -= label.DesiredSize.Width / 2;
                Canvas.SetLeft(label, lx);
                Canvas.SetTop(label, ly - label.DesiredSize.Height / 2);
                TrackCanvas.Children.Add(label);
            }
        }

        // ---- cars ----

        // A car is drawn as a simple livery dot: cleaner on the thin track ribbon than a little car
        // shape. Sits in a Canvas centred on the origin so PlaceCars' translate lands it on the
        // racing line; the rotation PlaceCars also applies is a no-op on a circle.
        private static Canvas BuildCar(Brush body)
        {
            const double d = 15;   // dot diameter in the 1000-unit canvas
            var c = new Canvas();
            c.Children.Add(new Ellipse { Width = d, Height = d, Fill = body, Stroke = Dark, StrokeThickness = 2,
                RenderTransform = new TranslateTransform(-d / 2, -d / 2) });
            return c;
        }

        private void PlaceCars()
        {
            if (_geometry == null) return;
            foreach (var (car, label, off) in _cars)
            {
                double f = (_progress + off) % 1.0;
                if (f < 0) f += 1.0;
                _geometry.GetPointAtFractionLength(f, out var p, out var tan);
                double ang = Math.Atan2(tan.Y, tan.X) * 180 / Math.PI;
                car.RenderTransform = new TransformGroup { Children = { new RotateTransform(ang), new TranslateTransform(p.X, p.Y) } };
                Canvas.SetLeft(label, p.X - 14);
                Canvas.SetTop(label, p.Y - 30);
            }
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (!_running || _geometry == null) return;
            var args = (RenderingEventArgs)e;
            if (_lastRenderTime == TimeSpan.Zero) { _lastRenderTime = args.RenderingTime; return; }
            double dtMs = (args.RenderingTime - _lastRenderTime).TotalMilliseconds;
            _lastRenderTime = args.RenderingTime;
            if (dtMs <= 0) return;
            if (dtMs > 60) dtMs = 60;   // don't lurch after a stall

            // ~one lap of the drawn path every ~52s - an unhurried formation-lap crawl
            double perMs = 1.0 / (52000.0);
            _progress = (_progress + perMs * dtMs) % 1.0;
            PlaceCars();
        }

        // ---- trivia ----

        private void NextTrivia()
        {
            if (_trivia.Count == 0) return;
            _triviaIndex = (_triviaIndex + 1) % _trivia.Count;
            _pendingTrivia = _trivia[_triviaIndex];

            // First question: nothing to fade out. Otherwise cross-fade the current pair away, then
            // swap - so questions dissolve into each other rather than snapping.
            if (string.IsNullOrEmpty(TriviaQ.Text)) { SwapAndReveal(); return; }
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320));
            fadeOut.Completed += (_, _) => SwapAndReveal();
            TriviaQA.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void SwapAndReveal()
        {
            TriviaA.BeginAnimation(OpacityProperty, null);
            TriviaA.Opacity = 0;                 // answer starts hidden under the (about to fade in) pair
            TriviaQ.Text = _pendingTrivia.Q;
            TriviaA.Text = _pendingTrivia.A;
            TriviaQA.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360)));
            // reveal the answer after ~3s of thinking time (its opacity composes under the wrapper's)
            TriviaA.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450)) { BeginTime = TimeSpan.FromSeconds(3) });
        }

        // ---- helpers ----

        private void Help_Click(object sender, RoutedEventArgs e) => HelpRequested?.Invoke(this, EventArgs.Empty);

        private static string DriverCode(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var surname = parts.Length > 0 ? parts[^1] : name;
            var up = surname.ToUpperInvariant();
            return up.Length >= 3 ? up[..3] : up;
        }

        // FIA country code for the header, derived from the two-letter geojson id prefix. A small
        // fixed map beats another data column for 24 known tracks.
        private static string CountryFromId(string id)
        {
            var cc = id.Length >= 2 ? id[..2] : id;
            return cc switch
            {
                "au" => "AUS", "cn" => "CHN", "jp" => "JPN", "bh" => "BHR", "sa" => "KSA",
                "us" => "USA", "ca" => "CAN", "mc" => "MON", "es" => "ESP", "at" => "AUT",
                "gb" => "GBR", "hu" => "HUN", "be" => "BEL", "nl" => "NED", "it" => "ITA",
                "az" => "AZE", "sg" => "SGP", "mx" => "MEX", "br" => "BRA", "qa" => "QAT", "ae" => "UAE",
                _ => cc.ToUpperInvariant()
            };
        }

        private static void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }
    }
}
