using System.Net;
using Spark.Api;

namespace Spark.Notes;

public enum SaveStatus
{
    /// <summary>The text is on the server.</summary>
    Saved,
    /// <summary>The note is in conflict; nothing was sent. Resolve it first.</summary>
    Conflict,
    /// <summary>Network or server error; the text is not saved.</summary>
    Failed,
}

/// <param name="ServerText">For conflicts: the server's current text.</param>
/// <param name="ServerETag">For conflicts: the ETag that goes with <paramref name="ServerText"/>.</param>
public sealed record SaveResult(SaveStatus Status, SparkFile? File = null, string? ServerText = null, string? ServerETag = null,
    Exception? Error = null, string? Detail = null)
{
    public bool Ok => Status == SaveStatus.Saved;
}

/// <summary>What a server listing / notification means for an open note.</summary>
public enum RemoteState
{
    /// <summary>Server has exactly the revision we last saved or loaded.</summary>
    InSync,
    /// <summary>An older revision this session produced (stale listing or a late echo of our own save).</summary>
    OwnEcho,
    /// <summary>A save is in flight, so the listing can't be judged yet; check again when it finishes.</summary>
    Deferred,
    /// <summary>A revision this session didn't produce: probably another device's edit.</summary>
    Foreign,
}

/// <summary>
/// Saves one note to the server: serialized and in order (every save sends the newest text), guarded by the
/// server's ETag, and able to tell its own revisions apart from other devices' edits.
/// Only a 412 that survives a content check counts as a conflict.
/// </summary>
public sealed class NoteSession
{
    private const int OwnETagMemory = 64;

    private readonly SparkClient _client;
    private readonly object _gate = new();
    private readonly LinkedList<string> _ownOrder = new();
    private readonly HashSet<string> _own = new();
    private Task _tail = Task.CompletedTask;
    private string? _pendingText;
    private int _queued;

    public NoteSession(SparkClient client, string fileId, string text, string etag)
    {
        _client = client;
        FileId = fileId;
        SavedText = text;
        ETag = etag;
        Remember(etag);
    }

    public string FileId { get; }
    /// <summary>ETag of the server revision we believe is current (what the next save sends as If-Match).</summary>
    public string ETag { get; private set; }
    /// <summary>The text the server has at <see cref="ETag"/>.</summary>
    public string SavedText { get; private set; }
    /// <summary>True once a save hit a genuine conflict; further saves are refused until it's resolved.</summary>
    public bool InConflict { get; private set; }
    /// <summary>True while any save is queued or running.</summary>
    public bool IsSaving => Volatile.Read(ref _queued) > 0;

    /// <summary>Raised (on the caller's thread) for diagnostics: retries, conflicts, adoptions.</summary>
    public event Action<string>? Log;

    /// <summary>Raised after every save attempt finishes, so callers can re-run deferred remote checks.</summary>
    public event Action? SaveFinished;

    public bool IsOwnETag(string etag)
    {
        lock (_gate) return _own.Contains(etag);
    }

    /// <summary>Classifies an ETag seen in a listing or notification for this note.</summary>
    public RemoteState Classify(string listedETag)
    {
        if (IsSaving) return RemoteState.Deferred;
        if (listedETag == ETag) return RemoteState.InSync;
        return IsOwnETag(listedETag) ? RemoteState.OwnEcho : RemoteState.Foreign;
    }

    /// <summary>
    /// Queues a save of <paramref name="text"/>. Saves run one at a time in call order, and each one sends the
    /// newest text queued so far, so an older text can never overwrite a newer one.
    /// </summary>
    public Task<SaveResult> SaveAsync(string text)
    {
        lock (_gate)
        {
            _pendingText = text;
            _queued++;
            var prev = _tail;
            var run = RunAfterAsync(prev);
            _tail = run;
            return run;
        }
    }

    private async Task<SaveResult> RunAfterAsync(Task previous)
    {
        try
        {
            await Task.Yield(); // never run network work inside SaveAsync's lock
            try { await previous.ConfigureAwait(true); } catch { /* the previous save reported its own failure */ }
            string text;
            lock (_gate) text = _pendingText!;
            return await SaveNowAsync(text).ConfigureAwait(true);
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
            SaveFinished?.Invoke();
        }
    }

