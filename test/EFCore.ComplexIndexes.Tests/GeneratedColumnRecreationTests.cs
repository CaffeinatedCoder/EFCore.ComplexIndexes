using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// PostgreSQL cannot change a generated column's expression in place, so Npgsql renders an
/// <c>ALTER COLUMN</c> that changes it, its storage or a <c>tsvector</c> column's configuration as
/// <c>DROP COLUMN</c> plus <c>ADD COLUMN</c>, and re-creates only EF Core's own indexes on the column.
/// <c>DROP COLUMN</c> takes every index and constraint that reads the column with it. Until #184 the
/// declarations were unchanged, so the differs emitted nothing: the migration ran without an error,
/// the snapshot still listed the indexes and constraints, and the database had none of them. They
/// are now dropped before the base operations and created again after them.
/// </summary>
[TestClass]
public class GeneratedColumnRecreationTests
{
    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class Booking
    {
        public int                   Id     { get; set; }
        public int                   RoomId { get; set; }
        public string                Code   { get; set; } = "";
        public DateTime              Start  { get; set; }
        public DateTime              End    { get; set; }
        public string                Search { get; set; } = "";
        public NpgsqlRange<DateTime> During { get; set; }
        public NpgsqlTsVector        Words  { get; set; } = null!;
        public EmailAddress          Email  { get; set; } = new();
    }

    private class Stay
    {
        public int                   Id     { get; set; }
        public string                Code   { get; set; } = "";
        public DateTime              From   { get; set; }
        public DateTime              To     { get; set; }
        public NpgsqlRange<DateTime> Period { get; set; }
    }

    private abstract class BookingContext(DbContextOptions options) : DbContext(options)
    {
        protected virtual string  SearchSql    => "lower(\"Code\")";
        protected virtual string  DuringSql    => "tstzrange(\"Start\", \"End\")";
        protected virtual string  PeriodSql    => "tstzrange(\"From\", \"To\")";
        protected virtual string  WordsConfig  => "english";
        protected virtual string? RoomIdSql    => null;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Booking>(b =>
            {
                b.ToTable("bookings");
                b.Property(x => x.Search).HasComputedColumnSql(SearchSql, stored: true);
                b.Property(x => x.During).HasComputedColumnSql(DuringSql, stored: true);
                b.HasGeneratedTsVectorColumn(x => x.Words, WordsConfig, x => new { x.Code });
                if (RoomIdSql is { } roomId)
                    b.Property(x => x.RoomId).HasComputedColumnSql(roomId, stored: true);
                b.ComplexProperty(x => x.Email);

                b.HasComplexCompositeIndex(x => new { x.Search, x.Email.Value }, indexName: "ix_bookings_search_email");
                b.HasComplexIndex(x => x.Code, filter: "{Search} <> ''", indexName: "ix_bookings_code_searchable");
                b.HasComplexIndex(x => x.Words, ix => ix.HasName("ix_bookings_words").UseGin());
                b.HasComplexIndex(x => x.RoomId, indexName: "ix_bookings_room");
                b.HasComplexIndex(x => x.Email.Value, indexName: "ix_bookings_email");

                b.HasExclusionConstraint(x => x.RoomId, x => x.During, name: "ex_bookings_room_during");
                b.HasExclusionConstraint(ex => ex.WithEquality(x => x.Email.Value).WithExpression("tstzrange(\"Start\", \"End\")", "&&").HasName("ex_bookings_email"));
                b.HasTemporalConstraint(x => x.Code, x => x.During, name: "tc_bookings_code");
            });

