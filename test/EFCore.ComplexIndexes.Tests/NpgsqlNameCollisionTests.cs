using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// PostgreSQL keeps constraint names unique per table and index names — including the index behind
/// every primary key, unique, exclusion and temporal constraint — unique per schema. Each kind was
/// checked against its own kind on one table at most, so a temporal constraint named like an
/// exclusion constraint, a temporal foreign key named like a native one, or any index-backed name
/// reused elsewhere in the schema scaffolded cleanly and failed at apply time (42710, 42P07) — or,
/// against an exclusion constraint, whose ADD is preceded by DROP CONSTRAINT IF EXISTS, silently
/// replaced the other constraint.
/// </summary>
[TestClass]
public class NpgsqlNameCollisionTests
{
    private class Booking
    {
        public int                   Id     { get; set; }
        public int                   RoomId { get; set; }
        public int                   DeskId { get; set; }
        public string                Code   { get; set; } = "";
        public NpgsqlRange<DateOnly> Period { get; set; }
    }

    private class Stay
    {
        public int                   Id        { get; set; }
        public int                   BookingId { get; set; }
        public int                   RoomId    { get; set; }
        public string                Code      { get; set; } = "";
        public NpgsqlRange<DateOnly> Period    { get; set; }
    }

    private static void MapBooking(EntityTypeBuilder<Booking> b, string? schema = null)
    {
        b.ToTable("bookings", schema);
        b.HasKey(x => x.Id);
    }

    private static void MapStay(EntityTypeBuilder<Stay> b, string? schema = null)
    {
        b.ToTable("stays", schema);
        b.HasKey(x => x.Id);
    }

    private abstract class ModelContext(DbContextOptions options) : DbContext(options)
    {
        protected abstract void Configure(ModelBuilder modelBuilder);

        protected override void OnModelCreating(ModelBuilder modelBuilder) => Configure(modelBuilder);
    }

    // ── Collisions ──

    private class TwoTemporalConstraintsContext(DbContextOptions<TwoTemporalConstraintsContext> o) : ModelContext(o)
    {
        protected override void Configure(ModelBuilder m) => m.Entity<Booking>(b =>
        {
            MapBooking(b);
            b.HasTemporalConstraint(x => x.RoomId, x => x.Period, name: "tc_booking");
            b.HasTemporalConstraint(x => x.DeskId, x => x.Period, name: "tc_booking");
        });
    }

    private class TemporalLikeExclusionContext(DbContextOptions<TemporalLikeExclusionContext> o) : ModelContext(o)
    {
        protected override void Configure(ModelBuilder m) => m.Entity<Booking>(b =>
        {
            MapBooking(b);
            b.HasTemporalConstraint(x => x.RoomId, x => x.Period, name: "no_overlap");
            b.HasExclusionConstraint(x => x.DeskId, x => x.Period, name: "no_overlap");
        });
    }

    private class TemporalForeignKeyLikeConstraintContext(DbContextOptions<TemporalForeignKeyLikeConstraintContext> o) : ModelContext(o)
    {
        protected override void Configure(ModelBuilder m)
        {
            m.Entity<Booking>(b =>
            {
                MapBooking(b);
                b.HasTemporalConstraint(x => x.RoomId, x => x.Period);
            });
            m.Entity<Stay>(b =>
            {
                MapStay(b);
                b.HasTemporalConstraint(x => x.BookingId, x => x.Period, name: "stay_rules");
                b.HasTemporalForeignKey<Stay, Booking>(x => x.RoomId, x => x.Period, x => x.RoomId, x => x.Period, name: "stay_rules");
            });
        }
    }

    private class TemporalLikeNativeIndexElsewhereContext(DbContextOptions<TemporalLikeNativeIndexElsewhereContext> o) : ModelContext(o)
    {
        protected override void Configure(ModelBuilder m)
        {
            m.Entity<Booking>(b =>
            {
                MapBooking(b);
                b.HasTemporalConstraint(x => x.RoomId, x => x.Period, name: "ix_code");
            });
            m.Entity<Stay>(b =>
            {
                MapStay(b);
                b.HasIndex(x => x.Code).HasDatabaseName("ix_code");
            });
        }
    }

    private class TemporalLikeComplexIndexContext(DbContextOptions<TemporalLikeComplexIndexContext> o) : ModelContext(o)
    {
        protected override void Configure(ModelBuilder m) => m.Entity<Booking>(b =>
        {
            MapBooking(b);
            b.HasTemporalConstraint(x => x.RoomId, x => x.Period, name: "booking_room");
            b.HasComplexIndex(x => x.Code, indexName: "booking_room");
        });
    }

