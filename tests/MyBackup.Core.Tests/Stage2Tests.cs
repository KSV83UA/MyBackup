using MyBackup.Core.Config;
using MyBackup.Core.Jobs;
using MyBackup.Core.Restic;
using MyBackup.Core.Sources;

namespace MyBackup.Core.Tests;

public class ConfigMaskingTests
{
    private static AppConfig Sample() => new()
    {
        Restic = new ResticConfig
        {
            Repository = Secrets.Protect("rest:https://user:url-pass@host:8000/repo/"),
            Password = Secrets.Protect("repo-pass"),
            Environment = { ["AWS_SECRET_ACCESS_KEY"] = "plain-aws" },
        },
        Telegram = new TelegramConfig { BotToken = "123:token", Proxy = "socks5://u:proxy-pass@127.0.0.1:1080" },
        Jobs =
        [
            new JobConfig
            {
                Name = "j",
                Sources =
                [
                    new MsSqlSourceConfig { Name = "a", ConnectionString = "Server=.;User Id=sa;Pwd=\"s;q\"", Databases = ["*"] },
                    new PostgreSqlSourceConfig { Name = "pg", PgDumpPath = "pg_dump.exe", Password = "pg-pass", Databases = ["*"] },
                ],
            },
        ],
    };

    [Theory]
    [InlineData("rest:https://user:secret@host:8000/r/", "rest:https://user:••••••@host:8000/r/")]
    [InlineData("socks5://u:p@127.0.0.1:1080", "socks5://u:••••••@127.0.0.1:1080")]
    [InlineData(@"D:\restic-repo", @"D:\restic-repo")]
    [InlineData("sftp:backup@host:/srv/repo", "sftp:backup@host:/srv/repo")]
    [InlineData("s3:https://s3.example/bucket", "s3:https://s3.example/bucket")]
    public void UrlMaskHidesOnlyPassword(string url, string expected) => Assert.Equal(expected, ConfigMasking.MaskUrl(url));

    [Theory]
    [InlineData("Server=.;Integrated Security=true", "Server=.;Integrated Security=true")]
    [InlineData("Server=.;User Id=sa;Password=x1", "Server=.;User Id=sa;Password=••••••")]
    [InlineData("pwd = 'a;b';Server=.", "pwd = ••••••;Server=.")]
    public void ConnectionStringMaskHidesOnlyPassword(string cs, string expected) =>
        Assert.Equal(expected, ConfigMasking.MaskConnectionString(cs));

    [Fact]
    public void ClientCopyHasNoSecrets()
    {
        var json = ConfigLoader.Serialize(ConfigMasking.ForClient(Sample()));
        foreach (var secret in new[] { "url-pass", "repo-pass", "plain-aws", "123:token", "proxy-pass", "s;q", "pg-pass", "dpapi:" })
            Assert.DoesNotContain(secret, json);
    }

    [Fact]
    public void UnchangedMasksKeepStoredValues()
    {
        var current = Sample();
        var (merged, errors) = ConfigMasking.MergeFromClient(ConfigMasking.ForClient(current), current);

        Assert.Empty(errors);
        Assert.Equal(current.Restic.Password, merged.Restic.Password);
        Assert.Equal(current.Restic.Repository, merged.Restic.Repository);
        Assert.Equal("123:token", Secrets.Reveal(merged.Telegram.BotToken));
        Assert.Equal("pg-pass", Secrets.Reveal(((PostgreSqlSourceConfig)merged.Jobs[0].Sources[1]).Password));
        Assert.Contains("Pwd=\"s;q\"", Secrets.Reveal(((MsSqlSourceConfig)merged.Jobs[0].Sources[0]).ConnectionString));
    }

