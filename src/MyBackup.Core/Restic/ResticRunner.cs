using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyBackup.Core.Config;
using MyBackup.Core.Util;

namespace MyBackup.Core.Restic;

public sealed class ResticSettings
{
    public required string ExecutablePath { get; init; }
    public required string Repository { get; init; }
    public required string Password { get; init; }
    public required string Host { get; init; }
    public string? CacheDirectory { get; init; }
    public required string TempDirectory { get; init; }
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public int? PackSizeMiB { get; init; }
    public string? Compression { get; init; }

    public static string ResolveExecutable(string configured)
    {
        var exe = System.Environment.ExpandEnvironmentVariables(configured);
        return Path.IsPathRooted(exe) ? exe : Path.Combine(AppContext.BaseDirectory, exe);
    }

    public static ResticSettings FromConfig(AppConfig config)
    {
        return new ResticSettings
        {
            ExecutablePath = ResolveExecutable(config.Restic.Executable),
            Repository = Secrets.Reveal(config.Restic.Repository),
            Password = Secrets.Reveal(config.Restic.Password),
            Host = config.ServerName,
            CacheDirectory = string.IsNullOrWhiteSpace(config.Restic.CacheDirectory)
                ? Path.Combine(config.ResolvedDataDirectory, "cache")
                : System.Environment.ExpandEnvironmentVariables(config.Restic.CacheDirectory),
            TempDirectory = config.StagingDirectory,
            Environment = config.Restic.Environment.ToDictionary(kv => kv.Key, kv => Secrets.Reveal(kv.Value)),
            PackSizeMiB = config.Restic.PackSizeMiB,
            Compression = config.Restic.Compression,
        };
    }
}

/// <summary>Обёртка над restic.exe: собирает аргументы, читает JSON-вывод, классифицирует результат.</summary>
public sealed class ResticRunner(ResticSettings settings, ILogger logger)
{
    public ResticSettings Settings => settings;

