using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MyBackup.Core.Config;
using MyBackup.Core.Restic;
using MyBackup.Core.Util;

namespace MyBackup.Core.Sources;

public sealed record SqlDatabaseInfo(string Name, long? SizeBytes, string RecoveryModel, string State, bool CanBackup);

public sealed record SqlServerInfo(string Login, string Version, IReadOnlyList<SqlDatabaseInfo> Databases);

/// <summary>
/// Базы MS SQL. Для каждой базы: BACKUP DATABASE (COPY_ONLY, CHECKSUM, без сжатия) в файл,
/// RESTORE VERIFYONLY, отправка файла через restic, удаление файла. Базы обрабатываются по одной,
/// поэтому место нужно только под самую большую.
/// </summary>
public sealed class MsSqlSource(MsSqlSourceConfig config) : IBackupSource
{
    public string Title => string.IsNullOrWhiteSpace(config.Name) ? "SQL" : $"SQL: {config.Name}";

    public async Task<IReadOnlyList<BackupUnit>> PlanAsync(BackupContext ctx, IList<string> notes, CancellationToken ct)
    {
        IEnumerable<string> databases = config.Databases;
        if (config.Databases.Contains("*"))
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            databases = await ListUserDatabasesAsync(conn, ct).ConfigureAwait(false);
        }