    [Fact]
    public void NewValuesAreEncryptedAndEditedUrlKeepsPassword()
    {
        var current = Sample();
        var client = ConfigMasking.ForClient(current);
        client.Restic.Password = "new-pass";
        client.Restic.Repository = client.Restic.Repository.Replace("host:8000", "newhost:9000");
        client.Telegram.BotToken = "";
        client.Jobs[0].Sources.Reverse(); // порядок источников изменился — пароль находится по имени

        var (merged, errors) = ConfigMasking.MergeFromClient(client, current);

        Assert.Empty(errors);
        Assert.True(Secrets.IsProtected(merged.Restic.Password));
        Assert.Equal("new-pass", Secrets.Reveal(merged.Restic.Password));
        Assert.Equal("rest:https://user:url-pass@newhost:9000/repo/", Secrets.Reveal(merged.Restic.Repository));
        Assert.Equal("", merged.Telegram.BotToken);
        Assert.Equal("pg-pass", Secrets.Reveal(((PostgreSqlSourceConfig)merged.Jobs[0].Sources[0]).Password));
    }

    [Fact]
    public void MaskWithoutStoredValueIsAnError()
    {
        var current = Sample();
        var client = ConfigMasking.ForClient(current);
        client.Jobs[0].Sources.Add(new PostgreSqlSourceConfig { Name = "новый", Password = ConfigMasking.Mask, PgDumpPath = "pg_dump.exe", Databases = ["*"] });
        var (_, errors) = ConfigMasking.MergeFromClient(client, current);
        Assert.Contains(errors, e => e.Contains("новый"));
    }

    [Fact]
    public void PlainUrlWithoutPasswordIsNotEncrypted()
    {
        var current = new AppConfig();
        var client = new AppConfig { Restic = { Repository = @"D:\repo", Password = "p" } };
        var (merged, _) = ConfigMasking.MergeFromClient(client, current);
        Assert.Equal(@"D:\repo", merged.Restic.Repository);
        Assert.True(Secrets.IsProtected(merged.Restic.Password));
    }
}

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mybackup-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private ConfigStore Create(string json)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, json);
        return new ConfigStore(path, ConfigLoader.Load(path));
    }

    [Fact]
    public void SaveWritesFileKeepsBackupAndNotifies()
    {
        var store = Create($$"""
            // комментарий из примера
            { "dataDirectory": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(_dir, "data"))}}, "jobs": [] }
            """);
        AppConfig? notified = null;
        store.Changed += c => notified = c;

        var next = ConfigLoader.Clone(store.Current);
        next.Jobs.Add(new JobConfig { Name = "новая", Sources = [new PathsSourceConfig { Paths = [@"D:\x"] }] });
        var result = store.Save(next);

        Assert.True(result.Saved);
        Assert.Contains(result.Errors, e => e.StartsWith("restic.repository")); // сохраняется и с ошибками
        Assert.Same(store.Current, notified);
        Assert.NotSame(next, store.Current);
        Assert.Equal("новая", ConfigLoader.Load(store.FilePath).Jobs.Single().Name);
        Assert.Contains("новая", File.ReadAllText(store.FilePath)); // кириллица не экранируется
        Assert.Contains("комментарий из примера", File.ReadAllText(result.BackupPath!));
    }

    [Fact]
    public void ManualEditAfterStartIsNotOverwritten()
    {
        var store = Create("""{ "jobs": [] }""");
        File.SetLastWriteTimeUtc(store.FilePath, DateTime.UtcNow.AddMinutes(1));
        var result = store.Save(ConfigLoader.Clone(store.Current));
        Assert.False(result.Saved);
        Assert.Contains("вручную", result.Errors.Single());
    }

    [Fact]
    public void ExampleConfigSurvivesRoundTrip()
    {
        var example = ConfigLoader.Load(Path.Combine(TestPaths.RepoRoot, "src", "MyBackup.Service", "config.example.json"));
        var json = ConfigLoader.Serialize(example);
        Assert.Equal(json, ConfigLoader.Serialize(ConfigLoader.Parse(json)));
        Assert.Contains("\"type\": \"fileBase1C\"", json);
        Assert.DoesNotContain("typeName", json); // вычисляемые свойства в файл не пишутся
        Assert.DoesNotContain("effectiveName", json);
    }
}

public class ValidationStage2Tests
{
    private static AppConfig Valid() => new()
    {
        Restic = { Repository = @"D:\repo", Password = "p" },
        Jobs = [new JobConfig { Name = "a", Sources = [new PathsSourceConfig { Paths = [@"D:\x"] }] }],
    };

