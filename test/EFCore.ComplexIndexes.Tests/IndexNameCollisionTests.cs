using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// A complex index can resolve to a name already taken by another complex index or a native
/// <c>HasIndex</c>. The base differ emits one and this differ the other, neither seeing the other, so
/// the migration scaffolded two <c>CREATE INDEX</c> statements under one name and failed at apply
/// time (42P07). Where a name is taken depends on the provider: SQLite keeps index names unique
/// across the database and ignores configured schemas, PostgreSQL per schema, SQL Server per table.
/// The check used to be per table everywhere, so on SQLite two tables sharing an index name
/// scaffolded cleanly and failed with "index … already exists".
/// </summary>
[TestClass]
public class IndexNameCollisionTests
{
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

    private class Company
    {
        public Guid   Id   { get; set; }
        public string Name { get; set; } = "";
    }

    private class CollisionContext(DbContextOptions<CollisionContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.Name).HasDatabaseName("IX_dup");
                b.ComplexProperty(x => x.Email);
                b.HasComplexIndex(x => x.Email.Value, indexName: "IX_dup");
            });
    }

    // A native index on one table, a complex index of the same name on another.
    private class NativeOnAnotherTableContext(DbContextOptions<NativeOnAnotherTableContext> options) : DbContext(options)
    {
        public DbSet<Person>  People    => Set<Person>();
        public DbSet<Company> Companies => Set<Company>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Company>(b =>
            {
                b.ToTable("companies");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.Name).HasDatabaseName("IX_name");
            });

            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email);
                b.HasComplexIndex(x => x.Email.Value, indexName: "IX_name");
            });
        }
    }

    // Two complex indexes of one name, on two tables — in two schemas when asked to be.
    private class ComplexOnTwoTablesContext(DbContextOptions options) : DbContext(options)
    {
        protected virtual string? PeopleSchema     => null;
        protected virtual string? CompanySchema    => null;
        protected virtual string  CompanyIndexName => "IX_name";

        public DbSet<Person>  People    => Set<Person>();
        public DbSet<Company> Companies => Set<Company>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Company>(b =>
            {
                b.ToTable("companies", CompanySchema);
                b.HasKey(x => x.Id);
                b.HasComplexIndex(x => x.Name, indexName: CompanyIndexName);
            });

            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people", PeopleSchema);
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email);
                b.HasComplexIndex(x => x.Email.Value, indexName: "IX_name");
            });
        }
    }

    private class ComplexInTwoSchemasContext(DbContextOptions<ComplexInTwoSchemasContext> options) : ComplexOnTwoTablesContext(options)
    {
        protected override string PeopleSchema  => "north";
        protected override string CompanySchema => "south";
    }

    private class ComplexOnTwoTablesFixedContext(DbContextOptions<ComplexOnTwoTablesFixedContext> options) : ComplexOnTwoTablesContext(options)
    {
        protected override string CompanyIndexName => "IX_company_name";
    }

    // Two of EF Core's own indexes share a name; the complex index is named apart.
    private class NativeOnlyCollisionContext(DbContextOptions<NativeOnlyCollisionContext> options) : DbContext(options)
    {
        public DbSet<Person>  People    => Set<Person>();
        public DbSet<Company> Companies => Set<Company>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Company>(b =>
            {
                b.ToTable("companies");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.Name).HasDatabaseName("IX_shared");
            });

            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.Name).HasDatabaseName("IX_shared");
                b.ComplexProperty(x => x.Email);
                b.HasComplexIndex(x => x.Email.Value, indexName: "IX_email");
            });
        }
    }

    // The handover shape: the index used to be native and is now complex, under one name.
    private class NativeBeforeContext(DbContextOptions<NativeBeforeContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.Name).HasDatabaseName("IX_people_name");
                b.ComplexProperty(x => x.Email);
            });
    }

    private class ComplexAfterContext(DbContextOptions<ComplexAfterContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email);
                b.HasComplexIndex(x => x.Email.Value, indexName: "IX_people_name");
            });
    }

    private static List<string> CreatedTables(IReadOnlyList<MigrationOperation> operations, string indexName)
        => [.. operations.OfType<CreateIndexOperation>().Where(o => o.Name == indexName).Select(o => o.Table)];

    // ── Same table: a collision on every provider ──

    [TestMethod(DisplayName = "A complex index named like a native HasIndex on the same table is rejected at migrations add")]
    public void Collision_with_native_index_throws()
    {
        var target = MigrationHarness.SqliteModel<CollisionContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.CoreDiff(null, target));

        StringAssert.Contains(exception.Message, "IX_dup");
        StringAssert.Contains(exception.Message, "HasIndex");
        StringAssert.Contains(exception.Message, "Name");
        StringAssert.Contains(exception.Message, "unique per table");
    }

    [TestMethod(DisplayName = "The satellite differs inherit the check")]
    public void Collision_is_rejected_by_the_npgsql_differ()
    {
        var target = MigrationHarness.NpgsqlModel<CollisionContext>();

        Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(null, target));
    }

    [TestMethod(DisplayName = "SQL Server rejects a same-named native and complex index on one table")]
    public void Collision_is_rejected_by_the_sql_server_differ()
    {
        var target = MigrationHarness.SqlServerModel<CollisionContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.SqlServerDiff(null, target));

        StringAssert.Contains(exception.Message, "IX_dup");
        StringAssert.Contains(exception.Message, "unique per table");
    }

    // ── Core over SQLite: one namespace for the whole database ──

    // The premise behind IndexNameScope.Database, against what a consumer actually runs: EF's SQLite
    // generator, which leaves the schemas out of the DDL, and the engine bundled with
    // Microsoft.Data.Sqlite rather than the sqlite3 CLI. Passes with or without the fix.
    [TestMethod(DisplayName = "SQLite itself rejects one index name on two tables, whatever their schemas")]
    public void Sqlite_engine_rejects_one_index_name_on_two_tables()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();
        using var context = new MigrationHarness.EmptyContext(MigrationHarness.SqliteOptions(connection));

        var migration = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");
        migration.CreateTable("a", t => new { x = t.Column<string>() }, schema: "north");
        migration.CreateTable("b", t => new { x = t.Column<string>() }, schema: "south");
        migration.CreateIndex("IX_name", "a", "x", schema: "north");
        migration.CreateIndex("IX_name", "b", "x", schema: "south");

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(migration.Operations);

        var exception = Assert.ThrowsExactly<SqliteException>(() =>
        {
            foreach (var command in commands)
            {
                using var sql = connection.CreateCommand();
                sql.CommandText = command.CommandText;
                sql.ExecuteNonQuery();
            }
        });

        StringAssert.Contains(exception.Message, "index IX_name already exists");
    }

    [TestMethod(DisplayName = "SQLite: a complex index named like a native index on another table is rejected")]
    public void Sqlite_rejects_native_name_on_another_table()
    {
        var target = MigrationHarness.SqliteModel<NativeOnAnotherTableContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.CoreDiff(null, target));

        StringAssert.Contains(exception.Message, "'IX_name' on table 'people'");
        StringAssert.Contains(exception.Message, "on table 'companies'");
        StringAssert.Contains(exception.Message, "unique across the database");
    }

    [TestMethod(DisplayName = "SQLite: two complex indexes with one name on different tables are rejected")]
    public void Sqlite_rejects_complex_name_on_another_table()
    {
        var target = MigrationHarness.SqliteModel<ComplexOnTwoTablesContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.CoreDiff(null, target));

        StringAssert.Contains(exception.Message, "both resolve to the name 'IX_name'");
        StringAssert.Contains(exception.Message, "on table 'companies'");
        StringAssert.Contains(exception.Message, "on table 'people'");
    }

    // SQLite's provider keeps a configured schema in the model and leaves it out of the DDL, so
    // both indexes land in one database: checking per schema would miss this.
    [TestMethod(DisplayName = "SQLite: tables in different schemas still share one index namespace")]
    public void Sqlite_ignores_schemas()
    {
        var target = MigrationHarness.SqliteModel<ComplexInTwoSchemasContext>();

        Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.CoreDiff(null, target));
    }

    [TestMethod(DisplayName = "SQLite: two of EF Core's own indexes sharing a name are left to EF Core")]
    public void Sqlite_native_only_collision_is_not_reported()
    {
        var target = MigrationHarness.SqliteModel<NativeOnlyCollisionContext>();

        Assert.HasCount(1, CreatedTables(MigrationHarness.CoreDiff(null, target), "IX_email"));
    }

    // Target only: the snapshot is history, and a model that fixes the collision must diff.
    [TestMethod(DisplayName = "SQLite: a cross-table collision in the source model does not block the fix")]
    public void Sqlite_collision_in_source_stays_diffable()
    {
        var operations = MigrationHarness.CoreDiff(
            MigrationHarness.SqliteModel<ComplexOnTwoTablesContext>(),
            MigrationHarness.SqliteModel<ComplexOnTwoTablesFixedContext>());

        CollectionAssert.AreEqual(new[] { "companies" }, CreatedTables(operations, "IX_company_name"));
    }

    [TestMethod(DisplayName = "An index moving from native to complex under one name still diffs")]
    public void Handover_from_native_to_complex_still_diffs()
    {
        var source = MigrationHarness.SqliteModel<NativeBeforeContext>();
        var target = MigrationHarness.SqliteModel<ComplexAfterContext>();

        var operations = MigrationHarness.CoreDiff(source, target);

        // The native index is dropped by the base differ, the complex one created here; only the
        // target's native indexes are consulted, so this is a legitimate move, not a collision.
        Assert.IsTrue(operations.OfType<DropIndexOperation>().Any(o => o.Name == "IX_people_name"));
        Assert.IsTrue(operations.OfType<CreateIndexOperation>().Any(o => o.Name == "IX_people_name"));
    }

    // ── SQL Server: one namespace per table ──

    [TestMethod(DisplayName = "SQL Server: two complex indexes may share a name across tables")]
    public void Sql_server_allows_complex_name_on_another_table()
    {
        var target = MigrationHarness.SqlServerModel<ComplexOnTwoTablesContext>();

        CollectionAssert.AreEquivalent(
            new[] { "companies", "people" },
            CreatedTables(MigrationHarness.SqlServerDiff(null, target), "IX_name"));
    }

    [TestMethod(DisplayName = "SQL Server: a complex index may share a native index's name on another table")]
    public void Sql_server_allows_native_name_on_another_table()
    {
        var target = MigrationHarness.SqlServerModel<NativeOnAnotherTableContext>();

        CollectionAssert.AreEquivalent(
            new[] { "companies", "people" },
            CreatedTables(MigrationHarness.SqlServerDiff(null, target), "IX_name"));
    }

    // ── PostgreSQL: one namespace per schema ──

    [TestMethod(DisplayName = "PostgreSQL: a complex index named like a native index on another table is rejected")]
    public void Npgsql_rejects_native_name_on_another_table()
    {
        var target = MigrationHarness.NpgsqlModel<NativeOnAnotherTableContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(null, target));

        StringAssert.Contains(exception.Message, "HasIndex on table 'companies'");
        StringAssert.Contains(exception.Message, "unique per schema");
    }

    [TestMethod(DisplayName = "PostgreSQL: two complex indexes may share a name across schemas")]
    public void Npgsql_allows_complex_name_in_another_schema()
    {
        var target = MigrationHarness.NpgsqlModel<ComplexInTwoSchemasContext>();

        CollectionAssert.AreEquivalent(
            new[] { "companies", "people" },
            CreatedTables(MigrationHarness.NpgsqlDiff(null, target), "IX_name"));
    }
}
