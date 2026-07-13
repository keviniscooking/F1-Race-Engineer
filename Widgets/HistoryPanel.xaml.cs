using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
        private static readonly FontFamily MonoFont = new("Consolas");
        private static readonly SolidColorBrush TickLine = SavedRaceView.BrushFromHex("#4A5460");
        private static readonly SolidColorBrush TickText = SavedRaceView.BrushFromHex("#6B7684");

        private readonly RaceHistoryStore _store = new();
        private readonly ObservableCollection<SavedRaceView> _races = new();
        private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };
        private SavedRaceView? _current;      // race open in the detail view
        private SavedRaceView? _pendingDelete;

        public HistoryPanel()
        {
            InitializeComponent();
            RaceList.ItemsSource = _races;
            _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        }

        /// <summary>Reloads the list from disk and returns to the list view. Called each time the panel is opened.</summary>
        public void Reload()
        {
            _races.Clear();
            foreach (var r in _store.LoadAll()) _races.Add(new SavedRaceView(r));
            CountText.Text = _races.Count == 1 ? "1 race" : $"{_races.Count} races";
            EmptyState.Visibility = _races.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ConfirmOverlay.Visibility = Visibility.Collapsed;
            ShowList();
        }

        private void ShowList()
        {
            _current = null;
            DetailRoot.Visibility = Visibility.Collapsed;
            ListRoot.Visibility = Visibility.Visible;
        }

        // ---- list ----
        private void Card_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is SavedRaceView v) OpenDetail(v);
        }

        private void CardStint_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Grid g && g.Tag is SavedRaceView v)
                BuildBar(g, v.StintSegments, showLetters: false, gap: 1.5);
        }

        // ---- detail ----
        private void OpenDetail(SavedRaceView v)
        {
            _current = v;
            DName.Text = v.GrandPrix;
            DCountry.Visibility = v.HasCountry ? Visibility.Visible : Visibility.Collapsed;
            DCountryText.Text = v.Country;
            DSub.Text = v.DetailSubtitle;
            DFinish.Text = v.FinishText;
            DFinish.Foreground = v.FinishBrush;
            DGrid.Text = v.GridText;
            DPoints.Text = v.PointsText;
            DnfStrip.Visibility = v.HasDnfDetail ? Visibility.Visible : Visibility.Collapsed;
            DnfText.Text = v.DnfDetail;
            ClassList.ItemsSource = v.Classification;
            LapList.ItemsSource = v.Laps;
            BuildBar(DetailStintBar, v.StintSegments, showLetters: true, gap: 3);
            BuildTicks(DetailStintTicks, v);

            ListRoot.Visibility = Visibility.Collapsed;
            DetailRoot.Visibility = Visibility.Visible;
        }

        private void Back_Click(object sender, RoutedEventArgs e) => ShowList();

        /// <summary>Builds a proportional tyre-stint bar into <paramref name="host"/> (star columns weighted by lap count).</summary>
        private static void BuildBar(Grid host, IReadOnlyList<TyreStintSegment> segs, bool showLetters, double gap)
        {
            host.ColumnDefinitions.Clear();
            host.Children.Clear();
            for (int i = 0; i < segs.Count; i++)
                host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(segs[i].LapCount, GridUnitType.Star) });

            for (int i = 0; i < segs.Count; i++)
            {
                var seg = segs[i];
                var border = new Border
                {
                    Background = seg.Brush,
                    CornerRadius = new CornerRadius(showLetters ? 4 : 2),
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
                host.Children.Add(border);
            }
        }

        // A lap-number tick at each stint boundary (the lap a pit happened), aligned to the
        // boundary by reusing the bar's own star columns and right-aligning within each stint
        // except the last (whose end is the flag, not a pit).
        private static void BuildTicks(Grid host, SavedRaceView v)
        {
            host.ColumnDefinitions.Clear();
            host.Children.Clear();
            var segs = v.StintSegments;
            var stints = v.Source.PlayerStints;
            if (segs.Count < 2) return;

            for (int i = 0; i < segs.Count; i++)
                host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(segs[i].LapCount, GridUnitType.Star) });

            for (int i = 0; i < segs.Count - 1; i++)
            {
                var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
                panel.Children.Add(new Border { Width = 1, Height = 5, Background = TickLine, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 2) });
                panel.Children.Add(new TextBlock { Text = $"L{stints[i].EndLap}", FontSize = 9, FontFamily = MonoFont, Foreground = TickText, HorizontalAlignment = HorizontalAlignment.Right });
                Grid.SetColumn(panel, i);
                host.Children.Add(panel);
            }
        }

        // ---- export ----
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true; // don't also open the card
            if ((sender as FrameworkElement)?.Tag is SavedRaceView v) ExportRace(v);
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
                File.WriteAllText(dlg.FileName, RaceHtmlExporter.Export(v.Source));
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
        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as FrameworkElement)?.Tag is SavedRaceView v) ShowDeleteConfirm(v);
        }

        private void DetailDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_current != null) ShowDeleteConfirm(_current);
        }

        private void ShowDeleteConfirm(SavedRaceView v)
        {
            _pendingDelete = v;
            ConfirmText.Text = $"Delete “{v.GrandPrix}” from your race history?";
            ConfirmOverlay.Visibility = Visibility.Visible;
        }

        private void ConfirmCancel_Click(object sender, RoutedEventArgs e) => ConfirmOverlay.Visibility = Visibility.Collapsed;

        private void ConfirmDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingDelete == null) return;
            string gp = _pendingDelete.GrandPrix;
            _store.Delete(_pendingDelete.Source);
            _pendingDelete = null;
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
