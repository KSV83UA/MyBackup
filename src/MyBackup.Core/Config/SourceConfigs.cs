using System.Text.Json.Serialization;

namespace MyBackup.Core.Config;

/// <summary>Что копировать. Тип задаётся полем "type".</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FileBase1CSourceConfig), "fileBase1C")]
[JsonDerivedType(typeof(MsSqlSourceConfig), "msSql")]
[JsonDerivedType(typeof(PostgreSqlSourceConfig), "postgreSql")]
[JsonDerivedType(typeof(UserProfilesSourceConfig), "userProfiles")]
[JsonDerivedType(typeof(PathsSourceConfig), "paths")]
[JsonDerivedType(typeof(OneCServiceSourceConfig), "oneCService")]
[JsonDerivedType(typeof(OneCDumpSourceConfig), "oneCDump")]
public abstract class SourceConfig
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    [JsonIgnore] public abstract string TypeName { get; }

    /// <summary>Имя в отчёте и в теге снимков name:…; для баз данных к нему добавляется имя базы.</summary>
    [JsonIgnore] public virtual string EffectiveName => Name;
}

/// <summary>Файловая база 1С: папка с 1Cv8.1CD, копируется через VSS.</summary>
public sealed class FileBase1CSourceConfig : SourceConfig
{
    public string Path { get; set; } = "";

    /// <summary>Не копировать полнотекстовый индекс (1Cv8FTxt*) — он перестраивается.</summary>
    public bool ExcludeFullTextIndex { get; set; } = true;

    /// <summary>Не копировать журнал регистрации (1Cv8Log).</summary>
    public bool ExcludeEventLog { get; set; }

    [JsonIgnore] public override string TypeName => "fileBase1C";

    [JsonIgnore] public override string EffectiveName => string.IsNullOrWhiteSpace(Name)
        ? System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'))
        : Name;
}

/// <summary>Базы MS SQL: BACKUP DATABASE в файл, затем restic, затем файл удаляется.</summary>
public sealed class MsSqlSourceConfig : SourceConfig
{
    public string ConnectionString { get; set; } = "Server=.;Integrated Security=true;TrustServerCertificate=true";

    /// <summary>Имена баз; "*" — все пользовательские базы.</summary>
    public List<string> Databases { get; set; } = [];

    public List<string> ExcludeDatabases { get; set; } = [];

    /// <summary>Куда SQL Server пишет копию. По умолчанию — папка резервных копий экземпляра.</summary>
    public string? BackupDirectory { get; set; }

    /// <summary>Проверять копию командой RESTORE VERIFYONLY.</summary>
    public bool Verify { get; set; } = true;

    /// <summary>Сколько места оставить свободным на диске после создания копии, МиБ.</summary>
    public long ReserveFreeSpaceMiB { get; set; } = 1024;

    [JsonIgnore] public override string TypeName => "msSql";
}

/// <summary>Базы PostgreSQL: pg_dump идёт потоком прямо в restic, на диск ничего не пишется.</summary>
public sealed class PostgreSqlSourceConfig : SourceConfig
{
    public string PgDumpPath { get; set; } = "";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string User { get; set; } = "postgres";
    public string Password { get; set; } = "";

    /// <summary>Имена баз; "*" — все базы, кроме шаблонных.</summary>
    public List<string> Databases { get; set; } = [];

    public List<string> ExcludeDatabases { get; set; } = ["postgres"];

    [JsonIgnore] public override string TypeName => "postgreSql";
}

/// <summary>Профили пользователей без системных учётных записей.</summary>
public sealed class UserProfilesSourceConfig : SourceConfig
{
    /// <summary>
    /// Папки профиля: Desktop, Documents, Downloads, Pictures, Music, Videos, Favorites
    /// или относительный путь внутри профиля (например, AppData\Roaming\Программа).
    /// </summary>
    public List<string> Folders { get; set; } = ["Desktop", "Documents", "Downloads", "Pictures", "Favorites"];

    public List<string> ExcludeUsers { get; set; } = [];

    /// <summary>Маски исключений (без учёта регистра).</summary>
    public List<string> Exclude { get; set; } = ["~$*", "*.tmp", "Thumbs.db", "desktop.ini"];

    [JsonIgnore] public override string TypeName => "userProfiles";

    [JsonIgnore] public override string EffectiveName => string.IsNullOrWhiteSpace(Name) ? "Профили пользователей" : Name;
}

/// <summary>Произвольные папки и файлы.</summary>
public sealed class PathsSourceConfig : SourceConfig
{
    public List<string> Paths { get; set; } = [];

    /// <summary>Маски исключений (без учёта регистра).</summary>
    public List<string> Exclude { get; set; } = [];

    public bool UseVss { get; set; } = true;

    [JsonIgnore] public override string TypeName => "paths";

    [JsonIgnore] public override string EffectiveName => string.IsNullOrWhiteSpace(Name) ? "Папки" : Name;
}

/// <summary>Служебные файлы 1С: лицензии, настройки кластера, списки баз пользователей.</summary>
public sealed class OneCServiceSourceConfig : SourceConfig
{
    /// <summary>Папки srvinfo сервера 1С. По умолчанию ищется в Program Files\1cv8\srvinfo.</summary>
    public List<string> SrvInfoPaths { get; set; } = [];

    public bool IncludeLicenses { get; set; } = true;
    public bool IncludeUserBaseLists { get; set; } = true;

    [JsonIgnore] public override string TypeName => "oneCService";

    [JsonIgnore] public override string EffectiveName => string.IsNullOrWhiteSpace(Name) ? "Служебные файлы 1С" : Name;
}

/// <summary>
/// Выгрузка информационной базы 1С в .dt через Конфигуратор в пакетном режиме (DESIGNER /DumpIB).
/// Файл .dt сжат, поэтому передаётся целиком при каждом запуске — удобно отдельной задачей раз в неделю.
/// </summary>
public sealed class OneCDumpSourceConfig : SourceConfig
{
    /// <summary>Путь к 1cv8.exe. Пусто — самая новая платформа из Program Files\1cv8.</summary>
    public string? PlatformPath { get; set; }

    /// <summary>Файловая база: папка с 1Cv8.1CD.</summary>
    public string? FilePath { get; set; }

    /// <summary>Серверная база: "сервер\имя_базы", как в окне запуска 1С.</summary>
    public string? ServerBase { get; set; }

    /// <summary>Пользователь 1С с правами администратора; пусто — если пользователей в базе нет.</summary>
    public string? User { get; set; }

    public string? Password { get; set; }

    /// <summary>Сколько ждать выгрузку, прежде чем считать, что Конфигуратор завис.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromHours(3);

    [JsonIgnore] public override string TypeName => "oneCDump";

    [JsonIgnore] public override string EffectiveName =>
        !string.IsNullOrWhiteSpace(Name) ? Name
        : !string.IsNullOrWhiteSpace(FilePath) ? System.IO.Path.GetFileName(FilePath.Trim().TrimEnd('\\', '/')) + ".dt"
        : !string.IsNullOrWhiteSpace(ServerBase) ? ServerBase.Trim().Split('\\', '/').Last() + ".dt"
        : "Выгрузка 1С";
}
