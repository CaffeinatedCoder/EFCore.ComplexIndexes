using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Each facet of a complex index, changed on its own, re-creates the index. The differ compares
/// descriptors, so a facet left out of their equality makes its change produce no migration at all:
/// the model says one thing, the database keeps the old index, and nothing fails. Before #96 a
/// uniqueness-only, a filter-only and a provider-option-only change were each caught by one
/// incidental test, and a direction-only change by none.
/// </summary>
[TestClass]
public class IndexChangeDetectionTests
{
    private class Address
    {
        public string City { get; set; } = "";
    }

    private class Shop
    {
        public Guid    Id      { get; set; }
        public Address Address { get; set; } = new();
    }

    private abstract class ShopContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Shop>(b =>
            {
                b.ToTable("shops");
                b.ComplexProperty(x => x.Address);
                Declare(b);
            });

        protected abstract void Declare(EntityTypeBuilder<Shop> b);
    }

    private class PlainContext(DbContextOptions<PlainContext> options) : ShopContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Shop> b) => b.HasComplexIndex(x => x.Address.City, indexName: "ix_city");
    }

    private class DescendingContext(DbContextOptions<DescendingContext> options) : ShopContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Shop> b) => b.HasComplexIndex(x => DbOrder.Desc(x.Address.City), indexName: "ix_city");
    }

    private class UniqueContext(DbContextOptions<UniqueContext> options) : ShopContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Shop> b) => b.HasComplexIndex(x => x.Address.City, isUnique: true, indexName: "ix_city");
    }

    private class FilteredContext(DbContextOptions<FilteredContext> options) : ShopContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Shop> b) => b.HasComplexIndex(x => x.Address.City, filter: "[Address_City] <> N''", indexName: "ix_city");
    }

    private class FillFactor80Context(DbContextOptions<FillFactor80Context> options) : ShopContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Shop> b) => b.HasComplexIndex(x => x.Address.City, ix => ix.HasName("ix_city").HasFillFactor(80));
    }

    private class FillFactor90Context(DbContextOptions<FillFactor90Context> options) : ShopContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Shop> b) => b.HasComplexIndex(x => x.Address.City, ix => ix.HasName("ix_city").HasFillFactor(90));
    }

    private static CreateIndexOperation Recreated<TSource, TTarget>() where TSource : DbContext where TTarget : DbContext
    {
        var operations = MigrationHarness.SqlServerDiff(MigrationHarness.SqlServerModel<TSource>(), MigrationHarness.SqlServerModel<TTarget>());

        Assert.AreEqual("ix_city", Assert.ContainsSingle(operations.OfType<DropIndexOperation>()).Name);
        return Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
    }

    [TestMethod(DisplayName = "A direction-only change re-creates the index")]
    public void Direction_only_change_is_detected()
        => CollectionAssert.AreEqual(new[] { true }, Recreated<PlainContext, DescendingContext>().IsDescending);

    [TestMethod(DisplayName = "A uniqueness-only change re-creates the index")]
    public void Uniqueness_only_change_is_detected()
        => Assert.IsTrue(Recreated<PlainContext, UniqueContext>().IsUnique);

    [TestMethod(DisplayName = "A filter-only change re-creates the index")]
    public void Filter_only_change_is_detected()
        => Assert.AreEqual("[Address_City] <> N''", Recreated<PlainContext, FilteredContext>().Filter);

    [TestMethod(DisplayName = "A provider-option-only change re-creates the index")]
    public void Provider_option_only_change_is_detected()
        => Assert.AreEqual(90, Recreated<FillFactor80Context, FillFactor90Context>()["SqlServer:FillFactor"]);

    [TestMethod(DisplayName = "An unchanged index produces nothing")]
    public void Unchanged_index_produces_nothing()
    {
        foreach (var target in new[] { MigrationHarness.SqlServerModel<DescendingContext>(), MigrationHarness.SqlServerModel<FillFactor80Context>() })
            Assert.IsEmpty(MigrationHarness.SqlServerDiff(target, target));
    }
}
