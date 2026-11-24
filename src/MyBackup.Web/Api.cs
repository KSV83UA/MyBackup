using System.Text;
using System.Text.Json;
using Cronos;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MyBackup.Core.Config;
using MyBackup.Core.Jobs;
using MyBackup.Core.Notifications;
using MyBackup.Core.Restic;
using MyBackup.Core.Sources;
using MyBackup.Core.Util;

namespace MyBackup.Web;

internal sealed record LoginRequest(string? Password);

internal sealed record PasswordRequest(string? Current, string? New);

internal sealed record MsSqlRequest(string? ConnectionString);

internal sealed record ProfilesRequest(List<string>? Folders, List<string>? ExcludeUsers);

internal sealed record RunSummary(
    string JobName,
    string Trigger,
    DateTimeOffset Started,
    DateTimeOffset Finished,
    UnitStatus Status,
    long Uploaded,
    int Retries,
    int Units,
    int Failed,
    string OneLine)
{
    public static RunSummary From(RunReport r) => new(r.JobName, r.Trigger, r.Started, r.Finished, r.Status,
        r.TotalUploaded, r.TotalRetries, r.Units.Count, r.Units.Count(u => u.Status == UnitStatus.Failed),
        ReportFormatter.OneLine(r));
}

internal sealed record Warning(string Level, string Text, string? Link = null);

internal static class Api
{
    private const string WebTrigger = "вручную, веб-интерфейс";

