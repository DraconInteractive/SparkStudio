using System.Net;
using Spark.Api;
using Spark.Notes;

namespace Spark.Integration.Tests;

/// <summary>Runs only when SPARK_TEST_URL points at a disposable SPARK instance (e.g. http://192.168.4.20:47693).</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SPARK_TEST_URL")))
            Skip = "Set SPARK_TEST_URL to a disposable SPARK test instance to run live tests.";
    }
}

/// <summary>Creates a uniquely named root project for one test and destroys it (and its files) afterwards.</summary>
public sealed class Sandbox : IAsyncLifetime
{
    public SparkClient Client { get; } = new(new Uri(Environment.GetEnvironmentVariable("SPARK_TEST_URL")!.TrimEnd('/') + "/"));
    public Project Project { get; private set; } = null!;
    public string Dir => Project.Directory;
    public string Drive => Project.DriveLetter;

    public async Task InitializeAsync()
    {
        Project = await Client.CreateProjectAsync(new { name = "studio-test-" + Guid.NewGuid().ToString("N")[..8], description = "" });
    }

    public async Task DisposeAsync()
    {
        try { await Client.DeleteProjectAsync(Project.Id, destroyDirectory: true); } catch { }
        Client.Dispose();
    }
}

public class LiveServerTests : IAsyncLifetime
{
    private readonly Sandbox _s = new();
    public Task InitializeAsync() => _s.InitializeAsync();
    public Task DisposeAsync() => _s.DisposeAsync();

