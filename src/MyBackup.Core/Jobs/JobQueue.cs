using System.Collections.Concurrent;
using System.Threading.Channels;

namespace MyBackup.Core.Jobs;

public sealed record JobRequest(string JobName, string Trigger);

/// <summary>Очередь задач: выполняются по одной, одна и та же задача не ставится дважды.</summary>
public sealed class JobQueue
{
    private readonly Channel<JobRequest> _channel =
        Channel.CreateUnbounded<JobRequest>(new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<string, string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private JobStatus? _current;
    private CancellationTokenSource? _currentCts;

    public JobStatus? Current
    {
        get
        {
            lock (_lock) return _current;
        }
    }

    /// <summary>Задачи в очереди, которые ещё не начались.</summary>
    public IReadOnlyList<string> Waiting
    {
        get
        {
            var running = Current?.JobName;
            return _pending.Keys.Where(k => !k.Equals(running, StringComparison.OrdinalIgnoreCase)).Order().ToList();
        }
    }

    public bool TryEnqueue(string jobName, string trigger) =>
        _pending.TryAdd(jobName, trigger) && _channel.Writer.TryWrite(new JobRequest(jobName, trigger));

    public IAsyncEnumerable<JobRequest> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    /// <summary>Начало выполнения: возвращает токен, который отменяется остановкой службы или <see cref="CancelCurrent"/>.</summary>
    public CancellationToken BeginRun(JobStatus status, CancellationToken serviceToken)
    {
        lock (_lock)
        {
            _currentCts?.Dispose();
            _currentCts = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
            _current = status;
            return _currentCts.Token;
        }
    }

    public void EndRun(string jobName)
    {
        lock (_lock)
        {
            _current = null;
            _currentCts?.Dispose();
            _currentCts = null;
        }

        _pending.TryRemove(jobName, out _);
    }

    /// <summary>Убрать задачу из списка ожидающих, не выполняя (например, удалена из настроек).</summary>
    public void Complete(string jobName) => _pending.TryRemove(jobName, out _);

    /// <summary>Остановить выполняемую задачу. false — ничего не выполняется.</summary>
    public bool CancelCurrent(string requestedBy)
    {
        lock (_lock)
        {
            if (_current is null || _currentCts is null) return false;
            _current.RequestCancel(requestedBy);
            _currentCts.Cancel();
            return true;
        }
    }
}

/// <summary>Время следующего запуска каждой задачи (для /status и веб-интерфейса).</summary>
public sealed class ScheduleInfo
{
    private readonly ConcurrentDictionary<string, DateTimeOffset?> _next = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset? NextRun(string jobName) => _next.TryGetValue(jobName, out var at) ? at : null;

    public void Set(string jobName, DateTimeOffset? at) => _next[jobName] = at;

    public string? Error(string jobName) => _errors.TryGetValue(jobName, out var e) ? e : null;

    public void SetError(string jobName, string? error)
    {
        if (error is null) _errors.TryRemove(jobName, out _);
        else _errors[jobName] = error;
    }

    public void Retain(IEnumerable<string> jobNames)
    {
        var keep = new HashSet<string>(jobNames, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _next.Keys.Where(k => !keep.Contains(k))) _next.TryRemove(key, out _);
        foreach (var key in _errors.Keys.Where(k => !keep.Contains(k))) _errors.TryRemove(key, out _);
    }
}
