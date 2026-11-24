using MyBackup.Core.Config;
using MyBackup.Core.Jobs;
using MyBackup.Core.Notifications;
using MyBackup.Core.Restic;
using MyBackup.Core.Util;

namespace MyBackup.Core.Tests;

public class FmtTests
{
    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(1536, "1,5 КБ")]
    [InlineData(4509715660, "4,2 ГБ")]
    [InlineData(200L * 1024 * 1024, "200 МБ")]
    public void Size_IsHumanReadableRussian(long bytes, string expected) => Assert.Equal(expected, Fmt.Size(bytes));

    [Fact]
    public void Duration_Formats()
    {
        Assert.Equal("45 с", Fmt.Duration(TimeSpan.FromSeconds(45)));
        Assert.Equal("41 мин", Fmt.Duration(TimeSpan.FromMinutes(41)));
        Assert.Equal("1 ч 05 мин", Fmt.Duration(TimeSpan.FromMinutes(65)));
    }
}

public class ResticJsonTests
{
    [Fact]
    public void ParsesSummary()
    {
        const string line = """{"message_type":"summary","files_new":2,"files_changed":1,"files_unmodified":5,"dirs_new":1,"data_blobs":2,"tree_blobs":2,"data_added":1234,"data_added_packed":800,"total_files_processed":8,"total_bytes_processed":99999,"total_duration":0.52,"snapshot_id":"abc123"}""";
        var msg = ResticJson.TryParse(line);
        Assert.NotNull(msg);
        var s = msg.ToSummary();
        Assert.Equal("summary", msg.MessageType);
        Assert.Equal(800, s.DataAddedPacked);
        Assert.Equal(99999, s.TotalBytesProcessed);
        Assert.Equal("abc123", s.SnapshotId);
    }

    [Fact]
    public void ParsesStatus()
    {
        var msg = ResticJson.TryParse("""{"message_type":"status","percent_done":0.5,"total_files":10,"files_done":5,"total_bytes":2000,"bytes_done":1000,"seconds_elapsed":12,"seconds_remaining":12.5}""");
        Assert.NotNull(msg);
        var st = msg.ToStatus();
        Assert.Equal(0.5, st.PercentDone);
        Assert.Equal(12.5, st.SecondsRemaining);
    }

    [Theory]
    [InlineData("""{"message_type":"error","error":{"message":"access denied"},"during":"archival","item":"C:\\a.txt"}""", "access denied")]
    [InlineData("""{"message_type":"error","error":"access denied","during":"archival","item":"C:\\a.txt"}""", "access denied")]
    public void ParsesErrorInBothShapes(string line, string expected)
    {
        var msg = ResticJson.TryParse(line);
        Assert.NotNull(msg);
        Assert.Equal(expected, msg.ErrorText);
        Assert.Equal(@"C:\a.txt", msg.Item);
    }

    [Fact]
    public void IgnoresNonJson()
    {
        Assert.Null(ResticJson.TryParse("Fatal: unable to open repository"));
        Assert.Null(ResticJson.TryParse(""));
        Assert.Null(ResticJson.TryParse("{broken"));
    }

    [Theory]
    [InlineData(0, ResticOutcome.Success)]
    [InlineData(3, ResticOutcome.Partial)]
    [InlineData(10, ResticOutcome.Fatal)]
    [InlineData(12, ResticOutcome.Fatal)]
    [InlineData(11, ResticOutcome.Retryable)]
    [InlineData(1, ResticOutcome.Retryable)]
    public void ClassifiesExitCodes(int code, ResticOutcome expected) => Assert.Equal(expected, ResticJson.Classify(code));
}

public class RetryPolicyTests
{
    [Fact]
    public void DoublesUpToMax()
    {
        var p = new RetryPolicy(new RetryConfig { InitialDelay = TimeSpan.FromMinutes(1), MaxDelay = TimeSpan.FromMinutes(30) });
        var delays = Enumerable.Range(1, 7).Select(i => p.GetDelay(i).TotalMinutes).ToArray();
        Assert.Equal([1, 2, 4, 8, 16, 30, 30], delays);
    }
}

public class BandwidthScheduleTests
{
    private static readonly DateTime Monday = new(2026, 11, 23);

    private static BandwidthSchedule Create(int work = 1024, int off = 0) => new(new BandwidthConfig
    {
        WorkHours = "08:00-20:00", WorkHoursUploadLimitKiB = work, OffHoursUploadLimitKiB = off,
    });

