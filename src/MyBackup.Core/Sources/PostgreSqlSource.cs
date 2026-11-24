using System.Diagnostics;
using MyBackup.Core.Config;
using MyBackup.Core.Restic;
using MyBackup.Core.Util;

namespace MyBackup.Core.Sources;

/// <summary>
/// Базы PostgreSQL: pg_dump -Fc -Z0 идёт потоком в restic (--stdin-from-command),
/// на диск ничего не пишется; ошибка pg_dump прерывает снимок.
/// </summary>
public sealed class PostgreSqlSource(PostgreSqlSourceConfig config) : IBackupSource
{
    public string Title => string.IsNullOrWhiteSpace(config.Name) ? "PostgreSQL" : $"PostgreSQL: {config.Name}";

    public async Task<IReadOnlyList<BackupUnit>> PlanAsync(BackupContext ctx, IList<string> notes, CancellationToken ct)
    {
        IEnumerable<string> databases = config.Databases;
        if (config.Databases.Contains("*")) databases = await ListDatabasesAsync(ct).ConfigureAwait(false);

        var exclude = new HashSet<string>(config.ExcludeDatabases, StringComparer.OrdinalIgnoreCase);
        return databases
            .Where(d => d != "*" && !exclude.Contains(d))
            .Distinct(StringComparer.Ordinal)
            .Select(db =>
            {
                var name = string.IsNullOrWhiteSpace(config.Name) ? db : $"{config.Name}/{db}";
                return (BackupUnit)new DatabaseUnit(config, db)
                {
                    Title = $"PostgreSQL: {name}", SourceType = config.TypeName, Name = name,
                };
            })
            .ToList();
    }

    private async Task<List<string>> ListDatabasesAsync(CancellationToken ct)
    {
        var pgDump = PathHelper.Normalize(config.PgDumpPath);
        var psql = Path.Combine(Path.GetDirectoryName(pgDump) ?? "", "psql.exe");
        if (!File.Exists(psql)) throw new FileNotFoundException($"для \"*\" нужен psql.exe рядом с pg_dump: {psql}");

        var psi = new ProcessStartInfo(psql);
        foreach (var a in ConnectionArgs("postgres")) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-At");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY 1");
        psi.Environment["PGPASSWORD"] = Secrets.Reveal(config.Password);

        var names = new List<string>();
        var errors = new List<string>();
        var code = await ProcessRunner.RunAsync(psi,
            l =>
            {
                if (!string.IsNullOrWhiteSpace(l)) lock (names) names.Add(l.Trim());
            },
            l =>
            {
                if (!string.IsNullOrWhiteSpace(l)) lock (errors) errors.Add(l);
            }, ct).ConfigureAwait(false);
        if (code != 0) throw new InvalidOperationException($"psql: {string.Join(" | ", errors.TakeLast(3))}");
        return names;
    }

    private List<string> ConnectionArgs(string database) =>
        ["-w", "-h", config.Host, "-p", config.Port.ToString(), "-U", config.User, "-d", database];

    private sealed class DatabaseUnit(PostgreSqlSourceConfig config, string database) : BackupUnit
    {
        public override Task<ResticBackupRequest> PrepareAsync(BackupContext ctx, IList<string> notes, CancellationToken ct)
        {
            var pgDump = PathHelper.Normalize(config.PgDumpPath);
            if (!File.Exists(pgDump)) throw new FileNotFoundException($"pg_dump не найден: {pgDump}");

            List<string> args = ["-Fc", "-Z0", "-w", "-h", config.Host, "-p", config.Port.ToString(), "-U", config.User, "-d", database];
            return Task.FromResult(new ResticBackupRequest
            {
                Stdin = new StdinCommand($"{SystemInfo.SafeFileName(database)}.pgdump", pgDump, args),
                Tags = Tags,
                Environment = new Dictionary<string, string> { ["PGPASSWORD"] = Secrets.Reveal(config.Password) },
            });
        }
    }
}
