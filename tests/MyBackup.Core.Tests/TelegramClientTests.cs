using System.Net;
using System.Text;
using System.Text.Json;
using MyBackup.Core.Notifications;

namespace MyBackup.Core.Tests;

public class TelegramClientTests
{
    private sealed class FakeHandler(HttpStatusCode status, string response) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task SendMessagePostsHtmlWithoutPreview()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """{"ok":true,"result":{}}""");
        using var client = new TelegramClient("123:ABC", handler: handler);

        await client.SendMessageAsync(42, "<b>Отчёт</b>", CancellationToken.None);

        var (url, body) = Assert.Single(handler.Requests);
        Assert.Equal("https://api.telegram.org/bot123:ABC/sendMessage", url);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(42, json.RootElement.GetProperty("chat_id").GetInt64());
        Assert.Equal("<b>Отчёт</b>", json.RootElement.GetProperty("text").GetString());
        Assert.Equal("HTML", json.RootElement.GetProperty("parse_mode").GetString());
        Assert.True(json.RootElement.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
    }

    [Fact]
    public async Task GetUpdatesParsesMessages()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"ok":true,"result":[{"update_id":7,"message":{"message_id":1,"chat":{"id":42,"type":"private"},
             "from":{"id":5,"username":"admin"},"text":"/status"}}]}
            """);
        using var client = new TelegramClient("t", handler: handler);

        var updates = await client.GetUpdatesAsync(5, 50, CancellationToken.None);

        var u = Assert.Single(updates);
        Assert.Equal(7, u.UpdateId);
        Assert.Equal(42, u.Message!.Chat.Id);
        Assert.Equal("admin", u.Message.From!.Username);
        Assert.Equal("/status", u.Message.Text);
        Assert.Contains("\"offset\":5", handler.Requests[0].Body);
        Assert.Contains("\"allowed_updates\":[\"message\"]", handler.Requests[0].Body);
    }

    [Fact]
    public async Task ApiErrorIsReported()
    {
        var handler = new FakeHandler(HttpStatusCode.Unauthorized, """{"ok":false,"error_code":401,"description":"Unauthorized"}""");
        using var client = new TelegramClient("bad", handler: handler);

        var e = await Assert.ThrowsAsync<TelegramApiException>(() => client.SendMessageAsync(1, "x", CancellationToken.None));
        Assert.Contains("Unauthorized", e.Message);
    }
}
