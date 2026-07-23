using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
// NOTE: deliberately NOT "using System.Windows.Shapes" - it makes Path ambiguous with
// System.IO.Path, which this file already uses for the export filename. The handful of shape
// types in DrawGap are fully qualified instead.
using WShapes = System.Windows.Shapes;
using Microsoft.Win32;
using F1RaceEngineer.Models;
using F1RaceEngineer.Telemetry;

namespace F1RaceEngineer.Widgets
{
    public partial class HistoryPanel : UserControl
    {
        private static readonly SolidColorBrush TabActiveBg = SavedRaceView.BrushFromHex("#1C2733");
        private static readonly SolidColorBrush TabActiveInk = SavedRaceView.BrushFromHex("#E6EDF3");
        private static readonly SolidColorBrush TabIdleInk = SavedRaceView.BrushFromHex("#6B7684");

        // Gap-evolution chart. The trace uses the app's accent blue (the same colour pit timings
        // are shown in), over a translucent fill so the side of the zero line the race was spent
        // on reads at a glance.
        private static readonly SolidColorBrush GapLineBrush = SavedRaceView.BrushFromHex("#79C0FF");
        private static readonly SolidColorBrush GapZeroBrush = SavedRaceView.BrushFromHex("#4A5460");
        private static readonly SolidColorBrush GapGridBrush = SavedRaceView.BrushFromHex("#232B35");
        // Pit markers reuse the tower's player/rival accents, so the same two colours mean the
        // same two people wherever the app distinguishes them.
        private static readonly SolidColorBrush GapPitYouBrush = FrozenAlpha(0x99, 0x1F, 0x6F, 0xEB);
        private static readonly SolidColorBrush GapPitRivalBrush = FrozenAlpha(0x99, 0x37, 0xBE, 0xDD);

        private static SolidColorBrush FrozenAlpha(byte a, byte r, byte g, byte b)
        {
            var s = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            s.Freeze();
            return s;
        }

        private readonly RaceHistoryStore _store = new();
        private readonly ObservableCollection<SeasonGroupView> _seasons = new();   // currently displayed
        private List<SeasonGroupView> _allSeasons = new();                          // every loaded save/season
        private SeasonGroupView? _saveFilter;                                       // null = ALL
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };
        private SavedRaceView? _current;            // session shown in the detail view
        private WeekendCardView? _currentWeekend;   // its weekend (Race + optional Sprint)
        private List<SavedRace> _pendingDelete = new();

        // Weekend cards per row, kept at a sensible size instead of stretching into wide
        // letterboxes: 2 at the default window, up to 4 maximised (~360px target width). Applied to
        // every season's UniformGrid in code - a RelativeSource binding to the panel doesn't resolve
        // from inside an ItemsPanelTemplate.
        public HistoryPanel()
        {
            InitializeComponent();
            SeasonList.ItemsSource = _seasons;
            _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        }

        private void ListScroller_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyCardColumns();

        // Column count derived from the scroller's own live width (so it's correct regardless of
        // when sections realise vs when a resize fires), then pushed onto every season's card grid.
        private void ApplyCardColumns()
        {
            double w = ListScroller.ActualWidth;
            if (w <= 0) return;
            int cols = System.Math.Clamp((int)(w / 360.0), 2, 4);
            foreach (var ug in FindDescendants<System.Windows.Controls.Primitives.UniformGrid>(SeasonList))
                ug.Columns = cols;
        }