    /// <summary>Эти настройки из браузера не меняются: от них зависит доступ к самому интерфейсу или что запускается.</summary>
    internal static readonly string[] ReadOnlySettings =
        ["serverName", "dataDirectory", "web", "restic.executable", "telegram.apiBaseUrl"];

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/session", Session).AllowAnonymous();
        app.MapPost("/api/login", LoginAsync).AllowAnonymous();
        app.MapPost("/api/logout", (Delegate)LogoutAsync).AllowAnonymous();

        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/status", Status);
        api.MapPost("/jobs/{name}/run", RunJob);
        api.MapPost("/current/cancel", CancelCurrent);
        api.MapGet("/history", History);
        api.MapGet("/logs", Logs);
        api.MapGet("/config", GetConfig);
        api.MapPut("/config", PutConfigAsync);
        api.MapGet("/cron", Cron);
        api.MapGet("/fs", Fs);
        api.MapGet("/discover/1c", Discover1C);
        api.MapGet("/discover/1c-scan", Scan1CAsync);
        api.MapPost("/discover/mssql", DiscoverMsSqlAsync);
        api.MapPost("/discover/profiles", DiscoverProfiles);
        api.MapGet("/discover/1c-platforms", () => Results.Ok(OneCDumpSource.FindPlatforms()));
        api.MapGet("/snapshots", SnapshotsAsync);
        api.MapGet("/snapshots/{id}/ls", SnapshotLsAsync);
        api.MapGet("/snapshots/{id}/download", SnapshotDownloadAsync);
        api.MapPost("/repository/check", CheckRepositoryAsync);
        api.MapPost("/repository/init", InitRepositoryAsync);
        api.MapPost("/telegram/test", TelegramTestAsync);
        api.MapGet("/telegram/chats", (WebUiContext ctx) => Results.Ok(ctx.Telegram.UnknownChats));
        api.MapPost("/password", ChangePasswordAsync);
    }

    private static IResult Error(string message, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error = message }, statusCode: status);

    private static ILogger Log(WebUiContext ctx) => ctx.LoggerFactory.CreateLogger("Web");

    // ---------- вход ----------

    private static IResult Session(HttpContext http, WebUiContext ctx) => Results.Ok(new
    {
        authenticated = http.User.Identity?.IsAuthenticated == true,
        passwordSet = ctx.Password.IsSet,
        serverName = ctx.Store.Current.ServerName,
        version = WebUiContext.Version,
    });

    private static async Task<IResult> LoginAsync(LoginRequest request, HttpContext http, WebUiContext ctx)
    {
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "?";
        if (!ctx.Throttle.CanTry(ip, out var wait))
            return Error($"Слишком много неудачных попыток. Подождите {wait} с.", StatusCodes.Status429TooManyRequests);
        if (!ctx.Password.IsSet)
            return Error("Пароль не задан. На сервере от имени администратора выполните: MyBackup.exe web-password");

        if (!ctx.Password.Verify(request.Password ?? ""))
        {
            ctx.Throttle.Failed(ip);
            Log(ctx).LogWarning("Веб-интерфейс: неверный пароль с адреса {Ip}", ip);
            return Error("Неверный пароль", StatusCodes.Status401Unauthorized);
        }

        ctx.Throttle.Succeeded(ip);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, WebUi.CreatePrincipal(ctx.Password));
        Log(ctx).LogInformation("Веб-интерфейс: вход с адреса {Ip}", ip);
        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Ok(new { ok = true });
    }

    private static async Task<IResult> ChangePasswordAsync(PasswordRequest request, HttpContext http, WebUiContext ctx)
    {
        if (!ctx.Password.Verify(request.Current ?? "")) return Error("Текущий пароль указан неверно");
        if (request.New is null || request.New.Length < WebPasswordStore.MinLength)
            return Error($"Новый пароль должен быть не короче {WebPasswordStore.MinLength} символов");

        ctx.Password.Set(request.New);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, WebUi.CreatePrincipal(ctx.Password));
        Log(ctx).LogInformation("Веб-интерфейс: пароль изменён");
        return Results.Ok(new { ok = true });
    }

    // ---------- состояние ----------

    private static IResult Status(WebUiContext ctx)
    {
        var cfg = ctx.Store.Current;
        var history = ctx.History.ReadLast(300);
        var cur = ctx.Queue.Current?.Snapshot();

        var jobs = cfg.Jobs.Select(j =>
        {
            var runs = history.Where(r => r.JobName.Equals(j.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            var last = runs.FirstOrDefault();
            return new
            {
                j.Name,
                j.Enabled,
                j.Schedule,
                nextRun = j.Enabled ? ctx.Schedule.NextRun(j.Name) : null,
                sources = j.Sources.Count(s => s.Enabled),
                last = last is null ? null : RunSummary.From(last),
                lastSuccess = runs.FirstOrDefault(r => r.Status != UnitStatus.Failed)?.Finished,
                blockingErrors = ConfigLoader.ValidateForRun(cfg, j),
            };
        }).ToList();

        var current = cur is null
            ? null
            : new
            {
                cur.JobName,
                cur.Trigger,
                cur.StartedAt,
                cur.UnitTitle,
                cur.UnitIndex,
                cur.UnitTotal,
                cur.UnitStartedAt,
                cur.Attempt,
                percent = cur.Status?.PercentDone,
                bytesDone = cur.Status?.BytesDone,
                totalBytes = cur.Status?.TotalBytes,
                filesDone = cur.Status?.FilesDone,
                totalFiles = cur.Status?.TotalFiles,
                secondsRemaining = cur.Status?.SecondsRemaining,
                cancelRequested = cur.CancelRequestedBy is not null,
            };

        return Results.Ok(new
        {
            serverName = cfg.ServerName,
            version = WebUiContext.Version,
            startedAt = ctx.StartedAt,
            now = DateTimeOffset.Now,
            current,
            waiting = ctx.Queue.Waiting,
            jobs,
            warnings = BuildWarnings(cfg, history),
            configErrors = ConfigLoader.Validate(cfg),
            recent = history.Take(8).Select(RunSummary.From),
        });
    }

    private static List<Warning> BuildWarnings(AppConfig cfg, IReadOnlyList<RunReport> history)
    {
        var w = new List<Warning>();
        var restic = ResticSettings.ResolveExecutable(cfg.Restic.Executable);
        if (!File.Exists(restic)) w.Add(new Warning("error", $"Не найден restic: {restic}"));
        if (cfg.Restic.UseVss && !SystemInfo.IsAdministrator())
            w.Add(new Warning("warn", "VSS недоступен: служба запущена без прав администратора — открытые файлы и базы 1С копируются «на ходу»"));

        if (!cfg.Telegram.Enabled)
            w.Add(new Warning("info", "Уведомления в Telegram выключены", "#/settings/telegram"));
        else if (cfg.Telegram.AllowedChatIds.Count == 0)
            w.Add(new Warning("warn", "Telegram: не разрешён ни один чат — отчёты никуда не отправляются. Напишите боту и разрешите чат в настройках",
                "#/settings/telegram"));

        foreach (var job in cfg.Jobs.Where(j => j.Enabled))
        {
            var runs = history.Where(r => r.JobName.Equals(job.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (runs.Count == 0) continue;
            var lastOk = runs.FirstOrDefault(r => r.Status != UnitStatus.Failed);
            if (runs[0].Status == UnitStatus.Failed)
                w.Add(new Warning("error",
                    $"Задача {job.Name}: последний запуск {runs[0].Started.ToLocalTime():dd.MM HH:mm} завершился с ошибкой", "#/history"));
            if (lastOk is null)
                w.Add(new Warning("error", $"Задача {job.Name}: ещё не было ни одной успешной копии", "#/history"));
            else if (DateTimeOffset.Now - lastOk.Finished > TimeSpan.FromHours(48))
                w.Add(new Warning("error",
                    $"Задача {job.Name}: последняя успешная копия {(int)(DateTimeOffset.Now - lastOk.Finished).TotalDays} дн. назад", "#/history"));
        }

        if (cfg.Web.Urls.Any(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !WebUi.IsLoopbackUrl(u)))
            w.Add(new Warning("warn", "Веб-интерфейс доступен по сети без шифрования (http). Используйте https или доступ только через VPN"));
        return w;
    }

    private static IResult RunJob(string name, WebUiContext ctx)
    {
        var cfg = ctx.Store.Current;
        var job = cfg.Jobs.FirstOrDefault(j => j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (job is null) return Error($"Задача «{name}» не найдена", StatusCodes.Status404NotFound);

        var errors = ConfigLoader.ValidateForRun(cfg, job);
        if (errors.Count > 0)
            return Results.Json(new { error = "Задачу нельзя запустить: в настройках есть ошибки", errors },
                statusCode: StatusCodes.Status409Conflict);

        if (!ctx.Queue.TryEnqueue(job.Name, WebTrigger))
            return Error($"Задача {job.Name} уже выполняется или стоит в очереди", StatusCodes.Status409Conflict);
        Log(ctx).LogInformation("Задача {Job} поставлена в очередь из веб-интерфейса", job.Name);
        return Results.Ok(new { queued = true });
    }

    private static IResult CancelCurrent(WebUiContext ctx)
    {
        if (!ctx.Queue.CancelCurrent("веб-интерфейс")) return Error("Сейчас ничего не выполняется", StatusCodes.Status404NotFound);
        Log(ctx).LogWarning("Выполняемая задача остановлена из веб-интерфейса");
        return Results.Ok(new { ok = true });
    }

    private static IResult History(WebUiContext ctx, int? count, string? job) =>
        Results.Ok(ctx.History.ReadLast(Math.Clamp(count ?? 100, 1, 1000), string.IsNullOrWhiteSpace(job) ? null : job));

    private static IResult Logs(WebUiContext ctx, int? lines)
    {
        var dir = ctx.Store.Current.LogDirectory;
        var file = Directory.Exists(dir)
            ? new DirectoryInfo(dir).GetFiles("mybackup-*.log").OrderByDescending(f => f.Name, StringComparer.Ordinal).FirstOrDefault()
            : null;
        if (file is null) return Results.Ok(new { file = (string?)null, lines = Array.Empty<string>() });

        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var all = new List<string>();
        while (reader.ReadLine() is { } line) all.Add(line);
        var take = Math.Clamp(lines ?? 400, 10, 5000);
        return Results.Ok(new { file = file.FullName, lines = all.Skip(Math.Max(0, all.Count - take)) });
    }

    // ---------- настройки ----------

    private static IResult GetConfig(WebUiContext ctx)
    {
        var cfg = ctx.Store.Current;
        return Results.Ok(new
        {
            config = JsonSerializer.SerializeToNode(ConfigMasking.ForClient(cfg), ConfigLoader.WriteOptions),
            path = ctx.Store.FilePath,
            dataDirectory = cfg.ResolvedDataDirectory,
            errors = ConfigLoader.Validate(cfg),
            mask = ConfigMasking.Mask,
            readOnly = ReadOnlySettings,
            isAdministrator = SystemInfo.IsAdministrator(),
        });
    }

    private static async Task<IResult> PutConfigAsync(HttpRequest request, WebUiContext ctx)
    {
        string body;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8)) body = await reader.ReadToEndAsync();

        AppConfig incoming;
        try
        {
            incoming = ConfigLoader.Parse(body);
        }
        catch (Exception e) when (e is JsonException or ConfigException or NotSupportedException)
        {
            return Results.Json(new { saved = false, errors = new[] { "Неверные данные: " + e.Message } }, statusCode: 400);
        }

        var current = ctx.Store.Current;
        var (merged, mergeErrors) = ConfigMasking.MergeFromClient(incoming, current);
        if (mergeErrors.Count > 0) return Results.Json(new { saved = false, errors = mergeErrors }, statusCode: 400);

        KeepReadOnly(merged, current);
        var result = ctx.Store.Save(merged);
        if (!result.Saved) return Results.Json(new { saved = false, errors = result.Errors }, statusCode: 409);

        Log(ctx).LogInformation("Настройки изменены в веб-интерфейсе (прежняя версия: {Backup})", result.BackupPath ?? "—");
        return Results.Ok(new { saved = true, errors = result.Errors, backupPath = result.BackupPath });
    }

    internal static void KeepReadOnly(AppConfig target, AppConfig current)
    {
        target.ServerName = current.ServerName;
        target.DataDirectory = current.DataDirectory;
        target.Web = ConfigLoader.Clone(current).Web;
        target.Restic.Executable = current.Restic.Executable;
        target.Telegram.ApiBaseUrl = current.Telegram.ApiBaseUrl;
    }

    private static IResult Cron(string? expr)
    {
        try
        {
            var cron = CronExpression.Parse(expr ?? "");
            var next = new List<DateTimeOffset>();
            var t = DateTimeOffset.Now;
            for (var i = 0; i < 5; i++)
            {
                if (cron.GetNextOccurrence(t, TimeZoneInfo.Local) is not { } n) break;
                next.Add(n);
                t = n;
            }

            return Results.Ok(new { valid = true, next });
        }
        catch (CronFormatException e)
        {
            return Results.Ok(new { valid = false, error = e.Message });
        }
    }

    // ---------- выбор файлов и поиск ----------

    private static IResult Fs(string? path, bool? files) => Results.Ok(FileBrowser.List(path, files == true));

    private static IResult Discover1C() =>
        Results.Ok(OneCBaseDiscovery.FromUserLists(ProfileDiscovery.Discover([])));

    private static async Task<IResult> Scan1CAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return Error("Папка не найдена");
        var found = await Task.Run(() => OneCBaseDiscovery.ScanFolder(path, 6, TimeSpan.FromSeconds(20)));
        return Results.Ok(found);
    }

    private static async Task<IResult> DiscoverMsSqlAsync(MsSqlRequest request, WebUiContext ctx, HttpContext http)
    {
        var cs = request.ConnectionString ?? "";
        if (cs.Contains(ConfigMasking.Mask, StringComparison.Ordinal))
        {
            var saved = ctx.Store.Current.Jobs.SelectMany(j => j.Sources).OfType<MsSqlSourceConfig>()
                .FirstOrDefault(s => ConfigMasking.MaskConnectionString(s.ConnectionString) == cs);
            if (saved is null) return Error("Пароль в строке подключения скрыт: сохраните настройки или введите пароль заново");
            cs = saved.ConnectionString;
        }

        if (string.IsNullOrWhiteSpace(cs)) return Error("Укажите строку подключения");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            return Results.Ok(await MsSqlSource.ListDatabasesAsync(cs, cts.Token));
        }
        catch (Exception e) when (e is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested)
        {
            return Error("Не удалось подключиться: " + e.Message);
        }
    }

    private static IResult DiscoverProfiles(ProfilesRequest request)
    {
        var config = new UserProfilesSourceConfig { ExcludeUsers = request.ExcludeUsers ?? [] };
        if (request.Folders is not null) config.Folders = request.Folders;

        var all = ProfileDiscovery.Discover([]);
        var included = ProfileDiscovery.Discover(config.ExcludeUsers);
        var (paths, notes, users) = UserProfilesSource.ResolvePaths(config, included, KnownFolders.ReadShellFolder);
        return Results.Ok(new
        {
            profiles = all.Select(p => new
            {
                p.UserName, p.ProfilePath, p.HiveLoaded, excluded = included.All(i => i.Sid != p.Sid),
            }),
            paths,
            notes,
            users,
        });
    }

    // ---------- хранилище и Telegram ----------

    private static ResticRunner CreateRestic(WebUiContext ctx) =>
        new(ResticSettings.FromConfig(ctx.Store.Current), Log(ctx));

    private static async Task<IResult> SnapshotsAsync(WebUiContext ctx, HttpContext http)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            var snapshots = await CreateRestic(ctx).SnapshotsAsync(null, cts.Token);
            return Results.Ok(snapshots.OrderByDescending(s => s.Time));
        }
        catch (Exception e) when (!http.RequestAborted.IsCancellationRequested)
        {
            return Error(e is OperationCanceledException ? "хранилище не ответило за 3 минуты" : e.Message,
                StatusCodes.Status502BadGateway);
        }
    }

    internal static bool IsSnapshotId(string? id) =>
        id is { Length: >= 8 and <= 64 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Путь внутри снимка: /C/Users/… — без «..», двоеточий (синтаксис «снимок:путь») и управляющих символов.</summary>
    internal static bool IsSnapshotPath(string? path) =>
        path is { Length: > 0 and < 4096 } && path[0] == '/' && !path.Contains(':')
        && !path.Any(char.IsControl) && !path.Split('/').Any(s => s is "." or "..");

    private static async Task<IResult> SnapshotLsAsync(string id, string? path, WebUiContext ctx, HttpContext http)
    {
        path = string.IsNullOrEmpty(path) ? "/" : path;
        if (!IsSnapshotId(id) || !IsSnapshotPath(path)) return Error("Неверный снимок или путь");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        cts.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            var nodes = await CreateRestic(ctx).LsAsync(id, path, cts.Token);
            return Results.Ok(new
            {
                path,
                entries = nodes.OrderBy(n => n.Type != "dir").ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase),
            });
        }
        catch (Exception e) when (!http.RequestAborted.IsCancellationRequested)
        {
            return Error(e is OperationCanceledException ? "хранилище не ответило за 3 минуты" : e.Message,
                StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>Файл из снимка — как есть, папка — zip-архивом; поток идёт прямо из restic в браузер.</summary>
    private static async Task SnapshotDownloadAsync(string id, string? path, string? type, long? size, HttpContext http,
        WebUiContext ctx)
    {
        if (!IsSnapshotId(id) || !IsSnapshotPath(path))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { error = "Неверный снимок или путь" });
            return;
        }

        var p = path!.Length > 1 ? path.TrimEnd('/') : path;
        var isDir = type == "dir";
        var name = p == "/" ? $"snapshot-{id[..8]}" : p[(p.LastIndexOf('/') + 1)..];
        var snapshot = id;
        var dumpPath = p;
        if (isDir && p != "/" && p.LastIndexOf('/') > 0)
        {
            snapshot = $"{id}:{p[..p.LastIndexOf('/')]}";
            dumpPath = "/" + name;
        }

        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(isDir ? name + ".zip" : name);
        http.Response.Headers.ContentDisposition = disposition.ToString();
        http.Response.ContentType = isDir ? "application/zip" : "application/octet-stream";
        if (!isDir && size is >= 0) http.Response.ContentLength = size;

        var log = Log(ctx);
        log.LogInformation("Скачивание из снимка {Id}: {Path}", id[..8], p);
        try
        {
            await CreateRestic(ctx).DumpAsync(snapshot, dumpPath, isDir, http.Response.Body, http.RequestAborted);
        }
        catch (Exception e) when (!http.Response.HasStarted && !http.RequestAborted.IsCancellationRequested)
        {
            http.Response.Headers.Remove("Content-Disposition");
            http.Response.ContentLength = null;
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            await http.Response.WriteAsJsonAsync(new { error = e.Message });
        }
        catch (Exception e) when (!http.RequestAborted.IsCancellationRequested)
        {
            // Часть файла уже ушла: обрываем соединение, чтобы браузер не сохранил неполный файл как целый.
            log.LogWarning("Скачивание {Path} прервано: {Error}", p, e.Message);
            http.Abort();
        }
    }

    private static async Task<IResult> CheckRepositoryAsync(WebUiContext ctx, HttpContext http)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var restic = CreateRestic(ctx);
            var version = await restic.RunAsync(["version"], cts.Token, quiet: true);
            var (exists, error) = await restic.RepositoryExistsAsync(cts.Token);
            return Results.Ok(new { version = version.Stdout.Trim(), exists, error });
        }
        catch (Exception e) when (!http.RequestAborted.IsCancellationRequested)
        {
            return Results.Ok(new { version = (string?)null, exists = (bool?)null, error = e.Message });
        }
    }

    private static async Task<IResult> InitRepositoryAsync(WebUiContext ctx, HttpContext http)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var restic = CreateRestic(ctx);
            var (exists, error) = await restic.RepositoryExistsAsync(cts.Token);
            if (exists == true) return Error("Хранилище уже создано");
            if (exists is null) return Error("Хранилище недоступно: " + error);

            var r = await restic.InitAsync(cts.Token);
            if (!r.Success) return Error("Не удалось создать хранилище: " + r.ErrorMessage);
            Log(ctx).LogInformation("Хранилище создано из веб-интерфейса");
            return Results.Ok(new { ok = true, output = r.Stdout.Trim() });
        }
        catch (Exception e) when (!http.RequestAborted.IsCancellationRequested)
        {
            return Error(e.Message);
        }
    }

    private static async Task<IResult> TelegramTestAsync(WebUiContext ctx, HttpContext http)
    {
        var client = ctx.Telegram.GetClient();
        if (client is null) return Error("Telegram выключен или не задан токен — включите и сохраните настройки");

        var cfg = ctx.Store.Current;
        try
        {
            var me = await client.GetMeAsync(http.RequestAborted);
            var sent = new List<long>();
            var errors = new List<string>();
            foreach (var chat in cfg.Telegram.AllowedChatIds)
            {
                try
                {
                    await client.SendMessageAsync(chat,
                        $"🔔 <b>{Fmt.HtmlEscape(cfg.ServerName)}</b>: проверка уведомлений MyBackup", http.RequestAborted);
                    sent.Add(chat);
                }
                catch (Exception e) when (!http.RequestAborted.IsCancellationRequested)
                {
                    errors.Add($"{chat}: {e.Message}");
                }
            }

            return Results.Ok(new { bot = me?.Username, sent, errors });
        }
        catch (Exception e) when (!http.RequestAborted.IsCancellationRequested)
        {
            return Error("Telegram: " + e.Message, StatusCodes.Status502BadGateway);
        }
    }
}
