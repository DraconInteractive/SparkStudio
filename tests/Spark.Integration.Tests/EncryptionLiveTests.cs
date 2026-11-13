using System.Text;
using Spark.Api;
using Spark.Notes;
using Spark.Notes.Crypto;

namespace Spark.Integration.Tests;

/// <summary>Client-side encryption against the live test instance: the server must only ever see ciphertext.</summary>
public class EncryptionLiveTests : IAsyncLifetime
{
    private readonly Sandbox _s = new();
    private SparkClient _other = null!;     // a second device that knows the passphrase
    private SparkClient _outsider = null!;  // someone reading the server's storage without the key
    private VaultFile _vault = null!;
    private byte[] _key = null!;

    public async Task InitializeAsync()
    {
        await _s.InitializeAsync();
        (_vault, _key, _) = ProjectVault.Create("integration passphrase", 1000);
        await _s.Client.UploadAsync(_s.Drive, _s.Dir, ProjectVault.FileName, Encoding.UTF8.GetBytes(ProjectVault.Serialize(_vault)));
        _s.Client.Codec = Ring();
        _other = new SparkClient(_s.Client.BaseUri) { Codec = Ring() };
        _outsider = new SparkClient(_s.Client.BaseUri);
    }

    private Keyring Ring()
    {
        var ring = new Keyring();
        ring.Register(_s.Dir, _vault);
        ring.AddKey(_vault.KeyId, _key, remember: false);
        return ring;
    }

    public async Task DisposeAsync()
    {
        _other.Dispose();
        _outsider.Dispose();
        await _s.DisposeAsync();
    }

    private async Task AssertStoredEncryptedAsync(string fileId, string mustNotContain)
    {
        var raw = (await _outsider.GetTextAsync(fileId)).Text;
        Assert.True(NoteCipher.IsEncrypted(raw), "stored as plain text: " + raw);
        Assert.DoesNotContain(mustNotContain, raw);
    }

    [LiveFact]
    public async Task Notes_are_stored_encrypted_and_read_back_as_plain_text()
    {
        var file = await _s.Client.UploadTextAsync(_s.Drive, _s.Dir + "/Sub", "Plan.md.enc", "# Launch codes\n[[Other]]");
        await AssertStoredEncryptedAsync(file.Id, "Launch codes");

        var content = await _s.Client.GetTextAsync(file.Id);
        Assert.Equal("# Launch codes\n[[Other]]", content.Text);

        var (saved, etag) = await _s.Client.PutTextAsync(file, "# Launch codes v2", content.ETag);
        await AssertStoredEncryptedAsync(file.Id, "v2");
        Assert.Equal("# Launch codes v2", (await _other.GetTextAsync(file.Id)).Text);
        Assert.Equal(etag, (await _s.Client.GetTextAsync(saved.Id)).ETag);

        // Without the key the text can't be read.
        var locked = new SparkClient(_s.Client.BaseUri) { Codec = new Keyring() };
        await Assert.ThrowsAsync<VaultLockedException>(() => locked.GetTextAsync(file.Id));
        locked.Dispose();
    }

    [LiveFact]
    public async Task Session_saves_and_conflicts_compare_plain_text_not_ciphertext()
    {
        var file = await _s.Client.UploadTextAsync(_s.Drive, _s.Dir, "Typing.md.enc", "base\n");
        var start = await _s.Client.GetTextAsync(file.Id);
        var session = new NoteSession(_s.Client, file, start.Text, start.ETag);

        Assert.Equal(SaveStatus.Saved, (await session.SaveAsync("base\nmine\n")).Status);
        await AssertStoredEncryptedAsync(file.Id, "mine");

        // Another device saves the same text: different ciphertext, but not a conflict.
        var theirs = await _other.GetTextAsync(file.Id);
        await _other.PutTextAsync(file, "base\nmine\nshared\n", theirs.ETag);
        var r = await session.SaveAsync("base\nmine\nshared\n");
        Assert.Equal(SaveStatus.Saved, r.Status);
        Assert.False(session.InConflict);

        // A genuinely different edit elsewhere is a conflict, reported with the decrypted server text.
        theirs = await _other.GetTextAsync(file.Id);
        await _other.PutTextAsync(file, "theirs\n", theirs.ETag);
        var conflict = await session.SaveAsync("mine again\n");
        Assert.Equal(SaveStatus.Conflict, conflict.Status);
        Assert.Equal("theirs\n", conflict.ServerText);
    }

    [LiveFact]
    public async Task Renaming_keeps_the_note_decryptable_and_plain_notes_get_encrypted_on_save()
    {
        var plain = await _outsider.UploadTextAsync(_s.Drive, _s.Dir, "Old.md", "# Written before encryption");
        var content = await _s.Client.GetTextAsync(plain.Id);
        Assert.False(((Keyring)_s.Client.Codec!).WasReadEncrypted(plain.Id));

        // Saving a plain note that lies in the vault's folder encrypts it, whatever its name.
        await _s.Client.PutTextAsync(plain, content.Text, content.ETag);
        await AssertStoredEncryptedAsync(plain.Id, "Written before");

        var renamed = await NoteOperations.RenameFileAsync(_s.Client, plain, "Old.md.enc");
        Assert.Equal("Old.md.enc", renamed.Name);
        Assert.Equal("# Written before encryption", (await _s.Client.GetTextAsync(renamed.Id)).Text);

        var (_, files) = await NoteOperations.ListTreeAsync(_s.Client, _s.Drive, _s.Dir);
        var index = new NoteIndex(_s.Dir, files.Where(f => !ProjectVault.IsVaultFile(f)));
        Assert.Equal(renamed.Id, index.Resolve("Old")?.Id);
    }

    [LiveFact]
    public async Task Rename_rewrites_links_inside_encrypted_notes()
    {
        var target = await _s.Client.UploadTextAsync(_s.Drive, _s.Dir, "Timer.md.enc", "# Timer");
        var source = await _s.Client.UploadTextAsync(_s.Drive, _s.Dir, "A.md.enc", "See [[Timer]]");

        var result = await NoteOperations.RenameAsync(_s.Client, _s.Drive, _s.Dir, target, "Watch Timer.md.enc", new LinkCache());

        Assert.Equal(1, result.LinksUpdated);
        Assert.Equal("See [[Watch Timer]]", (await _s.Client.GetTextAsync(source.Id)).Text);
        await AssertStoredEncryptedAsync(source.Id, "Watch Timer");
    }
}
