using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Spark.Api;

public sealed class SparkApiException : Exception
{
    public HttpStatusCode Status { get; }
    /// <summary>The server's machine-readable error code, e.g. "column_not_empty".</summary>
    public string? Code { get; }

    public SparkApiException(HttpStatusCode status, string? code, string message)
        : base(message)
    {
        Status = status;
        Code = code;
    }
}

/// <summary>Thin typed wrapper around the SPARK REST API (projects, boards and files).</summary>
public sealed class SparkClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly HttpClient _http;

    public Uri BaseUri { get; }

    public SparkClient(Uri baseUri, string? bearerToken = null)
    {
        BaseUri = baseUri;
        _http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrWhiteSpace(bearerToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    public void Dispose() => _http.Dispose();

    // ---------- Server ----------

    public async Task<string> GetApiVersionAsync(CancellationToken ct = default)
    {
        var node = await SendAsync<JsonObject>(HttpMethod.Get, "version", null, ct);
        return node?["api_version"]?.GetValue<string>() ?? "";
    }

    /// <summary>The TCP notification port advertised by the server's /api contract, if any.</summary>
    public async Task<int?> GetNotificationPortAsync(CancellationToken ct = default)
    {
        var node = await SendAsync<JsonObject>(HttpMethod.Get, "api", null, ct);
        return node?["notifications"]?["port"]?.GetValue<int>();
    }

    // ---------- Projects ----------

    public async Task<List<Project>> ListProjectsAsync(CancellationToken ct = default)
    {
        var all = new List<Project>();
        for (int offset = 0; ; offset += 500)
        {
            var page = await SendAsync<ProjectPage>(HttpMethod.Get, $"projects?limit=500&offset={offset}", null, ct);
            all.AddRange(page!.Projects);
            if (!page.HasMore) return all;
        }
    }

    public Task<Project?> GetProjectAsync(string id, CancellationToken ct = default) =>
        SendAsync<Project>(HttpMethod.Get, $"projects/{id}", null, ct);

    /// <summary>Body: name, description (required, may be empty), optional parent_id, github_url, drive.</summary>
    public async Task<Project> CreateProjectAsync(object body, CancellationToken ct = default) =>
        (await SendAsync<Project>(HttpMethod.Post, "projects", body, ct))!;

    /// <summary>Body: any of name, description, github_url.</summary>
    public async Task<Project> UpdateProjectAsync(string id, object body, CancellationToken ct = default) =>
        (await SendAsync<Project>(HttpMethod.Patch, $"projects/{id}", body, ct))!;

    /// <summary>Deletes the project and ALL descendant projects; optionally destroys its directory and files.</summary>
    public Task DeleteProjectAsync(string id, bool destroyDirectory, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"projects/{id}", new { destroy_directory = destroyDirectory }, ct);

    // ---------- Boards ----------

    public async Task<List<BoardSummary>> ListBoardsAsync(string projectId, CancellationToken ct = default) =>
        (await SendAsync<BoardList>(HttpMethod.Get, $"projects/{projectId}/boards", null, ct))!.Boards;

    public Task<Board?> GetBoardAsync(string projectId, string boardId, CancellationToken ct = default) =>
        SendAsync<Board>(HttpMethod.Get, $"projects/{projectId}/boards/{boardId}", null, ct);

    public Task<Board?> CreateBoardAsync(string projectId, string name, CancellationToken ct = default) =>
        SendAsync<Board>(HttpMethod.Post, $"projects/{projectId}/boards", new { name }, ct);

    public Task<Board?> RenameBoardAsync(string projectId, string boardId, string name, CancellationToken ct = default) =>
        SendAsync<Board>(HttpMethod.Patch, $"projects/{projectId}/boards/{boardId}", new { name }, ct);

    public Task DeleteBoardAsync(string projectId, string boardId, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"projects/{projectId}/boards/{boardId}", null, ct);

    public Task<BoardColumn?> CreateColumnAsync(string projectId, string boardId, string name, CancellationToken ct = default) =>
        SendAsync<BoardColumn>(HttpMethod.Post, $"projects/{projectId}/boards/{boardId}/columns", new { name }, ct);

    public Task<BoardColumn?> RenameColumnAsync(string projectId, string boardId, string columnId, string name, CancellationToken ct = default) =>
        SendAsync<BoardColumn>(HttpMethod.Patch, $"projects/{projectId}/boards/{boardId}/columns/{columnId}", new { name }, ct);

    public Task DeleteColumnAsync(string projectId, string boardId, string columnId, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"projects/{projectId}/boards/{boardId}/columns/{columnId}", null, ct);

    public Task<BoardTask?> CreateTaskAsync(string projectId, string boardId, string columnId, string name, string description, CancellationToken ct = default) =>
        SendAsync<BoardTask>(HttpMethod.Post, $"projects/{projectId}/boards/{boardId}/columns/{columnId}/tasks", new { name, description }, ct);

    /// <summary>Partially updates a task; null arguments are left unchanged.</summary>
    public Task<BoardTask?> UpdateTaskAsync(string projectId, string boardId, string taskId,
        string? name = null, string? description = null, string? columnId = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, string>();
        if (name != null) body["name"] = name;
        if (description != null) body["description"] = description;
        if (columnId != null) body["column_id"] = columnId;
        return SendAsync<BoardTask>(HttpMethod.Patch, $"projects/{projectId}/boards/{boardId}/tasks/{taskId}", body, ct);
    }

    public Task DeleteTaskAsync(string projectId, string boardId, string taskId, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"projects/{projectId}/boards/{boardId}/tasks/{taskId}", null, ct);

    // ---------- Files ----------

    public async Task<List<string>> ListDirectoriesAsync(string drive, CancellationToken ct = default)
    {
        var node = await SendAsync<JsonObject>(HttpMethod.Get, $"files/directories?drive={Uri.EscapeDataString(drive)}", null, ct);
        return node!["directories"]!.AsArray().Select(d => d!.GetValue<string>()).ToList();
    }

    public async Task<List<SparkFile>> ListFilesAsync(string drive, string directory, CancellationToken ct = default)
    {
        var all = new List<SparkFile>();
        for (int offset = 0; ; offset += 500)
        {
            var page = await SendAsync<FilePage>(HttpMethod.Get,
                $"files?drive={Uri.EscapeDataString(drive)}&directory={Uri.EscapeDataString(directory)}&limit=500&offset={offset}", null, ct);
            all.AddRange(page!.Files);
            if (!page.HasMore) return all;
        }
    }

    public Task<SparkFile?> GetFileAsync(string id, CancellationToken ct = default) =>
        SendAsync<SparkFile>(HttpMethod.Get, $"files/{id}", null, ct);

    public Task CreateDirectoryAsync(string drive, string directory, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Post, "files/directories", new { directory, drive }, ct);

    public Task DeleteDirectoryAsync(string drive, string directory, bool recursive, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, "files/directories", new { directory, drive, recursive }, ct);

    public Task<SparkFile?> MoveFileAsync(string id, string directory, CancellationToken ct = default) =>
        SendAsync<SparkFile>(HttpMethod.Patch, $"files/{id}", new { directory }, ct);

    /// <summary>
    /// Renames a file in place (API 1.45+): ID, bytes, content revision and download URL are unchanged.
    /// Check <see cref="SupportsFileRenameAsync"/> first; older servers reject the name field.
    /// </summary>
    public async Task<SparkFile> RenameFileAsync(string id, string name, CancellationToken ct = default) =>
        (await SendAsync<SparkFile>(HttpMethod.Patch, $"files/{id}", new { name }, ct))!;

    private bool? _supportsRename;

    /// <summary>Whether the server's /api contract says PATCH /files/{id} accepts a new name.</summary>
    public async Task<bool> SupportsFileRenameAsync(CancellationToken ct = default)
    {
        if (_supportsRename is { } known) return known;
        var api = await SendAsync<JsonObject>(HttpMethod.Get, "api", null, ct);
        var patch = api?["endpoints"]?.AsArray().OfType<JsonObject>().FirstOrDefault(e =>
            e["method"]?.ToString() == "PATCH" && e["path"]?.ToString() == "/files/{id}");
        _supportsRename = patch?["input"]?["body"] is JsonObject body && body.ContainsKey("name");
        return _supportsRename.Value;
    }

    public Task DeleteFileAsync(string id, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"files/{id}", null, ct);

    public async Task<SparkFile> UploadAsync(string drive, string directory, string name, byte[] content, CancellationToken ct = default)
    {
        var url = $"files?drive={Uri.EscapeDataString(drive)}&directory={Uri.EscapeDataString(directory)}&name={Uri.EscapeDataString(name)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(content) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<SparkFile>(Json, ct))!;
    }

    public Task<SparkFile> UploadTextAsync(string drive, string directory, string name, string text, CancellationToken ct = default) =>
        UploadAsync(drive, directory, name, Utf8NoBom.GetBytes(text), ct);

    public async Task<byte[]> GetBytesAsync(string id, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"files/{id}/content", HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(resp, ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<TextContent> GetTextAsync(string id, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync($"files/{id}/content", ct);
        await EnsureSuccessAsync(resp, ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var etag = resp.Headers.ETag?.ToString() ?? "";
        return new TextContent(DecodeUtf8(bytes), etag);
    }

    /// <summary>Saves text guarded by <paramref name="etag"/>. Throws with status 412 if the file changed meanwhile.</summary>
    public async Task<(SparkFile File, string ETag)> PutTextAsync(string id, string text, string etag, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, $"files/{id}/content")
        {
            Content = new ByteArrayContent(Utf8NoBom.GetBytes(text)),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
        req.Headers.TryAddWithoutValidation("If-Match", etag);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
        var file = (await resp.Content.ReadFromJsonAsync<SparkFile>(Json, ct))!;
        return (file, resp.Headers.ETag?.ToString() ?? $"\"{file.Id}-{file.ContentRevision}\"");
    }

    /// <summary>The server's ETag format for content: "{id}-{content_revision}", or "{id}" for never-edited uploads.</summary>
    public static string ETagFor(SparkFile f) =>
        string.IsNullOrEmpty(f.ContentRevision) ? $"\"{f.Id}\"" : $"\"{f.Id}-{f.ContentRevision}\"";

    public static string DecodeUtf8(byte[] bytes)
    {
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }

    // ---------- Plumbing ----------

    private async Task<T?> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body != null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        using var resp = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(resp, ct);
        if (resp.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object)) return default;
        return await resp.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        string? code = null, detail = null;
        try
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (JsonNode.Parse(text) is JsonObject obj)
            {
                code = obj["error"]?.ToString();
                detail = obj["message"]?.ToString();
            }
        }
        catch (JsonException) { }
        var msg = $"{(int)resp.StatusCode} {resp.ReasonPhrase}" + (code != null ? $": {code}" : "") + (detail != null ? $" ({detail})" : "");
        throw new SparkApiException(resp.StatusCode, code, msg);
    }

    private sealed record ProjectPage(List<Project> Projects, bool HasMore);
    private sealed record BoardList(List<BoardSummary> Boards);
    private sealed record FilePage(List<SparkFile> Files, bool HasMore);
}
