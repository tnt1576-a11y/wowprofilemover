using System.IO;
using System.Text.Json;

namespace WowProfileMover.App;

public sealed class AppSettings
{
    public string LastInstallation { get; set; } = "";
    public string BackupRoot { get; set; } = "";
    public static AppSettings Load(string dataRoot)
    {
        try
        {
            var file = Path.Combine(dataRoot, "settings.json");
            var settings = File.Exists(file) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) ?? new() : new();
            if (string.IsNullOrWhiteSpace(settings.BackupRoot)) settings.BackupRoot = Path.Combine(dataRoot, "Backups");
            return settings;
        }
        catch { return new AppSettings { BackupRoot = Path.Combine(dataRoot, "Backups") }; }
    }
    public void Save(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        var path = Path.Combine(dataRoot, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
