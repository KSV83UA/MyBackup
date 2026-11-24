using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using MyBackup.Core.Config;
using MyBackup.Core.Restic;
using MyBackup.Core.Util;

namespace MyBackup.Core.Sources;

/// <summary>Установленная платформа 1С.</summary>
public sealed record OneCPlatform(string Version, string Path);

/// <summary>
/// Выгрузка информационной базы 1С в .dt: Конфигуратор в пакетном режиме
/// (1cv8.exe DESIGNER /F|/S … /DumpIB файл.dt /Out лог /DumpResult файл), затем restic, затем файл удаляется.
/// </summary>
public sealed class OneCDumpSource(OneCDumpSourceConfig config) : SingleUnitSource
{
    static OneCDumpSource() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public override string Title => $"Выгрузка 1С: {config.EffectiveName}";

    protected override BackupUnit CreateUnit() =>
        new Unit(config) { Title = Title, SourceType = config.TypeName, Name = config.EffectiveName };

    private static IEnumerable<string> DefaultRoots =>
    [
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "1cv8"),
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "1cv8"),
    ];

    /// <summary>Платформы вида &lt;корень&gt;\8.3.24.1586\bin\1cv8.exe — сначала самые новые, 64-разрядные раньше 32-разрядных.</summary>
    public static IReadOnlyList<OneCPlatform> FindPlatforms(IEnumerable<string>? roots = null)
    {
        var found = new List<(Version Version, int Order, string Path)>();
        var order = 0;
        foreach (var root in (roots ?? DefaultRoots).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            order++;
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var exe = System.IO.Path.Combine(dir, "bin", "1cv8.exe");
                if (Version.TryParse(System.IO.Path.GetFileName(dir), out var v) && File.Exists(exe)) found.Add((v, order, exe));
            }
        }

        return found.OrderByDescending(f => f.Version).ThenBy(f => f.Order)
            .Select(f => new OneCPlatform(f.Version.ToString(), f.Path)).ToList();
    }

    public static string? ResolvePlatform(string? configured, IEnumerable<string>? roots = null) =>
        string.IsNullOrWhiteSpace(configured) ? FindPlatforms(roots).FirstOrDefault()?.Path : PathHelper.Normalize(configured);

    internal static List<string> BuildArguments(OneCDumpSourceConfig c, string password, string dtPath, string logPath,
        string resultPath)
    {
        var args = new List<string> { "DESIGNER" };
        if (!string.IsNullOrWhiteSpace(c.FilePath))
        {
            // Без «\» в конце: в командной строке он оказался бы перед закрывающей кавычкой.
            var path = PathHelper.Normalize(c.FilePath);
            args.Add("/F");
            args.Add(path.Length > 3 ? path.TrimEnd('\\', '/') : path);
        }
        else
        {
            args.Add("/S");
            args.Add(c.ServerBase!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(c.User))
        {
            args.Add("/N");
            args.Add(c.User.Trim());
        }

        if (password.Length > 0)
        {
            args.Add("/P");
            args.Add(password);
        }

        args.AddRange(["/DumpIB", dtPath, "/Out", logPath, "/DumpResult", resultPath, "/DisableStartupDialogs", "/DisableStartupMessages"]);
        return args;
    }

    /// <summary>Итог выгрузки: код процесса, содержимое файла /DumpResult («0» — успех), размер .dt и журнал.</summary>
    internal static (bool Ok, string? Error) Evaluate(int exitCode, string? result, long? dtLength, string log)
    {
        result = result?.Trim();
        var ok = exitCode == 0 && (string.IsNullOrEmpty(result) || result == "0") && dtLength > 0;
        if (ok) return (true, null);

        var details = string.Join(" ", log.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3));
        if (details.Length == 0) details = dtLength is null or 0 ? "файл выгрузки не создан" : "Конфигуратор сообщил об ошибке";
        var code = string.IsNullOrEmpty(result) || result == exitCode.ToString() ? $"код {exitCode}" : $"код {exitCode}, результат {result}";
        return (false, $"выгрузка .dt не удалась ({code}): {Fmt.Truncate(details, 400)}");
    }

    /// <summary>Журнал Конфигуратора: UTF-8 с BOM, UTF-16 или ANSI (1251) в зависимости от версии платформы.</summary>
    internal static string ReadLog(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    private sealed class Unit(OneCDumpSourceConfig config) : BackupUnit
    {
        private string? _dt;

        public override async Task<ResticBackupRequest> PrepareAsync(BackupContext ctx, IList<string> notes, CancellationToken ct)
        {
            var exe = ResolvePlatform(config.PlatformPath)
                      ?? throw new InvalidOperationException(
                          "не найдена платформа 1С (Program Files\\1cv8\\<версия>\\bin\\1cv8.exe) — установите 1С или укажите путь к 1cv8.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException($"не найден {exe}");
            if (!string.IsNullOrWhiteSpace(config.FilePath))
            {
                var basePath = PathHelper.Normalize(config.FilePath);
                if (!File.Exists(System.IO.Path.Combine(basePath, OneCBaseDiscovery.DatabaseFile)))
                    throw new FileNotFoundException($"в папке нет {OneCBaseDiscovery.DatabaseFile}: {basePath}");
            }

            var dir = System.IO.Path.Combine(ctx.Config.StagingDirectory, "1c-dt");
            Directory.CreateDirectory(dir);
            var fileName = SystemInfo.SafeFileName(Name);
            if (!fileName.EndsWith(".dt", StringComparison.OrdinalIgnoreCase)) fileName += ".dt";
            _dt = System.IO.Path.Combine(dir, fileName);
            DeleteTemp();

            var psi = new ProcessStartInfo(exe);
            foreach (var a in BuildArguments(config, Secrets.Reveal(config.Password), _dt, _dt + ".log", _dt + ".result"))
                psi.ArgumentList.Add(a);

            ctx.Logger.LogInformation("{Unit}: выгрузка в {File}", Title, _dt);
            var sw = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(config.Timeout > TimeSpan.Zero ? config.Timeout : TimeSpan.FromHours(3));
            int exitCode;
            try
            {
                exitCode = await ProcessRunner.RunAsync(psi, null, null, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"выгрузка не завершилась за {Fmt.Duration(config.Timeout)} — Конфигуратор остановлен");
            }

            var log = File.Exists(_dt + ".log") ? ReadLog(await File.ReadAllBytesAsync(_dt + ".log", ct).ConfigureAwait(false)) : "";
            var result = File.Exists(_dt + ".result") ? await File.ReadAllTextAsync(_dt + ".result", ct).ConfigureAwait(false) : null;
            var length = File.Exists(_dt) ? new FileInfo(_dt).Length : (long?)null;
            var (ok, error) = Evaluate(exitCode, result, length, log);
            if (!ok) throw new InvalidOperationException(error);

            notes.Add($"выгрузка .dt: {Fmt.Size(length!.Value)} за {Fmt.Duration(sw.Elapsed)}");
            return new ResticBackupRequest { Paths = [_dt], Tags = Tags, UseVss = false };
        }

        public override Task CleanupAsync(BackupContext ctx)
        {
            try
            {
                DeleteTemp();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ctx.Logger.LogWarning(e, "Не удалось удалить временную выгрузку {File}", _dt);
            }

            return Task.CompletedTask;
        }

        private void DeleteTemp()
        {
            if (_dt is null) return;
            foreach (var f in new[] { _dt, _dt + ".log", _dt + ".result" })
                if (File.Exists(f))
                    File.Delete(f);
        }
    }
}
