using System.Diagnostics;
using System.Text;

namespace MyBackup.Core.Sources;

/// <summary>База 1С из списков пользователей (ibases.v8i) или найденная на диске.</summary>
public sealed record OneCBaseInfo(
    string Name,
    string Kind,
    string? Path,
    string? Server,
    string? Ref,
    bool Exists,
    long? SizeBytes,
    IReadOnlyList<string> Users);

/// <summary>Поиск баз 1С: по спискам баз пользователей и по папкам на диске.</summary>
public static class OneCBaseDiscovery
{
    public const string DatabaseFile = "1Cv8.1CD";

    /// <summary>Базы из ibases.v8i всех пользователей и общего списка в ProgramData.</summary>
    public static IReadOnlyList<OneCBaseInfo> FromUserLists(IReadOnlyList<UserProfile> profiles)
    {
        var lists = profiles
            .Select(p => (User: p.UserName, File: System.IO.Path.Combine(p.ProfilePath, "AppData", "Roaming", "1C", "1CEStart", "ibases.v8i")))
            .Append(("общий список", System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "1C", "1CEStart", "ibases.v8i")));

        var found = new Dictionary<string, (string Name, string Kind, string? Path, string? Server, string? Ref, SortedSet<string> Users)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (user, file) in lists)
        {
            string text;
            try
            {
                if (!File.Exists(file)) continue;
                text = File.ReadAllText(file, Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var (name, connect) in ParseV8i(text))
            {
                var c = ParseConnect(connect);
                var key = c.Kind switch
                {
                    "file" => "file|" + NormalizeDir(c.Path!),
                    "server" => $"server|{c.Server}|{c.Ref}",
                    _ => "other|" + connect,
                };
                if (!found.TryGetValue(key, out var entry))
                    found[key] = entry = (name, c.Kind, c.Kind == "file" ? NormalizeDir(c.Path!) : c.Path, c.Server, c.Ref,
                        new SortedSet<string>(StringComparer.OrdinalIgnoreCase));
                entry.Users.Add(user);
            }
        }

        return found.Values
            .Select(e =>
            {
                var (exists, size) = e.Kind == "file" ? Probe(e.Path!) : (false, null);
                return new OneCBaseInfo(e.Name, e.Kind, e.Path, e.Server, e.Ref, exists, size, e.Users.ToList());
            })
            .OrderBy(b => b.Kind != "file").ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Папки с 1Cv8.1CD внутри <paramref name="root"/> (глубина и время поиска ограничены).</summary>
    public static IReadOnlyList<OneCBaseInfo> ScanFolder(string root, int maxDepth, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        var result = new List<OneCBaseInfo>();
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((System.IO.Path.GetFullPath(root), 0));
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        while (queue.Count > 0 && sw.Elapsed < timeout && result.Count < 200)
        {
            var (dir, depth) = queue.Dequeue();
            if (File.Exists(System.IO.Path.Combine(dir, DatabaseFile)))
            {
                var (exists, size) = Probe(dir);
                result.Add(new OneCBaseInfo(System.IO.Path.GetFileName(dir.TrimEnd('\\')), "file", dir, null, null, exists, size, []));
                continue; // внутри базы других баз не бывает
            }

            if (depth >= maxDepth) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", options))
                {
                    var name = System.IO.Path.GetFileName(sub);
                    if (name.StartsWith('$') || name.Equals("Windows", StringComparison.OrdinalIgnoreCase)) continue;
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // пропускаем
            }
        }

        return result.OrderBy(b => b.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Разбор ibases.v8i: секции [Имя] со строкой Connect=… (папки списка без Connect пропускаются).</summary>
    public static IEnumerable<(string Name, string Connect)> ParseV8i(string text)
    {
        string? name = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                name = line[1..^1].Trim();
                continue;
            }

            if (name is not null && line.StartsWith("Connect=", StringComparison.OrdinalIgnoreCase))
            {
                yield return (name, line["Connect=".Length..]);
                name = null;
            }
        }
    }

    /// <summary>File="D:\1C\Buh"; → файловая; Srvr="srv";Ref="buh"; → серверная; иначе — другая (веб-публикация).</summary>
    public static (string Kind, string? Path, string? Server, string? Ref) ParseConnect(string connect)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < connect.Length)
        {
            var eq = connect.IndexOf('=', i);
            if (eq < 0) break;
            var key = connect[i..eq].Trim().TrimStart(';').Trim();
            i = eq + 1;
            var value = new StringBuilder();
            if (i < connect.Length && connect[i] == '"')
            {
                i++;
                while (i < connect.Length)
                {
                    if (connect[i] == '"')
                    {
                        if (i + 1 < connect.Length && connect[i + 1] == '"')
                        {
                            value.Append('"');
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    value.Append(connect[i++]);
                }

                var semi = connect.IndexOf(';', i);
                i = semi < 0 ? connect.Length : semi + 1;
            }
            else
            {
                var semi = connect.IndexOf(';', i);
                value.Append(semi < 0 ? connect[i..] : connect[i..semi]);
                i = semi < 0 ? connect.Length : semi + 1;
            }

            if (key.Length > 0) values[key] = value.ToString();
        }

        if (values.TryGetValue("File", out var path) && path.Length > 0) return ("file", path, null, null);
        if (values.TryGetValue("Srvr", out var srvr)) return ("server", null, srvr, values.GetValueOrDefault("Ref"));
        return ("other", null, null, null);
    }

    private static string NormalizeDir(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path).TrimEnd('\\');
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static (bool Exists, long? Size) Probe(string dir)
    {
        try
        {
            var file = new FileInfo(System.IO.Path.Combine(dir, DatabaseFile));
            return file.Exists ? (true, file.Length) : (false, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (false, null);
        }
    }
}
