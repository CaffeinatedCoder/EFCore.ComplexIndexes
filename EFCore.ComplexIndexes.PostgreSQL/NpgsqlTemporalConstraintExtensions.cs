using System.Linq.Expressions;
using EFCore.ComplexIndexes;
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

        private static List<TemporalConstraintDefinition> GetExisting(EntityTypeBuilder<TEntity> entityTypeBuilder)
        {
            var annotation = entityTypeBuilder.Metadata.FindAnnotation(NpgsqlTemporalAnnotations.Constraints);

            return annotation?.Value is string json && !string.IsNullOrEmpty(json)
                       ? TemporalConstraintSerializer.Deserialize(json)
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
        => expression.Body is NewExpression
               ? ComplexIndexExtensions.ExtractPropertyPaths(expression)
               : [ComplexIndexExtensions.ExtractSinglePath(expression.Body)];
}
