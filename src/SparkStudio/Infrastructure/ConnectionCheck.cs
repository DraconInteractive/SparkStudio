using System.Net;
using System.Net.Http;
using System.Text;
using Spark.Api;

namespace SparkStudio.Infrastructure;

/// <summary>Outcome of checking a server URL + device ID + token with POST /auth/verify.</summary>
public sealed record ConnectionCheck(bool Ok, string Headline, string Details, AuthInfo? Auth, IReadOnlyList<string> MissingPermissions)
{
    public static bool IsPlaintext(Uri uri) => uri.Scheme == Uri.UriSchemeHttp;

    /// <summary>Verifies credentials against the server. Never throws.</summary>
    public static async Task<ConnectionCheck> RunAsync(SparkClient client, string deviceId, bool hasToken)
    {
        AuthInfo auth;
        try
        {
            auth = await client.VerifyAsync(deviceId);
        }
        catch (SparkApiException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            return Fail(hasToken ? "Token rejected" : "This server requires a device token",
                hasToken
                    ? "The server says the token is missing, invalid or revoked. Ask an administrator for a new device token (Admin → Credentials)."
                    : "Open connection settings (the key button) and enter this device's ID and token.");
        }
        catch (SparkApiException ex) when (ex.Status == HttpStatusCode.Forbidden)
        {
            return Fail(ex.Code == "device_identity_mismatch" ? "Device ID doesn't match the token" : "Token not accepted",
                ex.Code == "device_identity_mismatch"
                    ? $"This token was issued for a different device ID than \"{deviceId}\". Use the device ID the administrator issued it for."
                    : $"The server refused this token ({ex.Code ?? "forbidden"}). Administrator tokens can't be used by apps; ask for a device token.");
        }
        catch (SparkApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            // Servers that predate /auth/verify are open-mode only.
            return new ConnectionCheck(true, "Connected", "This server doesn't support device authentication.", null, Array.Empty<string>());
        }
        catch (HttpRequestException ex)
        {
            return Fail("Can't reach the server", ex.InnerException?.Message ?? ex.Message);
        }
        catch (TaskCanceledException)
        {
            return Fail("Can't reach the server", "The request timed out.");
        }
        catch (Exception ex)
        {
            return Fail("Connection failed", ex.Message);
        }

        if (!auth.Authenticated)
        {
            var note = hasToken ? " It doesn't require a token, so the token isn't used." : "";
            return new ConnectionCheck(true, "Open server (no authentication)", "Anyone on the network can use this server." + note,
                auth, Array.Empty<string>());
        }

        var missing = SparkPermissions.Used.Where(p => !auth.Permissions.Contains(p.Permission)).ToList();
        var sb = new StringBuilder();
        sb.Append($"Authenticated as {auth.DeviceId}. Permissions: {string.Join(", ", auth.Permissions.OrderBy(p => p))}.");
        if (missing.Count > 0)
        {
            sb.Append("\n\nMissing (these features won't work):");
            foreach (var (perm, purpose) in missing) sb.Append($"\n  • {perm}: {purpose}");
        }
        return new ConnectionCheck(true, $"Connected as {auth.DeviceId}", sb.ToString(), auth, missing.Select(m => m.Permission).ToList());
    }

    private static ConnectionCheck Fail(string headline, string details) =>
        new(false, headline, details, null, Array.Empty<string>());
}
