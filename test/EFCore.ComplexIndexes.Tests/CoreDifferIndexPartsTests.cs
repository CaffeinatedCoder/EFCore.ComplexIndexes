using EFCore.ComplexIndexes.PostgreSQL;
using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// #134 refused <c>NULLS FIRST</c>/<c>NULLS LAST</c> ordering and expression parts on providers no
/// satellite serves, and returned early on the others. On SQL Server and PostgreSQL the core differ
/// runs in place of the satellite's under <c>UseComplexIndexes()</c> or without the satellite's
/// <c>.targets</c>, and it scaffolded such an index with the runtime-wiring sentinel, which failed at
/// apply naming <c>UseNpgsqlComplexIndexes()</c>, on SQL Server too. The packed core README said
/// <c>migrations add</c> fails (#172). It now does.
/// </summary>
[TestClass]
public class CoreDifferIndexPartsTests
{
    private class Commit
    {
        public int       Id         { get; set; }
        public DateTime  Created    { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string    Title      { get; set; } = "";
    }

    private class NullsLastContext(DbContextOptions<NullsLastContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Commit>(b => b.HasComplexCompositeIndex(
                   x => new { x.Created, Reviewed = DbOrder.NullsLast(x.ReviewedAt) }, indexName: "ix_commits_reviewed"));
    }

    private class ExpressionContext(DbContextOptions<ExpressionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Commit>(b => b.HasExpressionIndex("lower(\"Title\")", indexName: "ix_commits_title"));
    }

    private class PlainContext(DbContextOptions<PlainContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Commit>(b => b.HasComplexIndex(x => x.Created, indexName: "ix_commits_created"));
    }

    private static DbContextOptions SqlServer => new DbContextOptionsBuilder().UseSqlServer(MigrationHarness.SqlServerConnection).Options;

    private static IRelationalModel SqlServerModel(bool expression)
        => expression ? MigrationHarness.SqlServerModel<ExpressionContext>() : MigrationHarness.SqlServerModel<NullsLastContext>();

    private static IRelationalModel NpgsqlModel(bool expression)
        => expression ? MigrationHarness.NpgsqlModel<ExpressionContext>() : MigrationHarness.NpgsqlModel<NullsLastContext>();

    [TestMethod(DisplayName = "On SQL Server the core differ refuses NULLS ordering and expression parts, which neither differ renders")]
    [DataRow(false, "ix_commits_reviewed", "declares NULLS FIRST/LAST ordering", "Remove the DbOrder.NullsFirst/NullsLast marker.")]
    [DataRow(true,  "ix_commits_title",    "contains a SQL expression part",     "a computed column holding the expression")]
    public void Core_differ_on_sqlserver_refuses_parts(bool expression, string name, string what, string remedy)
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => MigrationHarness.Diff<CustomMigrationsModelDiffer>(SqlServer, null, SqlServerModel(expression)));

        StringAssert.Contains(ex.Message, $"The complex index '{name}' on table 'Commit' {what}");
        StringAssert.Contains(ex.Message, "SQL Server cannot express it, and the EFCore.ComplexIndexes.SqlServer satellite refuses it as well");
        StringAssert.Contains(ex.Message, remedy);
        Assert.DoesNotContain("UseNpgsqlComplexIndexes", ex.Message);
    }

    [TestMethod(DisplayName = "The SQL Server satellite still refuses NULLS ordering and expression parts with its own message")]
    [DataRow(false, "declares NULLS FIRST/LAST ordering, which SQL Server does not support")]
    [DataRow(true,  "contains a SQL expression part. SQL Server has no expression-index DDL")]
    public void Sqlserver_satellite_refuses_parts(bool expression, string what)
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => MigrationHarness.Diff<SqlServerComplexIndexMigrationsModelDiffer>(SqlServer, null, SqlServerModel(expression)));

        StringAssert.Contains(ex.Message, what);
    }

    [TestMethod(DisplayName = "On PostgreSQL the core differ refuses NULLS ordering and expression parts, naming UseNpgsqlComplexIndexes()")]
    [DataRow(false, "ix_commits_reviewed", "declares NULLS FIRST/LAST ordering")]
    [DataRow(true,  "ix_commits_title",    "contains a SQL expression part")]
    public void Core_differ_on_npgsql_refuses_parts(bool expression, string name, string what)
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => MigrationHarness.Diff<CustomMigrationsModelDiffer>(MigrationHarness.NpgsqlOptions(), null, NpgsqlModel(expression)));

        StringAssert.Contains(ex.Message, $"The complex index '{name}' on table 'Commit' {what}, which only the EFCore.ComplexIndexes.PostgreSQL satellite renders");
        StringAssert.Contains(ex.Message, "call UseNpgsqlComplexIndexes() instead of UseComplexIndexes()");
    }

    [TestMethod(DisplayName = "The PostgreSQL satellite still renders NULLS ordering and expression parts")]
    [DataRow(false, "\"ReviewedAt\" NULLS LAST")]
    [DataRow(true,  "(lower(\"Title\"))")]
    public void Npgsql_satellite_renders_parts(bool expression, string rendered)
    {
        var target = NpgsqlModel(expression);
        var sql    = MigrationHarness.NpgsqlSql(MigrationHarness.NpgsqlDiff(null, target).OfType<CreateIndexOperation>());

        StringAssert.Contains(sql, rendered);
    }

    [TestMethod(DisplayName = "A snapshot holding such an index stays diffable by the core differ on SQL Server, so it can be removed")]
    [DataRow(false, "ix_commits_reviewed")]
    [DataRow(true,  "ix_commits_title")]
    public void Snapshot_with_parts_stays_diffable(bool expression, string name)
    {
        var operations = MigrationHarness.Diff<CustomMigrationsModelDiffer>(
            SqlServer, SqlServerModel(expression), MigrationHarness.SqlServerModel<PlainContext>());

        Assert.Contains(o => o is DropIndexOperation drop && drop.Name == name, operations);
    }
}
