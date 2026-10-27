using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DysonHarness;

/// <summary>
/// Forces <see cref="DateTimeKind.Utc"/> on every SQLite DateTime read and write.
/// SQLite returns Unspecified, and System.Text.Json then drops the Z.
/// Stored TEXT is unchanged.
/// </summary>
public sealed class UtcDateTimeValueConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeValueConverter()
        : base(v => SpecifyUtc(v), v => SpecifyUtc(v))
    {
    }

    public static DateTime SpecifyUtc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc);
}

/// <summary>Nullable twin of <see cref="UtcDateTimeValueConverter"/>.</summary>
public sealed class NullableUtcDateTimeValueConverter : ValueConverter<DateTime?, DateTime?>
{
    public NullableUtcDateTimeValueConverter()
        : base(v => SpecifyUtc(v), v => SpecifyUtc(v))
    {
    }

    public static DateTime? SpecifyUtc(DateTime? value) =>
        value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
}
