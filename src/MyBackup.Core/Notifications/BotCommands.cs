using System.Text;
using MyBackup.Core.Config;
using MyBackup.Core.Jobs;
using MyBackup.Core.Util;

namespace MyBackup.Core.Notifications;

public sealed class BotContext
{
    public required string ServerName { get; init; }
    public required IReadOnlyList<JobConfig> Jobs { get; init; }
    public required Func<JobStatusSnapshot?> Current { get; init; }
    public required Func<string, DateTimeOffset?> NextRun { get; init; }
    public required Func<int, string?, IReadOnlyList<RunReport>> History { get; init; }

    /// <summary>Поставить задачу в очередь; false — уже выполняется или в очереди.</summary>
    public required Func<string, string, bool> Enqueue { get; init; }

    /// <summary>Остановить выполняемую задачу; false — ничего не выполняется.</summary>
    public Func<string, bool>? Cancel { get; init; }
}

/// <summary>Команды Telegram-бота: /status, /last, /run, /jobs, /help. Ответ — HTML.</summary>
public static class BotCommands
{
    public static (string Command, string Argument)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (!text.StartsWith('/')) return null;

        var space = text.IndexOf(' ');
        var cmd = space < 0 ? text : text[..space];
        var arg = space < 0 ? "" : text[(space + 1)..].Trim();
        var at = cmd.IndexOf('@'); // /run@MyBackupBot
        if (at > 0) cmd = cmd[..at];
        return (cmd.ToLowerInvariant(), arg);
    }

    public static string? Handle(string? text, BotContext ctx, string requestedBy)
    {
        var parsed = Parse(text);
        if (parsed is null) return null;
        var (cmd, arg) = parsed.Value;
        return cmd switch
        {
            "/start" or "/help" => Help(ctx),
            "/status" => Status(ctx),
            "/last" => Last(ctx, arg),
            "/jobs" => Jobs(ctx),
            "/run" => Run(ctx, arg, requestedBy),
            "/stop" => Stop(ctx, requestedBy),
            _ => "Неизвестная команда. /help — список команд.",
        };
    }

    private static string Help(BotContext ctx) =>
        $"""
         <b>MyBackup · {E(ctx.ServerName)}</b>
         /status — что выполняется сейчас и когда следующий запуск
         /last [задача] — отчёт о последнем запуске
         /run [задача] — запустить копирование сейчас
         /stop — остановить выполняемую задачу
         /jobs — список задач
         """;

    private static string Status(BotContext ctx)
    {
        var sb = new StringBuilder();
        var cur = ctx.Current();
        if (cur is not null)
        {
            sb.Append("⏳ <b>Выполняется:</b> ").Append(E(cur.JobName)).Append(" (").Append(E(cur.Trigger))
                .Append("), с ").Append(cur.StartedAt.ToLocalTime().ToString("HH:mm", Fmt.Ru)).AppendLine();
            if (cur.UnitTitle is not null)
            {
                sb.Append(E(cur.UnitTitle)).Append(" (").Append(cur.UnitIndex).Append(" из ").Append(cur.UnitTotal).Append(')');
                if (cur.Status is { } s)
                {
                    sb.Append(" · ").Append((s.PercentDone * 100).ToString("0", Fmt.Ru)).Append('%');
                    if (s.TotalBytes > 0) sb.Append(" · ").Append(Fmt.Size(s.BytesDone)).Append(" из ").Append(Fmt.Size(s.TotalBytes));
                    if (s.SecondsRemaining is > 0)
                        sb.Append(" · осталось ~").Append(Fmt.Duration(TimeSpan.FromSeconds(s.SecondsRemaining.Value)));
                }

                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("💤 Сейчас ничего не выполняется.");
        }

        foreach (var job in ctx.Jobs.Where(j => j.Enabled))
            sb.Append("Следующий запуск ").Append(E(job.Name)).Append(": ").Append(FormatNext(ctx.NextRun(job.Name))).AppendLine();

        if (ctx.History(1, null).FirstOrDefault() is { } last)
            sb.Append("Последний: ").Append(E(ReportFormatter.OneLine(last))).AppendLine();

        return sb.ToString().TrimEnd();
    }

    private static string Last(BotContext ctx, string jobName)
    {
        var report = ctx.History(1, string.IsNullOrWhiteSpace(jobName) ? null : jobName).FirstOrDefault();
        return report is null ? "Запусков ещё не было." : ReportFormatter.ToTelegramHtml(report);
    }

    private static string Jobs(BotContext ctx)
    {
        if (ctx.Jobs.Count == 0) return "Задач нет.";
        var sb = new StringBuilder("<b>Задачи</b>\n");
        foreach (var job in ctx.Jobs)
        {
            sb.Append(job.Enabled ? "• " : "◦ ").Append(E(job.Name)).Append(" — <code>").Append(E(job.Schedule)).Append("</code>");
            sb.Append(job.Enabled ? $", следующий: {FormatNext(ctx.NextRun(job.Name))}" : ", отключена").AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string Run(BotContext ctx, string jobName, string requestedBy)
    {
        var enabled = ctx.Jobs.Where(j => j.Enabled).ToList();
        JobConfig? job;
        if (string.IsNullOrWhiteSpace(jobName))
        {
            if (enabled.Count != 1)
                return "Укажите задачу: " + string.Join(", ", enabled.Select(j => $"/run {E(j.Name)}"));
            job = enabled[0];
        }
        else
        {
            job = ctx.Jobs.FirstOrDefault(j => j.Name.Equals(jobName, StringComparison.OrdinalIgnoreCase));
            if (job is null) return $"Задача «{E(jobName)}» не найдена. /jobs — список задач.";
        }

        return ctx.Enqueue(job.Name, $"Telegram, {requestedBy}")
            ? $"▶️ Задача {E(job.Name)} поставлена в очередь. Отчёт придёт по завершении."
            : $"Задача {E(job.Name)} уже выполняется или стоит в очереди.";
    }

    private static string Stop(BotContext ctx, string requestedBy)
    {
        var cur = ctx.Current();
        if (cur is null || ctx.Cancel is null) return "Сейчас ничего не выполняется.";
        return ctx.Cancel($"Telegram, {requestedBy}")
            ? $"⏹ Задача {E(cur.JobName)} останавливается. Отчёт придёт по завершении."
            : "Сейчас ничего не выполняется.";
    }

    private static string FormatNext(DateTimeOffset? at) =>
        at is null ? "—" : at.Value.ToLocalTime().ToString("dd.MM HH:mm", Fmt.Ru);

    private static string E(string? s) => Fmt.HtmlEscape(s);
}
