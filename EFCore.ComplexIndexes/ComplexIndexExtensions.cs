using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EFCore.ComplexIndexes;

public static class ComplexIndexExtensions
{
    // ── Single-column index on a complex type property ──

    extension<TProperty>(ComplexTypePropertyBuilder<TProperty> builder)
    {
        /// <summary>
        /// Configures a single-column index on a complex type property.
        /// </summary>
        /// <typeparam name="TProperty">The type of the property.</typeparam>
        /// <param name="isUnique">Whether the index is unique.</param>
        /// <param name="filter">A SQL filter for the index.</param>
        /// <param name="indexName">The custom name of the index.</param>
        /// <returns>The same builder instance so that multiple configuration calls can be chained.</returns>
        public ComplexTypePropertyBuilder<TProperty> HasComplexIndex(
            bool    isUnique  = false,
            string? filter    = null,
            string? indexName = null
        )
        {
            builder.HasAnnotation(ComplexIndexAnnotations.IsIndexed, true);
            builder.HasAnnotation(ComplexIndexAnnotations.IsUnique,  isUnique);

            if (filter is not null)
                builder.HasAnnotation(ComplexIndexAnnotations.Filter, filter);

            if (indexName is not null)
                builder.HasAnnotation(ComplexIndexAnnotations.IndexName, indexName);

            return builder;
        }

        /// <summary>
        /// Configures a single-column index on a complex type property using a builder callback.
        /// Provider-specific options (e.g., GIN, clustered) are available as extension methods
        /// on <see cref="ComplexIndexBuilder"/> from the corresponding satellite package.
        /// </summary>
        public ComplexTypePropertyBuilder<TProperty> HasComplexIndex(Action<ComplexIndexBuilder> configure)
        {
            var indexBuilder = new ComplexIndexBuilder();
            configure(indexBuilder);

            builder.HasAnnotation(ComplexIndexAnnotations.IsIndexed, true);

            foreach (var (key, value) in indexBuilder.Annotations)
                builder.HasAnnotation(key, value);

            return builder;
        }
    }

    // ── Multi-column composite index ──

    extension<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        /// <summary>
        /// Configures a multi-column composite index for the entity type. Per-column sort direction
        /// can be declared with <see cref="DbOrder.Desc{T}"/> (e.g. <c>x => new { x.A, DbOrder.Desc(x.B) }</c>).
        /// </summary>
        public EntityTypeBuilder<TEntity> HasComplexCompositeIndex<TProperties>(
            Expression<Func<TEntity, TProperties>> columns,
            bool                                   isUnique  = false,
            string?                                filter    = null,
            string?                                indexName = null
        )
        {
            var parts = ExtractIndexParts(columns);
            EntityTypeBuilder<TEntity>.RequireComposite(parts);

            var definition = ComplexIndexExtensions.BuildCompositeDefinition(parts, isUnique, filter, indexName, providerAnnotations: null);
            EntityTypeBuilder<TEntity>.StoreOrReplace(builder, parts, definition);

            return builder;
        }

        /// <summary>
        /// Configures a multi-column composite index using a builder callback. Per-column sort direction
        /// can be declared with <see cref="DbOrder.Desc{T}"/>. Provider-specific options are available as
        /// extension methods on <see cref="ComplexIndexBuilder"/> from the corresponding satellite package.
        /// </summary>
        public EntityTypeBuilder<TEntity> HasComplexCompositeIndex<TProperties>(
            Expression<Func<TEntity, TProperties>> columns,
            Action<ComplexIndexBuilder>            configure
        )
        {
            var parts = ExtractIndexParts(columns);
            EntityTypeBuilder<TEntity>.RequireComposite(parts);

            var indexBuilder = new ComplexIndexBuilder();
            configure(indexBuilder);

            var annotations = indexBuilder.Annotations;

            var providerAnnotations = annotations
                                     .Where(kv => kv.Key != ComplexIndexAnnotations.IsUnique
                                               && kv.Key != ComplexIndexAnnotations.Filter
                                               && kv.Key != ComplexIndexAnnotations.IndexName)
                                     .ToDictionary(kv => kv.Key, kv => kv.Value);

            var definition = ComplexIndexExtensions.BuildCompositeDefinition(
                parts,
                annotations.TryGetValue(ComplexIndexAnnotations.IsUnique, out var u) && u is true,
                annotations.GetValueOrDefault(ComplexIndexAnnotations.Filter) as string,
                annotations.GetValueOrDefault(ComplexIndexAnnotations.IndexName) as string,
                providerAnnotations.Count > 0 ? providerAnnotations : null
            );

            EntityTypeBuilder<TEntity>.StoreOrReplace(builder, parts, definition);

            return builder;
        }

