using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SparkStudio.Infrastructure;

/// <summary>Credentials used for one server URL.</summary>
public sealed class ConnectionProfile
{
    /// <summary>Device ID sent to /auth/verify and in the TCP hello; must match the token's subject.</summary>
    public string DeviceId { get; set; } = Settings.DefaultDeviceId;

    /// <summary>Device token encrypted with Windows DPAPI (current user), base64. Null for open-mode servers.</summary>
    public string? ProtectedToken { get; set; }

    [JsonIgnore]
    public bool HasToken => !string.IsNullOrEmpty(ProtectedToken);

    public string? GetToken()
    {
        if (string.IsNullOrEmpty(ProtectedToken)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception) { return null; } // e.g. settings copied from another Windows account
    }

    public void SetToken(string? token)
    {
        ProtectedToken = string.IsNullOrWhiteSpace(token)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), Entropy, DataProtectionScope.CurrentUser));
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SparkStudio.DeviceToken.v1");
}

public sealed class Settings
{
    public static string DefaultDeviceId { get; } = MakeDefaultDeviceId();

    public string ServerUrl { get; set; } = "http://192.168.4.20:47683";
    public int NotificationPort { get; set; } = 47684;
    public bool AutoSave { get; set; } = true;
    public string? LastProjectId { get; set; }

    /// <summary>How board cards are ordered within columns: Created, Name or Severity.</summary>
    public string BoardSort { get; set; } = "Created";
    public bool BoardSortDescending { get; set; }

    /// <summary>The links panel (right of the note) is minimised to a thin strip.</summary>
    public bool LinksPanelCollapsed { get; set; }
    /// <summary>Width of the links panel when shown, in pixels.</summary>
    public double LinksPanelWidth { get; set; } = 250;

    /// <summary>Per-server credentials, keyed by normalised URL.</summary>
    public Dictionary<string, ConnectionProfile> Connections { get; set; } = new();

    /// <summary>Legacy plaintext token from earlier versions; migrated into <see cref="Connections"/> on load.</summary>
    [JsonPropertyName("Token")]
    public string? LegacyToken { get; set; }

    public static string NormaliseUrl(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();

    public ConnectionProfile ProfileFor(string url) =>
        Connections.TryGetValue(NormaliseUrl(url), out var p) ? p : new ConnectionProfile();

    public void SaveProfile(string url, ConnectionProfile profile)
    {
        Connections[NormaliseUrl(url)] = profile;
        Save();
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SparkStudio", "settings.json");

    public static Settings Load()
    {
        Settings s;
        try
        {
            s = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings()
                : new Settings();
        }
        catch (Exception) { s = new Settings(); }

        if (!string.IsNullOrWhiteSpace(s.LegacyToken))
        {
            var profile = s.ProfileFor(s.ServerUrl);
            profile.SetToken(s.LegacyToken);
            s.Connections[NormaliseUrl(s.ServerUrl)] = profile;
            s.LegacyToken = null;
            s.Save();
        }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, options));
        }
        catch (Exception) { }
    }

    private static string MakeDefaultDeviceId()
    {
        var machine = Regex.Replace(Environment.MachineName.ToLowerInvariant(), "[^a-z0-9_-]", "-");
        var id = "spark-studio-" + machine;
        return id.Length > 64 ? id[..64] : id;
    }

    public static bool IsValidDeviceId(string id) => Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,64}$");
}
