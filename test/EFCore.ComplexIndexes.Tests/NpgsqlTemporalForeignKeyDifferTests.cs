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

#pragma warning disable EF1001

[TestClass]
public class NpgsqlTemporalForeignKeyDifferTests
{
    private const string DefaultName = "FK_subscription_addons_subscriptions_subscription_id_active_during";

    // ── Helpers ──

    private static IRelationalModel BuildRelationalModel<TContext>() where TContext : DbContext
        => MigrationHarness.NpgsqlModel<TContext>();

    private static IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
        => MigrationHarness.NpgsqlDiff(source, target);

    // Temporal DDL is rendered at design time, so adds show up as plain SqlOperations.
    private static List<string> ForeignKeySql(IEnumerable<MigrationOperation> operations)
        => [.. operations.OfType<SqlOperation>().Select(o => o.Sql).Where(s => s.Contains("FOREIGN KEY"))];

    private static List<string> ConstraintSql(IEnumerable<MigrationOperation> operations)
        => [.. operations.OfType<SqlOperation>().Select(o => o.Sql).Where(s => s.Contains("WITHOUT OVERLAPS"))];

    private class EmptyContext(DbContextOptions options) : DbContext(options);

    private class Subscription
    {
        public int                   RowId          { get; set; }
        public int                   SubscriptionId { get; set; }
        public int                   TenantId       { get; set; }
        public NpgsqlRange<DateOnly> ValidDuring    { get; set; }
    }

    private class SubscriptionAddOn
    {
        public int                   Id             { get; set; }
        public int                   SubscriptionId { get; set; }
        public int                   TenantId       { get; set; }
        public NpgsqlRange<DateOnly> ActiveDuring   { get; set; }
    }

