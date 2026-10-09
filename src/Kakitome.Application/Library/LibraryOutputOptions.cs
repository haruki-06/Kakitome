namespace Kakitome.Application.Library;

/// <summary>
/// Which human-readable formats are written for transcripts and summaries. JSON is always written: it is the
/// structured source that lets the Library be re-indexed and migrated without the database (ADR-018).
/// </summary>
public sealed class LibraryOutputOptions
{
    public bool WriteMarkdown { get; set; } = true;

    /// <summary>Timestamp-free reading versions (<c>transcript.txt</c>, <c>summary.txt</c>); on by default.</summary>
    public bool WriteText { get; set; } = true;
}

/// <summary>What to do when a file Kakitome wants to write was changed outside the app.</summary>
public enum ConflictPolicy
{
    /// <summary>Write nothing and report the conflict (default; the UI offers a reconcile choice).</summary>
    Fail,

    /// <summary>
    /// The user chose to replace: the externally changed file is kept under a <c>.conflict-&lt;time&gt;</c> name
    /// first, so nothing is lost.
    /// </summary>
    PreserveAndOverwrite,
}

/// <summary>Result of a save that may be blocked by external edits.</summary>
public sealed record SaveResult(bool Saved, IReadOnlyList<string> ConflictingFiles, IReadOnlyList<string> PreservedFiles)
{
    public static SaveResult Blocked(IReadOnlyList<string> conflicts) => new(false, conflicts, []);
}
