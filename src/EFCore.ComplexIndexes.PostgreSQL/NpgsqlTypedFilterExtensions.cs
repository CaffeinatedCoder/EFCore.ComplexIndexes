using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// Typed filter predicates: <c>x => x.RevokedAt == null</c> instead of <c>"revoked_at IS NULL"</c>.
/// The lambda is translated at declaration time into a filter template whose property paths the
/// differ resolves like any <c>{Property.Path}</c> placeholder — so <c>HasColumnName</c>, complex
/// members and <c>ToJson()</c> members are honored, and the resolved SQL is baked into the
/// migration with no runtime wiring.
/// </summary>
/// <remarks>
/// The translated subset is deliberately small and fails at the declaration: comparisons
/// (<c>== null</c> / <c>!= null</c> become <c>IS NULL</c> / <c>IS NOT NULL</c>), <c>&amp;&amp;</c>,
/// <c>||</c>, <c>!</c>, boolean properties, and on the operands the string operations and
/// constants a typed expression index accepts. Enums are refused — how one is stored depends on
/// the property's value conversion, which a filter cannot see — as are values with no portable SQL
/// spelling, such as dates; use the string overload for those.
/// </remarks>
public static class NpgsqlTypedFilterExtensions
{
    /// <summary>Applies a typed predicate as the filter of a complex-property index.</summary>
    /// <typeparam name="TEntity">The entity type the predicate is written against; give it explicitly.</typeparam>
    public static ComplexIndexBuilder HasFilter<TEntity>(this ComplexIndexBuilder builder, Expression<Func<TEntity, bool>> predicate)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return builder.HasFilter(NpgsqlLinqIndexTranslator.TranslatePredicate(predicate));
    }

    /// <summary>Applies a typed predicate as the filter of an expression index.</summary>
    /// <typeparam name="TEntity">The entity type the predicate is written against; give it explicitly.</typeparam>
    public static ExpressionIndexBuilder HasFilter<TEntity>(this ExpressionIndexBuilder builder, Expression<Func<TEntity, bool>> predicate)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return builder.HasFilter(NpgsqlLinqIndexTranslator.TranslatePredicate(predicate));
    }

    extension<TEntity>(EntityTypeBuilder<TEntity> builder) where TEntity : class
    {
        /// <summary>Configures a single-column index at the entity level with a typed filter.</summary>
        public EntityTypeBuilder<TEntity> HasComplexIndex(
            Expression<Func<TEntity, object?>> property,
            Expression<Func<TEntity, bool>>    filter,
            bool                               isUnique  = false,
            string?                            indexName = null
        )
        {
            ArgumentNullException.ThrowIfNull(filter);
            return builder.HasComplexIndex(property, isUnique, NpgsqlLinqIndexTranslator.TranslatePredicate(filter), indexName);
        }

        /// <summary>Configures a composite index with a typed filter.</summary>
        public EntityTypeBuilder<TEntity> HasComplexCompositeIndex<TProperties>(
            Expression<Func<TEntity, TProperties>> columns,
            Expression<Func<TEntity, bool>>        filter,
            bool                                   isUnique  = false,
            string?                                indexName = null
        )
        {
            ArgumentNullException.ThrowIfNull(filter);
            return builder.HasComplexCompositeIndex(columns, isUnique, NpgsqlLinqIndexTranslator.TranslatePredicate(filter), indexName);
        }

        /// <summary>Configures a typed expression index with a typed filter.</summary>
        public EntityTypeBuilder<TEntity> HasExpressionIndex<TResult>(
            Expression<Func<TEntity, TResult>> expression,
            Expression<Func<TEntity, bool>>    filter,
            bool                               isUnique  = false,
            string?                            indexName = null
        )
        {
            ArgumentNullException.ThrowIfNull(filter);
            return builder.HasExpressionIndex(expression, isUnique, NpgsqlLinqIndexTranslator.TranslatePredicate(filter), indexName);
        }

        /// <summary>Adds an exclusion constraint in the scheduling shape with a typed filter.</summary>
        public EntityTypeBuilder<TEntity> HasExclusionConstraint(
            Expression<Func<TEntity, object?>> equalityColumns,
            Expression<Func<TEntity, object?>> overlapsColumn,
            Expression<Func<TEntity, bool>>    filter,
            string?                            name = null
        )
        {
            ArgumentNullException.ThrowIfNull(filter);
            return builder.HasExclusionConstraint(equalityColumns, overlapsColumn, NpgsqlLinqIndexTranslator.TranslatePredicate(filter), name);
        }
    }

    extension(IMutableEntityType entityType)
    {
        /// <summary>
        /// Typed form of <see cref="ComplexIndexMutableExtensions.AddComplexIndexFilter"/>: ANDs the
        /// translated predicate onto the filter of every selected complex index, idempotently.
        /// </summary>
        /// <typeparam name="TEntity">The entity type the predicate is written against; give it explicitly.</typeparam>
        public int AddComplexIndexFilter<TEntity>(Expression<Func<TEntity, bool>> predicate, Func<ComplexIndexDeclaration, bool>? where = null)
            where TEntity : class
        {
            ArgumentNullException.ThrowIfNull(predicate);
            return entityType.AddComplexIndexFilter(NpgsqlLinqIndexTranslator.TranslatePredicate(predicate), where);
        }

        /// <summary>
        /// Typed form of <see cref="NpgsqlExclusionMutableExtensions.AddExclusionConstraintFilter"/>:
        /// ANDs the translated predicate onto the filter of every selected exclusion constraint, idempotently.
        /// </summary>
        /// <typeparam name="TEntity">The entity type the predicate is written against; give it explicitly.</typeparam>
        public int AddExclusionConstraintFilter<TEntity>(Expression<Func<TEntity, bool>> predicate, Func<ExclusionConstraintDeclaration, bool>? where = null)
            where TEntity : class
        {
            ArgumentNullException.ThrowIfNull(predicate);
            return entityType.AddExclusionConstraintFilter(NpgsqlLinqIndexTranslator.TranslatePredicate(predicate), where);
        }
    }
}
