namespace Kakitome.Application.Diagnostics;

/// <summary>
/// Removes who the user is from log and diagnostics text: the profile, Documents and Library paths become placeholders
/// and the Windows user and computer names are replaced, so a log can be attached to a public issue as is.
/// </summary>
public static class LogRedactor
{
    private static readonly Lazy<(string Find, string Replace)[]> Rules = new(BuildRules);

    /// <summary>Extra paths to hide (e.g. a Library in a custom folder); call once at startup.</summary>
    public static void AddPath(string? path, string placeholder)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            Extra.Add((Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), placeholder));
        }
    }

    private static readonly List<(string Find, string Replace)> Extra = [];

    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var (find, replace) in Extra.ToArray().Concat(Rules.Value))
        {
            if (find.Length > 0)
            {
                text = text.Replace(find, replace, StringComparison.OrdinalIgnoreCase);
            }
        }

        return text;
    }

    private static (string, string)[] BuildRules()
    {
        // Longest first, so the Documents folder inside the profile is not cut by the profile rule.
        var rules = new List<(string, string)>
        {
            (Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)), "%DOCUMENTS%"),
            (Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), "%LOCALAPPDATA%"),
            (Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "%USERPROFILE%"),
        };
        rules = [.. rules.Where(r => r.Item1.Length > 3).OrderByDescending(r => r.Item1.Length)];
        if (Environment.UserName is { Length: >= 3 } user)
        {
            rules.Add(("\\" + user + "\\", "\\%USERNAME%\\"));
        }

        if (Environment.MachineName is { Length: >= 3 } machine)
        {
            rules.Add((machine, "%COMPUTERNAME%"));
        }

        return [.. rules];
    }
}
