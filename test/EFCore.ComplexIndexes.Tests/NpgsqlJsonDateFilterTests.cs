using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// A date or time member of a <c>ToJson()</c> document resolves to its text, because the cast EF
/// Core's queries use is not IMMUTABLE and cannot appear in an index or constraint. Compared with a
/// column in a filter, that text met a typed column and the migration failed at apply
/// (<c>operator does not exist: text &lt; timestamp with time zone</c>, #119). The comparison is
/// refused at <c>migrations add</c>; what applied before still does.
/// </summary>
[TestClass]
public class NpgsqlJsonDateFilterTests
{
    public class Profile
    {
        public DateTime At   { get; set; }
        public int      Rank { get; set; }
    }

    public class Session
    {
        public int                   Id        { get; set; }
        public DateTime              ValidFrom { get; set; }
        public NpgsqlRange<DateTime> Period    { get; set; }
        public Profile               Profile   { get; set; } = new();
    }

    private static void Map(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Session> b)
    {
        b.ToTable("sessions");
        b.HasKey(x => x.Id);
        b.ComplexProperty(x => x.Profile, p => p.ToJson("profile"));
    }

    private class TypedComparisonContext(DbContextOptions<TypedComparisonContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Session>(b =>
            {
                Map(b);
                b.HasComplexIndex(x => x.Id, x => x.Profile.At < x.ValidFrom, indexName: "ix_at");
            });
    }

    private class ColumnFirstContext(DbContextOptions<ColumnFirstContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Session>(b =>
            {
                Map(b);
                b.HasComplexIndex(x => x.Id, isUnique: false, filter: "\"ValidFrom\" >= ({Profile.At})", indexName: "ix_at");
            });
    }

    // A snapshot 5.4.x scaffolded: the comparison was accepted then, at rendering version 2, the
    // first to resolve a JSON date member to its text.
    private class ColumnFirstSnapshotContext(DbContextOptions<ColumnFirstSnapshotContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Session>(b =>
            {
                Map(b);
                b.HasComplexIndex(x => x.Id, isUnique: false, filter: "\"ValidFrom\" >= ({Profile.At})", indexName: "ix_at");
            });
            modelBuilder.HasAnnotation(ComplexIndexAnnotations.RenderingVersion, 2);
        }
    }

    private class NoDeclarationContext(DbContextOptions<NoDeclarationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Session>(Map);
    }

    private class ExclusionContext(DbContextOptions<ExclusionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Session>(b =>
            {
                Map(b);
                b.HasExclusionConstraint(x => x.Id, x => x.Period, x => x.Profile.At != x.ValidFrom, name: "ex_at");
            });
    }

    private class StillAppliesContext(DbContextOptions<StillAppliesContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Session>(b =>
            {
                Map(b);
                b.HasComplexIndex(x => x.Id, isUnique: false, filter: "{Profile.At} IS NOT NULL", indexName: "ix_at_set");
                b.HasComplexIndex(x => x.Id, isUnique: false, filter: "{Profile.At} > '2026-01-01'", indexName: "ix_at_literal");
                b.HasComplexIndex(x => x.Id, x => x.Profile.Rank > x.Id, indexName: "ix_rank");
            });
    }

    [TestMethod(DisplayName = "A typed filter comparing a JSON date member with a column is refused at migrations add")]
    public void Typed_comparison_with_a_column_is_refused()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<TypedComparisonContext>()));

        StringAssert.Contains(exception.Message, "'Profile.At'");
        StringAssert.Contains(exception.Message, "IMMUTABLE");
    }

    [TestMethod(DisplayName = "A string filter comparing a column with a JSON date member is refused too")]
    public void Column_first_comparison_is_refused()
        => Assert.ThrowsExactly<InvalidOperationException>(
               () => MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<ColumnFirstContext>()));

    [TestMethod(DisplayName = "An exclusion constraint filter comparing a JSON date member with a column is refused")]
    public void Exclusion_filter_comparison_is_refused()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<ExclusionContext>()));

        StringAssert.Contains(exception.Message, "'Profile.At'");
    }

    [TestMethod(DisplayName = "A snapshot holding a refused index filter still diffs, so the index can be dropped")]
    public void Refused_index_filter_in_the_snapshot_is_dropped()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<ColumnFirstSnapshotContext>(),
            MigrationHarness.NpgsqlModel<NoDeclarationContext>());

        Assert.AreEqual("ix_at", operations.OfType<DropIndexOperation>().Single().Name);
    }

    [TestMethod(DisplayName = "A snapshot holding a refused exclusion filter still diffs, so the constraint can be dropped")]
    public void Refused_exclusion_filter_in_the_snapshot_is_dropped()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<ExclusionContext>(),
            MigrationHarness.NpgsqlModel<NoDeclarationContext>());

        Assert.IsTrue(
            operations.OfType<SqlOperation>().Any(o => o.Sql.Contains("DROP CONSTRAINT IF EXISTS \"ex_at\"")),
            "Expected the exclusion constraint to be dropped.");
    }

    [TestMethod(DisplayName = "A null test, a string literal and a typed member still resolve")]
    public void What_applied_before_still_resolves()
    {
        var filters = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<StillAppliesContext>())
                                      .OfType<CreateIndexOperation>()
                                      .ToDictionary(o => o.Name, o => o.Filter);

        Assert.AreEqual("(\"profile\" ->> 'At') IS NOT NULL", filters["ix_at_set"]);
        Assert.AreEqual("(\"profile\" ->> 'At') > '2026-01-01'", filters["ix_at_literal"]);
        Assert.AreEqual("(CAST(\"profile\" ->> 'Rank' AS integer)) > \"Id\"", filters["ix_rank"]);
    }
}
