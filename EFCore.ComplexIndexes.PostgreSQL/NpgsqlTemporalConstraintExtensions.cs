using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// PostgreSQL 18 temporal-constraint API. Adds a temporal <c>UNIQUE</c> constraint
/// (<c>UNIQUE (key…, period WITHOUT OVERLAPS)</c>) as standalone DDL, alongside the entity's normal
/// EF key. The period (range) column stays a plain mapped column — it is deliberately *not* part of
/// an EF key, because EF Core forbids non-comparable range types (e.g. <c>NpgsqlRange&lt;T&gt;</c>) in
/// keys. Use a surrogate or scalar EF primary key for change tracking and this constraint for the
/// non-overlap guarantee.
/// </summary>
/// <remarks>
/// Temporal constraints over scalar columns require the <c>btree_gist</c> extension. The differ
/// injects <c>CREATE EXTENSION IF NOT EXISTS btree_gist</c> automatically; call <c>UseBtreeGist</c>
/// for explicit control or <c>SuppressTemporalExtensionAutoInjection</c> to opt out. Rendering the
/// <c>WITHOUT OVERLAPS</c> clause requires runtime wiring via <c>UseNpgsqlComplexIndexes()</c>.
/// </remarks>
public static class NpgsqlTemporalConstraintExtensions
{
    extension<TEntity>(EntityTypeBuilder<TEntity> builder) where TEntity : class
    {
        /// <summary>
        /// Adds a temporal <c>UNIQUE</c> constraint: <paramref name="keyColumns"/> (one or more scalar
        /// columns, e.g. <c>x =&gt; x.RoomId</c> or <c>x =&gt; new { x.RoomId, x.Floor }</c>) plus
        /// <paramref name="period"/> — a range column rendered last with <c>WITHOUT OVERLAPS</c>.
        /// </summary>
        public EntityTypeBuilder<TEntity> HasTemporalConstraint<TKey>(
            Expression<Func<TEntity, TKey>>    keyColumns,
            Expression<Func<TEntity, object?>> period,
            string?                            name = null
        )
        {
            var keys       = ExtractPaths(keyColumns);
            var periodPath = ComplexIndexExtensions.ExtractSinglePath(period.Body);

            if (keys.Count == 0)
                throw new ArgumentException("A temporal constraint requires at least one key column.", nameof(keyColumns));

            if (keys.Contains(periodPath))
                throw new ArgumentException(
                    $"The period column '{periodPath}' must not also appear in the key columns.",
                    nameof(period)
                );

            var definition = new TemporalConstraintDefinition
                             {
                                 KeyProperties  = keys,
                                 PeriodProperty = periodPath,
                                 Name           = name
                             };

            var existing = GetExisting(builder);
            existing.RemoveAll(d => d.KeyProperties.SequenceEqual(keys) && d.PeriodProperty == periodPath);
            existing.Add(definition);

            builder.HasAnnotation(NpgsqlTemporalAnnotations.Constraints, TemporalConstraintSerializer.Serialize(existing));
            return builder;
        }

