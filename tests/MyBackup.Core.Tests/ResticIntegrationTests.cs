using Microsoft.Extensions.Logging.Abstractions;
using MyBackup.Core.Config;
using MyBackup.Core.Jobs;
using MyBackup.Core.Restic;

namespace MyBackup.Core.Tests;

/// <summary>Временное хранилище restic на диске, общее для тестов класса.</summary>
public sealed class ResticRepoFixture : IDisposable
{
    public const string Password = "test-only-password";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "mybackup-it-" + Guid.NewGuid().ToString("N"));
    public string Repo => Path.Combine(Root, "repo");

    public ResticRepoFixture()
    {
        Directory.CreateDirectory(Root);
        var r = CreateRunner().InitAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (!r.Success) throw new InvalidOperationException("restic init: " + r.ErrorMessage);
    }

    public AppConfig CreateConfig(string? password = null) => new()
    {
        ServerName = "test-host",
        DataDirectory = Path.Combine(Root, "data"),
        Restic = new ResticConfig
        {
            Executable = TestPaths.Restic, Repository = Repo, Password = password ?? Password, UseVss = false,
            CacheDirectory = Path.Combine(Root, "cache"),
        },
        Retry = new RetryConfig { MaxAttempts = 2, InitialDelay = TimeSpan.FromMilliseconds(200), MaxDelay = TimeSpan.FromSeconds(1) },
    };

    public ResticRunner CreateRunner(string? password = null) =>
        new(ResticSettings.FromConfig(CreateConfig(password)), NullLogger.Instance);

    public string NewDir(string name)
    {
        var dir = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // restic мог ещё держать файл кеша — временную папку уберёт система
        }
    }
}

public class ResticIntegrationTests(ResticRepoFixture repo) : IClassFixture<ResticRepoFixture>
{
    [Fact]
    public async Task BackupFolderCreatesTaggedSnapshot()
    {
        var src = repo.NewDir("src");
        await File.WriteAllTextAsync(Path.Combine(src, "файл с пробелом.txt"), "данные");
        var runner = repo.CreateRunner();

        var r = await runner.BackupAsync(new ResticBackupRequest { Paths = [src], Tags = ["mybackup", "name:тест,1"] },
            null, null, CancellationToken.None);

        Assert.Equal(ResticOutcome.Success, r.Outcome);
        Assert.NotNull(r.Summary?.SnapshotId);
        Assert.Equal(1, r.Summary!.TotalFilesProcessed);

        var snapshots = await runner.SnapshotsAsync(["name:тест_1"], CancellationToken.None);
        var snap = Assert.Single(snapshots);
        Assert.Equal("test-host", snap.Hostname);
        Assert.Contains("mybackup", snap.Tags);
    }

    [Fact]
    public async Task StdinCommandSuccessAndFailure()
    {
        var runner = repo.CreateRunner();
        var ok = await runner.BackupAsync(new ResticBackupRequest
        {
            Stdin = new StdinCommand("dump.txt", "cmd.exe", ["/c", "echo", "hello"]), Tags = ["stdin-ok"],
        }, null, null, CancellationToken.None);
        Assert.Equal(ResticOutcome.Success, ok.Outcome);

        var failed = await runner.BackupAsync(new ResticBackupRequest
        {
            Stdin = new StdinCommand("dump.txt", "cmd.exe", ["/c", "exit", "3"]), Tags = ["stdin-fail"],
        }, null, null, CancellationToken.None);
        Assert.Equal(ResticOutcome.Retryable, failed.Outcome);
        Assert.Empty(await runner.SnapshotsAsync(["stdin-fail"], CancellationToken.None));
    }

    [Fact]
    public async Task WrongPasswordIsFatal()
    {
        var src = repo.NewDir("src");
        await File.WriteAllTextAsync(Path.Combine(src, "a.txt"), "a");
        var r = await repo.CreateRunner("wrong").BackupAsync(new ResticBackupRequest { Paths = [src] }, null, null,
            CancellationToken.None);
        Assert.Equal(ResticOutcome.Fatal, r.Outcome);
        Assert.Contains("неверный пароль", r.ErrorMessage);
    }

