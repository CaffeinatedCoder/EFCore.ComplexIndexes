using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL;

/// <summary>
/// The typed factory for PostgreSQL declarations of <typeparamref name="TEntity"/>. Stateless:
/// every method is a pure function from typed lambdas and options to a declaration value. Other
/// packages extend it with C# 14 extension members (CodoMetis.Hierarchies' ltree index, say).
/// </summary>
/// <remarks>
/// Options are parameters, so signature help shows exactly what a method accepts, and an option the
/// method does not have is CS1739 at the call. Lambdas are translated here, at declaration.
/// </remarks>
public readonly struct PgSchema<TEntity> where TEntity : class
{
    public PgKey<TValue> Key<TValue>(Expression<Func<TEntity, TValue>> key) => new(LambdaTranslator.Key(key));

    // ── B-tree ──────────────────────────────────────────────────────────────────────────────

    public PgIndex.BTree BTree<TValue>(
        Expression<Func<TEntity, TValue>> key,
        Expression<Func<TEntity, bool>>? where = null,
        IEnumerable<Expression<Func<TEntity, object?>>>? include = null,
        int? fillFactor = null,
        bool? deduplicateItems = null,
        bool concurrently = false,
        string? name = null)
        => BTree([Key(key)], where, include, fillFactor, deduplicateItems, concurrently, name);

    public PgIndex.BTree BTree(
        ValueList<BTreeKey> keys,
        Expression<Func<TEntity, bool>>? where = null,
        IEnumerable<Expression<Func<TEntity, object?>>>? include = null,
        int? fillFactor = null,
        bool? deduplicateItems = null,
        bool concurrently = false,
        string? name = null)
        => new(keys)
        {
            Where = Filter(where), Include = Paths(include), FillFactor = fillFactor,
            DeduplicateItems = deduplicateItems, Concurrently = concurrently, Name = name,
        };

    /// <summary>A unique index. Only B-tree can be unique, so there is nothing to choose.</summary>
    public PgIndex.BTree Unique<TValue>(
        Expression<Func<TEntity, TValue>> key,
        Expression<Func<TEntity, bool>>? where = null,
        IEnumerable<Expression<Func<TEntity, object?>>>? include = null,
        bool nullsDistinct = true,
        int? fillFactor = null,
        bool concurrently = false,
        string? name = null)
        => Unique([Key(key)], where, include, nullsDistinct, fillFactor, concurrently, name);

    public PgIndex.BTree Unique(
        ValueList<BTreeKey> keys,
        Expression<Func<TEntity, bool>>? where = null,
        IEnumerable<Expression<Func<TEntity, object?>>>? include = null,
        bool nullsDistinct = true,
        int? fillFactor = null,
        bool concurrently = false,
        string? name = null)
        => BTree(keys, where, include, fillFactor, null, concurrently, name) with { Unique = new Uniqueness(nullsDistinct) };

    // ── Hash and SP-GiST: one key each ─────────────────────────────────────────────────────

    public PgIndex.Hash Hash<TValue>(
        Expression<Func<TEntity, TValue>> key,
        Expression<Func<TEntity, bool>>? where = null,
        int? fillFactor = null,
        bool concurrently = false,
        string? name = null)
        => Hash(Key(key), where, fillFactor, concurrently, name);

    public PgIndex.Hash Hash(
        HashKey key,
        Expression<Func<TEntity, bool>>? where = null,
        int? fillFactor = null,
        bool concurrently = false,
        string? name = null)
        => new(key) { Where = Filter(where), FillFactor = fillFactor, Concurrently = concurrently, Name = name };

    public PgIndex.SpGist SpGist(
        SpGistKey key,
        Expression<Func<TEntity, bool>>? where = null,
        IEnumerable<Expression<Func<TEntity, object?>>>? include = null,
        int? fillFactor = null,
        bool concurrently = false,
        string? name = null)
        => new(key) { Where = Filter(where), Include = Paths(include), FillFactor = fillFactor, Concurrently = concurrently, Name = name };

    // ── GiST, GIN, BRIN ────────────────────────────────────────────────────────────────────

    public PgIndex.Gist Gist(
        ValueList<GistKey> keys,
        Expression<Func<TEntity, bool>>? where = null,
        IEnumerable<Expression<Func<TEntity, object?>>>? include = null,
        int? fillFactor = null,
        GistBuffering? buffering = null,
        bool concurrently = false,
        string? name = null)
        => new(keys)
        {
            Where = Filter(where), Include = Paths(include), FillFactor = fillFactor,
            Buffering = buffering, Concurrently = concurrently, Name = name,
        };

    public PgIndex.Gin Gin<TValue>(
        Expression<Func<TEntity, TValue>> key,
        GinOpClass? ops = null,
        Expression<Func<TEntity, bool>>? where = null,
        bool? fastUpdate = null,
        int? pendingListLimit = null,
        bool concurrently = false,
        string? name = null)
        => Gin([ops is null ? Key(key) : Key(key).Ops(ops)], where, fastUpdate, pendingListLimit, concurrently, name);

    public PgIndex.Gin Gin(
        ValueList<GinKey> keys,
        Expression<Func<TEntity, bool>>? where = null,
        bool? fastUpdate = null,
        int? pendingListLimit = null,
        bool concurrently = false,
        string? name = null)
        => new(keys)
        {
            Where = Filter(where), FastUpdate = fastUpdate, PendingListLimit = pendingListLimit,
            Concurrently = concurrently, Name = name,
        };

    public PgIndex.Brin Brin(
        ValueList<BrinKey> keys,
        Expression<Func<TEntity, bool>>? where = null,
        int? pagesPerRange = null,
        bool? autosummarize = null,
        bool concurrently = false,
        string? name = null)
        => new(keys)
        {
            Where = Filter(where), PagesPerRange = pagesPerRange, Autosummarize = autosummarize,
            Concurrently = concurrently, Name = name,
        };

    // ── Exclusion constraints ──────────────────────────────────────────────────────────────

    /// <summary>Starts an element list with an equality element. Not yet a constraint: see <see cref="ExclusionDraft{TEntity}"/>.</summary>
    public ExclusionDraft<TEntity> Equal<TValue>(Expression<Func<TEntity, TValue>> key) => new([Element(key, ExclusionOperator.Equal)]);

    /// <summary>Starts an element list with an overlap element; typed, so only a range, multirange or array operand compiles.</summary>
    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, NpgsqlRange<T>>> range) => new([Element(range, ExclusionOperator.Overlaps)]);

    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, NpgsqlRange<T>?>> range) where T : struct => new([Element(range, ExclusionOperator.Overlaps)]);

    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, T[]>> arrayOrMultirange) => new([Element(arrayOrMultirange, ExclusionOperator.Overlaps)]);

    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, List<T>>> arrayOrMultirange) => new([Element(arrayOrMultirange, ExclusionOperator.Overlaps)]);

    /// <summary>A GiST exclusion constraint. Its first parameter only accepts an element list that holds a non-equality element.</summary>
    public PgExclusion.Gist Exclusion(
        ExclusionElements<TEntity> elements,
        Expression<Func<TEntity, bool>>? where = null,
        Deferral? deferral = null,
        string? name = null)
        => new(elements.Elements) { Where = Filter(where), Deferral = deferral ?? Deferral.NotDeferrable, Name = name };

    // ── shared ─────────────────────────────────────────────────────────────────────────────

    internal static ExclusionElement Element(LambdaExpression key, ExclusionOperator op) => new(LambdaTranslator.Key(key), op);

    private static Predicate? Filter(Expression<Func<TEntity, bool>>? where) => where is null ? null : LambdaTranslator.Predicate(where);

    private static ValueList<string> Paths(IEnumerable<Expression<Func<TEntity, object?>>>? include) =>
        include is null ? ValueList<string>.Empty : [.. include.Select(LambdaTranslator.Path)];
}

