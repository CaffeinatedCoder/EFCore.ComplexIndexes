using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// The v5 whitelist: the Npgsql differ forwards exactly the <c>Npgsql:*</c> index-option keys onto
/// the index operation — and nothing else from the property (column facets like
/// <c>Relational:ColumnName</c> leaked into scaffolded migrations before v5).
/// </summary>
[TestClass]
public class NpgsqlAnnotationForwardingTests
{
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

    private class Payload
    {
        public string Json { get; set; } = "";
    }

    private class Document
    {
        public Guid    Id      { get; set; }
        public Payload Payload { get; set; } = new();
    }

    private class GinIndexContext(DbContextOptions<GinIndexContext> options) : DbContext(options)
    {
        public DbSet<Document> Documents => Set<Document>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Document>(b =>
            {
                b.ToTable("documents");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Payload, c =>
                    c.Property(x => x.Json)
                     .HasColumnName("payload")
                     .HasColumnType("jsonb")
                     .HasComplexIndex(ix => ix.UseGin().HasOperators("jsonb_path_ops")));
            });
    }

    [TestMethod(DisplayName = "Npgsql index options are forwarded onto the operation")]
    public void Npgsql_index_options_are_forwarded()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<GinIndexContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual("gin", createIndex["Npgsql:IndexMethod"]);
        Assert.IsTrue(((string[])createIndex["Npgsql:IndexOperators"]!).SequenceEqual(["jsonb_path_ops"]));
    }

    [TestMethod(DisplayName = "Column facets are not forwarded onto the operation")]
    public void Column_facets_are_not_forwarded()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<GinIndexContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.IsNull(createIndex.FindAnnotation("Relational:ColumnName"));
        Assert.IsNull(createIndex.FindAnnotation("Relational:ColumnType"));
    }
}