        private static System.Collections.Generic.IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var d in FindDescendants<T>(child)) yield return d;
            }
        }

        /// <summary>Reloads the list from disk and returns to the list view. Called each time the panel is opened.</summary>
        public void Reload()
        {
            _allSeasons = SeasonGroupView.Build(_store.LoadAll());
            _saveFilter = null;                 // reopening always starts on ALL
            int weekends = _allSeasons.Sum(s => s.Weekends.Count);
            CountText.Text = weekends == 1 ? "1 race" : $"{weekends} races";
            EmptyState.Visibility = weekends == 0 ? Visibility.Visible : Visibility.Collapsed;
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            BuildSaveFilter();
            ApplySaveFilter();
            ShowList();
        }

        // ---- save switcher ----
        // Chips: "ALL" plus one per loaded save (season section), labelled by team·driver. Hidden
        // when there's nothing to switch between (0 or 1 save).
        private void BuildSaveFilter()
        {
            SaveFilterBar.Children.Clear();
            if (_allSeasons.Count <= 1) { SaveFilterBar.Visibility = Visibility.Collapsed; return; }
            SaveFilterBar.Visibility = Visibility.Visible;
            AddFilterChip("ALL", null);
            foreach (var s in _allSeasons)
                AddFilterChip(s.HasIdentity ? s.IdentityText : s.Label, s);
        }

        private void AddFilterChip(string text, SeasonGroupView? key)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(11, 4, 11, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = key,
                Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.Bold }
            };
            chip.MouseLeftButtonUp += FilterChip_Click;
            SaveFilterBar.Children.Add(chip);
            StyleFilterChip(chip, key == _saveFilter);
        }

        private static void StyleFilterChip(Border chip, bool active)
        {
            chip.Background = active ? TabActiveBg : System.Windows.Media.Brushes.Transparent;
            chip.BorderBrush = active ? TabActiveBg : SavedRaceView.BrushFromHex("#2A313B");
            chip.BorderThickness = new Thickness(1);
            if (chip.Child is TextBlock t) t.Foreground = active ? TabActiveInk : TabIdleInk;
        }

        private void FilterChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Border chip) return;
            _saveFilter = chip.Tag as SeasonGroupView;
            foreach (var c in SaveFilterBar.Children.OfType<Border>())
                StyleFilterChip(c, c.Tag as SeasonGroupView == _saveFilter);
            ApplySaveFilter();
        }

        private void ApplySaveFilter()
        {
            _seasons.Clear();
            foreach (var s in _allSeasons)
                if (_saveFilter == null || ReferenceEquals(s, _saveFilter)) _seasons.Add(s);
            // Sections realise after this returns; set their column counts once laid out (Background
            // runs after the layout/render pass, so the grids exist and the scroller has a width).
            Dispatcher.BeginInvoke(new System.Action(ApplyCardColumns), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ShowList()
        {
            _current = null;
            _currentWeekend = null;
            DetailRoot.Visibility = Visibility.Collapsed;
            ListRoot.Visibility = Visibility.Visible;
        }

        // ---- list ----
        private void Card_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is WeekendCardView w) OpenDetail(w);
        }

        private void CardStint_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Grid g && g.Tag is WeekendCardView w)
                StintStripRenderer.Render(g, null, w.Race.StintSegments, w.Race.TotalLaps, showLetters: false, gap: 1.5, cornerRadius: 2);
        }

        // ---- detail ----
        private void OpenDetail(WeekendCardView w)
        {
            _currentWeekend = w;

            // Sprint weekends get a Race/Sprint switcher; a plain weekend has just the one session.
            SessionTabs.Visibility = w.HasSprint ? Visibility.Visible : Visibility.Collapsed;
            ShowSession(w.Race);

            ListRoot.Visibility = Visibility.Collapsed;
            DetailRoot.Visibility = Visibility.Visible;
        }

        // Populates the detail view from one session and marks the matching tab active.
        private void ShowSession(SavedRaceView v)
        {
            _current = v;
            DName.Text = v.GrandPrix;
            DFlag.Visibility = v.HasFlag ? Visibility.Visible : Visibility.Collapsed;
            DFlagRect.Fill = v.CountryFlagBrush;
            DCountry.Visibility = v.ShowCountryCode ? Visibility.Visible : Visibility.Collapsed;
            DCountryText.Text = v.Country;
            DSub.Text = v.DetailSubtitle;
            DFinish.Text = v.FinishText;
            DFinish.Foreground = v.FinishBrush;
            DGrid.Text = v.GridText;
            DGained.Text = v.DeltaShort;
            DGained.Foreground = v.DeltaShortBrush;
            DPoints.Text = v.PointsText;
            DnfStrip.Visibility = v.HasDnfDetail ? Visibility.Visible : Visibility.Collapsed;
            DnfText.Text = v.DnfDetail;
            ClassList.ItemsSource = v.Classification;
            LapList.ItemsSource = v.Laps;
            // The penalties card is the one binding-driven part of this otherwise imperatively
            // populated detail view, so it needs the view model as its DataContext (see the note
            // in HistoryPanel.xaml). Without this its bindings fail and it always reads "No penalties".
            PenaltiesCard.DataContext = v;
            StintStripRenderer.Render(DetailStintBar, DetailStintTicks, v.StintSegments, v.TotalLaps,
                showLetters: true, gap: 3, cornerRadius: 4);

            bool isSprint = _currentWeekend?.Sprint == v;
            StyleTab(RaceTabBtn, !isSprint);
            StyleTab(SprintTabBtn, isSprint);

            // The head-to-head is per SESSION, not per weekend - a sprint weekend can have one for
            // the race, the sprint, both or neither - so it's rebuilt here on every session switch
            // rather than once when the weekend opens.
            _h2h = v.BuildHeadToHead();
            ViewTabs.Visibility = _h2h != null ? Visibility.Visible : Visibility.Collapsed;
            ShowView(showH2H: false); // switching session always lands back on the result
        }

        // ---- head to head ----
        private HeadToHeadView? _h2h;

        private void ResultTab_Click(object sender, RoutedEventArgs e) => ShowView(showH2H: false);
        private void H2HTab_Click(object sender, RoutedEventArgs e) => ShowView(showH2H: true);

        /// <summary>
        /// Swaps the detail body wholesale. The two bodies are siblings and never both visible;
        /// the header rows above them stay put, so switching view never loses which race you're in.
        /// </summary>
        private void ShowView(bool showH2H)
        {
            bool h2h = showH2H && _h2h != null;
            ResultBody.Visibility = h2h ? Visibility.Collapsed : Visibility.Visible;
            H2HBody.Visibility = h2h ? Visibility.Visible : Visibility.Collapsed;
            StyleTab(ResultTabBtn, !h2h);
            StyleTab(H2HTabBtn, h2h);

            if (!h2h || _h2h == null) return;
            PopulateH2H(_h2h);
        }

        private void PopulateH2H(HeadToHeadView v)
        {
            H2HYouName.Text = v.You.Name;
            H2HYouResult.Text = v.You.PositionText;
            H2HYouPoints.Text = v.You.PointsText;
            H2HYouLivery.Background = v.You.LiveryBrush;

            H2HRivalName.Text = v.Rival.Name;
            H2HRivalResult.Text = v.Rival.PositionText;
            H2HRivalPoints.Text = v.Rival.PointsText;
            H2HRivalLivery.Background = v.Rival.LiveryBrush;

            H2HVerdict.Text = v.VerdictText;
            H2HVerdict.Foreground = v.VerdictBrush;
            H2HMargin.Text = v.MarginText;

            // No separate driver header on the tape: it now shares a card with the verdict, which
            // already names both drivers over their own sides.
            TapeList.ItemsSource = v.Rows;

            GapLegend.Text = $"above = {v.You.Name} ahead";
            GapEmpty.Visibility = v.HasGapSeries ? Visibility.Collapsed : Visibility.Visible;
            DrawGap();

            H2HStintYouLabel.Text = v.You.Name;
            H2HStintRivalLabel.Text = v.Rival.Name;

            StopsYouName.Text = v.You.Name;
            StopsRivalName.Text = v.Rival.Name;
            StopsYouList.ItemsSource = v.You.Stops;
            StopsRivalList.ItemsSource = v.Rival.Stops;
            StopsYouEmpty.Visibility = v.You.HasStops ? Visibility.Collapsed : Visibility.Visible;
            StopsRivalEmpty.Visibility = v.Rival.HasStops ? Visibility.Collapsed : Visibility.Visible;
            StopsYouTotal.Text = v.You.StopTotalText;
            StopsRivalTotal.Text = v.Rival.StopTotalText;

            RenderH2HStints();
        }

        private void GapCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawGap();
        private void H2HStintYou_Loaded(object sender, RoutedEventArgs e) => RenderH2HStints();
        private void H2HStintRival_Loaded(object sender, RoutedEventArgs e) => RenderH2HStints();

        /// <summary>
        /// Both strategy bars share one lap axis (the longer of the two race distances), so the
        /// bars are directly comparable left-to-right - an undercut reads as a visible offset
        /// between the two rather than something you have to work out from stint lengths.
        /// </summary>
        private void RenderH2HStints()
        {
            if (_h2h == null || _current == null) return;
            int laps = Math.Max(1, _current.TotalLaps);
            StintStripRenderer.Render(H2HStintYou, H2HTicksYou, _h2h.You.StintSegments, laps, showLetters: true, gap: 3, cornerRadius: 4);
            StintStripRenderer.Render(H2HStintRival, H2HTicksRival, _h2h.Rival.StintSegments, laps, showLetters: true, gap: 3, cornerRadius: 4);
        }

        /// <summary>
        /// The gap-evolution chart: vector-drawn on every resize rather than rendered to an image,
        /// matching this project's "drawn, not captured" convention. The zero line is centred and
        /// the axis is symmetric, so "ahead" and "behind" are equally readable and the shape isn't
        /// distorted by one big swing in a single direction.
        /// </summary>
        private void DrawGap()
        {
            GapCanvas.Children.Clear();
            if (_h2h == null || !_h2h.HasGapSeries) return;

            double w = GapCanvas.ActualWidth, h = GapCanvas.ActualHeight;
            if (w < 8 || h < 8) return; // not laid out yet; SizeChanged will call back

            var series = _h2h.GapSeries;
            double mid = h / 2;
            double scale = (h / 2 - 6) / _h2h.GapMaxAbsSeconds;

            // The plot is inset from the left to clear the y-axis labels, and from the right so
            // the final lap number can't overflow the card. Previously the trace started at x=0
            // and ran underneath its own "+20s" label, while the last lap label spilled 4px past
            // the card edge.
            const double PlotLeft = 40, PlotRight = 14;
            double plotW = Math.Max(1, w - PlotLeft - PlotRight);

            double StepX(int i) => series.Count == 1 ? PlotLeft + plotW / 2 : PlotLeft + plotW * i / (series.Count - 1.0);
            double XForLap(int lap) => StepX(Math.Max(0, Math.Min(series.Count - 1, lap - series[0].Lap)));
            double Y(double gap) => mid - gap * scale;

            // ---- Y gridlines on a "nice" step (1/2/5 x a power of ten) chosen from the range,
            //      so the chart reads the same whether the race was decided by 0.4s or 40s.
            //      Majors are labelled gridlines; minors are unlabelled half-steps that give the
            //      eye something to interpolate against without adding clutter.
            double major = NiceStep(_h2h.GapMaxAbsSeconds, 3);
            for (double v = major; v <= _h2h.GapMaxAbsSeconds + 0.001; v += major)
            {
                foreach (double s in new[] { v, -v })
                {
                    double y = Y(s);
                    if (y < 2 || y > h - 2) continue;
                    GapCanvas.Children.Add(new WShapes.Line { X1 = PlotLeft, X2 = w - PlotRight, Y1 = y, Y2 = y, Stroke = GapGridBrush, StrokeThickness = 1 });
                    AddGapLabel($"{(s > 0 ? "+" : "")}{s:0.#}", 0, y - 7);
                }
                double minorV = v - major / 2;
                foreach (double s in new[] { minorV, -minorV })
                {
                    double y = Y(s);
                    if (y < 2 || y > h - 2) continue;
                    GapCanvas.Children.Add(new WShapes.Line { X1 = PlotLeft, X2 = PlotLeft + 6, Y1 = y, Y2 = y, Stroke = GapGridBrush, StrokeThickness = 1 });
                }
            }

            // Zero line: the moment-by-moment "who's actually ahead" reference. Drawn after the
            // gridlines and brighter, so it never gets lost among them.
            GapCanvas.Children.Add(new WShapes.Line { X1 = PlotLeft, X2 = w - PlotRight, Y1 = mid, Y2 = mid, Stroke = GapZeroBrush, StrokeThickness = 1 });

            // ---- Pit markers: the swings in this trace ARE the stops, so mark them. Colour
            //      matches the tower's convention - the player's own accent blue, the rival in
            //      cyan - so "which of us pitted" is the same visual language in both places.
            foreach (var (laps, brush) in new[] { (_h2h.You.PitLaps, GapPitYouBrush), (_h2h.Rival.PitLaps, GapPitRivalBrush) })
                foreach (int lap in laps)
                {
                    double x = XForLap(lap);
                    if (x < PlotLeft || x > w - PlotRight) continue;
                    GapCanvas.Children.Add(new WShapes.Line
                    {
                        X1 = x, X2 = x, Y1 = 0, Y2 = h, Stroke = brush, StrokeThickness = 1,
                        StrokeDashArray = new DoubleCollection { 3, 3 }
                    });
                }

            // ---- Filled area between the trace and the zero line. The fill is a vertical
            //      gradient with a hard stop at the zero line, so "ahead" reads green and
            //      "behind" reads red without having to split the geometry at every crossing.
            var fill = new System.Windows.Media.PathGeometry();
            var fig = new System.Windows.Media.PathFigure { StartPoint = new Point(StepX(0), mid), IsClosed = true, IsFilled = true };
            for (int i = 0; i < series.Count; i++)
                fig.Segments.Add(new System.Windows.Media.LineSegment(new Point(StepX(i), Y(series[i].GapSeconds)), false));
            fig.Segments.Add(new System.Windows.Media.LineSegment(new Point(StepX(series.Count - 1), mid), false));
            fill.Figures.Add(fig);
            GapCanvas.Children.Add(new WShapes.Path { Data = fill, Fill = SplitFill(h, mid) });

            var poly = new WShapes.Polyline { Stroke = GapLineBrush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            for (int i = 0; i < series.Count; i++)
                poly.Points.Add(new Point(StepX(i), Y(series[i].GapSeconds)));
            GapCanvas.Children.Add(poly);

            // ---- Lap axis: majors on a nice step scaled to race length (a 5-lap sprint and a
            //      78-lap grand prix both get a readable number of labels), minors between.
            GapAxis.Children.Clear();
            int firstLap = series[0].Lap, lastLap = series[^1].Lap;
            int lapMajor = Math.Max(1, (int)NiceStep(Math.Max(1, lastLap - firstLap), 5));
            for (int lap = firstLap; lap <= lastLap; lap++)
            {
                bool isMajor = lap == firstLap || lap == lastLap || (lap - firstLap) % lapMajor == 0;
                double x = XForLap(lap);
                if (isMajor) AddAxisLabel($"{lap}", x - 6);
                else if (lapMajor > 2)
                    GapAxis.Children.Add(new WShapes.Line { X1 = x, X2 = x, Y1 = 0, Y2 = 3, Stroke = GapGridBrush, StrokeThickness = 1 });
            }
        }

        /// <summary>
        /// A "nice" axis step - 1, 2 or 5 times a power of ten - closest to range/target. Keeps
        /// gridlines on round values at any scale, so the same chart code reads correctly whether
        /// the two drivers finished 0.4s or 40s apart.
        /// </summary>
        private static double NiceStep(double range, int target)
        {
            if (range <= 0 || target <= 0) return 1;
            double raw = range / target;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double norm = raw / mag;
            double step = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 5 ? 5 : 10;
            return step * mag;
        }

        /// <summary>
        /// Green above the zero line, red below, with a hard transition exactly on it. Absolute
        /// mapping ties the gradient to the canvas rather than the path's own bounds, which shift
        /// with the data and would otherwise put the colour change in the wrong place.
        /// </summary>
        private static Brush SplitFill(double h, double mid)
        {
            double t = h <= 0 ? 0.5 : mid / h;
            var b = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, h)
            };
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x3C, 0x97, 0xC4, 0x59), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x3C, 0x97, 0xC4, 0x59), t));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x3C, 0xE1, 0x2E, 0x2E), t));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x3C, 0xE1, 0x2E, 0x2E), 1));
            b.Freeze();
            return b;
        }

        private void AddGapLabel(string text, double x, double y)
        {
            var t = new TextBlock { Text = text, FontSize = 9.5, Foreground = TabIdleInk, FontFamily = ConsolasFont };
            Canvas.SetLeft(t, x);
            Canvas.SetTop(t, y);
            GapCanvas.Children.Add(t);
        }

        private void AddAxisLabel(string text, double x)
        {
            var t = new TextBlock { Text = text, FontSize = 9.5, Foreground = TabIdleInk, FontFamily = ConsolasFont };
            Canvas.SetLeft(t, Math.Max(0, x));
            Canvas.SetTop(t, 0);
            GapAxis.Children.Add(t);
        }

        private static readonly FontFamily ConsolasFont = new("Consolas");

        private static void StyleTab(Border tab, bool active)
        {
            tab.Background = active ? TabActiveBg : System.Windows.Media.Brushes.Transparent;
            if (tab.Child is TextBlock t) t.Foreground = active ? TabActiveInk : TabIdleInk;
        }

        private void RaceTab_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWeekend != null) ShowSession(_currentWeekend.Race);
        }

        private void SprintTab_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWeekend?.Sprint is SavedRaceView s) ShowSession(s);
        }

        private void Back_Click(object sender, RoutedEventArgs e) => ShowList();

        // ---- export ----
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true; // don't also open the card
            if ((sender as FrameworkElement)?.Tag is WeekendCardView w) ExportRace(w);
        }

        private void DetailExport_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWeekend != null) ExportRace(_currentWeekend);
        }

        /// <summary>
        /// Exports the whole WEEKEND, not one session: a sprint weekend used to export only the
        /// feature race, and the head-to-head never left the app at all. Both export buttons now
        /// produce the same complete document, so which one was pressed can't change what you get.
        /// </summary>
        private void ExportRace(WeekendCardView w)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Web page (*.html)|*.html",
                FileName = Sanitize($"{w.Race.Source.Circuit}-{w.Race.GrandPrix}") + ".html"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                // Explicit UTF-8 BOM so the em-dashes / arrows / flag glyphs decode correctly no
                // matter what a consumer assumes, not just when it honours the meta charset.
                File.WriteAllText(dlg.FileName, RaceHtmlExporter.Export(w), new UTF8Encoding(true));
                ShowToast($"Exported {Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                ShowToast($"Export failed: {ex.Message}");
            }
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
            return name.Replace(' ', '-');
        }

        // ---- delete ----
        // From a list card: removes the whole weekend (both sessions of a Sprint weekend).
        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.Tag is not WeekendCardView w) return;
            string what = w.HasSprint ? $"“{w.Race.GrandPrix}” weekend (Race + Sprint)" : $"“{w.Race.GrandPrix}”";
            ShowDeleteConfirm(w.Sessions.Select(s => s.Source).ToList(), what);
        }

        // From the detail view: removes only the session currently shown.
        private void DetailDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_current != null) ShowDeleteConfirm(new List<SavedRace> { _current.Source }, $"“{_current.GrandPrix}”");
        }

        private void ShowDeleteConfirm(List<SavedRace> races, string what)
        {
            _pendingDelete = races;
            ConfirmText.Text = $"Delete {what} from your race history?";
            ConfirmOverlay.Visibility = Visibility.Visible;
        }

        private void ConfirmCancel_Click(object sender, RoutedEventArgs e) => ConfirmOverlay.Visibility = Visibility.Collapsed;

        private void ConfirmDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingDelete.Count == 0) return;
            string gp = _pendingDelete[0].GrandPrix;
            foreach (var r in _pendingDelete) _store.Delete(r);
            _pendingDelete = new List<SavedRace>();
            Reload();
            ShowToast($"Deleted {gp}");
        }

        // ---- toast ----
        private void ShowToast(string text)
        {
            ToastText.Text = text;
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Start();
        }
    }
}
