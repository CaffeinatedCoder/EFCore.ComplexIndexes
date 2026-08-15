using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

[TestClass]
public class NpgsqlTemporalConstraintDifferTests
{
    private const string CreateExtensionPrefix = "CREATE EXTENSION IF NOT EXISTS btree_gist";
    private const string DefaultName           = "AK_room_bookings_room_id_booked_during";

    // ── Helpers ──

    private static IRelationalModel BuildRelationalModel<TContext>() where TContext : DbContext
        => MigrationHarness.NpgsqlModel<TContext>();

    private static IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
        => MigrationHarness.NpgsqlDiff(source, target);

    // The temporal constraint DDL is rendered at design time, so it shows up as a plain SqlOperation.
    private static List<string> TemporalSql(IEnumerable<MigrationOperation> operations)
        => [.. operations.OfType<SqlOperation>().Select(o => o.Sql).Where(s => s.Contains("WITHOUT OVERLAPS"))];

    private class EmptyContext(DbContextOptions options) : DbContext(options);

    // The period is a real range column (NpgsqlRange<DateOnly>) — a plain mapped column, never a key.
    private class RoomBooking
    {
        public int                   Id           { get; set; }
        public int                   RoomId       { get; set; }
        public NpgsqlRange<DateOnly> BookedDuring { get; set; }
    }

    private static void MapBooking(EntityTypeBuilder<RoomBooking> b)
    {
        b.ToTable("room_bookings");
        b.HasKey(x => x.Id);                                  // surrogate PK for EF tracking
        b.Property(x => x.RoomId).HasColumnName("room_id");
        b.Property(x => x.BookedDuring).HasColumnName("booked_during");
    }

    private class PlainContext(DbContextOptions<PlainContext> options) : DbContext(options)
    {
        public DbSet<RoomBooking> Bookings => Set<RoomBooking>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RoomBooking>(MapBooking);
    }

    private class TemporalContext(DbContextOptions<TemporalContext> options) : DbContext(options)
    {
        public DbSet<RoomBooking> Bookings => Set<RoomBooking>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RoomBooking>(b =>
            {
                MapBooking(b);
                b.HasTemporalConstraint(x => x.RoomId, x => x.BookedDuring);
            });
    }

