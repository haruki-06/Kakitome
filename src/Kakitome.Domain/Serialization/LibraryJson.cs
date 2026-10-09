using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Kakitome.Domain.Library;
using Kakitome.Domain.Summaries;
using Kakitome.Domain.Transcripts;

namespace Kakitome.Domain.Serialization;

/// <summary>
/// Reads and writes canonical Library JSON. Output is indented UTF-8 (no BOM) with Japanese and other
/// non-ASCII text left unescaped so files stay readable in Notepad / VS Code.
/// </summary>
public static class LibraryJson
{
    private static readonly LibraryJsonContext Context = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    });

    public static byte[] Serialize(RecordingMetadata value) => Serialize(value, Context.RecordingMetadata);

    public static byte[] Serialize(TranscriptDocument value) => Serialize(value, Context.TranscriptDocument);

    public static byte[] Serialize(SummaryDocument value) => Serialize(value, Context.SummaryDocument);

    public static RecordingMetadata DeserializeMetadata(ReadOnlySpan<byte> utf8) =>
        Deserialize(utf8, Context.RecordingMetadata, RecordingMetadata.SchemaName, m => m.Schema);

    public static TranscriptDocument DeserializeTranscript(ReadOnlySpan<byte> utf8) =>
        Deserialize(utf8, Context.TranscriptDocument, TranscriptDocument.SchemaName, t => t.Schema);

    public static SummaryDocument DeserializeSummary(ReadOnlySpan<byte> utf8) =>
        Deserialize(utf8, Context.SummaryDocument, SummaryDocument.SchemaName, s => s.Schema);

    private static byte[] Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(value);
        var json = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        var result = new byte[json.Length + 1];
        json.CopyTo(result, 0);
        result[^1] = (byte)'\n';
        return result;
    }

    private static T Deserialize<T>(ReadOnlySpan<byte> utf8, JsonTypeInfo<T> typeInfo, string schema, Func<T, string> getSchema)
        where T : class
    {
        // Tolerate a UTF-8 BOM added by external editors.
        var bom = Encoding.UTF8.Preamble;
        if (utf8.StartsWith(bom))
        {
            utf8 = utf8[bom.Length..];
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize(utf8, typeInfo);
        }
        catch (JsonException ex)
        {
            throw new LibraryFormatException($"Invalid {schema} JSON: {ex.Message}", ex);
        }

        if (value is null)
        {
            throw new LibraryFormatException($"Empty {schema} document.");
        }

        if (!string.Equals(getSchema(value), schema, StringComparison.Ordinal))
        {
            throw new LibraryFormatException($"Expected schema '{schema}' but found '{getSchema(value)}'.");
        }

        return value;
    }
}

[JsonSerializable(typeof(RecordingMetadata))]
[JsonSerializable(typeof(TranscriptDocument))]
[JsonSerializable(typeof(SummaryDocument))]
internal sealed partial class LibraryJsonContext : JsonSerializerContext;

/// <summary>A Library file exists but cannot be understood. The file itself is never modified.</summary>
public sealed class LibraryFormatException : Exception
{
    public LibraryFormatException()
    {
    }

    public LibraryFormatException(string message)
        : base(message)
    {
    }

    public LibraryFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
