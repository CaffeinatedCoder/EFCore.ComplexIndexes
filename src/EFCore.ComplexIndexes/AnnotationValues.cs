using System.Collections;
using System.Text.Json;

namespace EFCore.ComplexIndexes;

/// <summary>
/// Equality and hashing for provider annotation values.
/// </summary>
/// <remarks>
/// Index option values are often arrays — operator classes, included columns — and
/// <see cref="object.Equals(object?, object?)"/> compares arrays by reference, so structurally
/// identical values produced by two model builds never match. Every place that compares annotation
/// values must go through here, or a no-op diff turns into phantom drop/create churn.
/// </remarks>
internal static class AnnotationValues
{
    public static bool ValuesEqual(object? a, object? b)
    {
        // Strings are IEnumerable but must compare as scalars, not as char sequences.
        if (a is string || b is string)
            return Equals(a, b);

        if (a is IEnumerable left && b is IEnumerable right)
            return left.Cast<object?>().SequenceEqual(right.Cast<object?>());

        return Equals(a, b);
    }

    public static void AddValue(ref HashCode hash, object? value)
    {
        if (value is not string && value is IEnumerable sequence)
            foreach (var item in sequence)
                hash.Add(item);
        else
            hash.Add(value);
    }

    /// <summary>
    /// Replaces the <see cref="JsonElement"/>s a deserialized definition carries with plain values,
    /// so provider annotations compare and render the same whether they came from the code model
    /// or from the snapshot.
    /// </summary>
    public static Dictionary<string, object?> NormalizeProviderAnnotations(Dictionary<string, object?>? annotations)
    {
        if (annotations is null) return [];

        var result = new Dictionary<string, object?>(annotations.Count);

        foreach (var (key, value) in annotations)
        {
            result[key] = value is JsonElement je
                              ? NormalizeJsonElement(je)
                              : value;
        }

        return result;
    }

    private static object? NormalizeJsonElement(JsonElement je)
    {
        return je.ValueKind switch
               {
                   JsonValueKind.String => je.GetString(),
                   JsonValueKind.True   => true,
                   JsonValueKind.False  => false,
                   JsonValueKind.Number => NormalizeNumber(je),
                   JsonValueKind.Null   => null,
                   JsonValueKind.Array => je.EnumerateArray()
                                            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
                                            .ToArray(),
                   _ => je.ToString()
               };
    }

    // int first: provider generators read their numeric index options with `as int?` (e.g. SQL
    // Server's FILLFACTOR), which returns null for a boxed long or double — the option would be
    // silently dropped. (A ternary here would also coerce every integral to double.)
    private static object NormalizeNumber(JsonElement je)
    {
        if (je.TryGetInt32(out var i)) return i;
        if (je.TryGetInt64(out var l)) return l;
        return je.GetDouble();
    }
}
