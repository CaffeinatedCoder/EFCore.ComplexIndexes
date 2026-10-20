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
    /// <summary>
    /// A complete third-party extension, as CodoMetis.Hierarchies would write it against the kernel:
    /// its own closed union, descriptor and contributor, and a factory method on PgSchema. The X
    /// probes change one line of it each.
    /// </summary>
    private const string LtreeExtension = """
        public closed record LtreeObject : IPostgresObject, ISchemaDeclaration<LtreeObject>
        {
            public static string OwnerId => "Ltree.Path";
            public string Owner => OwnerId;
            public string? Name { get; init; }
            public string Identity => this switch
            {
                MaintainedPath m => $"maintained|{m.Path}|{m.Parent}",
                PathCheck c => $"check|{c.Path}",
            };
            public string ToAnnotationValue() => System.Text.Json.JsonSerializer.Serialize(this);
            public static LtreeObject FromAnnotationValue(string json) => throw new NotSupportedException();

            public sealed record MaintainedPath(string Path, string Parent) : LtreeObject;
            public sealed record PathCheck(string Path) : LtreeObject;
        }

        public sealed record LtreeDescriptor(TableRef Table, Identifier Name, SqlFragment Ddl, bool IsTrigger) : ISchemaDescriptor
        {
            public ISchemaDescriptor WithName(Identifier name) => this with { Name = name };
        }

        public sealed class LtreeContributor : SchemaContributor<LtreeObject, LtreeDescriptor>
        {
            public override SchemaPlacement Placement => SchemaPlacement.AfterTables(order: 30);

            protected override LtreeDescriptor Resolve(LtreeObject declaration, SchemaResolver r) => declaration switch
            {
                LtreeObject.MaintainedPath m => new(r.Table, r.Name(m.Name, "TR", [new KeyTarget.Column(m.Path)]),
                    r.Sql.Fragment($"BEFORE INSERT OR UPDATE ON {r.Table} FOR EACH ROW EXECUTE FUNCTION codometis_ltree_path()"), true),
                /*CHECK-ARM*/LtreeObject.PathCheck c => new(r.Table, r.Name(c.Name, "CK", [new KeyTarget.Column(c.Path)]),
                    r.Sql.Fragment($"CHECK (nlevel({r.Target(new KeyTarget.Column(c.Path))}) > 0)"), false),/*END-CHECK-ARM*/
            };

            protected override void Create(LtreeDescriptor d, SchemaSql sql)
            {
                if (d.IsTrigger) sql.Statement($"CREATE TRIGGER {d.Name} {d.Ddl}");
                else sql.Statement($"ALTER TABLE {d.Table} ADD CONSTRAINT {d.Name} {d.Ddl}");
            }

            protected override void Drop(LtreeDescriptor d, SchemaSql sql)
            {
                if (d.IsTrigger) sql.Statement($"DROP TRIGGER IF EXISTS {/*NAME*/d.Name} ON {d.Table}");
                else sql.Statement($"ALTER TABLE {d.Table} DROP CONSTRAINT IF EXISTS {d.Name}");
            }

            protected override IEnumerable<NameClaim> Claims(LtreeDescriptor d) =>
                d.IsTrigger ? [new NameClaim.Trigger(d.Table, d.Name)] : [new NameClaim.Constraint(d.Table, d.Name)];
        }

        public static class LtreeSchemaExtensions
        {
            extension<TEntity>(PgSchema<TEntity> pg) where TEntity : class
            {
                public LtreeObject.MaintainedPath MaintainedPath(
                    System.Linq.Expressions.Expression<Func<TEntity, TreePath>> path,
                    System.Linq.Expressions.Expression<Func<TEntity, int?>> parent)
                    => new(((System.Linq.Expressions.MemberExpression)path.Body).Member.Name,
                           ((System.Linq.Expressions.MemberExpression)parent.Body).Member.Name);
            }
        }

        public static class CategoryConfiguration
        {
            public static void Configure(EntityTypeBuilder<Category> b) =>
                b./*APPLY*/DeclarePostgres(pg => [pg.MaintainedPath(x => x.Path, x => x.ParentId), pg.Gist([pg.Key(x => x.Path)])]);
        }
        """;

    /// <summary>Vocabulary only: what CodoMetis.ValueRanges adds so its own range types are typed exclusion operands.</summary>
    private const string RangeVocabulary = """
        public static class ValueRangesSchemaExtensions
        {
            extension<TEntity>(ExclusionDraft<TEntity> draft) where TEntity : class
            {
                public ExclusionElements<TEntity> Overlaps(System.Linq.Expressions.Expression<Func<TEntity, DayRange>> range)
                    => draft.WithUncheckedOperator(range, "&&");
            }
        }
        """;

    private const string BookingConfiguration = """
        public static class BookingConfiguration
        {
            public static void Configure(EntityTypeBuilder<Booking> b) =>
                b.DeclarePostgres(pg => [pg.Exclusion(pg.Equal(x => x.RoomId).Overlaps(x => x.Validity))]);
        }
        """;

    private static string Without(string source, string from, string to) =>
        source[..source.IndexOf(from, StringComparison.Ordinal)] + source[(source.IndexOf(to, StringComparison.Ordinal) + to.Length)..];

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

        // ── E: the engine seam: SQL text only from what the dialect checked ─────────────────
        new("E1.string-in-ddl", "E", ProbeShape.TopLevel,
            "public static class Ddl { public static void Drop(SchemaSql sql, Identifier name) => sql.Statement($\"DROP INDEX {name}\"); }",
            "public static class Ddl { public static void Drop(SchemaSql sql, string name) => sql.Statement($\"DROP INDEX {name}\"); }",
            "CS1503"),
        new("E2.fragment-from-string", "E", ProbeShape.TopLevel,
            "public static class Ddl { public static SqlFragment Make(SchemaSql sql) => sql.Fragment($\"lower(email)\"); }",
            "public static class Ddl { public static SqlFragment Make(SchemaSql sql) => new SqlFragment(\"lower(email)\"); }",
            "CS1729"),
        new("E3.identifier-from-string", "E", ProbeShape.TopLevel,
            "public static class Ddl { public static Identifier Make(ISchemaDialect dialect) => dialect.Identifier(\"ix_people_name\"); }",
            "public static class Ddl { public static Identifier Make(ISchemaDialect dialect) => \"ix_people_name\"; }",
            "CS0029"),
        new("E4.render-a-declaration", "E", ProbeShape.TopLevel,
            LtreeExtension,
            LtreeExtension.Replace("protected override void Create(LtreeDescriptor d, SchemaSql sql)", "protected override void Create(LtreeObject d, SchemaSql sql)"),
            "CS0115"),

        // ── X: an extension package, end to end ───────────────────────────────────────────
        new("X1.extension-misses-its-own-case", "X", ProbeShape.TopLevel,
            LtreeExtension,
            Without(LtreeExtension, "/*CHECK-ARM*/", "/*END-CHECK-ARM*/"),
            "CS8509"),
        new("X2.extension-renders-a-raw-name", "X", ProbeShape.TopLevel,
            LtreeExtension,
            LtreeExtension.Replace("{/*NAME*/d.Name}", "{d.Name.Name}"),
            "CS1503"),
        new("X3.extension-declaration-on-sqlserver", "X", ProbeShape.TopLevel,
            LtreeExtension,
            LtreeExtension.Replace("b./*APPLY*/DeclarePostgres(pg => [pg.MaintainedPath(x => x.Path, x => x.ParentId), pg.Gist([pg.Key(x => x.Path)])]);",
                "b.DeclareSqlServer(ss => [PgSchema.For<Category>().MaintainedPath(x => x.Path, x => x.ParentId)]);"),
            "CS0029"),

        // ── V: vocabulary from another package, no contributor ────────────────────────────
        new("V1.operand-by-extension-member", "V", ProbeShape.TopLevel,
            RangeVocabulary + BookingConfiguration,
            BookingConfiguration,
            "CS0411"),
    ];
}
