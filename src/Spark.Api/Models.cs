namespace Spark.Api;

public sealed record Project
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Directory { get; init; } = "";
    public string DriveId { get; init; } = "";
    public string DriveLetter { get; init; } = "";
    public bool DriveOnline { get; init; }
    public string? GithubUrl { get; init; }
    public string? ParentId { get; init; }
    /// <summary>Immediate child IDs on shallow reads.</summary>
    public List<string> Subprojects { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record BoardSummary
{
    public string Id { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record Board
{
    public string Id { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string Name { get; init; } = "";
    public List<BoardColumn> Columns { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record BoardColumn
{
    public string Id { get; init; } = "";
    public string BoardId { get; init; } = "";
    public string Name { get; init; } = "";
    public List<BoardTask> Tasks { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record BoardTask
{
    public string Id { get; init; } = "";
    public string BoardId { get; init; } = "";
    public string ColumnId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>One of <see cref="TaskSeverity.Levels"/> (API 1.48+); null on servers without task severity.</summary>
    public string? Severity { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Task severity levels (API 1.48+). Values are case-sensitive on the wire.</summary>
public static class TaskSeverity
{
    public const string Normal = "Normal";

    /// <summary>All levels, least to most severe.</summary>
    public static readonly IReadOnlyList<string> Levels = new[] { "Very Low", "Low", "Normal", "High", "Very High", "Urgent" };

    public static bool IsValid(string? value) => value != null && Levels.Contains(value);

    /// <summary>0 (Very Low) .. 5 (Urgent); tasks without a severity rank as Normal.</summary>
    public static int Rank(string? value)
    {
        var i = value == null ? -1 : ((IList<string>)Levels).IndexOf(value);
        return i < 0 ? 2 : i;
    }
}

public sealed record SparkFile
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Directory { get; init; } = "/";
    public long SizeBytes { get; init; }
    public string ContentType { get; init; } = "";
    public string ContentRevision { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string DriveId { get; init; } = "";
    public string DriveLetter { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Logical path including the file name, e.g. /projects/SPARK/notes.md.</summary>
    public string FullPath => Directory == "/" ? "/" + Name : Directory + "/" + Name;
}

/// <summary>Result of POST /auth/verify.</summary>
public sealed record AuthInfo
{
    /// <summary>False on open-mode servers (no credentials enforced).</summary>
    public bool Authenticated { get; init; }
    public string? DeviceId { get; init; }
    public List<string> Permissions { get; init; } = new();
    /// <summary>"open" on open-mode servers.</summary>
    public string? AuthMode { get; init; }

    public bool IsOpenMode => !Authenticated && AuthMode == "open";
}

/// <summary>The parts of the /api contract the client depends on.</summary>
public sealed record ServerContract(
    string ApiVersion,
    string Authentication,
    string NotificationTransport,
    string? NotificationHost,
    int? NotificationPort,
    bool SupportsFileRename,
    bool SupportsTaskSeverity = false)
{
    public bool IsTokenMode => Authentication == "bearer";
    public bool NotificationsUseTls => NotificationTransport == "tcp+tls";
}

/// <summary>Permissions this app needs, grouped by what they unlock (token mode).</summary>
public static class SparkPermissions
{
    public static readonly (string Permission, string Purpose)[] Used =
    {
        ("projects.read", "list projects"),
        ("files.read", "read notes and files"),
        ("files.write", "save, create, rename and delete notes"),
        ("boards.read", "view boards"),
        ("boards.write", "edit boards and tasks"),
        ("projects.write", "create projects and edit descriptions"),
        ("notifications.read", "live updates"),
        ("discovery.read", "read the API contract (server version, native rename, notification transport)"),
    };
}

/// <summary>Text content of a file together with the ETag required to save it.</summary>
public sealed record TextContent(string Text, string ETag);
