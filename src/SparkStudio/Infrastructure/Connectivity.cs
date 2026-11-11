using System.IO;
using System.Net.Http;
using System.Net.Sockets;

namespace SparkStudio.Infrastructure;

/// <summary>
/// App-wide online/offline state. Anything that hits a network error reports it here; the main view model
/// probes the server and switches the app into (or out of) offline mode.
/// </summary>
public static class Connectivity
{
    /// <summary>True while the server can't be reached and saved copies are shown read-only.</summary>
    public static bool IsOffline { get; set; }

    /// <summary>Set by the main view model: probes the server, switches modes, and returns true if it's reachable.</summary>
    public static Func<Task<bool>>? CheckAsync { get; set; }

    public static bool IsNetworkError(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: null } => true, // no HTTP response at all
        TaskCanceledException => true,                      // HttpClient timeout
        SocketException or IOException => true,
        _ => ex.InnerException != null && IsNetworkError(ex.InnerException),
    };

    /// <summary>Reports a suspected connection loss. Returns true if the server turned out to be reachable.</summary>
    public static async Task<bool> ReportAsync() => CheckAsync == null || await CheckAsync();
}
