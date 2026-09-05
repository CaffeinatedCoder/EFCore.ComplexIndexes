using Microsoft.EntityFrameworkCore.Metadata;

namespace EFCore.ComplexIndexes;

/// <summary>
/// Reads the indexes this package declares back from an EF Core model — mutable or finalized —
/// so an application can check its own conventions ("every unique index on a withdrawable
/// aggregate is filtered to live rows") instead of trusting each configuration to remember them.
/// </summary>
/// <remarks>
/// The differ builds its index descriptors from <see cref="GetDeclaredComplexIndexes"/> too, so
/// this is the same reading of the model that the migration is scaffolded from, not a second
/// parser that can disagree with it.
/// </remarks>
public static class ComplexIndexModelExtensions
{
    extension(IReadOnlyEntityType entityType)
    {
        /// <summary>
        /// The complex indexes declared on this entity type itself: property-level ones on its
        /// declared properties (complex members included), then the entity-level list. Inherited
        /// declarations are reported by the type that declares them, as with EF's own
        /// <c>GetDeclaredIndexes</c>.
        /// </summary>
        public IReadOnlyList<ComplexIndexDeclaration> GetDeclaredComplexIndexes()
        {
            var result = new List<ComplexIndexDeclaration>();

            CollectPropertyLevel(entityType, entityType, pathPrefix: "", result);

            if (entityType.FindAnnotation(ComplexIndexAnnotations.CompositeIndexes)?.Value is string json
             && !string.IsNullOrEmpty(json))
            {
                foreach (var definition in CompositeIndexSerializer.Deserialize(json))
                    result.Add(new ComplexIndexDeclaration(
                                   entityType,
                                   property: null,
                                   definition.EffectiveParts,
                                   definition.IndexName,
                                   definition.IsUnique,
                                   definition.Filter,
                                   AnnotationValues.NormalizeProviderAnnotations(definition.ProviderAnnotations)));
            }

            return result;
        }

        /// <summary>
        /// The complex indexes that apply to this entity type: its own declarations plus those of
        /// its base types, base types first.
        /// </summary>
        public IReadOnlyList<ComplexIndexDeclaration> GetComplexIndexes()
        {
            var result = new List<ComplexIndexDeclaration>();

            for (var type = entityType; type is not null; type = type.BaseType)
                result.InsertRange(0, type.GetDeclaredComplexIndexes());

            return result;
        }
    }

    extension(IReadOnlyModel model)
    {
        /// <summary>Every complex index declared anywhere in the model, each reported once on its declaring type.</summary>
        public IReadOnlyList<ComplexIndexDeclaration> GetComplexIndexes()
            => [.. model.GetEntityTypes().SelectMany(entityType => entityType.GetDeclaredComplexIndexes())];

        /// <summary>
        /// Finds the complex index declared with the explicit name <paramref name="name"/>, or
        /// null. Default names are derived by the differ from the resolved column names and are not
        /// matched here — a declaration without a name has <see cref="ComplexIndexDeclaration.Name"/> null.
        /// </summary>
        /// <param name="name">The explicit index name, as given to <c>indexName:</c> or <c>HasName</c>.</param>
        public ComplexIndexDeclaration? FindComplexIndex(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return model.GetComplexIndexes().FirstOrDefault(index => index.Name == name);
        }
    }

    private static void CollectPropertyLevel(
        IReadOnlyEntityType           entityType,
        IReadOnlyTypeBase             typeBase,
        string                        pathPrefix,
        List<ComplexIndexDeclaration> result
    )
    {
        foreach (var property in typeBase.GetDeclaredProperties())
        {
            if (property.FindAnnotation(ComplexIndexAnnotations.IsIndexed)?.Value is not true)
                continue;

            result.Add(new ComplexIndexDeclaration(
                           entityType,
                           property,
                           [new IndexPartDefinition { PropertyPath = pathPrefix + property.Name }],
                           property.FindAnnotation(ComplexIndexAnnotations.IndexName)?.Value as string,
                           property.FindAnnotation(ComplexIndexAnnotations.IsUnique)?.Value is true,
                           property.FindAnnotation(ComplexIndexAnnotations.Filter)?.Value as string,
                           new Dictionary<string, object?>()));
        }

        foreach (var complexProperty in typeBase.GetDeclaredComplexProperties())
            CollectPropertyLevel(entityType, complexProperty.ComplexType, $"{pathPrefix}{complexProperty.Name}.", result);
    }
}
