using MyBackup.Core.Restic;

namespace MyBackup.Core.Jobs;

public sealed record JobStatusSnapshot(
    string JobName,
    string Trigger,
    DateTimeOffset StartedAt,
    string? UnitTitle,
    int UnitIndex,
    int UnitTotal,
    DateTimeOffset? UnitStartedAt,
    int Attempt,
    ResticStatus? Status,
    string? CancelRequestedBy);

/// <summary>Текущее состояние выполняемой задачи для /status и веб-интерфейса.</summary>
public sealed class JobStatus(string jobName, string trigger) : IJobProgress
{
    private readonly Lock _lock = new();
    private string? _unit;
    private int _index;
    private int _total;
    private DateTimeOffset? _unitStarted;
    private int _attempt;
    private ResticStatus? _status;
    private string? _cancelBy;

    public string JobName => jobName;
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public string? CancelReason
    {
        get
        {
            lock (_lock) return _cancelBy is null ? null : $"остановлено вручную ({_cancelBy})";
        }
    }

    public void OnUnitStarted(string unitTitle, int index, int total)
    {
        lock (_lock)
        {
            _unit = unitTitle;
            _index = index;
            _total = total;
            _unitStarted = DateTimeOffset.Now;
            _attempt = 0;
            _status = null;
        }
    }

    public void OnAttempt(int attempt)
    {
        lock (_lock)
        {
            _attempt = attempt;
            _status = null;
        }
    }

    public void OnStatus(ResticStatus status)
    {
        lock (_lock) _status = status;
    }

    public void RequestCancel(string by)
    {
        lock (_lock) _cancelBy ??= by;
    }

    public JobStatusSnapshot Snapshot()
    {
        lock (_lock)
            return new JobStatusSnapshot(jobName, trigger, StartedAt, _unit, _index, _total, _unitStarted, _attempt,
                _status, _cancelBy);
    }
}