/// <summary>An exclusion element list that has no non-equality element yet. It converts to nothing and no factory accepts it.</summary>
public sealed record ExclusionDraft<TEntity>(ValueList<ExclusionElement> Elements) where TEntity : class
{
    public ExclusionDraft<TEntity> Equal<TValue>(Expression<Func<TEntity, TValue>> key) => new([.. Elements, PgSchema<TEntity>.Element(key, ExclusionOperator.Equal)]);

    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, NpgsqlRange<T>>> range) => new([.. Elements, PgSchema<TEntity>.Element(range, ExclusionOperator.Overlaps)]);

    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, NpgsqlRange<T>?>> range) where T : struct => new([.. Elements, PgSchema<TEntity>.Element(range, ExclusionOperator.Overlaps)]);

    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, T[]>> arrayOrMultirange) => new([.. Elements, PgSchema<TEntity>.Element(arrayOrMultirange, ExclusionOperator.Overlaps)]);

    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, List<T>>> arrayOrMultirange) => new([.. Elements, PgSchema<TEntity>.Element(arrayOrMultirange, ExclusionOperator.Overlaps)]);

    /// <summary>Any operator, unchecked: the name says the compiler did not look at it.</summary>
    public ExclusionElements<TEntity> WithUncheckedOperator<TValue>(Expression<Func<TEntity, TValue>> key, string @operator) => new([.. Elements, PgSchema<TEntity>.Element(key, new ExclusionOperator(@operator))]);
}

/// <summary>An exclusion element list that holds at least one non-equality element: the stage <see cref="PgSchema{TEntity}.Exclusion"/> accepts.</summary>
public sealed record ExclusionElements<TEntity>(ValueList<ExclusionElement> Elements) where TEntity : class
{
    public ExclusionElements<TEntity> Equal<TValue>(Expression<Func<TEntity, TValue>> key) => new([.. Elements, PgSchema<TEntity>.Element(key, ExclusionOperator.Equal)]);

    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, NpgsqlRange<T>>> range) => new([.. Elements, PgSchema<TEntity>.Element(range, ExclusionOperator.Overlaps)]);

    public ExclusionElements<TEntity> Overlaps<T>(Expression<Func<TEntity, NpgsqlRange<T>?>> range) where T : struct => new([.. Elements, PgSchema<TEntity>.Element(range, ExclusionOperator.Overlaps)]);

    public ExclusionElements<TEntity> WithUncheckedOperator<TValue>(Expression<Func<TEntity, TValue>> key, string @operator) => new([.. Elements, PgSchema<TEntity>.Element(key, new ExclusionOperator(@operator))]);
}

/// <summary>Where declarations meet EF's <see cref="EntityTypeBuilder{TEntity}"/>: one call applies a list of values.</summary>
public static class PostgresDeclarationExtensions
{
    extension<TEntity>(EntityTypeBuilder<TEntity> builder) where TEntity : class
    {
        /// <summary>
        /// Applies PostgreSQL declarations. The list takes any <see cref="IPostgresObject"/>, so
        /// another package's PostgreSQL declarations go in the same list; a SQL Server one does not compile.
        /// </summary>
        public EntityTypeBuilder<TEntity> DeclarePostgres(Func<PgSchema<TEntity>, IEnumerable<IPostgresObject>> declare)
        {
            builder.Metadata.Declare(declare(default));
            return builder;
        }
    }
}

/// <summary>The factory outside a configuration: for declarations kept in fields, shared, or tested on their own.</summary>
public static class PgSchema
{
    public static PgSchema<TEntity> For<TEntity>() where TEntity : class => default;
}
