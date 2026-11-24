using System.Text.Json.Serialization;

namespace MyBackup.Core.Config;

/// <summary>Корень файла настроек (config.json).</summary>
public sealed class AppConfig
{
    /// <summary>Имя сервера в отчётах и в снимках restic (--host).</summary>
    public string ServerName { get; set; } = Environment.MachineName;

    /// <summary>Папка для журналов, истории и временных файлов. По умолчанию %ProgramData%\MyBackup.</summary>
    public string? DataDirectory { get; set; }

    public ResticConfig Restic { get; set; } = new();
    public RetryConfig Retry { get; set; } = new();
    public BandwidthConfig Bandwidth { get; set; } = new();
    public TelegramConfig Telegram { get; set; } = new();
    public WebConfig Web { get; set; } = new();
    public List<JobConfig> Jobs { get; set; } = [];

    [JsonIgnore]
    public string ResolvedDataDirectory => string.IsNullOrWhiteSpace(DataDirectory)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MyBackup")
        : Path.GetFullPath(Environment.ExpandEnvironmentVariables(DataDirectory));

    [JsonIgnore] public string StagingDirectory => Path.Combine(ResolvedDataDirectory, "staging");
    [JsonIgnore] public string LogDirectory => Path.Combine(ResolvedDataDirectory, "logs");
    [JsonIgnore] public string HistoryFile => Path.Combine(ResolvedDataDirectory, "history.jsonl");
    [JsonIgnore] public string WebPasswordFile => Path.Combine(ResolvedDataDirectory, "web-password");
    [JsonIgnore] public string WebKeysDirectory => Path.Combine(ResolvedDataDirectory, "web-keys");
    [JsonIgnore] public string ConfigBackupDirectory => Path.Combine(ResolvedDataDirectory, "config-backups");
}

public sealed class ResticConfig
{
    /// <summary>Путь к restic.exe; относительный путь считается от папки программы.</summary>
    public string Executable { get; set; } = "restic.exe";

    /// <summary>Адрес хранилища: rest:https://..., s3:..., sftp:..., D:\repo, \\nas\repo.</summary>
    public string Repository { get; set; } = "";

    /// <summary>Пароль хранилища. Лучше хранить в виде "dpapi:..." (команда MyBackup protect).</summary>
    public string Password { get; set; } = "";

    public string? CacheDirectory { get; set; }

    /// <summary>Дополнительные переменные окружения (например, ключи S3). Значения могут быть "dpapi:...".</summary>
    public Dictionary<string, string> Environment { get; set; } = new();

    /// <summary>Размер пакета restic в МиБ (4–128). Меньше — меньше потерь при обрыве.</summary>
    public int? PackSizeMiB { get; set; }

    /// <summary>auto | off | max.</summary>
    public string? Compression { get; set; }

    /// <summary>Использовать VSS-снимки для открытых файлов (нужны права администратора).</summary>
    public bool UseVss { get; set; } = true;
}

public sealed class RetryConfig
{
    public int MaxAttempts { get; set; } = 10;
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(30);
}

public sealed class BandwidthConfig
{
    /// <summary>Рабочее время в формате "08:00-20:00". Пусто — ограничение не меняется по времени.</summary>
    public string? WorkHours { get; set; }

    public List<DayOfWeek> WorkDays { get; set; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    /// <summary>Ограничение отправки в рабочее время, КиБ/с. 0 — без ограничения.</summary>
    public int WorkHoursUploadLimitKiB { get; set; }

    /// <summary>Ограничение отправки в нерабочее время, КиБ/с. 0 — без ограничения.</summary>
    public int OffHoursUploadLimitKiB { get; set; }
}

public sealed class TelegramConfig
{
    public bool Enabled { get; set; }
    public string BotToken { get; set; } = "";
    public List<long> AllowedChatIds { get; set; } = [];

    /// <summary>Прокси для api.telegram.org: http://host:port или socks5://host:port.</summary>
    public string? Proxy { get; set; }

    public bool NotifyOnStart { get; set; }
    public bool EnableCommands { get; set; } = true;
    public string ApiBaseUrl { get; set; } = "https://api.telegram.org";
}

public sealed class WebConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Адреса веб-интерфейса. По умолчанию — только с самого сервера. Для доступа по сети (через VPN)
    /// добавьте, например, "https://0.0.0.0:8443" и сертификат.
    /// </summary>
    public List<string> Urls { get; set; } = ["http://127.0.0.1:8480"];

    /// <summary>Сертификат .pfx для https-адресов.</summary>
    public string? CertificatePath { get; set; }

    /// <summary>Пароль сертификата; можно "dpapi:...".</summary>
    public string? CertificatePassword { get; set; }
}

public sealed class JobConfig
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Расписание в формате cron из 5 полей: "0 3 * * *" — каждый день в 03:00.</summary>
    public string Schedule { get; set; } = "0 3 * * *";

    /// <summary>Сколько времени задача может повторять попытки после старта.</summary>
    public TimeSpan MaxDuration { get; set; } = TimeSpan.FromHours(6);

    public List<SourceConfig> Sources { get; set; } = [];
    public RetentionConfig Retention { get; set; } = new();
}

public sealed class RetentionConfig
{
    /// <summary>
    /// Удалять старые снимки с этого сервера. Для хранилища в режиме append-only должно быть false:
    /// очистка выполняется на стороне хранилища.
    /// </summary>
    public bool ApplyFromClient { get; set; }

    public int KeepDaily { get; set; } = 7;
    public int KeepWeekly { get; set; } = 4;
    public int KeepMonthly { get; set; } = 12;
    public bool Prune { get; set; }
}
