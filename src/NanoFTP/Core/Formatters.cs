using System.Globalization;

namespace NanoFTP.Core;

public static class Formatters
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public static string FileSize(long bytes)
    {
        if (bytes < 0)
        {
            return "—";
        }

        if (bytes < 1024)
        {
            return $"{bytes.ToString("N0", French)} o";
        }

        string[] units = ["Ko", "Mo", "Go", "To"];
        var value = bytes / 1024d;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.0";
        return $"{value.ToString(format, French)} {units[unit]}";
    }

    public static string Date(DateTime? value)
    {
        if (value is null || value.Value.Year < 1900)
        {
            return "—";
        }

        return value.Value.ToString("dd MMM yyyy · HH:mm", French);
    }

    public static string Time(DateTime value)
        => value.ToString("HH:mm:ss", French);

    public static string RelativeDate(DateTime value)
    {
        var local = value.ToLocalTime();
        var now = DateTime.Now;
        var delta = now - local;

        if (delta < TimeSpan.FromMinutes(1))
        {
            return "à l'instant";
        }

        if (delta < TimeSpan.FromHours(1))
        {
            return $"il y a {(int)delta.TotalMinutes} min";
        }

        if (local.Date == now.Date)
        {
            return $"aujourd'hui à {local:HH:mm}";
        }

        if (local.Date == now.Date.AddDays(-1))
        {
            return $"hier à {local:HH:mm}";
        }

        return local.ToString("dd MMM · HH:mm", French);
    }

    public static string Count(int count, string singular, string plural)
        => count <= 1 ? $"{count} {singular}" : $"{count} {plural}";
}
