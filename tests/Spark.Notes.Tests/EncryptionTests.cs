using System.IO;
using System.Security.Cryptography;
using Spark.Api;
using Spark.Notes.Crypto;

namespace Spark.Notes.Tests;

public sealed class EncryptionTests : IDisposable
{
    private const int FastIterations = 1000; // the real default (600k) is deliberately slow
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private const string KeyId = "0123456789abcdef0123456789abcdef";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spark-crypto-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class MemoryKeyStore : IKeyStore
    {
        public readonly Dictionary<string, byte[]> Keys = new();
        public byte[]? Load(string keyId) => Keys.TryGetValue(keyId, out var k) ? k.ToArray() : null;
        public void Save(string keyId, byte[] key) => Keys[keyId] = key.ToArray();
        public void Remove(string keyId) => Keys.Remove(keyId);
    }

    private static SparkFile F(string id, string dir, string name = "Note.md.enc") => new() { Id = id, Directory = dir, Name = name };

    // ---------- Note format ----------

    [Theory]
    [InlineData("")]
    [InlineData("# Plan\n\nSecret [[Link]] ünïcödé 🔒\r\nline")]
    public void Notes_round_trip_and_the_stored_form_is_text_without_the_plaintext(string plain)
    {
        var stored = NoteCipher.Encrypt(plain, Key, KeyId);

        Assert.True(NoteCipher.IsEncrypted(stored));
        Assert.Equal(KeyId, NoteCipher.KeyIdOf(stored));
        Assert.StartsWith(NoteCipher.Begin + "\n", stored);
        Assert.DoesNotContain("Secret", stored);
        Assert.All(stored, c => Assert.True(c == '\n' || (c >= ' ' && c < 127), $"control/non-ASCII char {(int)c}")); // server: UTF-8 text only
        Assert.Equal(plain, NoteCipher.Decrypt(stored, Key));
        Assert.Equal(plain, NoteCipher.Decrypt(stored.Replace("\n", "\r\n"), Key)); // survives CRLF conversion
    }

    [Fact]
    public void The_same_text_encrypts_differently_each_time()
    {
        Assert.NotEqual(NoteCipher.Encrypt("same", Key, KeyId), NoteCipher.Encrypt("same", Key, KeyId));
    }

    [Fact]
    public void Tampering_a_wrong_key_or_a_swapped_key_id_fail_to_decrypt()
    {
        var stored = NoteCipher.Encrypt("pay Alice $10", Key, KeyId);
        var lines = stored.Split('\n');
        var body = lines[4].ToCharArray();
        body[20] = body[20] == 'A' ? 'B' : 'A';
        lines[4] = new string(body);

        Assert.ThrowsAny<CryptographicException>(() => NoteCipher.Decrypt(string.Join('\n', lines), Key));
        Assert.ThrowsAny<CryptographicException>(() => NoteCipher.Decrypt(stored, RandomNumberGenerator.GetBytes(32)));
        Assert.ThrowsAny<CryptographicException>(() => NoteCipher.Decrypt(stored.Replace(KeyId, "f" + KeyId[1..]), Key));
    }

    [Fact]
    public void Plain_notes_are_not_mistaken_for_encrypted_ones()
    {
        Assert.False(NoteCipher.IsEncrypted("# Title\nBEGIN SPARK ENCRYPTED NOTE"));
        Assert.Null(NoteCipher.KeyIdOf("hello"));
    }

    // ---------- Vault ----------

    [Fact]
    public void A_vault_unlocks_with_its_passphrase_or_recovery_key_only()
    {
        var (vault, key, code) = ProjectVault.Create("correct horse battery", FastIterations);
        var parsed = ProjectVault.Parse(ProjectVault.Serialize(vault))!;

        Assert.Equal(key, ProjectVault.Unlock(parsed, "correct horse battery"));
        Assert.Equal(key, ProjectVault.UnlockWithRecovery(parsed, code));
        Assert.Equal(key, ProjectVault.UnlockWithRecovery(parsed, code.ToLowerInvariant().Replace("-", " ").Replace('0', 'o')));
        Assert.Throws<WrongPassphraseException>(() => ProjectVault.Unlock(parsed, "correct horse batterY"));
        Assert.Throws<WrongPassphraseException>(() => ProjectVault.UnlockWithRecovery(parsed, "0000-0000-0000-0000-0000-0000-0000-0000"));
        Assert.Throws<WrongPassphraseException>(() => ProjectVault.UnlockWithRecovery(parsed, "too short"));
        Assert.Matches("^([0-9A-Z]{4}-){7}[0-9A-Z]{4}$", code);
        Assert.DoesNotContain(Convert.ToBase64String(key), ProjectVault.Serialize(vault));
    }

