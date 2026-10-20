using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// SQL Server treats NULLs as equal in a unique index, so EF Core's convention filters its own unique
/// indexes over nullable columns with <c>IS NOT NULL</c>. A unique complex index got no such filter:
/// an optional complex property, whose columns are all nullable, admitted one row without a value
/// and refused the second with a duplicate key.
/// </summary>
[TestClass]
public class SqlServerNullableUniqueTests
{
    private class Registration
    {
        public string Vin   { get; set; } = "";
        public string Plate { get; set; } = "";
    }

    private class Car
    {
        public int           Id           { get; set; }
        public string?       Tag          { get; set; }
        public string        Code         { get; set; } = "";
        public Registration? Registration { get; set; }
    }

    private abstract class CarContext(DbContextOptions options) : DbContext(options)
    {
        protected virtual int? RenderingVersion => null;

        protected abstract void Declare(EntityTypeBuilder<Car> b);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Car>(b =>
            {
                b.ToTable("cars");
                b.Property(x => x.Tag).HasMaxLength(20);
                b.Property(x => x.Code).HasMaxLength(20);
                b.HasIndex(x => x.Tag).IsUnique().HasDatabaseName("ux_native_tag");
                b.ComplexProperty(x => x.Registration, c =>
                {
                    c.Property(r => r.Vin).HasMaxLength(17);
                    c.Property(r => r.Plate).HasMaxLength(12);
                });
                Declare(b);
            });

            // A snapshot scaffolded by an earlier 5.x records the rules it was rendered under.
            if (RenderingVersion is { } version)
                modelBuilder.HasAnnotation("CustomIndex:RenderingVersion", version);
        }
    }

    private class OptionalContext : CarContext
    {
        public OptionalContext(DbContextOptions<OptionalContext> options) : base(options) { }

        protected OptionalContext(DbContextOptions options) : base(options) { }

        protected override void Declare(EntityTypeBuilder<Car> b)
        {
            b.HasComplexIndex(x => x.Registration!.Vin, isUnique: true, indexName: "ux_complex_vin");
            b.HasComplexCompositeIndex(x => new { x.Code, x.Registration!.Plate }, isUnique: true, indexName: "ux_code_plate");
            b.HasComplexIndex(x => x.Code, isUnique: true, indexName: "ux_code");
            b.HasComplexIndex(x => x.Registration!.Plate, isUnique: true, filter: "[Registration_Plate] <> N''", indexName: "ux_plate_filtered");
            b.HasComplexCompositeIndex(x => new { x.Registration!.Plate, x.Registration.Vin }, ix => ix.IsUnique().IsClustered().HasName("cx_vin"));
            b.HasKey(x => x.Id).IsClustered(false);
        }
    }

    private class OptionalV2SnapshotContext(DbContextOptions<OptionalV2SnapshotContext> options) : OptionalContext(options)
    {
        protected override int? RenderingVersion => 2;
    }

    private static Dictionary<string, CreateIndexOperation> Creates<TContext>() where TContext : DbContext
        => MigrationHarness.SqlServerDiff(null, MigrationHarness.SqlServerModel<TContext>())
                           .OfType<CreateIndexOperation>()
                           .ToDictionary(o => o.Name);

    [TestMethod(DisplayName = "A unique complex index over nullable columns gets EF Core's IS NOT NULL filter")]
    public void Nullable_columns_get_the_filter()
    {
        var creates = Creates<OptionalContext>();

        // The same answer EF Core's convention gives its own unique index on a nullable column.
        Assert.AreEqual("[Tag] IS NOT NULL", creates["ux_native_tag"].Filter);
        Assert.AreEqual("[Registration_Vin] IS NOT NULL", creates["ux_complex_vin"].Filter);
        Assert.AreEqual("[Registration_Plate] IS NOT NULL", creates["ux_code_plate"].Filter);

        StringAssert.Contains(MigrationHarness.SqlServerSql([creates["ux_complex_vin"]], MigrationHarness.SqlServerModel<OptionalContext>()),
                              "CREATE UNIQUE INDEX [ux_complex_vin] ON [cars] ([Registration_Vin]) WHERE [Registration_Vin] IS NOT NULL;");
    }

    [TestMethod(DisplayName = "No filter for required columns, a declared filter or a clustered index")]
    public void Filter_only_where_ef_core_adds_one()
    {
        var creates = Creates<OptionalContext>();

        Assert.IsNull(creates["ux_code"].Filter);
        Assert.AreEqual("[Registration_Plate] <> N''", creates["ux_plate_filtered"].Filter);
        Assert.IsNull(creates["cx_vin"].Filter);
    }

    [TestMethod(DisplayName = "Rendered with its model, an index without the filter applies without one, as database update renders it")]
    public void Rendering_with_the_model_adds_no_legacy_filter()
    {
        // Rendered without a model, SQL Server's generator adds its pre-2.0 legacy IS NOT NULL filter
        // to every unique index over nullable columns, so this index looked filtered while the
        // migration `database update` applies was not. That is how the harness hid #88.
        var target = MigrationHarness.SqlServerModel<OptionalV2SnapshotContext>();
        var create = MigrationHarness.SqlServerDiff(null, target).OfType<CreateIndexOperation>().Single(o => o.Name == "ux_complex_vin");

        Assert.IsNull(create.Filter);
        StringAssert.Contains(MigrationHarness.SqlServerSql([create], target),
                              "CREATE UNIQUE INDEX [ux_complex_vin] ON [cars] ([Registration_Vin]);");
    }

    [TestMethod(DisplayName = "A snapshot from an earlier 5.x rebuilds the index with the filter, once")]
    public void Earlier_snapshot_rebuilds_once()
    {
        var operations = MigrationHarness.SqlServerDiff(
            MigrationHarness.SqlServerModel<OptionalV2SnapshotContext>(), MigrationHarness.SqlServerModel<OptionalContext>());

        CollectionAssert.AreEquivalent(new[] { "ux_complex_vin", "ux_code_plate" },
                                       operations.OfType<DropIndexOperation>().Select(o => o.Name).ToArray());
        CollectionAssert.AreEquivalent(new[] { "ux_complex_vin", "ux_code_plate" },
                                       operations.OfType<CreateIndexOperation>().Select(o => o.Name).ToArray());

        Assert.IsEmpty(MigrationHarness.SqlServerDiff(
            MigrationHarness.SqlServerModel<OptionalContext>(), MigrationHarness.SqlServerModel<OptionalContext>()));
    }
}