    [LiveFact]
    public async Task Text_save_is_guarded_by_etag()
    {
        var c = _s.Client;
        var file = await c.UploadTextAsync(_s.Drive, _s.Dir, "Note.md", "# Hello\n");
        var first = await c.GetTextAsync(file.Id);
        Assert.Equal("# Hello\n", first.Text);
        Assert.Equal(SparkClient.ETagFor(file), first.ETag);

        var (saved, etag2) = await c.PutTextAsync(file.Id, "# Hello\n\nSee [[Other]]\n", first.ETag);
        Assert.NotEqual(first.ETag, etag2);
        Assert.Equal(etag2, SparkClient.ETagFor(saved));

        var stale = await Assert.ThrowsAsync<SparkApiException>(() => c.PutTextAsync(file.Id, "lost update", first.ETag));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.Status);
        Assert.Equal("# Hello\n\nSee [[Other]]\n", (await c.GetTextAsync(file.Id)).Text);
    }

    [LiveFact]
    public async Task Tree_listing_includes_subfolders()
    {
        var c = _s.Client;
        await c.CreateDirectoryAsync(_s.Drive, _s.Dir + "/Design/Watch");
        await c.UploadTextAsync(_s.Drive, _s.Dir, "Top.md", "");
        await c.UploadTextAsync(_s.Drive, _s.Dir + "/Design/Watch", "Battery.md", "");
        var (dirs, files) = await NoteOperations.ListTreeAsync(c, _s.Drive, _s.Dir);
        Assert.Contains(_s.Dir + "/Design/Watch", dirs);
        Assert.Equal(new[] { "Battery.md", "Top.md" }, files.Select(f => f.Name).OrderBy(n => n));
        var index = new NoteIndex(_s.Dir, files);
        Assert.Equal("Design/Watch/Battery.md", index.RelativePath(index.Resolve("battery")!));
    }

    [LiveFact]
    public async Task Rename_moves_content_and_rewrites_backlinks()
    {
        var c = _s.Client;
        var target = await c.UploadTextAsync(_s.Drive, _s.Dir, "Timer.md", "# Timer\n");
        await c.CreateDirectoryAsync(_s.Drive, _s.Dir + "/Sub");
        var a = await c.UploadTextAsync(_s.Drive, _s.Dir, "A.md", "See [[Timer]] and [[timer#Setup|setup]].\n");
        var b = await c.UploadTextAsync(_s.Drive, _s.Dir + "/Sub", "B.md", "Unrelated [[Other]] but also [[Timer]]\n`[[Timer]]`\n");

        var result = await NoteOperations.RenameAsync(c, _s.Drive, _s.Dir, target, "Watch Timer.md", new LinkCache());

        Assert.Equal("Watch Timer.md", result.File.Name);
        Assert.Equal("# Timer\n", (await c.GetTextAsync(result.File.Id)).Text);
        if (await c.SupportsFileRenameAsync() == true)
        {
            // Native rename keeps the file's identity.
            Assert.Equal(target.Id, result.File.Id);
            Assert.Equal("Watch Timer.md", (await c.GetFileAsync(target.Id))!.Name);
        }
        else
        {
            await Assert.ThrowsAsync<SparkApiException>(() => c.GetFileAsync(target.Id));
        }
        Assert.Equal("See [[Watch Timer]] and [[Watch Timer#Setup|setup]].\n", (await c.GetTextAsync(a.Id)).Text);
        Assert.Equal("Unrelated [[Other]] but also [[Watch Timer]]\n`[[Timer]]`\n", (await c.GetTextAsync(b.Id)).Text);
        Assert.Equal(3, result.LinksUpdated);
        Assert.Equal(0, result.NotesFailed);
    }

    [LiveFact]
    public async Task Rename_refuses_to_overwrite_existing_name()
    {
        var c = _s.Client;
        var one = await c.UploadTextAsync(_s.Drive, _s.Dir, "One.md", "1");
        await c.UploadTextAsync(_s.Drive, _s.Dir, "Two.md", "2");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NoteOperations.RenameAsync(c, _s.Drive, _s.Dir, one, "two.md", new LinkCache()));
        Assert.Equal("1", (await c.GetTextAsync(one.Id)).Text);
    }

    [LiveFact]
    public async Task Native_rename_keeps_identity_revision_and_etag()
    {
        var c = _s.Client;
        if (await c.SupportsFileRenameAsync() != true) return; // server predates API 1.45
        var file = await c.UploadTextAsync(_s.Drive, _s.Dir, "Draft.md", "text");
        var before = await c.GetTextAsync(file.Id);

        var renamed = await c.RenameFileAsync(file.Id, "Final.md");

        Assert.Equal(file.Id, renamed.Id);
        Assert.Equal("Final.md", renamed.Name);
        Assert.Equal(file.Directory, renamed.Directory);
        var after = await c.GetTextAsync(file.Id);
        Assert.Equal("text", after.Text);
        Assert.Equal(before.ETag, after.ETag); // an open editor's ETag stays valid across a rename
    }

    [LiveFact]
    public async Task Verify_reports_open_mode_on_open_servers()
    {
        var contract = await _s.Client.GetContractAsync();
        if (contract?.IsTokenMode == true) return; // this check is for open-mode test instances
        var auth = await _s.Client.VerifyAsync("studio-integration-test");
        Assert.False(auth.Authenticated);
        Assert.True(auth.IsOpenMode);
    }

    [LiveFact]
    public async Task Task_severity_defaults_to_normal_and_survives_other_edits()
    {
        var c = _s.Client;
        var pid = _s.Project.Id;
        var board = (await c.CreateBoardAsync(pid, "Sev"))!;
        var col = (await c.CreateColumnAsync(pid, board.Id, "To do"))!;
        var done = (await c.CreateColumnAsync(pid, board.Id, "Done"))!;
        var plain = (await c.CreateTaskAsync(pid, board.Id, col.Id, "Plain", ""))!;

        if ((await c.GetContractAsync())?.SupportsTaskSeverity != true)
        {
            Assert.Null(plain.Severity); // pre-1.48 server: no severity, and omitting it keeps requests valid
            return;
        }

        Assert.Equal(TaskSeverity.Normal, plain.Severity);
        var urgent = (await c.CreateTaskAsync(pid, board.Id, col.Id, "Fire", "", severity: "Urgent"))!;
        Assert.Equal("Urgent", urgent.Severity);

        // Omitted severity on PATCH is retained.
        var moved = (await c.UpdateTaskAsync(pid, board.Id, urgent.Id, columnId: done.Id, name: "Fire!"))!;
        Assert.Equal("Urgent", moved.Severity);

        foreach (var level in TaskSeverity.Levels)
            Assert.Equal(level, (await c.UpdateTaskAsync(pid, board.Id, plain.Id, severity: level))!.Severity);

        var bad = await Assert.ThrowsAsync<SparkApiException>(() => c.UpdateTaskAsync(pid, board.Id, plain.Id, severity: "urgent"));
        Assert.Equal("invalid_severity", bad.Code);

        var full = (await c.GetBoardAsync(pid, board.Id))!;
        Assert.Equal("Urgent", full.Columns.Single(x => x.Id == done.Id).Tasks.Single().Severity);
    }

    [LiveFact]
    public async Task Board_lifecycle()
    {
        var c = _s.Client;
        var pid = _s.Project.Id;
        var board = (await c.CreateBoardAsync(pid, "Development"))!;
        var todo = (await c.CreateColumnAsync(pid, board.Id, "To do"))!;
        var done = (await c.CreateColumnAsync(pid, board.Id, "Done"))!;
        var task = (await c.CreateTaskAsync(pid, board.Id, todo.Id, "Write editor", ""))!;

        var moved = (await c.UpdateTaskAsync(pid, board.Id, task.Id, columnId: done.Id))!;
        Assert.Equal(done.Id, moved.ColumnId);
        var renamed = (await c.UpdateTaskAsync(pid, board.Id, task.Id, description: "Links: [[Spec]]"))!;
        Assert.Equal("Write editor", renamed.Name);
        Assert.Equal("Links: [[Spec]]", renamed.Description);

        var notEmpty = await Assert.ThrowsAsync<SparkApiException>(() => c.DeleteColumnAsync(pid, board.Id, done.Id));
        Assert.Equal("column_not_empty", notEmpty.Code);
        await c.DeleteColumnAsync(pid, board.Id, todo.Id);

        var full = (await c.GetBoardAsync(pid, board.Id))!;
        Assert.Equal(new[] { "Done" }, full.Columns.Select(col => col.Name));
        Assert.Single(full.Columns[0].Tasks);

        await c.DeleteBoardAsync(pid, board.Id);
        Assert.Empty(await c.ListBoardsAsync(pid));
    }

    [LiveFact]
    public async Task Notifications_report_file_changes()
    {
        var url = new Uri(Environment.GetEnvironmentVariable("SPARK_TEST_URL")!);
        var port = await _s.Client.GetNotificationPortAsync() ?? 47684;
        await using var live = new NotificationClient(url.Host, port, "studio-integration-test");
        var welcomed = new TaskCompletionSource();
        var seen = new System.Collections.Concurrent.ConcurrentQueue<SparkNotification>();
        var arrived = new SemaphoreSlim(0);
        live.ResyncRequired += () => welcomed.TrySetResult();
        // Other test classes run in parallel and create files too: look for this test's file among the events.
        live.Notified += n =>
        {
            if (n.Type != "file.created") return;
            seen.Enqueue(n);
            arrived.Release();
        };
        live.Start();
        await welcomed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var file = await _s.Client.UploadTextAsync(_s.Drive, _s.Dir, "Live.md", "");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!seen.Any(n => n.Resource == $"/files/{file.Id}"))
            Assert.True(await arrived.WaitAsync(deadline - DateTime.UtcNow > TimeSpan.Zero ? deadline - DateTime.UtcNow : TimeSpan.Zero),
                "no file.created notification for the uploaded file");
    }
}
