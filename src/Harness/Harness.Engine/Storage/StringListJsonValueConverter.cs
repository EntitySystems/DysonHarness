using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DysonHarness;

/// <summary>
/// EF Core value converter: <see cref="List{T}"/> of strings ↔ JSON TEXT (SQLite).
/// Normalize on write; empty list on null/whitespace/invalid JSON read.
/// </summary>
public sealed class StringListJsonValueConverter : ValueConverter<List<string>, string>
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public StringListJsonValueConverter()
        : base(
            v => JsonSerializer.Serialize(Normalize(v), JsonOptions),
            v => Deserialize(v))
    {
    }

    /// <summary>Comparer so EF change-tracking detects list mutations.</summary>
    public static ValueComparer<List<string>> Comparer { get; } = new(
        (a, b) => ReferenceEquals(a, b)
            || (a != null && b != null && a.SequenceEqual(b)),
        v => v.Aggregate(0, (h, s) => HashCode.Combine(h, StringComparer.Ordinal.GetHashCode(s))),
        v => v.ToList());

    /// <summary>Trim, drop empties, preserve order, distinct (ordinal).</summary>
    public static List<string> Normalize(IEnumerable<string>? values)
    {
        if (values is null)
            return [];

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var trimmed = raw.Trim();
            if (!seen.Add(trimmed))
                continue;

            result.Add(trimmed);
        }

        return result;
    }

    /// <summary>null/whitespace/<c>[]</c>/bad JSON → empty list (never throws).</summary>
    public static List<string> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(json, JsonOptions);
            return list is null ? [] : Normalize(list);
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
