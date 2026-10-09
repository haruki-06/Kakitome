using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kakitome.Domain.Recordings;

/// <summary>
/// Stable identity of a recording. Stored in <c>metadata.json</c>; directory names may change
/// (project rename, user moves) but the id does not. Time-ordered (UUIDv7).
/// </summary>
[JsonConverter(typeof(RecordingIdJsonConverter))]
public readonly record struct RecordingId(Guid Value) : IParsable<RecordingId>
{
    public static RecordingId New() => new(Guid.CreateVersion7());

    public static RecordingId Parse(string s, IFormatProvider? provider = null) =>
        TryParse(s, provider, out var id) ? id : throw new FormatException($"Invalid recording id '{s}'.");

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out RecordingId result)
    {
        if (Guid.TryParseExact(s, "D", out var g) && g != Guid.Empty)
        {
            result = new RecordingId(g);
            return true;
        }

        result = default;
        return false;
    }

    public override string ToString() => Value.ToString("D", CultureInfo.InvariantCulture);
}

internal sealed class RecordingIdJsonConverter : JsonConverter<RecordingId>
{
    public override RecordingId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        RecordingId.TryParse(reader.GetString(), CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new JsonException("Invalid recording id.");

    public override void Write(Utf8JsonWriter writer, RecordingId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
