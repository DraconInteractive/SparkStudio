using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spark.Notes.Crypto;

/// <summary>Thrown when a passphrase or recovery key doesn't open a vault.</summary>
public sealed class WrongPassphraseException : Exception
{
    public WrongPassphraseException(string message) : base(message) { }
}

/// <summary>A key wrapped (encrypted) with AES-256-GCM: Base64 nonce, and Base64 ciphertext followed by the tag.</summary>
public sealed record WrappedKey(string Nonce, string Ciphertext);

public sealed record KdfParams(string Name, int Iterations, string Salt);

/// <summary>
/// <c>.spark-vault.json</c>, stored in an encrypted project's folder on the server. It holds the project's random
/// 256-bit data key twice, never in the clear: once wrapped with a key derived from the passphrase
/// (PBKDF2-HMAC-SHA256), once wrapped with a key derived from the recovery key (HKDF-SHA256). Reading it reveals
/// nothing without one of those. Any device with the passphrase can open the project.
/// </summary>
public sealed record VaultFile
{
    public string Format { get; init; } = ProjectVault.FormatName;
    public int Version { get; init; } = 1;
    /// <summary>Random ID naming the data key; encrypted notes carry it so the right key can be found.</summary>
    public string KeyId { get; init; } = "";
    public KdfParams Kdf { get; init; } = new("pbkdf2-sha256", ProjectVault.DefaultIterations, "");
    public WrappedKey Passphrase { get; init; } = new("", "");
    public WrappedKey? Recovery { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public static class ProjectVault
{
    public const string FileName = ".spark-vault.json";
    public const string FormatName = "spark-vault";
    /// <summary>OWASP's 2023 recommendation for PBKDF2-HMAC-SHA256.</summary>
    public const int DefaultIterations = 600_000;
    public const int MinPassphraseLength = 8;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool IsVaultFile(Spark.Api.SparkFile f) => f.Name.Equals(FileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>A new vault with a fresh data key. Returns the key (to unlock straight away) and the recovery key to show once.</summary>
    public static (VaultFile Vault, byte[] Key, string RecoveryCode) Create(string passphrase, int iterations = DefaultIterations)
    {
        var key = RandomNumberGenerator.GetBytes(NoteCipher.KeySize);
        var keyId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var salt = RandomNumberGenerator.GetBytes(16);
        var recovery = RandomNumberGenerator.GetBytes(20);

        var kek = PassphraseKey(passphrase, salt, iterations);
        var rek = RecoveryKey(recovery, keyId);
        var vault = new VaultFile
        {
            KeyId = keyId,
            Kdf = new KdfParams("pbkdf2-sha256", iterations, Convert.ToBase64String(salt)),
            Passphrase = Wrap(kek, key, keyId),
            Recovery = Wrap(rek, key, keyId),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        CryptographicOperations.ZeroMemory(kek);
        CryptographicOperations.ZeroMemory(rek);
        return (vault, key, FormatRecoveryCode(recovery));
    }

    /// <summary>The project's data key. Takes about half a second (deliberately slow key derivation).</summary>
    public static byte[] Unlock(VaultFile vault, string passphrase)
    {
        Check(vault);
        if (vault.Kdf.Name != "pbkdf2-sha256") throw new NotSupportedException($"Unknown key derivation \"{vault.Kdf.Name}\".");
        var kek = PassphraseKey(passphrase, Convert.FromBase64String(vault.Kdf.Salt), vault.Kdf.Iterations);
        try { return Unwrap(kek, vault.Passphrase, vault.KeyId) ?? throw new WrongPassphraseException("That passphrase doesn't unlock this project."); }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    public static byte[] UnlockWithRecovery(VaultFile vault, string code)
    {
        Check(vault);
        if (vault.Recovery == null) throw new WrongPassphraseException("This project has no recovery key.");
        var bytes = ParseRecoveryCode(code) ?? throw new WrongPassphraseException("That isn't a valid recovery key.");
        var rek = RecoveryKey(bytes, vault.KeyId);
        try { return Unwrap(rek, vault.Recovery, vault.KeyId) ?? throw new WrongPassphraseException("That recovery key doesn't unlock this project."); }
        finally { CryptographicOperations.ZeroMemory(rek); }
    }

    /// <summary>The same vault with the data key wrapped under a new passphrase (notes don't need re-encrypting).</summary>
    public static VaultFile ChangePassphrase(VaultFile vault, byte[] key, string newPassphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var kek = PassphraseKey(newPassphrase, salt, vault.Kdf.Iterations);
        try
        {
            return vault with
            {
                Kdf = new KdfParams("pbkdf2-sha256", vault.Kdf.Iterations, Convert.ToBase64String(salt)),
                Passphrase = Wrap(kek, key, vault.KeyId),
            };
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    public static string Serialize(VaultFile vault) => JsonSerializer.Serialize(vault, Json) + "\n";

    /// <summary>Parses a vault file; null if the text isn't one.</summary>
    public static VaultFile? Parse(string json)
    {
        try
        {
            var v = JsonSerializer.Deserialize<VaultFile>(json, Json);
            return v is { Format: FormatName, Version: 1 } && v.KeyId.Length == 32 ? v : null;
        }
        catch (JsonException) { return null; }
    }

    private static void Check(VaultFile vault)
    {
        if (vault.Format != FormatName || vault.Version != 1) throw new NotSupportedException("Unsupported vault version.");
    }

    private static byte[] PassphraseKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC)), salt, iterations,
            HashAlgorithmName.SHA256, NoteCipher.KeySize);

    private static byte[] RecoveryKey(byte[] recovery, string keyId) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, recovery, NoteCipher.KeySize, Encoding.UTF8.GetBytes(keyId),
            Encoding.UTF8.GetBytes("spark-vault/recovery/1"));

    private static byte[] Aad(string keyId) => Encoding.UTF8.GetBytes("spark-vault/1/" + keyId);

    private static WrappedKey Wrap(byte[] kek, byte[] key, string keyId)
    {
        var nonce = RandomNumberGenerator.GetBytes(NoteCipher.NonceSize);
        var output = new byte[key.Length + NoteCipher.TagSize];
        using var gcm = new AesGcm(kek, NoteCipher.TagSize);
        gcm.Encrypt(nonce, key, output.AsSpan(0, key.Length), output.AsSpan(key.Length), Aad(keyId));
        return new WrappedKey(Convert.ToBase64String(nonce), Convert.ToBase64String(output));
    }

    private static byte[]? Unwrap(byte[] kek, WrappedKey wrapped, string keyId)
    {
        try
        {
            var input = Convert.FromBase64String(wrapped.Ciphertext);
            var key = new byte[input.Length - NoteCipher.TagSize];
            using var gcm = new AesGcm(kek, NoteCipher.TagSize);
            gcm.Decrypt(Convert.FromBase64String(wrapped.Nonce), input.AsSpan(0, key.Length), input.AsSpan(key.Length), key, Aad(keyId));
            return key;
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException) { return null; }
    }

    // ---------- Recovery codes: 160 random bits as 32 Crockford Base32 characters, "ABCD-EFGH-…" ----------

    private const string Base32 = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string FormatRecoveryCode(byte[] bytes)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Base32[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Base32[(buffer << (5 - bits)) & 31]);
        return string.Join('-', Enumerable.Range(0, (sb.Length + 3) / 4).Select(i => sb.ToString(i * 4, Math.Min(4, sb.Length - i * 4))));
    }

    /// <summary>Parses a recovery code, forgiving case, spaces, dashes and the usual look-alikes (O/0, I/L/1).</summary>
    public static byte[]? ParseRecoveryCode(string code)
    {
        var chars = code.ToUpperInvariant().Where(c => !char.IsWhiteSpace(c) && c != '-')
            .Select(c => c switch { 'O' => '0', 'I' or 'L' => '1', _ => c }).ToList();
        if (chars.Count != 32) return null;
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in chars)
        {
            var v = Base32.IndexOf(c);
            if (v < 0) return null;
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return bytes.Count == 20 ? bytes.ToArray() : null;
    }
}
