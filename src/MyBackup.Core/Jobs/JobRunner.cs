using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MyBackup.Core.Config;
using MyBackup.Core.Restic;
using MyBackup.Core.Sources;
using MyBackup.Core.Util;

namespace MyBackup.Core.Jobs;

public interface IJobProgress
{
    void OnUnitStarted(string unitTitle, int index, int total);
    void OnAttempt(int attempt) { }
    void OnStatus(ResticStatus status);

    /// <summary>Почему задача прервана, если её остановили вручную; null — остановка службы.</summary>
    string? CancelReason => null;
}

/// <summary>
/// Выполняет задачу: разбирает источники на единицы, для каждой — подготовка, restic с повторами
/// до конца окна задачи, уборка. Обрыв связи — штатная ситуация: restic не отправляет повторно
/// уже переданные блоки.
/// </summary>
public sealed class JobRunner
{
    private readonly AppConfig _config;
    private readonly ResticRunner _restic;
    private readonly ILogger _logger;
    private readonly RetryPolicy _retry;
    private readonly BandwidthSchedule _bandwidth;
    private readonly Func<DateTime> _now;

    public JobRunner(AppConfig config, ResticRunner restic, ILogger logger, Func<DateTime>? now = null)
    {
        _config = config;
        _restic = restic;
        _logger = logger;
        _retry = new RetryPolicy(config.Retry);
        _bandwidth = new BandwidthSchedule(config.Bandwidth);
        _now = now ?? (() => DateTime.Now);
    }

    public async Task<RunReport> RunAsync(JobConfig job, string trigger, IJobProgress? progress, CancellationToken ct)
    {
        var report = new RunReport
        {
            JobName = job.Name, ServerName = _config.ServerName, Trigger = trigger, Started = DateTimeOffset.Now,
        };
        var deadline = _now() + job.MaxDuration;
        Directory.CreateDirectory(_config.StagingDirectory);

        var vss = _config.Restic.UseVss && SystemInfo.IsAdministrator();
        if (_config.Restic.UseVss && !vss) report.Notes.Add("VSS недоступен: программа запущена без прав администратора");

        var ctx = new BackupContext { Config = _config, Logger = _logger, VssAvailable = vss };
        _logger.LogInformation("Задача {Job} запущена ({Trigger})", job.Name, trigger);

        // 1. Разбираем источники на единицы копирования.
        var units = new List<BackupUnit>();
        foreach (var sourceConfig in job.Sources.Where(s => s.Enabled))
        {
            var source = SourceFactory.Create(sourceConfig);
            try
            {
                units.AddRange(await source.PlanAsync(ctx, report.Notes, ct).ConfigureAwait(false));
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Источник {Source}: ошибка подготовки", source.Title);
                report.Units.Add(new UnitResult
                {
                    Title = source.Title, SourceType = sourceConfig.TypeName, Status = UnitStatus.Failed,
                    Message = ct.IsCancellationRequested ? $"прервано: {CancelReason(progress)}" : $"не удалось получить список: {e.Message}",
                });
            }
        }

        // 2. Копируем по одной.
        string? fatalError = null;
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            if (ct.IsCancellationRequested || fatalError is not null)
            {
                report.Units.Add(new UnitResult
                {
                    Title = unit.Title, SourceType = unit.SourceType, Status = UnitStatus.Failed,
                    Message = fatalError ?? $"не выполнено: {CancelReason(progress)}",
                });
                continue;
            }

            progress?.OnUnitStarted(unit.Title, i + 1, units.Count);
            var (result, fatal) = await ExecuteUnitAsync(unit, ctx, deadline, progress, ct).ConfigureAwait(false);
            report.Units.Add(result);
            if (fatal) fatalError = result.Message;
        }

        // 3. Правила хранения (только если хранилище не в режиме append-only).
        if (job.Retention.ApplyFromClient && fatalError is null && !ct.IsCancellationRequested
            && report.Units.Any(u => u.Status is UnitStatus.Ok or UnitStatus.Warning))
        {
            try
            {
                var r = await _restic.ForgetAsync(job.Retention, _bandwidth.LimitAt(_now()), ct).ConfigureAwait(false);
                if (!r.Success) report.Notes.Add($"очистка старых копий не выполнена: {r.ErrorMessage}");
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                report.Notes.Add($"очистка старых копий не выполнена: {e.Message}");
            }
        }

