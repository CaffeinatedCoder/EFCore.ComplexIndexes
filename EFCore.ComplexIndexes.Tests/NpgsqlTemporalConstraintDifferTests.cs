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
    private const string WithoutOverlaps       = "CustomTemporal:WithoutOverlaps";
    private const string CreateExtensionPrefix = "CREATE EXTENSION IF NOT EXISTS btree_gist";
    private const string DefaultName           = "AK_room_bookings_room_id_booked_during";

    // ── Helpers ──

    private static IRelationalModel BuildRelationalModel<TContext>() where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
                     .UseNpgsql("Host=localhost;Database=test")
                     .Options;

        using var context = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        return context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
    }

    private static IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        var options = new DbContextOptionsBuilder()
                     .UseNpgsql("Host=localhost;Database=test")
                     .Options;

        using var context = new EmptyContext(options);

        var differ = new NpgsqlComplexIndexMigrationsModelDiffer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<IMigrationsAnnotationProvider>(),
            context.GetService<IRelationalAnnotationProvider>(),
            context.GetService<IRowIdentityMapFactory>(),
            context.GetService<CommandBatchPreparerDependencies>()
        );

        return differ.GetDifferences(source, target);
    }

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

    // ── Tests ──

    [TestMethod(DisplayName = "Temporal constraint emits a stamped AddUniqueConstraintOperation")]
    public void Temporal_constraint_emits_add_unique()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<TemporalContext>());

        var addUnique = Assert.ContainsSingle(operations.OfType<AddUniqueConstraintOperation>());
        Assert.AreEqual("room_bookings", addUnique.Table);
        Assert.AreEqual(DefaultName,     addUnique.Name);
        Assert.IsTrue(addUnique.Columns.SequenceEqual(["room_id", "booked_during"]));
        Assert.AreEqual("booked_during", addUnique[WithoutOverlaps]);
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
        Assert.AreEqual("booked_during", Assert.ContainsSingle(operations.OfType<AddUniqueConstraintOperation>())[WithoutOverlaps]);
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
        Assert.ContainsSingle(operations.OfType<AddUniqueConstraintOperation>());
    }

    [TestMethod(DisplayName = "Named multi-key temporal constraint resolves all columns")]
    public void Named_multi_key_constraint()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<TemporalNamedMultiKeyContext>());

        var addUnique = Assert.ContainsSingle(operations.OfType<AddUniqueConstraintOperation>());
        Assert.AreEqual("uq_booking_temporal", addUnique.Name);
        Assert.IsTrue(addUnique.Columns.SequenceEqual(["Id", "room_id", "booked_during"]));
        Assert.AreEqual("booked_during", addUnique[WithoutOverlaps]);
    }

    [TestMethod(DisplayName = "No-op diff produces no temporal operations")]
    public void No_op_diff_produces_nothing()
    {
        var operations = GetDifferences(BuildRelationalModel<TemporalContext>(), BuildRelationalModel<TemporalContext>());

        Assert.IsEmpty(operations.OfType<AddUniqueConstraintOperation>());
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
        Assert.IsEmpty(operations.OfType<AddUniqueConstraintOperation>());
    }
}
