using System.Text.RegularExpressions;

namespace MyBackup.Core.Config;

/// <summary>
/// Пароли и токены не уходят в браузер. Вместо них отдаётся <see cref="Mask"/> (в адресах и строках
/// подключения маскируется только пароль). При сохранении маска заменяется прежним значением,
/// а новые пароли шифруются DPAPI.
/// </summary>
public static partial class ConfigMasking
{
    public const string Mask = "••••••";

    public static AppConfig ForClient(AppConfig config)
    {
        var c = ConfigLoader.Clone(config);
        c.Restic.Password = MaskPlain(c.Restic.Password);
        c.Restic.Repository = MaskUrl(c.Restic.Repository);
        foreach (var key in c.Restic.Environment.Keys.ToList()) c.Restic.Environment[key] = MaskPlain(c.Restic.Environment[key]);
        c.Telegram.BotToken = MaskPlain(c.Telegram.BotToken);
        c.Telegram.Proxy = c.Telegram.Proxy is null ? null : MaskUrl(c.Telegram.Proxy);
        c.Web.CertificatePassword = c.Web.CertificatePassword is null ? null : MaskPlain(c.Web.CertificatePassword);

        foreach (var source in c.Jobs.SelectMany(j => j.Sources))
        {
            switch (source)
            {
                case MsSqlSourceConfig m:
                    m.ConnectionString = MaskConnectionString(m.ConnectionString);
                    break;
                case PostgreSqlSourceConfig p:
                    p.Password = MaskPlain(p.Password);
                    break;
                case OneCDumpSourceConfig d:
                    d.Password = d.Password is null ? null : MaskPlain(d.Password);
                    break;
            }
        }

        return c;
    }

    /// <summary>
    /// Подставляет прежние значения вместо масок и шифрует новые секреты. Ошибки — если маску
    /// не к чему привязать (например, источник с паролем переименован и одновременно перемещён).
    /// </summary>
    public static (AppConfig Config, IReadOnlyList<string> Errors) MergeFromClient(AppConfig incoming, AppConfig current)
    {
        var c = ConfigLoader.Clone(incoming);
        var errors = new List<string>();

        c.Restic.Password = Resolve(c.Restic.Password, current.Restic.Password, Kind.Plain, "restic.password", errors);
        c.Restic.Repository = Resolve(c.Restic.Repository, current.Restic.Repository, Kind.Url, "restic.repository", errors);
        foreach (var key in c.Restic.Environment.Keys.ToList())
            c.Restic.Environment[key] = Resolve(c.Restic.Environment[key],
                current.Restic.Environment.GetValueOrDefault(key), Kind.Plain, $"restic.environment.{key}", errors);
        c.Telegram.BotToken = Resolve(c.Telegram.BotToken, current.Telegram.BotToken, Kind.Plain, "telegram.botToken", errors);
        if (c.Telegram.Proxy is not null)
            c.Telegram.Proxy = Resolve(c.Telegram.Proxy, current.Telegram.Proxy, Kind.Url, "telegram.proxy", errors);
        if (c.Web.CertificatePassword is not null)
            c.Web.CertificatePassword = Resolve(c.Web.CertificatePassword, current.Web.CertificatePassword, Kind.Plain,
                "web.certificatePassword", errors);

        for (var j = 0; j < c.Jobs.Count; j++)
        {
            var job = c.Jobs[j];
            var oldJob = current.Jobs.FirstOrDefault(x => x.Name.Equals(job.Name, StringComparison.OrdinalIgnoreCase))
                         ?? (j < current.Jobs.Count ? current.Jobs[j] : null);
            for (var i = 0; i < job.Sources.Count; i++)
            {
                var label = $"задача {job.Name}, источник {job.Sources[i].EffectiveName}";
                switch (job.Sources[i])
                {
                    case MsSqlSourceConfig m:
                        m.ConnectionString = Resolve(m.ConnectionString, FindOld(oldJob, m, i)?.ConnectionString,
                            Kind.ConnectionString, label + ": строка подключения", errors);
                        break;
                    case PostgreSqlSourceConfig p:
                        p.Password = Resolve(p.Password, FindOld(oldJob, p, i)?.Password, Kind.Plain, label + ": пароль", errors);
                        break;
                    case OneCDumpSourceConfig d when d.Password is not null:
                        d.Password = Resolve(d.Password, FindOld(oldJob, d, i)?.Password, Kind.Plain, label + ": пароль 1С", errors);
                        break;
                }
            }
        }

        return (c, errors);
    }

