using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

public partial class PostgresIntegrationTests
{
    // ── A move into the default schema keeps what is on the table (#185) ──

    // Npgsql 10 renders nothing for a RenameTableOperation whose NewSchema is null, so the table
    // stays where it was. The differs read null as "unchanged", dropped the indexes and constraints
    // under the old schema and created them on a table of that name in the default schema: 42P01.

    private class SchemaMoveBooking
    {
        public int                   Id     { get; set; }
        public int                   RoomId { get; set; }
        public string                Code   { get; set; } = "";
        public NpgsqlRange<DateTime> Period { get; set; }
    }

    private abstract class SchemaMoveContext(DbContextOptions options, string? schema) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<SchemaMoveBooking>(b =>
            {
                b.ToTable("sm_bookings", schema);
                b.HasComplexIndex(x => x.Code, indexName: "sm_ix_code");
                b.HasExclusionConstraint(x => x.RoomId, x => x.Period, name: "sm_ex_room");
            });
    }

    private class SchemaMoveNorthContext(DbContextOptions<SchemaMoveNorthContext> options) : SchemaMoveContext(options, "sm_north");

    private class SchemaMoveDefaultContext(DbContextOptions<SchemaMoveDefaultContext> options) : SchemaMoveContext(options, null);

    [TestMethod(DisplayName = "A move into the default schema applies and keeps the index and constraint (#185)")]
    public void Move_into_default_schema_applies()
    {
        var north = BuildRelationalModel<SchemaMoveNorthContext>();
        Apply(GetDifferences(null, north));
        var index      = RelationOid("sm_ix_code");
        var constraint = ExclusionOid("sm_ex_room");

        var target = BuildRelationalModel<SchemaMoveDefaultContext>();
        ApplyInTransaction(GetDifferences(north, target), target.Model);

        Assert.AreEqual(index, RelationOid("sm_ix_code"), "The index was rebuilt.");
        Assert.AreEqual(constraint, ExclusionOid("sm_ex_room"), "The exclusion constraint was rebuilt.");
    }

    private static uint? ExclusionOid(string name)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand("SELECT oid FROM pg_constraint WHERE conname = @name AND contype = 'x'", connection);
        cmd.Parameters.AddWithValue("name", name);
        return (uint?)cmd.ExecuteScalar();
    }
}
