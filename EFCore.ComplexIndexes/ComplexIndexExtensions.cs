using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EFCore.ComplexIndexes;

public static class ComplexIndexExtensions
{
    // ── Single-column index on a complex type property ──

    extension<TProperty>(ComplexTypePropertyBuilder<TProperty> builder)
    {
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
    }

    // ── Multi-column composite index ──

    extension<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : class
    {
        public EntityTypeBuilder<TEntity> HasComplexCompositeIndex<TProperties>(
            Expression<Func<TEntity, TProperties>> columns,
            bool                                   isUnique  = false,
            string?                                filter    = null,
            string?                                indexName = null
        )
        {
            var paths = ExtractPropertyPaths(columns);

            if (paths.Count < 2)
                throw new ArgumentException(
                    """
                    Composite index requires at least two properties. 
                    Use HasComplexIndex on a single property instead.
                    """
                );

            var definition = new CompositeIndexDefinition
                             {
                                 PropertyPaths = paths,
                                 IsUnique      = isUnique,
                                 Filter        = filter,
                                 IndexName     = indexName
                             };

            var existing = EntityTypeBuilder<TEntity>.GetExistingCompositeDefinitions(builder);
            existing.RemoveAll(d => d.PropertyPaths.SequenceEqual(paths));
            existing.Add(definition);
            builder.HasAnnotation(ComplexIndexAnnotations.CompositeIndexes, CompositeIndexSerializer.Serialize(existing));

            return builder;
        }

        private static List<CompositeIndexDefinition> GetExistingCompositeDefinitions(EntityTypeBuilder<TEntity> entityTypeBuilder)
        {
            var annotation = entityTypeBuilder
                            .Metadata
                            .FindAnnotation(ComplexIndexAnnotations.CompositeIndexes);

            if (annotation?.Value is string json && !string.IsNullOrEmpty(json))
                return CompositeIndexSerializer.Deserialize(json);

            return [];
        }
    }

    // ── Path extraction ──

    internal static List<string> ExtractPropertyPaths<TEntity, TProperties>(Expression<Func<TEntity, TProperties>> expression)
    {
        if (expression.Body is not NewExpression newExpr)
            throw new ArgumentException(
                """
                Expression must be an anonymous type constructor 
                (e.g., x => new { x.Prop1, x.Prop2 }).
                """
            );

        var paths = new List<string>(newExpr.Arguments.Count);
        foreach (var argument in newExpr.Arguments)
            paths.Add(ExtractSinglePath(argument));

        return paths;
    }

    internal static string ExtractSinglePath(Expression expression)
    {
        if (expression is UnaryExpression { NodeType: ExpressionType.Convert } unary)
            expression = unary.Operand;

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

        return string.Join(".", segments);
    }
}