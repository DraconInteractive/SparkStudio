using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Spark.Api;

namespace Spark.Integration.Tests;

/// <summary>Severity helpers and request shapes, checked against a fake HTTP server (no SPARK instance needed).</summary>
public class TaskSeverityTests
{
    [Fact]
    public void Levels_match_the_api_contract_in_order()
    {
        Assert.Equal(new[] { "Very Low", "Low", "Normal", "High", "Very High", "Urgent" }, TaskSeverity.Levels);
        Assert.True(TaskSeverity.Rank("Urgent") > TaskSeverity.Rank("Very High"));
        Assert.Equal(TaskSeverity.Rank("Normal"), TaskSeverity.Rank(null)); // pre-1.48 tasks rank as Normal
        Assert.False(TaskSeverity.IsValid("high")); // case-sensitive on the wire
    }

    /// <summary>Serves one canned JSON response per request and records each request body.</summary>
    private sealed class FakeHttp : IDisposable
    {
        private readonly HttpListener _listener = new();
        public Uri BaseUri { get; }
        public List<(string Method, string Path, JsonObject? Body)> Requests { get; } = new();

        public FakeHttp(string responseJson)
        {
            var port = FreePort();
            BaseUri = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(BaseUri.ToString());
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    var text = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                    lock (Requests) Requests.Add((ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath,
                        text.Length > 0 ? JsonNode.Parse(text) as JsonObject : null));
                    var bytes = Encoding.UTF8.GetBytes(responseJson);
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
        }

        private static int FreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose() => _listener.Close();
    }

    private const string TaskJson = "{\"id\":\"t\",\"board_id\":\"b\",\"column_id\":\"c\",\"name\":\"n\",\"description\":\"\",\"severity\":\"High\"}";

    [Fact]
    public async Task Severity_is_omitted_unless_given_so_older_servers_accept_the_request()
    {
        using var server = new FakeHttp(TaskJson);
        using var client = new SparkClient(server.BaseUri);

        await client.CreateTaskAsync("p", "b", "c", "n", "");
        await client.CreateTaskAsync("p", "b", "c", "n", "", severity: "Urgent");
        await client.UpdateTaskAsync("p", "b", "t", columnId: "c2");
        var updated = await client.UpdateTaskAsync("p", "b", "t", severity: "Very Low");

        Assert.False(server.Requests[0].Body!.ContainsKey("severity"));
        Assert.Equal("Urgent", server.Requests[1].Body!["severity"]!.ToString());
        Assert.False(server.Requests[2].Body!.ContainsKey("severity"));
        Assert.Equal(new[] { "severity" }, server.Requests[3].Body!.Select(kv => kv.Key));
        Assert.Equal("High", updated!.Severity); // parsed from the response
    }

    [Fact]
    public async Task Contract_detects_task_severity_from_the_task_endpoint_output()
    {
        const string api = """
            {"api_version":"1.48.0","authentication":"none","endpoints":[
              {"method":"GET","path":"/projects/{project_id}/boards/{board_id}/tasks/{task_id}",
               "output":{"body":{"id":"task-uuid","severity":"Normal"}}}]}
            """;
        using var server = new FakeHttp(api);
        using var client = new SparkClient(server.BaseUri);
        Assert.True((await client.GetContractAsync())!.SupportsTaskSeverity);
    }

    [Fact]
    public async Task Contract_without_severity_reports_unsupported()
    {
        const string api = """
            {"api_version":"1.46.0","authentication":"none","endpoints":[
              {"method":"GET","path":"/projects/{project_id}/boards/{board_id}/tasks/{task_id}",
               "output":{"body":{"id":"task-uuid"}}}]}
            """;
        using var server = new FakeHttp(api);
        using var client = new SparkClient(server.BaseUri);
        Assert.False((await client.GetContractAsync())!.SupportsTaskSeverity);
    }
}
