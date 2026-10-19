namespace SchemaKit.ApiProbes.Probes;

/// <summary>
/// One claim of design/schemakit-api.md: <see cref="Allowed"/> compiles clean, and
/// <see cref="Forbidden"/>, which differs from it only in the part the claim is about, fails with
/// <see cref="Diagnostic"/>. The pair is what makes the probe mean something: a forbidden snippet
/// that failed for an unrelated typo would pass on its own.
/// </summary>
/// <param name="Id">Cited by the design document.</param>
/// <param name="Principle">The section of the design document the probe backs.</param>
/// <param name="Shape">Where the snippets go: a statement in a configuration method, or a top-level type.</param>
/// <param name="Allowed">The legal declaration.</param>
/// <param name="Forbidden">The same declaration with the one part the database would reject.</param>
/// <param name="Diagnostic">The compiler error the forbidden form must produce.</param>
public sealed record Probe(string Id, string Principle, ProbeShape Shape, string Allowed, string Forbidden, string Diagnostic)
{
    public override string ToString() => Id;
}

public enum ProbeShape { Statement, TopLevel }

public static class ProbeCatalogue
{
    public static IReadOnlyList<Probe> All { get; } =
    [
        // ── P1: the access method decides which options exist ─────────────────────────────
        new("P1.gin-unique", "P1", ProbeShape.Statement,
            "people.DeclarePostgres(pg => [pg.Gin(x => x.Tags)]);",
            "people.DeclarePostgres(pg => [pg.Gin(x => x.Tags, unique: true)]);",
            "CS1739"),
        new("P1.gin-unique-key", "P1", ProbeShape.Statement,
            "people.DeclarePostgres(pg => [pg.Unique([pg.Key(x => x.Name)])]);",
            "people.DeclarePostgres(pg => [pg.Unique([pg.Key(x => x.Tags).Ops(GinOpClass.ArrayOps)])]);",
            "CS0029"),
        new("P1.gin-include", "P1", ProbeShape.Statement,
            "people.DeclarePostgres(pg => [pg.BTree(x => x.Name, include: [x => x.Rank])]);",
            "people.DeclarePostgres(pg => [pg.Gin(x => x.Tags, include: [x => x.Rank])]);",
            "CS1739"),
        new("P1.hash-include", "P1", ProbeShape.Statement,
            "people.DeclarePostgres(pg => [pg.SpGist(pg.Key(x => x.Name), include: [x => x.Rank])]);",
            "people.DeclarePostgres(pg => [pg.Hash(x => x.Name, include: [x => x.Rank])]);",
            "CS1739"),
        new("P1.gin-desc", "P1", ProbeShape.Statement,
            "people.DeclarePostgres(pg => [pg.BTree([pg.Key(x => x.Rank).Desc()])]);",
            "people.DeclarePostgres(pg => [pg.Gin([pg.Key(x => x.Tags).Desc()])]);",
            "CS0029"),
        new("P1.gist-nulls", "P1", ProbeShape.Statement,
            "grants.DeclarePostgres(pg => [pg.Gist([pg.Key(x => x.Period)])]);",
            "grants.DeclarePostgres(pg => [pg.Gist([pg.Key(x => x.Period).NullsLast()])]);",
            "CS0029"),
        new("P1.hash-two-keys", "P1", ProbeShape.Statement,
            "people.DeclarePostgres(pg => [pg.Hash(pg.Key(x => x.Name))]);",
            "people.DeclarePostgres(pg => [pg.Hash([pg.Key(x => x.Name), pg.Key(x => x.Rank)])]);",
            "CS9174"),
        new("P1.nulls-not-distinct-without-unique", "P1", ProbeShape.Statement,
            "people.DeclarePostgres(pg => [pg.Unique(x => x.Name, nullsDistinct: false)]);",
            "people.DeclarePostgres(pg => [pg.BTree(x => x.Name, nullsDistinct: false)]);",
            "CS1739"),
        new("P1.opclass-of-other-method", "P1", ProbeShape.Statement,
            "people.DeclarePostgres(pg => [pg.Gin([pg.Key(x => x.Payload).Ops(GinOpClass.JsonbPathOps)])]);",
            "people.DeclarePostgres(pg => [pg.BTree([pg.Key(x => x.Payload).Ops(GinOpClass.JsonbPathOps)])]);",
            "CS0029"),

        // ── P2: a declaration's stage is its type ─────────────────────────────────────────
        new("P2.exclusion-without-overlap", "P2", ProbeShape.Statement,
            "grants.DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.GranteeId).Equal(x => x.RoleId).Overlaps(x => x.Period))]);",
            "grants.DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.GranteeId).Equal(x => x.RoleId))]);",
            "CS1503"),

        // ── P3: operands and options are typed by the CLR type where it decides the store type
        new("P3.overlaps-on-text", "P3", ProbeShape.Statement,
            "grants.DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Period))]);",
            "grants.DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.GranteeId).Overlaps(x => x.Kind))]);",
            "CS0411"),
        new("P3.collate-on-integer", "P3", ProbeShape.Statement,
            "people.DeclarePostgres(pg => [pg.BTree([pg.Key(x => x.Name).Collate(\"C\")])]);",
            "people.DeclarePostgres(pg => [pg.BTree([pg.Key(x => x.Rank).Collate(\"C\")])]);",
            "CS1929"),

        // ── P4: each provider has its own vocabulary ──────────────────────────────────────
        new("P4.sqlserver-nulls-order", "P4", ProbeShape.Statement,
            "people.DeclareSqlServer(ss => [ss.Nonclustered([ss.Key(x => x.Name).Desc()])]);",
            "people.DeclareSqlServer(ss => [ss.Nonclustered([ss.Key(x => x.Name).NullsLast()])]);",
            "CS1061"),
        new("P4.sqlserver-clustered-include", "P4", ProbeShape.Statement,
            "people.DeclareSqlServer(ss => [ss.Nonclustered(x => x.Name, include: [x => x.Rank])]);",
            "people.DeclareSqlServer(ss => [ss.Clustered(x => x.Name, include: [x => x.Rank])]);",
            "CS1739"),
        new("P4.sqlserver-clustered-filter", "P4", ProbeShape.Statement,
            "people.DeclareSqlServer(ss => [ss.Nonclustered(x => x.Name, where: x => x.DeletedAt == null)]);",
            "people.DeclareSqlServer(ss => [ss.Clustered(x => x.Name, where: x => x.DeletedAt == null)]);",
            "CS1739"),
        new("P4.postgres-declaration-on-sqlserver", "P4", ProbeShape.Statement,
            "people.DeclarePostgres(pg => [PgSchema.For<Person>().Gin(x => x.Tags)]);",
            "people.DeclareSqlServer(ss => [PgSchema.For<Person>().Gin(x => x.Tags)]);",
            "CS0029"),

        // ── P5: SQL is a call you write, never a string you pass ──────────────────────────
        new("P5.string-filter", "P5", ProbeShape.Statement,
            "people.DeclarePostgres(pg => [pg.BTree(x => x.Name, where: x => Sql.Raw<bool>(\"{DeletedAt} IS NULL\"))]);",
            "people.DeclarePostgres(pg => [pg.BTree(x => x.Name, where: \"deleted_at IS NULL\")]);",
            "CS1503"),

        // ── P6: options with a fixed set of values are closed ─────────────────────────────
        new("P6.deferral-out-of-range", "P6", ProbeShape.Statement,
            "grants.DeclarePostgres(pg => [pg.Exclusion(pg.Overlaps(x => x.Period), deferral: Deferral.InitiallyDeferred)]);",
            "grants.DeclarePostgres(pg => [pg.Exclusion(pg.Overlaps(x => x.Period), deferral: (Deferral)2)]);",
            "CS0030"),

        // ── P7: lambdas are bound to the entity being configured ──────────────────────────
        new("P7.filter-of-another-entity", "P7", ProbeShape.Statement,
            "people.DeclarePostgres(pg => [pg.BTree(x => x.Name, where: (Person p) => p.DeletedAt == null)]);",
            "people.DeclarePostgres(pg => [pg.BTree(x => x.Name, where: (Order o) => !o.Cancelled)]);",
            "CS1661"),

        // ── P8: declarations are values ───────────────────────────────────────────────────
        new("P8.no-mutation", "P8", ProbeShape.Statement,
            "var index = PgSchema.For<Person>().BTree(x => x.Name); var named = index with { Name = \"ix_people_name\" };",
            "var index = PgSchema.For<Person>().BTree(x => x.Name); index.Name = \"ix_people_name\";",
            "CS8852"),

        // ── P9: closed inside ─────────────────────────────────────────────────────────────
        new("P9.no-foreign-case", "P9", ProbeShape.TopLevel,
            "public sealed record LtreeIndex(string Path) : IPostgresObject { public string Owner => \"Ltree\"; public string? Name => null; public string Identity => Path; public string ToAnnotationValue() => Path; }",
            "public sealed record LtreeIndex(ValueList<BTreeKey> Keys) : PgIndex;",
            "CS9382"),
        new("P9.missing-arm", "P9", ProbeShape.TopLevel,
            """
            public static class Methods
            {
                public static string Of(PgIndex index) => index switch
                {
                    PgIndex.BTree => "btree", PgIndex.Hash => "hash", PgIndex.Gist => "gist",
                    PgIndex.SpGist => "spgist", PgIndex.Gin => "gin", PgIndex.Brin => "brin",
                };
            }
            """,
            """
            public static class Methods
            {
                public static string Of(PgIndex index) => index switch
                {
                    PgIndex.BTree => "btree", PgIndex.Hash => "hash", PgIndex.Gist => "gist",
                    PgIndex.SpGist => "spgist", PgIndex.Gin => "gin",
                };
            }
            """,
            "CS8509"),
    ];
}
