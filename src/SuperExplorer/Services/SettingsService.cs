using System;
using System.IO;
using System.Text.Json;
using SuperExplorer.Models;

namespace SuperExplorer.Services
{
    public static class SettingsService
    {
        private static string _path;
        private static readonly object _lock = new object();

        public static AppSettings Current { get; private set; } = new AppSettings();

        private static string DataDir
        {
            get
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static void Load()
        {
            try
            {
                _path = Path.Combine(DataDir, "settings.json");
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    var s = JsonSerializer.Deserialize<AppSettings>(json);
                    if (s != null) Current = s;
                }
            }
            catch
            {
                Current = new AppSettings();
            }
        }

        public static void Save()
        {
            try
            {
                lock (_lock)
                {
                    _path = Path.Combine(DataDir, "settings.json");
                    var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_path, json);
                }
            }
            catch { }
        }
    }
}
