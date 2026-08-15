using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// PostgreSQL EXCLUDE-constraint API. An exclusion constraint generalizes uniqueness: no two rows
/// may satisfy all the per-element comparisons at once (e.g. equal keys <em>and</em> overlapping
/// periods). Unlike PostgreSQL 18's <c>UNIQUE … WITHOUT OVERLAPS</c>, an EXCLUDE constraint accepts
/// a <c>WHERE</c> predicate — the only way to express filtered overlap protection (e.g. ignoring
/// soft-deleted rows) — and works on every supported PostgreSQL version.
/// </summary>
/// <remarks>
/// Scalar equality elements under <c>USING gist</c> require the <c>btree_gist</c> extension; the
/// differ injects <c>CREATE EXTENSION IF NOT EXISTS btree_gist</c> automatically (call
/// <c>UseBtreeGist</c> for explicit control or <c>SuppressTemporalExtensionAutoInjection</c> to opt
/// out). The constraint DDL is emitted as a raw SQL operation at design time, so — unlike
/// expression indexes — no runtime <c>UseNpgsqlComplexIndexes()</c> wiring is required.
/// The generated <c>ADD CONSTRAINT</c> is preceded by <c>DROP CONSTRAINT IF EXISTS</c>, so
/// declaring a constraint that already exists in the database under the same name (hand-written
/// DDL from an earlier migration) adopts it cleanly instead of failing with 42P07.
/// </remarks>
public static class NpgsqlExclusionConstraintExtensions
{
    extension<TEntity>(EntityTypeBuilder<TEntity> builder) where TEntity : class
    {
        /// <summary>
        /// Adds an exclusion constraint in the common scheduling shape: one or more key columns
        /// compared with <c>=</c> plus an overlap column (range, geometry, …) compared with
        /// <c>&amp;&amp;</c>, optionally restricted by <paramref name="filter"/>:
        /// <c>EXCLUDE USING gist (key WITH =, …, period WITH &amp;&amp;) WHERE (filter)</c>.
        /// </summary>
        public EntityTypeBuilder<TEntity> HasExclusionConstraint(
            Expression<Func<TEntity, object?>> equalityColumns,
            Expression<Func<TEntity, object?>> overlapsColumn,
            string?                            filter = null,
            string?                            name   = null
        )
        {
            var equalityPaths = NpgsqlTemporalConstraintExtensions.ExtractPaths(equalityColumns);
            var overlapPath   = ComplexIndexExtensions.ExtractSinglePath(overlapsColumn);

            if (equalityPaths.Count == 0)
                throw new ArgumentException("An exclusion constraint requires at least one equality column.", nameof(equalityColumns));

            if (equalityPaths.Contains(overlapPath))
                throw new ArgumentException(
                    $"The overlap column '{overlapPath}' must not also appear in the equality columns.",
                    nameof(overlapsColumn)
                );

            var parts = equalityPaths
                       .Select(p => new ExclusionPartDefinition { PropertyPath = p, Operator = "=" })
                       .ToList();
            parts.Add(new ExclusionPartDefinition { PropertyPath = overlapPath, Operator = "&&" });

            return Store(builder, new ExclusionConstraintDefinition { Parts = parts, Filter = filter, Name = name });
        }

        /// <summary>
        /// Adds an exclusion constraint from an ordered list of elements with per-element operators
        /// using a builder callback — full control over operators, access method, predicate, and
        /// deferrability.
        /// </summary>
        public EntityTypeBuilder<TEntity> HasExclusionConstraint(Action<ExclusionConstraintBuilder<TEntity>> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            var constraintBuilder = new ExclusionConstraintBuilder<TEntity>();
            configure(constraintBuilder);

            return Store(builder, constraintBuilder.Build());
        }
    }

    /// <summary>
    /// Stores <paramref name="definition"/> in the entity's exclusion-constraint annotation, using
    /// the same identity rule as <c>ComplexIndexStorage.AddOrReplace</c> does for indexes: same
    /// ordered elements (operators ignored) + same filter → replace; same elements + a
    /// <em>different</em> filter → coexist as separate partial constraints.
    /// </summary>
    /// <remarks>
    /// The filter has to be part of the identity. Filtered overlap protection is the whole reason
    /// this API exists over <c>UNIQUE … WITHOUT OVERLAPS</c>, and "no overlap among active grants"
    /// plus "no overlap among revoked grants" is one constraint per filter over the same columns —
    /// keying on the elements alone silently kept only the last declaration.
    /// Coexisting constraints must both be named: the default <c>EX_{table}_{columns}</c> name is
    /// derived from the elements alone, so the two would collide in the database.
    /// </remarks>
    private static EntityTypeBuilder<TEntity> Store<TEntity>(
        EntityTypeBuilder<TEntity>    builder,
        ExclusionConstraintDefinition definition
    ) where TEntity : class
    {
        var existing = GetExisting(builder);
        existing.RemoveAll(d => HasSameElements(d, definition) && d.Filter == definition.Filter);

        var unnamedSibling = existing.FirstOrDefault(
            d => HasSameElements(d, definition) && (d.Name is null || definition.Name is null));

        if (unnamedSibling is not null)
            throw new ArgumentException(
                $"Two exclusion constraints over the same elements ({DescribeElements(definition)}) with " +
                "different filters must both have explicit names — the default names would collide in the database.");

        // Worse here than for indexes: each ADD CONSTRAINT is preceded by DROP CONSTRAINT IF EXISTS,
        // so a reused name does not fail at apply time — the second constraint silently drops and
        // replaces the first.
        if (definition.Name is not null && existing.Any(d => d.Name == definition.Name))
            throw new ArgumentException(
                $"The exclusion constraint name '{definition.Name}' is already used by another constraint " +
                "on this entity. Constraint names must be unique per table.");

        existing.Add(definition);

        builder.HasAnnotation(NpgsqlExclusionAnnotations.Constraints, ExclusionConstraintSerializer.Serialize(existing));
        return builder;
    }

