using EFCore.ComplexIndexes.PostgreSQL;
using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// PostgreSQL, SQL Server and SQLite keep an index or constraint when a column it reads is renamed,
/// and EF Core emits only the <c>RenameColumnOperation</c> for its own indexes. Until #182 the
/// differs compared the source under the old column name and the target under the new one, so every
/// complex index, exclusion constraint and temporal constraint on the column was dropped and created
/// again: a full rebuild under an exclusive lock, for a rename. The source is now resolved under
/// the names the base operations rename its columns to. Index and constraint names still come from
/// the source's own columns, so a default name that changes with the column is a rename, as EF
/// Core's own default-named indexes are.
/// </summary>
[TestClass]
public class ColumnRenameTests
{
    private class EmailAddress
    {
        public string? Value { get; set; }
    }

    private class Document
    {
        public int Rank { get; set; }
    }

    private class Member
    {
        public int                    Id        { get; set; }
        public string                 Code      { get; set; } = "";
        public EmailAddress           Email     { get; set; } = new();
        public DateTime?              DeletedAt { get; set; }
        public NpgsqlRange<DateTime>  Period    { get; set; }
        public Document               Doc       { get; set; } = new();
    }

    private sealed record Columns(string Table, string Code, string Email, string DeletedAt, string Period, string Doc)
    {
        public static readonly Columns Before = new("members", "code", "email", "deleted_at", "period", "doc");
        public static readonly Columns After  = new("members", "member_code", "email_address", "deleted_on", "valid_during", "document");
    }

    // ── PostgreSQL ────────────────────────────────────────────────────────────────────────────────