    [Fact]
    public void TelegramProblemsDoNotBlockBackups()
    {
        var c = Valid();
        c.Telegram.Enabled = true; // без токена
        Assert.Contains(ConfigLoader.Validate(c), e => e.StartsWith("telegram.botToken"));
        Assert.Empty(ConfigLoader.ValidateForRun(c, c.Jobs[0]));
    }

    [Fact]
    public void EmptyAllowedChatsIsAllowed()
    {
        var c = Valid();
        c.Telegram = new TelegramConfig { Enabled = true, BotToken = "1:x" };
        Assert.Empty(ConfigLoader.Validate(c));
    }

    [Fact]
    public void OtherJobErrorsDoNotBlockThisJob()
    {
        var c = Valid();
        c.Jobs.Add(new JobConfig { Name = "b", Schedule = "плохое" });
        Assert.NotEmpty(ConfigLoader.Validate(c));
        Assert.Empty(ConfigLoader.ValidateForRun(c, c.Jobs[0]));
        Assert.NotEmpty(ConfigLoader.ValidateForRun(c, c.Jobs[1]));
    }

    [Fact]
    public void DuplicateSourceNamesAreReported()
    {
        var c = Valid();
        c.Jobs[0].Sources.Add(new PathsSourceConfig { Paths = [@"D:\y"] }); // оба без имени → «Папки»
        Assert.Contains(ConfigLoader.Validate(c), e => e.Contains("два источника с именем \"Папки\""));
    }

    [Theory]
    [InlineData("RESTIC_PASSWORD_COMMAND")]
    [InlineData("restic_from_password_command")]
    [InlineData("RESTIC_PASSWORD_FILE")]
    [InlineData("RESTIC_REPOSITORY")]
    public void CommandVariablesAreForbidden(string name)
    {
        var c = Valid();
        c.Restic.Environment[name] = "x";
        Assert.Contains(ConfigLoader.ValidateForRun(c, c.Jobs[0]), e => e.Contains(name));
    }

    [Fact]
    public void PgDumpMustBePgDump()
    {
        var c = Valid();
        c.Jobs[0].Sources.Add(new PostgreSqlSourceConfig { PgDumpPath = @"C:\Windows\System32\cmd.exe", Databases = ["*"] });
        Assert.Contains(ConfigLoader.Validate(c), e => e.Contains("pg_dump.exe"));
    }

    [Fact]
    public void PlaceholderAndForeignDpapiValuesAreReportedDifferently()
    {
        var c = Valid();
        c.Restic.Password = "dpapi:ЗАМЕНИТЕ";
        Assert.Contains(ConfigLoader.Validate(c), e => e.StartsWith("restic.password") && e.Contains("не заполнено"));

        c.Restic.Password = "dpapi:" + Convert.ToBase64String(new byte[64]); // зашифровано не здесь
        Assert.Contains(ConfigLoader.Validate(c), e => e.StartsWith("restic.password") && e.Contains("расшифровать"));
    }

    [Fact]
    public void WebHttpsNeedsCertificate()
    {
        var c = Valid();
        c.Web.Urls = ["https://0.0.0.0:8443", "ftp://x"];
        var errors = ConfigLoader.Validate(c);
        Assert.Contains(errors, e => e.StartsWith("web.certificatePath"));
        Assert.Contains(errors, e => e.Contains("ftp://x"));
    }
}

public class PasswordTests
{
    [Fact]
    public void HashVerifies()
    {
        var hash = PasswordHasher.Hash("пароль-123");
        Assert.StartsWith("pbkdf2-sha256$600000$", hash);
        Assert.True(PasswordHasher.Verify("пароль-123", hash));
        Assert.False(PasswordHasher.Verify("пароль-124", hash));
        Assert.False(PasswordHasher.Verify("x", null));
        Assert.False(PasswordHasher.Verify("x", "garbage"));
        Assert.NotEqual(hash, PasswordHasher.Hash("пароль-123")); // соль
    }