    private static void MapSubscription(EntityTypeBuilder<Subscription> b)
    {
        b.ToTable("subscriptions");
        b.HasKey(x => x.RowId);
        b.Property(x => x.RowId).HasColumnName("row_id");
        b.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.ValidDuring).HasColumnName("valid_during");
    }

    private static void MapAddOn(EntityTypeBuilder<SubscriptionAddOn> b)
    {
        b.ToTable("subscription_addons");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
        b.Property(x => x.TenantId).HasColumnName("tenant_id");
        b.Property(x => x.ActiveDuring).HasColumnName("active_during");
    }

    private class TemporalForeignKeyContext(DbContextOptions<TemporalForeignKeyContext> options) : DbContext(options)
    {
        public DbSet<Subscription>      Subscriptions => Set<Subscription>();
        public DbSet<SubscriptionAddOn> AddOns        => Set<SubscriptionAddOn>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Subscription>(b =>
            {
                MapSubscription(b);
                b.HasTemporalConstraint(x => x.SubscriptionId, x => x.ValidDuring);
            });

            modelBuilder.Entity<SubscriptionAddOn>(b =>
            {
                MapAddOn(b);
                b.HasTemporalForeignKey<SubscriptionAddOn, Subscription>(
                    x => x.SubscriptionId,
                    x => x.ActiveDuring,
                    x => x.SubscriptionId,
                    x => x.ValidDuring);
            });
        }
    }

    private class TemporalNamedCompositeForeignKeyContext(DbContextOptions<TemporalNamedCompositeForeignKeyContext> options)
        : DbContext(options)
    {
        public DbSet<Subscription>      Subscriptions => Set<Subscription>();
        public DbSet<SubscriptionAddOn> AddOns        => Set<SubscriptionAddOn>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Subscription>(b =>
            {
                MapSubscription(b);
                b.HasTemporalConstraint(x => new { x.TenantId, x.SubscriptionId }, x => x.ValidDuring);
            });

            modelBuilder.Entity<SubscriptionAddOn>(b =>
            {
                MapAddOn(b);
                b.HasTemporalForeignKey(
                    x => new { x.TenantId, x.SubscriptionId },
                    x => x.ActiveDuring,
                    (Subscription x) => new { x.TenantId, x.SubscriptionId },
                    x => x.ValidDuring,
                    name: "fk_addons_subscriptions_temporal");
            });
        }
    }

    private class TemporalPrincipalOnlyContext(DbContextOptions<TemporalPrincipalOnlyContext> options) : DbContext(options)
    {
        public DbSet<Subscription>      Subscriptions => Set<Subscription>();
        public DbSet<SubscriptionAddOn> AddOns        => Set<SubscriptionAddOn>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Subscription>(b =>
            {
                MapSubscription(b);
                b.HasTemporalConstraint(x => x.SubscriptionId, x => x.ValidDuring);
            });
            modelBuilder.Entity<SubscriptionAddOn>(MapAddOn);
        }
    }

    private class MissingPrincipalTemporalConstraintContext(DbContextOptions<MissingPrincipalTemporalConstraintContext> options)
        : DbContext(options)
    {
        public DbSet<Subscription>      Subscriptions => Set<Subscription>();
        public DbSet<SubscriptionAddOn> AddOns        => Set<SubscriptionAddOn>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Subscription>(MapSubscription);

            modelBuilder.Entity<SubscriptionAddOn>(b =>
            {
                MapAddOn(b);
                b.HasTemporalForeignKey(
                    x => x.SubscriptionId,
                    x => x.ActiveDuring,
                    (Subscription x) => x.SubscriptionId,
                    x => x.ValidDuring);
            });
        }
    }


    private class TemporalRenamedPrincipalConstraintContext(DbContextOptions<TemporalRenamedPrincipalConstraintContext> options)
        : DbContext(options)
    {
        public DbSet<Subscription>      Subscriptions => Set<Subscription>();
        public DbSet<SubscriptionAddOn> AddOns        => Set<SubscriptionAddOn>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Subscription>(b =>
            {
                MapSubscription(b);
                b.HasTemporalConstraint(x => x.SubscriptionId, x => x.ValidDuring, name: "uq_subscriptions_temporal");
            });

            modelBuilder.Entity<SubscriptionAddOn>(b =>
            {
                MapAddOn(b);
                b.HasTemporalForeignKey(
                    x => x.SubscriptionId,
                    x => x.ActiveDuring,
                    (Subscription x) => x.SubscriptionId,
                    x => x.ValidDuring);
            });
        }
    }

    private class PrincipalOnlyNoDependentTableContext(DbContextOptions<PrincipalOnlyNoDependentTableContext> options)
        : DbContext(options)
    {
        public DbSet<Subscription> Subscriptions => Set<Subscription>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Subscription>(b =>
            {
                MapSubscription(b);
                b.HasTemporalConstraint(x => x.SubscriptionId, x => x.ValidDuring);
            });
    }

    private class AddOnOnlyContext(DbContextOptions<AddOnOnlyContext> options) : DbContext(options)
    {
        public DbSet<SubscriptionAddOn> AddOns => Set<SubscriptionAddOn>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<SubscriptionAddOn>(MapAddOn);
    }

    private class DependentPeriodDroppedContext(DbContextOptions<DependentPeriodDroppedContext> options) : DbContext(options)
    {
        public DbSet<Subscription>      Subscriptions => Set<Subscription>();
        public DbSet<SubscriptionAddOn> AddOns        => Set<SubscriptionAddOn>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Subscription>(b =>
            {
                MapSubscription(b);
                b.HasTemporalConstraint(x => x.SubscriptionId, x => x.ValidDuring);
            });

            modelBuilder.Entity<SubscriptionAddOn>(b =>
            {
                MapAddOn(b);
                b.Ignore(x => x.ActiveDuring);
            });
        }
    }

    private class PrincipalPeriodDroppedContext(DbContextOptions<PrincipalPeriodDroppedContext> options) : DbContext(options)
    {
        public DbSet<Subscription>      Subscriptions => Set<Subscription>();
        public DbSet<SubscriptionAddOn> AddOns        => Set<SubscriptionAddOn>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Subscription>(b =>
            {
                MapSubscription(b);
                b.Ignore(x => x.ValidDuring);
            });

            modelBuilder.Entity<SubscriptionAddOn>(MapAddOn);
        }
    }

    private class MismatchedKeyCountContext(DbContextOptions<MismatchedKeyCountContext> options) : DbContext(options)
    {
        public DbSet<Subscription>      Subscriptions => Set<Subscription>();
        public DbSet<SubscriptionAddOn> AddOns        => Set<SubscriptionAddOn>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Subscription>(MapSubscription);
            modelBuilder.Entity<SubscriptionAddOn>(b =>
            {
                MapAddOn(b);
                b.HasTemporalForeignKey<SubscriptionAddOn, Subscription>(
                    x => new { x.TenantId, x.SubscriptionId },
                    x => x.ActiveDuring,
                    x => x.SubscriptionId,
                    x => x.ValidDuring);
            });
        }
    }

    private class DependentPeriodInKeyContext(DbContextOptions<DependentPeriodInKeyContext> options) : DbContext(options)
    {
        public DbSet<Subscription>      Subscriptions => Set<Subscription>();
        public DbSet<SubscriptionAddOn> AddOns        => Set<SubscriptionAddOn>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Subscription>(MapSubscription);
            modelBuilder.Entity<SubscriptionAddOn>(b =>
            {
                MapAddOn(b);
                b.HasTemporalForeignKey<SubscriptionAddOn, Subscription>(
                    x => new { x.SubscriptionId, x.ActiveDuring },
                    x => x.ActiveDuring,
                    x => new { x.TenantId, x.SubscriptionId },
                    x => x.ValidDuring);
            });
        }
    }

    private class PrincipalPeriodInKeyContext(DbContextOptions<PrincipalPeriodInKeyContext> options) : DbContext(options)
    {
        public DbSet<Subscription>      Subscriptions => Set<Subscription>();
        public DbSet<SubscriptionAddOn> AddOns        => Set<SubscriptionAddOn>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Subscription>(MapSubscription);
            modelBuilder.Entity<SubscriptionAddOn>(b =>
            {
                MapAddOn(b);
                b.HasTemporalForeignKey<SubscriptionAddOn, Subscription>(
                    x => new { x.TenantId, x.SubscriptionId },
                    x => x.ActiveDuring,
                    x => new { x.SubscriptionId, x.ValidDuring },
                    x => x.ValidDuring);
            });
        }
    }

    private class BadDependentPeriodAddOn
    {
        public int Id             { get; set; }
        public int SubscriptionId { get; set; }
        public int ActiveDuring   { get; set; }
    }

    private static void MapBadDependentPeriodAddOn(EntityTypeBuilder<BadDependentPeriodAddOn> b)
    {
        b.ToTable("subscription_addons");
        b.HasKey(x => x.Id);
        b.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
        b.Property(x => x.ActiveDuring).HasColumnName("active_during");
    }

    private class NonRangeDependentPeriodContext(DbContextOptions<NonRangeDependentPeriodContext> options) : DbContext(options)
    {
        public DbSet<Subscription>            Subscriptions => Set<Subscription>();
        public DbSet<BadDependentPeriodAddOn> AddOns        => Set<BadDependentPeriodAddOn>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Subscription>(b =>
            {
                MapSubscription(b);
                b.HasTemporalConstraint(x => x.SubscriptionId, x => x.ValidDuring);
            });

            modelBuilder.Entity<BadDependentPeriodAddOn>(b =>
            {
                MapBadDependentPeriodAddOn(b);
                b.HasTemporalForeignKey(
                    x => x.SubscriptionId,
                    x => x.ActiveDuring,
                    (Subscription x) => x.SubscriptionId,
                    x => x.ValidDuring);
            });
        }
    }

    private class BadPrincipalSubscription
    {
        public int RowId          { get; set; }
        public int SubscriptionId { get; set; }
        public int ValidDuring    { get; set; }
    }

    private static void MapBadPrincipalSubscription(EntityTypeBuilder<BadPrincipalSubscription> b)
    {
        b.ToTable("subscriptions");
        b.HasKey(x => x.RowId);
        b.Property(x => x.RowId).HasColumnName("row_id");
        b.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
        b.Property(x => x.ValidDuring).HasColumnName("valid_during");
    }

    private class NonRangePrincipalPeriodContext(DbContextOptions<NonRangePrincipalPeriodContext> options) : DbContext(options)
    {
        public DbSet<BadPrincipalSubscription> Subscriptions => Set<BadPrincipalSubscription>();
        public DbSet<SubscriptionAddOn>        AddOns        => Set<SubscriptionAddOn>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<BadPrincipalSubscription>(MapBadPrincipalSubscription);

            modelBuilder.Entity<SubscriptionAddOn>(b =>
            {
                MapAddOn(b);
                b.HasTemporalForeignKey(
                    x => x.SubscriptionId,
                    x => x.ActiveDuring,
                    (BadPrincipalSubscription x) => x.SubscriptionId,
                    x => x.ValidDuring);
            });
        }
    }

    // ── Tests ──

    [TestMethod(DisplayName = "Temporal foreign key emits fully rendered PERIOD DDL")]
    public void Temporal_foreign_key_emits_add_foreign_key()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<TemporalForeignKeyContext>());

        var sql = Assert.ContainsSingle(ForeignKeySql(operations));
        Assert.AreEqual(
            $"ALTER TABLE \"subscription_addons\" ADD CONSTRAINT \"{DefaultName}\" " +
            "FOREIGN KEY (\"subscription_id\", PERIOD \"active_during\") " +
            "REFERENCES \"subscriptions\" (\"subscription_id\", PERIOD \"valid_during\");",
            sql);
    }

    [TestMethod(DisplayName = "Temporal DDL renders without the UseNpgsqlComplexIndexes wiring")]
    public void Temporal_foreign_key_survives_missing_runtime_wiring()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<TemporalForeignKeyContext>());

        var options = new DbContextOptionsBuilder().UseNpgsql("Host=localhost;Database=test").Options;
        using var context = new EmptyContext(options);

        var sql = string.Join("\n", context.GetService<IMigrationsSqlGenerator>()
                                           .Generate(operations, model: null)
                                           .Select(c => c.CommandText));

        StringAssert.Contains(sql, "PERIOD \"active_during\"");
        StringAssert.Contains(sql, "PERIOD \"valid_during\"");
    }

    [TestMethod(DisplayName = "Temporal foreign key is added after the principal temporal constraint")]
    public void Temporal_foreign_key_added_after_principal_temporal_constraint()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<TemporalForeignKeyContext>()).ToList();

        var uniqueIndex = operations.FindIndex(o => o is SqlOperation s && s.Sql.Contains("WITHOUT OVERLAPS"));
        var fkIndex     = operations.FindIndex(o => o is SqlOperation s && s.Sql.Contains("FOREIGN KEY"));

        Assert.IsTrue(uniqueIndex >= 0);
        Assert.IsTrue(fkIndex     > uniqueIndex);
    }

    [TestMethod(DisplayName = "Named composite temporal foreign key resolves all columns")]
    public void Named_composite_temporal_foreign_key()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<TemporalNamedCompositeForeignKeyContext>());

        var sql = Assert.ContainsSingle(ForeignKeySql(operations));
        Assert.AreEqual(
            "ALTER TABLE \"subscription_addons\" ADD CONSTRAINT \"fk_addons_subscriptions_temporal\" " +
            "FOREIGN KEY (\"tenant_id\", \"subscription_id\", PERIOD \"active_during\") " +
            "REFERENCES \"subscriptions\" (\"tenant_id\", \"subscription_id\", PERIOD \"valid_during\");",
            sql);
    }

    [TestMethod(DisplayName = "No-op temporal foreign key diff produces no FK operations")]
    public void No_op_diff_produces_nothing()
    {
        var operations = GetDifferences(BuildRelationalModel<TemporalForeignKeyContext>(),
                                        BuildRelationalModel<TemporalForeignKeyContext>());

        Assert.IsEmpty(ForeignKeySql(operations));
        Assert.IsEmpty(operations.OfType<DropForeignKeyOperation>());
    }

    [TestMethod(DisplayName = "Removing a temporal foreign key drops it by name")]
    public void Removing_temporal_foreign_key_drops_it()
    {
        var operations = GetDifferences(
            source: BuildRelationalModel<TemporalForeignKeyContext>(),
            target: BuildRelationalModel<TemporalPrincipalOnlyContext>());

        var drop = Assert.ContainsSingle(operations.OfType<DropForeignKeyOperation>());
        Assert.AreEqual(DefaultName,           drop.Name);
        Assert.AreEqual("subscription_addons", drop.Table);
        Assert.IsEmpty(ForeignKeySql(operations));
    }

    [TestMethod(DisplayName = "Renaming the principal temporal constraint becomes RENAME CONSTRAINT; the FK survives")]
    public void Renaming_principal_temporal_constraint_renames_in_place()
    {
        var operations = GetDifferences(
            source: BuildRelationalModel<TemporalForeignKeyContext>(),
            target: BuildRelationalModel<TemporalRenamedPrincipalConstraintContext>()).ToList();

        // ALTER TABLE … RENAME CONSTRAINT preserves dependents, so neither the constraint nor the
        // foreign key is rebuilt.
        Assert.IsEmpty(operations.OfType<DropForeignKeyOperation>());
        Assert.IsEmpty(ForeignKeySql(operations));
        Assert.IsEmpty(operations.OfType<DropUniqueConstraintOperation>());
        Assert.IsEmpty(ConstraintSql(operations));

        var rename = Assert.ContainsSingle(operations.OfType<SqlOperation>());
        Assert.AreEqual(
            "ALTER TABLE \"subscriptions\" RENAME CONSTRAINT " +
            "\"AK_subscriptions_subscription_id_valid_during\" TO \"uq_subscriptions_temporal\";",
            rename.Sql);
    }

    [TestMethod(DisplayName = "Dropping the dependent table skips explicit temporal FK drop")]
    public void Dropping_dependent_table_skips_explicit_foreign_key_drop()
    {
        var operations = GetDifferences(
            source: BuildRelationalModel<TemporalForeignKeyContext>(),
            target: BuildRelationalModel<PrincipalOnlyNoDependentTableContext>());

        Assert.IsEmpty(operations.OfType<DropForeignKeyOperation>());
        Assert.IsTrue(operations.OfType<DropTableOperation>().Any(o => o.Name == "subscription_addons"));
    }

    [TestMethod(DisplayName = "Dropping the principal table drops temporal FK first")]
    public void Dropping_principal_table_drops_foreign_key_first()
    {
        var operations = GetDifferences(
            source: BuildRelationalModel<TemporalForeignKeyContext>(),
            target: BuildRelationalModel<AddOnOnlyContext>()).ToList();

        var dropForeignKey     = operations.FindIndex(o => o is DropForeignKeyOperation);
        var dropPrincipalTable = operations.FindIndex(o => o is DropTableOperation { Name: "subscriptions" });

        Assert.IsTrue(dropForeignKey     >= 0);
        Assert.IsTrue(dropPrincipalTable > dropForeignKey);
    }

    [TestMethod(DisplayName = "Dropping the dependent period column drops temporal FK first")]
    public void Dropping_dependent_period_column_drops_foreign_key_first()
    {
        var operations = GetDifferences(
            source: BuildRelationalModel<TemporalForeignKeyContext>(),
            target: BuildRelationalModel<DependentPeriodDroppedContext>()).ToList();

        var dropForeignKey = operations.FindIndex(o => o is DropForeignKeyOperation);
        var dropColumn = operations.FindIndex(o => o is DropColumnOperation { Name: "active_during", Table: "subscription_addons" });

        Assert.IsTrue(dropForeignKey >= 0);
        Assert.IsTrue(dropColumn     > dropForeignKey);
    }

    [TestMethod(DisplayName = "Dropping the principal period column drops temporal FK and UNIQUE first")]
    public void Dropping_principal_period_column_drops_foreign_key_and_unique_first()
    {
        var operations = GetDifferences(
            source: BuildRelationalModel<TemporalForeignKeyContext>(),
            target: BuildRelationalModel<PrincipalPeriodDroppedContext>()).ToList();

        var dropForeignKey = operations.FindIndex(o => o is DropForeignKeyOperation);
        var dropUnique     = operations.FindIndex(o => o is DropUniqueConstraintOperation);
        var dropColumn     = operations.FindIndex(o => o is DropColumnOperation { Name: "valid_during", Table: "subscriptions" });

        Assert.IsTrue(dropForeignKey >= 0);
        Assert.IsTrue(dropUnique     > dropForeignKey);
        Assert.IsTrue(dropColumn     > dropUnique);
    }

    [TestMethod(DisplayName = "Adding only a temporal FK does not inject btree_gist")]
    public void Adding_only_foreign_key_does_not_inject_extension()
    {
        var operations = GetDifferences(
            source: BuildRelationalModel<TemporalPrincipalOnlyContext>(),
            target: BuildRelationalModel<TemporalForeignKeyContext>());

        Assert.ContainsSingle(ForeignKeySql(operations));
        Assert.IsFalse(operations.OfType<SqlOperation>().Any(o => o.Sql.Contains("btree_gist")));
    }

    [TestMethod(DisplayName = "Temporal FK requires matching key counts")]
    public void Mismatched_key_count_throws()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(BuildRelationalModel<MismatchedKeyCountContext>);

        StringAssert.Contains(ex.Message, "key-count mismatch");
    }

    [TestMethod(DisplayName = "Temporal FK dependent period cannot appear in dependent keys")]
    public void Dependent_period_in_key_throws()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(BuildRelationalModel<DependentPeriodInKeyContext>);

        StringAssert.Contains(ex.Message, "dependent period column 'ActiveDuring'");
    }

    [TestMethod(DisplayName = "Temporal FK principal period cannot appear in principal keys")]
    public void Principal_period_in_key_throws()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(BuildRelationalModel<PrincipalPeriodInKeyContext>);

        StringAssert.Contains(ex.Message, "principal period column 'ValidDuring'");
    }

    [TestMethod(DisplayName = "Temporal FK dependent period must be range-like")]
    public void Non_range_dependent_period_throws()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => GetDifferences(
                                                                     source: null,
                                                                     target: BuildRelationalModel<NonRangeDependentPeriodContext>()));

        StringAssert.Contains(ex.Message, "temporal foreign key dependent period property 'ActiveDuring'");
    }

    [TestMethod(DisplayName = "Temporal FK principal period must be range-like")]
    public void Non_range_principal_period_throws()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => GetDifferences(
                                                                     source: null,
                                                                     target: BuildRelationalModel<NonRangePrincipalPeriodContext>()));

        StringAssert.Contains(ex.Message, "temporal foreign key principal period property 'ValidDuring'");
    }

    [TestMethod(DisplayName = "Temporal foreign key requires a matching principal temporal constraint")]
    public void Missing_principal_temporal_constraint_throws()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => GetDifferences(
                                                                     source: null,
                                                                     target: BuildRelationalModel<
                                                                         MissingPrincipalTemporalConstraintContext>()));

        StringAssert.Contains(ex.Message, "no matching HasTemporalConstraint");
    }
}

#pragma warning restore EF1001