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
        /// Configures a single-column index declared at the entity level. Unlike the property-level
        /// <c>HasComplexIndex</c> (which holds one index per property), entity-level declarations can
        /// give the same column several differently-filtered indexes — name them explicitly. The
        /// selector may reach into complex properties (e.g. <c>x => x.Email.Value</c>) and may be
        /// wrapped in <see cref="DbOrder.Desc{T}"/>.
        /// </summary>
        public EntityTypeBuilder<TEntity> HasComplexIndex(
            Expression<Func<TEntity, object?>> property,
            bool                               isUnique  = false,
            string?                            filter    = null,
            string?                            indexName = null
        )
        {
            var part       = ExtractSinglePart(property.Body, property.Parameters[0]);
            var definition = ComplexIndexExtensions.BuildCompositeDefinition([part], isUnique, filter, indexName, providerAnnotations: null);
            ComplexIndexStorage.AddOrReplace(builder, definition);

            return builder;
        }

        /// <summary>
        /// Configures a single-column index at the entity level using a builder callback.
        /// Provider-specific options are available as extension methods on
        /// <see cref="ComplexIndexBuilder"/> from the corresponding satellite package.
        /// </summary>
        public EntityTypeBuilder<TEntity> HasComplexIndex(
            Expression<Func<TEntity, object?>> property,
            Action<ComplexIndexBuilder>        configure
        )
        {
            var part       = ExtractSinglePart(property.Body, property.Parameters[0]);
            var definition = ComplexIndexExtensions.BuildDefinitionFromBuilder([part], configure);
            ComplexIndexStorage.AddOrReplace(builder, definition);

            return builder;
        }

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
            ComplexIndexStorage.AddOrReplace(builder, definition);

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

            var definition = ComplexIndexExtensions.BuildDefinitionFromBuilder(parts, configure);
            ComplexIndexStorage.AddOrReplace(builder, definition);

            return builder;
        }

        private static void RequireComposite(List<IndexPartDefinition> parts)
        {
            if (parts.Count < 2)
                throw new ArgumentException(
                    """
                    Composite index requires at least two properties.
                    Use HasComplexIndex instead: on the complex property builder, or the
                    entity-level HasComplexIndex(x => x.Complex.Prop, ...) overload.
                    """
                );
        }
    }

    // Reads the callback's annotations apart into the core index facets and the provider options.
    private static CompositeIndexDefinition BuildDefinitionFromBuilder(
        List<IndexPartDefinition>   parts,
        Action<ComplexIndexBuilder> configure
    )
    {
        var indexBuilder = new ComplexIndexBuilder();
        configure(indexBuilder);

        var annotations = indexBuilder.Annotations;

        var providerAnnotations = annotations
                                 .Where(kv => kv.Key != ComplexIndexAnnotations.IsUnique
                                           && kv.Key != ComplexIndexAnnotations.Filter
                                           && kv.Key != ComplexIndexAnnotations.IndexName)
                                 .ToDictionary(kv => kv.Key, kv => kv.Value);

        return BuildCompositeDefinition(
            parts,
            annotations.TryGetValue(ComplexIndexAnnotations.IsUnique, out var u) && u is true,
            annotations.GetValueOrDefault(ComplexIndexAnnotations.Filter) as string,
            annotations.GetValueOrDefault(ComplexIndexAnnotations.IndexName) as string,
            providerAnnotations.Count > 0 ? providerAnnotations : null
        );
    }

    // Stores plain ascending composite indexes in the legacy column-path form (so snapshots written
    // before direction support are unchanged); switches to the ordered Parts form only when a part
    // carries sort options (descending or null ordering).
    private static CompositeIndexDefinition BuildCompositeDefinition(
        List<IndexPartDefinition>    parts,
        bool                         isUnique,
        string?                      filter,
        string?                      indexName,
        Dictionary<string, object?>? providerAnnotations
    )
    {
        var hasSortOptions = parts.Any(p => p.Descending || p.NullSort != DbNullSort.Default);

        return new CompositeIndexDefinition
               {
                   PropertyPaths       = hasSortOptions ? [] : [.. parts.Select(p => p.PropertyPath!)],
                   Parts               = hasSortOptions ? parts : null,
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

        return [.. newExpr.Arguments.Select(a => ExtractSinglePart(a, expression.Parameters[0]))];
    }

    internal static List<string> ExtractPropertyPaths<TEntity, TProperties>(Expression<Func<TEntity, TProperties>> expression)
        => [.. ExtractIndexParts(expression).Select(p => p.PropertyPath!)];

    internal static string ExtractSinglePath(LambdaExpression lambda)
        => ExtractSinglePart(lambda.Body, lambda.Parameters[0]).PropertyPath!;

    internal static string ExtractSinglePath(Expression expression, ParameterExpression root)
        => ExtractSinglePart(expression, root).PropertyPath!;

    internal static IndexPartDefinition ExtractSinglePart(Expression expression, ParameterExpression root)
    {
        var         original   = expression;
        bool?       descending = null;
        DbNullSort? nullSort   = null;

        // Peel off Convert boxing and DbOrder marker functions (Asc/Desc/NullsFirst/NullsLast)
        // in any order — they compose, e.g. DbOrder.NullsLast(DbOrder.Desc(x.B)). Markers of the
        // *same* kind do not compose: Asc(Desc(x)) is a contradiction, not a refinement, and
        // silently letting one win produces an index sorted the opposite way from what was written.
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
                switch (call.Method.Name)
                {
                    case nameof(DbOrder.Asc):
                        descending = Combine(descending, false, original, "DbOrder.Asc/DbOrder.Desc");
                        break;
                    case nameof(DbOrder.Desc):
                        descending = Combine(descending, true, original, "DbOrder.Asc/DbOrder.Desc");
                        break;
                    case nameof(DbOrder.NullsFirst):
                        nullSort = Combine(nullSort, DbNullSort.First, original, "DbOrder.NullsFirst/DbOrder.NullsLast");
                        break;
                    case nameof(DbOrder.NullsLast):
                        nullSort = Combine(nullSort, DbNullSort.Last, original, "DbOrder.NullsFirst/DbOrder.NullsLast");
                        break;
                }

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

        // The chain has to bottom out at the lambda's own parameter. A captured variable or a static
        // member produces a perfectly well-formed dotted path here — `captured.Name` — that no
        // property lookup can ever match, so without this the mistake surfaces much later as an
        // opaque "could not resolve property path" from the differ, far from the declaration.
        if (!ReferenceEquals(expression, root))
            throw new ArgumentException(
                $"The selector '{original}' does not start from the lambda parameter '{root.Name}'. "
              + $"Selectors must reference the entity directly (e.g. {root.Name} => {root.Name}.Prop "
              + $"or {root.Name} => {root.Name}.Complex.Prop); captured variables and static members "
              + "do not map to a column."
            );

        return new IndexPartDefinition
               {
                   PropertyPath = string.Join(".", segments),
                   Descending   = descending ?? false,
                   NullSort     = nullSort   ?? DbNullSort.Default
               };
    }

    // Repeating the same marker is harmless; pairing it with its opposite is a contradiction.
    private static T Combine<T>(T? existing, T value, Expression selector, string markers)
        where T : struct
        => existing is null || existing.Value.Equals(value)
               ? value
               : throw new ArgumentException(
                     $"Conflicting {markers} markers on '{selector}'. A column can carry only one.");
}