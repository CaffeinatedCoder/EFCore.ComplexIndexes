using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// PostgreSQL storage parameters (<c>WITH (fillfactor=70)</c>) are per-parameter annotations under
/// the <c>Npgsql:StorageParameter:</c> prefix. Npgsql's generator renders them from the operation, so
/// column indexes need no runtime wiring; the custom generator has to render them too for
/// expression indexes, in the same clause position — after <c>INCLUDE</c>/<c>NULLS NOT DISTINCT</c>,
/// before <c>WHERE</c>. The whitelist and the unknown-key rejection match exact keys, so the prefix
/// needs its own rule in both.
/// </summary>
[TestClass]
public class NpgsqlStorageParameterTests
{
    private class Payload
    {
        public string Json { get; set; } = "";
    }

    private class Document
    {
        public Guid    Id      { get; set; }
        public string  Title   { get; set; } = "";
        public Payload Payload { get; set; } = new();
    }

    private class StorageParameterContext(DbContextOptions<StorageParameterContext> options) : DbContext(options)
    {
        public DbSet<Document> Documents => Set<Document>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Document>(b =>
            {
                b.ToTable("docs");
                b.HasKey(x => x.Id);
                b.Property(x => x.Title).HasColumnName("title");
                b.ComplexProperty(x => x.Payload, c =>
                {
                    c.Property(x => x.Json).HasColumnName("json").HasColumnType("jsonb");

                    // Property-level: forwarded through the whitelist.
                    c.Property(x => x.Json).HasComplexIndex(ix => ix
                        .UseGin()
                        .HasStorageParameter("fastupdate", false)
                        .HasName("ix_docs_json"));
                });

                // Entity-level: stored as JSON in the definition, so the value round-trips through
                // the serializer before it reaches the operation.
                b.HasComplexCompositeIndex(x => new { x.Title, x.Payload.Json }, ix => ix
                    .HasStorageParameter("fillfactor", 70)
                    .HasStorageParameter("deduplicate_items", false)
                    .HasName("ix_docs_title_json"));

                // Expression index: rendered by the custom generator.
                b.HasExpressionIndex(ix => ix
                    .Expression("lower(title)")
                    .HasStorageParameter("fillfactor", 70)
                    .HasFilter("title IS NOT NULL")
                    .HasName("ix_docs_title_ci"));
            });
    }

    private static Dictionary<string, CreateIndexOperation> Creates()
        => MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<StorageParameterContext>())
                           .OfType<CreateIndexOperation>()
                           .ToDictionary(o => o.Name);

    [TestMethod(DisplayName = "A property-level storage parameter is forwarded and rendered by the stock generator")]
    public void Property_level_storage_parameter_renders()
    {
        var op = Creates()["ix_docs_json"];

        Assert.AreEqual(false, op["Npgsql:StorageParameter:fastupdate"]);

        StringAssert.Contains(
            MigrationHarness.NpgsqlSql([op], complexIndexWiring: false),
            "CREATE INDEX ix_docs_json ON docs USING gin (json) WITH (fastupdate=false)");
    }

    [TestMethod(DisplayName = "Entity-level storage parameters survive the JSON round trip as their original types")]
    public void Entity_level_storage_parameters_render()
    {
        var op = Creates()["ix_docs_title_json"];

        // An int, not a double — a boxed 70.0 would render as "70" too, but a generator reading the
        // option `as int?` would drop it, which is how FILLFACTOR went missing once before.
        Assert.AreEqual(70, op["Npgsql:StorageParameter:fillfactor"]);
        Assert.AreEqual(false, op["Npgsql:StorageParameter:deduplicate_items"]);

        var sql = MigrationHarness.NpgsqlSql([op], complexIndexWiring: false);
        StringAssert.Contains(sql, "ON docs (title, json) WITH (");
        StringAssert.Contains(sql, "fillfactor=70");
        StringAssert.Contains(sql, "deduplicate_items=false");
    }

    [TestMethod(DisplayName = "The custom generator renders storage parameters on expression indexes, before WHERE")]
    public void Expression_index_storage_parameter_renders_in_clause_order()
    {
        var op = Creates()["ix_docs_title_ci"];

        StringAssert.Contains(
            MigrationHarness.NpgsqlSql([op]),
            "CREATE INDEX ix_docs_title_ci ON docs ((lower(title))) WITH (fillfactor=70) WHERE title IS NOT NULL");
    }

    [TestMethod(DisplayName = "Storage parameters do not churn between two builds of the same model")]
    public void Storage_parameters_do_not_churn()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<StorageParameterContext>(),
            MigrationHarness.NpgsqlModel<StorageParameterContext>());

        Assert.IsFalse(operations.OfType<CreateIndexOperation>().Any());
        Assert.IsFalse(operations.OfType<DropIndexOperation>().Any());
    }

    [TestMethod(DisplayName = "A string-valued storage parameter is quoted")]
    public void String_storage_parameter_is_quoted()
    {
        var op = new CreateIndexOperation { Name = "ix_s", Table = "docs", Columns = ["lower(title)"] };
        op.AddAnnotation(ComplexIndexAnnotations.IndexParts, IndexPartsSerializer.Serialize([new ResolvedIndexPart(true, "lower(title)")]));
        op.AddAnnotation("Npgsql:StorageParameter:buffering", "on");

        StringAssert.Contains(MigrationHarness.NpgsqlSql([op]), "WITH (buffering='on')");
    }
}
