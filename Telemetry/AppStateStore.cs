using System;
using System.IO;
using System.Text.Json;

namespace F1RaceEngineer.Telemetry
{
    /// <summary>
    /// Remembers the app's between-launch state - the main window's placement and the UDP port -
    /// as a single JSON file under %LocalAppData%\F1RaceEngineer\ (same root as the race history
    /// and pit log, deliberately NOT the project folder, which may be cloud-synced).
    ///
    /// Replaces WindowStateStore/window.json. That type was named for window placement alone, so
    /// once the port needed persisting too the choice was a misnamed class or an honest rename;
    /// the rename won because the only cost was discarding saved window positions once, which the
    /// user explicitly accepted. No migration is attempted on purpose - reading the old file would
    /// be fifteen lines of code carried forever to save one window reposition.
    ///
    /// All IO is best-effort: a corrupt or missing file just means "defaults", never a crash. The
    /// port matters more than the geometry here - a dashboard people place once and leave can
    /// afford to forget where it was, but silently forgetting a changed port would look like the
    /// app was broken.
    /// </summary>
    public class AppStateStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "F1RaceEngineer", "settings.json");

        public const int DefaultPort = 20777;

        public class AppState
        {
            // Null when the window has never been placed, so a first run opens at the XAML size
            // instead of a zero-sized rect.
            public WindowPlacement? Window { get; set; }

            // The UDP port the app listens on. Persisted because it's now editable in Settings:
            // without this, changing it would work for one session and silently revert on the
            // next launch, which reads as the app being broken rather than forgetful.
            public int Port { get; set; } = DefaultPort;
        }

        public class WindowPlacement
        {
            public double Left { get; set; }
            public double Top { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public bool Maximized { get; set; }
        }

        /// <summary>Always returns usable state - defaults when there's nothing readable on disk.</summary>
        public AppState Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new AppState();
                var loaded = JsonSerializer.Deserialize<AppState>(File.ReadAllText(FilePath));
                if (loaded == null) return new AppState();
                // A port of 0 (or a nonsense one) would fail to bind on every launch with no way
                // for the user to tell why, so fall back rather than trust the file blindly.
                if (loaded.Port is < 1 or > 65535) loaded.Port = DefaultPort;
                return loaded;
            }
            catch { return new AppState(); }
        }

        public void Save(AppState state)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* best effort - a dashboard must never fail to close over remembering settings */ }
        }
    }
}
