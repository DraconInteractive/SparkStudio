using System.Security.Cryptography;
using Spark.Api;

namespace Spark.Notes.Crypto;

/// <summary>Keeps unlocked project keys on this device between sessions (implemented with DPAPI by the app).</summary>
public interface IKeyStore
{
    byte[]? Load(string keyId);
    void Save(string keyId, byte[] key);
    void Remove(string keyId);
}

/// <summary>
/// The encryption layer between the app and the server (<see cref="SparkClient.Codec"/>) and the offline copies.
/// <list type="bullet">
/// <item>Reading: text in the encrypted-note format is decrypted with the key it names; other text passes through.</item>
/// <item>Writing: text is encrypted when the file lies in a project with a vault (whatever its name) or was read
/// encrypted. Plain text never replaces an encrypted note, and a locked project refuses writes instead.</item>
/// </list>
/// </summary>
public sealed class Keyring : IContentCodec
{
    private readonly object _gate = new();
    private readonly IKeyStore? _store;
    private readonly Dictionary<string, byte[]> _keys = new();
    private readonly Dictionary<string, VaultFile> _vaults = new(StringComparer.Ordinal); // root directory -> vault
    private readonly Dictionary<string, string> _encryptedFiles = new();                  // file ID -> key ID

    public Keyring(IKeyStore? store = null) => _store = store;

    /// <summary>Raised when a vault is registered or a key is unlocked or locked.</summary>
    public event Action? Changed;

    // ---------- Vaults ----------

    /// <summary>Marks every file under <paramref name="rootDirectory"/> as belonging to <paramref name="vault"/>.</summary>
    public void Register(string rootDirectory, VaultFile vault)
    {
        lock (_gate)
        {
            if (_vaults.TryGetValue(rootDirectory, out var old) && old == vault) return;
            _vaults[rootDirectory] = vault;
        }
        Changed?.Invoke();
    }

    public void Unregister(string rootDirectory)
    {
        bool removed;
        lock (_gate) removed = _vaults.Remove(rootDirectory);
        if (removed) Changed?.Invoke();
    }

    /// <summary>The vault covering <paramref name="directory"/> (the innermost one if vaults nest).</summary>
    public VaultFile? VaultFor(string directory)
    {
        lock (_gate)
            return _vaults.Where(v => OfflineStore.IsUnder(directory, v.Key))
                .OrderByDescending(v => v.Key.Length).Select(v => v.Value).FirstOrDefault();
    }

    // ---------- Keys ----------

    public bool IsUnlocked(string keyId) => KeyFor(keyId) != null;

    /// <summary>True when the key was kept on this device (it unlocks without the passphrase next time).</summary>
    public bool IsRemembered(string keyId) => _store?.Load(keyId) is { } k && Zero(k);

    public void AddKey(string keyId, byte[] key, bool remember)
    {
        lock (_gate) _keys[keyId] = key.ToArray();
        if (remember) _store?.Save(keyId, key);
        else _store?.Remove(keyId);
        Changed?.Invoke();
    }

    /// <summary>Forgets a key, in memory and on this device. Its notes can't be read again until unlocked.</summary>
    public void Lock(string keyId)
    {
        lock (_gate)
        {
            if (_keys.Remove(keyId, out var k)) CryptographicOperations.ZeroMemory(k);
        }
        _store?.Remove(keyId);
        Changed?.Invoke();
    }

    private byte[]? KeyFor(string keyId)
    {
        lock (_gate)
        {
            if (_keys.TryGetValue(keyId, out var k)) return k;
            if (_store?.Load(keyId) is not { Length: NoteCipher.KeySize } stored) return null;
            _keys[keyId] = stored;
            return stored;
        }
    }

    /// <summary>True when the file's stored form is (or will be) encrypted: it lies in a vault, or was read encrypted.</summary>
    public bool IsEncrypted(SparkFile f) => KeyIdFor(f.Id, f.Directory) != null;

    private string? KeyIdFor(string? fileId, string? directory)
    {
        var keyId = directory != null ? VaultFor(directory)?.KeyId : null;
        if (keyId == null && fileId != null)
            lock (_gate) _encryptedFiles.TryGetValue(fileId, out keyId);
        return keyId;
    }

    /// <summary>True when the file's text was encrypted the last time it was read.</summary>
    public bool WasReadEncrypted(string fileId)
    {
        lock (_gate) return _encryptedFiles.ContainsKey(fileId);
    }

    /// <summary>The vault with its (unlocked) key wrapped under a new passphrase.</summary>
    public VaultFile ChangePassphrase(VaultFile vault, string newPassphrase) =>
        ProjectVault.ChangePassphrase(vault, KeyFor(vault.KeyId) ?? throw new VaultLockedException(vault.KeyId), newPassphrase);

    // ---------- IContentCodec ----------

    public string Decode(string fileId, string stored)
    {
        if (!NoteCipher.IsEncrypted(stored))
        {
            lock (_gate) _encryptedFiles.Remove(fileId);
            return stored;
        }
        var keyId = NoteCipher.KeyIdOf(stored)!;
        lock (_gate) _encryptedFiles[fileId] = keyId;
        var key = KeyFor(keyId) ?? throw new VaultLockedException(keyId);
        try { return NoteCipher.Decrypt(stored, key); }
        catch (CryptographicException e)
        {
            throw new CryptographicException("This note couldn't be decrypted: it was altered, damaged, or encrypted with another key.", e);
        }
    }

    public string Encode(string? fileId, string? directory, string text)
    {
        if (KeyIdFor(fileId, directory) is not { } keyId) return text;
        var key = KeyFor(keyId) ?? throw new VaultLockedException(keyId);
        return NoteCipher.Encrypt(text, key, keyId);
    }

    private static bool Zero(byte[] k)
    {
        CryptographicOperations.ZeroMemory(k);
        return true;
    }
}
