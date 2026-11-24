namespace MyBackup.Core.Jobs;

public enum UnitStatus
{
    Ok,
    Warning,
    Failed,
    Skipped,
}

public sealed class UnitResult
{
    public string Title { get; set; } = "";
    public string SourceType { get; set; } = "";
    public UnitStatus Status { get; set; }
    public string? Message { get; set; }
    public List<string> Notes { get; set; } = [];
    public long BytesProcessed { get; set; }
    public long FilesProcessed { get; set; }
    public long DataAdded { get; set; }
    public long DataAddedPacked { get; set; }
    public int Attempts { get; set; }
    public TimeSpan Duration { get; set; }
    public string? SnapshotId { get; set; }
}

public sealed class RunReport
{
    public string JobName { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string Trigger { get; set; } = "";
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset Finished { get; set; }
    public List<UnitResult> Units { get; set; } = [];
    public List<string> Notes { get; set; } = [];

    public UnitStatus Status =>
        Units.Any(u => u.Status == UnitStatus.Failed) ? UnitStatus.Failed
        : Units.Any(u => u.Status == UnitStatus.Warning) || Notes.Count > 0 && Units.Count == 0 ? UnitStatus.Warning
        : UnitStatus.Ok;

    public long TotalUploaded => Units.Sum(u => u.DataAddedPacked);
    public int TotalRetries => Units.Sum(u => Math.Max(0, u.Attempts - 1));
}
