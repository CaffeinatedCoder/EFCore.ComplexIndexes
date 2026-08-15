using System.Collections;

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
}
