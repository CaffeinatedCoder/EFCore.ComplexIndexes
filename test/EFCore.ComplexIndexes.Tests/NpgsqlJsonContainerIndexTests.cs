using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Indexing a <c>ToJson()</c> complex property — or a complex collection, which is always JSON — as a
/// whole: the PostgreSQL idiom is a GIN index over the <c>jsonb</c> container column. Before 5.1.0
/// the path resolved to nothing and failed with "could not resolve property path", and complex
/// collections were unreachable altogether (their members have no column and their element builder
/// is not the one the property-level API extends). The container is a real column, so these indexes
/// render through the stock generator with no runtime wiring; a sub-document nested inside the
/// document is a <c>-&gt;</c> extraction and goes through the custom generator like any expression.
/// </summary>
[TestClass]
public class NpgsqlJsonContainerIndexTests
{
    private class Address
    {
        public string City { get; set; } = "";
    }

    private class Payload
    {
        public string  Note    { get; set; } = "";
        public Address Address { get; set; } = new();
    }

    private class Tag
    {
        public string Name { get; set; } = "";
    }

    private class Order
    {
        public int       Id      { get; set; }
        public string    Name    { get; set; } = "";
        public Payload   Payload { get; set; } = new();
        public List<Tag> Tags    { get; set; } = [];
    }

    private class ContainerIndexContext(DbContextOptions<ContainerIndexContext> options) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Order>(b =>
            {
                b.ToTable("orders");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Payload, c =>
                {
                    c.ToJson("payload");
                    c.ComplexProperty(p => p.Address, a => a.HasJsonPropertyName("addr"));
                });
                b.ComplexCollection(x => x.Tags, c => c.ToJson("tags"));

                // Whole document, with an operator class: the common jsonb_path_ops GIN.
                b.HasComplexIndex(x => x.Payload, ix => ix.UseGin().HasOperators("jsonb_path_ops").HasName("ix_orders_payload"));

                // Whole collection.
                b.HasComplexIndex(x => x.Tags, ix => ix.UseGin().HasName("ix_orders_tags"));

                // A sub-document inside the JSON document.
                b.HasComplexIndex(x => x.Payload.Address, ix => ix.UseGin().HasName("ix_orders_payload_addr"));

                // The container column as one part of a composite index.
                b.HasComplexCompositeIndex(x => new { x.Name, x.Payload }, indexName: "ix_orders_name_payload");
            });
    }

    private static Dictionary<string, CreateIndexOperation> Creates()
        => MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<ContainerIndexContext>())
                           .OfType<CreateIndexOperation>()
                           .ToDictionary(o => o.Name);

    [TestMethod(DisplayName = "A ToJson complex property resolves to its container column — a plain column index")]
    public void Json_property_resolves_to_container_column()
    {
        var op = Creates()["ix_orders_payload"];

        CollectionAssert.AreEqual(new[] { "payload" }, op.Columns);
        Assert.AreEqual("gin", op["Npgsql:IndexMethod"]);
        CollectionAssert.AreEqual(new[] { "jsonb_path_ops" }, (string[])op["Npgsql:IndexOperators"]!);
        Assert.IsNull(op[ComplexIndexAnnotations.IndexParts], "A container column needs no parts annotation.");
        Assert.DoesNotContain(CustomMigrationsModelDiffer.RuntimeWiringSentinel, op.Columns);
    }

    [TestMethod(DisplayName = "The stock Npgsql generator renders the container GIN index — no runtime wiring needed")]
    public void Container_index_renders_through_the_stock_generator()
    {
        var sql = MigrationHarness.NpgsqlSql([Creates()["ix_orders_payload"]], complexIndexWiring: false);

        StringAssert.Contains(sql, "CREATE INDEX ix_orders_payload ON orders USING gin (payload jsonb_path_ops)");
    }

    [TestMethod(DisplayName = "A complex collection resolves to its container column")]
    public void Complex_collection_resolves_to_container_column()
    {
        var op = Creates()["ix_orders_tags"];

        CollectionAssert.AreEqual(new[] { "tags" }, op.Columns);
        Assert.AreEqual("gin", op["Npgsql:IndexMethod"]);
        Assert.IsNull(op[ComplexIndexAnnotations.IndexParts]);

        StringAssert.Contains(
            MigrationHarness.NpgsqlSql([op], complexIndexWiring: false),
            "CREATE INDEX ix_orders_tags ON orders USING gin (tags)");
    }

    [TestMethod(DisplayName = "A sub-document inside the JSON document resolves to a -> extraction honoring HasJsonPropertyName")]
    public void Nested_complex_property_resolves_to_jsonb_extraction()
    {
        var op = Creates()["ix_orders_payload_addr"];

        Assert.IsNotNull(op[ComplexIndexAnnotations.IndexParts], "An extraction is an expression part.");
        Assert.AreEqual("\"payload\" -> 'addr'", op.Columns[0]);

        StringAssert.Contains(
            MigrationHarness.NpgsqlSql([op]),
            "CREATE INDEX ix_orders_payload_addr ON orders USING gin ((\"payload\" -> 'addr'))");
    }

    [TestMethod(DisplayName = "The container column can be one part of a composite index")]
    public void Container_column_in_a_composite_index()
    {
        var op = Creates()["ix_orders_name_payload"];

        CollectionAssert.AreEqual(new[] { "Name", "payload" }, op.Columns);
        Assert.IsNull(op[ComplexIndexAnnotations.IndexParts]);
    }

    // ── The core differ has no JSON knowledge: the same declaration must still fail loudly there ──

    private class CoreContainerContext(DbContextOptions<CoreContainerContext> options) : DbContext(options)
    {
        public DbSet<Order> Orders => Set<Order>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Order>(b =>
            {
                b.ToTable("orders");
                b.HasKey(x => x.Id);
                b.Ignore(x => x.Tags);
                b.ComplexProperty(x => x.Payload, c => c.ToJson("payload"));
                b.HasComplexIndex(x => x.Payload, indexName: "ix_orders_payload");
            });
    }

    [TestMethod(DisplayName = "The core differ still rejects a container-column index rather than guessing")]
    public void Core_differ_rejects_container_index()
    {
        var target = MigrationHarness.SqliteModel<CoreContainerContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.CoreDiff(null, target));

        StringAssert.Contains(exception.Message, "Payload");
    }
}
