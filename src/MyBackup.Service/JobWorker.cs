using MyBackup.Core.Config;
using MyBackup.Core.Jobs;
using MyBackup.Core.Notifications;
using MyBackup.Core.Restic;
using MyBackup.Core.Util;

namespace MyBackup.Service;

/// <summary>Берёт задачи из очереди, выполняет, пишет историю и отправляет отчёт.</summary>
internal sealed class JobWorker(
    ConfigStore store,
    JobQueue queue,
    HistoryStore history,
    INotifier notifier,
    ILoggerFactory loggerFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Jobs");
        try
        {
            await foreach (var request in queue.ReadAllAsync(ct))
            {
                // Настройки берём на момент запуска: правка в веб-интерфейсе во время копирования на него не влияет.
                var config = store.Current;
                var job = config.Jobs.FirstOrDefault(j => j.Name.Equals(request.JobName, StringComparison.OrdinalIgnoreCase));
                if (job is null)
                {
                    queue.Complete(request.JobName);
                    continue;
                }

                var status = new JobStatus(job.Name, request.Trigger);
                var jobToken = queue.BeginRun(status, ct);
                try
                {
                    var errors = ConfigLoader.ValidateForRun(config, job);
                    RunReport report;
                    if (errors.Count > 0)
                    {
                        logger.LogError("Задача {Job} не запущена: ошибки в настройках: {Errors}", job.Name, string.Join("; ", errors));
                        report = ConfigErrorReport(config, job, request.Trigger, errors);
                    }
                    else
                    {
                        if (config.Telegram.NotifyOnStart)
                            await notifier.NotifyAsync(
                                $"▶️ <b>{Fmt.HtmlEscape(config.ServerName)}</b>: запуск {Fmt.HtmlEscape(job.Name)} ({Fmt.HtmlEscape(request.Trigger)})",
                                ct);

                        var restic = new ResticRunner(ResticSettings.FromConfig(config), logger);
                        report = await new JobRunner(config, restic, logger).RunAsync(job, request.Trigger, status, jobToken);
                    }

                    history.Append(report);

                    // При остановке службы даём на отправку отчёта не больше 10 секунд.
                    using var notifyCts = new CancellationTokenSource(ct.IsCancellationRequested
                        ? TimeSpan.FromSeconds(10)
                        : TimeSpan.FromMinutes(10));
                    await notifier.NotifyAsync(ReportFormatter.ToTelegramHtml(report), notifyCts.Token);
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    logger.LogError(e, "Задача {Job} завершилась с внутренней ошибкой", job.Name);
                    await TryNotifyAsync(
                        $"❌ <b>{Fmt.HtmlEscape(config.ServerName)}</b>: задача {Fmt.HtmlEscape(job.Name)} — внутренняя ошибка: {Fmt.HtmlEscape(e.Message)}",
                        ct);
                }
                finally
                {
                    queue.EndRun(job.Name);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // остановка службы
        }
    }

    private static RunReport ConfigErrorReport(AppConfig config, JobConfig job, string trigger, IReadOnlyList<string> errors)
    {
        var now = DateTimeOffset.Now;
        return new RunReport
        {
            JobName = job.Name, ServerName = config.ServerName, Trigger = trigger, Started = now, Finished = now,
            Units =
            [
                new UnitResult
                {
                    Title = "Настройки", SourceType = "config", Status = UnitStatus.Failed,
                    Message = "задача не запущена — ошибки в настройках", Notes = errors.Take(5).ToList(),
                },
            ],
        };
    }

    private async Task TryNotifyAsync(string html, CancellationToken ct)
    {
        try
        {
            await notifier.NotifyAsync(html, ct);
        }
        catch (Exception)
        {
            // уже записано в журнал
        }
    }
}