    public async Task<ResticBackupResult> BackupAsync(ResticBackupRequest request, int? limitUploadKiB,
        IProgress<ResticStatus>? progress, CancellationToken ct)
    {
        var args = new List<string> { "backup", "--json", "--host", settings.Host };
        foreach (var tag in request.Tags)
        {
            args.Add("--tag");
            args.Add(SanitizeTag(tag));
        }

        if (request.UseVss) args.Add("--use-fs-snapshot");
        foreach (var pattern in request.IExcludes)
        {
            args.Add("--iexclude");
            args.Add(pattern);
        }

        AddUploadOptions(args, limitUploadKiB);

        string? listFile = null;
        if (request.Stdin is { } stdin)
        {
            args.Add("--stdin-from-command");
            args.Add("--stdin-filename");
            args.Add(stdin.FileName);
            args.Add("--");
            args.Add(stdin.Command);
            args.AddRange(stdin.Arguments);
        }
        else
        {
            if (request.Paths.Count == 0) throw new ArgumentException("Нет путей для копирования", nameof(request));
            Directory.CreateDirectory(settings.TempDirectory);
            listFile = Path.Combine(settings.TempDirectory, $"files-{Guid.NewGuid():N}.lst");
            // --files-from-raw: пути разделены нулевым символом, без проблем с пробелами и \r\n
            var content = string.Join('\0', request.Paths) + "\0";
            await File.WriteAllTextAsync(listFile, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
            args.Add("--files-from-raw");
            args.Add(listFile);
        }

        ResticSummary? summary = null;
        string? exitError = null;
        var itemErrors = new List<string>();
        var stderr = new List<string>();
        var sync = new object();

        void Handle(string line, bool fromStderr)
        {
            var msg = ResticJson.TryParse(line);
            if (msg is null)
            {
                if (fromStderr && !string.IsNullOrWhiteSpace(line))
                {
                    lock (sync) stderr.Add(line);
                    logger.LogWarning("restic: {Line}", line);
                }

                return;
            }

            switch (msg.MessageType)
            {
                case "status":
                    progress?.Report(msg.ToStatus());
                    break;
                case "summary":
                    summary = msg.ToSummary();
                    break;
                case "error":
                    var text = string.IsNullOrEmpty(msg.Item) ? msg.ErrorText : $"{msg.Item}: {msg.ErrorText}";
                    lock (sync) itemErrors.Add(text);
                    logger.LogWarning("restic ({During}): {Error}", msg.During, text);
                    break;
                case "exit_error":
                    exitError = msg.Message;
                    break;
            }
        }

        int exitCode;
        try
        {
            exitCode = await ProcessRunner.RunAsync(CreateStartInfo(args, request.Environment),
                l => Handle(l, false), l => Handle(l, true), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ResticBackupResult
            {
                ExitCode = -1, Outcome = ResticOutcome.Cancelled, ItemErrors = itemErrors,
                ErrorMessage = "запуск прерван",
            };
        }
        finally
        {
            if (listFile is not null) TryDelete(listFile);
        }

        var outcome = ResticJson.Classify(exitCode);
        string? error = null;
        if (outcome is not ResticOutcome.Success)
        {
            List<string> tail;
            lock (sync) tail = stderr.TakeLast(5).ToList();
            error = exitError ?? (tail.Count > 0 ? string.Join(" | ", tail) : null) ?? ResticJson.DescribeExitCode(exitCode);
            if (exitCode is 10 or 11 or 12) error = $"{ResticJson.DescribeExitCode(exitCode)} ({error})";
        }

        return new ResticBackupResult
        {
            ExitCode = exitCode, Outcome = outcome, Summary = summary, ItemErrors = itemErrors, ErrorMessage = error,
        };
    }

    /// <summary>true — хранилище есть, false — нет (код 10), null — не удалось проверить (сеть, пароль).</summary>
    public async Task<(bool? Exists, string? Error)> RepositoryExistsAsync(CancellationToken ct)
    {
        var r = await RunAsync(["cat", "config"], ct, quiet: true).ConfigureAwait(false);
        return r.ExitCode switch
        {
            0 => (true, null),
            10 => (false, null),
            _ => (null, r.ErrorMessage),
        };
    }

    public Task<ResticCommandResult> InitAsync(CancellationToken ct) => RunAsync(["init"], ct);

    public Task<ResticCommandResult> UnlockAsync(CancellationToken ct) => RunAsync(["unlock"], ct);

    public async Task<IReadOnlyList<ResticSnapshot>> SnapshotsAsync(IReadOnlyCollection<string>? tags, CancellationToken ct)
    {
        var args = new List<string> { "snapshots", "--json", "--no-lock", "--host", settings.Host };
        if (tags is { Count: > 0 })
        {
            args.Add("--tag");
            args.Add(string.Join(',', tags.Select(SanitizeTag)));
        }

        var r = await RunAsync(args, ct).ConfigureAwait(false);
        if (!r.Success) throw new InvalidOperationException(r.ErrorMessage ?? ResticJson.DescribeExitCode(r.ExitCode));
        return JsonSerializer.Deserialize<List<ResticSnapshot>>(r.Stdout, ResticJson.Options) ?? [];
    }

    /// <summary>Содержимое одной папки снимка (без вложенных).</summary>
    public async Task<IReadOnlyList<ResticNode>> LsAsync(string snapshot, string path, CancellationToken ct)
    {
        var r = await RunAsync(["ls", "--json", "--no-lock", snapshot, path], ct, quiet: true).ConfigureAwait(false);
        if (!r.Success) throw new InvalidOperationException(r.ErrorMessage ?? ResticJson.DescribeExitCode(r.ExitCode));

        var self = path.TrimEnd('/');
        var nodes = new List<ResticNode>();
        foreach (var line in r.Stdout.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("struct_type", out var st) || st.GetString() != "node") continue;
            var node = doc.RootElement.Deserialize<ResticNode>(ResticJson.Options);
            if (node is null || node.Path.TrimEnd('/') == self) continue; // сама запрошенная папка
            nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>
    /// Файл из снимка — как есть, папку — zip-архивом (restic dump) прямо в <paramref name="output"/>.
    /// Для папки снимок указывается как «id:родитель», чтобы в архиве была только сама папка, а не весь путь.
    /// </summary>
    public async Task DumpAsync(string snapshot, string path, bool asZip, Stream output, CancellationToken ct)
    {
        var args = new List<string> { "dump", "--no-lock", "--quiet" };
        if (asZip)
        {
            args.Add("--archive");
            args.Add("zip");
        }

        args.Add(snapshot);
        args.Add(path);

        var psi = CreateStartInfo(args, null);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardErrorEncoding = Encoding.UTF8;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("restic не запустился");
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(output, ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // уже завершился
            }

            throw;
        }

        var errors = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var message = errors.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            throw new InvalidOperationException(message ?? ResticJson.DescribeExitCode(process.ExitCode));
        }
    }

    /// <summary>Удаляет старые снимки по правилам хранения отдельно для каждого источника (группировка по тегам).</summary>
    public Task<ResticCommandResult> ForgetAsync(RetentionConfig retention, int? limitUploadKiB, CancellationToken ct)
    {
        var args = new List<string>
        {
            "forget", "--host", settings.Host, "--tag", "mybackup", "--group-by", "host,tags",
            "--keep-daily", retention.KeepDaily.ToString(), "--keep-weekly", retention.KeepWeekly.ToString(),
            "--keep-monthly", retention.KeepMonthly.ToString(),
        };
        if (retention.Prune) args.Add("--prune");
        AddUploadOptions(args, limitUploadKiB);
        return RunAsync(args, ct);
    }

    public async Task<ResticCommandResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct, bool quiet = false)
    {
        var stdout = new StringBuilder();
        var stderr = new List<string>();
        string? exitError = null;
        var sync = new object();

        var exitCode = await ProcessRunner.RunAsync(CreateStartInfo(args, null),
            line =>
            {
                lock (sync) stdout.AppendLine(line);
            },
            line =>
            {
                var msg = ResticJson.TryParse(line);
                if (msg?.MessageType == "exit_error") exitError = msg.Message;
                else if (!string.IsNullOrWhiteSpace(line))
                    lock (sync) stderr.Add(line);
            }, ct).ConfigureAwait(false);

        string? error = null;
        if (exitCode != 0)
        {
            error = exitError ?? (stderr.Count > 0 ? string.Join(" | ", stderr.TakeLast(5)) : null)
                ?? ResticJson.DescribeExitCode(exitCode);
            if (!quiet) logger.LogWarning("restic {Command}: {Error}", args.FirstOrDefault(), error);
        }

        return new ResticCommandResult { ExitCode = exitCode, Stdout = stdout.ToString(), Stderr = stderr, ErrorMessage = error };
    }

