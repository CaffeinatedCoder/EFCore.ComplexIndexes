namespace EFCore.ComplexIndexes;

/// <summary>
/// The annotation keys this package writes onto the EF Core model. Index declarations are stored as
/// annotations rather than in a side table so they survive into the migration snapshot and are
/// visible to the design-time differ.
/// </summary>
public static class ComplexIndexAnnotations
{
    /// <summary>Set on a complex type property to mark it as indexed. Property-level (single-column) declarations only.</summary>
    public const string IsIndexed        = "CustomIndex:IsIndexed";

    /// <summary>Whether the property-level index is unique.</summary>
    public const string IsUnique         = "CustomIndex:IsUnique";

    /// <summary>The SQL predicate of a filtered (partial) property-level index.</summary>
    public const string Filter           = "CustomIndex:Filter";

    /// <summary>An explicit name for the property-level index, overriding the generated default.</summary>
    public const string IndexName        = "CustomIndex:Name";

    /// <summary>
    /// Set on an entity type, holding every entity-level index declaration as a JSON array of
    /// <see cref="CompositeIndexDefinition"/> (see <see cref="CompositeIndexSerializer"/>). Both
    /// multi-column indexes and single-column ones declared through the entity-level API live here.
    /// </summary>
    public const string CompositeIndexes = "CustomIndex:CompositeIndexes";

    /// <summary>
    /// Stamped onto a <c>CreateIndexOperation</c> by the differ to carry the ordered
    /// list of index parts (columns and/or raw SQL expressions) as JSON. Provider SQL
    /// generators read this to render expression indexes.
    /// </summary>
    public const string IndexParts = "CustomIndex:IndexParts";
}