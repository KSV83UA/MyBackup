using System.Text.Json;

namespace MyBackup.Core.Restic;

/// <summary>Строка JSON-вывода restic (status, summary, error, exit_error).</summary>
internal sealed class ResticMessage
{
    public string? MessageType { get; set; }

    // status
    public double PercentDone { get; set; }
    public long TotalFiles { get; set; }
    public long FilesDone { get; set; }
    public long TotalBytes { get; set; }
    public long BytesDone { get; set; }
    public double SecondsElapsed { get; set; }
    public double? SecondsRemaining { get; set; }

    // error: в разных версиях restic поле error — строка или объект {"message": "..."}
    public JsonElement? Error { get; set; }
    public string? During { get; set; }
    public string? Item { get; set; }

    public string ErrorText => Error switch
    {
        { ValueKind: JsonValueKind.String } e => e.GetString() ?? "",
        { ValueKind: JsonValueKind.Object } e when e.TryGetProperty("message", out var m) => m.GetString() ?? "",
        _ => Message ?? "",
    };

    // summary
    public long FilesNew { get; set; }
    public long FilesChanged { get; set; }
    public long FilesUnmodified { get; set; }
    public long DataAdded { get; set; }
    public long DataAddedPacked { get; set; }
    public long TotalFilesProcessed { get; set; }
    public long TotalBytesProcessed { get; set; }
    public double TotalDuration { get; set; }
    public string? SnapshotId { get; set; }

    // exit_error
    public int Code { get; set; }
    public string? Message { get; set; }

    public ResticStatus ToStatus() => new()
    {
        PercentDone = PercentDone,
        TotalBytes = TotalBytes,
        BytesDone = BytesDone,
        TotalFiles = TotalFiles,
        FilesDone = FilesDone,
        SecondsElapsed = SecondsElapsed,
        SecondsRemaining = SecondsRemaining,
    };

    public ResticSummary ToSummary() => new()
    {
        FilesNew = FilesNew,
        FilesChanged = FilesChanged,
        FilesUnmodified = FilesUnmodified,
        DataAdded = DataAdded,
        DataAddedPacked = DataAddedPacked,
        TotalFilesProcessed = TotalFilesProcessed,
        TotalBytesProcessed = TotalBytesProcessed,
        TotalDuration = TotalDuration,
        SnapshotId = SnapshotId,
    };
}

internal static class ResticJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static ResticMessage? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{') return null;
        try
        {
            return JsonSerializer.Deserialize<ResticMessage>(trimmed, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Коды выхода restic: 0 — успех, 3 — снимок неполный, 10 — нет хранилища, 11 — хранилище заблокировано, 12 — неверный пароль.</summary>
    public static ResticOutcome Classify(int exitCode) => exitCode switch
    {
        0 => ResticOutcome.Success,
        3 => ResticOutcome.Partial,
        10 or 12 => ResticOutcome.Fatal,
        _ => ResticOutcome.Retryable,
    };

    public static string DescribeExitCode(int exitCode) => exitCode switch
    {
        10 => "хранилище не найдено — выполните MyBackup init",
        11 => "хранилище заблокировано другим процессом",
        12 => "неверный пароль хранилища",
        _ => $"restic завершился с кодом {exitCode}",
    };
}
