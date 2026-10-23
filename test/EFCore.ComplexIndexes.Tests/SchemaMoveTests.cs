using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// EF Core's <c>RenameTableOperation</c> carries the target's schema in <c>NewSchema</c>, where
/// <c>null</c> is the default schema. The differs read <c>null</c> as "unchanged", so a table moved
/// into the default schema was compared under its old schema, and every complex index and
/// constraint on it was dropped and created again (#185). On SQL Server that is a needless rebuild;
/// on PostgreSQL, whose Npgsql 10 renders nothing for that move, the creates named a table that was
/// not there (42P01).
/// </summary>
[TestClass]
public class SchemaMoveTests
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
        public NpgsqlRange<DateTime> Period { get; set; }
        public EmailAddress          Email  { get; set; } = new();
    }

    private class Stay
    {
        public int                   Id     { get; set; }
        public string                Code   { get; set; } = "";
        public NpgsqlRange<DateTime> During { get; set; }
    }

    // ── PostgreSQL ────────────────────────────────────────────────────────────────────────────────

    private abstract class NpgsqlContext(DbContextOptions options, string? schema, string codeColumn = "Code") : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Booking>(b =>
            {
                b.ToTable("bookings", schema);
                b.Property(x => x.Code).HasColumnName(codeColumn);
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasComplexIndex(isUnique: true));
                b.HasComplexCompositeIndex(x => new { x.Code, x.Email.Value }, indexName: "ix_bookings_code_email");
                b.HasExclusionConstraint(x => x.RoomId, x => x.Period, name: "ex_bookings_room");
                b.HasTemporalConstraint(x => x.Code, x => x.Period, name: "tc_bookings_code");
            });

            modelBuilder.Entity<Stay>(b =>
            {
                b.ToTable("stays");
                b.HasTemporalForeignKey<Stay, Booking>(x => x.Code, x => x.During, x => x.Code, x => x.Period, name: "fk_stays_booking");
            });
        }
    }

    private class InNorth(DbContextOptions<InNorth> options) : NpgsqlContext(options, "north");

    private class InSouth(DbContextOptions<InSouth> options) : NpgsqlContext(options, "south");

    private class InDefault(DbContextOptions<InDefault> options) : NpgsqlContext(options, null);

    private class InDefaultRenamed(DbContextOptions<InDefaultRenamed> options) : NpgsqlContext(options, null, "booking_code");

    private static void AssertOnlyMoves(IReadOnlyList<MigrationOperation> operations, params Type[] allowed)
    {
        var others = operations.Where(o => !allowed.Contains(o.GetType())).Select(o => o is SqlOperation sql ? sql.Sql : o.GetType().Name).ToList();
        Assert.AreEqual(0, others.Count, string.Join(Environment.NewLine, others));
    }

    [TestMethod]
    public void PostgreSQL_move_into_the_default_schema_keeps_everything()
        => AssertOnlyMoves(
            MigrationHarness.NpgsqlDiff(MigrationHarness.NpgsqlModel<InNorth>(), MigrationHarness.NpgsqlModel<InDefault>()),
            typeof(RenameTableOperation));

    [TestMethod]
    public void PostgreSQL_move_into_the_default_schema_with_a_column_rename_renames_the_column_only()
    {
        var operations = MigrationHarness.NpgsqlDiff(MigrationHarness.NpgsqlModel<InNorth>(), MigrationHarness.NpgsqlModel<InDefaultRenamed>());

        AssertOnlyMoves(operations, typeof(RenameTableOperation), typeof(RenameColumnOperation));
        Assert.AreEqual("booking_code", operations.OfType<RenameColumnOperation>().Single().NewName);
    }

    // The moves that already worked, so the fix does not turn them around.
    [TestMethod]
    public void PostgreSQL_move_between_named_schemas_keeps_everything()
        => AssertOnlyMoves(
            MigrationHarness.NpgsqlDiff(MigrationHarness.NpgsqlModel<InNorth>(), MigrationHarness.NpgsqlModel<InSouth>()),
            typeof(EnsureSchemaOperation), typeof(RenameTableOperation));

    [TestMethod]
    public void PostgreSQL_move_out_of_the_default_schema_keeps_everything()
        => AssertOnlyMoves(
            MigrationHarness.NpgsqlDiff(MigrationHarness.NpgsqlModel<InDefault>(), MigrationHarness.NpgsqlModel<InNorth>()),
            typeof(EnsureSchemaOperation), typeof(RenameTableOperation));

    // ── SQL Server ────────────────────────────────────────────────────────────────────────────────

    private abstract class SqlServerContext(DbContextOptions options, string? schema) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Booking>(b =>
            {
                b.ToTable("bookings", schema);
                b.Ignore(x => x.Period);
                b.Property(x => x.Code).HasMaxLength(50);
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasMaxLength(200).HasComplexIndex(isUnique: true));
                b.HasComplexCompositeIndex(x => new { x.Code, x.Email.Value }, indexName: "ix_bookings_code_email");
            });
    }

    private class SqlServerInNorth(DbContextOptions<SqlServerInNorth> options) : SqlServerContext(options, "north");

    private class SqlServerInDefault(DbContextOptions<SqlServerInDefault> options) : SqlServerContext(options, null);

    [TestMethod]
    public void SqlServer_move_into_the_default_schema_keeps_the_indexes()
        => AssertOnlyMoves(
            MigrationHarness.SqlServerDiff(MigrationHarness.SqlServerModel<SqlServerInNorth>(), MigrationHarness.SqlServerModel<SqlServerInDefault>()),
            typeof(RenameTableOperation));
}