    [Fact]
    public async Task LockedFileGivesPartialSnapshot()
    {
        var src = repo.NewDir("src");
        await File.WriteAllTextAsync(Path.Combine(src, "ok.txt"), "ok");
        var lockedPath = Path.Combine(src, "locked.dat");
        await File.WriteAllTextAsync(lockedPath, "locked");

        await using var _ = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var r = await repo.CreateRunner().BackupAsync(new ResticBackupRequest { Paths = [src] }, null, null,
            CancellationToken.None);

        Assert.Equal(ResticOutcome.Partial, r.Outcome);
        Assert.Contains(r.ItemErrors, e => e.Contains("locked.dat"));
    }

    [Fact]
    public async Task CancelReturnsCancelled()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var r = await repo.CreateRunner().BackupAsync(new ResticBackupRequest
        {
            Stdin = new StdinCommand("slow.txt", "cmd.exe", ["/c", "ping", "-n", "30", "127.0.0.1"]),
        }, null, null, cts.Token);
        Assert.Equal(ResticOutcome.Cancelled, r.Outcome);
    }
}

public class JobRunnerIntegrationTests(ResticRepoFixture repo) : IClassFixture<ResticRepoFixture>
{
    private (AppConfig Config, JobConfig Job, string BaseDir) Setup(string? password = null)
    {
        var baseDir = repo.NewDir("Buh");
        File.WriteAllBytes(Path.Combine(baseDir, "1Cv8.1CD"), RandomBytes(512 * 1024));
        Directory.CreateDirectory(Path.Combine(baseDir, "1Cv8tmp"));
        File.WriteAllText(Path.Combine(baseDir, "1Cv8tmp", "garbage.tmp"), "временное");
        Directory.CreateDirectory(Path.Combine(baseDir, "1Cv8FTxt"));
        File.WriteAllText(Path.Combine(baseDir, "1Cv8FTxt", "index.dat"), "индекс");

        var docs = repo.NewDir("docs");
        File.WriteAllText(Path.Combine(docs, "договор.docx"), "договор");
        File.WriteAllText(Path.Combine(docs, "черновик.TMP"), "мусор");

        var config = repo.CreateConfig(password);
        var job = new JobConfig
        {
            Name = "test",
            MaxDuration = TimeSpan.FromMinutes(5),
            Sources =
            [
                new FileBase1CSourceConfig { Name = "Бухгалтерия-" + Path.GetFileName(baseDir), Path = baseDir },
                new PathsSourceConfig { Name = "Документы-" + Path.GetFileName(docs), Paths = [docs, @"C:\нет-такой-папки"], Exclude = ["*.tmp"] },
            ],
        };
        config.Jobs.Add(job);
        return (config, job, baseDir);
    }

    [Fact]
    public async Task RunsAllSourcesAndExcludesTemporaryFiles()
    {
        var (config, job, _) = Setup();
        var runner = new JobRunner(config, repo.CreateRunner(), NullLogger.Instance);

        var first = await runner.RunAsync(job, "тест", null, CancellationToken.None);

        Assert.Equal(UnitStatus.Ok, first.Status);
        Assert.Equal(2, first.Units.Count);
        Assert.All(first.Units, u => Assert.NotNull(u.SnapshotId));
        Assert.Contains(first.Units[1].Notes, n => n.Contains("не найден путь"));

        var listing = await repo.CreateRunner().RunAsync(["ls", first.Units[0].SnapshotId!], CancellationToken.None);
        Assert.Contains("1Cv8.1CD", listing.Stdout);
        Assert.DoesNotContain("garbage.tmp", listing.Stdout);
        Assert.DoesNotContain("index.dat", listing.Stdout);

        var docsListing = await repo.CreateRunner().RunAsync(["ls", first.Units[1].SnapshotId!], CancellationToken.None);
        Assert.Contains("договор.docx", docsListing.Stdout);
        Assert.DoesNotContain("черновик", docsListing.Stdout);

        // Повторный запуск без изменений: данные не передаются заново.
        var second = await runner.RunAsync(job, "тест", null, CancellationToken.None);
        Assert.Equal(UnitStatus.Ok, second.Status);
        Assert.True(first.Units[0].DataAdded > 400 * 1024);
        Assert.True(second.Units[0].DataAdded < 10 * 1024, $"повторно передано {second.Units[0].DataAdded} байт");
    }

