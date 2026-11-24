using MyBackup.Core.Config;
using MyBackup.Core.Jobs;
using MyBackup.Core.Notifications;
using MyBackup.Core.Util;

namespace MyBackup.Service;

/// <summary>
/// Приём команд Telegram (long polling). Команды выполняются только из разрешённых чатов; остальные чаты
/// запоминаются, чтобы их можно было разрешить в веб-интерфейсе. Включение бота и смена токена
/// подхватываются без перезапуска службы.
/// </summary>
internal sealed class TelegramBotService(
    ConfigStore store,
    TelegramHub hub,
    JobQueue queue,
    HistoryStore history,
    ScheduleInfo schedule,
    ILogger<TelegramBotService> logger) : BackgroundService
{
    private static readonly TimeSpan MinDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdleCheck = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string? activeKey = null;
        long offset = 0;
        var delay = MinDelay;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = hub.GetClient(out var key);
                if (client is null || !store.Current.Telegram.EnableCommands)
                {
                    activeKey = null;
                    await Task.Delay(IdleCheck, ct);
                    continue;
                }

                try
                {
                    if (key != activeKey)
                    {
                        offset = await SkipPendingUpdatesAsync(client, ct);
                        activeKey = key;
                        logger.LogInformation("Telegram: бот принимает команды");
                    }

                    // Короткий long polling, чтобы смена настроек подхватывалась быстро.
                    var updates = await client.GetUpdatesAsync(offset, 25, ct);
                    delay = MinDelay;
                    foreach (var update in updates)
                    {
                        offset = Math.Max(offset, update.UpdateId + 1);
                        if (update.Message is { } message) await HandleAsync(client, message, ct);
                    }
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("Telegram недоступен: {Error}. Повтор через {Delay}", e.Message, delay);
                    await Task.Delay(delay, ct);
                    delay = delay * 2 > MaxDelay ? MaxDelay : delay * 2;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // остановка службы
        }
    }

    /// <summary>
    /// Команды, присланные пока бот не работал, не выполняем: «/run» трёхчасовой давности никто не ждёт.
    /// Но чат последнего сообщения запоминаем — так можно разрешить его сразу после ввода токена.
    /// </summary>
    private async Task<long> SkipPendingUpdatesAsync(TelegramClient client, CancellationToken ct)
    {
        var pending = await client.GetUpdatesAsync(-1, 0, ct);
        foreach (var u in pending)
            if (u.Message is { } m && !store.Current.Telegram.AllowedChatIds.Contains(m.Chat.Id))
                hub.RecordUnknownChat(m);
        return pending.Count > 0 ? pending[^1].UpdateId + 1 : 0;
    }

    private async Task HandleAsync(TelegramClient client, TgMessage message, CancellationToken ct)
    {
        var config = store.Current;
        if (!config.Telegram.AllowedChatIds.Contains(message.Chat.Id))
        {
            hub.RecordUnknownChat(message);
            logger.LogWarning("Telegram: сообщение из неразрешённого чата {ChatId} (@{User}) — его можно разрешить в веб-интерфейсе",
                message.Chat.Id, message.From?.Username);
            return;
        }

        var ctx = new BotContext
        {
            ServerName = config.ServerName,
            Jobs = config.Jobs,
            Current = () => queue.Current?.Snapshot(),
            NextRun = schedule.NextRun,
            History = history.ReadLast,
            Enqueue = queue.TryEnqueue,
            Cancel = queue.CancelCurrent,
        };

        string? reply;
        try
        {
            reply = BotCommands.Handle(message.Text, ctx, message.From?.Username ?? message.Chat.Id.ToString());
        }
        catch (Exception e)
        {
            logger.LogError(e, "Telegram: ошибка обработки команды {Text}", message.Text);
            reply = "Ошибка: " + Fmt.HtmlEscape(e.Message);
        }

        if (reply is null) return;
        logger.LogInformation("Telegram: команда {Text} из чата {ChatId}", message.Text, message.Chat.Id);
        try
        {
            await client.SendMessageAsync(message.Chat.Id, reply, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Telegram: не удалось ответить: {Error}", e.Message);
        }
    }
}
