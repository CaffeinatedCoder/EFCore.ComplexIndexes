using EFCore.ComplexIndexes.PostgreSQL;
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
/// Diff polish (v5): a drop/create pair that differs only by name becomes a
/// <see cref="RenameIndexOperation"/> on providers that can rename standalone (the core default
/// stays drop + create), and a table rename no longer drops and recreates the indexes it carries.
/// </summary>
[TestClass]
public class IndexRenameTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public IndexRenameTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    // ── Harnesses: core differ on SQLite, Npgsql differ on PostgreSQL ──

    private IRelationalModel BuildSqliteModel<TContext>() where TContext : DbContext
        => MigrationHarness.SqliteModel<TContext>(_connection);

    private IReadOnlyList<MigrationOperation> CoreDifferences(IRelationalModel? source, IRelationalModel? target)
        => MigrationHarness.CoreDiff(_connection, source, target);

    private static IRelationalModel BuildNpgsqlModel<TContext>() where TContext : DbContext
        => MigrationHarness.NpgsqlModel<TContext>();

    private static IReadOnlyList<MigrationOperation> NpgsqlDifferences(IRelationalModel? source, IRelationalModel? target)
        => MigrationHarness.NpgsqlDiff(source, target);

    private class EmptyContext(DbContextOptions options) : DbContext(options);

    // ── Models ──

    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class Person
    {
        public Guid         Id    { get; set; }
        public EmailAddress Email { get; set; } = new();
    }

    private class IndexedContext(DbContextOptions options, string table, string indexName, bool isUnique) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable(table);
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email, c =>
                    c.Property(x => x.Value).HasColumnName("email")
                     .HasComplexIndex(isUnique: isUnique, indexName: indexName));
            });
    }

    private class OldNameContext(DbContextOptions<OldNameContext> options)
        : IndexedContext(options, "person", "ix_person_email_v1", isUnique: false);

    private class NewNameContext(DbContextOptions<NewNameContext> options)
        : IndexedContext(options, "person", "ix_person_email_v2", isUnique: false);

    private class NewNameUniqueContext(DbContextOptions<NewNameUniqueContext> options)
        : IndexedContext(options, "person", "ix_person_email_v2", isUnique: true);

    private class RenamedTableContext(DbContextOptions<RenamedTableContext> options)
        : IndexedContext(options, "people", "ix_person_email_v1", isUnique: false);

    private class RenamedTableRenamedIndexContext(DbContextOptions<RenamedTableRenamedIndexContext> options)
        : IndexedContext(options, "people", "ix_person_email_v2", isUnique: false);

    private class RenamedTableNoIndexContext(DbContextOptions<RenamedTableNoIndexContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasColumnName("email"));
            });
    }

    // ── Rename detection ──

    [TestMethod(DisplayName = "Npgsql: a name-only change becomes a RenameIndexOperation")]
    public void Name_only_change_becomes_rename_on_npgsql()
    {
        var operations = NpgsqlDifferences(
            BuildNpgsqlModel<OldNameContext>(),
            BuildNpgsqlModel<NewNameContext>());

        var rename = Assert.ContainsSingle(operations.OfType<RenameIndexOperation>());
        Assert.AreEqual("ix_person_email_v1", rename.Name);
        Assert.AreEqual("ix_person_email_v2", rename.NewName);
        Assert.AreEqual("person", rename.Table);

        Assert.IsEmpty(operations.OfType<DropIndexOperation>());
        Assert.IsEmpty(operations.OfType<CreateIndexOperation>());
    }

    [TestMethod(DisplayName = "Npgsql: a name change plus a real change still drops and recreates")]
    public void Name_and_uniqueness_change_still_rebuilds()
    {
        var operations = NpgsqlDifferences(
            BuildNpgsqlModel<OldNameContext>(),
            BuildNpgsqlModel<NewNameUniqueContext>());

        Assert.IsEmpty(operations.OfType<RenameIndexOperation>());
        Assert.ContainsSingle(operations.OfType<DropIndexOperation>());
        Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
    }

    [TestMethod(DisplayName = "Core default: a name-only change stays drop + create")]
    public void Core_default_keeps_drop_and_create()
    {
        var operations = CoreDifferences(
            BuildSqliteModel<OldNameContext>(),
            BuildSqliteModel<NewNameContext>());

        Assert.IsEmpty(operations.OfType<RenameIndexOperation>());
        Assert.ContainsSingle(operations.OfType<DropIndexOperation>());
        Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
    }

    // ── Table renames ──

    [TestMethod(DisplayName = "A renamed table keeps its complex indexes without churn")]
    public void Renamed_table_produces_no_index_operations()
    {
        var operations = CoreDifferences(
            BuildSqliteModel<OldNameContext>(),
            BuildSqliteModel<RenamedTableContext>());

        Assert.IsNotEmpty(operations.OfType<RenameTableOperation>());
        Assert.IsEmpty(operations.OfType<DropIndexOperation>());
        Assert.IsEmpty(operations.OfType<CreateIndexOperation>());
    }

    [TestMethod(DisplayName = "Index removed while its table is renamed: drop targets the old table, before the rename")]
    public void Drop_on_renamed_table_targets_old_name_before_rename()
    {
        var operations = CoreDifferences(
            BuildSqliteModel<OldNameContext>(),
            BuildSqliteModel<RenamedTableNoIndexContext>());

        var drop = Assert.ContainsSingle(operations.OfType<DropIndexOperation>());
        Assert.AreEqual("person", drop.Table);

        var dropPosition   = operations.ToList().IndexOf(drop);
        var renamePosition = operations.ToList().IndexOf(operations.OfType<RenameTableOperation>().Single());
        Assert.IsLessThan(renamePosition, dropPosition,
            "The index drop runs before the base operations rename the table, so it must target the old table name.");
    }

    [TestMethod(DisplayName = "Npgsql: table rename plus index rename yields a single RenameIndexOperation on the new table")]
    public void Table_and_index_rename_combine()
    {
        var operations = NpgsqlDifferences(
            BuildNpgsqlModel<OldNameContext>(),
            BuildNpgsqlModel<RenamedTableRenamedIndexContext>());

        var rename = Assert.ContainsSingle(operations.OfType<RenameIndexOperation>());
        Assert.AreEqual("ix_person_email_v1", rename.Name);
        Assert.AreEqual("ix_person_email_v2", rename.NewName);
        Assert.AreEqual("people", rename.Table);

        Assert.IsEmpty(operations.OfType<DropIndexOperation>());
        Assert.IsEmpty(operations.OfType<CreateIndexOperation>());

        // The index rename must run after the table rename.
        var indexRenamePosition = operations.ToList().IndexOf(rename);
        var tableRenamePosition = operations.ToList().IndexOf(operations.OfType<RenameTableOperation>().Single());
        Assert.IsLessThan(indexRenamePosition, tableRenamePosition);
    }
}
