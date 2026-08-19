namespace BilibiliDownloader.WinForms.Presentation;

using System.Globalization;

internal static class Formatters
{
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    public static string Duration(TimeSpan value) => value.TotalHours >= 1
        ? value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
        : value.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
}
