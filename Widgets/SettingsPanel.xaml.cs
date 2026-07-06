using System;
using System.Windows.Controls;
using F1RaceEngineer.Telemetry;

namespace F1RaceEngineer.Widgets
{
    public partial class SettingsPanel : UserControl
    {
        /// <summary>Preview mode: forces a preset for layout review without a live game connection.</summary>
        public event Action<PresetType>? PreviewPresetRequested;

        public SettingsPanel()
        {
            InitializeComponent();
        }

        private void PreviewPractice_Click(object sender, System.Windows.RoutedEventArgs e) => PreviewPresetRequested?.Invoke(PresetType.Practice);
        private void PreviewQualifying_Click(object sender, System.Windows.RoutedEventArgs e) => PreviewPresetRequested?.Invoke(PresetType.Qualifying);
        private void PreviewRace_Click(object sender, System.Windows.RoutedEventArgs e) => PreviewPresetRequested?.Invoke(PresetType.Race);
    }
}
