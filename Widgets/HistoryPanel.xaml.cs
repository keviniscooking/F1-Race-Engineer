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
        private readonly ObservableCollection<SeasonGroupView> _seasons = new();
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };
        private SavedRaceView? _current;            // session shown in the detail view
        private WeekendCardView? _currentWeekend;   // its weekend (Race + optional Sprint)
        private List<SavedRace> _pendingDelete = new();

        public HistoryPanel()
        {
            InitializeComponent();
            SeasonList.ItemsSource = _seasons;
            _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        }

        /// <summary>Reloads the list from disk and returns to the list view. Called each time the panel is opened.</summary>
        public void Reload()
        {
            _seasons.Clear();
            foreach (var s in SeasonGroupView.Build(_store.LoadAll())) _seasons.Add(s);
            int weekends = _seasons.Sum(s => s.Weekends.Count);
            CountText.Text = weekends == 1 ? "1 race" : $"{weekends} races";
            EmptyState.Visibility = weekends == 0 ? Visibility.Visible : Visibility.Collapsed;
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            ShowList();
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
            DCountry.Visibility = v.HasCountry ? Visibility.Visible : Visibility.Collapsed;
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
