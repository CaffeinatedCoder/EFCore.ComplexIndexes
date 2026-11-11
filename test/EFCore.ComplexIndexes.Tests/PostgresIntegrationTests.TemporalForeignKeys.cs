using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

public partial class PostgresIntegrationTests
{
    // ── Base operations around a temporal foreign key (#187, #188) ──

    private class TkRoom<TKey>
    {
        public int                   Id          { get; set; }
        public TKey                  RoomId      { get; set; } = default!;
        public NpgsqlRange<DateOnly> ValidDuring { get; set; }
    }

    private class TkBooking<TKey>
    {
        public int                   Id           { get; set; }
        public TKey                  RoomId       { get; set; } = default!;
        public NpgsqlRange<DateOnly> BookedDuring { get; set; }
    }

    private abstract class TemporalKeyContext<TKey>(DbContextOptions options) : DbContext(options)
    {
        protected abstract string Rooms    { get; }
        protected abstract string Bookings { get; }
        protected virtual  bool   WithBookingsAndConstraint => true;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TkRoom<TKey>>(b =>
            {
                b.ToTable(Rooms);
                if (WithBookingsAndConstraint)
                    b.HasTemporalConstraint(x => x.RoomId, x => x.ValidDuring);
            });

            if (WithBookingsAndConstraint)
                modelBuilder.Entity<TkBooking<TKey>>(b =>
                {
                    b.ToTable(Bookings);
                    b.HasTemporalForeignKey<TkBooking<TKey>, TkRoom<TKey>>(x => x.RoomId, x => x.BookedDuring, x => x.RoomId, x => x.ValidDuring);
                });
        }
    }

    // #187: the dependent sorts first, so EF Core drops its table after the principal constraint.

    private class TkDropV1Context(DbContextOptions<TkDropV1Context> options) : TemporalKeyContext<int>(options)
    {
        protected override string Rooms    => "tk1_z_rooms";
        protected override string Bookings => "tk1_a_bookings";
    }

    private class TkDropV2Context(DbContextOptions<TkDropV2Context> options) : TemporalKeyContext<int>(options)
    {
        protected override string Rooms    => "tk1_z_rooms";
        protected override string Bookings => "tk1_a_bookings";
        protected override bool   WithBookingsAndConstraint => false;
    }

    [TestMethod(DisplayName = "Dropping a temporal foreign key's dependent table with the principal constraint applies (#187)")]
    public void Dropping_dependent_table_and_principal_constraint_applies()
    {
        var v1 = BuildRelationalModel<TkDropV1Context>();
        Apply(GetDifferences(null, v1));

        var v2 = BuildRelationalModel<TkDropV2Context>();
        ApplyInTransaction(GetDifferences(v1, v2), v2.Model);

        Assert.IsNull(RelationOid("tk1_a_bookings"));
        Assert.IsNull(ConstraintType("tk1_z_rooms", "AK_tk1_z_rooms_RoomId_ValidDuring"));
    }

    // #188: the key's type changes on both tables, in either order of the table names.

    private class TkTypeAV1Context(DbContextOptions<TkTypeAV1Context> options) : TemporalKeyContext<int>(options)
    {
        protected override string Rooms    => "tk2_a_rooms";
        protected override string Bookings => "tk2_z_bookings";
    }

    private class TkTypeAV2Context(DbContextOptions<TkTypeAV2Context> options) : TemporalKeyContext<long>(options)
    {
        protected override string Rooms    => "tk2_a_rooms";
        protected override string Bookings => "tk2_z_bookings";
    }

    private class TkTypeZV1Context(DbContextOptions<TkTypeZV1Context> options) : TemporalKeyContext<int>(options)
    {
        protected override string Rooms    => "tk3_z_rooms";
        protected override string Bookings => "tk3_a_bookings";
    }

    private class TkTypeZV2Context(DbContextOptions<TkTypeZV2Context> options) : TemporalKeyContext<long>(options)
    {
        protected override string Rooms    => "tk3_z_rooms";
        protected override string Bookings => "tk3_a_bookings";
    }

    [TestMethod(DisplayName = "A key type change on both sides of a temporal foreign key applies, and the key still holds (#188)")]
    public void Key_type_change_on_both_sides_applies()
    {
        ApplyKeyTypeChange<TkTypeAV1Context, TkTypeAV2Context>("tk2_a_rooms", "tk2_z_bookings");
        ApplyKeyTypeChange<TkTypeZV1Context, TkTypeZV2Context>("tk3_z_rooms", "tk3_a_bookings");
    }

    private static void ApplyKeyTypeChange<TBefore, TAfter>(string rooms, string bookings)
        where TBefore : DbContext where TAfter : DbContext
    {
        var v1 = BuildRelationalModel<TBefore>();
        Apply(GetDifferences(null, v1));
        Sql($"INSERT INTO {rooms} (\"Id\", \"RoomId\", \"ValidDuring\") VALUES (1, 7, '[2024-01-01,2025-01-01)')");
        Sql($"INSERT INTO {bookings} (\"Id\", \"RoomId\", \"BookedDuring\") VALUES (1, 7, '[2024-03-01,2024-03-08)')");

        var v2 = BuildRelationalModel<TAfter>();
        ApplyInTransaction(GetDifferences(v1, v2), v2.Model);

        var foreignKey = $"FK_{bookings}_{rooms}_RoomId_BookedDuring";
        Assert.AreEqual("f", ConstraintType(bookings, foreignKey), $"{foreignKey} on {bookings}");

        var orphan = Assert.ThrowsExactly<PostgresException>(
            () => Sql($"INSERT INTO {bookings} (\"Id\", \"RoomId\", \"BookedDuring\") VALUES (2, 8, '[2024-03-01,2024-03-08)')"));
        Assert.AreEqual(PostgresErrorCodes.ForeignKeyViolation, orphan.SqlState); // 23503
    }
}
