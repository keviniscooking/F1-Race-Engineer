namespace F1RaceEngineer.Models
{
    /// <summary>
    /// One row in the settings panel: a toggleable widget for the currently-active
    /// preset. IsEnabled changes drive MainWindow's adaptive grid re-layout.
    /// </summary>
    public class WidgetToggle : ObservableObject
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";

        private bool _isEnabled;
        public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    }
}
