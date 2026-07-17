using System;
using System.IO;
using System.Text.Json;

namespace F1RaceEngineer.Telemetry
{
    /// <summary>
    /// Remembers the main window's size, position and maximized state between launches, as a
    /// single JSON file under %LocalAppData%\F1RaceEngineer\ (same root as the race history and
    /// pit log, deliberately NOT the project's OneDrive folder). As with the history store, all
    /// IO is best-effort: a corrupt or missing file just means "open at the default size", never
    /// a crash. This is a second-screen dashboard people place once and leave, so persisting the
    /// placement removes a per-launch chore.
    /// </summary>
    public class WindowStateStore
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "F1RaceEngineer", "window.json");

        public class WindowPlacement
        {
            public double Left { get; set; }
            public double Top { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public bool Maximized { get; set; }
        }

        public void Save(WindowPlacement placement)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(placement));
            }
            catch { /* best effort - a dashboard must never fail to close over remembering geometry */ }
        }

        /// <summary>Returns the saved placement, or null if there's nothing usable on disk.</summary>
        public WindowPlacement? Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                return JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(FilePath));
            }
            catch { return null; }
        }
    }
}