        report.Finished = DateTimeOffset.Now;
        _logger.LogInformation("Задача {Job} завершена: {Status:l}", job.Name, report.Status.ToString());
        return report;
    }

    private async Task<(UnitResult Result, bool Fatal)> ExecuteUnitAsync(BackupUnit unit, BackupContext ctx,
        DateTime deadline, IJobProgress? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new UnitResult { Title = unit.Title, SourceType = unit.SourceType };
        var fatal = false;
        var statusProgress = progress is null ? null : new SyncProgress<ResticStatus>(progress.OnStatus);

        try
        {
            ResticBackupRequest request;
            try
            {
                request = await unit.PrepareAsync(ctx, result.Notes, ct).ConfigureAwait(false);
            }
            catch (SkipUnitException e)
            {
                result.Status = UnitStatus.Skipped;
                result.Message = e.Message;
                return (result, false);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(e, "{Unit}: ошибка подготовки", unit.Title);
                result.Status = UnitStatus.Failed;
                result.Message = e.Message;
                return (result, false);
            }

            string? lastError = null;
            var unlocked = false;
            var failed = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var now = _now();
                if (now >= deadline)
                {
                    result.Status = UnitStatus.Failed;
                    result.Message = $"истекло время задачи{(lastError is null ? "" : $"; последняя ошибка: {lastError}")}";
                    break;
                }

                result.Attempts++;
                progress?.OnAttempt(result.Attempts);
                var limit = _bandwidth.LimitAt(now);
                var change = _bandwidth.NextChange(now);
                var stopAt = change is { } c && c < deadline ? c : deadline;

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(Clamp(stopAt - now));

                _logger.LogInformation("{Unit}: попытка {Attempt}, ограничение {Limit}", unit.Title, result.Attempts,
                    limit is null ? "нет" : $"{limit} КиБ/с");
                var r = await _restic.BackupAsync(request, limit, statusProgress, attemptCts.Token).ConfigureAwait(false);

                if (r.Outcome == ResticOutcome.Cancelled)
                {
                    ct.ThrowIfCancellationRequested();
                    if (change is not null && _now() >= change.Value.AddSeconds(-1) && change.Value < deadline)
                    {
                        _logger.LogInformation("{Unit}: смена ограничения скорости, перезапуск", unit.Title);
                        result.Attempts--; // не считаем это неудачей
                        continue;
                    }

                    lastError ??= "превышено время задачи";
                    continue; // следующая итерация увидит дедлайн
                }

                if (r.Outcome is ResticOutcome.Success or ResticOutcome.Partial)
                {
                    Fill(result, r.Summary);
                    if (r.Outcome == ResticOutcome.Partial)
                    {
                        result.Status = UnitStatus.Warning;
                        result.Message = $"не прочитано объектов: {r.ItemErrors.Count}";
                        result.Notes.AddRange(r.ItemErrors.Take(3).Select(e => Fmt.Truncate(e, 200)));
                    }
                    else
                    {
                        result.Status = UnitStatus.Ok;
                    }

                    break;
                }

                if (r.Outcome == ResticOutcome.Fatal)
                {
                    result.Status = UnitStatus.Failed;
                    result.Message = r.ErrorMessage;
                    fatal = true;
                    break;
                }

                // Retryable: сеть, блокировка, сбой дампа.
                lastError = r.ErrorMessage;
                failed++;
                _logger.LogWarning("{Unit}: попытка {Attempt} не удалась: {Error}", unit.Title, result.Attempts, r.ErrorMessage);

                if (r.ExitCode == 11 && !unlocked)
                {
                    unlocked = true;
                    await _restic.UnlockAsync(ct).ConfigureAwait(false); // снимает только «зависшие» блокировки
                }

                if (failed >= _retry.MaxAttempts)
                {
                    result.Status = UnitStatus.Failed;
                    result.Message = $"не удалось за {failed} попыток: {lastError}";
                    break;
                }

                var delay = _retry.GetDelay(failed);
                if (_now() + delay >= deadline)
                {
                    result.Status = UnitStatus.Failed;
                    result.Message = $"истекло время задачи; последняя ошибка: {lastError}";
                    break;
                }

                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // SqlClient при отмене бросает SqlException, а не OperationCanceledException
            result.Status = UnitStatus.Failed;
            result.Message = $"прервано: {CancelReason(progress)}";
        }
        finally
        {
            try
            {
                await unit.CleanupAsync(ctx).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "{Unit}: ошибка уборки", unit.Title);
            }

            result.Duration = sw.Elapsed;
        }

        _logger.LogInformation("{Unit}: {Status:l} {Message}", unit.Title, result.Status.ToString(), result.Message ?? "");
        return (result, fatal);
    }

    private static void Fill(UnitResult result, ResticSummary? s)
    {
        if (s is null) return;
        result.BytesProcessed = s.TotalBytesProcessed;
        result.FilesProcessed = s.TotalFilesProcessed;
        result.DataAdded = s.DataAdded;
        result.DataAddedPacked = s.DataAddedPacked > 0 ? s.DataAddedPacked : s.DataAdded;
        result.SnapshotId = s.SnapshotId;
    }

    private static string CancelReason(IJobProgress? progress) => progress?.CancelReason ?? "остановка службы";

    private static TimeSpan Clamp(TimeSpan t) =>
        t < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : t > TimeSpan.FromDays(7) ? TimeSpan.FromDays(7) : t;

    /// <summary>IProgress без SynchronizationContext: вызывает обработчик сразу.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