    private abstract class NpgsqlMemberContext(DbContextOptions options, Columns columns) : DbContext(options)
    {
        protected virtual void Vary(EntityTypeBuilder<Member> b) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Member>(b =>
            {
                b.ToTable(columns.Table);
                b.Property(x => x.Code).HasColumnName(columns.Code);
                b.Property(x => x.DeletedAt).HasColumnName(columns.DeletedAt);
                b.Property(x => x.Period).HasColumnName(columns.Period);
                b.ComplexProperty(x => x.Doc, c => c.ToJson(columns.Doc));

                // Property-level, default-named: the name follows the column.
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasColumnName(columns.Email).HasComplexIndex(isUnique: true));

                b.HasComplexCompositeIndex(x => new { x.Code, x.Email.Value }, filter: "{DeletedAt} IS NULL", indexName: "ix_members_code_email");
                b.HasComplexIndex(x => x.Code, ix => NpgsqlComplexIndexBuilderExtensions.IncludeProperties(ix.HasName("ix_members_code_incl"), "Email.Value"));
                b.HasExpressionIndex(x => x.Code.ToLower(), indexName: "ix_members_code_lower");
                b.HasComplexIndex(x => x.Doc.Rank, indexName: "ix_members_doc_rank");

                b.HasExclusionConstraint(x => x.Code, x => x.Period, filter: "{DeletedAt} IS NULL", name: "ex_members_code_period");
                b.HasExclusionConstraint(ex => ex.WithEquality(x => x.Email.Value).WithOverlaps(x => x.Period));
                b.HasTemporalConstraint(x => x.Code, x => x.Period, name: "tc_members_code");

                Vary(b);
            });
    }

    private class NpgsqlBefore(DbContextOptions<NpgsqlBefore> options) : NpgsqlMemberContext(options, Columns.Before);

    private class NpgsqlAfter(DbContextOptions<NpgsqlAfter> options) : NpgsqlMemberContext(options, Columns.After);

    private class NpgsqlTableAndColumnsRenamed(DbContextOptions<NpgsqlTableAndColumnsRenamed> options)
        : NpgsqlMemberContext(options, Columns.After with { Table = "people" });

    // The composite index also becomes unique: a real change, beside the rename.
    private class NpgsqlAfterWithChange(DbContextOptions<NpgsqlAfterWithChange> options) : NpgsqlMemberContext(options, Columns.After)
    {
        protected override void Vary(EntityTypeBuilder<Member> b)
            => b.HasComplexCompositeIndex(x => new { x.Code, x.Email.Value }, isUnique: true, filter: "{DeletedAt} IS NULL", indexName: "ix_members_code_email");
    }

    [TestMethod]
    public void PostgreSQL_renames_the_columns_and_nothing_else()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<NpgsqlBefore>(), MigrationHarness.NpgsqlModel<NpgsqlAfter>());

        CollectionAssert.AreEquivalent(
            new[] { "code → member_code", "deleted_at → deleted_on", "doc → document", "email → email_address", "period → valid_during" },
            operations.OfType<RenameColumnOperation>().Select(o => $"{o.Name} → {o.NewName}").ToArray());

        var sql = MigrationHarness.NpgsqlSql(operations);
        Assert.IsFalse(sql.Contains("DROP INDEX", StringComparison.Ordinal), sql);
        Assert.IsFalse(sql.Contains("CREATE", StringComparison.Ordinal), sql);
        Assert.IsFalse(sql.Contains("DROP CONSTRAINT", StringComparison.Ordinal), sql);
        Assert.IsFalse(sql.Contains("ADD CONSTRAINT", StringComparison.Ordinal), sql);
    }

    [TestMethod]
    public void PostgreSQL_renames_a_default_name_that_follows_the_column()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<NpgsqlBefore>(), MigrationHarness.NpgsqlModel<NpgsqlAfter>());

        var rename = operations.OfType<RenameIndexOperation>().Single();
        Assert.AreEqual("IX_members_email", rename.Name);
        Assert.AreEqual("IX_members_email_address", rename.NewName);

        var sql = MigrationHarness.NpgsqlSql(operations);
        StringAssert.Contains(sql, "RENAME CONSTRAINT \"EX_members_email_period\" TO \"EX_members_email_address_valid_during\"");
    }

    [TestMethod]
    public void PostgreSQL_renames_the_columns_of_a_renamed_table_and_nothing_else()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<NpgsqlBefore>(), MigrationHarness.NpgsqlModel<NpgsqlTableAndColumnsRenamed>());

        Assert.AreEqual(5, operations.OfType<RenameColumnOperation>().Count());
        Assert.IsFalse(operations.OfType<DropIndexOperation>().Any());
        Assert.IsFalse(operations.OfType<CreateIndexOperation>().Any());

        // EF Core rebuilds its own primary key around a table rename; nothing of this package's is.
        var sql = MigrationHarness.NpgsqlSql(operations);
        Assert.IsFalse(sql.Contains("EXCLUDE", StringComparison.Ordinal), sql);
        Assert.IsFalse(sql.Contains("WITHOUT OVERLAPS", StringComparison.Ordinal), sql);
        StringAssert.Contains(sql, "RENAME CONSTRAINT \"EX_members_email_period\" TO \"EX_people_email_address_valid_during\"");
    }

    [TestMethod]
    public void PostgreSQL_still_rebuilds_an_index_that_changed_beside_the_rename()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<NpgsqlBefore>(), MigrationHarness.NpgsqlModel<NpgsqlAfterWithChange>()).ToList();

        var drop   = operations.OfType<DropIndexOperation>().Single();
        var create = operations.OfType<CreateIndexOperation>().Single();
        Assert.AreEqual("ix_members_code_email", drop.Name);
        Assert.IsTrue(create.IsUnique);
        CollectionAssert.AreEqual(new[] { "member_code", "email_address" }, create.Columns);
        Assert.AreEqual("\"deleted_on\" IS NULL", create.Filter);

        var firstRename = operations.FindIndex(o => o is RenameColumnOperation);
        Assert.IsTrue(operations.IndexOf(drop) < firstRename, "The drop runs before the columns are renamed.");
        Assert.IsTrue(operations.IndexOf(create) > operations.FindLastIndex(o => o is RenameColumnOperation), "The create runs after.");
    }

    // A verbatim expression is compared as written: the differ cannot read SQL, and PostgreSQL
    // rewrites the stored expression on a rename, so a verbatim part left unchanged is no change.
    private abstract class VerbatimContext(DbContextOptions options, string codeColumn) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Member>(b =>
            {
                b.ToTable("members");
                b.Ignore(x => x.Period);
                b.Ignore(x => x.Doc);
                b.ComplexProperty(x => x.Email);
                b.Property(x => x.Code).HasColumnName(codeColumn);
                b.HasExpressionIndex("lower(code)", indexName: "ix_members_code_verbatim");
            });
    }

    private class VerbatimBefore(DbContextOptions<VerbatimBefore> options) : VerbatimContext(options, "code");

    private class VerbatimAfter(DbContextOptions<VerbatimAfter> options) : VerbatimContext(options, "member_code");

    [TestMethod]
    public void PostgreSQL_compares_a_verbatim_expression_as_written()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<VerbatimBefore>(), MigrationHarness.NpgsqlModel<VerbatimAfter>());

        Assert.AreEqual(1, operations.Count, string.Join(", ", operations.Select(o => o.GetType().Name)));
        Assert.IsInstanceOfType<RenameColumnOperation>(operations[0]);
    }

    // ── SQL Server ────────────────────────────────────────────────────────────────────────────────

    private abstract class SqlServerMemberContext(DbContextOptions options, Columns columns) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Member>(b =>
            {
                b.ToTable(columns.Table);
                b.Ignore(x => x.Period);
                b.Ignore(x => x.Doc);
                b.Property(x => x.Code).HasColumnName(columns.Code).HasMaxLength(50);
                b.Property(x => x.DeletedAt).HasColumnName(columns.DeletedAt);

                // Unique over a nullable column: the implicit IS NOT NULL filter names it.
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasColumnName(columns.Email).HasMaxLength(200).HasComplexIndex(isUnique: true));

                b.HasComplexCompositeIndex(x => new { x.Code, x.Email.Value }, filter: "{DeletedAt} IS NULL", indexName: "ix_members_code_email");
                b.HasComplexIndex(x => x.Code, ix => SqlServerComplexIndexBuilderExtensions.IncludeProperties(ix.HasName("ix_members_code_incl"), "Email.Value"));
            });
    }

    private class SqlServerBefore(DbContextOptions<SqlServerBefore> options) : SqlServerMemberContext(options, Columns.Before);

    private class SqlServerAfter(DbContextOptions<SqlServerAfter> options) : SqlServerMemberContext(options, Columns.After);

    [TestMethod]
    public void SqlServer_renames_the_columns_and_the_default_name_only()
    {
        var operations = MigrationHarness.SqlServerDiff(
            MigrationHarness.SqlServerModel<SqlServerBefore>(), MigrationHarness.SqlServerModel<SqlServerAfter>());

        Assert.AreEqual(3, operations.OfType<RenameColumnOperation>().Count());
        Assert.IsFalse(operations.OfType<DropIndexOperation>().Any(), string.Join(", ", operations.OfType<DropIndexOperation>().Select(o => o.Name)));
        Assert.IsFalse(operations.OfType<CreateIndexOperation>().Any());

        var rename = operations.OfType<RenameIndexOperation>().Single();
        Assert.AreEqual("IX_members_email", rename.Name);
        Assert.AreEqual("IX_members_email_address", rename.NewName);
    }

    // ── SQLite (the core differ) ──────────────────────────────────────────────────────────────────

    private abstract class SqliteMemberContext(DbContextOptions options, Columns columns) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Member>(b =>
            {
                b.ToTable(columns.Table);
                b.Ignore(x => x.Period);
                b.Ignore(x => x.Doc);
                b.Property(x => x.Code).HasColumnName(columns.Code);
                b.Property(x => x.DeletedAt).HasColumnName(columns.DeletedAt);
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasColumnName(columns.Email));

                b.HasComplexCompositeIndex(x => new { x.Code, x.Email.Value }, filter: "{DeletedAt} IS NULL", indexName: "ix_members_code_email");
                b.HasComplexIndex(x => x.Email.Value, isUnique: true, indexName: "ux_members_email");
            });
    }

    private class SqliteBefore(DbContextOptions<SqliteBefore> options) : SqliteMemberContext(options, Columns.Before);

    private class SqliteAfter(DbContextOptions<SqliteAfter> options) : SqliteMemberContext(options, Columns.After);

    [TestMethod]
    public void SQLite_renames_the_columns_and_nothing_else()
    {
        var operations = MigrationHarness.CoreDiff(
            MigrationHarness.SqliteModel<SqliteBefore>(), MigrationHarness.SqliteModel<SqliteAfter>());

        Assert.AreEqual(3, operations.OfType<RenameColumnOperation>().Count());
        Assert.AreEqual(3, operations.Count, string.Join(", ", operations.Select(o => o.GetType().Name)));
    }
}
