namespace MyBackup.Core.Restic;

/// <summary>Команда, вывод которой restic сохраняет как файл (--stdin-from-command).</summary>
public sealed record StdinCommand(string FileName, string Command, IReadOnlyList<string> Arguments);

/// <summary>Один запуск restic backup = один снимок.</summary>
public sealed class ResticBackupRequest
{
    public IReadOnlyList<string> Paths { get; init; } = [];
    public StdinCommand? Stdin { get; init; }
    public IReadOnlyList<string> IExcludes { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public bool UseVss { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

public sealed class ResticStatus
{
    public double PercentDone { get; init; }
    public long TotalBytes { get; init; }
    public long BytesDone { get; init; }
    public long TotalFiles { get; init; }
    public long FilesDone { get; init; }
    public double SecondsElapsed { get; init; }
    public double? SecondsRemaining { get; init; }
}

public sealed class ResticSummary
{
    public long FilesNew { get; init; }
    public long FilesChanged { get; init; }
    public long FilesUnmodified { get; init; }
    public long DataAdded { get; init; }
    public long DataAddedPacked { get; init; }
    public long TotalFilesProcessed { get; init; }
    public long TotalBytesProcessed { get; init; }
    public double TotalDuration { get; init; }
    public string? SnapshotId { get; init; }
}

public enum ResticOutcome
{
    /// <summary>Снимок создан полностью.</summary>
    Success,

    /// <summary>Снимок создан, но часть файлов не прочитана (код 3).</summary>
    Partial,

    /// <summary>Ошибка, которую стоит повторить: сеть, блокировка хранилища, сбой дампа.</summary>
    Retryable,

    /// <summary>Повтор не поможет: хранилища нет или неверный пароль.</summary>
    Fatal,

    /// <summary>Запуск прерван (смена режима скорости, окно задачи, остановка службы).</summary>
    Cancelled,
}

public sealed class ResticBackupResult
{
    public int ExitCode { get; init; }
    public ResticOutcome Outcome { get; init; }
    public ResticSummary? Summary { get; init; }
    public IReadOnlyList<string> ItemErrors { get; init; } = [];
    public string? ErrorMessage { get; init; }
}

public sealed class ResticCommandResult
{
    public int ExitCode { get; init; }
    public string Stdout { get; init; } = "";
    public IReadOnlyList<string> Stderr { get; init; } = [];
    public string? ErrorMessage { get; init; }
    public bool Success => ExitCode == 0;
}

public sealed class ResticSnapshot
{
    public string Id { get; set; } = "";
    public string ShortId { get; set; } = "";
    public DateTimeOffset Time { get; set; }
    public string? Hostname { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<string> Paths { get; set; } = [];
    public ResticSnapshotSummary? Summary { get; set; }
}

/// <summary>Файл или папка внутри снимка (restic ls --json).</summary>
public sealed class ResticNode
{
    public string Name { get; set; } = "";

    /// <summary>dir, file, symlink…</summary>
    public string Type { get; set; } = "";

    /// <summary>Путь внутри снимка: /C/Users/ivanov/Desktop.</summary>
    public string Path { get; set; } = "";

    public long? Size { get; set; }
    public DateTimeOffset? Mtime { get; set; }
}

public sealed class ResticSnapshotSummary
{
    public long DataAdded { get; set; }
    public long DataAddedPacked { get; set; }
    public long TotalBytesProcessed { get; set; }
    public long TotalFilesProcessed { get; set; }
}