            modelBuilder.Entity<Stay>(b =>
            {
                b.ToTable("stays");
                b.Property(x => x.Period).HasComputedColumnSql(PeriodSql, stored: true);
                b.HasTemporalForeignKey<Stay, Booking>(x => x.Code, x => x.Period, x => x.Code, x => x.During, name: "fk_stays_booking");
            });
        }
    }

    private class Before(DbContextOptions<Before> options) : BookingContext(options);

    private class SearchChanged(DbContextOptions<SearchChanged> options) : BookingContext(options)
    {
        protected override string SearchSql => "upper(\"Code\")";
    }

    private class DuringChanged(DbContextOptions<DuringChanged> options) : BookingContext(options)
    {
        protected override string DuringSql => "tstzrange(\"Start\", \"End\", '[]')";
    }

    private class PeriodChanged(DbContextOptions<PeriodChanged> options) : BookingContext(options)
    {
        protected override string PeriodSql => "tstzrange(\"From\", \"To\", '[]')";
    }

    private class WordsChanged(DbContextOptions<WordsChanged> options) : BookingContext(options)
    {
        protected override string WordsConfig => "simple";
    }

    private class RoomIdGenerated(DbContextOptions<RoomIdGenerated> options) : BookingContext(options)
    {
        protected override string RoomIdSql => "length(\"Code\")";
    }

    private static List<MigrationOperation> Diff<TTarget>() where TTarget : DbContext
        => MigrationHarness.NpgsqlDiff(MigrationHarness.NpgsqlModel<Before>(), MigrationHarness.NpgsqlModel<TTarget>()).ToList();

    // The names this package's operations drop, in order, and the SQL they add, by name.
    private static string[] Dropped(List<MigrationOperation> operations, int before)
        => [.. operations.Take(before).Select(o => o switch
           {
               DropIndexOperation drop            => drop.Name,
               DropUniqueConstraintOperation drop => drop.Name,
               DropForeignKeyOperation drop       => drop.Name,
               SqlOperation sql when sql.Sql.Contains("DROP CONSTRAINT IF EXISTS") => sql.Sql.Split('"')[^2],
               _                                  => null
           }).OfType<string>()];

    private static string[] Created(List<MigrationOperation> operations, int after)
        => [.. operations.Skip(after + 1).Select(o => o switch
           {
               CreateIndexOperation create => create.Name,
               SqlOperation sql when sql.Sql.Contains("ADD CONSTRAINT") => sql.Sql.Split("ADD CONSTRAINT ")[1].Split('"')[1],
               _                           => null
           }).OfType<string>()];

    private static int AlterOf(List<MigrationOperation> operations, string table, string column)
        => operations.FindIndex(o => o is AlterColumnOperation alter && alter.Table == table && alter.Name == column);

    [TestMethod]
    public void A_changed_expression_rebuilds_the_indexes_that_read_the_column_around_it()
    {
        var operations = Diff<SearchChanged>();
        var alter      = AlterOf(operations, "bookings", "Search");

        Assert.IsTrue(alter >= 0);
        CollectionAssert.AreEquivalent(new[] { "ix_bookings_search_email", "ix_bookings_code_searchable" }, Dropped(operations, alter));
        CollectionAssert.AreEquivalent(new[] { "ix_bookings_search_email", "ix_bookings_code_searchable" }, Created(operations, alter));
    }

    [TestMethod]
    public void A_changed_period_rebuilds_the_constraints_on_it_and_the_temporal_foreign_key_that_references_one()
    {
        var operations = Diff<DuringChanged>();
        var alter      = AlterOf(operations, "bookings", "During");

        Assert.IsTrue(alter >= 0);
        var dropped = Dropped(operations, alter);
        CollectionAssert.AreEquivalent(new[] { "fk_stays_booking", "tc_bookings_code", "ex_bookings_room_during" }, dropped);
        Assert.IsTrue(Array.IndexOf(dropped, "fk_stays_booking") < Array.IndexOf(dropped, "tc_bookings_code"),
                      "The foreign key goes before the constraint it references (2BP01).");

        var created = Created(operations, alter);
        CollectionAssert.AreEquivalent(new[] { "ex_bookings_room_during", "tc_bookings_code", "fk_stays_booking" }, created);
        Assert.IsTrue(Array.IndexOf(created, "tc_bookings_code") < Array.IndexOf(created, "fk_stays_booking"));
    }

    [TestMethod]
    public void A_changed_dependent_period_rebuilds_the_temporal_foreign_key_only()
    {
        var operations = Diff<PeriodChanged>();
        var alter      = AlterOf(operations, "stays", "Period");

        Assert.IsTrue(alter >= 0);
        CollectionAssert.AreEqual(new[] { "fk_stays_booking" }, Dropped(operations, alter));
        CollectionAssert.AreEqual(new[] { "fk_stays_booking" }, Created(operations, alter));
    }

    [TestMethod]
    public void A_changed_tsvector_configuration_rebuilds_the_index_on_the_column()
    {
        var operations = Diff<WordsChanged>();
        var alter      = AlterOf(operations, "bookings", "Words");

        Assert.IsTrue(alter >= 0);
        CollectionAssert.AreEqual(new[] { "ix_bookings_words" }, Dropped(operations, alter));
        CollectionAssert.AreEqual(new[] { "ix_bookings_words" }, Created(operations, alter));
    }

    [TestMethod]
    public void A_column_that_becomes_generated_rebuilds_what_reads_it()
    {
        var operations = Diff<RoomIdGenerated>();
        var alter      = AlterOf(operations, "bookings", "RoomId");

        Assert.IsTrue(alter >= 0);
        CollectionAssert.AreEquivalent(new[] { "ix_bookings_room", "ex_bookings_room_during" }, Dropped(operations, alter));
        CollectionAssert.AreEquivalent(new[] { "ix_bookings_room", "ex_bookings_room_during" }, Created(operations, alter));
    }
}
