using System.ComponentModel;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace EFCore.ComplexIndexes.Tests;

[TestClass]
public class MigrationsModelDifferTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public MigrationsModelDifferTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    // ── Helper: Build a relational model from a DbContext configurator ──

    private IRelationalModel BuildRelationalModel<TContext>()
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
                     .UseSqlite(_connection)
                     .Options;

        using var context = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        var       model   = context.GetService<IDesignTimeModel>().Model;
        return model.GetRelationalModel();
    }

    // ── Helper: Run the differ ──

    private IReadOnlyList<MigrationOperation> GetDifferences(
        IRelationalModel? source,
        IRelationalModel? target
    )
    {
        var options = new DbContextOptionsBuilder()
                     .UseSqlite(_connection)
                     .Options;

        using var context = new EmptyContext(options);

        var differ = new CustomMigrationsModelDiffer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<IMigrationsAnnotationProvider>(),
            context.GetService<IRelationalAnnotationProvider>(),
            context.GetService<IRowIdentityMapFactory>(),
            context.GetService<CommandBatchPreparerDependencies>()
        );

        return differ.GetDifferences(source, target);
    }

    // ── Contexts for testing ──

    private class EmptyContext(DbContextOptions options) : DbContext(options);

    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class PersonV1
    {
        public Guid         Id           { get; set; }
        public string       Name         { get; set; } = "";
        public EmailAddress EmailAddress { get; set; } = new();
    }

    // V1: no indexes
    private class ContextV1(DbContextOptions<ContextV1> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.ComplexProperty(x => x.EmailAddress, c => { c.Property(x => x.Value).HasColumnName("email_address"); });
            });
        }
    }

    // V2: single-column index added
    private class ContextV2(DbContextOptions<ContextV2> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.ComplexProperty(x => x.EmailAddress, c =>
                {
                    c.Property(x => x.Value)
                     .HasColumnName("email_address")
                     .HasComplexIndex(isUnique: true);
                });
            });
        }
    }

    // V3: composite index
    private class ContextV3(DbContextOptions<ContextV3> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Name).HasColumnName("name");
                builder.ComplexProperty(x => x.EmailAddress, c => { c.Property(x => x.Value).HasColumnName("email_address"); });
                builder.HasComplexCompositeIndex(
                    x => new { x.Name, x.EmailAddress.Value },
                    isUnique: true);
            });
        }
    }

    // ── Tests ──

    [TestMethod(DisplayName = "Initial migration creates index")]
    public void Initial_migration_creates_index()
    {
        var target     = BuildRelationalModel<ContextV2>();
        var operations = GetDifferences(source: null, target: target);

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual("person",        createIndex.Table);
        Assert.AreEqual("email_address", Assert.ContainsSingle(createIndex.Columns));
        Assert.IsTrue(createIndex.IsUnique);
        Assert.AreEqual("IX_person_email_address", createIndex.Name);
    }

    [TestMethod(DisplayName = "Adding index to existing table")]
    public void Adding_index_to_existing_table()
    {
        var source     = BuildRelationalModel<ContextV1>();
        var target     = BuildRelationalModel<ContextV2>();
        var operations = GetDifferences(source, target);

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual("email_address", Assert.ContainsSingle(createIndex.Columns));
        Assert.IsTrue(createIndex.IsUnique);
    }

    [TestMethod(DisplayName = "Removing index from existing table")]
    public void Removing_index_from_existing_table()
    {
        var source     = BuildRelationalModel<ContextV2>();
        var target     = BuildRelationalModel<ContextV1>();
        var operations = GetDifferences(source, target);

        var dropIndex = Assert.ContainsSingle(operations.OfType<DropIndexOperation>());
        Assert.AreEqual("IX_person_email_address", dropIndex.Name);
    }

    [TestMethod(DisplayName = "No changes produces no index operations")]
    public void No_changes_produces_no_index_operations()
    {
        var source     = BuildRelationalModel<ContextV2>();
        var target     = BuildRelationalModel<ContextV2>();
        var operations = GetDifferences(source, target);

        Assert.IsEmpty(operations.OfType<CreateIndexOperation>());
        Assert.IsEmpty(operations.OfType<DropIndexOperation>());
    }

    [TestMethod(DisplayName = "Composite index creates multi-column")]
    public void Composite_index_creates_multi_column()
    {
        var target     = BuildRelationalModel<ContextV3>();
        var operations = GetDifferences(source: null, target: target);

        var      createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        string[] columnNames = ["name", "email_address"];
        Assert.IsTrue(createIndex.Columns.SequenceEqual(columnNames));
        Assert.IsTrue(createIndex.IsUnique);
    }

    [TestMethod(DisplayName = "Filtered index preserves filter")]
    public void Filtered_index_preserves_filter()
    {
        // Build a context with a filtered index
        // (using ContextV2 modified with filter, or add another context variant)
        var target     = BuildRelationalModel<ContextWithFilteredIndex>();
        var operations = GetDifferences(source: null, target: target);

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual("deleted_at IS NULL", createIndex.Filter);
    }

    private class ContextWithFilteredIndex(
        DbContextOptions<ContextWithFilteredIndex> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.ComplexProperty(x => x.EmailAddress, c =>
                {
                    c.Property(x => x.Value)
                     .HasColumnName("email_address")
                     .HasComplexIndex(
                          isUnique: true,
                          filter: "deleted_at IS NULL");
                });
            });
        }
    }

    [TestMethod(DisplayName = "Dropping table does not emit separate drop index")]
    public void Dropping_table_does_not_emit_separate_drop_index()
    {
        var source = BuildRelationalModel<ContextV2>();
        // Target has no entities at all
        var target     = BuildRelationalModel<EmptyModelContext>();
        var operations = GetDifferences(source, target);

        // Table drop should exist, but no separate DropIndex
        Assert.IsNotEmpty(operations.OfType<DropTableOperation>());
        Assert.IsEmpty(operations.OfType<DropIndexOperation>());
    }

    private class EmptyModelContext(
        DbContextOptions<EmptyModelContext> options) : DbContext(options);
}