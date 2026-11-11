using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Spark.Api;

public sealed record SparkNotification(string Type, string? Resource, string? SourceDevice, JsonObject Raw);

/// <summary>Where and as whom to connect to the SPARK TCP notification stream.</summary>
/// <param name="Token">Raw device token (no "Bearer " prefix) for token-mode servers; null in open mode.</param>
/// <param name="UseTls">True when the contract advertises transport "tcp+tls" (certificate and hostname are verified).</param>
public sealed record NotificationOptions(string Host, int Port, string DeviceId, string? Token = null, bool UseTls = false);

/// <summary>
/// Listens to the SPARK TCP notification stream (newline-delimited JSON) and raises events.
/// Notifications are hints only: consumers re-read state over REST. The cursor is kept in memory,
/// so a fresh app start always resyncs from REST. Authentication failures stop the client instead of
/// retrying, as the protocol requires new credentials before reconnecting.
/// </summary>
public sealed class NotificationClient : IAsyncDisposable
{
    /// <summary>Server error codes after which reconnecting with the same hello is pointless.</summary>
    private static readonly HashSet<string> FatalErrors = new() { "unauthorized", "forbidden", "invalid_hello" };

    private readonly NotificationOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private ulong? _lastSequence;

    public event Action<SparkNotification>? Notified;
    /// <summary>Raised after (re)connecting when the client must refresh all cached state.</summary>
    public event Action? ResyncRequired;
    public event Action<bool>? ConnectionChanged;
    /// <summary>Raised with the server's error code (e.g. "unauthorized") when the server refuses this client; it then stops.</summary>
    public event Action<string>? Rejected;

    public NotificationClient(NotificationOptions options) => _options = options;

    public NotificationClient(string host, int port, string deviceId) : this(new NotificationOptions(host, port, deviceId)) { }

    /// <summary>The hello frame for this client (exposed for tests). Includes the token only when one is set.</summary>
    public JsonObject BuildHello()
    {
        var hello = new JsonObject { ["type"] = "hello", ["device_id"] = _options.DeviceId };
        if (!string.IsNullOrEmpty(_options.Token)) hello["token"] = _options.Token;
        if (_lastSequence is { } seq) hello["after_sequence"] = seq;
        return hello;
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        var rng = new Random();
        double backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            string? rejection = null;
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(_options.Host, _options.Port, ct);
                Stream stream = tcp.GetStream();
                if (_options.UseTls)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    // Default validation: trusted chain + hostname; SNI is sent for TargetHost.
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _options.Host }, ct);
                    stream = ssl;
                }
                await using var _ = stream;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

                await writer.WriteLineAsync(BuildHello().ToJsonString().AsMemory(), ct);

                bool welcomed = false;
                while (!ct.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(TimeSpan.FromSeconds(120));
                    var line = await reader.ReadLineAsync(idle.Token);
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (JsonNode.Parse(line) is not JsonObject msg) continue;

                    var type = msg["type"]?.GetValue<string>() ?? "";
                    if (type == "error")
                    {
                        var code = msg["error"]?.ToString() ?? "error";
                        if (FatalErrors.Contains(code))
                        {
                            rejection = code;
                            break;
                        }
                        continue;
                    }
                    if (type == "welcome" && !welcomed)
                    {
                        welcomed = true;
                        ConnectionChanged?.Invoke(true);
                        backoff = 1;
                    }
                    await HandleAsync(type, msg, writer, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) { /* fall through to reconnect */ }

            ConnectionChanged?.Invoke(false);
            if (rejection != null)
            {
                Rejected?.Invoke(rejection);
                return; // needs new credentials or configuration; don't hammer the server
            }
            var delay = Math.Min(30, backoff) * (0.5 + rng.NextDouble() * 0.5);
            backoff *= 2;
            try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task HandleAsync(string type, JsonObject msg, StreamWriter writer, CancellationToken ct)
    {
        if (msg["sequence"] is JsonValue sv && sv.TryGetValue<ulong>(out var seq)) _lastSequence = seq;

        switch (type)
        {
            case "ping":
                await writer.WriteLineAsync("{\"type\":\"pong\"}".AsMemory(), ct);
                break;
            case "pong":
            case "checkpoint":
            case "catchup_complete":
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
