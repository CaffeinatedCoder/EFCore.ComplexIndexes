using Microsoft.EntityFrameworkCore.Metadata;

namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// Reads the EXCLUDE constraints declared through <c>HasExclusionConstraint</c> back from an EF
/// Core model — mutable or finalized — the counterpart of
/// <see cref="ComplexIndexModelExtensions"/> for the constraint side, so an application can check
/// a convention that covers both ("every unique index <em>and</em> every exclusion constraint on a
/// withdrawable aggregate is filtered to live rows") in full rather than for indexes only.
/// </summary>
/// <remarks>
/// The differ builds its constraint descriptors from
/// <see cref="GetDeclaredExclusionConstraints"/> too: one reading of the model, shared.
/// </remarks>
public static class NpgsqlExclusionModelExtensions
{
    extension(IReadOnlyEntityType entityType)
    {
        /// <summary>
        /// The exclusion constraints declared on this entity type itself. Inherited declarations
        /// are reported by the type that declares them.
        /// </summary>
        public IReadOnlyList<ExclusionConstraintDeclaration> GetDeclaredExclusionConstraints()
        {
            if (entityType.FindAnnotation(NpgsqlExclusionAnnotations.Constraints)?.Value is not string json
             || string.IsNullOrEmpty(json))
                return [];

            return [.. ExclusionConstraintSerializer.Deserialize(json)
                                                    .Select(definition => new ExclusionConstraintDeclaration(entityType, definition))];
        }

        /// <summary>
        /// The exclusion constraints that apply to this entity type: its own declarations plus
        /// those of its base types, base types first.
        /// </summary>
        public IReadOnlyList<ExclusionConstraintDeclaration> GetExclusionConstraints()
        {
            var result = new List<ExclusionConstraintDeclaration>();

            for (var type = entityType; type is not null; type = type.BaseType)
                result.InsertRange(0, type.GetDeclaredExclusionConstraints());

            return result;
        }
    }

    extension(IReadOnlyModel model)
    {
        /// <summary>Every exclusion constraint declared anywhere in the model, each reported once on its declaring type.</summary>
        public IReadOnlyList<ExclusionConstraintDeclaration> GetExclusionConstraints()
            => [.. model.GetEntityTypes().SelectMany(entityType => entityType.GetDeclaredExclusionConstraints())];

        /// <summary>
        /// Finds the exclusion constraint declared with the explicit name <paramref name="name"/>,
        /// or null. Default names are derived by the differ from the resolved column names and are
        /// not matched here — a declaration without a name has
        /// <see cref="ExclusionConstraintDeclaration.Name"/> null.
        /// </summary>
        /// <param name="name">The explicit constraint name, as given to <c>name:</c> or <c>HasName</c>.</param>
        public ExclusionConstraintDeclaration? FindExclusionConstraint(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return model.GetExclusionConstraints().FirstOrDefault(constraint => constraint.Name == name);
        }
    }
}