        /// <summary>
        /// Adds a PostgreSQL 18 temporal foreign key. The scalar key columns are compared by equality,
        /// and the dependent period must be covered by matching principal periods.
        /// </summary>
        public EntityTypeBuilder<TEntity> HasTemporalForeignKey<TPrincipal>(
            Expression<Func<TEntity, object?>>    dependentKeyColumns,
            Expression<Func<TEntity, object?>>    dependentPeriod,
            Expression<Func<TPrincipal, object?>> principalKeyColumns,
            Expression<Func<TPrincipal, object?>> principalPeriod,
            string?                               name = null
        ) where TPrincipal : class
        {
            var dependentKeys      = ExtractPaths(dependentKeyColumns);
            var principalKeys      = ExtractPaths(principalKeyColumns);
            var dependentPeriodPath = ComplexIndexExtensions.ExtractSinglePath(dependentPeriod.Body);
            var principalPeriodPath = ComplexIndexExtensions.ExtractSinglePath(principalPeriod.Body);

            if (dependentKeys.Count == 0)
                throw new ArgumentException("A temporal foreign key requires at least one dependent key column.", nameof(dependentKeyColumns));

            if (principalKeys.Count == 0)
                throw new ArgumentException("A temporal foreign key requires at least one principal key column.", nameof(principalKeyColumns));

            if (dependentKeys.Count != principalKeys.Count)
                throw new ArgumentException(
                    $"Temporal foreign key key-count mismatch: dependent has {dependentKeys.Count} key column(s), principal has {principalKeys.Count}.",
                    nameof(principalKeyColumns)
                );

            if (dependentKeys.Contains(dependentPeriodPath))
                throw new ArgumentException(
                    $"The dependent period column '{dependentPeriodPath}' must not also appear in the dependent key columns.",
                    nameof(dependentPeriod)
                );

            if (principalKeys.Contains(principalPeriodPath))
                throw new ArgumentException(
                    $"The principal period column '{principalPeriodPath}' must not also appear in the principal key columns.",
                    nameof(principalPeriod)
                );

            var definition = new TemporalForeignKeyDefinition
                             {
                                 DependentKeyProperties = dependentKeys,
                                 DependentPeriodProperty = dependentPeriodPath,
                                 PrincipalEntityType     = typeof(TPrincipal).FullName ?? typeof(TPrincipal).Name,
                                 PrincipalKeyProperties  = principalKeys,
                                 PrincipalPeriodProperty = principalPeriodPath,
                                 Name                    = name
                             };

            var existing = GetExistingForeignKeys(builder);
            existing.RemoveAll(d => d.DependentKeyProperties.SequenceEqual(dependentKeys)
                                 && d.DependentPeriodProperty == dependentPeriodPath
                                 && d.PrincipalEntityType     == definition.PrincipalEntityType
                                 && d.PrincipalKeyProperties.SequenceEqual(principalKeys)
                                 && d.PrincipalPeriodProperty == principalPeriodPath);
            existing.Add(definition);

            builder.HasAnnotation(NpgsqlTemporalAnnotations.ForeignKeys, TemporalForeignKeySerializer.Serialize(existing));
            return builder;
        }

        private static List<TemporalConstraintDefinition> GetExisting(EntityTypeBuilder<TEntity> entityTypeBuilder)
        {
            var annotation = entityTypeBuilder.Metadata.FindAnnotation(NpgsqlTemporalAnnotations.Constraints);

            return annotation?.Value is string json && !string.IsNullOrEmpty(json)
                       ? TemporalConstraintSerializer.Deserialize(json)
                       : [];
        }

        private static List<TemporalForeignKeyDefinition> GetExistingForeignKeys(EntityTypeBuilder<TEntity> entityTypeBuilder)
        {
            var annotation = entityTypeBuilder.Metadata.FindAnnotation(NpgsqlTemporalAnnotations.ForeignKeys);

            return annotation?.Value is string json && !string.IsNullOrEmpty(json)
                       ? TemporalForeignKeySerializer.Deserialize(json)
                       : [];
        }
    }

    extension(ModelBuilder modelBuilder)
    {
        /// <summary>
        /// Declares the <c>btree_gist</c> extension on the model (via Npgsql's
        /// <c>HasPostgresExtension</c>) so it is created during migrations. Use this for explicit
        /// control; when present, the differ's automatic injection backs off.
        /// </summary>
        public ModelBuilder UseBtreeGist()
            => modelBuilder.HasPostgresExtension(NpgsqlTemporalAnnotations.BtreeGistExtension);

        /// <summary>
        /// Opts out of automatic <c>CREATE EXTENSION IF NOT EXISTS btree_gist</c> injection by the
        /// differ. Use when the extension is provisioned out of band.
        /// </summary>
        public ModelBuilder SuppressTemporalExtensionAutoInjection()
        {
            modelBuilder.HasAnnotation(NpgsqlTemporalAnnotations.SuppressAutoExtension, true);
            return modelBuilder;
        }
    }

    // Accepts either an anonymous-type key list (x => new { x.A, x.B }) or a single member (x => x.A).
    private static List<string> ExtractPaths<TEntity, TKey>(Expression<Func<TEntity, TKey>> expression)
    {
        var body = expression.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert } unary)
            body = unary.Operand;

        return body is NewExpression newExpression
                   ? [.. newExpression.Arguments.Select(ComplexIndexExtensions.ExtractSinglePath)]
                   : [ComplexIndexExtensions.ExtractSinglePath(body)];
    }
}