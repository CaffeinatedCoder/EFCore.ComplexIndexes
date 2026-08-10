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
/// Regression tests for operation ordering: the differ's custom DropIndex operations must precede
/// the base operations. An index that moves between a native <c>HasIndex</c> and a complex-index
/// declaration surfaces as a base-emitted CreateIndex plus our DropIndex of the same name (the
/// scaffolded migration collided at apply time before v5), and a removed complex property surfaces
/// as a base DropColumn that would take the index down with it.
/// </summary>
[TestClass]
public class OperationOrderingTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public OperationOrderingTests()
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

    private const string SharedIndexName = "ix_person_email";

    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class PersonComplex
    {
        public Guid         Id           { get; set; }
        public EmailAddress EmailAddress { get; set; } = new();
    }

    private class PersonFlat
    {
        public Guid   Id    { get; set; }
        public string Email { get; set; } = "";
    }

    private class PersonBare
    {
        public Guid Id { get; set; }
    }

    // Same table and column as FlatIndexContext, but the index is a complex-index declaration.
    private class ComplexIndexContext(DbContextOptions<ComplexIndexContext> options) : DbContext(options)
    {
        public DbSet<PersonComplex> People => Set<PersonComplex>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<PersonComplex>(b =>
            {
                b.ToTable("person");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.EmailAddress, c =>
                    c.Property(x => x.Value).HasColumnName("email")
                     .HasComplexIndex(indexName: SharedIndexName));
            });
    }

    // Same table and column as ComplexIndexContext, but the index is EF-native.
    private class FlatIndexContext(DbContextOptions<FlatIndexContext> options) : DbContext(options)
    {
        public DbSet<PersonFlat> People => Set<PersonFlat>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<PersonFlat>(b =>
            {
                b.ToTable("person");
                b.HasKey(x => x.Id);
                b.Property(x => x.Email).HasColumnName("email");
                b.HasIndex(x => x.Email).HasDatabaseName(SharedIndexName);
            });
    }

    private class BareContext(DbContextOptions<BareContext> options) : DbContext(options)
    {
        public DbSet<PersonBare> People => Set<PersonBare>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<PersonBare>(b =>
            {
                b.ToTable("person");
                b.HasKey(x => x.Id);
            });
    }

    [TestMethod(DisplayName = "Complex index → native index: drop of the same-named index precedes the base create")]
    public void Redeclared_index_drop_precedes_same_named_create()
    {
        var source = BuildRelationalModel<ComplexIndexContext>();
        var target = BuildRelationalModel<FlatIndexContext>();

        var operations = GetDifferences(source, target);

        var dropPosition   = IndexOf(operations, (DropIndexOperation op) => op.Name == SharedIndexName);
        var createPosition = IndexOf(operations, (CreateIndexOperation op) => op.Name == SharedIndexName);

        Assert.IsGreaterThanOrEqualTo(0, dropPosition,   "Expected a DropIndexOperation for the complex index.");
        Assert.IsGreaterThanOrEqualTo(0, createPosition, "Expected a CreateIndexOperation for the native index.");
        Assert.IsLessThan(createPosition, dropPosition,
            $"DropIndex (position {dropPosition}) must precede the same-named CreateIndex (position {createPosition}), " +
            "otherwise the migration collides at apply time.");
    }

    [TestMethod(DisplayName = "Removed complex property: index drop precedes the column drop")]
    public void Index_drop_precedes_column_drop()
    {
        var source = BuildRelationalModel<ComplexIndexContext>();
        var target = BuildRelationalModel<BareContext>();

        var operations = GetDifferences(source, target);

        var dropIndexPosition  = IndexOf(operations, (DropIndexOperation op) => op.Name == SharedIndexName);
        var dropColumnPosition = IndexOf(operations, (DropColumnOperation op) => op.Name == "email");

        Assert.IsGreaterThanOrEqualTo(0, dropIndexPosition,  "Expected a DropIndexOperation for the complex index.");
        Assert.IsGreaterThanOrEqualTo(0, dropColumnPosition, "Expected a DropColumnOperation for the removed column.");
        Assert.IsLessThan(dropColumnPosition, dropIndexPosition,
            "DropIndex must precede DropColumn — dropping the column first takes the index down with it, " +
            "and the explicit DROP INDEX then fails.");
    }

    private static int IndexOf<TOperation>(
        IReadOnlyList<MigrationOperation> operations,
        Func<TOperation, bool>            predicate
    ) where TOperation : MigrationOperation
    {
        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i] is TOperation op && predicate(op))
                return i;
        }

        return -1;
    }
}