    [Fact]
    public void LimitsDuringWorkHoursOnly()
    {
        Assert.Equal(DayOfWeek.Monday, Monday.DayOfWeek);
        var s = Create();
        Assert.Equal(1024, s.LimitAt(Monday.AddHours(10)));
        Assert.Null(s.LimitAt(Monday.AddHours(21)));
        Assert.Null(s.LimitAt(Monday.AddDays(5).AddHours(10))); // суббота
    }

    [Fact]
    public void NextChangeFindsBoundaries()
    {
        var s = Create();
        Assert.Equal(Monday.AddHours(20), s.NextChange(Monday.AddHours(10)));
        Assert.Equal(Monday.AddDays(1).AddHours(8), s.NextChange(Monday.AddHours(21)));
        Assert.Equal(Monday.AddDays(7).AddHours(8), s.NextChange(Monday.AddDays(4).AddHours(21))); // пятница → понедельник
    }

    [Fact]
    public void NoChangeWhenLimitsEqualOrNoHours()
    {
        Assert.Null(Create(500, 500).NextChange(Monday.AddHours(10)));
        Assert.Null(new BandwidthSchedule(new BandwidthConfig()).NextChange(Monday));
    }

    [Theory]
    [InlineData("08:00-20:00", true)]
    [InlineData("8:30-17:45", true)]
    [InlineData("20:00-08:00", false)]
    [InlineData("утро", false)]
    public void ParsesHours(string text, bool ok) => Assert.Equal(ok, BandwidthSchedule.TryParseHours(text, out _, out _));
}

public class SecretsTests
{
    [Fact]
    public void ProtectRevealRoundTrip()
    {
        var protectedValue = Secrets.Protect("пароль-123");
        Assert.StartsWith("dpapi:", protectedValue);
        Assert.DoesNotContain("пароль", protectedValue);
        Assert.Equal("пароль-123", Secrets.Reveal(protectedValue));
    }

    [Fact]
    public void PlainValueIsReturnedAsIs() => Assert.Equal("plain", Secrets.Reveal("plain"));

    [Fact]
    public void ForeignValueGivesClearError() =>
        Assert.Contains("другом компьютере",
            Assert.Throws<ConfigException>(() => Secrets.Reveal("dpapi:" + Convert.ToBase64String(new byte[64]))).Message);

    [Fact]
    public void ExamplePlaceholderSaysNotFilled() =>
        Assert.Contains("не заполнено", Assert.Throws<ConfigException>(() => Secrets.Reveal("dpapi:ЗАМЕНИТЕ")).Message);
}

public class ConfigLoaderTests
{
    [Fact]
    public void ExampleConfigIsValidExceptPlaceholders()
    {
        var config = ConfigLoader.Load(Path.Combine(TestPaths.RepoRoot, "src", "MyBackup.Service", "config.example.json"));
        // Ошибки только в местах «ЗАМЕНИТЕ»: пароль и токен вводятся на сервере.
        Assert.All(ConfigLoader.Validate(config), e => Assert.Matches("^(restic.password|telegram.botToken):", e));
        Assert.Equal(["http://127.0.0.1:8480"], config.Web.Urls);

        var sources = config.Jobs.Single().Sources;
        Assert.Contains(sources, s => s is FileBase1CSourceConfig { Name: "Бухгалтерия" });
        Assert.Contains(sources, s => s is MsSqlSourceConfig m && m.Databases.Contains("*"));
        Assert.Contains(sources, s => s is UserProfilesSourceConfig);
        Assert.Contains(sources, s => s is PathsSourceConfig { Paths.Count: 2 });
        Assert.Contains(sources, s => s is OneCServiceSourceConfig);
        Assert.Equal(1024, config.Bandwidth.WorkHoursUploadLimitKiB);
        Assert.Equal(TimeSpan.FromHours(6), config.Jobs[0].MaxDuration);
    }

    [Fact]
    public void TypeDiscriminatorMayBeAnywhere()
    {
        var c = ConfigLoader.Parse("""{ "jobs": [ { "name": "j", "sources": [ { "path": "D:\\b", "type": "fileBase1C" } ] } ] }""");
        Assert.IsType<FileBase1CSourceConfig>(c.Jobs[0].Sources[0]);
    }

