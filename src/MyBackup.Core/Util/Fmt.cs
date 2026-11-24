using System.Globalization;

namespace MyBackup.Core.Util;

/// <summary>Форматирование размеров и длительностей для отчётов.</summary>
public static class Fmt
{
    public static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly string[] Units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];

    public static string Size(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        if (unit == 0) return $"{bytes} Б";
        var format = value >= 100 ? "0" : "0.#";
        return value.ToString(format, Ru) + " " + Units[unit];
    }

    public static string Duration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds} с";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} мин";
        return $"{(int)d.TotalHours} ч {d.Minutes:00} мин";
    }

    public static string HtmlEscape(string? text) =>
        (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static string Truncate(string? text, int max)
    {
        text ??= "";
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
