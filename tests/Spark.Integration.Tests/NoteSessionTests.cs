using System.Net;
using Spark.Api;
using Spark.Notes;

namespace Spark.Integration.Tests;

/// <summary>Save ordering, own-revision tracking and conflict detection, against the live test instance.</summary>
public class NoteSessionTests : IAsyncLifetime
{
    private readonly Sandbox _s = new();
    public Task InitializeAsync() => _s.InitializeAsync();
    public Task DisposeAsync() => _s.DisposeAsync();

    private async Task<(SparkFile File, NoteSession Session)> OpenAsync(string text)
    {
        var file = await _s.Client.UploadTextAsync(_s.Drive, _s.Dir, "Typing.md", text);
        var content = await _s.Client.GetTextAsync(file.Id);
        return (file, new NoteSession(_s.Client, file, content.Text, content.ETag));
    }

    [LiveFact]
    public async Task Rapid_overlapping_saves_land_in_order_without_conflicts()
    {
        var (file, session) = await OpenAsync("");
        var listedDuringSaves = new List<string>();

        // Simulate continuous typing: many saves queued while earlier ones are still in flight.
        var saves = new List<Task<SaveResult>>();
        var text = "";
        for (int i = 0; i < 25; i++)
        {
            text += $"line {i}\n";
            saves.Add(session.SaveAsync(text));
            if (i % 5 == 0) listedDuringSaves.Add(SparkClient.ETagFor((await _s.Client.GetFileAsync(file.Id))!));
        }
        var results = await Task.WhenAll(saves);

        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.False(session.InConflict);
        Assert.Equal(text, (await _s.Client.GetTextAsync(file.Id)).Text); // newest text wins
        Assert.Equal(text, session.SavedText);

        // Listings taken mid-typing are our own revisions (stale echoes), never "foreign".
        foreach (var etag in listedDuringSaves)
            Assert.Contains(session.Classify(etag), new[] { RemoteState.InSync, RemoteState.OwnEcho });
        Assert.Equal(RemoteState.InSync, session.Classify(SparkClient.ETagFor((await _s.Client.GetFileAsync(file.Id))!)));
    }

    [LiveFact]
    public async Task Listing_is_deferred_while_a_save_is_in_flight()
    {
        var (_, session) = await OpenAsync("a");
        var save = session.SaveAsync("b");
        Assert.Equal(RemoteState.Deferred, session.Classify("\"anything\""));
        await save;
        Assert.NotEqual(RemoteState.Deferred, session.Classify(session.ETag));
    }

    [LiveFact]
    public async Task Another_devices_edit_is_a_genuine_conflict_with_the_server_text()
    {
        var (file, session) = await OpenAsync("base\n");
        Assert.True((await session.SaveAsync("base\nmine\n")).Ok);

        var other = await _s.Client.GetTextAsync(file.Id);
        await _s.Client.PutTextAsync(file.Id, "base\ntheirs\n", other.ETag); // another device

        Assert.Equal(RemoteState.Foreign, session.Classify(SparkClient.ETagFor((await _s.Client.GetFileAsync(file.Id))!)));
        var result = await session.SaveAsync("base\nmine\nmore\n");
        Assert.Equal(SaveStatus.Conflict, result.Status);
        Assert.Equal("base\ntheirs\n", result.ServerText);
        Assert.True(session.InConflict);
        Assert.Equal(SaveStatus.Conflict, (await session.SaveAsync("still typing")).Status); // refused until resolved
        Assert.Equal("base\ntheirs\n", (await _s.Client.GetTextAsync(file.Id)).Text);

        // Keep mine, against the revision the user reviewed.
        var kept = await session.OverwriteAsync("base\nmine\nmore\n", result.ServerETag!);
        Assert.True(kept.Ok);
        Assert.False(session.InConflict);
        Assert.Equal("base\nmine\nmore\n", (await _s.Client.GetTextAsync(file.Id)).Text);
    }

    [LiveFact]
    public async Task Keep_mine_refuses_if_the_server_changed_again_after_review()
    {
        var (file, session) = await OpenAsync("v1");
        var reviewed = await _s.Client.GetTextAsync(file.Id);
        await _s.Client.PutTextAsync(file.Id, "v2 from elsewhere", reviewed.ETag);

        var result = await session.OverwriteAsync("mine", reviewed.ETag);
        Assert.Equal(SaveStatus.Conflict, result.Status);
        Assert.Equal("v2 from elsewhere", result.ServerText);
    }

    [LiveFact]
    public async Task Etag_drift_with_unchanged_text_is_retried_silently()
    {
        var (file, session) = await OpenAsync("same");
        var other = await _s.Client.GetTextAsync(file.Id);
        var (_, drifted) = await _s.Client.PutTextAsync(file.Id, "same", other.ETag); // new revision, same text
        Assert.NotEqual(session.ETag, drifted); // the server bumps the revision even for identical text

        var result = await session.SaveAsync("same, then edited");
        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal("same, then edited", (await _s.Client.GetTextAsync(file.Id)).Text);
    }

    [LiveFact]
    public async Task Server_already_having_our_text_is_not_a_conflict()
    {
        var (file, session) = await OpenAsync("start");
        var other = await _s.Client.GetTextAsync(file.Id);
        await _s.Client.PutTextAsync(file.Id, "identical edit", other.ETag);

        var result = await session.SaveAsync("identical edit");
        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.False(session.InConflict);
        Assert.Equal(RemoteState.InSync, session.Classify(SparkClient.ETagFor((await _s.Client.GetFileAsync(file.Id))!)));
    }
}