    private void AddUploadOptions(List<string> args, int? limitUploadKiB)
    {
        if (limitUploadKiB is > 0)
        {
            args.Add("--limit-upload");
            args.Add(limitUploadKiB.Value.ToString());
        }

        if (settings.PackSizeMiB is { } pack)
        {
            args.Add("--pack-size");
            args.Add(pack.ToString());
        }

        if (!string.IsNullOrWhiteSpace(settings.Compression))
        {
            args.Add("--compression");
            args.Add(settings.Compression);
        }
    }

    /// <summary>
    /// Переменные, через которые restic выполняет команды или читает пароль/адрес из файлов: из настроек
    /// их не берём — иначе правка настроек равнялась бы запуску любой программы от имени службы.
    /// </summary>
    public static bool IsForbiddenVariable(string name) =>
        name.EndsWith("_COMMAND", StringComparison.OrdinalIgnoreCase)
        || name.Equals("RESTIC_PASSWORD", StringComparison.OrdinalIgnoreCase)
        || name.Equals("RESTIC_PASSWORD_FILE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("RESTIC_REPOSITORY", StringComparison.OrdinalIgnoreCase)
        || name.Equals("RESTIC_REPOSITORY_FILE", StringComparison.OrdinalIgnoreCase);

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? extraEnv)
    {
        if (!File.Exists(settings.ExecutablePath))
            throw new FileNotFoundException($"Не найден restic: {settings.ExecutablePath}", settings.ExecutablePath);

        var psi = new ProcessStartInfo(settings.ExecutablePath);
        foreach (var a in args) psi.ArgumentList.Add(a);

        foreach (var name in new[] { "RESTIC_PASSWORD_FILE", "RESTIC_PASSWORD_COMMAND", "RESTIC_REPOSITORY_FILE" })
            psi.Environment.Remove(name);

        psi.Environment["RESTIC_REPOSITORY"] = settings.Repository;
        psi.Environment["RESTIC_PASSWORD"] = settings.Password;
        psi.Environment["RESTIC_PROGRESS_FPS"] = "0.2"; // статус раз в 5 секунд
        if (!string.IsNullOrWhiteSpace(settings.CacheDirectory)) psi.Environment["RESTIC_CACHE_DIR"] = settings.CacheDirectory;
        foreach (var (k, v) in settings.Environment)
            if (!IsForbiddenVariable(k))
                psi.Environment[k] = v;
        if (extraEnv is not null)
            foreach (var (k, v) in extraEnv)
                psi.Environment[k] = v;

        return psi;
    }

    /// <summary>Для команды MyBackup restic …: restic с настройками хранилища, вывод прямо в консоль.</summary>
    public ProcessStartInfo CreatePassthroughStartInfo(IReadOnlyList<string> args)
    {
        var psi = CreateStartInfo(args, null);
        psi.UseShellExecute = false;
        return psi;
    }

    /// <summary>В --tag запятая разделяет теги, поэтому заменяем её.</summary>
    public static string SanitizeTag(string tag) => tag.Replace(',', '_');

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Не удалось удалить {Path}", path);
        }
    }
}