    [Fact]
    public void TyposAreReported() =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => ConfigLoader.Parse("""{ "restik": {} }"""));

    [Fact]
    public void ValidationFindsProblems()
    {
        var c = ConfigLoader.Parse("""
            { "telegram": { "enabled": true },
              "jobs": [ { "name": "a", "schedule": "каждый день", "sources": [ { "type": "msSql" } ] },
                        { "name": "A", "sources": [] } ] }
            """);
        var errors = ConfigLoader.Validate(c);
        Assert.Contains(errors, e => e.StartsWith("restic.repository"));
        Assert.Contains(errors, e => e.StartsWith("telegram.botToken"));
        Assert.Contains(errors, e => e.Contains("неверное расписание"));
        Assert.Contains(errors, e => e.Contains("повторяется"));
        Assert.Contains(errors, e => e.Contains("databases"));
    }
}

public class ReportFormatterTests
{
    private static RunReport Sample() => new()
    {
        JobName = "nightly",
        ServerName = "RDP<1C>",
        Started = new DateTimeOffset(2026, 11, 22, 3, 0, 0, TimeSpan.FromHours(3)),
        Finished = new DateTimeOffset(2026, 11, 22, 3, 41, 0, TimeSpan.FromHours(3)),
        Units =
        [
            new UnitResult { Title = "1С: Бухгалтерия", Status = UnitStatus.Ok, BytesProcessed = 4509715660, DataAddedPacked = 120 * 1024 * 1024, Attempts = 3 },
            new UnitResult { Title = "SQL: УТ", Status = UnitStatus.Failed, Message = "ошибка <sql>", Attempts = 1 },
        ],
    };

    [Fact]
    public void TelegramHtmlHasStatusAndEscapes()
    {
        var html = ReportFormatter.ToTelegramHtml(Sample());
        Assert.StartsWith("❌ <b>RDP&lt;1C&gt;</b>", html);
        Assert.Contains("✅ 1С: Бухгалтерия — 4,2 ГБ, новых 120 МБ", html);
        Assert.Contains("ошибка &lt;sql&gt;", html);
        Assert.Contains("повторов после обрывов: 2", html);
    }

    [Fact]
    public void TelegramHtmlIsTruncated()
    {
        var r = Sample();
        for (var i = 0; i < 200; i++) r.Units.Add(new UnitResult { Title = new string('x', 50), Status = UnitStatus.Ok });
        Assert.True(ReportFormatter.ToTelegramHtml(r).Length <= ReportFormatter.TelegramLimit + 2);
    }
}

public class BotCommandsTests
{
    private readonly List<(string Job, string Trigger)> _enqueued = [];

    private BotContext Context(params string[] jobs) => new()
    {
        ServerName = "srv",
        Jobs = jobs.Select(j => new JobConfig { Name = j }).ToList(),
        Current = () => null,
        NextRun = _ => new DateTimeOffset(2026, 11, 23, 1, 0, 0, TimeSpan.Zero),
        History = (_, _) => [],
        Enqueue = (j, t) =>
        {
            if (_enqueued.Any(e => e.Job == j)) return false;
            _enqueued.Add((j, t));
            return true;
        },
    };

    [Fact]
    public void ParsesBotSuffixAndArgument() => Assert.Equal(("/run", "nightly"), BotCommands.Parse("/Run@MyBackupBot  nightly"));

    [Fact]
    public void IgnoresPlainText() => Assert.Null(BotCommands.Handle("привет", Context("nightly"), "u"));

    [Fact]
    public void RunSingleJobWithoutArgument()
    {
        var reply = BotCommands.Handle("/run", Context("nightly"), "admin");
        Assert.Contains("поставлена в очередь", reply);
        Assert.Equal(("nightly", "Telegram, admin"), _enqueued.Single());
        Assert.Contains("уже выполняется", BotCommands.Handle("/run nightly", Context("nightly"), "admin"));
    }

    [Fact]
    public void RunAsksWhichJobWhenSeveral()
    {
        Assert.Contains("Укажите задачу", BotCommands.Handle("/run", Context("a", "b"), "u"));
        Assert.Contains("не найдена", BotCommands.Handle("/run c", Context("a", "b"), "u"));
        Assert.Empty(_enqueued);
    }

    [Fact]
    public void StatusWhenIdle()
    {
        var reply = BotCommands.Handle("/status", Context("nightly"), "u");
        Assert.Contains("ничего не выполняется", reply);
        Assert.Contains("Следующий запуск nightly", reply);
    }
}

internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MyBackup.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Не найден корень репозитория (MyBackup.slnx)");
    }

    public static string Restic
    {
        get
        {
            var path = Path.Combine(RepoRoot, "tools", "restic", "restic.exe");
            if (!File.Exists(path)) throw new InvalidOperationException($"Для интеграционных тестов положите restic.exe в {path}");
            return path;
        }
    }
}
