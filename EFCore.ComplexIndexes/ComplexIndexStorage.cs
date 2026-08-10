using Microsoft.EntityFrameworkCore;
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
    {
        var existing = GetExisting(entityTypeBuilder);
        existing.RemoveAll(d => HasSameParts(d, definition) && d.Filter == definition.Filter);

        var unnamedSibling = existing.FirstOrDefault(
            d => HasSameParts(d, definition) && (d.IndexName is null || definition.IndexName is null));

        if (unnamedSibling is not null)
            throw new ArgumentException(
                $"Two indexes over the same parts ({DescribeParts(definition)}) with different filters " +
                "must both have explicit index names — the default names would collide in the database.");

        existing.Add(definition);
        entityTypeBuilder.HasAnnotation(ComplexIndexAnnotations.CompositeIndexes, CompositeIndexSerializer.Serialize(existing));
    }

    public static List<CompositeIndexDefinition> GetExisting(EntityTypeBuilder entityTypeBuilder)
    {
        var annotation = entityTypeBuilder.Metadata.FindAnnotation(ComplexIndexAnnotations.CompositeIndexes);

        return annotation?.Value is string json && !string.IsNullOrEmpty(json)
                   ? CompositeIndexSerializer.Deserialize(json)
                   : [];
    }

    // Direction is deliberately ignored: re-declaring with a different DbOrder updates the index.
    private static bool HasSameParts(CompositeIndexDefinition a, CompositeIndexDefinition b)
        => a.EffectiveParts.Select(p => (p.PropertyPath, p.Expression, p.Template))
            .SequenceEqual(b.EffectiveParts.Select(p => (p.PropertyPath, p.Expression, p.Template)));

    private static string DescribeParts(CompositeIndexDefinition definition)
        => string.Join(", ", definition.EffectiveParts.Select(p => p.PropertyPath ?? p.Expression ?? p.Template));
}
