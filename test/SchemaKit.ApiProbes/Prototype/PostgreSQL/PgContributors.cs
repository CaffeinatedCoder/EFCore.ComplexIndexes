using System.Globalization;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CodoMetis.EFCore.SchemaKit.PostgreSQL;

/// <summary>
/// Renders <see cref="PgIndex"/> at design time. Closed inside: every switch here is over the
/// contributor's own union, with no discard, so a seventh method is a compile error in each.
/// The DDL is the shape 5.x renders (NpgsqlComplexIndexSqlGenerator), so a swap adds no migration.
/// </summary>
public sealed class PgIndexContributor : SchemaContributor<PgIndex>
{
    public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 10);

    protected override IReadOnlyList<MigrationOperation> Create(PgIndex index, SchemaTable table, ISchemaDialect dialect)
    {
        var name = index.Name ?? $"IX_{table.Name}_{string.Join("_", index.Targets.Select(t => t.NameToken))}";
        var keys = index switch
        {
            PgIndex.BTree b  => b.Keys.Select(k => Key(k.Target, k.Collation, k.OpClass?.Name, table) + (k.Descending ? " DESC" : "") + k.Nulls?.Sql),
            PgIndex.Hash h   => [Key(h.Key.Target, h.Key.Collation, h.Key.OpClass?.Name, table)],
            PgIndex.Gist g   => g.Keys.Select(k => Key(k.Target, k.Collation, k.OpClass?.Name, table)),
            PgIndex.SpGist s => [Key(s.Key.Target, s.Key.Collation, s.Key.OpClass?.Name, table)],
            PgIndex.Gin g    => g.Keys.Select(k => Key(k.Target, k.Collation, k.OpClass?.Name, table)),
            PgIndex.Brin b   => b.Keys.Select(k => Key(k.Target, k.Collation, k.OpClass?.Name, table)),
        };
        var (include, unique, storage) = index switch
        {
            PgIndex.BTree b  => (b.Include, b.Unique, Storage(("fillfactor", b.FillFactor), ("deduplicate_items", b.DeduplicateItems))),
            PgIndex.Hash h   => (ValueList<string>.Empty, null, Storage(("fillfactor", h.FillFactor))),
            PgIndex.Gist g   => (g.Include, null, Storage(("fillfactor", g.FillFactor), ("buffering", g.Buffering?.Sql))),
            PgIndex.SpGist s => (s.Include, null, Storage(("fillfactor", s.FillFactor))),
            PgIndex.Gin g    => (ValueList<string>.Empty, null, Storage(("fastupdate", g.FastUpdate), ("gin_pending_list_limit", g.PendingListLimit))),
            PgIndex.Brin b   => (ValueList<string>.Empty, (Uniqueness?)null, Storage(("pages_per_range", b.PagesPerRange), ("autosummarize", b.Autosummarize))),
        };

        var sql = $"CREATE {(unique is null ? "" : "UNIQUE ")}INDEX {(index.Concurrently ? "CONCURRENTLY " : "")}"
                + $"{dialect.Quote(name)} ON {dialect.Quote(table.Name)}"
                + (index is PgIndex.BTree ? "" : $" USING {index.Method}")
                + $" ({string.Join(", ", keys)})"
                + (include.Count > 0 ? $" INCLUDE ({string.Join(", ", include.Select(p => table.Render(new KeyTarget.Column(p))))})" : "")
                + (unique is { NullsDistinct: false } ? " NULLS NOT DISTINCT" : "")
                + (storage.Length > 0 ? $" WITH ({storage})" : "")
                + (index.Where is { } where ? $" WHERE {table.RenderPredicate(where)}" : "")
                + ";";
        return [new SqlOperation { Sql = sql, SuppressTransaction = index.Concurrently }];
    }

    protected override IReadOnlyList<MigrationOperation> Drop(PgIndex index, SchemaTable table, ISchemaDialect dialect)
        => [new SqlOperation { Sql = $"DROP INDEX IF EXISTS {dialect.Quote(index.Name!)};" }];

    protected override IReadOnlyList<MigrationOperation>? Rename(PgIndex from, PgIndex to, SchemaTable table, ISchemaDialect dialect)
        => [new SqlOperation { Sql = $"ALTER INDEX {dialect.Quote(from.Name!)} RENAME TO {dialect.Quote(to.Name!)};" }];

    private static string Key(KeyTarget target, string? collation, string? opClass, SchemaTable table) =>
        target switch
        {
            KeyTarget.Column   => table.Render(target),
            KeyTarget.Template => $"({table.Render(target)})",
        }
        + (collation is null ? "" : $" COLLATE \"{collation}\"")
        + (opClass is null ? "" : $" {opClass}");

    private static string Storage(params (string Name, object? Value)[] parameters) =>
        string.Join(", ", parameters.Where(p => p.Value is not null).Select(p => $"{p.Name}={Format(p.Value!)}"));

    private static string Format(object value) => value switch
    {
        bool b => b ? "true" : "false",
        string s => s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };
}

/// <summary>Renders <see cref="PgExclusion"/> at design time, in 5.x's exact shape (drop-if-exists, then add).</summary>
public sealed class PgExclusionContributor : SchemaContributor<PgExclusion>
{
    public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 20);

    protected override IReadOnlyList<MigrationOperation> Create(PgExclusion constraint, SchemaTable table, ISchemaDialect dialect)
    {
        var name = constraint.Name ?? $"EX_{table.Name}_{string.Join("_", constraint.ElementList.Select(e => e.Target.NameToken))}";
        var elements = constraint.ElementList.Select(e => e.Target switch
        {
            KeyTarget.Column   => $"{table.Render(e.Target)} WITH {e.Operator.Sql}",
            KeyTarget.Template => $"({table.Render(e.Target)}) WITH {e.Operator.Sql}",
        });
        var quoted = dialect.Quote(table.Name);
        var sql = $"ALTER TABLE {quoted} DROP CONSTRAINT IF EXISTS {dialect.Quote(name)};\n"
                + $"ALTER TABLE {quoted} ADD CONSTRAINT {dialect.Quote(name)} EXCLUDE USING {constraint.Method} ({string.Join(", ", elements)})"
                + (constraint.Where is { } where ? $" WHERE ({table.RenderPredicate(where)})" : "")
                + constraint.Deferral.Sql
                + ";";
        return [new SqlOperation { Sql = sql }];
    }

    protected override IReadOnlyList<MigrationOperation> Drop(PgExclusion constraint, SchemaTable table, ISchemaDialect dialect)
        => [new SqlOperation { Sql = $"ALTER TABLE {dialect.Quote(table.Name)} DROP CONSTRAINT IF EXISTS {dialect.Quote(constraint.Name!)};" }];

    protected override IReadOnlyList<MigrationOperation>? Rename(PgExclusion from, PgExclusion to, SchemaTable table, ISchemaDialect dialect)
        => [new SqlOperation { Sql = $"ALTER TABLE {dialect.Quote(table.Name)} RENAME CONSTRAINT {dialect.Quote(from.Name!)} TO {dialect.Quote(to.Name!)};" }];
}

public sealed class PostgresDialect : ISchemaDialect
{
    public string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    public int MaxIdentifierLength => 63;
}