    [Fact]
    public void Changing_the_passphrase_keeps_the_key_and_the_recovery_key()
    {
        var (vault, key, code) = ProjectVault.Create("old passphrase", FastIterations);
        var changed = ProjectVault.ChangePassphrase(vault, key, "new passphrase");

        Assert.Equal(key, ProjectVault.Unlock(changed, "new passphrase"));
        Assert.Throws<WrongPassphraseException>(() => ProjectVault.Unlock(changed, "old passphrase"));
        Assert.Equal(key, ProjectVault.UnlockWithRecovery(changed, code));
        Assert.Equal(vault.KeyId, changed.KeyId);
    }

    [Fact]
    public void Recovery_codes_round_trip()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        Assert.Equal(bytes, ProjectVault.ParseRecoveryCode(ProjectVault.FormatRecoveryCode(bytes)));
        Assert.Null(ProjectVault.ParseRecoveryCode("UUUU-UUUU-UUUU-UUUU-UUUU-UUUU-UUUU-UUUU")); // U isn't Crockford
    }

    [Fact]
    public void Parse_rejects_anything_that_is_not_a_vault()
    {
        Assert.Null(ProjectVault.Parse("{}"));
        Assert.Null(ProjectVault.Parse("not json"));
        Assert.Null(ProjectVault.Parse("{\"format\":\"spark-vault\",\"version\":2,\"key_id\":\"0123456789abcdef0123456789abcdef\"}"));
    }

    // ---------- Keyring (the client's codec) ----------

    private static (Keyring Ring, VaultFile Vault, byte[] Key, MemoryKeyStore Store) NewRing(bool unlocked = true)
    {
        var store = new MemoryKeyStore();
        var ring = new Keyring(store);
        var (vault, key, _) = ProjectVault.Create("passphrase", FastIterations);
        ring.Register("/projects/Secret", vault);
        if (unlocked) ring.AddKey(vault.KeyId, key, remember: false);
        return (ring, vault, key, store);
    }

    [Fact]
    public void Writes_in_a_vault_folder_are_encrypted_and_reads_decrypted()
    {
        var (ring, vault, key, _) = NewRing();

        var stored = ring.Encode(null, "/projects/Secret/Sub", "# Plan");
        Assert.Equal(vault.KeyId, NoteCipher.KeyIdOf(stored));
        Assert.Equal("# Plan", ring.Decode("f1", stored));
        Assert.Equal("# Plan", NoteCipher.Decrypt(stored, key));

        Assert.Equal("open", ring.Encode(null, "/projects/Other", "open"));          // other projects untouched
        Assert.Equal("open", ring.Encode(null, "/projects/SecretSauce", "open"));    // prefix of a name isn't inside
        Assert.Equal("plain", ring.Decode("f2", "plain"));                           // plain text passes through
        Assert.True(ring.IsEncrypted(F("x", "/projects/Secret")));
        Assert.False(ring.IsEncrypted(F("y", "/projects/Other")));
    }

    [Fact]
    public void A_file_read_encrypted_stays_encrypted_even_when_saved_by_id_alone()
    {
        var (ring, vault, key, _) = NewRing();
        var stored = NoteCipher.Encrypt("secret", key, vault.KeyId);
        var other = new Keyring();
        other.AddKey(vault.KeyId, key, remember: false); // knows the key but not the vault

        Assert.Equal("secret", other.Decode("f1", stored));
        Assert.True(other.WasReadEncrypted("f1"));
        Assert.True(NoteCipher.IsEncrypted(other.Encode("f1", null, "edited")));
        Assert.True(NoteCipher.IsEncrypted(other.Encode("f1", "/elsewhere", "edited")));

        other.Decode("f1", "now plain on the server");
        Assert.False(other.WasReadEncrypted("f1"));
    }

    [Fact]
    public void A_locked_project_refuses_reads_and_writes_instead_of_falling_back_to_plain_text()
    {
        var (ring, vault, key, _) = NewRing(unlocked: false);
        var stored = NoteCipher.Encrypt("secret", key, vault.KeyId);

        Assert.False(ring.IsUnlocked(vault.KeyId));
        Assert.Throws<VaultLockedException>(() => ring.Decode("f1", stored));
        Assert.Throws<VaultLockedException>(() => ring.Encode(null, "/projects/Secret", "new note"));
    }

    [Fact]
    public void Remembered_keys_unlock_a_new_session_and_locking_forgets_them()
    {
        var (ring, vault, key, store) = NewRing(unlocked: false);
        ring.AddKey(vault.KeyId, key, remember: true);
        Assert.True(ring.IsRemembered(vault.KeyId));

        var next = new Keyring(store);
        next.Register("/projects/Secret", vault);
        Assert.True(next.IsUnlocked(vault.KeyId));

        next.Lock(vault.KeyId);
        Assert.False(next.IsUnlocked(vault.KeyId));
        Assert.False(new Keyring(store).IsUnlocked(vault.KeyId));
    }

    [Fact]
    public void Unlocking_for_the_session_only_does_not_store_the_key()
    {
        var (ring, vault, _, store) = NewRing();
        Assert.True(ring.IsUnlocked(vault.KeyId));
        Assert.Empty(store.Keys);
    }

    // ---------- Offline copies ----------

    [Fact]
    public void Offline_copies_of_encrypted_notes_are_encrypted_on_disk()
    {
        var (ring, vault, _, _) = NewRing();
        var store = new OfflineStore(_dir) { Codec = ring };
        store.SaveContent(F("a", "/projects/Secret"), "# Secret plan", "\"a-1\"");
        store.SaveContent(F("b", "/projects/Open", "Open.md"), "# Open", "\"b-1\"");

        var onDisk = File.ReadAllText(Path.Combine(_dir, "content", "a.txt"));
        Assert.True(NoteCipher.IsEncrypted(onDisk));
        Assert.DoesNotContain("Secret plan", onDisk);
        Assert.Equal("# Open", File.ReadAllText(Path.Combine(_dir, "content", "b.txt")));
        Assert.Equal("# Secret plan", store.LoadContent("a")!.Text);

        ring.Lock(vault.KeyId);
        Assert.Throws<VaultLockedException>(() => store.LoadContent("a"));
        Assert.Equal("# Open", store.LoadContent("b")!.Text);
    }

    [Fact]
    public void Copies_saved_before_a_project_was_encrypted_get_encrypted()
    {
        var plainStore = new OfflineStore(_dir);
        plainStore.SaveContent(F("a", "/projects/Secret", "Plan.md"), "# Old plain copy", "\"a-1\"");
        plainStore.SaveContent(F("b", "/projects/Open", "Open.md"), "# Open", "\"b-1\"");

        var (ring, _, _, _) = NewRing();
        var store = new OfflineStore(_dir) { Codec = ring };
        Assert.Equal(1, store.ProtectUnder("/projects/Secret"));
        Assert.Equal(0, store.ProtectUnder("/projects/Secret"));

        Assert.DoesNotContain("Old plain copy", File.ReadAllText(Path.Combine(_dir, "content", "a.txt")));
        Assert.Equal("# Old plain copy", store.LoadContent("a")!.Text);
        Assert.Equal("# Open", File.ReadAllText(Path.Combine(_dir, "content", "b.txt")));
    }

    [Fact]
    public void Vaults_are_cached_for_offline_unlocking()
    {
        var store = new OfflineStore(_dir);
        var (vault, _, _) = ProjectVault.Create("passphrase", FastIterations);
        store.SaveVault("/projects/Secret", ProjectVault.Serialize(vault));
        Assert.Equal(vault.KeyId, ProjectVault.Parse(store.LoadVault("/projects/Secret")!)!.KeyId);
        Assert.Null(store.LoadVault("/projects/Other"));
    }

    // ---------- Names ----------

    [Fact]
    public void Encrypted_names_keep_their_titles_types_and_links()
    {
        var files = new List<SparkFile>
        {
            F("1", "/p", "Plan.md.enc"),
            F("2", "/p/Sub", "Todo.txt.enc"),
            F("3", "/p", "Plain.md"),
        };
        var index = new NoteIndex("/p", files);

        Assert.True(NoteIndex.IsNote(files[0]));
        Assert.Equal("Plan", NoteIndex.TitleOf(files[0]));
        Assert.True(NoteIndex.IsText(files[1]));
        Assert.False(NoteIndex.IsNote(files[1]));
        Assert.Equal("Todo.txt", NoteIndex.TitleOf(files[1]));
        Assert.Equal("1", index.Resolve("Plan")?.Id);
        Assert.Equal("1", index.Resolve("plan.md")?.Id);
        Assert.Equal("2", index.Resolve("Sub/Todo.txt")?.Id);
        Assert.Equal("Plan", index.LinkTextFor(files[0]));
        Assert.Equal("Plan.md", NoteIndex.LogicalName("Plan.md.enc"));
        Assert.Equal(".enc", NoteIndex.LogicalName(".enc"));
        Assert.True(NoteOperations.IsLinkScannable(files[1] with { SizeBytes = 10 }));
    }
}
