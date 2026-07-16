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
            StintStripRenderer.Render(DetailStintBar, DetailStintTicks, v.StintSegments, v.TotalLaps,
                showLetters: true, gap: 3, cornerRadius: 4);

            bool isSprint = _currentWeekend?.Sprint == v;
            StyleTab(RaceTabBtn, !isSprint);
            StyleTab(SprintTabBtn, isSprint);
        }

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
            if ((sender as FrameworkElement)?.Tag is WeekendCardView w) ExportRace(w.Race);
        }

        private void DetailExport_Click(object sender, RoutedEventArgs e)
        {
            if (_current != null) ExportRace(_current);
        }

        private void ExportRace(SavedRaceView v)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Web page (*.html)|*.html",
                FileName = Sanitize($"{v.Source.Circuit}-{v.GrandPrix}") + ".html"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                // Explicit UTF-8 BOM so the em-dashes / arrows / flag glyphs decode correctly no
                // matter what a consumer assumes, not just when it honours the meta charset.
                File.WriteAllText(dlg.FileName, RaceHtmlExporter.Export(v), new UTF8Encoding(true));
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
