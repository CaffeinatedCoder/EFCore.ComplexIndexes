using System.Text.Json.Serialization;

namespace EFCore.ComplexIndexes;

public sealed class CompositeIndexDefinition : IEquatable<CompositeIndexDefinition>
{
    [JsonPropertyName("paths")]  public          List<string>                 PropertyPaths       { get; init; } = [];
    [JsonPropertyName("parts")]  public          List<IndexPartDefinition>?   Parts               { get; init; }
    [JsonPropertyName("unique")] public          bool                         IsUnique            { get; init; }
    [JsonPropertyName("filter")] public          string?                      Filter              { get; init; }
    [JsonPropertyName("name")]   public          string?                      IndexName           { get; init; }
    [JsonPropertyName("props")]  public          Dictionary<string, object?>? ProviderAnnotations { get; init; }

    /// <summary>
    /// The ordered parts that define this index. When <see cref="Parts"/> is set it is
    /// authoritative (supports mixed columns and SQL expressions); otherwise the legacy
    /// <see cref="PropertyPaths"/> (column-only) representation is used. Keeping both
    /// preserves deserialization of migration snapshots written before expression support.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<IndexPartDefinition> EffectiveParts =>
        Parts ?? [.. PropertyPaths.Select(p => new IndexPartDefinition { PropertyPath = p })];

    public bool Equals(CompositeIndexDefinition? other) =>
        other is not null
     && EffectiveParts.SequenceEqual(other.EffectiveParts)
     && IsUnique  == other.IsUnique
     && Filter    == other.Filter
     && IndexName == other.IndexName
     && ProviderAnnotationsEqual(other);

    public override bool Equals(object? obj) => Equals(obj as CompositeIndexDefinition);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var part in EffectiveParts)
            hash.Add(part);

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