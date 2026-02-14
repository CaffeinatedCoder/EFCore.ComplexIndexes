using System.Text.Json.Serialization;

namespace EFCore.ComplexIndexes;

public sealed class CompositeIndexDefinition : IEquatable<CompositeIndexDefinition>
{
    [JsonPropertyName("paths")]  public required List<string>                 PropertyPaths       { get; init; }
    [JsonPropertyName("unique")] public          bool                         IsUnique            { get; init; }
    [JsonPropertyName("filter")] public          string?                      Filter              { get; init; }
    [JsonPropertyName("name")]   public          string?                      IndexName           { get; init; }
    [JsonPropertyName("props")]  public          Dictionary<string, object?>? ProviderAnnotations { get; init; }

    public bool Equals(CompositeIndexDefinition? other) =>
        other is not null
     && PropertyPaths.SequenceEqual(other.PropertyPaths)
     && IsUnique  == other.IsUnique
     && Filter    == other.Filter
     && IndexName == other.IndexName
     && ProviderAnnotationsEqual(other);

    public override bool Equals(object? obj) => Equals(obj as CompositeIndexDefinition);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var path in PropertyPaths)
            hash.Add(path);

        hash.Add(IsUnique);
        hash.Add(Filter);
        hash.Add(IndexName);

        if (ProviderAnnotations is not null)
        {
            foreach (var (key, value) in ProviderAnnotations.OrderBy(kv => kv.Key))
            {
                hash.Add(key);
                hash.Add(value);
            }
        }

        return hash.ToHashCode();
    }

    private bool ProviderAnnotationsEqual(CompositeIndexDefinition other)
    {
        var a = ProviderAnnotations       ?? [];
        var b = other.ProviderAnnotations ?? [];
        if (a.Count != b.Count) return false;
        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var otherValue)) return false;
            if (!Equals(value, otherValue)) return false;
        }
        return true;
    }
}