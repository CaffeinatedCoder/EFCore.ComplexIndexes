using System.Text.Json;

namespace EFCore.ComplexIndexes;

/// <summary>
/// Serializes the ordered list of <see cref="ResolvedIndexPart"/> carried on the
/// <see cref="ComplexIndexAnnotations.IndexParts"/> annotation of a <c>CreateIndexOperation</c>.
/// </summary>
public static class IndexPartsSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Serializes resolved parts for the annotation the custom SQL generator reads.</summary>
    /// <param name="parts">The ordered parts, with property paths already resolved to column names.</param>
    /// <returns>A compact JSON array.</returns>
    public static string Serialize(IReadOnlyList<ResolvedIndexPart> parts)
        => JsonSerializer.Serialize(parts, JsonOptions);

    /// <summary>Reads resolved parts back from the annotation JSON.</summary>
    /// <param name="json">JSON previously produced by <see cref="Serialize"/>.</param>
    /// <returns>The parts, or an empty list if the JSON represents null.</returns>
    public static List<ResolvedIndexPart> Deserialize(string json)
        => JsonSerializer.Deserialize<List<ResolvedIndexPart>>(json, JsonOptions) ?? [];
}
