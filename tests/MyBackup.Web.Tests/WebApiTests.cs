using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using MyBackup.Core.Config;
using MyBackup.Core.Jobs;
using MyBackup.Core.Notifications;

namespace MyBackup.Web.Tests;

/// <summary>Веб-интерфейс на тестовом сервере с настройками во временной папке.</summary>
public sealed class TestWebHost : IAsyncDisposable
{
    public const string Password = "test-web-password";
    public const string RepoPassword = "repo-secret-value";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "mybackup-web-" + Guid.NewGuid().ToString("N"));
    public ConfigStore Store { get; }
    public JobQueue Queue { get; } = new();
    public WebPasswordStore PasswordStore { get; }
    public WebApplication App { get; }

    public TestWebHost()
    {
        Directory.CreateDirectory(Root);
        var config = new AppConfig
        {
            ServerName = "test-server",
            DataDirectory = Path.Combine(Root, "data"),
            Restic = new ResticConfig
            {
                Repository = "rest:https://backup:repo-url-secret@backup.example:8000/test/",
                Password = Secrets.Protect(RepoPassword),
                Environment = { ["AWS_SECRET_ACCESS_KEY"] = Secrets.Protect("aws-secret") },
            },
            Jobs =
            [
                new JobConfig
                {
                    Name = "test",
                    Sources =
                    [
                        new PathsSourceConfig { Name = "Docs", Paths = [Root] },
                        new MsSqlSourceConfig { Name = "SQL", ConnectionString = "Server=.;User Id=sa;Password=sql-secret;", Databases = ["*"] },
                    ],
                },
            ],
        };
        var path = Path.Combine(Root, "config.json");
        File.WriteAllText(path, ConfigLoader.Serialize(config));

        Store = new ConfigStore(path, ConfigLoader.Load(path));
        PasswordStore = new WebPasswordStore(Store.Current.WebPasswordFile);
        PasswordStore.Set(Password);

        var ctx = new WebUiContext
        {
            Store = Store, Queue = Queue, Schedule = new ScheduleInfo(), History = new HistoryStore(Store.Current.HistoryFile),
            Telegram = new TelegramHub(Store, NullLogger.Instance), Password = PasswordStore, LoggerFactory = NullLoggerFactory.Instance,
        };
        App = WebUi.Build(ctx, b => b.WebHost.UseTestServer());
        App.StartAsync().GetAwaiter().GetResult();
    }

    /// <summary>Клиент, который хранит cookie, как браузер.</summary>
    public HttpClient Client(bool csrfHeader = true)
    {
        var client = new HttpClient(new CookieHandler(App.GetTestServer().CreateHandler())) { BaseAddress = new Uri("http://localhost") };
        if (csrfHeader) client.DefaultRequestHeaders.Add(WebUi.CsrfHeader, "1");
        return client;
    }

    public async Task<HttpClient> LoggedInAsync()
    {
        var client = Client();
        var r = await client.PostAsJsonAsync("/api/login", new { password = Password });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class CookieHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly CookieContainer _cookies = new();

        public string? RawCookie => _cookies.GetCookieHeader(new Uri("http://localhost"));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = new Uri("http://localhost");
            var header = _cookies.GetCookieHeader(uri);
            if (header.Length > 0) request.Headers.Add("Cookie", header);
            var response = await base.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var v in values)
                    _cookies.SetCookies(uri, v);
            return response;
        }
    }
}

