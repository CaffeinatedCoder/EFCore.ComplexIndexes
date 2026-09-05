using Microsoft.EntityFrameworkCore.Metadata;

namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// Amends exclusion-constraint declarations on an <see cref="IMutableEntityType"/> — the
/// counterpart of <see cref="ComplexIndexMutableExtensions"/> for the constraint side, so a shared
/// convention can install a live-rows filter on both in one place.
/// </summary>
/// <remarks>
/// Amends what is declared at the time of the call; run it after the declarations it should
/// cover, inside <c>OnModelCreating</c>.
/// </remarks>
public static class NpgsqlExclusionMutableExtensions
{
    extension(IMutableEntityType entityType)
    {
        /// <summary>
        /// Adds <paramref name="predicate"/> to the filter of every exclusion constraint declared on
        /// this type that <paramref name="where"/> selects, with AND: an unfiltered constraint gets
        /// the predicate as its filter, a filtered one gets <c>(existing) AND (predicate)</c>.
        /// Placeholders resolve like any other filter's. Applying the same predicate again is a
        /// no-op, so the call is safe to repeat.
        /// </summary>
        /// <param name="predicate">The SQL predicate to add, e.g. <c>"{RevokedAt} IS NULL"</c>.</param>
        /// <param name="where">Selects the declarations to amend; null amends every one.</param>
        /// <returns>How many declarations were changed.</returns>
        public int AddExclusionConstraintFilter(string predicate, Func<ExclusionConstraintDeclaration, bool>? where = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(predicate);

            if (entityType.FindAnnotation(NpgsqlExclusionAnnotations.Constraints)?.Value is not string json
             || string.IsNullOrEmpty(json))
                return 0;

            var definitions  = ExclusionConstraintSerializer.Deserialize(json);
            var declarations = entityType.GetDeclaredExclusionConstraints();
            var amended      = 0;

            for (var i = 0; i < definitions.Count; i++)
            {
                if (where?.Invoke(declarations[i]) == false)
                    continue;

                if (ComplexIndexStorage.Conjoin(definitions[i].Filter, predicate) is not { } filter)
                    continue;

                definitions[i] = new ExclusionConstraintDefinition
                                 {
                                     Parts             = definitions[i].Parts,
                                     Method            = definitions[i].Method,
                                     Filter            = filter,
                                     Name              = definitions[i].Name,
                                     Deferrable        = definitions[i].Deferrable,
                                     InitiallyDeferred = definitions[i].InitiallyDeferred
                                 };
                amended++;
            }

            if (amended > 0)
                entityType.SetAnnotation(NpgsqlExclusionAnnotations.Constraints, ExclusionConstraintSerializer.Serialize(definitions));

            return amended;
        }
    }
}
