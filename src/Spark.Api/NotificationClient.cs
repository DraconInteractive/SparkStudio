using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Spark.Api;

public sealed record SparkNotification(string Type, string? Resource, string? SourceDevice, JsonObject Raw);

/// <summary>
/// Listens to the SPARK TCP notification stream (newline-delimited JSON) and raises events.
/// Notifications are hints only: consumers re-read state over REST. The cursor is kept in memory,
/// so a fresh app start always resyncs from REST.
/// </summary>
public sealed class NotificationClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _deviceId;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private ulong? _lastSequence;

    public event Action<SparkNotification>? Notified;
    /// <summary>Raised after (re)connecting when the client must refresh all cached state.</summary>
    public event Action? ResyncRequired;
    public event Action<bool>? ConnectionChanged;

    public NotificationClient(string host, int port, string deviceId)
    {
        _host = host;
        _port = port;
        _deviceId = deviceId;
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        var rng = new Random();
        double backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(_host, _port, ct);
                await using var stream = tcp.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

                var hello = new JsonObject { ["type"] = "hello", ["device_id"] = _deviceId };
                if (_lastSequence is { } seq) hello["after_sequence"] = seq;
                await writer.WriteLineAsync(hello.ToJsonString().AsMemory(), ct);

                ConnectionChanged?.Invoke(true);
                backoff = 1;

                while (!ct.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(TimeSpan.FromSeconds(120));
                    var line = await reader.ReadLineAsync(idle.Token);
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (JsonNode.Parse(line) is not JsonObject msg) continue;
                    await HandleAsync(msg, writer, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { /* fall through to reconnect */ }

            ConnectionChanged?.Invoke(false);
            var delay = Math.Min(30, backoff) * (0.5 + rng.NextDouble() * 0.5);
            backoff *= 2;
            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task HandleAsync(JsonObject msg, StreamWriter writer, CancellationToken ct)
    {
        var type = msg["type"]?.GetValue<string>() ?? "";
        if (msg["sequence"] is JsonValue sv && sv.TryGetValue<ulong>(out var seq)) _lastSequence = seq;

        switch (type)
        {
            case "ping":
                await writer.WriteLineAsync("{\"type\":\"pong\"}".AsMemory(), ct);
                break;
            case "pong":
            case "checkpoint":
            case "catchup_complete":
            case "error":
                break;
            case "welcome":
                if (msg["resync_required"]?.GetValue<bool>() != false) ResyncRequired?.Invoke();
                break;
            default:
                Notified?.Invoke(new SparkNotification(type,
                    msg["resource"]?.GetValue<string>(),
                    msg["source_device"]?.GetValue<string>(), msg));
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop != null) { try { await _loop; } catch { } }
        _cts.Dispose();
    }
}
