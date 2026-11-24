using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MyBackup.Core.Config;
using MyBackup.Core.Jobs;
using MyBackup.Core.Sources;

namespace MyBackup.Core.Tests;

public class OneCDumpTests
{
    [Fact]
    public void ArgumentsForFileAndServerBases()
    {
        var file = OneCDumpSource.BuildArguments(new OneCDumpSourceConfig { FilePath = @"D:\1C\Buh\", User = " Админ " }, "пароль",
            @"C:\t\Buh.dt", @"C:\t\Buh.dt.log", @"C:\t\Buh.dt.result");
        Assert.Equal(["DESIGNER", "/F", @"D:\1C\Buh", "/N", "Админ", "/P", "пароль", "/DumpIB", @"C:\t\Buh.dt", "/Out", @"C:\t\Buh.dt.log",
            "/DumpResult", @"C:\t\Buh.dt.result", "/DisableStartupDialogs", "/DisableStartupMessages"], file);

        var server = OneCDumpSource.BuildArguments(new OneCDumpSourceConfig { ServerBase = "srv1c\\ut" }, "", "a.dt", "a.log", "a.res");
        Assert.Equal(["/S", "srv1c\\ut"], server.Skip(1).Take(2));
        Assert.DoesNotContain("/N", server);
        Assert.DoesNotContain("/P", server);
    }

    [Theory]
    [InlineData(0, "0", 100L, "", true)]
    [InlineData(0, null, 100L, "", true)] // старые платформы без /DumpResult
    [InlineData(1, "1", 100L, "Ошибка", false)]
    [InlineData(0, "1", 100L, "", false)]
    [InlineData(0, "0", 0L, "", false)]
    [InlineData(0, "0", null, "", false)]
    public void ResultIsJudgedByCodeResultFileAndDtSize(int code, string? result, long? length, string log, bool ok) =>
        Assert.Equal(ok, OneCDumpSource.Evaluate(code, result, length, log).Ok);

    [Fact]
    public void ErrorShowsLastLogLines()
    {
        var (_, error) = OneCDumpSource.Evaluate(1, "1", null, "начало\r\n\r\nОшибка: база заблокирована\r\nВыгрузка не выполнена\r\n");
        Assert.Contains("код 1", error);
        Assert.Contains("база заблокирована", error);
        Assert.Contains("Выгрузка не выполнена", error);
    }

    [Fact]
    public void LogInAnyEncodingIsReadable()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string text = "Выгрузка завершена";
        Assert.Equal(text, OneCDumpSource.ReadLog([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(text)]));
        Assert.Equal(text, OneCDumpSource.ReadLog([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)]));
        Assert.Equal(text, OneCDumpSource.ReadLog(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(text, OneCDumpSource.ReadLog(Encoding.GetEncoding(1251).GetBytes(text)));
    }

    [Fact]
    public void NewestPlatformIsChosen()
    {
        var root = Path.Combine(Path.GetTempPath(), "mybackup-1cv8-" + Guid.NewGuid().ToString("N"));
        var x64 = Path.Combine(root, "x64");
        var x86 = Path.Combine(root, "x86");
        foreach (var (r, v) in new[] { (x64, "8.3.22.1709"), (x64, "8.3.24.1586"), (x86, "8.3.24.1586"), (x64, "common") })
        {
            Directory.CreateDirectory(Path.Combine(r, v, "bin"));
            File.WriteAllText(Path.Combine(r, v, "bin", "1cv8.exe"), "");
        }

        var found = OneCDumpSource.FindPlatforms([x64, x86]);
        Assert.Equal(3, found.Count); // «common» — не версия
        Assert.Equal("8.3.24.1586", found[0].Version);
        Assert.StartsWith(x64, found[0].Path); // при равной версии — 64-разрядная
        Assert.Equal(found[0].Path, OneCDumpSource.ResolvePlatform(null, [x64, x86]));
        Assert.Null(OneCDumpSource.ResolvePlatform(null, [Path.Combine(root, "нет")]));
        Directory.Delete(root, true);
    }

    [Fact]
    public void ConfigRoundTripValidationAndNames()
    {
        var c = ConfigLoader.Parse("""
            { "restic": { "repository": "D:\\r", "password": "p" },
              "jobs": [ { "name": "w", "sources": [
                { "type": "oneCDump", "filePath": "D:\\1C\\Buh", "user": "Админ", "password": "x" },
                { "type": "fileBase1C", "path": "D:\\1C\\Buh" } ] } ] }
            """);
        var dump = Assert.IsType<OneCDumpSourceConfig>(c.Jobs[0].Sources[0]);
        Assert.Equal("Buh.dt", dump.EffectiveName); // не совпадает с «Buh» у копии файла базы
        Assert.Equal(TimeSpan.FromHours(3), dump.Timeout);
        Assert.Empty(ConfigLoader.Validate(c));
        Assert.Contains("\"type\": \"oneCDump\"", ConfigLoader.Serialize(c));

        dump.ServerBase = "srv\\buh"; // и файл, и сервер
        Assert.Contains(ConfigLoader.Validate(c), e => e.Contains("либо папку файловой базы"));
        dump.FilePath = null;
        dump.ServerBase = "srv-without-base";
        Assert.Contains(ConfigLoader.Validate(c), e => e.Contains("сервер\\имя_базы"));
        dump.ServerBase = "srv\\buh";
        dump.PlatformPath = @"C:\Windows\System32\cmd.exe";
        Assert.Contains(ConfigLoader.Validate(c), e => e.Contains("1cv8.exe"));
    }

    [Fact]
    public void PasswordIsMaskedAndKept()
    {
        var current = new AppConfig
        {
            Jobs = [new JobConfig { Name = "w", Sources = [new OneCDumpSourceConfig { FilePath = @"D:\b", Password = "секрет-1с" }] }],
        };
        var client = ConfigMasking.ForClient(current);
        Assert.DoesNotContain("секрет-1с", ConfigLoader.Serialize(client));
        var (merged, errors) = ConfigMasking.MergeFromClient(client, current);
        Assert.Empty(errors);
        Assert.Equal("секрет-1с", Secrets.Reveal(((OneCDumpSourceConfig)merged.Jobs[0].Sources[0]).Password));
    }
}

/// <summary>Полный цикл с поддельным Конфигуратором: выгрузка → restic → уборка.</summary>
public class OneCDumpIntegrationTests(ResticRepoFixture repo) : IClassFixture<ResticRepoFixture>
{
    /// <summary>Разбирает те же аргументы, что и 1cv8.exe: пишет .dt, журнал и файл результата.</summary>
    private string FakePlatform(bool ok)
    {
        var dir = repo.NewDir("1cv8");
        var path = Path.Combine(dir, ok ? "fake-ok.cmd" : "fake-fail.cmd");
        File.WriteAllText(path, $"""
            @echo off
            setlocal
            :loop
            if "%~1"=="" goto done
            if /I "%~1"=="/DumpIB" set "DT=%~2"
            if /I "%~1"=="/DumpResult" set "RES=%~2"
            if /I "%~1"=="/Out" set "LOG=%~2"
            shift
            goto loop
            :done
            >"%LOG%" echo Dump started
            {(ok
                ? ">\"%DT%\" echo FAKE-DT-CONTENT\r\n>\"%RES%\" echo 0\r\nexit /b 0"
                : ">>\"%LOG%\" echo Error: infobase is locked\r\n>\"%RES%\" echo 1\r\nexit /b 1")}
            """.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"), Encoding.ASCII);
        return path;
    }

    private (AppConfig Config, JobConfig Job) Setup(bool ok)
    {
        var baseDir = repo.NewDir("Buh");
        File.WriteAllText(Path.Combine(baseDir, "1Cv8.1CD"), "x");
        var config = repo.CreateConfig();
        var job = new JobConfig
        {
            Name = "dt", MaxDuration = TimeSpan.FromMinutes(5),
            Sources = [new OneCDumpSourceConfig { FilePath = baseDir, PlatformPath = FakePlatform(ok), User = "Admin", Password = "secret" }],
        };
        config.Jobs.Add(job);
        return (config, job);
    }

    [Fact]
    public async Task DumpIsBackedUpAndTemporaryFilesRemoved()
    {
        var (config, job) = Setup(ok: true);
        var report = await new JobRunner(config, repo.CreateRunner(), NullLogger.Instance).RunAsync(job, "тест", null, CancellationToken.None);

        var unit = Assert.Single(report.Units);
        Assert.True(unit.Status == UnitStatus.Ok, unit.Message);
        Assert.Contains(unit.Notes, n => n.StartsWith("выгрузка .dt"));
        Assert.Empty(Directory.GetFiles(Path.Combine(config.StagingDirectory, "1c-dt")));

        var snap = (await repo.CreateRunner().SnapshotsAsync([$"name:{job.Sources[0].EffectiveName}"], CancellationToken.None)).Single();
        var dt = snap.Paths.Single();
        Assert.EndsWith(".dt", dt);
        using var content = new MemoryStream();
        await repo.CreateRunner().DumpAsync(snap.Id, "/" + dt.Replace(":", "").Replace('\\', '/'), false, content, CancellationToken.None);
        Assert.Contains("FAKE-DT-CONTENT", Encoding.ASCII.GetString(content.ToArray()));
    }

    [Fact]
    public async Task FailedDumpReportsConfiguratorLog()
    {
        var (config, job) = Setup(ok: false);
        var report = await new JobRunner(config, repo.CreateRunner(), NullLogger.Instance).RunAsync(job, "тест", null, CancellationToken.None);

        var unit = Assert.Single(report.Units);
        Assert.Equal(UnitStatus.Failed, unit.Status);
        Assert.Contains("код 1", unit.Message);
        Assert.Contains("infobase is locked", unit.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(config.StagingDirectory, "1c-dt")));
    }

