using MyBackup.Core.Config;
using MyBackup.Core.Restic;

namespace MyBackup.Core.Sources;

/// <summary>Произвольные папки и файлы с масками исключений.</summary>
public sealed class PathsSource(PathsSourceConfig config) : SingleUnitSource
{
    private string DisplayName => config.EffectiveName;

    public override string Title => $"Папки: {DisplayName}";

    protected override BackupUnit CreateUnit() =>
        new Unit(config) { Title = Title, SourceType = config.TypeName, Name = DisplayName };

    private sealed class Unit(PathsSourceConfig config) : BackupUnit
    {
        public override Task<ResticBackupRequest> PrepareAsync(BackupContext ctx, IList<string> notes, CancellationToken ct)
        {
            var existing = new List<string>();
            foreach (var raw in config.Paths)
            {
                var path = PathHelper.Normalize(raw);
                if (Directory.Exists(path) || File.Exists(path)) existing.Add(path);
                else notes.Add($"не найден путь: {path}");
            }

            if (existing.Count == 0) throw new InvalidOperationException("ни один из указанных путей не найден");

            return Task.FromResult(new ResticBackupRequest
            {
                Paths = existing.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                IExcludes = config.Exclude,
                Tags = Tags,
                UseVss = config.UseVss && ctx.VssAvailable,
            });
        }
    }
}
