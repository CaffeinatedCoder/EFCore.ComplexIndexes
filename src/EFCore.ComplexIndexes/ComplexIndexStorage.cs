using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EFCore.ComplexIndexes;

/// <summary>
/// Shared storage helper for index definitions held in the <see cref="ComplexIndexAnnotations.CompositeIndexes"/>
/// entity annotation. Provider satellite packages call this when exposing their own index-definition APIs
/// (e.g. expression indexes) so the dedup-and-serialize logic lives in one place.
/// </summary>
internal static class ComplexIndexStorage
{
    /// <summary>
    /// Stores <paramref name="definition"/> in the entity's composite-index annotation. A definition
    /// over the same ordered parts (direction ignored) with the same filter replaces the existing
    /// one — re-declaring an index updates its direction, uniqueness, name, or provider options.
    /// A definition over the same parts with a <em>different</em> filter coexists as a separate
    /// partial index; both must then carry explicit names, because the differ's default names would
    /// collide in the database.
    /// </summary>
    public static void AddOrReplace(EntityTypeBuilder entityTypeBuilder, CompositeIndexDefinition definition)
        => AddOrReplace(entityTypeBuilder.Metadata, definition);

    /// <inheritdoc cref="AddOrReplace(EntityTypeBuilder, CompositeIndexDefinition)"/>
    public static void AddOrReplace(IMutableEntityType entityType, CompositeIndexDefinition definition)
    {
        var existing = GetExisting(entityType);
        existing.RemoveAll(d => HasSameParts(d, definition) && d.Filter == definition.Filter);

        var unnamedSibling = existing.FirstOrDefault(
            d => HasSameParts(d, definition) && (d.IndexName is null || definition.IndexName is null));

        if (unnamedSibling is not null)
            throw new ArgumentException(
                $"Two indexes over the same parts ({DescribeParts(definition)}) with different filters " +
                "must both have explicit index names — the default names would collide in the database.");

        // Reusing one explicit name for two different indexes collides just as surely as two default
        // names would. The differ catches this too, but only after the whole model is built — this
        // reports it at the offending declaration.
        if (definition.IndexName is not null && existing.Any(d => d.IndexName == definition.IndexName))
            throw new ArgumentException(
                $"The index name '{definition.IndexName}' is already used by another complex index on " +
                "this entity. Index names must be unique per table.");

        existing.Add(definition);
        Write(entityType, existing);
    }

    public static List<CompositeIndexDefinition> GetExisting(IReadOnlyEntityType entityType)
    {
        var annotation = entityType.FindAnnotation(ComplexIndexAnnotations.CompositeIndexes);

        return annotation?.Value is string json && !string.IsNullOrEmpty(json)
                   ? CompositeIndexSerializer.Deserialize(json)
                   : [];
    }

    public static void Write(IMutableEntityType entityType, IReadOnlyList<CompositeIndexDefinition> definitions)
        => entityType.SetAnnotation(ComplexIndexAnnotations.CompositeIndexes, CompositeIndexSerializer.Serialize(definitions));

    /// <summary>
    /// The filter that results from adding <paramref name="predicate"/> to <paramref name="existing"/>
    /// with AND, or null when it is already there — either as the whole filter or as the conjunct
    /// this method itself appended — so that applying an amendment twice is a no-op.
    /// </summary>
    public static string? Conjoin(string? existing, string predicate)
    {
        if (existing is null)
            return predicate;

        if (existing == predicate || existing.EndsWith($" AND ({predicate})", StringComparison.Ordinal))
            return null;

        return $"({existing}) AND ({predicate})";
    }

    // Direction is deliberately ignored: re-declaring with a different DbOrder updates the index.
    private static bool HasSameParts(CompositeIndexDefinition a, CompositeIndexDefinition b)
        => a.EffectiveParts.Select(p => (p.PropertyPath, p.Expression, p.Template))
            .SequenceEqual(b.EffectiveParts.Select(p => (p.PropertyPath, p.Expression, p.Template)));

    private static string DescribeParts(CompositeIndexDefinition definition)
        => string.Join(", ", definition.EffectiveParts.Select(p => p.PropertyPath ?? p.Expression ?? p.Template));
}
