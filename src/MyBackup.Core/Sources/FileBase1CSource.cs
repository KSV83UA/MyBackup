using MyBackup.Core.Config;
using MyBackup.Core.Restic;

namespace MyBackup.Core.Sources;

/// <summary>Файловая база 1С: копия папки с 1Cv8.1CD через VSS-снимок, без остановки базы.</summary>
public sealed class FileBase1CSource(FileBase1CSourceConfig config) : SingleUnitSource
{
    private string DisplayName => config.EffectiveName;

    public override string Title => $"1С: {DisplayName}";

    protected override BackupUnit CreateUnit() =>
        new Unit(config) { Title = Title, SourceType = config.TypeName, Name = DisplayName };

    private sealed class Unit(FileBase1CSourceConfig config) : BackupUnit
    {
        public override Task<ResticBackupRequest> PrepareAsync(BackupContext ctx, IList<string> notes, CancellationToken ct)
        {
            var path = PathHelper.Normalize(config.Path);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"папка базы не найдена: {path}");
            if (!File.Exists(Path.Combine(path, "1Cv8.1CD"))) notes.Add("в папке нет файла 1Cv8.1CD — проверьте путь");
            if (!ctx.VssAvailable) notes.Add("без VSS: база копируется «на ходу», копия может быть несогласованной");

            var excludes = new List<string> { "1Cv8tmp" };
            if (config.ExcludeFullTextIndex) excludes.Add("1Cv8FTxt*");
            if (config.ExcludeEventLog) excludes.Add("1Cv8Log");

            return Task.FromResult(new ResticBackupRequest
            {
                Paths = [path], IExcludes = excludes, Tags = Tags, UseVss = ctx.VssAvailable,
            });
        }
    }
}
