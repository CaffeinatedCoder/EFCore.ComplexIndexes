using Microsoft.EntityFrameworkCore.Metadata;

namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// One EXCLUDE constraint declared through <c>HasExclusionConstraint</c>, read back from the model.
/// Obtained through <see cref="NpgsqlExclusionModelExtensions"/>; the differ reads its
/// declarations through the same code, so what an application sees here is what the migration's
/// <c>ADD CONSTRAINT … EXCLUDE</c> is built from.
/// </summary>
/// <remarks>
/// This is the declaration, not the resolved constraint: column elements carry property paths,
/// and <see cref="Name"/> is null when the differ derives the default <c>EX_{table}_{columns}</c>
/// name from the resolved column names.
/// </remarks>
public sealed class ExclusionConstraintDeclaration
{
    internal ExclusionConstraintDeclaration(IReadOnlyEntityType entityType, ExclusionConstraintDefinition definition)
    {
        EntityType        = entityType;
        Name              = definition.Name;
        Parts             = definition.Parts;
        Method            = definition.Method ?? "gist";
        Filter            = definition.Filter;
        Deferrable        = definition.Deferrable;
        InitiallyDeferred = definition.InitiallyDeferred;
    }

    /// <summary>The entity type the constraint is declared on.</summary>
    public IReadOnlyEntityType EntityType { get; }

    /// <summary>The explicit constraint name, or null when the differ derives the default from the resolved columns.</summary>
    public string? Name { get; }

    /// <summary>The ordered elements, each a property path or a verbatim expression with its operator.</summary>
    public IReadOnlyList<ExclusionPartDefinition> Parts { get; }

    /// <summary>The index access method rendered after <c>USING</c>; <c>gist</c> unless <c>UseMethod</c> set another.</summary>
    public string Method { get; }

    /// <summary>The SQL predicate rendered as <c>WHERE (…)</c>, or null for an unfiltered constraint.</summary>
    public string? Filter { get; }

    /// <summary>Whether the constraint is <c>DEFERRABLE</c>.</summary>
    public bool Deferrable { get; }

    /// <summary>Whether a deferrable constraint is <c>INITIALLY DEFERRED</c>.</summary>
    public bool InitiallyDeferred { get; }
}
