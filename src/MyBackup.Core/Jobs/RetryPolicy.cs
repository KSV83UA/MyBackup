using MyBackup.Core.Config;

namespace MyBackup.Core.Jobs;

/// <summary>Пауза перед повтором растёт вдвое: 1, 2, 4, 8… минут, но не больше MaxDelay.</summary>
public sealed class RetryPolicy(RetryConfig config)
{
    public int MaxAttempts => Math.Max(1, config.MaxAttempts);

    public TimeSpan GetDelay(int failedAttempt)
    {
        if (failedAttempt < 1) failedAttempt = 1;
        var factor = Math.Pow(2, Math.Min(failedAttempt - 1, 20));
        var delay = TimeSpan.FromTicks((long)Math.Min(config.InitialDelay.Ticks * factor, config.MaxDelay.Ticks));
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }
}
