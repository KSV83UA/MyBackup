using MyBackup.Core.Config;
using MyBackup.Core.Restic;

namespace MyBackup.Core.Sources;

/// <summary>
/// Служебные файлы 1С: программные лицензии (иначе после переустановки их приходится активировать заново),
/// реестр кластера сервера 1С (srvinfo\*.lst) и списки баз пользователей (1CEStart).
/// </summary>
public sealed class OneCServiceSource(OneCServiceSourceConfig config) : SingleUnitSource
{
    private string DisplayName => config.EffectiveName;

    public override string Title => DisplayName;

    protected override BackupUnit CreateUnit() =>
        new Unit(config) { Title = Title, SourceType = config.TypeName, Name = DisplayName };

    public static List<string> CollectPaths(OneCServiceSourceConfig config, IReadOnlyList<UserProfile> profiles)
    {
        var paths = new List<string>();
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        void AddDir(string p)
        {
            if (Directory.Exists(p)) paths.Add(p);
        }

        void AddFile(string p)
        {
            if (File.Exists(p)) paths.Add(p);
        }

        if (config.IncludeLicenses) AddDir(Path.Combine(programData, "1C", "licenses"));
        AddDir(Path.Combine(programData, "1C", "1CEStart"));

        var srvInfo = config.SrvInfoPaths.Count > 0
            ? config.SrvInfoPaths.Select(PathHelper.Normalize)
            : new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "1cv8", "srvinfo"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "1cv8", "srvinfo"),
            };

        foreach (var dir in srvInfo.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
        {
            AddFile(Path.Combine(dir, "1cv8wsrv.lst"));
            foreach (var reg in Directory.EnumerateDirectories(dir, "reg_*"))
            {
                AddFile(Path.Combine(reg, "1CV8Clst.lst"));
                AddFile(Path.Combine(reg, "1CV8Reg.lst"));
            }
        }

        if (config.IncludeUserBaseLists)
            foreach (var profile in profiles)
                AddDir(Path.Combine(profile.ProfilePath, "AppData", "Roaming", "1C", "1CEStart"));

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed class Unit(OneCServiceSourceConfig config) : BackupUnit
    {
        public override Task<ResticBackupRequest> PrepareAsync(BackupContext ctx, IList<string> notes, CancellationToken ct)
        {
            IReadOnlyList<UserProfile> profiles = config.IncludeUserBaseLists ? ProfileDiscovery.Discover([]) : [];
            var paths = CollectPaths(config, profiles);
            if (paths.Count == 0) throw new SkipUnitException("служебные файлы 1С не найдены");

            notes.Add($"объектов: {paths.Count}");
            return Task.FromResult(new ResticBackupRequest { Paths = paths, Tags = Tags, UseVss = ctx.VssAvailable });
        }
    }
}
