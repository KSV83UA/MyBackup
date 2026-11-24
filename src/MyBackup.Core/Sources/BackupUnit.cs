using Microsoft.Extensions.Logging;
using MyBackup.Core.Config;
using MyBackup.Core.Restic;

namespace MyBackup.Core.Sources;

public sealed class BackupContext
{
    public required AppConfig Config { get; init; }
    public required ILogger Logger { get; init; }

    /// <summary>VSS доступен: включён в настройках и программа запущена с правами администратора.</summary>
    public required bool VssAvailable { get; init; }
}

/// <summary>Одна единица копирования = один снимок restic с отдельной строкой в отчёте.</summary>
public abstract class BackupUnit
{
    public required string Title { get; init; }
    public required string SourceType { get; init; }
    public required string Name { get; init; }

    public IReadOnlyList<string> Tags => ["mybackup", $"src:{SourceType}", $"name:{Name}"];

    /// <summary>Подготовка (например, BACKUP DATABASE в файл) и описание того, что передать restic.</summary>
    public abstract Task<ResticBackupRequest> PrepareAsync(BackupContext ctx, IList<string> notes, CancellationToken ct);

    /// <summary>Уборка после копирования (удаление временных файлов). Вызывается всегда.</summary>
    public virtual Task CleanupAsync(BackupContext ctx) => Task.CompletedTask;
}

public interface IBackupSource
{
    /// <summary>Разбивает источник на единицы копирования (например, по одной базе SQL).</summary>
    Task<IReadOnlyList<BackupUnit>> PlanAsync(BackupContext ctx, IList<string> notes, CancellationToken ct);

    /// <summary>Заголовок для отчёта, если источник не удалось даже разобрать на единицы.</summary>
    string Title { get; }
}

/// <summary>Нечего копировать — не ошибка, строка отчёта со статусом «пропущено».</summary>
public sealed class SkipUnitException(string message) : Exception(message);

public static class SourceFactory
{
    public static IBackupSource Create(SourceConfig config) => config switch
    {
        FileBase1CSourceConfig c => new FileBase1CSource(c),
        MsSqlSourceConfig c => new MsSqlSource(c),
        PostgreSqlSourceConfig c => new PostgreSqlSource(c),
        UserProfilesSourceConfig c => new UserProfilesSource(c),
        PathsSourceConfig c => new PathsSource(c),
        OneCServiceSourceConfig c => new OneCServiceSource(c),
        OneCDumpSourceConfig c => new OneCDumpSource(c),
        _ => throw new NotSupportedException($"Неизвестный тип источника: {config.GetType().Name}"),
    };
}

/// <summary>Источник из одной единицы копирования.</summary>
public abstract class SingleUnitSource : IBackupSource
{
    public abstract string Title { get; }
    protected abstract BackupUnit CreateUnit();

    public Task<IReadOnlyList<BackupUnit>> PlanAsync(BackupContext ctx, IList<string> notes, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BackupUnit>>([CreateUnit()]);
}

internal static class PathHelper
{
    public static string Normalize(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
}