    private class ComplexIndexesAcrossTablesContext(DbContextOptions<ComplexIndexesAcrossTablesContext> o) : ModelContext(o)
    {
        protected override void Configure(ModelBuilder m)
        {
            m.Entity<Booking>(b =>
            {
                MapBooking(b);
                b.HasComplexIndex(x => x.Code, indexName: "ix_by_code");
            });
            m.Entity<Stay>(b =>
            {
                MapStay(b);
                b.HasComplexIndex(x => x.Code, indexName: "ix_by_code");
            });
        }
    }

    private class TemporalForeignKeyLikeNativeForeignKeyContext(DbContextOptions<TemporalForeignKeyLikeNativeForeignKeyContext> o) : ModelContext(o)
    {
        protected override void Configure(ModelBuilder m)
        {
            m.Entity<Booking>(b =>
            {
                MapBooking(b);
                b.HasTemporalConstraint(x => x.RoomId, x => x.Period);
            });
            m.Entity<Stay>(b =>
            {
                MapStay(b);
                b.HasOne<Booking>().WithMany().HasForeignKey(x => x.BookingId).HasConstraintName("fk_stay");
                b.HasTemporalForeignKey<Stay, Booking>(x => x.RoomId, x => x.Period, x => x.RoomId, x => x.Period, name: "fk_stay");
            });
        }
    }

    private class TemporalLikeAlternateKeyElsewhereContext(DbContextOptions<TemporalLikeAlternateKeyElsewhereContext> o) : ModelContext(o)
    {
        protected override void Configure(ModelBuilder m)
        {
            m.Entity<Booking>(b =>
            {
                MapBooking(b);
                b.HasTemporalConstraint(x => x.RoomId, x => x.Period, name: "ak_code");
            });
            m.Entity<Stay>(b =>
            {
                MapStay(b);
                b.HasAlternateKey(x => x.Code).HasName("ak_code");
            });
        }
    }

    // ── Legitimate reuse ──

    private class TemporalForeignKeysAcrossTablesContext(DbContextOptions<TemporalForeignKeysAcrossTablesContext> o) : ModelContext(o)
    {
        protected override void Configure(ModelBuilder m)
        {
            m.Entity<Booking>(b =>
            {
                MapBooking(b);
                b.HasTemporalConstraint(x => x.RoomId, x => x.Period);
                b.HasTemporalConstraint(x => x.DeskId, x => x.Period);
                b.HasTemporalForeignKey<Booking, Booking>(x => x.DeskId, x => x.Period, x => x.RoomId, x => x.Period, name: "fk_room");
            });
            m.Entity<Stay>(b =>
            {
                MapStay(b);
                b.HasTemporalForeignKey<Stay, Booking>(x => x.RoomId, x => x.Period, x => x.RoomId, x => x.Period, name: "fk_room");
            });
        }
    }

    private class SameNameInTwoSchemasContext(DbContextOptions<SameNameInTwoSchemasContext> o) : ModelContext(o)
    {
        protected override void Configure(ModelBuilder m)
        {
            m.Entity<Booking>(b =>
            {
                MapBooking(b, "north");
                b.HasTemporalConstraint(x => x.RoomId, x => x.Period, name: "tc_room");
            });
            m.Entity<Stay>(b =>
            {
                MapStay(b, "south");
                b.HasTemporalConstraint(x => x.RoomId, x => x.Period, name: "tc_room");
            });
        }
    }

    // Two of EF Core's own indexes: nothing this package introduced, so nothing it reports.
    private class NativeOnlyCollisionContext(DbContextOptions<NativeOnlyCollisionContext> o) : ModelContext(o)
    {
        protected override void Configure(ModelBuilder m)
        {
            m.Entity<Booking>(b =>
            {
                MapBooking(b);
                b.HasIndex(x => x.Code).HasDatabaseName("ix_shared");
                b.HasTemporalConstraint(x => x.RoomId, x => x.Period);
            });
            m.Entity<Stay>(b =>
            {
                MapStay(b);
                b.HasIndex(x => x.Code).HasDatabaseName("ix_shared");
            });
        }
    }