    // Operators are deliberately ignored, mirroring how index identity ignores DbOrder direction:
    // re-declaring the same elements updates the operators rather than adding a second constraint.
    private static bool HasSameElements(ExclusionConstraintDefinition a, ExclusionConstraintDefinition b)
        => a.Parts.Select(p => (p.PropertyPath, p.Expression))
            .SequenceEqual(b.Parts.Select(p => (p.PropertyPath, p.Expression)));

    private static string DescribeElements(ExclusionConstraintDefinition definition)
        => string.Join(", ", definition.Parts.Select(p => p.PropertyPath ?? p.Expression));

    private static List<ExclusionConstraintDefinition> GetExisting(EntityTypeBuilder entityTypeBuilder)
    {
        var annotation = entityTypeBuilder.Metadata.FindAnnotation(NpgsqlExclusionAnnotations.Constraints);

        return annotation?.Value is string json && !string.IsNullOrEmpty(json)
                   ? ExclusionConstraintSerializer.Deserialize(json)
                   : [];
    }
}

/// <summary>
/// Builds a PostgreSQL EXCLUDE constraint: an ordered list of elements, each a column reference or
/// a verbatim SQL expression paired with its comparison operator.
/// </summary>
public sealed class ExclusionConstraintBuilder<TEntity> where TEntity : class
{
    private readonly List<ExclusionPartDefinition> _parts = [];

    private string? _method;
    private string? _filter;
    private string? _name;
    private bool    _deferrable;
    private bool    _initiallyDeferred;

    /// <summary>Adds a column element compared with <paramref name="operator"/> (e.g. <c>=</c>, <c>&amp;&amp;</c>).
    /// The selector may reach into complex properties (e.g. <c>x => x.Slot.Period</c>).</summary>
    public ExclusionConstraintBuilder<TEntity> WithColumn(Expression<Func<TEntity, object?>> column, string @operator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@operator);
        _parts.Add(new ExclusionPartDefinition
                   {
                       PropertyPath = ComplexIndexExtensions.ExtractSinglePath(column),
                       Operator     = @operator
                   });
        return this;
    }

    /// <summary>Adds a column element compared with <c>=</c>.</summary>
    public ExclusionConstraintBuilder<TEntity> WithEquality(Expression<Func<TEntity, object?>> column)
        => WithColumn(column, "=");

    /// <summary>Adds a column element compared with <c>&amp;&amp;</c> (overlap).</summary>
    public ExclusionConstraintBuilder<TEntity> WithOverlaps(Expression<Func<TEntity, object?>> column)
        => WithColumn(column, "&&");

    /// <summary>
    /// Adds a verbatim SQL expression element (e.g. <c>lower(email)</c>) compared with
    /// <paramref name="operator"/>. The expression is emitted exactly as given — it must reference
    /// real column names.
    /// </summary>
    public ExclusionConstraintBuilder<TEntity> WithExpression(string sql, string @operator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentException.ThrowIfNullOrWhiteSpace(@operator);
        _parts.Add(new ExclusionPartDefinition { Expression = sql, Operator = @operator });
        return this;
    }

    /// <summary>Sets the index access method (default: <c>gist</c>).</summary>
    public ExclusionConstraintBuilder<TEntity> UseMethod(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        _method = method;
        return this;
    }

    /// <summary>Applies a SQL predicate, rendered as <c>WHERE (…)</c>.</summary>
    public ExclusionConstraintBuilder<TEntity> HasFilter(string filter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filter);
        _filter = filter;
        return this;
    }

    /// <summary>Sets a custom name for the constraint.</summary>
    public ExclusionConstraintBuilder<TEntity> HasName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
        return this;
    }

    /// <summary>Marks the constraint <c>DEFERRABLE</c>, optionally <c>INITIALLY DEFERRED</c>.</summary>
    public ExclusionConstraintBuilder<TEntity> IsDeferrable(bool initiallyDeferred = false)
    {
        _deferrable        = true;
        _initiallyDeferred = initiallyDeferred;
        return this;
    }

    internal ExclusionConstraintDefinition Build()
    {
        if (_parts.Count == 0)
            throw new ArgumentException(
                "An exclusion constraint requires at least one element. Call WithColumn, WithEquality, " +
                "WithOverlaps, or WithExpression at least once.");

        return new ExclusionConstraintDefinition
               {
                   Parts             = _parts,
                   Method            = _method,
                   Filter            = _filter,
                   Name              = _name,
                   Deferrable        = _deferrable,
                   InitiallyDeferred = _initiallyDeferred
               };
    }
}
