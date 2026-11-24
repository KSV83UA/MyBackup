using System.Text;
using MyBackup.Core.Jobs;
using MyBackup.Core.Util;

namespace MyBackup.Core.Notifications;

/// <summary>Текст отчёта для Telegram (HTML) и для консоли.</summary>
public static class ReportFormatter
{
    public const int TelegramLimit = 4000;

    public static string Icon(UnitStatus s) => s switch
    {
        UnitStatus.Ok => "✅",
        UnitStatus.Warning => "⚠️",
        UnitStatus.Failed => "❌",
        _ => "⏭",
    };

    public static string ToTelegramHtml(RunReport r)
    {
        var sb = new StringBuilder();
        var started = r.Started.ToLocalTime();
        var finished = r.Finished.ToLocalTime();
        sb.Append(Icon(r.Status)).Append(" <b>").Append(Fmt.HtmlEscape(r.ServerName)).Append("</b> · ")
            .Append(Fmt.HtmlEscape(r.JobName)).Append(" · ")
            .Append(started.ToString("dd.MM HH:mm", Fmt.Ru)).Append('–').Append(finished.ToString("HH:mm", Fmt.Ru))
            .Append(" (").Append(Fmt.Duration(finished - started)).Append(')').AppendLine();

        foreach (var u in r.Units)
        {
            sb.Append(Icon(u.Status)).Append(' ').Append(Fmt.HtmlEscape(u.Title)).Append(" — ")
                .Append(Fmt.HtmlEscape(Detail(u))).AppendLine();
            foreach (var n in u.Notes.Take(3)) sb.Append("    · ").Append(Fmt.HtmlEscape(Fmt.Truncate(n, 200))).AppendLine();
        }

        foreach (var n in r.Notes.Take(5)) sb.Append("ℹ️ ").Append(Fmt.HtmlEscape(Fmt.Truncate(n, 300))).AppendLine();

        sb.Append("Передано: ").Append(Fmt.Size(r.TotalUploaded));
        if (r.TotalRetries > 0) sb.Append(" · повторов после обрывов: ").Append(r.TotalRetries);

        var text = sb.ToString();
        return text.Length <= TelegramLimit ? text : text[..TelegramLimit] + "\n…";
    }

    public static string ToPlainText(RunReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Icon(r.Status)} {r.ServerName} · {r.JobName} · {r.Started.ToLocalTime():dd.MM HH:mm}–{r.Finished.ToLocalTime():HH:mm}");
        foreach (var u in r.Units)
        {
            sb.AppendLine($"  {Icon(u.Status)} {u.Title} — {Detail(u)}");
            foreach (var n in u.Notes) sb.AppendLine($"      · {n}");
        }

        foreach (var n in r.Notes) sb.AppendLine($"  ℹ️ {n}");
        sb.Append($"  Передано: {Fmt.Size(r.TotalUploaded)}, повторов: {r.TotalRetries}");
        return sb.ToString();
    }

    /// <summary>Короткая строка «последний запуск» для /status.</summary>
    public static string OneLine(RunReport r) =>
        $"{Icon(r.Status)} {r.JobName} {r.Started.ToLocalTime().ToString("dd.MM HH:mm", Fmt.Ru)}–{r.Finished.ToLocalTime().ToString("HH:mm", Fmt.Ru)}";

    private static string Detail(UnitResult u) => u.Status switch
    {
        UnitStatus.Ok => $"{Fmt.Size(u.BytesProcessed)}, новых {Fmt.Size(u.DataAddedPacked)}",
        UnitStatus.Warning => $"{Fmt.Size(u.BytesProcessed)}, новых {Fmt.Size(u.DataAddedPacked)}; {u.Message}",
        _ => Fmt.Truncate(u.Message ?? "", 300),
    };
}
