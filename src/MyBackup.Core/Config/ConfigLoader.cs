using System.Text.Json;
using System.Text.Json.Serialization;
using Cronos;
using MyBackup.Core.Jobs;

namespace MyBackup.Core.Config;

public sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);

public static class ConfigLoader
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        AllowOutOfOrderMetadataProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Для записи файла: без пустых полей, кириллица не экранируется.</summary>
    public static JsonSerializerOptions WriteOptions { get; } = new(JsonOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyBackup", "config.json");

    public static string Serialize(AppConfig config) => JsonSerializer.Serialize(config, WriteOptions);

    /// <summary>Глубокая копия через JSON.</summary>
    public static AppConfig Clone(AppConfig config) => Parse(Serialize(config));

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path)) throw new ConfigException($"Файл настроек не найден: {path}");
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (JsonException e)
        {
            throw new ConfigException($"Ошибка в файле настроек {path}: {e.Message}", e);
        }
    }

    public static AppConfig Parse(string json) =>
        JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? throw new ConfigException("Файл настроек пуст");

    /// <summary>Проверяет настройки и возвращает список ошибок (пустой — всё в порядке).</summary>
    public static IReadOnlyList<string> Validate(AppConfig c)
    {
        var errors = new List<string>();
        ValidateEngine(c, errors);

        // Пустой allowedChatIds допустим: бот запоминает, кто ему писал, и чат можно разрешить в веб-интерфейсе.
        if (c.Telegram.Enabled)
        {
            if (string.IsNullOrWhiteSpace(c.Telegram.BotToken)) errors.Add("telegram.botToken: не указан токен бота");
            CheckSecret(c.Telegram.BotToken, "telegram.botToken", errors);
            CheckSecret(c.Telegram.Proxy, "telegram.proxy", errors);
            if (!string.IsNullOrWhiteSpace(c.Telegram.Proxy) && !IsProtectedOrUrl(c.Telegram.Proxy))
                errors.Add("telegram.proxy: ожидается адрес вида socks5://host:port");
        }

        if (c.Web.Enabled)
        {
            foreach (var url in c.Web.Urls)
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
                    errors.Add($"web.urls: \"{url}\" — ожидается адрес вида http://127.0.0.1:8480");
                else if (u.Scheme == "https" && string.IsNullOrWhiteSpace(c.Web.CertificatePath))
                    errors.Add($"web.certificatePath: для {url} нужен сертификат .pfx");
            }

            CheckSecret(c.Web.CertificatePassword, "web.certificatePassword", errors);
        }

        if (c.Jobs.Count == 0) errors.Add("jobs: нет ни одной задачи");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in c.Jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Name)) errors.Add("jobs: у задачи не указано имя");
            else if (!names.Add(job.Name)) errors.Add($"jobs: имя задачи \"{job.Name}\" повторяется");
            ValidateJob(job, errors);
        }

        return errors;
    }

    /// <summary>
    /// Ошибки, из-за которых задачу нельзя выполнить: хранилище, повторы, скорость и сама задача.
    /// Ошибки Telegram, веб-интерфейса и других задач копированию не мешают.
    /// </summary>
    public static IReadOnlyList<string> ValidateForRun(AppConfig c, JobConfig job)
    {
        var errors = new List<string>();
        ValidateEngine(c, errors);
        ValidateJob(job, errors);
        return errors;
    }

    private static void ValidateEngine(AppConfig c, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(c.Restic.Repository)) errors.Add("restic.repository: не указан адрес хранилища");
        if (string.IsNullOrWhiteSpace(c.Restic.Password)) errors.Add("restic.password: не указан пароль хранилища");
        CheckSecret(c.Restic.Repository, "restic.repository", errors);
        CheckSecret(c.Restic.Password, "restic.password", errors);
        foreach (var (key, value) in c.Restic.Environment)
        {
            if (Restic.ResticRunner.IsForbiddenVariable(key))
                errors.Add($"restic.environment.{key}: эту переменную задавать нельзя (адрес и пароль — в repository и password)");
            CheckSecret(value, $"restic.environment.{key}", errors);
        }

        if (c.Restic.PackSizeMiB is < 4 or > 128) errors.Add("restic.packSizeMiB: допустимо от 4 до 128");
        if (c.Restic.Compression is { Length: > 0 } comp && comp is not ("auto" or "off" or "max"))
            errors.Add("restic.compression: допустимо auto, off или max");
        if (c.Retry.MaxAttempts < 1) errors.Add("retry.maxAttempts: должно быть не меньше 1");
        if (c.Retry.InitialDelay <= TimeSpan.Zero || c.Retry.MaxDelay < c.Retry.InitialDelay)
            errors.Add("retry: пауза должна быть больше нуля, а максимальная — не меньше начальной");

        if (!string.IsNullOrWhiteSpace(c.Bandwidth.WorkHours) && !BandwidthSchedule.TryParseHours(c.Bandwidth.WorkHours, out _, out _))
            errors.Add("bandwidth.workHours: ожидается формат \"08:00-20:00\" (начало раньше конца)");
        if (c.Bandwidth.WorkHoursUploadLimitKiB < 0 || c.Bandwidth.OffHoursUploadLimitKiB < 0)
            errors.Add("bandwidth: ограничение скорости не может быть отрицательным");
    }

    private static void ValidateJob(JobConfig job, List<string> errors)
    {
        var label = string.IsNullOrWhiteSpace(job.Name) ? "(без имени)" : job.Name;
        try
        {
            CronExpression.Parse(job.Schedule);
        }
        catch (CronFormatException)
        {
            errors.Add($"задача {label}: неверное расписание \"{job.Schedule}\" (ожидается cron из 5 полей, например \"0 3 * * *\")");
        }

        if (job.MaxDuration <= TimeSpan.Zero) errors.Add($"задача {label}: maxDuration должно быть больше нуля");
        if (job.Sources.Count == 0) errors.Add($"задача {label}: не указано, что копировать (sources)");

        foreach (var s in job.Sources) ValidateSource(s, label, errors);

        // Имя источника — тег цепочки снимков: у двух источников одной задачи оно должно различаться,
        // иначе правила хранения смешают их снимки. Для баз имя дополняется именем базы.
        var sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in job.Sources.Where(s => s is not (MsSqlSourceConfig or PostgreSqlSourceConfig)))
            if (!string.IsNullOrWhiteSpace(s.EffectiveName) && !sourceNames.Add(s.EffectiveName))
                errors.Add($"задача {label}: два источника с именем \"{s.EffectiveName}\" — задайте разные имена");
    }

    private static void CheckSecret(string? value, string field, List<string> errors)
    {
        if (!Secrets.IsProtected(value)) return;
        try
        {
            Secrets.Reveal(value);
        }
        catch (ConfigException e) when (e.InnerException is FormatException)
        {
            // "dpapi:ЗАМЕНИТЕ" из примера настроек — это не зашифрованное значение, а заготовка.
            errors.Add($"{field}: не заполнено (в настройках заготовка из примера) — введите значение");
        }
        catch (ConfigException)
        {
            errors.Add($"{field}: не удаётся расшифровать значение dpapi — оно создано на другом компьютере или повреждено; введите заново");
        }
    }

    private static bool IsProtectedOrUrl(string value) =>
        Secrets.IsProtected(value) || Uri.TryCreate(value, UriKind.Absolute, out _);

    private static void ValidateSource(SourceConfig s, string job, List<string> errors)
    {
        var where = $"задача {job}, источник {s.TypeName}{(string.IsNullOrEmpty(s.Name) ? "" : $" \"{s.Name}\"")}";
        switch (s)
        {
            case FileBase1CSourceConfig f when string.IsNullOrWhiteSpace(f.Path):
                errors.Add($"{where}: не указан path — папка базы");
                break;
            case MsSqlSourceConfig m:
                if (string.IsNullOrWhiteSpace(m.ConnectionString)) errors.Add($"{where}: не указан connectionString");
                CheckSecret(m.ConnectionString, $"{where}: connectionString", errors);
                if (m.Databases.Count == 0) errors.Add($"{where}: не указаны databases (имена баз или \"*\")");
                break;
            case PostgreSqlSourceConfig p:
                if (string.IsNullOrWhiteSpace(p.PgDumpPath)) errors.Add($"{where}: не указан pgDumpPath — путь к pg_dump.exe");
                else if (!System.IO.Path.GetFileName(p.PgDumpPath.Trim()).Equals("pg_dump.exe", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{where}: pgDumpPath должен указывать на pg_dump.exe");
                CheckSecret(p.Password, $"{where}: password", errors);
                if (p.Databases.Count == 0) errors.Add($"{where}: не указаны databases (имена баз или \"*\")");
                break;
            case OneCDumpSourceConfig d:
                var hasFile = !string.IsNullOrWhiteSpace(d.FilePath);
                var hasServer = !string.IsNullOrWhiteSpace(d.ServerBase);
                if (hasFile == hasServer)
                    errors.Add($"{where}: укажите либо папку файловой базы (filePath), либо серверную базу (serverBase)");
                else if (hasServer && !d.ServerBase!.Trim().Contains('\\'))
                    errors.Add($"{where}: серверная база указывается как сервер\\имя_базы");
                if (!string.IsNullOrWhiteSpace(d.PlatformPath)
                    && !Path.GetFileName(d.PlatformPath.Trim()).Equals("1cv8.exe", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{where}: platformPath должен указывать на 1cv8.exe");
                if (d.Timeout <= TimeSpan.Zero) errors.Add($"{where}: timeout должно быть больше нуля");
                CheckSecret(d.Password, $"{where}: password", errors);
                break;
            case PathsSourceConfig ps when ps.Paths.Count == 0:
                errors.Add($"{where}: не указаны paths");
                break;
        }
    }
}
