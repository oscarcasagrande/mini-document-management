using DocReader.Domain;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DocReader.Infrastructure.Persistence;

/// <summary>
/// Stores enums as readable upper snake case text, so the tables can be inspected and queried by
/// hand: <c>OcrRunning</c> becomes <c>OCR_RUNNING</c>.
/// </summary>
public sealed class UpperSnakeCaseEnumConverter<TEnum>()
    : ValueConverter<TEnum, string>(value => ToDatabase(value), value => FromDatabase(value))
    where TEnum : struct, Enum
{
    public static string ToDatabase(TEnum value) => EnumNaming.ToUpperSnakeCase(value);

    public static TEnum FromDatabase(string value) =>
        EnumNaming.TryParse<TEnum>(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Value {value} is not a member of {typeof(TEnum).Name}.");
}
