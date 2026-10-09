using System.Globalization;

namespace Kakitome.Domain.Rendering;

public static class TimeFormat
{
    /// <summary>Formats seconds as <c>HH:MM:SS</c> (hours always present, may exceed 24).</summary>
    public static string Clock(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
        {
            seconds = 0;
        }

        var total = (long)Math.Floor(seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{total / 3600:00}:{total / 60 % 60:00}:{total % 60:00}");
    }

    /// <summary>Player position, e.g. <c>00:12 / 32:12</c>; hours appear when the recording is an hour or longer.</summary>
    public static string Playback(TimeSpan position, TimeSpan duration)
    {
        var withHours = duration.TotalHours >= 1;
        string Format(TimeSpan t)
        {
            t = t < TimeSpan.Zero ? TimeSpan.Zero : t;
            return withHours
                ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
                : string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes:00}:{t.Seconds:00}");
        }

        return $"{Format(position)} / {Format(duration)}";
    }

    /// <summary><c>[HH:MM:SS]</c> timestamp link text used in transcript Markdown/TXT.</summary>
    public static string Stamp(double seconds) => $"[{Clock(seconds)}]";

    public static string DateTime(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// Scripts written without spaces between words (Japanese, Chinese, Thai…) join segments directly;
    /// others join with a single space.
    /// </summary>
    public static bool UsesSpaces(string? language) =>
        language is null
        || !(language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("th", StringComparison.OrdinalIgnoreCase));
}
