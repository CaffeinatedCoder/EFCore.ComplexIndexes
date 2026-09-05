using Microsoft.EntityFrameworkCore.Metadata;

namespace EFCore.ComplexIndexes;

/// <summary>
/// One index declared through this package, read back from the model: a property-level
/// <c>HasComplexIndex</c>, an entity-level <c>HasComplexIndex</c> or <c>HasComplexCompositeIndex</c>,
/// or a provider satellite's expression index. Obtained through
/// <see cref="ComplexIndexModelExtensions"/>; the differ reads its declarations through the same
/// code, so what an application sees here is what the migration is built from.
/// </summary>
/// <remarks>
/// This is the declaration, not the resolved index. Parts carry property paths, and
/// <see cref="Name"/> is null when the differ derives the default <c>IX_{table}_{columns}</c>
/// name from the resolved column names. Resolving columns needs the relational model and, for
/// JSON members and expression templates, the provider satellite, so it happens in the differ only.
/// </remarks>
public sealed class ComplexIndexDeclaration
{
    internal ComplexIndexDeclaration(
        IReadOnlyEntityType                  entityType,
        IReadOnlyProperty?                   property,
        IReadOnlyList<IndexPartDefinition>   parts,
        string?                              name,
        bool                                 isUnique,
        string?                              filter,
        IReadOnlyDictionary<string, object?> providerAnnotations
    )
    {
        EntityType          = entityType;
        Property            = property;
        Parts               = parts;
        Name                = name;
        IsUnique            = isUnique;
        Filter              = filter;
        ProviderAnnotations = providerAnnotations;
    }

    /// <summary>The entity type the index is declared on.</summary>
    public IReadOnlyEntityType EntityType { get; }

    /// <summary>
    /// The indexed property of a property-level declaration (<c>HasComplexIndex</c> on a
    /// <c>ComplexTypePropertyBuilder</c>); null for an entity-level declaration.
    /// </summary>
    public IReadOnlyProperty? Property { get; }

    /// <summary>
    /// The ordered parts: a dotted property path per column, or a verbatim SQL expression or a
    /// template for an expression index. A property-level declaration has exactly one column part.
    /// </summary>
    public IReadOnlyList<IndexPartDefinition> Parts { get; }

    /// <summary>The explicit index name, or null when the differ derives the default from the resolved columns.</summary>
    public string? Name { get; }

    /// <summary>Whether the index is unique.</summary>
    public bool IsUnique { get; }

    /// <summary>The SQL predicate of a filtered (partial) index, or null for a full index.</summary>
    public string? Filter { get; }

    /// <summary>
    /// The provider index options of an entity-level declaration (<c>Npgsql:IndexMethod</c>, …),
    /// with the artefacts of the JSON round trip normalized to plain values. A property-level
    /// declaration stores its options as annotations on <see cref="Property"/> under the same keys
    /// and they are not repeated here: which of a property's annotations are index options and
    /// which are column facets is something only the provider satellite's differ knows.
    /// </summary>
    public IReadOnlyDictionary<string, object?> ProviderAnnotations { get; }

    /// <summary>Whether this is a property-level declaration (see <see cref="Property"/>).</summary>
    public bool IsPropertyLevel => Property is not null;
}
