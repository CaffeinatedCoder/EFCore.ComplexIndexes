using System.Linq.Expressions;
using System.Text.RegularExpressions;
using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// JSON members are rendered the way Npgsql's query translation renders them, because PostgreSQL
/// uses an expression index only for a query whose expression matches it. Before 5.4.0 every
/// member was <c>"doc" -&gt; 'A' -&gt;&gt; 'B'</c> text with no cast, which matched only a top-level
/// string: a nested member (<c>#&gt;&gt; '{A,B}'</c> in the query) or any typed member
/// (<c>CAST(… AS integer)</c>) got an index that applied cleanly, enforced uniqueness, and was never
/// used by a single query. The same rules must reach databases built by earlier versions, so a model
/// without <see cref="ComplexIndexAnnotations.RenderingVersion"/> is still rendered the old way, and
/// the difference surfaces as one drop-and-create per affected index.
/// </summary>
[TestClass]
public class NpgsqlJsonMemberRenderingTests
{
    public enum Tier { Basic, Pro }

    public class Address
    {
        public string City       { get; set; } = "";
        public int    Zip        { get; set; }
        public string PostalCode { get; set; } = "";
    }

    public class Profile
    {
        public string       Slug     { get; set; } = "";
        public int          Rank     { get; set; }
        public long         Big      { get; set; }
        public decimal      Price    { get; set; }
        public double       Ratio    { get; set; }
        public bool         Active   { get; set; }
        public Guid         Key      { get; set; }
        public DateTime     At       { get; set; }
        public DateOnly     Day      { get; set; }
        public Tier         Tier     { get; set; }
        public Tier         TierText { get; set; }
        public int?         Maybe    { get; set; }
        public byte[]       Blob     { get; set; } = [];
        public List<string> Tags     { get; set; } = [];
        public Address      Address  { get; set; } = new();
    }

    public class Account
    {
        public int                   Id      { get; set; }
        public NpgsqlRange<DateTime> Period  { get; set; }
        public Profile               Profile { get; set; } = new();
    }

    private static void ConfigureAccount(EntityTypeBuilder<Account> b)
    {
        b.ToTable("accounts");
        b.HasKey(x => x.Id);
        b.ComplexProperty(x => x.Profile, p =>
        {
            p.ToJson("profile");
            p.Property(x => x.TierText).HasConversion<string>();
            p.Property(x => x.Price).HasPrecision(18, 2);
            p.ComplexProperty(x => x.Address, a => a.Property(x => x.PostalCode).HasJsonPropertyName("postal_code"));
        });
    }

    // One explicitly named index per member shape, with the SQL Npgsql's queries use for it.
    internal static readonly (string Name, Expression<Func<Account, object?>> Member, string Sql)[] Members =
    [
        ("ix_slug",      x => x.Profile.Slug,               "\"profile\" ->> 'Slug'"),
        ("ix_rank",      x => x.Profile.Rank,               "CAST(\"profile\" ->> 'Rank' AS integer)"),
        ("ix_big",       x => x.Profile.Big,                "CAST(\"profile\" ->> 'Big' AS bigint)"),
        ("ix_price",     x => x.Profile.Price,              "CAST(\"profile\" ->> 'Price' AS numeric(18,2))"),
        ("ix_ratio",     x => x.Profile.Ratio,              "CAST(\"profile\" ->> 'Ratio' AS double precision)"),
        ("ix_active",    x => x.Profile.Active,             "CAST(\"profile\" ->> 'Active' AS boolean)"),
        ("ix_key",       x => x.Profile.Key,                "CAST(\"profile\" ->> 'Key' AS uuid)"),
        ("ix_tier",      x => x.Profile.Tier,               "CAST(\"profile\" ->> 'Tier' AS integer)"),
        ("ix_tier_text", x => x.Profile.TierText,           "\"profile\" ->> 'TierText'"),
        ("ix_maybe",     x => x.Profile.Maybe,              "CAST(\"profile\" ->> 'Maybe' AS integer)"),
        ("ix_blob",      x => x.Profile.Blob,               "decode(\"profile\" ->> 'Blob', 'base64')"),
        ("ix_tags",      x => x.Profile.Tags,               "\"profile\" -> 'Tags'"),
        ("ix_city",      x => x.Profile.Address.City,       "\"profile\" #>> '{Address,City}'"),
        ("ix_zip",       x => x.Profile.Address.Zip,        "CAST(\"profile\" #>> '{Address,Zip}' AS integer)"),
        ("ix_postal",    x => x.Profile.Address.PostalCode, "\"profile\" #>> ARRAY['Address','postal_code']::text[]"),
        ("ix_address",   x => x.Profile.Address,            "\"profile\" -> 'Address'")
    ];