    private class FixedContext(DbContextOptions<FixedContext> o) : ModelContext(o)
    {
        protected override void Configure(ModelBuilder m) => m.Entity<Booking>(b =>
        {
            MapBooking(b);
            b.HasTemporalConstraint(x => x.RoomId, x => x.Period, name: "tc_room");
            b.HasTemporalConstraint(x => x.DeskId, x => x.Period, name: "tc_desk");
        });
    }

    private static InvalidOperationException Rejected<TContext>() where TContext : DbContext
        => Assert.ThrowsExactly<InvalidOperationException>(
               () => MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<TContext>()));

    private static void Accepted<TContext>() where TContext : DbContext
        => MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<TContext>());

    [TestMethod(DisplayName = "Two temporal constraints with one name on one table are rejected (42710)")]
    public void Two_temporal_constraints_collide()
    {
        var message = Rejected<TwoTemporalConstraintsContext>().Message;
        StringAssert.Contains(message, "temporal constraint 'tc_booking' on table 'bookings'");
        StringAssert.Contains(message, "(42710)");
    }

    [TestMethod(DisplayName = "A temporal constraint named like an exclusion constraint is rejected, and the silent replacement named")]
    public void Temporal_constraint_like_exclusion_constraint_collides()
    {
        var message = Rejected<TemporalLikeExclusionContext>().Message;
        StringAssert.Contains(message, "exclusion constraint");
        StringAssert.Contains(message, "DROP CONSTRAINT IF EXISTS");
    }

    [TestMethod(DisplayName = "A temporal foreign key named like a temporal constraint on its table is rejected")]
    public void Temporal_foreign_key_like_temporal_constraint_collides()
    {
        var message = Rejected<TemporalForeignKeyLikeConstraintContext>().Message;
        StringAssert.Contains(message, "'stay_rules'");
        StringAssert.Contains(message, "temporal foreign key");
    }

    [TestMethod(DisplayName = "A temporal constraint named like an index on another table is rejected (42P07)")]
    public void Temporal_constraint_like_native_index_elsewhere_collides()
    {
        var message = Rejected<TemporalLikeNativeIndexElsewhereContext>().Message;
        StringAssert.Contains(message, "the index on table 'stays'");
        StringAssert.Contains(message, "(42P07)");
    }

    [TestMethod(DisplayName = "A temporal constraint named like a complex index on its table is rejected")]
    public void Temporal_constraint_like_complex_index_collides()
        => StringAssert.Contains(Rejected<TemporalLikeComplexIndexContext>().Message, "(42P07)");

    [TestMethod(DisplayName = "Two complex indexes with one name on different tables of one schema are rejected")]
    public void Complex_indexes_across_tables_collide()
    {
        var message = Rejected<ComplexIndexesAcrossTablesContext>().Message;
        StringAssert.Contains(message, "complex index 'ix_by_code'");
        StringAssert.Contains(message, "unique per schema");
    }

    [TestMethod(DisplayName = "A temporal foreign key named like a native foreign key on its table is rejected")]
    public void Temporal_foreign_key_like_native_foreign_key_collides()
        => StringAssert.Contains(Rejected<TemporalForeignKeyLikeNativeForeignKeyContext>().Message, "the foreign key on table 'stays'");

    [TestMethod(DisplayName = "A temporal constraint named like an alternate key on another table is rejected")]
    public void Temporal_constraint_like_alternate_key_elsewhere_collides()
        => StringAssert.Contains(Rejected<TemporalLikeAlternateKeyElsewhereContext>().Message, "the unique constraint on table 'stays'");

    [TestMethod(DisplayName = "Temporal foreign keys may share a name across tables — they own no index")]
    public void Temporal_foreign_keys_across_tables_are_allowed()
        => Accepted<TemporalForeignKeysAcrossTablesContext>();

    [TestMethod(DisplayName = "The same name in two schemas is allowed")]
    public void Same_name_in_two_schemas_is_allowed()
        => Accepted<SameNameInTwoSchemasContext>();

    [TestMethod(DisplayName = "A collision between two of EF Core's own objects is not reported")]
    public void Native_only_collision_is_not_reported()
        => Accepted<NativeOnlyCollisionContext>();

    // Target only: the snapshot is history, and a model that fixes the collision must diff.
    [TestMethod(DisplayName = "A collision in the source model does not block the fix")]
    public void Collision_in_source_stays_diffable()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<TwoTemporalConstraintsContext>(),
            MigrationHarness.NpgsqlModel<FixedContext>());

        Assert.IsTrue(operations.OfType<SqlOperation>().Any(o => o.Sql.Contains("tc_desk")));
    }
}
