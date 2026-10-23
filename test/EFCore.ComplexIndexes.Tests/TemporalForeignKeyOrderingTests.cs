using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Two ways a base operation met a temporal foreign key the differ had left in place.
/// <list type="bullet">
/// <item>A key whose dependent table the base operations drop goes with the table, so its drop was
/// left out — also when its principal constraint is dropped or changed, which runs before the base
/// operations and fails while the key still references it (2BP01, #187).</item>
/// <item>A key column's type changed on both tables: PostgreSQL re-checks the key after each
/// <c>ALTER COLUMN</c>, and a GiST key has no cross-type operator, so altering the dependent first
/// failed (42804, #188). The key is now dropped before the base operations and added after.</item>
/// </list>
/// </summary>
[TestClass]
public class TemporalForeignKeyOrderingTests
{
    private class Room<TKey>
    {
        public int                   Id          { get; set; }
        public TKey                  RoomId      { get; set; } = default!;
        public int                   Floor       { get; set; }
        public long                  Capacity    { get; set; }
        public NpgsqlRange<DateOnly> ValidDuring { get; set; }
    }

    private class Booking<TKey>
    {
        public int                   Id           { get; set; }
        public TKey                  RoomId       { get; set; } = default!;
        public NpgsqlRange<DateOnly> BookedDuring { get; set; }
    }

    // ── #187: the dependent table is dropped, the principal constraint goes or changes ──

    private abstract class DropContext(DbContextOptions options) : DbContext(options)
    {
        protected virtual bool WithBookings => true;
        protected virtual bool WithConstraint => true;
        protected virtual bool OnFloorToo => false;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Room<int>>(b =>
            {
                b.ToTable("z_rooms");
                if (OnFloorToo)
                    b.HasTemporalConstraint(x => new { x.RoomId, x.Floor }, x => x.ValidDuring, name: "tc_rooms");
                else if (WithConstraint)
                    b.HasTemporalConstraint(x => x.RoomId, x => x.ValidDuring, name: "tc_rooms");
            });

