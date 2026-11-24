using System.Globalization;
using MyBackup.Core.Config;

namespace MyBackup.Core.Jobs;

/// <summary>Ограничение скорости отправки по рабочему/нерабочему времени.</summary>
public sealed class BandwidthSchedule
{
    private readonly BandwidthConfig _config;
    private readonly TimeOnly _start;
    private readonly TimeOnly _end;
    private readonly bool _hasHours;

    public BandwidthSchedule(BandwidthConfig config)
    {
        _config = config;
        _hasHours = !string.IsNullOrWhiteSpace(config.WorkHours) && TryParseHours(config.WorkHours, out _start, out _end);
    }

    public static bool TryParseHours(string text, out TimeOnly start, out TimeOnly end)
    {
        start = end = default;
        var parts = text.Split('-', StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && TimeOnly.TryParseExact(parts[0], ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out start)
               && TimeOnly.TryParseExact(parts[1], ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out end)
               && start < end;
    }

    public bool IsWorkTime(DateTime local) =>
        _hasHours
        && _config.WorkDays.Contains(local.DayOfWeek)
        && TimeOnly.FromDateTime(local) >= _start
        && TimeOnly.FromDateTime(local) < _end;

    /// <summary>Ограничение в КиБ/с на момент времени; null — без ограничения.</summary>
    public int? LimitAt(DateTime local)
    {
        var limit = IsWorkTime(local) ? _config.WorkHoursUploadLimitKiB : _config.OffHoursUploadLimitKiB;
        return limit > 0 ? limit : null;
    }

    /// <summary>Ближайший момент смены ограничения; null — ограничение по времени не меняется.</summary>
    public DateTime? NextChange(DateTime local)
    {
        if (!_hasHours || _config.WorkHoursUploadLimitKiB == _config.OffHoursUploadLimitKiB || _config.WorkDays.Count == 0)
            return null;

        var current = IsWorkTime(local);
        for (var day = 0; day <= 8; day++)
        {
            var date = local.Date.AddDays(day);
            foreach (var t in new[] { _start, _end })
            {
                var candidate = date.Add(t.ToTimeSpan());
                if (candidate > local && IsWorkTime(candidate) != current) return candidate;
            }
        }

        return null;
    }
}