    private async Task<SaveResult> SaveNowAsync(string text)
    {
        if (InConflict) return new SaveResult(SaveStatus.Conflict, Detail: "unresolved conflict");
        if (text == SavedText) return new SaveResult(SaveStatus.Saved);
        try
        {
            return Accept(await _client.PutTextAsync(FileId, text, ETag), text);
        }
        catch (SparkApiException ex) when (ex.Status == HttpStatusCode.PreconditionFailed)
        {
            return await RecoverFromPreconditionFailedAsync(text);
        }
        catch (Exception ex)
        {
            return new SaveResult(SaveStatus.Failed, Error: ex);
        }
    }

    /// <summary>
    /// A 412 only means the ETag moved. If the server's text is what we last saved (only the ETag drifted) or
    /// already what we're saving, it isn't a real conflict: adopt the new ETag and carry on.
    /// </summary>
    private async Task<SaveResult> RecoverFromPreconditionFailedAsync(string text)
    {
        TextContent server;
        try { server = await _client.GetTextAsync(FileId); }
        catch (Exception ex) { return new SaveResult(SaveStatus.Failed, Error: ex); }

        if (server.Text == text)
        {
            Log?.Invoke($"412 on {FileId}: server already has this text; adopting {server.ETag} (was {ETag})");
            Adopt(server.Text, server.ETag);
            return new SaveResult(SaveStatus.Saved);
        }
        if (server.Text == SavedText)
        {
            Log?.Invoke($"412 on {FileId}: ETag drifted {ETag} -> {server.ETag} with unchanged text; retrying");
            ETag = server.ETag;
            Remember(server.ETag);
            try
            {
                return Accept(await _client.PutTextAsync(FileId, text, server.ETag), text);
            }
            catch (SparkApiException ex) when (ex.Status == HttpStatusCode.PreconditionFailed)
            {
                try { server = await _client.GetTextAsync(FileId); }
                catch (Exception e) { return new SaveResult(SaveStatus.Failed, Error: e); }
            }
            catch (Exception ex) { return new SaveResult(SaveStatus.Failed, Error: ex); }
        }

        InConflict = true;
        Log?.Invoke($"CONFLICT on {FileId}: local ETag {ETag}, server ETag {server.ETag}, " +
                    $"server text {server.Text.Length} chars, ours {text.Length} chars, last saved {SavedText.Length} chars");
        return new SaveResult(SaveStatus.Conflict, ServerText: server.Text, ServerETag: server.ETag);
    }

    private SaveResult Accept((SparkFile File, string ETag) put, string text)
    {
        ETag = put.ETag;
        SavedText = text;
        Remember(put.ETag);
        return new SaveResult(SaveStatus.Saved, put.File);
    }

    /// <summary>Takes a server revision as the new baseline (reload, or "use the server's version").</summary>
    public void Adopt(string text, string etag)
    {
        SavedText = text;
        ETag = etag;
        InConflict = false;
        Remember(etag);
    }

    /// <summary>
    /// Resolves a conflict by overwriting the server revision the user reviewed (<paramref name="reviewedETag"/>).
    /// Returns a new conflict if the server changed again since then.
    /// </summary>
    public async Task<SaveResult> OverwriteAsync(string text, string reviewedETag)
    {
        await WaitForSavesAsync();
        try
        {
            var put = await _client.PutTextAsync(FileId, text, reviewedETag);
            InConflict = false;
            return Accept(put, text);
        }
        catch (SparkApiException ex) when (ex.Status == HttpStatusCode.PreconditionFailed)
        {
            var server = await _client.GetTextAsync(FileId);
            InConflict = true;
            return new SaveResult(SaveStatus.Conflict, ServerText: server.Text, ServerETag: server.ETag);
        }
        catch (Exception ex) { return new SaveResult(SaveStatus.Failed, Error: ex); }
    }

    /// <summary>Completes when every queued save has finished.</summary>
    public async Task WaitForSavesAsync()
    {
        Task tail;
        lock (_gate) tail = _tail;
        try { await tail; } catch { }
    }

    private void Remember(string etag)
    {
        lock (_gate)
        {
            if (!_own.Add(etag)) return;
            _ownOrder.AddLast(etag);
            if (_ownOrder.Count > OwnETagMemory)
            {
                _own.Remove(_ownOrder.First!.Value);
                _ownOrder.RemoveFirst();
            }
        }
    }
}
