using System.Text;
using Kakitome.Application.Asr;
using Kakitome.Application.Decision;
using Kakitome.Domain.Transcripts;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Asr;

/// <summary>User glossaries (ADR-030): format, corrections, prompt hints and storage in the Library.</summary>
public sealed class GlossaryTests
{
    [Fact]
    public void Parses_terms_corrections_and_comments()
    {
        var g = Glossary.Parse("""
            # 憲法の講義
            堀木訴訟
            罪刑法定主義
              堀木訴訟
            新居の自由 -> 信教の自由
            葬儀権 → 争議権
            法律訴訟=>堀木訴訟
            """);

        Assert.Equal(["堀木訴訟", "罪刑法定主義", "信教の自由", "争議権"], g.Terms);
        Assert.Equal(
            [new GlossaryReplacement("新居の自由", "信教の自由"), new GlossaryReplacement("葬儀権", "争議権"), new GlossaryReplacement("法律訴訟", "堀木訴訟")],
            g.Replacements);
        Assert.Empty(g.Issues);
    }

    [Theory]
    [InlineData("a -> b", "tooShort")]          // one character would rewrite everything
    [InlineData(" -> 信教の自由", "emptySide")]
    [InlineData("新居の自由 -> ", "emptySide")]
    [InlineData("同じ -> 同じ", "duplicate")]
    public void Unsafe_or_broken_lines_are_reported_not_used(string line, string reason)
    {
        var g = Glossary.Parse(line);

        Assert.Empty(g.Replacements);
        Assert.Equal(reason, Assert.Single(g.Issues).Reason);
        Assert.Equal(1, g.Issues[0].Line);
    }

    [Fact]
    public void Overlong_terms_are_skipped()
    {
        var g = Glossary.Parse(new string('長', Glossary.MaxTermLength + 1) + "\n短い語");

        Assert.Equal(["短い語"], g.Terms);
        Assert.Equal("tooLong", Assert.Single(g.Issues).Reason);
    }

    [Fact]
    public void Corrections_apply_longest_first_in_one_pass()
    {
        var g = Glossary.Parse("""
            法律訴訟 -> 堀木訴訟
            堀木訴訟の判決 -> 堀木訴訟判決
            新居 -> 信教
            """);

        // The longest matching correction wins, and every position is rewritten at most once.
        Assert.Equal("堀木訴訟判決と信教の自由", g.Apply("堀木訴訟の判決と新居の自由"));
        Assert.Equal("堀木訴訟について", g.Apply("法律訴訟について"));
    }

    [Fact]
    public void Latin_corrections_match_whole_words_only()
    {
        var g = Glossary.Parse("cube cuddle -> kubectl\nsequel -> SQL");

        Assert.Equal("run kubectl and SQL, not sequels", g.Apply("run cube cuddle and sequel, not sequels"));
    }

    [Fact]
    public void Project_glossary_wins_over_the_shared_one()
    {
        var shared = Glossary.Parse("共通語\n誤り -> 共通の正解");
        var project = Glossary.Parse("専門語\n誤り -> 専門の正解");

        var merged = Glossary.Merge(project, shared);

        Assert.Equal(["専門語", "専門の正解", "共通語", "共通の正解"], merged.Terms);
        Assert.Equal("専門の正解", merged.Apply("誤り"));
    }

    [Fact]
    public void The_prompt_is_the_hints_and_never_the_recognized_text()
    {
        var builder = new AsrPromptBuilder(Glossary.Parse("堀木訴訟\n朝日訴訟"), "ja");

        Assert.Equal("堀木訴訟、朝日訴訟。", builder.Next(null));
        Assert.Equal("堀木訴訟、朝日訴訟。", builder.Next("黒玉:思ったより多い! 黒玉:戻そう")); // a mistake is not carried on
    }

    [Fact]
    public void Without_a_glossary_Japanese_gets_the_punctuated_opening_every_time()
    {
        var builder = new AsrPromptBuilder(Glossary.Empty, "ja");

        Assert.Equal(AsrPromptBuilder.JapaneseStyleSeed, builder.Next(null));
        Assert.Equal(AsrPromptBuilder.JapaneseStyleSeed, builder.Next("前の文。"));
        Assert.Null(new AsrPromptBuilder(Glossary.Empty, "en").Next("previous text"));
    }

    [Fact]
    public void A_large_glossary_rotates_and_keeps_the_current_topic_first()
    {
        var terms = Enumerable.Range(0, 60).Select(i => $"用語{i:D2}番").ToList(); // 6 chars each: ~15 fit
        var builder = new AsrPromptBuilder(Glossary.Parse(string.Join('\n', terms)), "ja");

        var first = builder.Next(null)!.TrimEnd('。').Split('、');
        var second = builder.Next(null)!.TrimEnd('。').Split('、');
        Assert.InRange(first.Length, 10, 20);
        Assert.Empty(first.Intersect(second));              // the next chunk offers other terms
        Assert.Equal(terms[0], first[0]);

        builder.Observe("今日は用語42番の話です");          // recognized → current topic
        Assert.Equal("用語42番", builder.Next(null)!.Split('、')[0]);

        var seen = new HashSet<string>();
        for (var i = 0; i < 10; i++)
        {
            seen.UnionWith(builder.Next(null)!.TrimEnd('。').Split('、'));
        }

        Assert.Equal(terms.Count, seen.Count);              // every term is offered over a long recording
    }

