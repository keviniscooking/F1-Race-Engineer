using System;
using System.Reflection;
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

            // InformationalVersion (not AssemblyVersion) preserves the exact <Version>
            // string from the csproj (e.g. "1.0.0") instead of a zero-padded 4-part
            // AssemblyVersion (e.g. "1.0.0.0"). The SDK appends "+{git commit sha}" to
            // it automatically for traceable builds - truncate at '+' since that's far
            // more detail than belongs in a small settings-panel label.
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var plainVersion = version?.Split('+')[0];
            VersionText.Text = string.IsNullOrEmpty(plainVersion) ? "" : $"v{plainVersion}";
        }

        private void PreviewPractice_Click(object sender, System.Windows.RoutedEventArgs e) => PreviewPresetRequested?.Invoke(PresetType.Practice);
        private void PreviewQualifying_Click(object sender, System.Windows.RoutedEventArgs e) => PreviewPresetRequested?.Invoke(PresetType.Qualifying);
        private void PreviewRace_Click(object sender, System.Windows.RoutedEventArgs e) => PreviewPresetRequested?.Invoke(PresetType.Race);
    }
}
