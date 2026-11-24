using Microsoft.AspNetCore.Builder;
using MyBackup.Core.Config;
using MyBackup.Core.Jobs;
using MyBackup.Core.Notifications;
using MyBackup.Core.Util;
using MyBackup.Web;

namespace MyBackup.Service;

/// <summary>
/// Веб-интерфейс как отдельное приложение внутри службы: если порт занят или сертификат не читается,
/// копирование продолжает работать, а ошибка уходит в журнал и Telegram.
/// </summary>
internal sealed class WebUiHostedService(
    ConfigStore store,
    JobQueue queue,
    ScheduleInfo schedule,
    HistoryStore history,
    TelegramHub telegram,
    WebPasswordStore password,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private WebApplication? _app;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Web");
        var web = store.Current.Web;
        if (!web.Enabled)
        {
            logger.LogInformation("Веб-интерфейс отключён (web.enabled = false)");
            return;
        }

        try
        {
            var ctx = new WebUiContext
            {
                Store = store, Queue = queue, Schedule = schedule, History = history, Telegram = telegram,
                Password = password, LoggerFactory = loggerFactory,
            };
            _app = WebUi.Build(ctx, builder => WebUi.ConfigureKestrel(builder, web));
            await _app.StartAsync(ct);

            logger.LogInformation("Веб-интерфейс: {Urls}", string.Join(", ", web.Urls));
            if (!password.IsSet)
                logger.LogWarning("Пароль веб-интерфейса не задан — выполните от имени администратора: MyBackup.exe web-password");
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogError(e, "Веб-интерфейс не запущен ({Urls})", string.Join(", ", web.Urls));
            try
            {
                await telegram.NotifyAsync(
                    $"⚠️ <b>{Fmt.HtmlEscape(store.Current.ServerName)}</b>: веб-интерфейс не запущен: {Fmt.HtmlEscape(e.Message)}. Копирование работает.",
                    ct);
            }
            catch (Exception)
            {
                // уже записано в журнал
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken);
            await _app.DisposeAsync();
        }

        await base.StopAsync(cancellationToken);
    }
}
