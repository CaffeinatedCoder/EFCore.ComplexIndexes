using System.Text.Json.Serialization;

namespace EFCore.ComplexIndexes;

/// <summary>
/// One entity-level index declaration, as stored in the
/// <see cref="ComplexIndexAnnotations.CompositeIndexes"/> annotation. Covers multi-column indexes
/// and single-column ones declared through the entity-level API.
/// </summary>
/// <remarks>
/// Equality is the index's identity for deduplication: ordered parts (ignoring sort direction) plus
/// filter. Two declarations with the same parts but different filters are distinct partial indexes
/// and both survive — see <c>ComplexIndexStorage.AddOrReplace</c>.
/// </remarks>
public sealed class CompositeIndexDefinition : IEquatable<CompositeIndexDefinition>
{
    /// <summary>
    /// Legacy column-only representation: dotted property paths in index order. Still written when
    /// every column is ascending, so that existing snapshots do not churn. Prefer
    /// <see cref="EffectiveParts"/> when reading.
    /// </summary>
    [JsonPropertyName("paths")]  public          List<string>                 PropertyPaths       { get; init; } = [];

    /// <summary>
    /// Ordered parts, used when the index needs more than plain ascending columns (expressions,
    /// templates, descending columns, null ordering). Null on definitions that use the legacy
    /// <see cref="PropertyPaths"/> form.
    /// </summary>
    [JsonPropertyName("parts")]  public          List<IndexPartDefinition>?   Parts               { get; init; }

    /// <summary>Whether the index is unique.</summary>
    [JsonPropertyName("unique")] public          bool                         IsUnique            { get; init; }

    /// <summary>The SQL predicate of a filtered (partial) index, or null for a full index. Part of the index's identity.</summary>
    [JsonPropertyName("filter")] public          string?                      Filter              { get; init; }

    /// <summary>An explicit index name, or null to let the differ generate one from the resolved column names.</summary>
    [JsonPropertyName("name")]   public          string?                      IndexName           { get; init; }

    /// <summary>
    /// Provider-specific annotations (for example <c>Npgsql:IndexMethod</c>) copied onto the
    /// <c>CreateIndexOperation</c> so the provider's own SQL generator renders them.
    /// </summary>
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

    /// <summary>Compares parts, uniqueness, filter, name and provider annotations.</summary>
    /// <param name="other">The definition to compare with.</param>
    /// <returns><c>true</c> if the two describe the same index.</returns>
    public bool Equals(CompositeIndexDefinition? other) =>
        other is not null
     && EffectiveParts.SequenceEqual(other.EffectiveParts)
     && IsUnique  == other.IsUnique
     && Filter    == other.Filter
     && IndexName == other.IndexName
     && ProviderAnnotationsEqual(other);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as CompositeIndexDefinition);

    /// <inheritdoc />
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
                AnnotationValues.AddValue(ref hash, value);
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
            if (!AnnotationValues.ValuesEqual(value, otherValue)) return false;
        }
        return true;
    }
}