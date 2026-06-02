using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Regression tests for the phantom complex-index churn: every <c>migrations add</c> re-creating
/// identical indexes. Two independent causes:
/// (1) the diff collects ALL non-core property annotations, so the snapshot model's serialized
///     <c>Relational:ColumnType</c> (absent on the code model) makes every complex index differ;
/// (2) array-valued provider annotations (e.g. operator classes) are compared/hashed by reference,
///     so structurally-equal indexes never match.
/// </summary>
[TestClass]
public class PhantomIndexChurnTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public PhantomIndexChurnTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    private IRelationalModel BuildRelationalModel<TContext>()
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>().UseSqlite(_connection).Options;
        using var context = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        return context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
    }

    private IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        var options = new DbContextOptionsBuilder().UseSqlite(_connection).Options;
        using var context = new EmptyContext(options);
        var differ = new CustomMigrationsModelDiffer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<IMigrationsAnnotationProvider>(),
            context.GetService<IRelationalAnnotationProvider>(),
            context.GetService<IRowIdentityMapFactory>(),
            context.GetService<CommandBatchPreparerDependencies>());
        return differ.GetDifferences(source, target);
    }

    private class EmptyContext(DbContextOptions options) : DbContext(options);

    private class Address
    {
        public string Value { get; set; } = "";
    }

    private class Person
    {
        public Guid Id { get; set; }
        public Address Address { get; set; } = new();
    }

    // Index with an array-valued provider annotation (e.g. operator classes).
    private class ArrayAnnotationContext(DbContextOptions<ArrayAnnotationContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("person");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Address, c =>
                    c.Property(x => x.Value).HasColumnName("value")
                     .HasComplexIndex(ix => ix.HasAnnotation("Test:Operators", new[] { "op_a", "op_b" })));
            });
    }

    // Identical index/value as ArrayAnnotationContext, but a distinct context type — so EF's
    // per-context model cache yields a separate model (and a separate array instance), exactly as
    // the snapshot model and code model differ at `migrations add` time.
    private class ArrayAnnotationCloneContext(DbContextOptions<ArrayAnnotationCloneContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("person");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Address, c =>
                    c.Property(x => x.Value).HasColumnName("value")
                     .HasComplexIndex(ix => ix.HasAnnotation("Test:Operators", new[] { "op_a", "op_b" })));
            });
    }

    // Same index, different array value — a genuine change that MUST still be detected.
    private class ArrayAnnotationChangedContext(DbContextOptions<ArrayAnnotationChangedContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("person");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Address, c =>
                    c.Property(x => x.Value).HasColumnName("value")
                     .HasComplexIndex(ix => ix.HasAnnotation("Test:Operators", new[] { "op_a", "op_c" })));
            });
    }

    // Mimics the SNAPSHOT model: column type serialized via HasColumnType.
    private class ExplicitColumnTypeContext(DbContextOptions<ExplicitColumnTypeContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("person");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Address, c =>
                    c.Property(x => x.Value).HasColumnName("value").HasColumnType("TEXT")
                     .HasComplexIndex(isUnique: true));
            });
    }

    // Mimics the CODE model: column type left to convention (no explicit annotation).
    private class ConventionColumnTypeContext(DbContextOptions<ConventionColumnTypeContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("person");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Address, c =>
                    c.Property(x => x.Value).HasColumnName("value")
                     .HasComplexIndex(isUnique: true));
            });
    }

    [TestMethod(DisplayName = "Array-valued annotation: identical models produce no index operations")]
    public void Array_annotation_no_change_produces_no_operations()
    {
        var source = BuildRelationalModel<ArrayAnnotationContext>();
        var target = BuildRelationalModel<ArrayAnnotationCloneContext>();

        var operations = GetDifferences(source, target);

        Assert.IsEmpty(operations.OfType<CreateIndexOperation>());
        Assert.IsEmpty(operations.OfType<DropIndexOperation>());
    }

    [TestMethod(DisplayName = "ColumnType asymmetry (snapshot vs code model) produces no index operations")]
    public void ColumnType_asymmetry_produces_no_operations()
    {
        var source = BuildRelationalModel<ExplicitColumnTypeContext>();
        var target = BuildRelationalModel<ConventionColumnTypeContext>();

        var operations = GetDifferences(source, target);

        Assert.IsEmpty(operations.OfType<CreateIndexOperation>());
        Assert.IsEmpty(operations.OfType<DropIndexOperation>());
    }

    [TestMethod(DisplayName = "Array-valued annotation: a changed value is still detected")]
    public void Array_annotation_value_change_is_detected()
    {
        var source = BuildRelationalModel<ArrayAnnotationContext>();
        var target = BuildRelationalModel<ArrayAnnotationChangedContext>();

        var operations = GetDifferences(source, target);

        Assert.IsNotEmpty(operations.OfType<CreateIndexOperation>());
        Assert.IsNotEmpty(operations.OfType<DropIndexOperation>());
    }
}
