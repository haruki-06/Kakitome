using System.Globalization;
using System.Text;

namespace Kakitome.Domain.Library;

/// <summary>
/// Windows-safe naming for Library folders. User-facing text (including Japanese) is preserved; only
/// characters and names Windows cannot store are replaced.
/// </summary>
public static class LibraryNaming
{
    /// <summary>Maximum length (UTF-16 units) of a sanitized name segment, keeping full paths well under MAX_PATH.</summary>
    public const int MaxSegmentLength = 80;

    public const string TimestampFormat = "yyyy-MM-dd_HH-mm-ss";

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>
    /// Returns a name safe to use as a single Windows path segment, or <paramref name="fallback"/> when
    /// nothing usable remains.
    /// </summary>
    public static string SanitizeSegment(string? name, string fallback = "Untitled")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);

        var input = (name ?? string.Empty).Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder(input.Length);
        foreach (var rune in input.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(IsInvalid(rune) ? "_" : rune.ToString());
            }
        }

        var result = CollapseSpaces(sb.ToString()).Trim(' ').TrimEnd('.', ' ');
        result = Truncate(result, MaxSegmentLength).TrimEnd('.', ' ');

        if (result.Length == 0 || result.All(c => c is '_' or '.' or ' '))
        {
            return fallback;
        }

        // Windows treats "CON.txt" like "CON": check the part before the first dot.
        var stem = result.Split('.', 2)[0].TrimEnd(' ');
        if (ReservedDeviceNames.Contains(stem))
        {
            result = "_" + result;
        }

        return result;
    }

    /// <summary><c>&lt;Project Name&gt;_&lt;yyyy-MM-dd_HH-mm-ss&gt;</c> using the local wall-clock time of the recording.</summary>
    public static string RecordingDirectoryName(string projectName, DateTimeOffset localTimestamp)
    {
        var project = SanitizeSegment(projectName, LibraryLayout.DefaultProjectName);
        return $"{project}_{localTimestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture)}";
    }

    /// <summary>Appends <c>_2</c>, <c>_3</c>, … until <paramref name="exists"/> returns false.</summary>
    public static string MakeUnique(string name, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        if (!exists(name))
        {
            return name;
        }

        for (var i = 2; i < 10_000; i++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{name}_{i}");
            if (!exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not find a unique name for '{name}'.");
    }

    private static bool IsInvalid(Rune rune) =>
        rune.Value < 0x20
        || rune.Value == 0x7F
        || rune.Value is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
        || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;

    private static string CollapseSpaces(string value)
    {
        var sb = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var c in value)
        {
            if (c == ' ')
            {
                if (!lastWasSpace)
                {
                    sb.Append(c);
                }

                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        return sb.ToString();
    }

    /// <summary>Truncates on a text-element boundary so surrogate pairs / combining marks are never split.</summary>
    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var enumerator = StringInfo.GetTextElementEnumerator(value);
        var end = 0;
        while (enumerator.MoveNext())
        {
            var next = enumerator.ElementIndex + ((string)enumerator.Current).Length;
            if (next > maxLength)
            {
                break;
            }

            end = next;
        }

        return value[..end];
    }
}
