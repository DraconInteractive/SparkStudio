using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SparkStudio;

public partial class App : Application
{
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SparkStudio", "error.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log(args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>Keeps the app (and any unsaved note) alive after an unexpected UI error.</summary>
    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        MessageBox.Show(MainWindow!, $"Something went wrong: {e.Exception.Message}\n\nDetails were written to {LogPath}.",
            "SPARK Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch (Exception) { }
    }
}