    [Fact]
    public void StoreChangesStampAndRejectsShort()
    {
        var path = Path.Combine(Path.GetTempPath(), "mybackup-pw-" + Guid.NewGuid().ToString("N"), "web-password");
        var store = new WebPasswordStore(path);
        Assert.False(store.IsSet);
        Assert.Throws<ArgumentException>(() => store.Set("short"));
        store.Set("first-password");
        var stamp = store.Stamp();
        store.Set("second-password");
        Assert.NotEqual(stamp, store.Stamp());
        Assert.True(store.Verify("second-password"));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}

public class OneCDiscoveryTests
{
    [Fact]
    public void ParsesUserBaseList()
    {
        const string v8i = "\uFEFF[Бухгалтерия предприятия]\r\nConnect=File=\"D:\\1C\\Buh\";\r\nID=1\r\n[Папка]\r\nFolder=/\r\n"
                           + "[УТ]\r\nConnect=Srvr=\"srv1c\";Ref=\"ut\";\r\n[Веб]\r\nConnect=ws=\"http://x/y\";\r\n";
        var bases = OneCBaseDiscovery.ParseV8i(v8i).ToList();
        Assert.Equal(3, bases.Count);
        Assert.Equal(("Бухгалтерия предприятия", "File=\"D:\\1C\\Buh\";"), bases[0]);

        Assert.Equal(("file", @"D:\1C\Buh", null, null), OneCBaseDiscovery.ParseConnect(bases[0].Connect));
        Assert.Equal(("server", null, "srv1c", "ut"), OneCBaseDiscovery.ParseConnect(bases[1].Connect));
        Assert.Equal("other", OneCBaseDiscovery.ParseConnect(bases[2].Connect).Kind);
        Assert.Equal(@"D:\1C\a""b", OneCBaseDiscovery.ParseConnect("File=\"D:\\1C\\a\"\"b\";").Path);
    }

    [Fact]
    public void ScanFindsBaseFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), "mybackup-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "1C", "Buh"));
        Directory.CreateDirectory(Path.Combine(root, "1C", "ZUP", "nested"));
        File.WriteAllText(Path.Combine(root, "1C", "Buh", "1Cv8.1CD"), "x");
        File.WriteAllText(Path.Combine(root, "1C", "ZUP", "1Cv8.1CD"), "xy");

        var found = OneCBaseDiscovery.ScanFolder(root, 4, TimeSpan.FromSeconds(10));
        Assert.Equal(["Buh", "ZUP"], found.Select(b => b.Name));
        Assert.All(found, b => Assert.True(b.Exists));
        Directory.Delete(root, true);
    }
}

public class DeployScriptTests
{
    /// <summary>
    /// Windows PowerShell 5.1 читает .ps1 без BOM в кодировке системы. На английской Windows (1252) байты
    /// русских букв превращаются в «умные кавычки», и скрипт не разбирается — так сломалась первая установка.
    /// </summary>
    [Fact]
    public void PowerShellScriptsAreUtf8WithBom()
    {
        var scripts = Directory.GetFiles(Path.Combine(TestPaths.RepoRoot, "deploy"), "*.ps1");
        Assert.NotEmpty(scripts);
        foreach (var file in scripts)
        {
            var b = File.ReadAllBytes(file);
            Assert.True(b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF,
                $"{Path.GetFileName(file)}: сохраните в UTF-8 с BOM");
        }
    }
}

public class JobQueueTests
{
    [Fact]
    public void CancelStopsOnlyRunningJobWithReason()
    {
        var q = new JobQueue();
        Assert.False(q.CancelCurrent("кто-то"));
        Assert.True(q.TryEnqueue("a", "тест"));
        Assert.False(q.TryEnqueue("A", "тест"));
        Assert.Equal(["a"], q.Waiting);

        var status = new JobStatus("a", "тест");
        var token = q.BeginRun(status, CancellationToken.None);
        Assert.Empty(q.Waiting);
        Assert.True(q.CancelCurrent("веб"));
        Assert.True(token.IsCancellationRequested);
        Assert.Equal("остановлено вручную (веб)", ((IJobProgress)status).CancelReason);

        q.EndRun("a");
        Assert.Null(q.Current);
        Assert.True(q.TryEnqueue("a", "снова"));
    }

    [Fact]
    public void ForbiddenVariablesAreDetected()
    {
        Assert.True(ResticRunner.IsForbiddenVariable("RESTIC_PASSWORD_COMMAND"));
        Assert.True(ResticRunner.IsForbiddenVariable("Restic_Password"));
        Assert.False(ResticRunner.IsForbiddenVariable("AWS_ACCESS_KEY_ID"));
    }
}
