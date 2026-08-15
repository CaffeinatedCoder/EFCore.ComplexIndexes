using System.Text.Json;
using System.Text.Json.Serialization;

namespace EFCore.ComplexIndexes;

/// <summary>
/// JSON round trip for the entity-level index definitions stored under
/// <see cref="ComplexIndexAnnotations.CompositeIndexes"/>. The format is written into migration
/// snapshots, so changes to it must stay backwards-compatible with snapshots already in the wild.
/// </summary>
public static class CompositeIndexSerializer
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented          = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

    /// <summary>Serializes index definitions to the JSON stored on the entity type's annotation.</summary>
    /// <param name="definitions">The definitions to serialize.</param>
    /// <returns>A compact JSON array.</returns>
    public static string Serialize(IReadOnlyList<CompositeIndexDefinition> definitions)
        => JsonSerializer.Serialize(definitions, JsonOptions);

    /// <summary>Reads index definitions back from the annotation JSON.</summary>
    /// <param name="json">JSON previously produced by <see cref="Serialize"/>.</param>
    /// <returns>The definitions, or an empty list if the JSON represents null.</returns>
    public static List<CompositeIndexDefinition> Deserialize(string json)
        => JsonSerializer.Deserialize<List<CompositeIndexDefinition>>(json, JsonOptions) ?? [];
}