    [Fact]
    public void Terms_related_to_the_recent_text_are_offered_first()
    {
        var filler = Enumerable.Range(0, 60).Select(i => $"用語{i:D2}番");
        var builder = new AsrPromptBuilder(Glossary.Parse(string.Join('\n', filler.Append("家永教科書訴訟").Append("らい予防法"))), "ja");

        var prompt = builder.Next("次は教科書検定の問題で、高校の日本史の教科書が不合格になったという話です");

        Assert.StartsWith("家永教科書訴訟、", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("らい予防法", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Decoder_loops_are_collapsed_by_cleanup()
    {
        var engine = new RuleBasedDecisionEngine();
        const string text = "主権国家というのは誰が入って誰が入って誰が入って入れないのか";

        var cleaned = CleanupJobHandler.Apply(text, engine.FindEditCandidates(text, "ja").Where(c => c.Risk == EditRisk.Low));

        Assert.Equal("主権国家というのは誰が入って入れないのか", cleaned);
    }

    [Fact]
    public async Task Import_checks_the_file_and_keeps_the_previous_glossary()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var service = new GlossaryService(f.Store);
        var scope = new GlossaryScope("憲法");
        var source = Path.Combine(f.Root, "dict.txt");

        await File.WriteAllTextAsync(source, "堀木訴訟\n新居の自由 -> 信教の自由\n", Encoding.UTF8, TestContext.Current.CancellationToken);
        var first = await service.ImportAsync(scope, source, TestContext.Current.CancellationToken);
        Assert.Null(first.PreservedPreviousFile);

        await File.WriteAllBytesAsync(source, Encoding.GetEncoding(932).GetBytes("朝日訴訟\r\n"), TestContext.Current.CancellationToken); // Shift_JIS
        var second = await service.ImportAsync(scope, source, TestContext.Current.CancellationToken);

        Assert.NotNull(second.PreservedPreviousFile);
        Assert.True(File.Exists(Path.Combine(f.LibraryRoot, "Projects", "憲法", second.PreservedPreviousFile)));
        Assert.Equal(["朝日訴訟"], (await service.LoadAsync(scope, TestContext.Current.CancellationToken)).Terms);
        Assert.Equal(new GlossaryInfo(scope, service.FullPathFor(scope), true, 1, 0), await service.GetInfoAsync(scope, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(new byte[] { 0x23, 0x20, 0x6D, 0x65, 0x6D, 0x6F, 0x0A }, "empty")] // only a comment
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 }, "notText")]   // a ZIP
    public async Task Import_rejects_files_that_are_not_glossaries(byte[] content, string reason)
    {
        await using var f = await LibraryFixture.CreateAsync();
        var service = new GlossaryService(f.Store);
        var source = Path.Combine(f.Root, "x.txt");
        await File.WriteAllBytesAsync(source, content, TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(GlossaryScope.Shared, source, TestContext.Current.CancellationToken));

        Assert.Equal(reason, ex.Message);
        Assert.False(File.Exists(service.FullPathFor(GlossaryScope.Shared)));
    }

    [Fact]
    public async Task Project_and_shared_glossaries_are_combined_and_removal_keeps_the_file()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var service = new GlossaryService(f.Store);
        Directory.CreateDirectory(Path.Combine(f.LibraryRoot, "Projects", "憲法"));
        await File.WriteAllTextAsync(Path.Combine(f.LibraryRoot, "Projects", "glossary.txt"), "共通語\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(f.LibraryRoot, "Projects", "憲法", "glossary.txt"), "堀木訴訟\n", TestContext.Current.CancellationToken);

        Assert.Equal(["堀木訴訟", "共通語"], (await service.LoadForProjectAsync("憲法", TestContext.Current.CancellationToken)).Terms);
        Assert.Equal(["共通語"], (await service.LoadForProjectAsync("Inbox", TestContext.Current.CancellationToken)).Terms);

        var kept = Assert.IsType<string>(service.Remove(new GlossaryScope("憲法")));

        Assert.StartsWith("glossary.removed-", kept, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(f.LibraryRoot, "Projects", "憲法", kept)));
        Assert.Equal(["共通語"], (await service.LoadForProjectAsync("憲法", TestContext.Current.CancellationToken)).Terms);
    }

    [Fact]
    public async Task A_new_file_explains_the_format()
    {
        await using var f = await LibraryFixture.CreateAsync();
        var service = new GlossaryService(f.Store);

        var path = await service.EnsureFileAsync(GlossaryScope.Shared, japanese: true, TestContext.Current.CancellationToken);

        Assert.Contains("誤 -> 正", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.True((await service.LoadAsync(GlossaryScope.Shared, TestContext.Current.CancellationToken)).IsEmpty); // comments only
    }
}