        var exclude = new HashSet<string>(config.ExcludeDatabases, StringComparer.OrdinalIgnoreCase);
        return databases
            .Where(d => d != "*" && !exclude.Contains(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(db =>
            {
                var name = string.IsNullOrWhiteSpace(config.Name) ? db : $"{config.Name}/{db}";
                return (BackupUnit)new DatabaseUnit(config, db) { Title = $"SQL: {name}", SourceType = config.TypeName, Name = name };
            })
            .ToList();
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(Secrets.Reveal(config.ConnectionString));
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private static async Task<List<string>> ListUserDatabasesAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT name FROM sys.databases
            WHERE database_id > 4 AND state = 0 AND source_database_id IS NULL
            ORDER BY name
            """;
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) list.Add(reader.GetString(0));
        return list;
    }

    /// <summary>
    /// Пользовательские базы с размером и правом на BACKUP — для выбора в веб-интерфейсе.
    /// Служба работает как SYSTEM: в SQL Server 2012+ у этой учётной записи прав на копирование по умолчанию нет.
    /// </summary>
    public static async Task<SqlServerInfo> ListDatabasesAsync(string connectionString, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(Secrets.Reveal(connectionString));
        if (builder.ConnectTimeout > 15) builder.ConnectTimeout = 15;
        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        string login, version;
        await using (var info = new SqlCommand("SELECT SUSER_SNAME(), CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128))", conn))
        await using (var r = await info.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            await r.ReadAsync(ct).ConfigureAwait(false);
            login = r.IsDBNull(0) ? "" : r.GetString(0);
            version = r.IsDBNull(1) ? "" : r.GetString(1);
        }

        // sys.master_files виден не всем — тогда размер неизвестен, но список баз есть.
        const string sql = """
            SELECT d.name,
                   (SELECT SUM(CAST(mf.size AS bigint)) * 8192 FROM sys.master_files mf WHERE mf.database_id = d.database_id),
                   d.recovery_model_desc, d.state_desc,
                   HAS_PERMS_BY_NAME(d.name, 'DATABASE', 'BACKUP DATABASE')
            FROM sys.databases d
            WHERE d.database_id > 4 AND d.source_database_id IS NULL
            ORDER BY d.name
            """;
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<SqlDatabaseInfo>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new SqlDatabaseInfo(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetString(2), reader.GetString(3), !reader.IsDBNull(4) && reader.GetInt32(4) == 1));
        return new SqlServerInfo(login, version, list);
    }

    internal static string QuoteName(string name) => "[" + name.Replace("]", "]]") + "]";

    private sealed class DatabaseUnit(MsSqlSourceConfig config, string database) : BackupUnit
    {
        private string? _backupFile;

        public override async Task<ResticBackupRequest> PrepareAsync(BackupContext ctx, IList<string> notes, CancellationToken ct)
        {
            await using var conn = new SqlConnection(Secrets.Reveal(config.ConnectionString));
            await conn.OpenAsync(ct).ConfigureAwait(false);

            var baseDir = config.BackupDirectory is { Length: > 0 } configured
                ? PathHelper.Normalize(configured)
                : await GetDefaultBackupDirectoryAsync(conn, ct).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("не удалось определить папку резервных копий SQL Server — укажите backupDirectory");

            var dir = Path.Combine(baseDir, "MyBackup");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, SystemInfo.SafeFileName(database) + ".bak");
            _backupFile = file;

            var needed = await EstimateBackupSizeAsync(conn, ct).ConfigureAwait(false);
            var free = SystemInfo.GetFreeBytes(dir);
            var reserve = config.ReserveFreeSpaceMiB * 1024 * 1024;
            if (needed is { } n && free is { } f && n + reserve > f)
                throw new InvalidOperationException(
                    $"недостаточно места для копии базы: нужно ~{Fmt.Size(n)} + запас {Fmt.Size(reserve)}, свободно {Fmt.Size(f)} ({dir})");

            var compressionDefault = await ScalarAsync<int?>(conn,
                "SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = 'backup compression default'", ct)
                .ConfigureAwait(false);

            // Без сжатия SQL: restic сам сожмёт и передаст только изменённые блоки.
            var backupSql = $"BACKUP DATABASE {QuoteName(database)} TO DISK = @path WITH COPY_ONLY, CHECKSUM, INIT, FORMAT"
                            + (compressionDefault == 1 ? ", NO_COMPRESSION" : "");
            ctx.Logger.LogInformation("SQL {Database}: BACKUP в {File}", database, file);
            await ExecAsync(conn, backupSql, file, ct).ConfigureAwait(false);

            if (!File.Exists(file))
                throw new InvalidOperationException(
                    $"файл копии {file} не найден на этом компьютере — SQL Server на другом сервере? Укажите backupDirectory как общую папку");

            if (config.Verify)
            {
                await ExecAsync(conn, "RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM", file, ct).ConfigureAwait(false);
                notes.Add("проверка копии SQL пройдена");
            }

            return new ResticBackupRequest { Paths = [file], Tags = Tags, UseVss = false };
        }

        public override Task CleanupAsync(BackupContext ctx)
        {
            if (_backupFile is not null && File.Exists(_backupFile))
            {
                try
                {
                    File.Delete(_backupFile);
                }
                catch (Exception e)
                {
                    ctx.Logger.LogWarning(e, "Не удалось удалить временную копию {File}", _backupFile);
                }
            }

            return Task.CompletedTask;
        }

        private async Task<long?> EstimateBackupSizeAsync(SqlConnection conn, CancellationToken ct)
        {
            var sql = $"EXEC {QuoteName(database)}.sys.sp_executesql N'SELECT CAST(SUM(CAST(FILEPROPERTY(name, ''SpaceUsed'') AS bigint)) * 8192 AS bigint) FROM sys.database_files WHERE type = 0'";
            var used = await ScalarAsync<long?>(conn, sql, ct).ConfigureAwait(false);
            return used is null ? null : (long)(used.Value * 1.1);
        }

        private static async Task<string?> GetDefaultBackupDirectoryAsync(SqlConnection conn, CancellationToken ct)
        {
            var dir = await ScalarAsync<string?>(conn,
                "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000))", ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(dir)) return dir;

            await using var cmd = new SqlCommand(
                "EXEC master.dbo.xp_instance_regread N'HKEY_LOCAL_MACHINE', N'Software\\Microsoft\\MSSQLServer\\MSSQLServer', N'BackupDirectory'",
                conn);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) && !reader.IsDBNull(1) ? reader.GetString(1) : null;
        }

        private static async Task<T?> ScalarAsync<T>(SqlConnection conn, string sql, CancellationToken ct)
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
            var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return value is null or DBNull ? default : (T)value;
        }

        private static async Task ExecAsync(SqlConnection conn, string sql, string path, CancellationToken ct)
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 0 };
            cmd.Parameters.Add(new SqlParameter("@path", SqlDbType.NVarChar, 4000) { Value = path });
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
