using System.Text.Json;
using AVRDUDEPROG2.Models;

namespace AVRDUDEPROG2.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AVRDUDEPROG2",
        "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
                if (settings.SchemaVersion < 2)
                {
                    settings.SchemaVersion = 2;
                    settings.WindowWidth = 1060;
                    settings.WindowHeight = 700;
                }
                return settings;
            }
        }
        catch
        {
            // A damaged preferences file must never prevent programming access.
        }

        return new AppSettings { SchemaVersion = 2 };
    }

    public void Save(AppSettings settings)
    {
        settings.SchemaVersion = 2;
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, SettingsPath, true);
    }
}
