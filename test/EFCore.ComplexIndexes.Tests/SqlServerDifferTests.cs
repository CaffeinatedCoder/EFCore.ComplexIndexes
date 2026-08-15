using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace EFCore.ComplexIndexes.Tests;

[TestClass]
public class SqlServerDifferTests
{
    // ── Harness ──

    private static IRelationalModel BuildRelationalModel<TContext>() where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
                     .UseSqlServer("Server=localhost;Database=test;Trusted_Connection=True")
                     .Options;

        using var context = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        return context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
    }

    private static IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        var options = new DbContextOptionsBuilder()
                     .UseSqlServer("Server=localhost;Database=test;Trusted_Connection=True")
                     .Options;

        using var context = new EmptyContext(options);

        var differ = new SqlServerComplexIndexMigrationsModelDiffer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<IMigrationsAnnotationProvider>(),
            context.GetService<IRelationalAnnotationProvider>(),
            context.GetService<IRowIdentityMapFactory>(),
            context.GetService<CommandBatchPreparerDependencies>()
        );

        return differ.GetDifferences(source, target);
    }

    // End-to-end SQL through the stock SQL Server migrations generator — no runtime wiring needed.
    private static string GenerateSql(IReadOnlyList<MigrationOperation> operations)
    {
        var options = new DbContextOptionsBuilder()
                     .UseSqlServer("Server=localhost;Database=test;Trusted_Connection=True")
                     .Options;

        using var context   = new EmptyContext(options);
        var       generator = context.GetService<IMigrationsSqlGenerator>();

        return string.Join("\n", generator.Generate(operations, model: null).Select(c => c.CommandText));
    }

    private class EmptyContext(DbContextOptions options) : DbContext(options);

    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class Person
    {
        public Guid         Id    { get; set; }
        public string       Name  { get; set; } = "";
        public EmailAddress Email { get; set; } = new();
    }

    private static void MapPerson(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Person> b)
    {
        b.ToTable("person");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasColumnName("name");
        b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasColumnName("email").HasMaxLength(256));
    }

    private class CoveringIndexContext(DbContextOptions<CoveringIndexContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                // Include entries are property paths since v5 ("Name" resolves to its column);
                // unknown entries still pass through verbatim as column names.
                b.HasComplexIndex(x => x.Email.Value, ix => ix
                    .IsUnique()
                    .HasName("ux_person_email")
                    .IncludeProperties("Name")
                    .IsCreatedOnline()
                    .HasFillFactor(80));
            });
    }

    private class FilteredDescendingContext(DbContextOptions<FilteredDescendingContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasComplexCompositeIndex(
                    x => new { x.Name, Email = DbOrder.Desc(x.Email.Value) },
                    filter:    "[name] IS NOT NULL",
                    indexName: "ix_person_name_email");
            });
    }

    private class NullsOrderingContext(DbContextOptions<NullsOrderingContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasComplexIndex(x => DbOrder.NullsFirst(x.Name), indexName: "ix_person_name");
            });
    }

    // ── Tests ──

    [TestMethod(DisplayName = "SQL Server index options are forwarded and rendered by the stock generator")]
    public void SqlServer_options_are_forwarded_and_rendered()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<CoveringIndexContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual("ux_person_email", createIndex.Name);
        Assert.AreEqual(true, createIndex["SqlServer:Online"]);
        Assert.AreEqual(80,   createIndex["SqlServer:FillFactor"]);
        Assert.IsTrue(((string[])createIndex["SqlServer:Include"]!).SequenceEqual(["name"]));

        var sql = GenerateSql([createIndex]);
        StringAssert.Contains(sql, "CREATE UNIQUE INDEX [ux_person_email] ON [person] ([email])");
        StringAssert.Contains(sql, "INCLUDE ([name])");
        StringAssert.Contains(sql, "ONLINE = ON");
        StringAssert.Contains(sql, "FILLFACTOR = 80");
    }

    [TestMethod(DisplayName = "Column facets are not forwarded onto the operation")]
    public void Column_facets_are_not_forwarded()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<CoveringIndexContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.IsNull(createIndex.FindAnnotation("Relational:ColumnName"));
        Assert.IsNull(createIndex.FindAnnotation("MaxLength"));
    }

    [TestMethod(DisplayName = "Filtered descending composite renders WHERE and DESC")]
    public void Filtered_descending_composite_renders()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<FilteredDescendingContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.IsNotNull(createIndex.IsDescending);

        var sql = GenerateSql([createIndex]);
        StringAssert.Contains(sql, "[name], [email] DESC");
        StringAssert.Contains(sql, "WHERE [name] IS NOT NULL");
    }

    [TestMethod(DisplayName = "NULLS FIRST/LAST is rejected with a clear error")]
    public void Nulls_ordering_is_rejected()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => GetDifferences(source: null, target: BuildRelationalModel<NullsOrderingContext>()));

        StringAssert.Contains(exception.Message, "NULLS FIRST/LAST");
    }

    private class DataCompressionContext(DbContextOptions<DataCompressionContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasComplexIndex(x => x.Email.Value, ix => ix
                    .HasName("ix_person_email_compressed")
                    .UseDataCompression(DataCompressionType.Page));
            });
    }

    [TestMethod(DisplayName = "Data compression is forwarded and rendered by the stock generator")]
    public void Data_compression_is_forwarded()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<DataCompressionContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual(DataCompressionType.Page, createIndex["SqlServer:DataCompression"]);

        StringAssert.Contains(GenerateSql(operations), "DATA_COMPRESSION = PAGE");
    }
}
