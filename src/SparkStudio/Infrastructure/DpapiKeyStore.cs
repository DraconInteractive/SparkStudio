using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spark.Notes.Crypto;

namespace SparkStudio.Infrastructure;

/// <summary>
/// Remembered project keys, each encrypted with Windows DPAPI for the current Windows account, in
/// <c>%LOCALAPPDATA%\SparkStudio\keys.json</c> (key ID -> protected key). Another account, or a copy of the file on
/// another PC, can't decrypt them.
/// </summary>
public sealed class DpapiKeyStore : IKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SparkStudio.ProjectKey.v1");
    private readonly object _gate = new();
    private readonly string _path;

    public DpapiKeyStore(string? path = null) => _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SparkStudio", "keys.json");

    public byte[]? Load(string keyId)
    {
        lock (_gate)
        {
            if (!Read().TryGetValue(keyId, out var b64)) return null;
            try { return ProtectedData.Unprotect(Convert.FromBase64String(b64), Entropy, DataProtectionScope.CurrentUser); }
            catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
        }
    }

    public void Save(string keyId, byte[] key)
    {
        lock (_gate)
        {
            var all = Read();
            all[keyId] = Convert.ToBase64String(ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
            Write(all);
        }
    }

    public void Remove(string keyId)
    {
        lock (_gate)
        {
            var all = Read();
            if (all.Remove(keyId)) Write(all);
        }
    }

    private Dictionary<string, string> Read()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) ?? new()
                : new();
        }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    private void Write(Dictionary<string, string> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
