using System.IO;
using System.Text.Json;

namespace ForzaOverlay.Core.Configuration;

public static class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public static string GetConfigFilePath()
    {
        return Path.Combine(GetExecutableDirectory(), "config", "config.json");
    }

    public static string GetExecutableDirectory()
    {
        string? exeDirectory = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(exeDirectory))
        {
            exeDirectory = AppContext.BaseDirectory;
        }

        return exeDirectory;
    }

    public static AppSettings Load()
    {
        try
        {
            string path = GetConfigFilePath();
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public static bool Save(AppSettings settings)
    {
        try
        {
            string path = GetConfigFilePath();
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(path, json);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
