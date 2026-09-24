using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocReader.Api.Http;

/// <summary>
/// Writes every instant in UTC with a trailing <c>Z</c>, as the PRD requires, instead of the
/// <c>+00:00</c> offset that the round trip format produces. Reading stays permissive: any ISO 8601
/// value a client sends is accepted and normalized to UTC.
/// </summary>
public sealed class UtcDateTimeOffsetJsonConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset().ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
}
