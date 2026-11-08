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
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
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

/// <summary>Text content of a file together with the ETag required to save it.</summary>
public sealed record TextContent(string Text, string ETag);