            if (WithBookings)
                modelBuilder.Entity<Booking<int>>(b =>
                {
                    b.ToTable("a_bookings");
                    b.HasTemporalForeignKey<Booking<int>, Room<int>>(x => x.RoomId, x => x.BookedDuring, x => x.RoomId, x => x.ValidDuring, name: "fk_bookings_room");
                });
        }
    }

    private class DropBefore(DbContextOptions<DropBefore> options) : DropContext(options);

    private class DropBoth(DbContextOptions<DropBoth> options) : DropContext(options)
    {
        protected override bool WithBookings => false;
        protected override bool WithConstraint => false;
    }

    private class DropBookingsChangeConstraint(DbContextOptions<DropBookingsChangeConstraint> options) : DropContext(options)
    {
        protected override bool WithBookings => false;
        protected override bool OnFloorToo => true;
    }

    private class DropBookingsOnly(DbContextOptions<DropBookingsOnly> options) : DropContext(options)
    {
        protected override bool WithBookings => false;
    }

    private static void AssertForeignKeyDroppedFirst(List<MigrationOperation> operations)
    {
        var foreignKey = operations.FindIndex(o => o is DropForeignKeyOperation { Name: "fk_bookings_room" });
        var constraint = operations.FindIndex(o => o is DropUniqueConstraintOperation { Name: "tc_rooms" });
        var table      = operations.FindIndex(o => o is DropTableOperation { Name: "a_bookings" });

        Assert.IsTrue(foreignKey >= 0, "The foreign key is dropped by name.");
        Assert.IsTrue(foreignKey < constraint && constraint < table,
                      string.Join(", ", operations.Select(o => o.GetType().Name)));
    }

    [TestMethod]
    public void Dropping_the_dependent_table_and_the_principal_constraint_drops_the_key_first()
        => AssertForeignKeyDroppedFirst(
            MigrationHarness.NpgsqlDiff(MigrationHarness.NpgsqlModel<DropBefore>(), MigrationHarness.NpgsqlModel<DropBoth>()).ToList());

    [TestMethod]
    public void Dropping_the_dependent_table_and_changing_the_principal_constraint_drops_the_key_first()
        => AssertForeignKeyDroppedFirst(
            MigrationHarness.NpgsqlDiff(MigrationHarness.NpgsqlModel<DropBefore>(), MigrationHarness.NpgsqlModel<DropBookingsChangeConstraint>()).ToList());

    // The key still goes with its table when nothing else needs it gone.
    [TestMethod]
    public void Dropping_the_dependent_table_alone_leaves_the_key_to_the_table()
    {
        var operations = MigrationHarness.NpgsqlDiff(MigrationHarness.NpgsqlModel<DropBefore>(), MigrationHarness.NpgsqlModel<DropBookingsOnly>());

        Assert.IsFalse(operations.OfType<DropForeignKeyOperation>().Any());
        Assert.IsFalse(operations.OfType<DropUniqueConstraintOperation>().Any());
    }

    // ── #188: the key's type changes on both tables ──

    private abstract class KeyTypeContext<TKey>(DbContextOptions options) : DbContext(options)
    {
        protected virtual bool CapacityAsInt => false;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Room<TKey>>(b =>
            {
                b.ToTable("a_rooms");
                if (CapacityAsInt)
                    b.Property(x => x.Capacity).HasColumnType("integer");
                b.HasTemporalConstraint(x => x.RoomId, x => x.ValidDuring, name: "tc_rooms");
            });

            modelBuilder.Entity<Booking<TKey>>(b =>
            {
                b.ToTable("z_bookings");
                b.HasTemporalForeignKey<Booking<TKey>, Room<TKey>>(x => x.RoomId, x => x.BookedDuring, x => x.RoomId, x => x.ValidDuring, name: "fk_bookings_room");
            });
        }
    }

    private class IntKeys(DbContextOptions<IntKeys> options) : KeyTypeContext<int>(options);

    private class LongKeys(DbContextOptions<LongKeys> options) : KeyTypeContext<long>(options);

    private class IntKeysCapacityRetyped(DbContextOptions<IntKeysCapacityRetyped> options) : KeyTypeContext<int>(options)
    {
        protected override bool CapacityAsInt => true;
    }

    [TestMethod]
    public void A_key_type_change_rebuilds_the_key_around_the_column_changes()
    {
        var operations = MigrationHarness.NpgsqlDiff(MigrationHarness.NpgsqlModel<IntKeys>(), MigrationHarness.NpgsqlModel<LongKeys>()).ToList();

        var drop   = operations.FindIndex(o => o is DropForeignKeyOperation { Name: "fk_bookings_room" });
        var add    = operations.FindIndex(o => o is SqlOperation sql && sql.Sql.Contains("ADD CONSTRAINT \"fk_bookings_room\""));
        var alters = operations.Select((o, i) => (o, i)).Where(p => p.o is AlterColumnOperation { Name: "RoomId" }).Select(p => p.i).ToList();

        Assert.AreEqual(2, alters.Count);
        Assert.IsTrue(drop >= 0 && drop < alters.Min(), "The key is dropped before either column changes.");
        Assert.IsTrue(add > alters.Max(), "And added after both.");

        // The principal constraint's index follows its column; PostgreSQL rebuilds it in place.
        Assert.IsFalse(operations.OfType<DropUniqueConstraintOperation>().Any());
    }

    [TestMethod]
    public void A_type_change_on_a_column_the_key_does_not_read_leaves_the_key_alone()
    {
        var operations = MigrationHarness.NpgsqlDiff(MigrationHarness.NpgsqlModel<IntKeys>(), MigrationHarness.NpgsqlModel<IntKeysCapacityRetyped>());

        Assert.IsTrue(operations.OfType<AlterColumnOperation>().Any(o => o.Name == "Capacity"));
        Assert.IsFalse(operations.OfType<DropForeignKeyOperation>().Any());
        Assert.IsFalse(operations.OfType<SqlOperation>().Any(o => o.Sql.Contains("ADD CONSTRAINT")));
    }
}
