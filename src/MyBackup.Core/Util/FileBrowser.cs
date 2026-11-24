namespace MyBackup.Core.Util;

public sealed record FsEntry(
    string Name,
    string Path,
    bool IsDirectory,
    long? Size,
    DateTime? Modified,
    bool Hidden,
    bool IsLink,
    string? Label = null,
    long? FreeBytes = null,
    long? TotalBytes = null);

public sealed record FsListing(string? Path, string? Parent, IReadOnlyList<FsEntry> Entries, bool Truncated, string? Error);

/// <summary>Содержимое папок сервера для выбора в веб-интерфейсе. Без пути — список дисков.</summary>
public static class FileBrowser
{
    public static FsListing List(string? path, bool includeFiles, int max = 3000)
    {
        if (string.IsNullOrWhiteSpace(path)) return Drives();

        string full;
        try
        {
            full = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new FsListing(path, null, [], false, "неверный путь");
        }

        if (!System.IO.Path.IsPathFullyQualified(full)) return new FsListing(path, null, [], false, "нужен полный путь");

        var dir = new DirectoryInfo(full);
        var parent = dir.Parent?.FullName;
        if (!dir.Exists) return new FsListing(full, parent, [], false, "папка не найдена");

        var entries = new List<FsEntry>();
        var truncated = false;
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
            foreach (var info in dir.EnumerateFileSystemInfos("*", options)
                         .OrderBy(i => i is FileInfo).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            {
                var isDir = info is DirectoryInfo;
                if (!isDir && !includeFiles) continue;
                if (entries.Count >= max)
                {
                    truncated = true;
                    break;
                }

                var attrs = info.Attributes;
                entries.Add(new FsEntry(
                    info.Name,
                    info.FullName,
                    isDir,
                    info is FileInfo f ? f.Length : null,
                    info.LastWriteTime,
                    attrs.HasFlag(FileAttributes.Hidden) || attrs.HasFlag(FileAttributes.System),
                    attrs.HasFlag(FileAttributes.ReparsePoint)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new FsListing(full, parent, entries, false, e is UnauthorizedAccessException ? "нет доступа" : e.Message);
        }

        return new FsListing(full, parent, entries, truncated, null);
    }

    private static FsListing Drives()
    {
        var entries = new List<FsEntry>();
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown) continue;
            try
            {
                if (!d.IsReady) continue;
                var kind = d.DriveType switch
                {
                    DriveType.Network => "сетевой",
                    DriveType.Removable => "съёмный",
                    _ => null,
                };
                var label = string.Join(" · ", new[] { d.VolumeLabel, kind }.Where(s => !string.IsNullOrWhiteSpace(s)));
                entries.Add(new FsEntry(d.Name.TrimEnd('\\'), d.RootDirectory.FullName, true, null, null, false, false,
                    label, d.AvailableFreeSpace, d.TotalSize));
            }
            catch (IOException)
            {
                // диск отключился
            }
        }

        return new FsListing(null, null, entries, false, null);
    }
}
