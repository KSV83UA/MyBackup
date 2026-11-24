using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyBackup.Core.Notifications;

public sealed class TelegramApiException(string message) : Exception(message);

/// <summary>Минимальный клиент Telegram Bot API: sendMessage и getUpdates (long polling), с поддержкой прокси.</summary>
public sealed class TelegramClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public TelegramClient(string token, string? proxy = null, string apiBaseUrl = "https://api.telegram.org",
        HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Не указан токен бота", nameof(token));
        handler ??= new SocketsHttpHandler
        {
            Proxy = string.IsNullOrWhiteSpace(proxy) ? null : new WebProxy(proxy),
            UseProxy = !string.IsNullOrWhiteSpace(proxy),
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        _baseUrl = $"{apiBaseUrl.TrimEnd('/')}/bot{token}/";
    }

    public Task SendMessageAsync(long chatId, string html, CancellationToken ct) =>
        CallAsync<JsonElement>("sendMessage",
            new SendMessageRequest(chatId, html, "HTML", new LinkPreviewOptions(true)), ct);

    public async Task<IReadOnlyList<TgUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct) =>
        await CallAsync<List<TgUpdate>>("getUpdates",
            new GetUpdatesRequest(offset, timeoutSeconds, ["message"]), ct).ConfigureAwait(false) ?? [];

    public Task<TgUser?> GetMeAsync(CancellationToken ct) => CallAsync<TgUser>("getMe", new { }, ct);

    private async Task<T?> CallAsync<T>(string method, object body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(_baseUrl + method, body, Json, ct).ConfigureAwait(false);
        var result = await response.Content.ReadFromJsonAsync<TgResponse<T>>(Json, ct).ConfigureAwait(false);
        if (result is null || !result.Ok)
            throw new TelegramApiException($"Telegram {method}: {result?.Description ?? response.StatusCode.ToString()}");
        return result.Result;
    }

    public void Dispose() => _http.Dispose();

    private sealed record SendMessageRequest(long ChatId, string Text, string ParseMode, LinkPreviewOptions LinkPreviewOptions);

    private sealed record LinkPreviewOptions(bool IsDisabled);

    private sealed record GetUpdatesRequest(long Offset, int Timeout, string[] AllowedUpdates);

    private sealed class TgResponse<T>
    {
        public bool Ok { get; set; }
        public T? Result { get; set; }
        public string? Description { get; set; }
    }
}

public sealed class TgUpdate
{
    public long UpdateId { get; set; }
    public TgMessage? Message { get; set; }
}

public sealed class TgMessage
{
    public long MessageId { get; set; }
    public TgChat Chat { get; set; } = new();
    public TgUser? From { get; set; }
    public string? Text { get; set; }
}

public sealed class TgChat
{
    public long Id { get; set; }
    public string? Type { get; set; }
    public string? Title { get; set; }
    public string? Username { get; set; }
}

public sealed class TgUser
{
    public long Id { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
}