    [Fact]
    public async Task SnapshotFolderCanBeListedAndZipped()
    {
        var src = repo.NewDir("Обмен");
        Directory.CreateDirectory(Path.Combine(src, "Сканы"));
        File.WriteAllText(Path.Combine(src, "договор.txt"), "договор");
        File.WriteAllText(Path.Combine(src, "Сканы", "скан.txt"), "скан");
        var runner = repo.CreateRunner();
        var r = await runner.BackupAsync(new Restic.ResticBackupRequest { Paths = [src], Tags = ["zip-test"] }, null, null, CancellationToken.None);
        var id = r.Summary!.SnapshotId!;
        var rp = "/" + src.Replace(":", "").Replace('\\', '/');

        var nodes = await runner.LsAsync(id, rp, CancellationToken.None);
        Assert.Equal(["Сканы", "договор.txt"], nodes.Select(n => n.Name).Order(StringComparer.Ordinal));
        Assert.Equal("dir", nodes.Single(n => n.Name == "Сканы").Type);
        Assert.Equal(Encoding.UTF8.GetByteCount("договор"), nodes.Single(n => n.Name == "договор.txt").Size);

        // Как в веб-интерфейсе: «снимок:родитель» + «/имя» — в архиве только сама папка, без полного пути.
        using var zip = new MemoryStream();
        await runner.DumpAsync($"{id}:{rp}", "/Сканы", true, zip, CancellationToken.None);
        zip.Position = 0;
        using var archive = new ZipArchive(zip);
        Assert.Contains("Сканы/скан.txt", archive.Entries.Select(e => e.FullName));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.DumpAsync(id, rp + "/нет-такого.txt", false, Stream.Null, CancellationToken.None));
    }
}
