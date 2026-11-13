using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Spark.Api;
using SparkStudio.Views;

namespace SparkStudio.Infrastructure;

public static class Ui
{
    public static Window? Owner => Application.Current?.MainWindow;

    public static void Error(string action, Exception ex)
    {
        var msg = ex switch
        {
            SparkApiException { Status: System.Net.HttpStatusCode.Unauthorized } =>
                "The server rejected this device's token (missing, invalid or revoked). Update it in connection settings (key button in the top bar).",
            SparkApiException { Status: System.Net.HttpStatusCode.Forbidden } forbidden =>
                $"This device isn't allowed to do that ({forbidden.Code ?? "forbidden"}). Its token may be missing a permission; hover the connection status to see what it has.",
            SparkApiException api => api.Code switch
            {
                "column_not_empty" => "That column still has tasks. Move or delete them first.",
                "conflict" or "name_conflict" => "Something with that name already exists.",
                _ => api.Message,
            },
            HttpRequestException => "Could not reach the SPARK server. " + ex.Message,
            TaskCanceledException => "The request to the SPARK server timed out.",
            _ => ex.Message,
        };
        MessageBox.Show(Owner!, msg, action, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Explains that an action needs the server, and returns true when offline (so callers can stop).</summary>
    public static bool BlockedOffline(string action)
    {
        if (!Connectivity.IsOffline) return false;
        MessageBox.Show(Owner!, "You're offline, so files are read-only. This will work again once the connection is back.",
            action, MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    public static void Info(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static string? Prompt(string title, string label, string initial = "") =>
        InputDialog.Show(Owner, title, label, initial);

    /// <summary>Runs an async action and reports failures instead of crashing.</summary>
    public static async Task<bool> Try(string action, Func<Task> work)
    {
        try
        {
            await work();
            return true;
        }
        catch (Exception ex)
        {
            // A lost connection switches the app to offline mode (with its own banner) instead of a popup.
            if (Connectivity.IsNetworkError(ex) && (Connectivity.IsOffline || !await Connectivity.ReportAsync())) return false;
            Error(action, ex);
            return false;
        }
    }

    public static void Post(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);
}

/// <summary>Coalesces bursts of calls into one, after a quiet period, on the UI thread.</summary>
public sealed class Debouncer
{
    private readonly DispatcherTimer _timer;
    private Func<Task>? _action;

    public Debouncer(TimeSpan delay)
    {
        _timer = new DispatcherTimer { Interval = delay };
        _timer.Tick += async (_, _) =>
        {
            _timer.Stop();
            var a = _action;
            _action = null;
            if (a != null) await a();
        };
    }

    public void Run(Func<Task> action)
    {
        _action = action;
        _timer.Stop();
        _timer.Start();
    }

    public void Cancel()
    {
        _timer.Stop();
        _action = null;
    }
}
