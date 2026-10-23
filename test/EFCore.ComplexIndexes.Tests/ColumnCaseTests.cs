using EFCore.ComplexIndexes.PostgreSQL;
using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// SQL Server reads identifiers without regard to case under its default collation, so a filter
/// <c>[note] IS NULL</c> or an INCLUDE entry <c>note</c> reads the column <c>Note</c>, and the
/// package's own validation accepts that spelling. The check that moves an unchanged index out of
/// the way of <c>ALTER COLUMN</c> matched names ordinally, missed the dependency, and the migration
/// failed at apply (Msg 5074, #189). Names now compare as the provider compares identifiers; on
/// PostgreSQL, where a quoted <c>"note"</c> is another column than <c>"Note"</c>, they still do not.
/// </summary>
[TestClass]
public class ColumnCaseTests
{
    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class Member
    {
        public int          Id    { get; set; }
        public string?      Note  { get; set; }
        public string?      Other { get; set; }
        public EmailAddress Email { get; set; } = new();
    }

    // ── SQL Server ────────────────────────────────────────────────────────────────────────────────

    private abstract class SqlServerContext(DbContextOptions options) : DbContext(options)
    {
        protected virtual int    NoteLength => 200;
        protected virtual string NoteColumn => "Note";
        protected abstract void Declare(EntityTypeBuilder<Member> b);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Member>(b =>
            {
                b.ToTable("members");
                b.Ignore(x => x.Other);
                b.Property(x => x.Note).HasColumnName(NoteColumn).HasMaxLength(NoteLength);
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasMaxLength(200));
                Declare(b);
            });
    }

    private abstract class FilterContext(DbContextOptions options) : SqlServerContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Member> b)
            => b.HasComplexIndex(x => x.Email.Value, isUnique: true, filter: "[note] IS NULL", indexName: "ux_members_email_without_note");
    }

    private class FilterBefore(DbContextOptions<FilterBefore> options) : FilterContext(options);

    private class FilterNarrowed(DbContextOptions<FilterNarrowed> options) : FilterContext(options)
    {
        protected override int NoteLength => 100;
    }

    private class FilterRenamedInCase(DbContextOptions<FilterRenamedInCase> options) : FilterContext(options)
    {
        protected override string NoteColumn => "NOTE";
    }

    private abstract class IncludeContext(DbContextOptions options) : SqlServerContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Member> b)
            => b.HasComplexIndex(x => x.Email.Value, ix => SqlServerComplexIndexBuilderExtensions.IncludeProperties(ix.HasName("ix_members_email"), "note"));
    }

    private class IncludeBefore(DbContextOptions<IncludeBefore> options) : IncludeContext(options);

    private class IncludeNarrowed(DbContextOptions<IncludeNarrowed> options) : IncludeContext(options)
    {
        protected override int NoteLength => 100;
    }

    private static void AssertRebuiltAround<TOperation>(IReadOnlyList<MigrationOperation> list, string index)
        where TOperation : MigrationOperation
    {
        var operations = list.ToList();
        var drop       = operations.FindIndex(o => o is DropIndexOperation drop && drop.Name == index);
        var create     = operations.FindIndex(o => o is CreateIndexOperation create && create.Name == index);
        var base_      = operations.FindIndex(o => o is TOperation);

        Assert.IsTrue(base_ >= 0, typeof(TOperation).Name);
        Assert.IsTrue(drop >= 0 && drop < base_, string.Join(", ", operations.Select(o => o.GetType().Name)));
        Assert.IsTrue(create > base_, string.Join(", ", operations.Select(o => o.GetType().Name)));
    }

    [TestMethod]
    public void SqlServer_a_filter_naming_the_column_in_another_case_moves_the_index_out_of_the_way()
        => AssertRebuiltAround<AlterColumnOperation>(
            MigrationHarness.SqlServerDiff(MigrationHarness.SqlServerModel<FilterBefore>(), MigrationHarness.SqlServerModel<FilterNarrowed>()),
            "ux_members_email_without_note");

    [TestMethod]
    public void SqlServer_an_include_entry_naming_the_column_in_another_case_moves_the_index_out_of_the_way()
        => AssertRebuiltAround<AlterColumnOperation>(
            MigrationHarness.SqlServerDiff(MigrationHarness.SqlServerModel<IncludeBefore>(), MigrationHarness.SqlServerModel<IncludeNarrowed>()),
            "ix_members_email");

    [TestMethod]
    public void SqlServer_a_case_only_rename_under_the_filter_rebuilds_the_index_around_it()
        => AssertRebuiltAround<RenameColumnOperation>(
            MigrationHarness.SqlServerDiff(MigrationHarness.SqlServerModel<FilterBefore>(), MigrationHarness.SqlServerModel<FilterRenamedInCase>()),
            "ux_members_email_without_note");

    // ── PostgreSQL: case matters ──────────────────────────────────────────────────────────────────

    private abstract class NpgsqlContext(DbContextOptions options, string noteSql) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Member>(b =>
            {
                b.ToTable("members");
                b.Property(x => x.Note).HasColumnName("Note").HasComputedColumnSql(noteSql, stored: true);
                b.Property(x => x.Other).HasColumnName("note");
                b.ComplexProperty(x => x.Email);
                b.HasComplexIndex(x => x.Email.Value, filter: "\"note\" IS NULL", indexName: "ix_members_email_without_note");
            });
    }

    private class NpgsqlBefore(DbContextOptions<NpgsqlBefore> options) : NpgsqlContext(options, "lower(\"note\")");

    private class NpgsqlNoteChanged(DbContextOptions<NpgsqlNoteChanged> options) : NpgsqlContext(options, "upper(\"note\")");

    // "Note" is dropped and added again (#184); the index reads "note", another column.
    [TestMethod]
    public void PostgreSQL_a_filter_naming_another_column_in_another_case_leaves_the_index_alone()
    {
        var operations = MigrationHarness.NpgsqlDiff(MigrationHarness.NpgsqlModel<NpgsqlBefore>(), MigrationHarness.NpgsqlModel<NpgsqlNoteChanged>());

        Assert.IsTrue(operations.OfType<AlterColumnOperation>().Any(o => o.Name == "Note"));
        Assert.IsFalse(operations.Any(o => o is DropIndexOperation or CreateIndexOperation));
    }
}
