using System.IO;
using System.Text.Json;

namespace SparkStudio.Infrastructure;

public sealed class Settings
{
    public string ServerUrl { get; set; } = "http://192.168.4.20:47683";
    public int NotificationPort { get; set; } = 47684;
    public string? Token { get; set; }
    public bool AutoSave { get; set; } = true;
    public string? LastProjectId { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SparkStudio", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception) { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { }
    }
}
