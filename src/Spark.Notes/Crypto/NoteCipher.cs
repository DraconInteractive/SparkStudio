using System.Security.Cryptography;
using System.Text;

namespace Spark.Notes.Crypto;

/// <summary>Thrown when encrypted content is read or written while its project's key isn't unlocked.</summary>
public sealed class VaultLockedException : Exception
{
    public VaultLockedException(string keyId)
        : base("This note is encrypted and its project is locked. Unlock the project to read or edit it.") => KeyId = keyId;

    public string KeyId { get; }
}

/// <summary>
/// The format of an encrypted note: AES-256-GCM, armoured as text because the server only accepts UTF-8 text
/// for ETag-guarded saves.
/// <code>
/// -----BEGIN SPARK ENCRYPTED NOTE-----
/// Version: 1
/// Key: 9f2c…(key ID, 32 hex digits)
///
/// Base64( nonce[12] | ciphertext | tag[16] ), 64 characters per line
/// -----END SPARK ENCRYPTED NOTE-----
/// </code>
/// The ciphertext is the note's UTF-8 text. The associated data <c>"spark-note/1/" + keyId</c> binds the version and
/// key ID, so neither can be altered without decryption failing. A fresh random nonce is used for every encryption,
/// so the same text encrypts differently each time: compare decrypted text, never the stored form.
/// </summary>
public static class NoteCipher
{
    public const string Begin = "-----BEGIN SPARK ENCRYPTED NOTE-----";
    public const string End = "-----END SPARK ENCRYPTED NOTE-----";
    public const int KeySize = 32, NonceSize = 12, TagSize = 16;

    public static bool IsEncrypted(string text) => text.TrimStart('﻿', ' ', '\t', '\r', '\n').StartsWith(Begin, StringComparison.Ordinal);

    /// <summary>The key ID an encrypted note names, or null if the text isn't an encrypted note.</summary>
    public static string? KeyIdOf(string text) => IsEncrypted(text) ? Parse(text).KeyId : null;

    public static string Encrypt(string plain, byte[] key, string keyId)
    {
        if (key.Length != KeySize) throw new ArgumentException("Keys are 256-bit.", nameof(key));
        var data = Encoding.UTF8.GetBytes(plain);
        var blob = new byte[NonceSize + data.Length + TagSize];
        var nonce = blob.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var gcm = new AesGcm(key, TagSize))
            gcm.Encrypt(nonce, data, blob.AsSpan(NonceSize, data.Length), blob.AsSpan(NonceSize + data.Length), Aad(keyId));
        CryptographicOperations.ZeroMemory(data);

        var sb = new StringBuilder(Begin).Append('\n')
            .Append("Version: 1\n")
            .Append("Key: ").Append(keyId).Append("\n\n");
        var b64 = Convert.ToBase64String(blob);
        for (int i = 0; i < b64.Length; i += 64) sb.Append(b64, i, Math.Min(64, b64.Length - i)).Append('\n');
        return sb.Append(End).Append('\n').ToString();
    }

    /// <summary>Decrypts an armoured note. Throws <see cref="CryptographicException"/> if it was altered or the key is wrong.</summary>
    public static string Decrypt(string armoured, byte[] key)
    {
        var (keyId, blob) = Parse(armoured);
        if (blob.Length < NonceSize + TagSize) throw new CryptographicException("The encrypted note is truncated.");
        var plain = new byte[blob.Length - NonceSize - TagSize];
        using (var gcm = new AesGcm(key, TagSize))
            gcm.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize, plain.Length), blob.AsSpan(NonceSize + plain.Length), plain, Aad(keyId));
        var text = Encoding.UTF8.GetString(plain);
        CryptographicOperations.ZeroMemory(plain);
        return text;
    }

    private static byte[] Aad(string keyId) => Encoding.UTF8.GetBytes("spark-note/1/" + keyId);

    private static (string KeyId, byte[] Blob) Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Trim('﻿', ' ', '\t', '\r', '\n').Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != Begin || lines[^1].Trim() != End)
            throw new CryptographicException("Not an encrypted SPARK note.");
        string? version = null, keyId = null;
        int i = 1;
        for (; i < lines.Length - 1 && lines[i].Trim().Length > 0; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon < 0) break;
            var name = lines[i][..colon].Trim();
            var value = lines[i][(colon + 1)..].Trim();
            if (name.Equals("Version", StringComparison.OrdinalIgnoreCase)) version = value;
            else if (name.Equals("Key", StringComparison.OrdinalIgnoreCase)) keyId = value.ToLowerInvariant();
        }
        if (version != "1") throw new CryptographicException($"Unsupported encrypted note version \"{version}\".");
        if (keyId == null || keyId.Length != 32 || !keyId.All(Uri.IsHexDigit)) throw new CryptographicException("The encrypted note has no valid key ID.");
        var b64 = string.Concat(lines[i..^1].Select(l => l.Trim()));
        try { return (keyId, Convert.FromBase64String(b64)); }
        catch (FormatException) { throw new CryptographicException("The encrypted note is damaged."); }
    }
}