    [Fact]
    public async Task WrongPasswordStopsJobAfterFirstUnit()
    {
        var (config, job, _) = Setup("wrong");
        var report = await new JobRunner(config, repo.CreateRunner("wrong"), NullLogger.Instance)
            .RunAsync(job, "тест", null, CancellationToken.None);

        Assert.Equal(UnitStatus.Failed, report.Status);
        Assert.All(report.Units, u => Assert.Contains("неверный пароль", u.Message));
        Assert.Equal(1, report.Units[0].Attempts); // фатальная ошибка не повторяется
        Assert.Equal(0, report.Units[1].Attempts); // вторая единица даже не запускалась
    }

    [Fact]
    public async Task MissingBaseFolderFailsOnlyThatUnit()
    {
        var (config, job, _) = Setup();
        job.Sources.Insert(0, new FileBase1CSourceConfig { Name = "Нет", Path = @"C:\нет-базы-1с" });

        var report = await new JobRunner(config, repo.CreateRunner(), NullLogger.Instance)
            .RunAsync(job, "тест", null, CancellationToken.None);

        Assert.Equal(UnitStatus.Failed, report.Units[0].Status);
        Assert.Contains("не найдена", report.Units[0].Message);
        Assert.Equal(UnitStatus.Ok, report.Units[1].Status);
        Assert.Equal(UnitStatus.Ok, report.Units[2].Status);
    }

    [Fact]
    public async Task FailingDumpIsRetriedThenReported()
    {
        // where.exe не понимает аргументы pg_dump и завершается с кодом 2 — как упавший дамп.
        var config = repo.CreateConfig();
        var job = new JobConfig
        {
            Name = "pg",
            MaxDuration = TimeSpan.FromMinutes(5),
            Sources = [new PostgreSqlSourceConfig { PgDumpPath = Path.Combine(Environment.SystemDirectory, "where.exe"), Databases = ["zup"] }],
        };
        config.Jobs.Add(job);

        var report = await new JobRunner(config, repo.CreateRunner(), NullLogger.Instance)
            .RunAsync(job, "тест", null, CancellationToken.None);

        var unit = Assert.Single(report.Units);
        Assert.Equal("PostgreSQL: zup", unit.Title);
        Assert.Equal(UnitStatus.Failed, unit.Status);
        Assert.Equal(2, unit.Attempts); // retry.maxAttempts = 2
        Assert.Contains("2 попыток", unit.Message);
        Assert.Empty(await repo.CreateRunner().SnapshotsAsync(["name:zup"], CancellationToken.None));
    }

    [Fact]
    public async Task HistoryRoundTrip()
    {
        var (config, job, _) = Setup();
        var report = await new JobRunner(config, repo.CreateRunner(), NullLogger.Instance)
            .RunAsync(job, "тест", null, CancellationToken.None);

        var store = new HistoryStore(Path.Combine(repo.NewDir("hist"), "history.jsonl"));
        store.Append(report);
        var last = Assert.Single(store.ReadLast(5));
        Assert.Equal(report.Units.Count, last.Units.Count);
        Assert.Equal(report.Status, last.Status);
        Assert.Empty(store.ReadLast(5, "другая"));
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        Random.Shared.NextBytes(b);
        return b;
    }
}
