using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// A temporal foreign key whose drop and add differ only by name becomes <c>RENAME CONSTRAINT</c>,
/// placed after the base operations. Until #196 that path asked none of the questions the other
/// three rename paths ask, so a key in the way of a key-column retype (#188) or referencing a
/// rebuilt principal constraint (#187) was renamed instead of dropped first: 42804 or 2BP01 at
/// apply. A default name contains the principal table's name, so renaming that table is enough.
/// </summary>
[TestClass]
public class TemporalForeignKeyRenameTests
{
    private class Room
    {
        public int                   Id          { get; set; }
        public int                   RoomId      { get; set; }
        public DateOnly              From        { get; set; }
        public DateOnly              To          { get; set; }
        public NpgsqlRange<DateOnly> ValidDuring { get; set; }
    }

    private class Booking
    {
        public int                   Id           { get; set; }
        public int                   RoomId       { get; set; }
        public NpgsqlRange<DateOnly> BookedDuring { get; set; }
    }

    private abstract class KeyContext(DbContextOptions options) : DbContext(options)
    {
        protected virtual string  Rooms         => "a_rooms";
        protected virtual string? KeyType       => null;
        protected virtual string? ForeignKey    => null;
        protected virtual string  ValidDuring   => "daterange(\"From\", \"To\")";

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Room>(b =>
            {
                b.ToTable(Rooms);
                if (KeyType is not null)
                    b.Property(x => x.RoomId).HasColumnType(KeyType);
                b.Property(x => x.ValidDuring).HasComputedColumnSql(ValidDuring, stored: true);
                b.HasTemporalConstraint(x => x.RoomId, x => x.ValidDuring, name: "tc_rooms");
            });

            modelBuilder.Entity<Booking>(b =>
            {
                b.ToTable("z_bookings");
                if (KeyType is not null)
                    b.Property(x => x.RoomId).HasColumnType(KeyType);
                b.HasTemporalForeignKey<Booking, Room>(x => x.RoomId, x => x.BookedDuring, x => x.RoomId, x => x.ValidDuring, name: ForeignKey);
            });
        }
    }

    private class Before(DbContextOptions<Before> options) : KeyContext(options);

    private class RoomsRenamedAndRetyped(DbContextOptions<RoomsRenamedAndRetyped> options) : KeyContext(options)
    {
        protected override string  Rooms   => "c_rooms";
        protected override string? KeyType => "bigint";
    }

    private class KeyNamedAndRetyped(DbContextOptions<KeyNamedAndRetyped> options) : KeyContext(options)
    {
        protected override string? KeyType    => "bigint";
        protected override string? ForeignKey => "fk_bookings_room";
    }

    private class RoomsRenamedAndPeriodChanged(DbContextOptions<RoomsRenamedAndPeriodChanged> options) : KeyContext(options)
    {
        protected override string Rooms       => "c_rooms";
        protected override string ValidDuring => "daterange(\"From\", \"To\", '[]')";
    }

    private class RoomsRenamed(DbContextOptions<RoomsRenamed> options) : KeyContext(options)
    {
        protected override string Rooms => "c_rooms";
    }

    private static List<MigrationOperation> Diff<TTarget>() where TTarget : DbContext
        => MigrationHarness.NpgsqlDiff(MigrationHarness.NpgsqlModel<Before>(), MigrationHarness.NpgsqlModel<TTarget>()).ToList();

    private static string Listing(List<MigrationOperation> operations)
        => string.Join(Environment.NewLine, operations.Select(o => o is SqlOperation sql ? sql.Sql : o.GetType().Name));

    private static void AssertKeyRebuiltAround(List<MigrationOperation> operations, Func<MigrationOperation, bool> baseOperation)
    {
        var drop  = operations.FindIndex(o => o is DropForeignKeyOperation);
        var add   = operations.FindIndex(o => o is SqlOperation sql && sql.Sql.Contains("FOREIGN KEY"));
        var first = operations.FindIndex(o => baseOperation(o));
        var last  = operations.FindLastIndex(o => baseOperation(o));

        Assert.IsTrue(first >= 0, Listing(operations));
        Assert.IsTrue(drop >= 0 && drop < first, "The key is dropped before the base operations:" + Environment.NewLine + Listing(operations));
        Assert.IsTrue(add > last, "And added after them:" + Environment.NewLine + Listing(operations));
        Assert.IsFalse(operations.OfType<SqlOperation>().Any(o => o.Sql.Contains("RENAME CONSTRAINT \"FK_")), Listing(operations));
    }

    [TestMethod]
    public void A_principal_table_rename_with_a_key_retype_drops_the_key_first()
        => AssertKeyRebuiltAround(Diff<RoomsRenamedAndRetyped>(), o => o is AlterColumnOperation { Name: "RoomId" });

    [TestMethod]
    public void A_key_name_change_with_a_key_retype_drops_the_key_first()
    {
        var operations = Diff<KeyNamedAndRetyped>();
        AssertKeyRebuiltAround(operations, o => o is AlterColumnOperation { Name: "RoomId" });
        Assert.IsFalse(operations.OfType<SqlOperation>().Any(o => o.Sql.Contains("RENAME CONSTRAINT")), Listing(operations));
    }

    // The principal constraint is rebuilt around the period's DROP and ADD COLUMN (#184); the key,
    // renamed with the table, must go before the constraint does (2BP01).
    [TestMethod]
    public void A_principal_table_rename_with_a_rebuilt_principal_constraint_drops_the_key_first()
    {
        var operations = Diff<RoomsRenamedAndPeriodChanged>();
        var key        = operations.FindIndex(o => o is DropForeignKeyOperation);
        var constraint = operations.FindIndex(o => o is DropUniqueConstraintOperation { Name: "tc_rooms" });

        Assert.IsTrue(constraint >= 0, Listing(operations));
        Assert.IsTrue(key >= 0 && key < constraint, Listing(operations));
    }

    // Nothing in the way: the key keeps its catalog entry and is renamed.
    [TestMethod]
    public void A_principal_table_rename_alone_renames_the_key()
    {
        var operations = Diff<RoomsRenamed>();

        Assert.IsFalse(operations.OfType<DropForeignKeyOperation>().Any(), Listing(operations));
        Assert.IsTrue(operations.OfType<SqlOperation>().Any(o => o.Sql.Contains("RENAME CONSTRAINT \"FK_z_bookings_a_rooms_RoomId_BookedDuring\"")), Listing(operations));
    }
}
