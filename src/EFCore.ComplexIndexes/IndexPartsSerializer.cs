using System.Text.Json;

namespace EFCore.ComplexIndexes;

/// <summary>
/// Serializes the ordered list of <see cref="ResolvedIndexPart"/> carried on the
/// <see cref="ComplexIndexAnnotations.IndexParts"/> annotation of a <c>CreateIndexOperation</c>.
/// </summary>
public static class IndexPartsSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public static string Serialize(IReadOnlyList<ResolvedIndexPart> parts)
        => JsonSerializer.Serialize(parts, JsonOptions);

    public static List<ResolvedIndexPart> Deserialize(string json)
        => JsonSerializer.Deserialize<List<ResolvedIndexPart>>(json, JsonOptions) ?? [];
}
