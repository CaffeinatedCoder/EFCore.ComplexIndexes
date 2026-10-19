namespace CodoMetis.EFCore.SchemaKit.PostgreSQL;

/// <summary>A resolved <see cref="PgIndex"/>: identifiers and SQL, compared by value. <see cref="Concurrently"/> never causes a rebuild.</summary>
public sealed record PgIndexDescriptor(
    TableRef Table,
    Identifier Name,
    SqlFragment Using,
    bool Unique,
    bool NullsNotDistinct,
    ValueList<SqlFragment> Keys,
    ValueList<SqlFragment> Include,
    ValueList<SqlFragment> Storage,
    SqlFragment? Where,
    Operational<bool> Concurrently) : ISchemaDescriptor
{
    public ISchemaDescriptor WithName(Identifier name) => this with { Name = name };
}

/// <summary>
/// Diffs and renders <see cref="PgIndex"/>. Closed inside: every switch is over the contributor's own
/// union, with no discard, so a seventh method is a compile error in each. The DDL is the shape 5.x
/// renders (NpgsqlComplexIndexSqlGenerator), so a swap adds no migration.
/// </summary>
public sealed class PgIndexContributor : SchemaContributor<PgIndex, PgIndexDescriptor>
{
    public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 10);

    protected override PgIndexDescriptor Resolve(PgIndex index, SchemaResolver r)
    {
        var sql = r.Sql;
        var keys = index switch
        {
            PgIndex.BTree b  => b.Keys.Select(k => BTreeKey(k, r)),
            PgIndex.Hash h   => [Key(h.Key.Target, h.Key.Collation, h.Key.OpClass?.Name, r)],
            PgIndex.Gist g   => g.Keys.Select(k => Key(k.Target, k.Collation, k.OpClass?.Name, r)),
            PgIndex.SpGist s => [Key(s.Key.Target, s.Key.Collation, s.Key.OpClass?.Name, r)],
            PgIndex.Gin g    => g.Keys.Select(k => Key(k.Target, k.Collation, k.OpClass?.Name, r)),
            PgIndex.Brin b   => b.Keys.Select(k => Key(k.Target, k.Collation, k.OpClass?.Name, r)),
        };
        var include = index switch
        {
            PgIndex.BTree b  => b.Include,
            PgIndex.Gist g   => g.Include,
            PgIndex.SpGist s => s.Include,
            PgIndex.Hash or PgIndex.Gin or PgIndex.Brin => ValueList<string>.Empty,
        };
        IEnumerable<SqlFragment?> storage = index switch
        {
            PgIndex.BTree b  => [b.FillFactor is { } f ? sql.Fragment($"fillfactor={f}") : null,
                                 b.DeduplicateItems is { } d ? sql.Fragment($"deduplicate_items={d}") : null],
            PgIndex.Hash h   => [h.FillFactor is { } f ? sql.Fragment($"fillfactor={f}") : null],
            PgIndex.Gist g   => [g.FillFactor is { } f ? sql.Fragment($"fillfactor={f}") : null,
                                 g.Buffering is null ? null : g.Buffering == GistBuffering.On ? sql.Fragment($"buffering=on")
                                     : g.Buffering == GistBuffering.Off ? sql.Fragment($"buffering=off") : sql.Fragment($"buffering=auto")],
            PgIndex.SpGist s => [s.FillFactor is { } f ? sql.Fragment($"fillfactor={f}") : null],
            PgIndex.Gin g    => [g.FastUpdate is { } fast ? sql.Fragment($"fastupdate={fast}") : null,
                                 g.PendingListLimit is { } l ? sql.Fragment($"gin_pending_list_limit={l}") : null],
            PgIndex.Brin b   => [b.PagesPerRange is { } p ? sql.Fragment($"pages_per_range={p}") : null,
                                 b.Autosummarize is { } a ? sql.Fragment($"autosummarize={a}") : null],
        };
        var unique = index as PgIndex.BTree is { Unique: { } u } ? u : null;
        var method = index switch
        {
            PgIndex.BTree  => SchemaSql.Empty,
            PgIndex.Hash   => sql.Fragment($" USING hash"),
            PgIndex.Gist   => sql.Fragment($" USING gist"),
            PgIndex.SpGist => sql.Fragment($" USING spgist"),
            PgIndex.Gin    => sql.Fragment($" USING gin"),
            PgIndex.Brin   => sql.Fragment($" USING brin"),
        };

        return new PgIndexDescriptor(
            r.Table,
            r.Name(index.Name, "IX", index.Targets),
            method,
            unique is not null,
            unique is { NullsDistinct: false },
            [.. keys],
            [.. include.Select(p => r.Target(new KeyTarget.Column(p)))],
            [.. storage.OfType<SqlFragment>()],
            index.Where is { } w ? r.Predicate(w) : null,
            new Operational<bool>(index.Concurrently));
    }

    protected override void Create(PgIndexDescriptor d, SchemaSql sql)
    {
        var unique = d.Unique ? sql.Fragment($"UNIQUE ") : SchemaSql.Empty;
        var concurrently = d.Concurrently.Value ? sql.Fragment($"CONCURRENTLY ") : SchemaSql.Empty;
        var include = d.Include.Count > 0 ? sql.Fragment($" INCLUDE ({sql.List(d.Include)})") : SchemaSql.Empty;
        var nulls = d.NullsNotDistinct ? sql.Fragment($" NULLS NOT DISTINCT") : SchemaSql.Empty;
        var storage = d.Storage.Count > 0 ? sql.Fragment($" WITH ({sql.List(d.Storage)})") : SchemaSql.Empty;
        var where = d.Where is { } w ? sql.Fragment($" WHERE {w}") : SchemaSql.Empty;

        sql.Statement(
            $"CREATE {unique}INDEX {concurrently}{d.Name} ON {d.Table}{d.Using} ({sql.List(d.Keys)}){include}{nulls}{storage}{where}",
            suppressTransaction: d.Concurrently.Value);
    }

    protected override void Drop(PgIndexDescriptor d, SchemaSql sql) => sql.Statement($"DROP INDEX IF EXISTS {d.Name}");

    protected override bool Rename(PgIndexDescriptor from, PgIndexDescriptor to, SchemaSql sql)
    {
        sql.Statement($"ALTER INDEX {from.Name} RENAME TO {to.Name}");
        return true;
    }

    protected override IEnumerable<NameClaim> Claims(PgIndexDescriptor d) => [new NameClaim.Relation(d.Table.Schema, d.Name)];

    private static SqlFragment BTreeKey(BTreeKey key, SchemaResolver r)
    {
        var sql = r.Sql;
        var direction = key.Descending ? sql.Fragment($" DESC") : SchemaSql.Empty;
        var nulls = key.Nulls is null ? SchemaSql.Empty : key.Nulls == NullOrder.First ? sql.Fragment($" NULLS FIRST") : sql.Fragment($" NULLS LAST");
        return sql.Fragment($"{Key(key.Target, key.Collation, key.OpClass?.Name, r)}{direction}{nulls}");
    }

    private static SqlFragment Key(KeyTarget target, string? collation, string? opClass, SchemaResolver r)
    {
        var sql = r.Sql;
        var expression = target switch
        {
            KeyTarget.Column   => r.Target(target),
            KeyTarget.Template => sql.Fragment($"({r.Target(target)})"),
        };
        var collate = collation is null ? SchemaSql.Empty : sql.Fragment($" COLLATE {r.Dialect.Identifier(collation)}");
        var ops = opClass is null ? SchemaSql.Empty : sql.Fragment($" {r.Dialect.Name(opClass)}");
        return sql.Fragment($"{expression}{collate}{ops}");
    }
}

