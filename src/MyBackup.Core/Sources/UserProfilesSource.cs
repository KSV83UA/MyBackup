using MyBackup.Core.Config;
using MyBackup.Core.Restic;
using MyBackup.Core.Util;

namespace MyBackup.Core.Sources;

/// <summary>Основные папки всех пользовательских профилей одним снимком.</summary>
public sealed class UserProfilesSource(UserProfilesSourceConfig config) : SingleUnitSource
{
    private string DisplayName => config.EffectiveName;

    public override string Title => DisplayName;

    protected override BackupUnit CreateUnit() =>
        new Unit(config) { Title = Title, SourceType = config.TypeName, Name = DisplayName };

    /// <summary>Список путей для копирования; отдельно — для тестов и команды MyBackup profiles.</summary>
    public static (List<string> Paths, List<string> Notes, int Users) ResolvePaths(
        UserProfilesSourceConfig config, IReadOnlyList<UserProfile> profiles, Func<string, string, string?> readShellFolder)
    {
        var paths = new List<string>();
        var notes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var users = 0;

        foreach (var profile in profiles)
        {
            var any = false;
            foreach (var folder in config.Folders)
            {
                var path = KnownFolders.Resolve(profile, folder, readShellFolder);
                if (SystemInfo.IsNetworkPath(path))
                {
                    notes.Add($"{profile.UserName}: {folder} на сетевом ресурсе ({path}) — пропущено");
                    continue;
                }

                if (!Directory.Exists(path) && !File.Exists(path)) continue;
                if (seen.Add(Path.GetFullPath(path)))
                {
                    paths.Add(Path.GetFullPath(path));
                    any = true;
                }
            }

            if (any) users++;
        }

        return (paths, notes, users);
    }

    private sealed class Unit(UserProfilesSourceConfig config) : BackupUnit
    {
        public override Task<ResticBackupRequest> PrepareAsync(BackupContext ctx, IList<string> notes, CancellationToken ct)
        {
            var profiles = ProfileDiscovery.Discover(config.ExcludeUsers);
            var (paths, resolveNotes, users) = ResolvePaths(config, profiles, KnownFolders.ReadShellFolder);
            foreach (var n in resolveNotes) notes.Add(n);
            if (paths.Count == 0) throw new SkipUnitException("профили пользователей с данными не найдены");

            notes.Insert(0, $"пользователей: {users}");
            return Task.FromResult(new ResticBackupRequest
            {
                Paths = paths, IExcludes = config.Exclude, Tags = Tags, UseVss = ctx.VssAvailable,
            });
        }
    }
}
