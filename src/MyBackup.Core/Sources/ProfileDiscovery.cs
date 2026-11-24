using Microsoft.Win32;

namespace MyBackup.Core.Sources;

public sealed record UserProfile(string Sid, string UserName, string ProfilePath, bool HiveLoaded);

/// <summary>Поиск профилей пользователей по реестру (ProfileList), без системных и служебных учётных записей.</summary>
public static class ProfileDiscovery
{
    private const string ProfileListKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public static IReadOnlyList<UserProfile> Discover(IEnumerable<string> excludeUsers)
    {
        var exclude = excludeUsers.ToList();
        using var list = Registry.LocalMachine.OpenSubKey(ProfileListKey);
        if (list is null) return [];

        var result = new List<UserProfile>();
        foreach (var sid in list.GetSubKeyNames())
        {
            using var key = list.OpenSubKey(sid);
            if (key?.GetValue("ProfileImagePath") is not string rawPath) continue;

            var path = Environment.ExpandEnvironmentVariables(rawPath);
            if (!ProfileFilter.IsRegularUser(sid, path, exclude)) continue;
            if (!Directory.Exists(path)) continue;

            result.Add(new UserProfile(sid, ProfileFilter.FolderName(path), path, IsHiveLoaded(sid)));
        }

        return result.OrderBy(p => p.UserName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsHiveLoaded(string sid)
    {
        using var key = Registry.Users.OpenSubKey(sid);
        return key is not null;
    }
}

public static class ProfileFilter
{
    /// <summary>
    /// Обычные пользователи — SID вида S-1-5-21-… (локальные и доменные). Отсекаются SYSTEM (S-1-5-18),
    /// LOCAL/NETWORK SERVICE (S-1-5-19/20), служебные (S-1-5-80/82/90/96…), испорченные копии (.bak),
    /// временные профили TEMP и пользователи из списка исключений.
    /// </summary>
    public static bool IsRegularUser(string sid, string profilePath, IReadOnlyCollection<string> excludeUsers)
    {
        if (!sid.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase)) return false;
        if (sid.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) return false;

        var name = FolderName(profilePath);
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Equals("TEMP", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("TEMP.", StringComparison.OrdinalIgnoreCase)) return false;

        var shortName = name.Split('.')[0]; // ivanov.DOMAIN → ivanov
        return !excludeUsers.Any(u =>
            u.Equals(name, StringComparison.OrdinalIgnoreCase) || u.Equals(shortName, StringComparison.OrdinalIgnoreCase));
    }

    public static string FolderName(string profilePath) => Path.GetFileName(profilePath.TrimEnd('\\', '/'));
}

/// <summary>Папки профиля с учётом перенаправления (User Shell Folders).</summary>
public static class KnownFolders
{
    private static readonly Dictionary<string, (string RegistryValue, string Default)> Map =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Desktop"] = ("Desktop", "Desktop"),
            ["Documents"] = ("Personal", "Documents"),
            ["Downloads"] = ("{374DE290-123F-4565-9164-39C4925E467B}", "Downloads"),
            ["Pictures"] = ("My Pictures", "Pictures"),
            ["Music"] = ("My Music", "Music"),
            ["Videos"] = ("My Video", "Videos"),
            ["Favorites"] = ("Favorites", "Favorites"),
        };

    public static bool IsKnown(string folder) => Map.ContainsKey(folder);

    /// <summary>
    /// Путь папки. Перенаправление видно, только когда куст пользователя загружен (пользователь в системе);
    /// иначе берётся стандартный путь внутри профиля.
    /// </summary>
    public static string Resolve(UserProfile profile, string folder, Func<string, string, string?> readShellFolder)
    {
        if (Map.TryGetValue(folder, out var m))
        {
            var raw = profile.HiveLoaded ? readShellFolder(profile.Sid, m.RegistryValue) : null;
            return string.IsNullOrWhiteSpace(raw)
                ? Path.Combine(profile.ProfilePath, m.Default)
                : ExpandForUser(raw, profile);
        }

        return Path.Combine(profile.ProfilePath, folder);
    }

    public static string ExpandForUser(string raw, UserProfile profile)
    {
        var s = raw
            .Replace("%USERPROFILE%", profile.ProfilePath, StringComparison.OrdinalIgnoreCase)
            .Replace("%USERNAME%", profile.UserName.Split('.')[0], StringComparison.OrdinalIgnoreCase);
        return Environment.ExpandEnvironmentVariables(s);
    }

    public static string? ReadShellFolder(string sid, string valueName)
    {
        using var key = Registry.Users.OpenSubKey($@"{sid}\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
        return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }
}
