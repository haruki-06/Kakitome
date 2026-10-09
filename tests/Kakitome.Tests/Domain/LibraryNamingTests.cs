using Kakitome.Domain.Library;

namespace Kakitome.Tests.Domain;

public sealed class LibraryNamingTests
{
    [Theory]
    [InlineData("大学講義", "大学講義")]
    [InlineData("会議: Q3/計画?", "会議_ Q3_計画_")]
    [InlineData("a<b>c\"d|e*f\\g", "a_b_c_d_e_f_g")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("trailing dots...", "trailing dots")]
    [InlineData("tab\tand\nnewline", "tab and newline")]
    [InlineData("Mixed 日本語 and English", "Mixed 日本語 and English")]
    public void Sanitize_replaces_only_what_Windows_cannot_store(string input, string expected) =>
        Assert.Equal(expected, LibraryNaming.SanitizeSegment(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    [InlineData("???")]
    public void Sanitize_falls_back_when_nothing_usable_remains(string? input) =>
        Assert.Equal("Untitled", LibraryNaming.SanitizeSegment(input));

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul", "_nul")]
    [InlineData("COM1.txt", "_COM1.txt")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("CONSOLE", "CONSOLE")]
    public void Sanitize_escapes_reserved_device_names(string input, string expected) =>
        Assert.Equal(expected, LibraryNaming.SanitizeSegment(input));

    [Fact]
    public void Sanitize_truncates_without_splitting_surrogate_pairs()
    {
        var input = new string('あ', LibraryNaming.MaxSegmentLength - 1) + "😀😀";
        var result = LibraryNaming.SanitizeSegment(input);

        Assert.True(result.Length <= LibraryNaming.MaxSegmentLength);
        Assert.False(char.IsHighSurrogate(result[^1]));
        Assert.EndsWith("あ", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_normalizes_to_NFC()
    {
        var decomposed = "ガ"; // カ + combining dakuten
        Assert.Equal("ガ", LibraryNaming.SanitizeSegment(decomposed));
    }

    [Fact]
    public void Recording_directory_name_is_project_and_local_timestamp()
    {
        var local = new DateTimeOffset(2026, 9, 30, 14, 5, 9, TimeSpan.FromHours(9));
        Assert.Equal("大学講義_2026-09-30_14-05-09", LibraryNaming.RecordingDirectoryName("大学講義", local));
        Assert.Equal("会議_ A_2026-09-30_14-05-09", LibraryNaming.RecordingDirectoryName("会議: A", local));
        Assert.Equal("Inbox_2026-09-30_14-05-09", LibraryNaming.RecordingDirectoryName("  ", local));
    }

    [Fact]
    public void MakeUnique_appends_counter()
    {
        var taken = new HashSet<string> { "x", "x_2" };
        Assert.Equal("x_3", LibraryNaming.MakeUnique("x", taken.Contains));
        Assert.Equal("y", LibraryNaming.MakeUnique("y", taken.Contains));
    }

    [Theory]
    [InlineData("flac", null, "audio.flac")]
    [InlineData(".WAV", null, "audio.wav")]
    [InlineData("wav", "system", "audio.system.wav")]
    public void Audio_file_names(string ext, string? suffix, string expected) =>
        Assert.Equal(expected, LibraryLayout.AudioFileName(ext, suffix));

    [Theory]
    [InlineData("../wav", null)]
    [InlineData("wav", "../x")]
    [InlineData("w a v", null)]
    public void Audio_file_names_reject_unsafe_parts(string ext, string? suffix) =>
        Assert.Throws<ArgumentException>(() => LibraryLayout.AudioFileName(ext, suffix));
}