    /// <summary>Тот же источник в прежней версии: по имени, иначе по позиции.</summary>
    private static T? FindOld<T>(JobConfig? oldJob, T source, int index) where T : SourceConfig
    {
        if (oldJob is null) return null;
        var sameType = oldJob.Sources.OfType<T>().ToList();
        return sameType.FirstOrDefault(s => s.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase))
               ?? (index < oldJob.Sources.Count ? oldJob.Sources[index] as T : null);
    }

    private enum Kind
    {
        Plain,
        Url,
        ConnectionString,
    }

    private static string Resolve(string? incoming, string? stored, Kind kind, string field, List<string> errors)
    {
        incoming ??= "";
        stored ??= "";
        if (Secrets.IsProtected(incoming)) return incoming; // вставили уже зашифрованное значение
        if (incoming == MaskFor(stored, kind)) incoming = Secrets.IsProtected(stored) ? stored : SafeReveal(stored); // не менялось
        if (Secrets.IsProtected(incoming)) return incoming;

        if (incoming.Contains(Mask, StringComparison.Ordinal))
        {
            // Изменили адрес, но оставили замаскированный пароль — подставляем прежний.
            var oldPassword = kind switch
            {
                Kind.Url => UrlPassword().Match(SafeReveal(stored)) is { Success: true } m ? m.Groups["pw"].Value : null,
                Kind.ConnectionString => ConnPassword().Match(SafeReveal(stored)) is { Success: true } m ? m.Groups["pw"].Value : null,
                _ => null,
            };
            if (oldPassword is null)
            {
                errors.Add($"{field}: введите значение заново");
                return stored;
            }

            incoming = incoming.Replace(Mask, oldPassword, StringComparison.Ordinal);
        }

        var hasSecret = kind switch
        {
            Kind.Plain => incoming.Length > 0,
            Kind.Url => UrlPassword().IsMatch(incoming),
            Kind.ConnectionString => ConnPassword().IsMatch(incoming),
            _ => false,
        };
        return hasSecret ? Secrets.Protect(incoming) : incoming;
    }

    private static string MaskFor(string stored, Kind kind) => kind switch
    {
        Kind.Plain => MaskPlain(stored),
        Kind.Url => MaskUrl(stored),
        _ => MaskConnectionString(stored),
    };

    private static string MaskPlain(string? value) => string.IsNullOrEmpty(value) ? "" : Mask;

    /// <summary>rest:https://user:пароль@host/ → rest:https://user:••••••@host/.</summary>
    public static string MaskUrl(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var plain = SafeReveal(value);
        if (plain.Length == 0) return Mask;
        var m = UrlPassword().Match(plain);
        return m.Success ? plain[..m.Groups["pw"].Index] + Mask + plain[(m.Groups["pw"].Index + m.Groups["pw"].Length)..] : plain;
    }

    /// <summary>...;Password=секрет;... → ...;Password=••••••;...</summary>
    public static string MaskConnectionString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var plain = SafeReveal(value);
        if (plain.Length == 0) return Mask;
        return ConnPassword().Replace(plain, m => m.Groups["key"].Value + Mask);
    }

    /// <summary>Значение, которое не удалось расшифровать, показывается как маска целиком.</summary>
    private static string SafeReveal(string value)
    {
        try
        {
            return Secrets.Reveal(value);
        }
        catch (ConfigException)
        {
            return "";
        }
    }

    [GeneratedRegex(@"^.*?://[^/:@]*:(?<pw>[^@/]+)@")]
    private static partial Regex UrlPassword();

    [GeneratedRegex(@"(?<key>(?:^|;)\s*(?:password|pwd)\s*=\s*)(?<pw>""[^""]*""|'[^']*'|[^;]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ConnPassword();
}
