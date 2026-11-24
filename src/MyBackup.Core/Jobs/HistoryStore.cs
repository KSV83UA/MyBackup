using System.Text.Json;
using MyBackup.Core.Config;

namespace MyBackup.Core.Jobs;

/// <summary>Журнал запусков: одна строка JSON на запуск (history.jsonl).</summary>
public sealed class HistoryStore(string path)
{
    private const int MaxLines = 2000;
    private readonly Lock _lock = new();

    private static readonly JsonSerializerOptions Options = new(ConfigLoader.JsonOptions)
    {
        WriteIndented = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
    };

    public void Append(RunReport report)
    {
        var line = JsonSerializer.Serialize(report, Options);
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllLines(path, [line]);
            TrimIfNeeded();
        }
    }

    public IReadOnlyList<RunReport> ReadLast(int count, string? jobName = null)
    {
        lock (_lock)
        {
            if (!File.Exists(path)) return [];
            var reports = new List<RunReport>();
            foreach (var line in File.ReadLines(path).Reverse())
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                RunReport? r;
                try
                {
                    r = JsonSerializer.Deserialize<RunReport>(line, Options);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (r is null || jobName is not null && !r.JobName.Equals(jobName, StringComparison.OrdinalIgnoreCase)) continue;
                reports.Add(r);
                if (reports.Count >= count) break;
            }

            return reports;
        }
    }

    private void TrimIfNeeded()
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length > MaxLines) File.WriteAllLines(path, lines[^MaxLines..]);
    }
}
