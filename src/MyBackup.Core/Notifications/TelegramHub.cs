using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MyBackup.Core.Config;

namespace MyBackup.Core.Notifications;

/// <summary>Чат, написавший боту, но не входящий в allowedChatIds.</summary>
public sealed record TelegramChatSeen(long ChatId, string? Type, string? Title, string? UserName, string? FirstName,
    DateTimeOffset LastSeen);

/// <summary>
/// Telegram для службы: клиент пересоздаётся, когда в настройках меняются токен или прокси;
/// уведомления уходят в чаты из текущих настроек. Запоминает чаты, которые писали боту без
/// разрешения, — их можно разрешить в веб-интерфейсе.
/// </summary>
public sealed class TelegramHub(ConfigStore store, ILogger logger,
    Func<string, string?, string, TelegramClient>? factory = null) : INotifier, IDisposable
{
    private const int MaxUnknownChats = 20;

    private readonly Func<string, string?, string, TelegramClient> _factory =
        factory ?? ((token, proxy, api) => new TelegramClient(token, proxy, api));

    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<long, TelegramChatSeen> _unknown = new();
    private string? _key;
    private TelegramClient? _client;

    /// <summary>Клиент для текущих настроек; null — Telegram выключен или токен не задан/не расшифровывается.</summary>
    public TelegramClient? GetClient() => GetClient(out _);

    /// <param name="key">Меняется вместе с токеном/прокси: по нему бот понимает, что нужно начать заново.</param>
    public TelegramClient? GetClient(out string key)
    {
        key = "";
        var t = store.Current.Telegram;
        if (!t.Enabled || string.IsNullOrWhiteSpace(t.BotToken)) return null;

        string token, proxy;
        try
        {
            token = Secrets.Reveal(t.BotToken);
            proxy = Secrets.Reveal(t.Proxy);
        }
        catch (ConfigException)
        {
            return null;
        }

        key = $"{t.ApiBaseUrl}\n{proxy}\n{token}";
        lock (_lock)
        {
            if (_client is not null && _key == key) return _client;

            var old = _client;
            _client = _factory(token, string.IsNullOrWhiteSpace(proxy) ? null : proxy, t.ApiBaseUrl);
            _key = key;
            if (old is not null)
            {
                // Старый клиент может ещё ждать ответа long polling — закрываем его позже.
                _ = Task.Delay(TimeSpan.FromMinutes(3)).ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
            }

            return _client;
        }
    }

    public async Task NotifyAsync(string html, CancellationToken ct)
    {
        var client = GetClient();
        var chats = store.Current.Telegram.AllowedChatIds;
        if (client is null || chats.Count == 0) return;
        await new TelegramNotifier(client, chats, logger).NotifyAsync(html, ct).ConfigureAwait(false);
    }

    public void RecordUnknownChat(TgMessage message)
    {
        var seen = new TelegramChatSeen(message.Chat.Id, message.Chat.Type, message.Chat.Title,
            message.From?.Username ?? message.Chat.Username, message.From?.FirstName, DateTimeOffset.Now);
        _unknown[message.Chat.Id] = seen;

        if (_unknown.Count > MaxUnknownChats)
            foreach (var old in _unknown.Values.OrderByDescending(c => c.LastSeen).Skip(MaxUnknownChats))
                _unknown.TryRemove(old.ChatId, out _);
    }

    /// <summary>Писали боту, но ещё не разрешены.</summary>
    public IReadOnlyList<TelegramChatSeen> UnknownChats
    {
        get
        {
            var allowed = store.Current.Telegram.AllowedChatIds.ToHashSet();
            return _unknown.Values.Where(c => !allowed.Contains(c.ChatId)).OrderByDescending(c => c.LastSeen).ToList();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _client?.Dispose();
            _client = null;
        }
    }
}
