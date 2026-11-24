using Microsoft.Extensions.Logging;

namespace MyBackup.Core.Notifications;

public interface INotifier
{
    Task NotifyAsync(string html, CancellationToken ct);
}

public sealed class NullNotifier : INotifier
{
    public Task NotifyAsync(string html, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Рассылка во все разрешённые чаты с повторами: Telegram тоже может быть недоступен.</summary>
public sealed class TelegramNotifier(TelegramClient client, IReadOnlyList<long> chatIds, ILogger logger) : INotifier
{
    private static readonly TimeSpan[] Delays = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    public async Task NotifyAsync(string html, CancellationToken ct)
    {
        foreach (var chatId in chatIds)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await client.SendMessageAsync(chatId, html, ct).ConfigureAwait(false);
                    break;
                }
                catch (Exception e) when (!ct.IsCancellationRequested && attempt < Delays.Length)
                {
                    logger.LogWarning("Telegram: не удалось отправить в {Chat} ({Error}), повтор через {Delay}", chatId,
                        e.Message, Delays[attempt]);
                    await Task.Delay(Delays[attempt], ct).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    logger.LogError("Telegram: сообщение в {Chat} не отправлено: {Error}", chatId, e.Message);
                    break;
                }
            }
        }
    }
}
