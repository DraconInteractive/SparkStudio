using System.IO;

namespace SparkStudio.Infrastructure;

/// <summary>
/// Diagnostics for note syncing (conflicts, ETag drift, foreign revisions), written to
/// %LOCALAPPDATA%\\SparkStudio\\sync.log so a reported conflict can be traced afterwards.
/// </summary>
public static class SyncLog
{
    private const long MaxBytes = 1_000_000;

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SparkStudio", "sync.log");

    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxBytes) File.Move(LogPath, LogPath + ".1", overwrite: true);
                File.AppendAllText(LogPath, $"[{DateTime.Now:O}] {message}\n");
            }
        }
        catch (Exception) { }
    }
}