    private class TemporalSuppressedContext(DbContextOptions<TemporalSuppressedContext> options) : DbContext(options)
    {
        public DbSet<RoomBooking> Bookings => Set<RoomBooking>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.SuppressTemporalExtensionAutoInjection();
            modelBuilder.Entity<RoomBooking>(b =>
            {
                MapBooking(b);
                b.HasTemporalConstraint(x => x.RoomId, x => x.BookedDuring);
            });
        }
    }

    private class TemporalWithExtensionContext(DbContextOptions<TemporalWithExtensionContext> options) : DbContext(options)
    {
        public DbSet<RoomBooking> Bookings => Set<RoomBooking>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.UseBtreeGist();
            modelBuilder.Entity<RoomBooking>(b =>
            {
                MapBooking(b);
                b.HasTemporalConstraint(x => x.RoomId, x => x.BookedDuring);
            });
        }
    }

    private class TemporalNamedMultiKeyContext(DbContextOptions<TemporalNamedMultiKeyContext> options) : DbContext(options)
    {
        public DbSet<RoomBooking> Bookings => Set<RoomBooking>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RoomBooking>(b =>
            {
                MapBooking(b);
                b.HasTemporalConstraint(x => new { x.Id, x.RoomId }, x => x.BookedDuring, name: "uq_booking_temporal");
            });
    }

    // Same default-named constraint as TemporalContext, on a renamed table.
    private class TemporalRenamedTableContext(DbContextOptions<TemporalRenamedTableContext> options) : DbContext(options)
    {
        public DbSet<RoomBooking> Bookings => Set<RoomBooking>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RoomBooking>(b =>
            {
                MapBooking(b);
                b.ToTable("bookings");
                b.HasTemporalConstraint(x => x.RoomId, x => x.BookedDuring);
            });
    }

    // ── Tests ──

    [TestMethod(DisplayName = "Default-named temporal constraint on a renamed table becomes RENAME CONSTRAINT")]
    public void Renamed_table_renames_default_named_temporal_constraint()
    {
        var operations = GetDifferences(
            source: BuildRelationalModel<TemporalContext>(),
            target: BuildRelationalModel<TemporalRenamedTableContext>()).ToList();

        Assert.IsNotEmpty(operations.OfType<RenameTableOperation>());
        Assert.IsEmpty(operations.OfType<DropUniqueConstraintOperation>());
        Assert.IsEmpty(TemporalSql(operations));

        var rename = Assert.ContainsSingle(
            operations.OfType<SqlOperation>().Where(o => o.Sql.Contains("RENAME CONSTRAINT")));
        Assert.AreEqual(
            "ALTER TABLE \"bookings\" RENAME CONSTRAINT " +
            "\"AK_room_bookings_room_id_booked_during\" TO \"AK_bookings_room_id_booked_during\";",
            rename.Sql);

        // The rename references the new table name, so it must run after the base RenameTable.
        Assert.IsTrue(operations.IndexOf(rename) > operations.FindIndex(o => o is RenameTableOperation));
    }

    [TestMethod(DisplayName = "Temporal constraint emits fully rendered WITHOUT OVERLAPS DDL")]
    public void Temporal_constraint_emits_add_unique()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<TemporalContext>());

        var sql = Assert.ContainsSingle(TemporalSql(operations));
        Assert.AreEqual(
            $"ALTER TABLE \"room_bookings\" ADD CONSTRAINT \"{DefaultName}\" " +
            "UNIQUE (\"room_id\", \"booked_during\" WITHOUT OVERLAPS);",
            sql);
    }

    [TestMethod(DisplayName = "Temporal DDL renders without the UseNpgsqlComplexIndexes wiring")]
    public void Temporal_constraint_survives_missing_runtime_wiring()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<TemporalContext>());

        // The whole point of rendering at design time: a consumer who never calls
        // UseNpgsqlComplexIndexes() must still get WITHOUT OVERLAPS, not a plain UNIQUE that
        // applies cleanly and silently drops the non-overlap guarantee.
        var options = new DbContextOptionsBuilder().UseNpgsql("Host=localhost;Database=test").Options;
        using var context = new EmptyContext(options);

        var sql = string.Join("\n", context.GetService<IMigrationsSqlGenerator>()
                                           .Generate(operations, model: null)
                                           .Select(c => c.CommandText));

        StringAssert.Contains(sql, "WITHOUT OVERLAPS");
    }

    [TestMethod(DisplayName = "Temporal constraint auto-injects CREATE EXTENSION btree_gist first")]
    public void Temporal_constraint_injects_extension()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<TemporalContext>());

        var first = operations[0] as SqlOperation;
        Assert.IsNotNull(first, "First operation should be the CREATE EXTENSION SqlOperation.");
        StringAssert.Contains(first!.Sql, CreateExtensionPrefix);
    }

    [TestMethod(DisplayName = "Auto-injection can be suppressed")]
    public void Auto_injection_can_be_suppressed()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<TemporalSuppressedContext>());

        Assert.IsFalse(operations.OfType<SqlOperation>().Any(o => o.Sql.Contains("btree_gist")));
        StringAssert.Contains(Assert.ContainsSingle(TemporalSql(operations)), "\"booked_during\" WITHOUT OVERLAPS");
    }

    [TestMethod(DisplayName = "Explicit UseBtreeGist suppresses duplicate injection")]
    public void Explicit_extension_suppresses_injection()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<TemporalWithExtensionContext>());

        Assert.IsFalse(
            operations.OfType<SqlOperation>().Any(o => o.Sql.Contains(CreateExtensionPrefix)),
            "When the extension is declared, the differ must not inject its own CREATE EXTENSION."
        );
        // The constraint is still emitted.
        Assert.ContainsSingle(TemporalSql(operations));
    }

    [TestMethod(DisplayName = "Named multi-key temporal constraint resolves all columns")]
    public void Named_multi_key_constraint()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<TemporalNamedMultiKeyContext>());

        var sql = Assert.ContainsSingle(TemporalSql(operations));
        Assert.AreEqual(
            "ALTER TABLE \"room_bookings\" ADD CONSTRAINT \"uq_booking_temporal\" " +
            "UNIQUE (\"Id\", \"room_id\", \"booked_during\" WITHOUT OVERLAPS);",
            sql);
    }

    [TestMethod(DisplayName = "No-op diff produces no temporal operations")]
    public void No_op_diff_produces_nothing()
    {
        var operations = GetDifferences(BuildRelationalModel<TemporalContext>(), BuildRelationalModel<TemporalContext>());

        Assert.IsEmpty(TemporalSql(operations));
        Assert.IsEmpty(operations.OfType<DropUniqueConstraintOperation>());
        Assert.IsFalse(operations.OfType<SqlOperation>().Any(o => o.Sql.Contains("btree_gist")));
    }

    [TestMethod(DisplayName = "Removing a temporal constraint drops it by name")]
    public void Removing_temporal_constraint_drops_it()
    {
        var operations = GetDifferences(
            source: BuildRelationalModel<TemporalContext>(),
            target: BuildRelationalModel<PlainContext>());

        var drop = Assert.ContainsSingle(operations.OfType<DropUniqueConstraintOperation>());
        Assert.AreEqual(DefaultName,     drop.Name);
        Assert.AreEqual("room_bookings", drop.Table);
        Assert.IsEmpty(TemporalSql(operations));
    }
}