public class WebApiTests
{
    [Fact]
    public async Task ApiRequiresLoginButPageIsPublic()
    {
        await using var host = new TestWebHost();
        var client = host.Client();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/config")).StatusCode);

        var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("app.js", await page.Content.ReadAsStringAsync());
        Assert.Contains("default-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/app.js")).StatusCode);

        var session = await client.GetFromJsonAsync<JsonObject>("/api/session");
        Assert.False(session!["authenticated"]!.GetValue<bool>());
        Assert.True(session["passwordSet"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ChangingRequestsNeedOwnHeader()
    {
        await using var host = new TestWebHost();
        var r = await host.Client(csrfHeader: false).PostAsJsonAsync("/api/login", new { password = TestWebHost.Password });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task WrongPasswordIsThrottled()
    {
        await using var host = new TestWebHost();
        var client = host.Client();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/login", new { password = "wrong" })).StatusCode);

        // Даже верный пароль не принимается, пока действует пауза.
        var r = await client.PostAsJsonAsync("/api/login", new { password = TestWebHost.Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
    }

    [Fact]
    public async Task StatusShowsJobsAndWarnings()
    {
        await using var host = new TestWebHost();
        var client = await host.LoggedInAsync();
        var status = await client.GetFromJsonAsync<JsonObject>("/api/status");

        var job = status!["jobs"]!.AsArray().Single()!;
        Assert.Equal("test", job["name"]!.GetValue<string>());
        Assert.Equal(2, job["sources"]!.GetValue<int>());
        Assert.Null(status["current"]);
        Assert.Contains(status["warnings"]!.AsArray(), w => w!["text"]!.GetValue<string>().Contains("Telegram"));
    }

    [Fact]
    public async Task ConfigNeverSendsSecretsAndKeepsThemOnSave()
    {
        await using var host = new TestWebHost();
        var client = await host.LoggedInAsync();

        var raw = await client.GetStringAsync("/api/config");
        foreach (var secret in new[] { TestWebHost.RepoPassword, "repo-url-secret", "aws-secret", "sql-secret", "dpapi:" })
            Assert.DoesNotContain(secret, raw);

        var config = JsonNode.Parse(raw)!["config"]!;
        Assert.Equal(ConfigMasking.Mask, config["restic"]!["password"]!.GetValue<string>());
        Assert.Equal($"rest:https://backup:{ConfigMasking.Mask}@backup.example:8000/test/", config["restic"]!["repository"]!.GetValue<string>());

        // Сохраняем без изменений, кроме расписания — секреты остаются прежними.
        config["jobs"]![0]!["schedule"] = "30 2 * * *";
        var put = await client.PutAsync("/api/config", JsonContent.Create(config));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var saved = host.Store.Current;
        Assert.Equal("30 2 * * *", saved.Jobs[0].Schedule);
        Assert.Equal(TestWebHost.RepoPassword, Secrets.Reveal(saved.Restic.Password));
        Assert.Equal("rest:https://backup:repo-url-secret@backup.example:8000/test/", Secrets.Reveal(saved.Restic.Repository));
        Assert.Equal("aws-secret", Secrets.Reveal(saved.Restic.Environment["AWS_SECRET_ACCESS_KEY"]));
        Assert.Contains("Password=sql-secret", Secrets.Reveal(((MsSqlSourceConfig)saved.Jobs[0].Sources[1]).ConnectionString));
        Assert.DoesNotContain("sql-secret", File.ReadAllText(host.Store.FilePath)); // на диске — зашифровано
        Assert.Single(Directory.GetFiles(saved.ConfigBackupDirectory));
    }

    [Fact]
    public async Task ReadOnlySettingsCannotBeChangedFromBrowser()
    {
        await using var host = new TestWebHost();
        var client = await host.LoggedInAsync();
        var config = (await client.GetFromJsonAsync<JsonObject>("/api/config"))!["config"]!;

        config["serverName"] = "evil";
        config["restic"]!["executable"] = @"C:\Windows\System32\cmd.exe";
        config["web"]!["urls"] = new JsonArray("http://0.0.0.0:80");
        config["telegram"]!["apiBaseUrl"] = "https://attacker.example";
        config["restic"]!["environment"]!["RESTIC_PASSWORD_COMMAND"] = "calc.exe";
        var put = await client.PutAsync("/api/config", JsonContent.Create(config));
        var body = await put.Content.ReadFromJsonAsync<JsonObject>();

        var saved = host.Store.Current;
        Assert.Equal("test-server", saved.ServerName);
        Assert.Equal("restic.exe", saved.Restic.Executable);
        Assert.Equal(["http://127.0.0.1:8480"], saved.Web.Urls);
        Assert.Equal("https://api.telegram.org", saved.Telegram.ApiBaseUrl);

        // Опасную переменную окружения сохранить можно, но задача с ней не запустится.
        Assert.Contains(body!["errors"]!.AsArray(), e => e!.GetValue<string>().Contains("RESTIC_PASSWORD_COMMAND"));
        Assert.NotEmpty(ConfigLoader.ValidateForRun(saved, saved.Jobs[0]));
    }

    [Fact]
    public async Task ManualFileEditIsNotOverwritten()
    {
        await using var host = new TestWebHost();
        var client = await host.LoggedInAsync();
        var config = (await client.GetFromJsonAsync<JsonObject>("/api/config"))!["config"]!;

        File.SetLastWriteTimeUtc(host.Store.FilePath, DateTime.UtcNow.AddMinutes(5)); // кто-то поправил файл вручную
        var put = await client.PutAsync("/api/config", JsonContent.Create(config));
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
    }

    [Fact]
    public async Task InvalidJsonIsRejected()
    {
        await using var host = new TestWebHost();
        var client = await host.LoggedInAsync();
        var put = await client.PutAsync("/api/config", new StringContent("""{ "restik": {} }""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task RunQueuesJobOnceAndCancelNeedsRunningJob()
    {
        await using var host = new TestWebHost();
        var client = await host.LoggedInAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/jobs/test/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/api/jobs/TEST/run", null)).StatusCode);
        Assert.Contains("test", host.Queue.Waiting);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/jobs/nope/run", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/current/cancel", null)).StatusCode);
        var status = new JobStatus("test", "тест");
        var token = host.Queue.BeginRun(status, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/current/cancel", null)).StatusCode);
        Assert.True(token.IsCancellationRequested);
        Assert.Equal("остановлено вручную (веб-интерфейс)", status.CancelReason);
    }

    [Fact]
    public async Task PasswordChangeEndsOtherSessions()
    {
        await using var host = new TestWebHost();
        var first = await host.LoggedInAsync();
        var second = await host.LoggedInAsync();

        var r = await first.PostAsJsonAsync("/api/password", new { current = TestWebHost.Password, @new = "new-password-123" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/status")).StatusCode);
        Assert.True(host.PasswordStore.Verify("new-password-123"));
    }

    [Fact]
    public async Task FileBrowserAndCronHelpers()
    {
        await using var host = new TestWebHost();
        var client = await host.LoggedInAsync();

        var drives = await client.GetFromJsonAsync<JsonObject>("/api/fs");
        Assert.NotEmpty(drives!["entries"]!.AsArray());

        File.WriteAllText(Path.Combine(host.Root, "файл.txt"), "x");
        var folder = await client.GetFromJsonAsync<JsonObject>("/api/fs?files=true&path=" + Uri.EscapeDataString(host.Root));
        Assert.Contains(folder!["entries"]!.AsArray(), e => e!["name"]!.GetValue<string>() == "файл.txt");

        var cron = await client.GetFromJsonAsync<JsonObject>("/api/cron?expr=" + Uri.EscapeDataString("0 3 * * 1-5"));
        Assert.True(cron!["valid"]!.GetValue<bool>());
        Assert.Equal(5, cron["next"]!.AsArray().Count);
        var bad = await client.GetFromJsonAsync<JsonObject>("/api/cron?expr=nonsense");
        Assert.False(bad!["valid"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("a2a91978", "/C/Users", true)]
    [InlineData("a2a91978b931f87ebe13facd536d1a3e3247b812a1918c150895e34c9dc17688", "/", true)]
    [InlineData("A2A91978", "/C", false)]          // restic выдаёт id строчными
    [InlineData("a2a9", "/C", false)]
    [InlineData("a2a91978", "C/Users", false)]
    [InlineData("a2a91978", "/C/../Windows", false)]
    [InlineData("a2a91978", "/C/x:y", false)]       // «снимок:путь» подставляет сервер, а не браузер
    public void SnapshotIdAndPathAreValidated(string id, string path, bool ok) =>
        Assert.Equal(ok, Api.IsSnapshotId(id) && Api.IsSnapshotPath(path));

    [Fact]
    public async Task SnapshotBrowserRejectsBadInput()
    {
        await using var host = new TestWebHost();
        var client = await host.LoggedInAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/snapshots/zzz/ls")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/snapshots/a2a91978/ls?path=/C/../x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/snapshots/a2a91978/download?path=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await host.Client().GetAsync("/api/snapshots/a2a91978/download?path=/C")).StatusCode);
    }

    [Fact]
    public async Task LogoutEndsSession()
    {
        await using var host = new TestWebHost();
        var client = await host.LoggedInAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
    }
}

public class LoginThrottleTests
{
    [Fact]
    public void PauseGrowsAfterFiveFailures()
    {
        var time = new ManualTime();
        var t = new LoginThrottle(time);
        for (var i = 0; i < 4; i++) t.Failed("ip");
        Assert.True(t.CanTry("ip", out _));
        t.Failed("ip");
        Assert.False(t.CanTry("ip", out var wait));
        Assert.Equal(30, wait);
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.True(t.CanTry("ip", out _));
        t.Failed("ip");
        Assert.False(t.CanTry("ip", out wait));
        Assert.Equal(60, wait);
        t.Succeeded("ip");
        Assert.True(t.CanTry("ip", out _));
        Assert.True(t.CanTry("other", out _));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan d) => _now += d;
    }
}
