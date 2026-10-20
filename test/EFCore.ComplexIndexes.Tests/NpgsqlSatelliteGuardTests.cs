using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Guards for defects the 5.5.0 analysis seeded into the PostgreSQL satellite while the whole suite
/// kept passing (#83). Each test names the seed it catches, and each was proven by applying that
/// seed. Most of them are silent: the migration scaffolds and applies, and the database differs from
/// the model.
/// </summary>
[TestClass]
public class NpgsqlSatelliteGuardTests
{
    public class Address
    {
        public string City { get; set; } = "";
    }

    public class Profile
    {
        public TimeSpan Duration { get; set; }
        public Address  Address  { get; set; } = new();
        public Address  Address2 { get; set; } = new();
        public Address  Quoted   { get; set; } = new();
    }

    public class Grant
    {
        public int                   Id        { get; set; }
        public int                   RoomId    { get; set; }
        public string                Code      { get; set; } = "";
        public DateOnly?             RevokedAt { get; set; }
        public NpgsqlRange<DateOnly> Period    { get; set; }
        public Profile               Profile   { get; set; } = new();
    }

    private static void MapGrant(EntityTypeBuilder<Grant> b)
    {
        b.ToTable("grants");
        b.HasKey(x => x.Id);
        b.Property(x => x.RoomId).HasColumnName("room_id");
        b.Property(x => x.Code).HasColumnName("code");
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        b.Property(x => x.Period).HasColumnName("period");
        b.ComplexProperty(x => x.Profile, p =>
        {
            p.ToJson("profile");
            p.ComplexProperty(x => x.Address, a => a.HasJsonPropertyName("addr"));
            p.ComplexProperty(x => x.Quoted, a => a.Property(x => x.City).HasJsonPropertyName("o'clock"));
        });
    }

    private abstract class GrantContext(DbContextOptions options) : DbContext(options)
    {
        protected abstract void Configure(EntityTypeBuilder<Grant> b);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Grant>(b =>
            {
                MapGrant(b);
                Configure(b);
            });
    }