        private static void RequireComposite(List<IndexPartDefinition> parts)
        {
            if (parts.Count < 2)
                throw new ArgumentException(
                    """
                    Composite index requires at least two properties.
                    Use HasComplexIndex on a single property instead.
                    """
                );
        }

        private static void StoreOrReplace(
            EntityTypeBuilder<TEntity> entityTypeBuilder,
            List<IndexPartDefinition>  parts,
            CompositeIndexDefinition   definition
        )
        {
            var pathKey  = parts.Select(p => p.PropertyPath).ToList();
            var existing = EntityTypeBuilder<TEntity>.GetExistingCompositeDefinitions(entityTypeBuilder);

            // Replace any existing definition over the same column set, regardless of direction.
            existing.RemoveAll(d => d.EffectiveParts.Select(p => p.PropertyPath).SequenceEqual(pathKey));
            existing.Add(definition);

            entityTypeBuilder.HasAnnotation(ComplexIndexAnnotations.CompositeIndexes, CompositeIndexSerializer.Serialize(existing));
        }

        private static List<CompositeIndexDefinition> GetExistingCompositeDefinitions(EntityTypeBuilder<TEntity> entityTypeBuilder)
        {
            var annotation = entityTypeBuilder
                            .Metadata
                            .FindAnnotation(ComplexIndexAnnotations.CompositeIndexes);

            return annotation?.Value is string json && !string.IsNullOrEmpty(json)
                       ? CompositeIndexSerializer.Deserialize(json)
                       : [];
        }
    }

    // Stores ascending-only composite indexes in the legacy column-path form (so snapshots written
    // before direction support are unchanged); switches to the ordered Parts form only when a
    // descending column is present.
    private static CompositeIndexDefinition BuildCompositeDefinition(
        List<IndexPartDefinition>    parts,
        bool                         isUnique,
        string?                      filter,
        string?                      indexName,
        Dictionary<string, object?>? providerAnnotations
    )
    {
        var hasDirection = parts.Any(p => p.Descending);

        return new CompositeIndexDefinition
               {
                   PropertyPaths       = hasDirection ? [] : [.. parts.Select(p => p.PropertyPath!)],
                   Parts               = hasDirection ? parts : null,
                   IsUnique            = isUnique,
                   Filter              = filter,
                   IndexName           = indexName,
                   ProviderAnnotations = providerAnnotations
               };
    }

    // ── Path extraction ──

    internal static List<IndexPartDefinition> ExtractIndexParts<TEntity, TProperties>(Expression<Func<TEntity, TProperties>> expression)
    {
        if (expression.Body is not NewExpression newExpr)
            throw new ArgumentException(
                """
                Expression must be an anonymous type constructor
                (e.g., x => new { x.Prop1, x.Prop2 }).
                """
            );

        return [.. newExpr.Arguments.Select(ExtractSinglePart)];
    }

    internal static List<string> ExtractPropertyPaths<TEntity, TProperties>(Expression<Func<TEntity, TProperties>> expression)
        => [.. ExtractIndexParts(expression).Select(p => p.PropertyPath!)];

    internal static string ExtractSinglePath(Expression expression) => ExtractSinglePart(expression).PropertyPath!;

    internal static IndexPartDefinition ExtractSinglePart(Expression expression)
    {
        var descending = false;

        // Peel off Convert boxing and DbOrder.Asc/Desc(...) direction markers in any order.
        while (true)
        {
            if (expression is UnaryExpression { NodeType: ExpressionType.Convert } unary)
            {
                expression = unary.Operand;
                continue;
            }

            if (expression is MethodCallExpression { Method.DeclaringType: { } declaringType } call
             && declaringType == typeof(DbOrder))
            {
                descending = call.Method.Name == nameof(DbOrder.Desc);
                expression = call.Arguments[0];
                continue;
            }

            break;
        }

        var segments = new Stack<string>();
        while (expression is MemberExpression member)
        {
            segments.Push(member.Member.Name);
            expression = member.Expression!;
        }

        if (segments.Count == 0)
            throw new ArgumentException(
                """
                Each member must be a property access
                (e.g., x.Prop or x.Complex.Prop).
                """
            );

        return new IndexPartDefinition { PropertyPath = string.Join(".", segments), Descending = descending };
    }
}