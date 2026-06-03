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
    public static void AddOrReplace(EntityTypeBuilder entityTypeBuilder, CompositeIndexDefinition definition)
    {
        var existing = GetExisting(entityTypeBuilder);
        existing.RemoveAll(d => d.EffectiveParts.SequenceEqual(definition.EffectiveParts));
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
}