/// <summary>A resolved <see cref="PgExclusion"/>.</summary>
public sealed record PgExclusionDescriptor(
    TableRef Table,
    Identifier Name,
    SqlFragment Using,
    ValueList<SqlFragment> Elements,
    SqlFragment? Where,
    Deferral Deferral) : ISchemaDescriptor
{
    public ISchemaDescriptor WithName(Identifier name) => this with { Name = name };
}

/// <summary>Diffs and renders <see cref="PgExclusion"/> in 5.x's exact shape (drop-if-exists, then add).</summary>
public sealed class PgExclusionContributor : SchemaContributor<PgExclusion, PgExclusionDescriptor>
{
    public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 20);

    protected override PgExclusionDescriptor Resolve(PgExclusion constraint, SchemaResolver r) => new(
        r.Table,
        r.Name(constraint.Name, "EX", constraint.ElementList.Select(e => e.Target)),
        constraint switch
        {
            PgExclusion.Gist   => r.Sql.Fragment($"gist"),
            PgExclusion.SpGist => r.Sql.Fragment($"spgist"),
        },
        [.. constraint.ElementList.Select(e => e.Target switch
        {
            KeyTarget.Column   => r.Sql.Fragment($"{r.Target(e.Target)} WITH {r.Dialect.Operator(e.Operator.Sql)}"),
            KeyTarget.Template => r.Sql.Fragment($"({r.Target(e.Target)}) WITH {r.Dialect.Operator(e.Operator.Sql)}"),
        })],
        constraint.Where is { } w ? r.Predicate(w) : null,
        constraint.Deferral);

    protected override void Create(PgExclusionDescriptor d, SchemaSql sql)
    {
        var where = d.Where is { } w ? sql.Fragment($" WHERE ({w})") : SchemaSql.Empty;
        var deferral = d.Deferral == Deferral.InitiallyDeferred ? sql.Fragment($" DEFERRABLE INITIALLY DEFERRED")
                     : d.Deferral == Deferral.InitiallyImmediate ? sql.Fragment($" DEFERRABLE")
                     : SchemaSql.Empty;
        sql.Statement($"ALTER TABLE {d.Table} DROP CONSTRAINT IF EXISTS {d.Name}");
        sql.Statement($"ALTER TABLE {d.Table} ADD CONSTRAINT {d.Name} EXCLUDE USING {d.Using} ({sql.List(d.Elements)}){where}{deferral}");
    }

    protected override void Drop(PgExclusionDescriptor d, SchemaSql sql) => sql.Statement($"ALTER TABLE {d.Table} DROP CONSTRAINT IF EXISTS {d.Name}");

    protected override bool Rename(PgExclusionDescriptor from, PgExclusionDescriptor to, SchemaSql sql)
    {
        sql.Statement($"ALTER TABLE {to.Table} RENAME CONSTRAINT {from.Name} TO {to.Name}");
        return true;
    }

    /// <summary>A constraint name per table, and the backing index's name per schema.</summary>
    protected override IEnumerable<NameClaim> Claims(PgExclusionDescriptor d) =>
        [new NameClaim.Constraint(d.Table, d.Name), new NameClaim.Relation(d.Table.Schema, d.Name)];
}
