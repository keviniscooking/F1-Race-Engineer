using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using F1RaceEngineer.Models;

namespace F1RaceEngineer.Telemetry
{
    /// <summary>
    /// Persists completed races as one JSON file each, under
    /// %LocalAppData%\F1RaceEngineer\history\ (same root as the pit log; deliberately NOT
    /// under the project folder, which may sit in a cloud-synced directory - writing races
    /// there would trigger sync churn on every save). One file
    /// per race keeps delete trivial (delete the file) and export self-contained. All IO is
    /// defensive - a history feature must never take the app down, so failures degrade to
    /// "no history" rather than throwing.
    /// </summary>
    public class RaceHistoryStore
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "F1RaceEngineer", "history");

        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        /// <summary>
        /// Writes the race, unless one with the same SessionUid already exists (the game
        /// re-sends Final Classification repeatedly at session end). Returns true only when a
        /// new file was actually written.
        /// </summary>
        public bool Save(SavedRace race)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                if (Directory.EnumerateFiles(Dir, $"*_{race.SessionUid}.json").Any())
                    return false; // already saved this session

                // Date-prefixed name sorts chronologically on disk; the uid suffix makes the
                // dedupe check above a cheap filename glob.
                string name = $"{race.SavedAtUtc:yyyyMMddHHmmss}_{race.SessionUid}.json";
                File.WriteAllText(Path.Combine(Dir, name), JsonSerializer.Serialize(race, JsonOpts));
                return true;
            }
            catch { return false; }
        }

        /// <summary>Loads every saved race, newest first. Corrupt files are skipped, not fatal.</summary>
        public List<SavedRace> LoadAll()
        {
            var list = new List<SavedRace>();
            try
            {
                if (!Directory.Exists(Dir)) return list;
                foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
                {
                    try
                    {
                        var race = JsonSerializer.Deserialize<SavedRace>(File.ReadAllText(file));
                        if (race != null) { race.FilePath = file; list.Add(race); }
                    }
                    catch { /* skip a single corrupt file */ }
                }
            }
            catch { /* directory unreadable - return whatever we have */ }
            return list.OrderByDescending(r => r.SavedAtUtc).ToList();
        }

        public void Delete(SavedRace race)
        {
            try
            {
                if (!string.IsNullOrEmpty(race.FilePath) && File.Exists(race.FilePath))
                    File.Delete(race.FilePath);
            }
            catch { /* best effort */ }
        }
    }
}
