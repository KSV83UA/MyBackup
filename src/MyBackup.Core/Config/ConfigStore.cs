namespace MyBackup.Core.Config;

public sealed record ConfigSaveResult(bool Saved, IReadOnlyList<string> Errors, string? BackupPath = null);

/// <summary>
/// Текущие настройки службы. Веб-интерфейс сохраняет новую версию целиком: файл переписывается
/// (прежняя версия — в config-backups), остальные части службы берут <see cref="Current"/> при каждом
/// использовании, поэтому перезапуск не нужен. Объект настроек после публикации не меняется.
/// </summary>
public sealed class ConfigStore
{
    private const int KeepBackups = 30;
    private readonly Lock _lock = new();
    private volatile AppConfig _current;
    private DateTime? _fileStamp;

    public ConfigStore(string path, AppConfig initial)
    {
        FilePath = path;
        _current = initial;
        _fileStamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }

    public string FilePath { get; }
    public AppConfig Current => _current;

    /// <summary>Вызывается после сохранения новой версии (в потоке сохранения).</summary>
    public event Action<AppConfig>? Changed;

    /// <summary>Ошибки текущих настроек. Задачи с ошибками в настройках не запускаются.</summary>
    public IReadOnlyList<string> CurrentErrors() => ConfigLoader.Validate(_current);

    /// <summary>
    /// Сохраняет настройки даже с ошибками заполнения (их можно исправлять по частям), но не поверх
    /// ручной правки файла, сделанной после запуска службы.
    /// </summary>
    public ConfigSaveResult Save(AppConfig config)
    {
        string? backup;
        lock (_lock)
        {
            var stamp = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : (DateTime?)null;
            if (stamp != _fileStamp)
                return new ConfigSaveResult(false,
                    [$"файл {FilePath} изменён вручную после запуска службы — перезапустите службу, чтобы не потерять эти правки"]);

            var json = ConfigLoader.Serialize(config);
            var copy = ConfigLoader.Parse(json); // наружу — копия, не связанная с объектом вызывающего
            backup = BackupCurrentFile();

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
            _fileStamp = File.GetLastWriteTimeUtc(FilePath);
            _current = copy;
        }

        Changed?.Invoke(_current);
        return new ConfigSaveResult(true, ConfigLoader.Validate(_current), backup);
    }

    private string? BackupCurrentFile()
    {
        if (!File.Exists(FilePath)) return null;
        var dir = _current.ConfigBackupDirectory;
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"config-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
        File.Copy(FilePath, target, overwrite: true);

        foreach (var old in new DirectoryInfo(dir).GetFiles("config-*.json")
                     .OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeepBackups))
        {
            try
            {
                old.Delete();
            }
            catch (IOException)
            {
                // не критично
            }
        }

        return target;
    }
}
