using System.Text.Json.Serialization;

namespace EFCore.ComplexIndexes;

public sealed class CompositeIndexDefinition : IEquatable<CompositeIndexDefinition>
{
    [JsonPropertyName("paths")]  public required List<string> PropertyPaths { get; init; }
    [JsonPropertyName("unique")] public          bool         IsUnique      { get; init; }
    [JsonPropertyName("filter")] public          string?      Filter        { get; init; }
    [JsonPropertyName("name")]   public          string?      IndexName     { get; init; }

    public bool Equals(CompositeIndexDefinition? other) =>
        other is not null
     && PropertyPaths.SequenceEqual(other.PropertyPaths)
     && IsUnique  == other.IsUnique
     && Filter    == other.Filter
     && IndexName == other.IndexName;

    public override bool Equals(object? obj) => Equals(obj as CompositeIndexDefinition);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var path in PropertyPaths)
            hash.Add(path);
        
        hash.Add(IsUnique);
        hash.Add(Filter);
        hash.Add(IndexName);
        return hash.ToHashCode();
    }
}