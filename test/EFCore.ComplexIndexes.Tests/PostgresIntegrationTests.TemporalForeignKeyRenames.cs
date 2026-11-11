using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

public partial class PostgresIntegrationTests
{
    // ── A renamed temporal foreign key in the way of a key retype (#196) ──

    private class TrRoom
    {
        public int                   Id          { get; set; }
        public int                   RoomId      { get; set; }
        public NpgsqlRange<DateOnly> ValidDuring { get; set; }
    }

    private class TrBooking
    {
        public int                   Id           { get; set; }
        public int                   RoomId       { get; set; }
        public NpgsqlRange<DateOnly> BookedDuring { get; set; }
    }

    private abstract class TemporalKeyRenameContext(DbContextOptions options) : DbContext(options)
    {
        protected abstract string  Rooms      { get; }
        protected abstract string  Bookings   { get; }
        protected virtual  string? KeyType    => null;
        protected virtual  string? ForeignKey => null;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TrRoom>(b =>
            {
                b.ToTable(Rooms);
                if (KeyType is not null)
                    b.Property(x => x.RoomId).HasColumnType(KeyType);
                b.HasTemporalConstraint(x => x.RoomId, x => x.ValidDuring);
            });

            modelBuilder.Entity<TrBooking>(b =>
            {
                b.ToTable(Bookings);
                if (KeyType is not null)
                    b.Property(x => x.RoomId).HasColumnType(KeyType);
                b.HasTemporalForeignKey<TrBooking, TrRoom>(x => x.RoomId, x => x.BookedDuring, x => x.RoomId, x => x.ValidDuring, name: ForeignKey);
            });
        }
    }

    // The principal table is renamed, so the default key name changes with it.
    private class TrTableV1Context(DbContextOptions<TrTableV1Context> options) : TemporalKeyRenameContext(options)
    {
        protected override string Rooms    => "tr1_a_rooms";
        protected override string Bookings => "tr1_b_bookings";
    }

    private class TrTableV2Context(DbContextOptions<TrTableV2Context> options) : TemporalKeyRenameContext(options)
    {
        protected override string  Rooms    => "tr1_c_rooms";
        protected override string  Bookings => "tr1_b_bookings";
        protected override string? KeyType  => "bigint";
    }

    // The key is given an explicit name.
    private class TrNameV1Context(DbContextOptions<TrNameV1Context> options) : TemporalKeyRenameContext(options)
    {
        protected override string Rooms    => "tr2_a_rooms";
        protected override string Bookings => "tr2_z_bookings";
    }

    private class TrNameV2Context(DbContextOptions<TrNameV2Context> options) : TemporalKeyRenameContext(options)
    {
        protected override string  Rooms      => "tr2_a_rooms";
        protected override string  Bookings   => "tr2_z_bookings";
        protected override string? KeyType    => "bigint";
        protected override string? ForeignKey => "tr2_fk_bookings";
    }

    [TestMethod(DisplayName = "A temporal foreign key renamed with a key retype applies, and the key still holds (#196)")]
    public void Renamed_temporal_foreign_key_with_key_retype_applies()
    {
        ApplyRenameAndRetype<TrTableV1Context, TrTableV2Context>("tr1_a_rooms", "tr1_c_rooms", "tr1_b_bookings", "FK_tr1_b_bookings_tr1_c_rooms_RoomId_BookedDuring");
        ApplyRenameAndRetype<TrNameV1Context, TrNameV2Context>("tr2_a_rooms", "tr2_a_rooms", "tr2_z_bookings", "tr2_fk_bookings");
    }

    private static void ApplyRenameAndRetype<TBefore, TAfter>(string roomsBefore, string roomsAfter, string bookings, string foreignKey)
        where TBefore : DbContext where TAfter : DbContext
    {
        var v1 = BuildRelationalModel<TBefore>();
        Apply(GetDifferences(null, v1));
        Sql($"INSERT INTO {roomsBefore} (\"Id\", \"RoomId\", \"ValidDuring\") VALUES (1, 7, '[2024-01-01,2025-01-01)')");
        Sql($"INSERT INTO {bookings} (\"Id\", \"RoomId\", \"BookedDuring\") VALUES (1, 7, '[2024-03-01,2024-03-08)')");

        var v2 = BuildRelationalModel<TAfter>();
        ApplyInTransaction(GetDifferences(v1, v2), v2.Model);

        Assert.AreEqual("f", ConstraintType(bookings, foreignKey), $"{foreignKey} on {bookings} referencing {roomsAfter}");

        var orphan = Assert.ThrowsExactly<PostgresException>(
            () => Sql($"INSERT INTO {bookings} (\"Id\", \"RoomId\", \"BookedDuring\") VALUES (2, 8, '[2024-03-01,2024-03-08)')"));
        Assert.AreEqual(PostgresErrorCodes.ForeignKeyViolation, orphan.SqlState); // 23503
    }
}
