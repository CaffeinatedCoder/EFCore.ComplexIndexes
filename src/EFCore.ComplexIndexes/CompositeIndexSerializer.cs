using System.Text.Json;
using System.Text.Json.Serialization;

namespace EFCore.ComplexIndexes;

public static class CompositeIndexSerializer
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented          = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

    public static string Serialize(IReadOnlyList<CompositeIndexDefinition> definitions)
        => JsonSerializer.Serialize(definitions, JsonOptions);

    public static List<CompositeIndexDefinition> Deserialize(string json)
        => JsonSerializer.Deserialize<List<CompositeIndexDefinition>>(json, JsonOptions) ?? [];
}