    // Date and time members stay text (their casts cannot be indexed); unique, so they are allowed.
    internal static readonly (string Name, Expression<Func<Account, object?>> Member, string Sql)[] TextFallbackMembers =
    [
        ("ux_at",  x => x.Profile.At,  "\"profile\" ->> 'At'"),
        ("ux_day", x => x.Profile.Day, "\"profile\" ->> 'Day'")
    ];

    // Rendered identically by both versions: top-level strings, a top-level sub-document, and the text fallbacks.
    internal static readonly HashSet<string> UnaffectedByRollout = ["ix_slug", "ix_tier_text", "ix_address", "ux_at", "ux_day"];

    internal static void DeclareMatrix(EntityTypeBuilder<Account> b)
    {
        ConfigureAccount(b);
        foreach (var (name, member, _) in Members)
            b.HasComplexIndex(member, indexName: name);
        foreach (var (name, member, _) in TextFallbackMembers)
            b.HasComplexIndex(member, isUnique: true, indexName: name);
    }

    public class MatrixContext(DbContextOptions<MatrixContext> options) : DbContext(options)
    {
        public DbSet<Account> Accounts => Set<Account>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Account>(DeclareMatrix);
    }

    // The same declarations as a model written before 5.4.0 carries them: no rendering version.
    public class MatrixBefore54Context(DbContextOptions<MatrixBefore54Context> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Account>(DeclareMatrix);
            modelBuilder.Model.RemoveAnnotation(ComplexIndexAnnotations.RenderingVersion);
        }
    }

    // Default names, which applications match on: a rendering change must never rename an index.
    private static void DeclareDefaultNamed(EntityTypeBuilder<Account> b)
    {
        ConfigureAccount(b);
        b.HasComplexIndex(x => x.Profile.Rank);
        b.HasComplexIndex(x => x.Profile.Address.Zip, isUnique: true);
        b.HasComplexIndex(x => x.Profile.Address.City);
        b.HasExpressionIndex(x => x.Profile.Address.City.ToLower());
    }

    public class DefaultNamedContext(DbContextOptions<DefaultNamedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Account>(DeclareDefaultNamed);
    }

    public class DefaultNamedBefore54Context(DbContextOptions<DefaultNamedBefore54Context> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Account>(DeclareDefaultNamed);
            modelBuilder.Model.RemoveAnnotation(ComplexIndexAnnotations.RenderingVersion);
        }
    }

    public class PropertyLevelContext(DbContextOptions<PropertyLevelContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Account>(b =>
            {
                b.ToTable("accounts");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Profile, p =>
                {
                    p.ToJson("profile");
                    p.Property(x => x.Rank).HasComplexIndex();
                });
            });
    }

    // Filters: index filters follow the new rules; exclusion constraint filters keep the old ones.
    internal static void DeclareFilters(EntityTypeBuilder<Account> b)
    {
        ConfigureAccount(b);
        b.HasComplexIndex(x => x.Profile.Slug, filter: "{Profile.Address.City} IS NOT NULL", indexName: "ix_filtered_city");
        b.HasComplexIndex(x => x.Profile.Slug, x => x.Profile.Rank > 5, indexName: "ix_filtered_rank");
        b.HasExclusionConstraint(x => x.Id, x => x.Period, filter: "{Profile.Address.City} IS NOT NULL", name: "ex_accounts_city");
    }

    public class FilterContext(DbContextOptions<FilterContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Account>(DeclareFilters);
    }

    public class FilterBefore54Context(DbContextOptions<FilterBefore54Context> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Account>(DeclareFilters);
            modelBuilder.Model.RemoveAnnotation(ComplexIndexAnnotations.RenderingVersion);
        }
    }

    public class NonUniqueDateContext(DbContextOptions<NonUniqueDateContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Account>(b =>
            {
                ConfigureAccount(b);
                b.HasComplexIndex(x => x.Profile.At, indexName: "ix_at");
            });
    }

    public class NonUniqueDateOnlyContext(DbContextOptions<NonUniqueDateOnlyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Account>(b =>
            {
                ConfigureAccount(b);
                b.HasComplexIndex(x => x.Profile.Day);
            });
    }

    public class TrailingDateContext(DbContextOptions<TrailingDateContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Account>(b =>
            {
                ConfigureAccount(b);
                b.HasComplexCompositeIndex(x => new { x.Profile.Slug, x.Profile.At }, indexName: "ix_slug_at");
            });
    }

    public class NoIndexContext(DbContextOptions<NoIndexContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Account>(ConfigureAccount);
    }

    // ── Helpers ──

    private static Dictionary<string, string> PartSqlByName(IEnumerable<MigrationOperation> operations)
        => operations.OfType<CreateIndexOperation>().ToDictionary(
               o => o.Name,
               o => o.FindAnnotation(ComplexIndexAnnotations.IndexParts)?.Value is string json
                        ? IndexPartsSerializer.Deserialize(json)[0].Value
                        : o.Columns[0]);

    // The member expression EF's own query compares, with the table alias and identifier quoting
    // removed: Npgsql quotes only when it must, the differ always does, and PostgreSQL reads both
    // as the same column.
    private static string QueriedExpression(Expression<Func<Account, bool>> predicate)
    {
        using var context = new MatrixContext(
            new DbContextOptionsBuilder<MatrixContext>().UseNpgsql(MigrationHarness.NpgsqlConnection).Options);

        var sql   = context.Accounts.Where(predicate).ToQueryString();
        var where = sql[(sql.IndexOf("WHERE ", StringComparison.Ordinal) + 6)..].ReplaceLineEndings(" ").Trim();
        var cut   = where.LastIndexOf(" = ", StringComparison.Ordinal);

        return Normalize(cut < 0 ? where : where[..cut]);
    }

    private static string Normalize(string sql)
    {
        sql = Regex.Replace(sql, @"(?<![\w'])a\.", "").Replace("\"", "");
        return sql.StartsWith('(') && sql.EndsWith(')') ? sql[1..^1] : sql;
    }

    // ── Rendering ──

    [TestMethod(DisplayName = "Every JSON member type renders exactly as Npgsql's query translation does")]
    public void Members_render_as_the_query_translation_does()
    {
        var parts = PartSqlByName(MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<MatrixContext>()));

        foreach (var (name, _, sql) in Members.Concat(TextFallbackMembers))
            Assert.AreEqual(sql, parts[name], name);
    }

    // The guard against drift: the expected SQL above is asserted against EF's own query output, so
    // a future Npgsql that translates a member differently fails here rather than in production.
    [TestMethod(DisplayName = "The rendered expressions are the ones EF Core's own queries compare")]
    public void Rendered_expressions_match_live_query_translation()
    {
        var key   = Guid.NewGuid();
        var blob  = new byte[] { 1, 2, 3 };
        var cases = new (string Name, Expression<Func<Account, bool>> Query)[]
        {
            ("ix_slug",      a => a.Profile.Slug == "x"),
            ("ix_rank",      a => a.Profile.Rank == 5),
            ("ix_big",       a => a.Profile.Big == 5L),
            ("ix_price",     a => a.Profile.Price == 5.25m),
            ("ix_ratio",     a => a.Profile.Ratio == 0.5),
            ("ix_active",    a => a.Profile.Active),
            ("ix_key",       a => a.Profile.Key == key),
            ("ix_tier",      a => a.Profile.Tier == Tier.Pro),
            ("ix_tier_text", a => a.Profile.TierText == Tier.Pro),
            ("ix_maybe",     a => a.Profile.Maybe == 5),
            ("ix_blob",      a => a.Profile.Blob == blob),
            ("ix_city",      a => a.Profile.Address.City == "x"),
            ("ix_zip",       a => a.Profile.Address.Zip == 5),
            ("ix_postal",    a => a.Profile.Address.PostalCode == "x")
        };

        var parts = PartSqlByName(MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<MatrixContext>()));

        foreach (var (name, query) in cases)
            Assert.AreEqual(QueriedExpression(query), Normalize(parts[name]), name);
    }

    [TestMethod(DisplayName = "A property-level declaration inside the document renders the same way")]
    public void Property_level_member_renders_typed()
    {
        var create = Assert.ContainsSingle(
            MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<PropertyLevelContext>()).OfType<CreateIndexOperation>());

        Assert.AreEqual("IX_accounts_profileRank", create.Name);
        Assert.AreEqual("CAST(\"profile\" ->> 'Rank' AS integer)", PartSqlByName([create])[create.Name]);
    }

    [TestMethod(DisplayName = "Default index names do not change with the rendering")]
    public void Default_names_are_stable_across_rendering_versions()
    {
        var before = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<DefaultNamedBefore54Context>())
                                     .OfType<CreateIndexOperation>().Select(o => o.Name).Order().ToList();
        var after  = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<DefaultNamedContext>())
                                     .OfType<CreateIndexOperation>().Select(o => o.Name).Order().ToList();

        CollectionAssert.AreEqual(before, after);
        CollectionAssert.Contains(after, "IX_accounts_profileRank");
        CollectionAssert.Contains(after, "IX_accounts_profileAddressZip");
        CollectionAssert.Contains(after, "IX_accounts_lowerprofileAddressCity");
    }

    // ── Rollout ──

    [TestMethod(DisplayName = "A pre-5.4.0 model rebuilds exactly the indexes whose rendering changed, under the same names")]
    public void Rollout_rebuilds_only_affected_indexes()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<MatrixBefore54Context>(),
            MigrationHarness.NpgsqlModel<MatrixContext>());

        var affected = Members.Select(m => m.Name).Concat(TextFallbackMembers.Select(m => m.Name))
                              .Where(n => !UnaffectedByRollout.Contains(n)).Order().ToList();

        var dropped = operations.OfType<DropIndexOperation>().Select(o => o.Name).Order().ToList();
        var created = operations.OfType<CreateIndexOperation>().Select(o => o.Name).Order().ToList();

        Assert.AreEqual(string.Join(", ", affected), string.Join(", ", dropped));
        Assert.AreEqual(string.Join(", ", affected), string.Join(", ", created));

        // Drops before creates: the same name is dropped and re-created in one migration.
        var lastDrop    = operations.ToList().FindLastIndex(o => o is DropIndexOperation);
        var firstCreate = operations.ToList().FindIndex(o => o is CreateIndexOperation);
        Assert.IsLessThan(firstCreate, lastDrop);
    }

    [TestMethod(DisplayName = "Once the model carries the rendering version, it diffs clean against itself")]
    public void Current_rendering_has_no_churn()
        => Assert.IsEmpty(MigrationHarness.NpgsqlDiff(
               MigrationHarness.NpgsqlModel<MatrixContext>(),
               MigrationHarness.NpgsqlModel<MatrixContext>()));

    [TestMethod(DisplayName = "Declaring an index marks the model; a model without one stays unmarked")]
    public void Declarations_mark_the_model()
    {
        Assert.AreEqual(
            ComplexIndexStorage.CurrentRenderingVersion,
            MigrationHarness.NpgsqlModel<PropertyLevelContext>().Model[ComplexIndexAnnotations.RenderingVersion]);
        Assert.IsNull(MigrationHarness.NpgsqlModel<NoIndexContext>().Model[ComplexIndexAnnotations.RenderingVersion]);
    }

    // ── Filters ──

    [TestMethod(DisplayName = "Index filters render JSON placeholders by the new rules")]
    public void Index_filters_follow_the_rendering()
    {
        var creates = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<FilterContext>())
                                      .OfType<CreateIndexOperation>().ToDictionary(o => o.Name);

        Assert.AreEqual("(\"profile\" #>> '{Address,City}') IS NOT NULL", creates["ix_filtered_city"].Filter);

        // The old rendering compared text with an integer: 42883 at apply time.
        StringAssert.Contains(creates["ix_filtered_rank"].Filter, "(CAST(\"profile\" ->> 'Rank' AS integer)) > 5");
    }

    [TestMethod(DisplayName = "Exclusion constraint filters keep the original JSON rendering")]
    public void Exclusion_filters_keep_the_original_rendering()
    {
        var add = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<FilterContext>())
                                  .OfType<SqlOperation>().Single(o => o.Sql.Contains("EXCLUDE")).Sql;

        StringAssert.Contains(add, "WHERE ((\"profile\" -> 'Address' ->> 'City') IS NOT NULL)");
    }

    [TestMethod(DisplayName = "The rollout leaves exclusion constraints alone")]
    public void Rollout_does_not_touch_exclusion_constraints()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<FilterBefore54Context>(),
            MigrationHarness.NpgsqlModel<FilterContext>());

        Assert.IsFalse(operations.OfType<SqlOperation>().Any(o => o.Sql.Contains("ex_accounts_city")));
        CollectionAssert.AreEquivalent(
            new[] { "ix_filtered_city", "ix_filtered_rank" },
            operations.OfType<CreateIndexOperation>().Select(o => o.Name).ToList());
    }

    // ── Date and time members ──

    [TestMethod(DisplayName = "A non-unique index leading with a timestamp member is rejected")]
    public void Non_unique_timestamp_member_is_rejected()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<NonUniqueDateContext>()));

        StringAssert.Contains(exception.Message, "'ix_at'");
        StringAssert.Contains(exception.Message, "'Profile.At'");
        StringAssert.Contains(exception.Message, "timestamp with time zone");
    }

    [TestMethod(DisplayName = "A non-unique index leading with a DateOnly member is rejected")]
    public void Non_unique_date_member_is_rejected()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<NonUniqueDateOnlyContext>()));

        StringAssert.Contains(exception.Message, "'Profile.Day'");
        StringAssert.Contains(exception.Message, " date.");
    }

    [TestMethod(DisplayName = "A timestamp member after a usable leading part is allowed")]
    public void Trailing_timestamp_member_is_allowed()
    {
        var create = Assert.ContainsSingle(
            MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<TrailingDateContext>()).OfType<CreateIndexOperation>());

        Assert.AreEqual("ix_slug_at", create.Name);
    }

    // Validation reads the target only: a snapshot holding the index must stay diffable, or the
    // model that removes it could never be migrated.
    [TestMethod(DisplayName = "A rejected index in the source model can still be dropped")]
    public void Rejected_index_in_source_can_be_dropped()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<NonUniqueDateContext>(),
            MigrationHarness.NpgsqlModel<NoIndexContext>());

        Assert.AreEqual("ix_at", Assert.ContainsSingle(operations.OfType<DropIndexOperation>()).Name);
    }
}
