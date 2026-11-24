using System.Diagnostics;
using MyBackup.Core.Config;
using MyBackup.Core.Jobs;
using MyBackup.Core.Notifications;
using MyBackup.Core.Restic;
using MyBackup.Core.Sources;
using MyBackup.Core.Util;
using Serilog.Extensions.Logging;

namespace MyBackup.Service;

/// <summary>Команды администратора: MyBackup.exe &lt;команда&gt; [--config путь].</summary>
internal static class Cli
{
    private const string Usage = """
        MyBackup — резервное копирование сервера 1С через restic

        Использование: MyBackup.exe [команда] [--config путь\config.json]

          (без команды)        запуск службы (или в консоли — для отладки)
          check                проверить настройки, restic, хранилище и Telegram
          init                 создать хранилище restic, если его ещё нет
          run [задача]         выполнить задачу сейчас и показать отчёт
          snapshots [имя]      список снимков (имя — фильтр по источнику)
          profiles             какие профили и папки пользователей будут скопированы
          protect <текст>      зашифровать пароль для config.json (значение dpapi:...)
          telegram-test        проверить бота; без allowedChatIds — показать chat_id написавших боту
          web-password         задать пароль веб-интерфейса (спросит дважды)
          restic <аргументы>   выполнить restic с настройками хранилища (например, restore)

        Настройки по умолчанию: %ProgramData%\MyBackup\config.json
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        string? configPath = null;
        string? command = null;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            // У restic нет флага --config, поэтому его можно указывать и после "restic"; -c — только для своих команд.
            if ((args[i] == "--config" || args[i] == "-c" && command != "restic") && i + 1 < args.Length)
            {
                configPath = args[++i];
                continue;
            }

            if (command is null) command = args[i].ToLowerInvariant();
            else rest.Add(args[i]);
        }

        configPath = Path.GetFullPath(configPath ?? ConfigLoader.DefaultPath);

        try
        {
            return command switch
            {
                null or "service" => await ServiceHost.RunAsync(configPath),
                "help" or "-h" or "--help" or "/?" => Print(Usage, 0),
                "protect" => Protect(rest),
                "check" => await CheckAsync(configPath),
                "init" => await InitAsync(configPath),
                "run" => await RunJobAsync(configPath, rest),
                "snapshots" => await SnapshotsAsync(configPath, rest),
                "profiles" => Profiles(configPath),
                "telegram-test" => await TelegramTestAsync(configPath),
                "web-password" => WebPassword(configPath, rest),
                "restic" => await ResticPassthroughAsync(configPath, rest),
                _ => Print($"Неизвестная команда: {command}\n\n{Usage}", 2),
            };
        }
        catch (ConfigException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
    }

    private static int Print(string text, int code)
    {
        Console.WriteLine(text);
        return code;
    }

    private static AppConfig LoadValid(string path)
    {
        var config = ConfigLoader.Load(path);
        var errors = ConfigLoader.Validate(config);
        if (errors.Count == 0) return config;
        foreach (var e in errors) Console.Error.WriteLine("  ✗ " + e);
        throw new ConfigException($"В настройках {errors.Count} ошибок: {path}");
    }

    private static (ResticRunner Restic, Microsoft.Extensions.Logging.ILogger Logger, IDisposable Log) CreateRunner(AppConfig config)
    {
        var log = Logging.Create(config, console: true);
        var logger = new SerilogLoggerFactory(log, dispose: true).CreateLogger("MyBackup");
        return (new ResticRunner(ResticSettings.FromConfig(config), logger), logger, log);
    }

    private static CancellationTokenSource CancelOnCtrlC()
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.Error.WriteLine("Остановка…");
            cts.Cancel();
        };
        return cts;
    }

    private static int WebPassword(string path, List<string> rest)
    {
        var config = File.Exists(path) ? ConfigLoader.Load(path) : new AppConfig();
        string password;
        if (rest.Count > 0)
        {
            password = string.Join(' ', rest);
        }
        else
        {
            if (Console.IsInputRedirected) return Print("Укажите пароль: MyBackup.exe web-password \"пароль\"", 2);
            Console.Write("Новый пароль веб-интерфейса: ");
            password = ReadHidden();
            Console.Write("Повторите: ");
            if (ReadHidden() != password) return Print("Пароли не совпадают.", 2);
        }

        if (password.Length < WebPasswordStore.MinLength)
            return Print($"Пароль должен быть не короче {WebPasswordStore.MinLength} символов.", 2);

        try
        {
            new WebPasswordStore(config.WebPasswordFile).Set(password);
        }
        catch (UnauthorizedAccessException)
        {
            return Print($"Нет доступа к {config.WebPasswordFile} — запустите от имени администратора.", 1);
        }

        Console.WriteLine("Пароль сохранён. Открытые сеансы веб-интерфейса завершены.");
        Console.WriteLine($"Веб-интерфейс: {string.Join(", ", config.Web.Urls)}");
        return 0;
    }

    private static string ReadHidden()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return sb.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
    }

    private static int Protect(List<string> rest)
    {
        if (rest.Count == 0) return Print("Укажите текст: MyBackup.exe protect \"пароль\"", 2);
        Console.WriteLine(Secrets.Protect(string.Join(' ', rest)));
        return 0;
    }

    private static async Task<int> CheckAsync(string path)
    {
        var config = ConfigLoader.Load(path);
        var errors = ConfigLoader.Validate(config);
        Console.WriteLine($"Настройки: {path}");
        if (errors.Count > 0)
        {
            foreach (var e in errors) Console.WriteLine("  ✗ " + e);
            return 2;
        }

        Console.WriteLine("  ✓ ошибок нет");
        var ok = true;
        var (restic, _, log) = CreateRunner(config);
        using (log)
        {
            Console.WriteLine($"restic: {restic.Settings.ExecutablePath}");
            if (!File.Exists(restic.Settings.ExecutablePath))
            {
                Console.WriteLine("  ✗ файл не найден");
                return 1;
            }

            var version = await restic.RunAsync(["version"], CancellationToken.None);
            Console.WriteLine("  ✓ " + version.Stdout.Trim());

            Console.WriteLine($"Хранилище: {restic.Settings.Repository}");
            var (exists, error) = await restic.RepositoryExistsAsync(CancellationToken.None);
            Console.WriteLine(exists switch
            {
                true => "  ✓ доступно",
                false => "  ✗ не создано — выполните MyBackup.exe init",
                null => $"  ✗ недоступно: {error}",
            });
            ok &= exists == true;

            Console.WriteLine(SystemInfo.IsAdministrator()
                ? "VSS: ✓ есть права администратора"
                : "VSS: ✗ нет прав администратора (служба работает как LocalSystem — там права есть)");

            if (config.Telegram.Enabled)
            {
                try
                {
                    using var tg = new TelegramClient(Secrets.Reveal(config.Telegram.BotToken), config.Telegram.Proxy,
                        config.Telegram.ApiBaseUrl);
                    var me = await tg.GetMeAsync(CancellationToken.None);
                    Console.WriteLine($"Telegram: ✓ бот @{me?.Username}");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Telegram: ✗ {e.Message}");
                    ok = false;
                }
            }

            if (config.Web.Enabled)
            {
                var passwordSet = new WebPasswordStore(config.WebPasswordFile).IsSet;
                Console.WriteLine($"Веб-интерфейс: {string.Join(", ", config.Web.Urls)}");
                Console.WriteLine(passwordSet ? "  ✓ пароль задан" : "  ✗ пароль не задан — выполните MyBackup.exe web-password");
                ok &= passwordSet;
            }

            foreach (var job in config.Jobs)
            {
                var next = Cronos.CronExpression.Parse(job.Schedule).GetNextOccurrence(DateTimeOffset.Now, TimeZoneInfo.Local);
                Console.WriteLine($"Задача {job.Name}: {(job.Enabled ? $"следующий запуск {next?.ToLocalTime():dd.MM.yyyy HH:mm}" : "отключена")}, источников: {job.Sources.Count}");
            }
        }

        return ok ? 0 : 1;
    }

    private static async Task<int> InitAsync(string path)
    {
        var config = LoadValid(path);
        var (restic, _, log) = CreateRunner(config);
        using (log)
        {
            var (exists, error) = await restic.RepositoryExistsAsync(CancellationToken.None);
            if (exists == true) return Print("Хранилище уже создано.", 0);
            if (exists is null) return Print($"Хранилище недоступно: {error}", 1);

            var r = await restic.InitAsync(CancellationToken.None);
            if (!r.Success) return Print($"Не удалось создать хранилище: {r.ErrorMessage}", 1);
            Console.WriteLine(r.Stdout.Trim());
            Console.WriteLine();
            Console.WriteLine("ВАЖНО: сохраните пароль хранилища отдельно от сервера. Без него копии не восстановить.");
            return 0;
        }
    }

    private static async Task<int> RunJobAsync(string path, List<string> rest)
    {
        var config = LoadValid(path);
        var job = rest.Count > 0
            ? config.Jobs.FirstOrDefault(j => j.Name.Equals(rest[0], StringComparison.OrdinalIgnoreCase))
            : config.Jobs.Count == 1 ? config.Jobs[0] : null;
        if (job is null)
            return Print($"Укажите задачу: {string.Join(", ", config.Jobs.Select(j => j.Name))}", 2);

        var (restic, logger, log) = CreateRunner(config);
        using (log)
        using (var cts = CancelOnCtrlC())
        {
            var report = await new JobRunner(config, restic, logger).RunAsync(job, "вручную", null, cts.Token);
            new HistoryStore(config.HistoryFile).Append(report);
            Console.WriteLine();
            Console.WriteLine(ReportFormatter.ToPlainText(report));

            if (config.Telegram.Enabled)
            {
                using var tg = new TelegramClient(Secrets.Reveal(config.Telegram.BotToken), config.Telegram.Proxy,
                    config.Telegram.ApiBaseUrl);
                await new TelegramNotifier(tg, config.Telegram.AllowedChatIds, logger)
                    .NotifyAsync(ReportFormatter.ToTelegramHtml(report), CancellationToken.None);
            }

            return report.Status == UnitStatus.Failed ? 1 : 0;
        }
    }

    private static async Task<int> SnapshotsAsync(string path, List<string> rest)
    {
        var config = LoadValid(path);
        var (restic, _, log) = CreateRunner(config);
        using (log)
        {
            var snapshots = await restic.SnapshotsAsync(null, CancellationToken.None);
            var filter = rest.Count > 0 ? string.Join(' ', rest) : null;
            var rows = snapshots
                .Select(s => (Snapshot: s, Name: s.Tags.FirstOrDefault(t => t.StartsWith("name:"))?[5..] ?? "—"))
                .Where(x => filter is null || x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Name).ThenBy(x => x.Snapshot.Time)
                .ToList();

            foreach (var (s, name) in rows)
            {
                var size = s.Summary is null ? "" : Fmt.Size(s.Summary.TotalBytesProcessed);
                Console.WriteLine($"{s.ShortId}  {s.Time.ToLocalTime():dd.MM.yyyy HH:mm}  {size,10}  {name}");
            }

            Console.WriteLine($"Всего снимков: {rows.Count}");
            return 0;
        }
    }

    private static int Profiles(string path)
    {
        var config = ConfigLoader.Load(path);
        var profileSources = config.Jobs.SelectMany(j => j.Sources).OfType<UserProfilesSourceConfig>().ToList();
        if (profileSources.Count == 0) profileSources.Add(new UserProfilesSourceConfig());

        foreach (var src in profileSources)
        {
            var profiles = ProfileDiscovery.Discover(src.ExcludeUsers);
            Console.WriteLine($"Профилей: {profiles.Count} (папки: {string.Join(", ", src.Folders)})");
            foreach (var p in profiles)
                Console.WriteLine($"  {p.UserName,-24} {p.ProfilePath}  {(p.HiveLoaded ? "в системе" : "")}");

            var (paths, notes, users) = UserProfilesSource.ResolvePaths(src, profiles, KnownFolders.ReadShellFolder);
            Console.WriteLine($"Будет скопировано ({users} польз.):");
            foreach (var p in paths) Console.WriteLine("  " + p);
            foreach (var n in notes) Console.WriteLine("  ! " + n);
        }

        var oneC = config.Jobs.SelectMany(j => j.Sources).OfType<OneCServiceSourceConfig>().FirstOrDefault()
                   ?? new OneCServiceSourceConfig();
        var oneCPaths = OneCServiceSource.CollectPaths(oneC, ProfileDiscovery.Discover([]));
        Console.WriteLine($"Служебные файлы 1С: {oneCPaths.Count}");
        foreach (var p in oneCPaths) Console.WriteLine("  " + p);
        return 0;
    }

    private static async Task<int> TelegramTestAsync(string path)
    {
        var config = ConfigLoader.Load(path);
        if (string.IsNullOrWhiteSpace(config.Telegram.BotToken))
            return Print("Укажите telegram.botToken (токен от @BotFather).", 2);

        using var tg = new TelegramClient(Secrets.Reveal(config.Telegram.BotToken), config.Telegram.Proxy,
            config.Telegram.ApiBaseUrl);
        var me = await tg.GetMeAsync(CancellationToken.None);
        Console.WriteLine($"Бот: @{me?.Username}");

        if (config.Telegram.AllowedChatIds.Count == 0)
        {
            Console.WriteLine("allowedChatIds пуст. Напишите боту любое сообщение и запустите команду снова.");
            Console.WriteLine("(Служба MyBackup в этот момент должна быть остановлена.)");
            var updates = await tg.GetUpdatesAsync(0, 0, CancellationToken.None);
            foreach (var chat in updates.Where(u => u.Message is not null).Select(u => u.Message!.Chat).DistinctBy(c => c.Id))
                Console.WriteLine($"  chat_id {chat.Id}  {chat.Type}  {chat.Title ?? chat.Username}");
            return 0;
        }

        foreach (var chatId in config.Telegram.AllowedChatIds)
        {
            await tg.SendMessageAsync(chatId,
                $"🔔 <b>{Fmt.HtmlEscape(config.ServerName)}</b>: проверка уведомлений MyBackup", CancellationToken.None);
            Console.WriteLine($"  ✓ отправлено в {chatId}");
        }

        return 0;
    }

    private static async Task<int> ResticPassthroughAsync(string path, List<string> rest)
    {
        var config = LoadValid(path);
        var (restic, _, log) = CreateRunner(config);
        using (log)
        {
            Console.CancelKeyPress += (_, e) => e.Cancel = true; // Ctrl+C получит сам restic
            using var process = Process.Start(restic.CreatePassthroughStartInfo(rest))!;
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
    }
}
