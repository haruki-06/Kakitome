using System.Text;
using Kakitome.Domain.Library;
using Kakitome.Domain.Recordings;
using Kakitome.Domain.Serialization;
using Kakitome.Tests.TestSupport;

namespace Kakitome.Tests.Domain;

public sealed class LibraryJsonTests
{
    [Fact]
    public void Metadata_round_trips()
    {
        var original = Samples.Metadata();
        var copy = LibraryJson.DeserializeMetadata(LibraryJson.Serialize(original));

        Assert.Equal(original.Id, copy.Id);
        Assert.Equal(original.Title, copy.Title);
        Assert.Equal(original.Tags, copy.Tags);
        Assert.Equal(original.RecordedAt, copy.RecordedAt);
        Assert.Equal(CaptureStatus.Completed, copy.Capture!.Status);
        Assert.Equal(AudioStreamRole.Microphone, copy.Audio.Single().Role);
    }

    [Fact]
    public void Json_is_human_readable_UTF8_with_unescaped_Japanese_and_camelCase_enums()
    {
        var json = Encoding.UTF8.GetString(LibraryJson.Serialize(Samples.Metadata()));

        Assert.Contains("\"title\": \"機械学習 第3回\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceType\": \"recording\"", json, StringComparison.Ordinal);
        Assert.Contains("\"role\": \"microphone\"", json, StringComparison.Ordinal);
        Assert.Contains("\"schema\": \"kakitome.metadata\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", json, StringComparison.Ordinal);
        Assert.EndsWith("}\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_properties_survive_a_rewrite()
    {
        var json = Encoding.UTF8.GetString(LibraryJson.Serialize(Samples.Metadata()));
        json = json.Replace("\"title\":", "\"userNote\": {\"rating\": 5},\n  \"title\":", StringComparison.Ordinal);

        var rewritten = Encoding.UTF8.GetString(LibraryJson.Serialize(LibraryJson.DeserializeMetadata(Encoding.UTF8.GetBytes(json))));

        Assert.Contains("\"userNote\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"rating\": 5", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Byte_order_mark_and_comments_from_external_editors_are_tolerated()
    {
        var bytes = LibraryJson.Serialize(Samples.Metadata());
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("// edited in Notepad\n")).Concat(bytes).ToArray();

        Assert.Equal("機械学習 第3回", LibraryJson.DeserializeMetadata(withBom).Title);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"schema\": \"kakitome.transcript\", \"id\": \"0199a0b0-0000-7000-8000-000000000001\", \"title\": \"t\", \"project\": \"p\", \"createdAt\": \"2026-09-30T00:00:00Z\", \"sourceType\": \"recording\"}")]
    [InlineData("{\"schema\": \"kakitome.metadata\", \"title\": \"missing id\"}")]
    [InlineData("null")]
    public void Invalid_documents_raise_LibraryFormatException(string json) =>
        Assert.Throws<LibraryFormatException>(() => LibraryJson.DeserializeMetadata(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Transcript_and_summary_round_trip()
    {
        var id = RecordingId.New();
        var transcript = LibraryJson.DeserializeTranscript(LibraryJson.Serialize(Samples.Transcript(id)));
        var summary = LibraryJson.DeserializeSummary(LibraryJson.Serialize(Samples.Summary(id)));

        Assert.Equal(4, transcript.Segments.Count);
        Assert.Equal("田中", transcript.Speakers[1].Label);
        Assert.Equal(id, summary.RecordingId);
        Assert.Equal("受講者", summary.ActionItems.Single().Owner);
    }

    [Fact]
    public void Recording_ids_are_uuid_v7_and_parseable()
    {
        var a = RecordingId.New();

        Assert.Equal(7, a.Value.Version);
        Assert.Equal(a, RecordingId.Parse(a.ToString(), null));
        Assert.False(RecordingId.TryParse(Guid.Empty.ToString(), null, out _));
    }
}