    private static IReadOnlyList<MigrationOperation> Create<TContext>() where TContext : DbContext
        => MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<TContext>());

    private static IReadOnlyList<MigrationOperation> Diff<TSource, TTarget>()
        where TSource : DbContext where TTarget : DbContext
        => MigrationHarness.NpgsqlDiff(MigrationHarness.NpgsqlModel<TSource>(), MigrationHarness.NpgsqlModel<TTarget>());

    private static List<string> ExclusionSql(IEnumerable<MigrationOperation> operations)
        => [.. operations.OfType<SqlOperation>().Select(o => o.Sql).Where(s => s.Contains("CONSTRAINT"))];

    // ── EndStatement(builder, suppressTransaction: false) in the parts path ──

    [TestMethod(DisplayName = "CREATE INDEX CONCURRENTLY from the parts path runs outside the migration transaction")]
    public void Concurrent_parts_index_suppresses_the_transaction()
    {
        var operation = new CreateIndexOperation { Name = "ix_lower_code", Table = "grants", Columns = ["lower(code)"] };
        operation.AddAnnotation(ComplexIndexAnnotations.IndexParts, IndexPartsSerializer.Serialize([new ResolvedIndexPart(true, "lower(code)")]));
        operation.AddAnnotation(NpgsqlAnnotations.CreatedConcurrently, true);

        using var context = new MigrationHarness.EmptyContext(MigrationHarness.NpgsqlOptions(complexIndexWiring: true));
        var command = Assert.ContainsSingle(context.GetService<IMigrationsSqlGenerator>().Generate([operation]));

        StringAssert.Contains(command.CommandText, "CREATE INDEX CONCURRENTLY");
        Assert.IsTrue(command.TransactionSuppressed, "PostgreSQL refuses CREATE INDEX CONCURRENTLY inside a transaction block.");
    }

    // ── ExclusionDescriptor.Equals without Deferrable/InitiallyDeferred, or without Method ──

    private class PlainExclusionContext(DbContextOptions<PlainExclusionContext> o) : GrantContext(o)
    {
        protected override void Configure(EntityTypeBuilder<Grant> b)
            => b.HasExclusionConstraint(ex => ex.WithEquality(x => x.RoomId).WithOverlaps(x => x.Period).HasName("ex_room"));
    }

    private class DeferrableExclusionContext(DbContextOptions<DeferrableExclusionContext> o) : GrantContext(o)
    {
        protected override void Configure(EntityTypeBuilder<Grant> b)
            => b.HasExclusionConstraint(ex => ex.WithEquality(x => x.RoomId).WithOverlaps(x => x.Period).HasName("ex_room")
                                                .IsDeferrable());
    }

    private class InitiallyDeferredExclusionContext(DbContextOptions<InitiallyDeferredExclusionContext> o) : GrantContext(o)
    {
        protected override void Configure(EntityTypeBuilder<Grant> b)
            => b.HasExclusionConstraint(ex => ex.WithEquality(x => x.RoomId).WithOverlaps(x => x.Period).HasName("ex_room")
                                                .IsDeferrable(initiallyDeferred: true));
    }

    private class RenamedDeferrableExclusionContext(DbContextOptions<RenamedDeferrableExclusionContext> o) : GrantContext(o)
    {
        protected override void Configure(EntityTypeBuilder<Grant> b)
            => b.HasExclusionConstraint(ex => ex.WithEquality(x => x.RoomId).WithOverlaps(x => x.Period).HasName("ex_room_deferred")
                                                .IsDeferrable());
    }

    private class SpGistExclusionContext(DbContextOptions<SpGistExclusionContext> o) : GrantContext(o)
    {
        protected override void Configure(EntityTypeBuilder<Grant> b)
            => b.HasExclusionConstraint(ex => ex.WithEquality(x => x.RoomId).WithOverlaps(x => x.Period).HasName("ex_room")
                                                .UseMethod("spgist"));
    }

    private class RenamedSpGistExclusionContext(DbContextOptions<RenamedSpGistExclusionContext> o) : GrantContext(o)
    {
        protected override void Configure(EntityTypeBuilder<Grant> b)
            => b.HasExclusionConstraint(ex => ex.WithEquality(x => x.RoomId).WithOverlaps(x => x.Period).HasName("ex_room_spgist")
                                                .UseMethod("spgist"));
    }

    private static void AssertRebuilt(IReadOnlyList<MigrationOperation> operations, string droppedName, string addedFragment)
    {
        var sql = ExclusionSql(operations);

        Assert.IsFalse(sql.Any(s => s.Contains("RENAME CONSTRAINT")), "A real change must not become a rename.");
        Assert.IsTrue(sql.Any(s => s.StartsWith($"ALTER TABLE \"grants\" DROP CONSTRAINT IF EXISTS \"{droppedName}\";", StringComparison.Ordinal)
                                && !s.Contains("ADD CONSTRAINT")),
                      $"Expected a standalone drop of '{droppedName}'.");
        Assert.IsTrue(sql.Any(s => s.Contains("ADD CONSTRAINT") && s.Contains(addedFragment)), $"Expected an ADD with '{addedFragment}'.");
    }

    [TestMethod(DisplayName = "Making an exclusion constraint deferrable drops and re-adds it")]
    public void Deferrable_change_rebuilds_the_constraint()
        => AssertRebuilt(Diff<PlainExclusionContext, DeferrableExclusionContext>(), "ex_room", " DEFERRABLE;");

    [TestMethod(DisplayName = "Making a deferrable exclusion constraint initially deferred drops and re-adds it")]
    public void Initially_deferred_change_rebuilds_the_constraint()
        => AssertRebuilt(Diff<DeferrableExclusionContext, InitiallyDeferredExclusionContext>(), "ex_room", " DEFERRABLE INITIALLY DEFERRED;");

    // A rename compares the two sides with Equals directly, not through the hash set.
    [TestMethod(DisplayName = "A rename plus a deferrability change is not a rename")]
    public void Rename_with_deferrable_change_rebuilds_the_constraint()
        => AssertRebuilt(Diff<PlainExclusionContext, RenamedDeferrableExclusionContext>(), "ex_room", "\"ex_room_deferred\"");

    [TestMethod(DisplayName = "Changing an exclusion constraint's access method drops and re-adds it")]
    public void Method_change_rebuilds_the_constraint()
        => AssertRebuilt(Diff<PlainExclusionContext, SpGistExclusionContext>(), "ex_room", "EXCLUDE USING spgist");

    [TestMethod(DisplayName = "A rename plus an access method change is not a rename")]
    public void Rename_with_method_change_rebuilds_the_constraint()
        => AssertRebuilt(Diff<PlainExclusionContext, RenamedSpGistExclusionContext>(), "ex_room", "EXCLUDE USING spgist");

    // ── Exclusion constraint drops placed after the base operations ──

    // The constraint's index goes with it, so a native index taking over its name can only be
    // created once the constraint is gone: in the other order, CREATE INDEX fails with 42P07.
    private class NativeIndexTakesTheNameContext(DbContextOptions<NativeIndexTakesTheNameContext> o) : GrantContext(o)
    {
        protected override void Configure(EntityTypeBuilder<Grant> b)
            => b.HasIndex(x => x.RoomId).HasDatabaseName("ex_room");
    }

    [TestMethod(DisplayName = "An exclusion constraint is dropped before the base operations")]
    public void Exclusion_drop_precedes_base_operations()
    {
        var operations = Diff<PlainExclusionContext, NativeIndexTakesTheNameContext>().ToList();

        var drop   = operations.FindIndex(o => o is SqlOperation { Sql: var sql } && sql.Contains("DROP CONSTRAINT IF EXISTS \"ex_room\""));
        var create = operations.FindIndex(o => o is CreateIndexOperation { Name: "ex_room" });

        Assert.IsGreaterThanOrEqualTo(0, drop);
        Assert.IsGreaterThanOrEqualTo(0, create);
        Assert.IsLessThan(create, drop);
    }

    // ── JSON members: HasJsonPropertyName on a nested complex property, keys with digits or quotes, interval ──

    private class JsonContext(DbContextOptions<JsonContext> o) : GrantContext(o)
    {
        protected override void Configure(EntityTypeBuilder<Grant> b)
        {
            b.HasComplexIndex(x => x.Profile.Address.City, indexName: "ix_addr_city");
            b.HasComplexIndex(x => x.Profile.Address2.City, indexName: "ix_address2_city");
            b.HasComplexIndex(x => x.Profile.Quoted.City, indexName: "ix_quoted_city");
            b.HasComplexIndex(x => x.Profile.Duration, isUnique: true, indexName: "ux_duration");
        }
    }

    private static string PartSql(string indexName)
    {
        var create = Create<JsonContext>().OfType<CreateIndexOperation>().Single(o => o.Name == indexName);
        return Assert.ContainsSingle(IndexPartsSerializer.Deserialize((string)create[ComplexIndexAnnotations.IndexParts]!)).Value;
    }

    // Seed: the nested complex property's path segment ignores HasJsonPropertyName. The index then
    // reads a key the document does not have, and no query uses it.
    [TestMethod(DisplayName = "A nested complex property's JSON name is used in the path")]
    public void Nested_complex_property_honors_its_json_name()
        => Assert.AreEqual("\"profile\" #>> '{addr,City}'", PartSql("ix_addr_city"));

    // Seed: char.IsAsciiLetter instead of IsAsciiLetterOrDigit. EF Core's queries read the member as
    // '{Address2,City}'; the ARRAY form would never match them.
    [TestMethod(DisplayName = "A JSON key with a digit keeps the '{A,B}' path form")]
    public void Json_key_with_digit_keeps_the_literal_path_form()
        => Assert.AreEqual("\"profile\" #>> '{Address2,City}'", PartSql("ix_address2_city"));

    // Seed: no '' doubling in the ARRAY[…] form.
    [TestMethod(DisplayName = "A JSON key with a quote is escaped in the ARRAY path form")]
    public void Json_key_with_quote_is_escaped()
        => Assert.AreEqual("\"profile\" #>> ARRAY['Quoted','o''clock']::text[]", PartSql("ix_quoted_city"));

    // Seed: "interval" removed from StableTextInputTypes. interval_in is not IMMUTABLE, so the cast
    // cannot appear in an index: CREATE INDEX fails with 42P17.
    [TestMethod(DisplayName = "A TimeSpan JSON member stays text rather than casting to interval")]
    public void TimeSpan_member_is_not_cast_to_interval()
        => Assert.AreEqual("\"profile\" ->> 'Duration'", PartSql("ux_duration"));

    // ── Typed filters: a quote in a string literal, null on the left ──

    private class QuotedLiteralFilterContext(DbContextOptions<QuotedLiteralFilterContext> o) : GrantContext(o)
    {
        protected override void Configure(EntityTypeBuilder<Grant> b)
            => b.HasComplexIndex(x => x.Code, x => x.Code != "O'Brien", indexName: "ix_code_quoted");
    }

    private class NullOnTheLeftFilterContext(DbContextOptions<NullOnTheLeftFilterContext> o) : GrantContext(o)
    {
        protected override void Configure(EntityTypeBuilder<Grant> b)
            => b.HasComplexIndex(x => x.Code, x => null != x.RevokedAt, indexName: "ix_code_revoked");
    }

    // Seed: no '' doubling in typed string literals.
    [TestMethod(DisplayName = "A quote in a typed filter's string literal is doubled")]
    public void Typed_filter_literal_doubles_quotes()
        => Assert.AreEqual(
               "\"code\" <> 'O''Brien'",
               Create<QuotedLiteralFilterContext>().OfType<CreateIndexOperation>().Single().Filter);

    // Seed: `null != x.P` rendered IS NULL — the filter covers exactly the rows it should exclude.
    [TestMethod(DisplayName = "null != x.P renders IS NOT NULL")]
    public void Null_on_the_left_keeps_the_negation()
        => Assert.AreEqual(
               "\"revoked_at\" IS NOT NULL",
               Create<NullOnTheLeftFilterContext>().OfType<CreateIndexOperation>().Single().Filter);

    // ── Quote without "" doubling ──

    private class QuotedIdentifierContext(DbContextOptions<QuotedIdentifierContext> o) : GrantContext(o)
    {
        protected override void Configure(EntityTypeBuilder<Grant> b)
        {
            b.Property(x => x.RoomId).HasColumnName("room\"id");
            b.HasExclusionConstraint(x => x.RoomId, x => x.Period, name: "ex_\"room\"");
        }
    }

    [TestMethod(DisplayName = "A double quote in a constraint or column name is doubled")]
    public void Quote_doubles_double_quotes()
    {
        var sql = Assert.ContainsSingle(ExclusionSql(Create<QuotedIdentifierContext>()));

        StringAssert.Contains(sql, "ADD CONSTRAINT \"ex_\"\"room\"\"\" EXCLUDE USING gist (\"room\"\"id\" WITH =, \"period\" WITH &&)");
    }
}
