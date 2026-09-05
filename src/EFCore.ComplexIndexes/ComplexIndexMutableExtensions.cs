using Microsoft.EntityFrameworkCore.Metadata;

namespace EFCore.ComplexIndexes;

/// <summary>
/// Adds and amends complex-index declarations on an <see cref="IMutableEntityType"/> — the surface
/// for a shared convention that installs an obligation once instead of relying on every
/// configuration to repeat it: a soft-delete helper that already installs the query filter can
/// install the live-rows index filter in the same call.
/// </summary>
/// <remarks>
/// These amend what is declared <em>at the time of the call</em>. Run them after the declarations
/// they should cover — at the end of <c>OnModelCreating</c>, after the configurations have been
/// applied — and keep a check on <see cref="ComplexIndexModelExtensions.GetComplexIndexes(IReadOnlyEntityType)"/>
/// for what a later declaration might add. They are meant for <c>OnModelCreating</c>: a model
/// finalizing convention cannot overwrite an explicitly set annotation, and the declarations live
/// in one.
/// </remarks>
public static class ComplexIndexMutableExtensions
{
    extension(IMutableEntityType entityType)
    {
        /// <summary>
        /// Adds an entity-level index declaration, with the identity and validation of the fluent
        /// API: the same ordered parts with the same filter replace an existing declaration, the
        /// same parts with a different filter coexist and must both be named, and a reused explicit
        /// name throws.
        /// </summary>
        /// <param name="definition">The declaration, as <see cref="CompositeIndexDefinition"/>.</param>
        public void AddComplexIndex(CompositeIndexDefinition definition)
        {
            ArgumentNullException.ThrowIfNull(definition);

            if (definition.EffectiveParts.Count == 0)
                throw new ArgumentException("An index needs at least one part.", nameof(definition));

            ComplexIndexStorage.AddOrReplace(entityType, definition);
        }

        /// <summary>
        /// Adds <paramref name="predicate"/> to the filter of every complex index declared on this
        /// type — property-level and entity-level alike — that <paramref name="where"/> selects, with
        /// AND: an unfiltered index gets the predicate as its filter, a filtered one gets
        /// <c>(existing) AND (predicate)</c>. Placeholders in the predicate resolve like any other
        /// filter's. Applying the same predicate again is a no-op, so the call is safe to repeat.
        /// </summary>
        /// <param name="predicate">The SQL predicate to add, e.g. <c>"{RevokedAt} IS NULL"</c>.</param>
        /// <param name="where">Selects the declarations to amend; null amends every one.</param>
        /// <returns>How many declarations were changed.</returns>
        public int AddComplexIndexFilter(string predicate, Func<ComplexIndexDeclaration, bool>? where = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(predicate);

            var amended = 0;

            // Property-level declarations hold their filter on the property.
            foreach (var declaration in entityType.GetDeclaredComplexIndexes())
            {
                if (declaration.Property is not { } property || where?.Invoke(declaration) == false)
                    continue;

                if (ComplexIndexStorage.Conjoin(declaration.Filter, predicate) is not { } filter)
                    continue;

                var mutable = property as IMutableProperty
                           ?? throw new InvalidOperationException(
                                  $"The property '{property.Name}' on '{entityType.DisplayName()}' is not mutable; " +
                                  "amend declarations on the mutable model, inside OnModelCreating.");

                mutable.SetAnnotation(ComplexIndexAnnotations.Filter, filter);
                amended++;
            }

            // Entity-level declarations are one JSON list; rewrite it once.
            var definitions  = ComplexIndexStorage.GetExisting(entityType);
            var declarations = entityType.GetDeclaredComplexIndexes().Where(d => !d.IsPropertyLevel).ToList();
            var changed      = false;

            for (var i = 0; i < definitions.Count; i++)
            {
                if (where?.Invoke(declarations[i]) == false)
                    continue;

                if (ComplexIndexStorage.Conjoin(definitions[i].Filter, predicate) is not { } filter)
                    continue;

                definitions[i] = WithFilter(definitions[i], filter);
                changed        = true;
                amended++;
            }

            if (changed)
                ComplexIndexStorage.Write(entityType, definitions);

            return amended;
        }
    }

    private static CompositeIndexDefinition WithFilter(CompositeIndexDefinition definition, string filter)
        => new()
           {
               PropertyPaths       = definition.PropertyPaths,
               Parts               = definition.Parts,
               IsUnique            = definition.IsUnique,
               Filter              = filter,
               IndexName           = definition.IndexName,
               ProviderAnnotations = definition.ProviderAnnotations
           };
}
