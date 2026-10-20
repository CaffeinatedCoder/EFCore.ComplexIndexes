using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// An index with <c>NULLS FIRST</c>/<c>NULLS LAST</c> ordering goes through the parts annotation,
/// which only the PostgreSQL satellite's generator renders, with the runtime-wiring sentinel in its
/// columns. On SQLite, where no satellite exists, that sentinel failed only while double-quoted
/// string literals are off: with them on, as in a default build of SQLite, it was taken for a string
/// and the index applied with a constant column and without its ordering (#134). The core differ
/// now refuses the declaration on a provider no satellite serves.
/// </summary>
[TestClass]
public class SqliteNullOrderingTests
{
    public class EmailAddress
    {
        public string? Value { get; set; }
    }

    public class Member
    {
        public int          Id    { get; set; }
        public EmailAddress Email { get; set; } = new();
    }

    private sealed class NullsFirstContext(DbContextOptions<NullsFirstContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Member>(b =>
            {
                b.ToTable("members");
                b.ComplexProperty(x => x.Email);
                b.HasComplexIndex(x => DbOrder.NullsFirst(x.Email.Value), isUnique: true, indexName: "ux_members_email");
            });
    }

    private sealed class NullsLastContext(DbContextOptions<NullsLastContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Member>(b =>
            {
                b.ToTable("members");
                b.ComplexProperty(x => x.Email);
                b.HasComplexCompositeIndex(x => new { x.Id, Email = DbOrder.NullsLast(x.Email.Value) }, indexName: "ix_members_email");
            });
    }

    // The PostgreSQL package's API, in a model shared with SQLite tests.
    private sealed class ExpressionContext(DbContextOptions<ExpressionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Member>(b =>
            {
                b.ToTable("members");
                b.ComplexProperty(x => x.Email);
                b.HasExpressionIndex("lower(\"Email_Value\")", isUnique: true);
            });
    }

    private sealed class PlainContext(DbContextOptions<PlainContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Member>(b =>
            {
                b.ToTable("members");
                b.ComplexProperty(x => x.Email);
                b.HasComplexIndex(x => x.Email.Value, isUnique: true, indexName: "ux_members_email");
            });
    }

    private static SqliteConnection OpenWithDoubleQuotedStrings()
    {
        var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        const int dqsDdl = 1014; // SQLITE_DBCONFIG_DQS_DDL, on in a default build of SQLite
        Assert.AreEqual(0, SQLitePCL.raw.sqlite3_db_config(connection.Handle!, dqsDdl, 1, out int enabled));
        Assert.AreEqual(1, enabled);
        return connection;
    }

    private static void Apply(SqliteConnection connection, IReadOnlyList<MigrationOperation> operations, IRelationalModel target)
    {
        using var context = new MigrationHarness.EmptyContext(
            new DbContextOptionsBuilder().UseSqlite(connection).UseComplexIndexes().Options);
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, target.Model);
        context.GetService<IMigrationCommandExecutor>().ExecuteNonQuery(commands, context.GetService<IRelationalConnection>());
    }

    private static string? IndexSql(SqliteConnection connection, string index)
    {
        using var sql = connection.CreateCommand();
        sql.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = $name";
        sql.Parameters.AddWithValue("$name", index);
        return sql.ExecuteScalar() as string;
    }

    [TestMethod(DisplayName = "On SQLite with double-quoted strings on, a NULLS FIRST index fails at migrations add instead of applying")]
    public void Nulls_first_is_refused_before_it_can_apply()
    {
        using var connection = OpenWithDoubleQuotedStrings();
        var target = MigrationHarness.SqliteModel<NullsFirstContext>(connection);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => Apply(connection, MigrationHarness.CoreDiff(connection, null, target), target));

        StringAssert.Contains(exception.Message, "'ux_members_email' on table 'members' declares NULLS FIRST/LAST ordering");
        StringAssert.Contains(exception.Message, "SQLite has no satellite");
        StringAssert.Contains(exception.Message, "Remove the DbOrder.NullsFirst/NullsLast marker");
        Assert.IsNull(IndexSql(connection, "ux_members_email"));
    }

    [TestMethod(DisplayName = "A composite index with a NULLS LAST part is refused on SQLite")]
    public void Nulls_last_part_is_refused()
    {
        using var connection = OpenWithDoubleQuotedStrings();
        var target = MigrationHarness.SqliteModel<NullsLastContext>(connection);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => Apply(connection, MigrationHarness.CoreDiff(connection, null, target), target));

        StringAssert.Contains(exception.Message, "'ix_members_email'");
    }

    [TestMethod(DisplayName = "An expression part is refused on SQLite, where its quoted SQL would be taken for a string")]
    public void Expression_part_is_refused()
    {
        using var connection = OpenWithDoubleQuotedStrings();
        var target = MigrationHarness.SqliteModel<ExpressionContext>(connection);

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => Apply(connection, MigrationHarness.CoreDiff(connection, null, target), target));

        StringAssert.Contains(exception.Message, "contains a SQL expression part");
        StringAssert.Contains(exception.Message, "SQLite has no satellite");
    }

    [TestMethod(DisplayName = "A database that applied the index with its constant column is fixed by removing the marker")]
    public void Snapshot_with_the_index_stays_diffable_and_the_index_is_fixed()
    {
        using var connection = OpenWithDoubleQuotedStrings();
        var source = MigrationHarness.SqliteModel<NullsFirstContext>(connection);
        var target = MigrationHarness.SqliteModel<PlainContext>(connection);

        // What a migration scaffolded before the fix applied on such a database.
        using (var sql = connection.CreateCommand())
        {
            sql.CommandText = "CREATE TABLE \"members\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_members\" PRIMARY KEY AUTOINCREMENT, \"Email_Value\" TEXT NULL);"
                            + $"CREATE UNIQUE INDEX \"ux_members_email\" ON \"members\" (\"Email_Value\", \"{CustomMigrationsModelDiffer.RuntimeWiringSentinel}\");";
            sql.ExecuteNonQuery();
        }
        StringAssert.Contains(IndexSql(connection, "ux_members_email"), CustomMigrationsModelDiffer.RuntimeWiringSentinel);

        var operations = MigrationHarness.CoreDiff(connection, source, target);
        Assert.IsTrue(operations.OfType<DropIndexOperation>().Any(o => o.Name == "ux_members_email"));
        Apply(connection, operations, target);

        Assert.AreEqual("CREATE UNIQUE INDEX \"ux_members_email\" ON \"members\" (\"Email_Value\")", IndexSql(connection, "ux_members_email"));
    }
}
