using Cronos;
using MyBackup.Core.Config;
using MyBackup.Core.Jobs;

namespace MyBackup.Service;

/// <summary>
/// Ставит задачи в очередь по расписанию cron. Проверка раз в 20 секунд: если сервер спал
/// или был выключен в момент запуска, задача стартует сразу после пробуждения (один раз).
/// После сохранения настроек расписание пересчитывается без перезапуска службы.
/// </summary>
internal sealed class SchedulerService(
    ConfigStore store,
    JobQueue queue,
    ScheduleInfo schedule,
    ILogger<SchedulerService> logger) : BackgroundService
{
    private sealed record Entry(JobConfig Job, CronExpression Cron);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        AppConfig? applied = null;
        var entries = new List<Entry>();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            do
            {
                var config = store.Current;
                if (!ReferenceEquals(config, applied))
                {
                    entries = Apply(config, entries);
                    applied = config;
                }

                var now = DateTimeOffset.Now;
                foreach (var (job, cron) in entries)
                {
                    var at = schedule.NextRun(job.Name);
                    if (at is null || at > now) continue;

                    if (queue.TryEnqueue(job.Name, "по расписанию"))
                        logger.LogInformation("Задача {Job} поставлена в очередь по расписанию", job.Name);
                    else
                        logger.LogWarning("Задача {Job} ещё выполняется — запуск по расписанию пропущен", job.Name);

                    schedule.Set(job.Name, cron.GetNextOccurrence(now, TimeZoneInfo.Local));
                }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // остановка службы
        }
    }

    /// <summary>Новый список задач; у задач с прежним расписанием время следующего запуска не сбрасывается.</summary>
    private List<Entry> Apply(AppConfig config, List<Entry> previous)
    {
        var result = new List<Entry>();
        schedule.Retain(config.Jobs.Select(j => j.Name));
        foreach (var job in config.Jobs)
        {
            if (!job.Enabled)
            {
                schedule.Set(job.Name, null);
                schedule.SetError(job.Name, null);
                continue;
            }

            CronExpression cron;
            try
            {
                cron = CronExpression.Parse(job.Schedule);
            }
            catch (CronFormatException e)
            {
                schedule.Set(job.Name, null);
                schedule.SetError(job.Name, e.Message);
                logger.LogError("Задача {Job}: неверное расписание {Schedule} — по расписанию не запускается", job.Name, job.Schedule);
                continue;
            }

            schedule.SetError(job.Name, null);
            var old = previous.FirstOrDefault(e => e.Job.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase));
            if (old is null || old.Job.Schedule != job.Schedule || schedule.NextRun(job.Name) is null)
            {
                var at = cron.GetNextOccurrence(DateTimeOffset.Now, TimeZoneInfo.Local);
                schedule.Set(job.Name, at);
                logger.LogInformation("Задача {Job}: следующий запуск {At:dd.MM.yyyy HH:mm}", job.Name, at?.ToLocalTime());
            }

            result.Add(new Entry(job, cron));
        }

        return result;
    }
}
