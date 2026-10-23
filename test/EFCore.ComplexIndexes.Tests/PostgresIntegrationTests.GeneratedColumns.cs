using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

public partial class PostgresIntegrationTests
{
    // ── A changed generation expression keeps what reads the column (#184) ──

    // Npgsql renders the change as DROP COLUMN plus ADD COLUMN, which takes every index and
    // constraint on the column with it, and re-creates only EF Core's own indexes.

    private class GeneratedBooking
    {
        public int                   Id     { get; set; }
        public int                   RoomId { get; set; }
        public string                Code   { get; set; } = "";
        public DateTime              Start  { get; set; }
        public DateTime              End    { get; set; }
        public string                Search { get; set; } = "";
        public NpgsqlRange<DateTime> During { get; set; }
    }

    private class GeneratedStay
    {
        public int                   Id     { get; set; }
        public string                Code   { get; set; } = "";
        public NpgsqlRange<DateTime> Period { get; set; }
    }

    private abstract class GeneratedColumnContext(DbContextOptions options, string searchSql, string duringSql) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<GeneratedBooking>(b =>
            {
                b.ToTable("gc_bookings");
                b.Property(x => x.Search).HasComputedColumnSql(searchSql, stored: true);
                b.Property(x => x.During).HasComputedColumnSql(duringSql, stored: true);
                b.HasIndex(x => x.Search).HasDatabaseName("gc_ix_native_search");

                b.HasComplexIndex(x => x.Search, isUnique: true, indexName: "gc_ux_search");
                b.HasExclusionConstraint(x => x.RoomId, x => x.During, name: "gc_ex_room_during");
                b.HasTemporalConstraint(x => x.Code, x => x.During, name: "gc_uq_code_during");
            });

            modelBuilder.Entity<GeneratedStay>(b =>
            {
                b.ToTable("gc_stays");
                b.HasTemporalForeignKey<GeneratedStay, GeneratedBooking>(x => x.Code, x => x.Period, x => x.Code, x => x.During, name: "gc_fk_stays");
            });
        }
    }

    private class GeneratedColumnV1Context(DbContextOptions<GeneratedColumnV1Context> options)
        : GeneratedColumnContext(options, "lower(\"Code\")", "tstzrange(\"Start\", \"End\")");

    private class GeneratedColumnV2Context(DbContextOptions<GeneratedColumnV2Context> options)
        : GeneratedColumnContext(options, "upper(\"Code\")", "tstzrange(\"Start\", \"End\", '[]')");

    [TestMethod(DisplayName = "A changed generation expression keeps the indexes and constraints on the column (#184)")]
    public void Changed_generation_expression_keeps_indexes_and_constraints()
    {
        var v1 = BuildRelationalModel<GeneratedColumnV1Context>();
        Apply(GetDifferences(null, v1));

        var v2 = BuildRelationalModel<GeneratedColumnV2Context>();
        ApplyInTransaction(GetDifferences(v1, v2), v2.Model);

        Assert.IsNotNull(RelationOid("gc_ix_native_search"), "EF Core's own index, which Npgsql re-creates.");
        Assert.IsNotNull(RelationOid("gc_ux_search"), "The complex index went with the column.");
        Assert.AreEqual("x", ConstraintType("gc_bookings", "gc_ex_room_during"));
        Assert.AreEqual("u", ConstraintType("gc_bookings", "gc_uq_code_during"));
        Assert.AreEqual("f", ConstraintType("gc_stays", "gc_fk_stays"));

        // And they enforce: the exclusion constraint over the new, closed ranges.
        Sql("INSERT INTO gc_bookings (\"Id\", \"RoomId\", \"Code\", \"Start\", \"End\") VALUES (1, 7, 'a', '2024-01-01Z', '2024-01-10Z')");
        var refused = Assert.ThrowsExactly<PostgresException>(
            () => Sql("INSERT INTO gc_bookings (\"Id\", \"RoomId\", \"Code\", \"Start\", \"End\") VALUES (2, 7, 'b', '2024-01-10Z', '2024-01-20Z')"));
        Assert.AreEqual(PostgresErrorCodes.ExclusionViolation, refused.SqlState); // 23P01
    }
}
