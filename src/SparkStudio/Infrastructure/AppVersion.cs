using System.Reflection;

namespace SparkStudio.Infrastructure;

/// <summary>The app's release version (the &lt;Version&gt; in SparkStudio.csproj).</summary>
public static class AppVersion
{
    public static Version Current { get; } = typeof(AppVersion).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>e.g. "v0.0.1".</summary>
    public static string Display => $"v{Current.Major}.{Current.Minor}.{Math.Max(0, Current.Build)}";
}
