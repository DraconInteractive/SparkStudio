using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Spark.Api;

namespace Spark.Integration.Tests;

/// <summary>Protocol tests against a fake notification server on loopback (no SPARK instance needed).</summary>
public class NotificationClientTests
{
    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Accepted;

        public FakeServer() => _listener.Start();

        /// <summary>Accepts one connection, returns the hello it sent, then replies with <paramref name="reply"/> lines.</summary>
        public async Task<JsonObject> ServeOnceAsync(params string[] reply)
        {
            using var tcp = await _listener.AcceptTcpClientAsync();
            Interlocked.Increment(ref Accepted);
            var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var hello = (JsonObject)JsonNode.Parse((await reader.ReadLineAsync())!)!;
            foreach (var line in reply)
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await stream.WriteAsync(bytes);
            }
            if (reply.Length > 0 && reply[^1].Contains("\"error\"")) tcp.Close(); // server closes after an auth error
            else await Task.Delay(300);
            return hello;
        }

        /// <summary>Counts any further connection attempts during <paramref name="window"/>.</summary>
        public async Task<int> CountReconnectsAsync(TimeSpan window)
        {
            int n = 0;
            using var cts = new CancellationTokenSource(window);
            try
            {
                while (true)
                {
                    using var tcp = await _listener.AcceptTcpClientAsync(cts.Token);
                    n++;
                }
            }
            catch (OperationCanceledException) { }
            return n;
        }

        public void Dispose() => _listener.Stop();
    }

    private const string Welcome = "{\"type\":\"welcome\",\"protocol_version\":2,\"resync_required\":true,\"sequence\":1,\"resources\":[]}";

    [Fact]
    public async Task Token_hello_carries_raw_token_and_device_id()
    {
        using var server = new FakeServer();
        await using var client = new NotificationClient(new NotificationOptions("127.0.0.1", server.Port, "studio-pc", Token: "spark1_secret"));
        var connected = new TaskCompletionSource();
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();

        var hello = await server.ServeOnceAsync(Welcome).WaitAsync(TimeSpan.FromSeconds(10));
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("hello", hello["type"]!.ToString());
        Assert.Equal("studio-pc", hello["device_id"]!.ToString());
        Assert.Equal("spark1_secret", hello["token"]!.ToString()); // raw, no "Bearer " prefix
    }

    [Fact]
    public void Open_mode_hello_has_no_token_field()
    {
        var hello = new NotificationClient(new NotificationOptions("h", 1, "studio-pc")).BuildHello();
        Assert.False(hello.ContainsKey("token"));
        Assert.Equal("studio-pc", hello["device_id"]!.ToString());
    }

    [Theory]
    [InlineData("unauthorized")]
    [InlineData("forbidden")]
    public async Task Auth_errors_stop_the_client_instead_of_reconnecting(string code)
    {
        using var server = new FakeServer();
        await using var client = new NotificationClient(new NotificationOptions("127.0.0.1", server.Port, "studio-pc", Token: "bad"));
        var rejected = new TaskCompletionSource<string>();
        client.Rejected += c => rejected.TrySetResult(c);
        client.Start();

        await server.ServeOnceAsync($"{{\"type\":\"error\",\"error\":\"{code}\"}}").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(code, await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, await server.CountReconnectsAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Dropped_connection_reconnects_with_token_and_cursor()
    {
        using var server = new FakeServer();
        await using var client = new NotificationClient(new NotificationOptions("127.0.0.1", server.Port, "studio-pc", Token: "spark1_secret"));
        client.Start();

        await server.ServeOnceAsync(Welcome, "{\"type\":\"file.created\",\"resource\":\"/files/x\",\"sequence\":7}")
            .WaitAsync(TimeSpan.FromSeconds(10));
        var resume = await server.ServeOnceAsync(Welcome).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal("spark1_secret", resume["token"]!.ToString()); // token supplied again on every connection
        Assert.Equal(7UL, resume["after_sequence"]!.GetValue<ulong>());
    }
}
