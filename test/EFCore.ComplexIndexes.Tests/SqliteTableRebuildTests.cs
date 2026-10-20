using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// SQLite runs an <c>ALTER COLUMN</c>, a <c>DROP COLUMN</c> and every key or constraint change by
/// rebuilding the table, and EF Core re-creates only the indexes in its own model. Every complex
/// index on the table vanished, unique ones included, and the migration applied cleanly. EF's SQLite
/// generator emits rebuilds after every other operation, so no operation the differ adds can re-create
/// the index afterwards; <c>migrations add</c> fails instead, naming the indexes and the way out.
/// </summary>
[TestClass]
public class SqliteTableRebuildTests
{
    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class Member
    {
        public int          Id       { get; set; }
        public string?      Nickname { get; set; }
        public string?      Note     { get; set; }
        public EmailAddress Email    { get; set; } = new();
    }

    private class Visit
    {
        public int     Id    { get; set; }
        public string? Notes { get; set; }
    }

    private abstract class MemberContext(DbContextOptions options) : DbContext(options)
    {
        protected virtual bool WithComplexIndex => true;

        protected abstract void Vary(EntityTypeBuilder<Member> b);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Member>(b =>
            {
                b.ToTable("members");
                b.HasIndex(x => x.Nickname);
                b.ComplexProperty(x => x.Email);
                if (WithComplexIndex)
                    b.HasComplexIndex(x => x.Email.Value, isUnique: true, indexName: "ux_members_email");
                Vary(b);
            });

            // A second table with no complex index, whose rebuild this package has nothing to say about.
            modelBuilder.Entity<Visit>().ToTable("visits");
        }
    }

    private class V1Context(DbContextOptions<V1Context> options) : MemberContext(options)
    {
        protected override void Vary(EntityTypeBuilder<Member> b) => b.Property(x => x.Nickname).IsRequired();
    }

    // Only unrelated columns change: Nickname becomes optional, Note goes away.
    private class V2Context(DbContextOptions<V2Context> options) : MemberContext(options)
    {
        protected override void Vary(EntityTypeBuilder<Member> b) => b.Ignore(x => x.Note);
    }

    // The three-step way out: the same change, with the complex index declared nowhere on the table.
    private class V1WithoutIndexContext(DbContextOptions<V1WithoutIndexContext> options) : MemberContext(options)
    {
        protected override bool WithComplexIndex => false;
        protected override void Vary(EntityTypeBuilder<Member> b) => b.Property(x => x.Nickname).IsRequired();
    }

    private class V2WithoutIndexContext(DbContextOptions<V2WithoutIndexContext> options) : MemberContext(options)
    {
        protected override bool WithComplexIndex => false;
        protected override void Vary(EntityTypeBuilder<Member> b) => b.Ignore(x => x.Note);
    }

    // A change on the other table only.
    private class VisitChangedContext(DbContextOptions<VisitChangedContext> options) : MemberContext(options)
    {
        protected override void Vary(EntityTypeBuilder<Member> b) => b.Property(x => x.Nickname).IsRequired();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Visit>().Property(x => x.Notes).IsRequired();
        }
    }

    private static IRelationalModel Model<TContext>(SqliteConnection connection) where TContext : DbContext
        => MigrationHarness.SqliteModel<TContext>(connection);

    private static void Apply(SqliteConnection connection, IRelationalModel? source, IRelationalModel target)
    {
        using var context = new MigrationHarness.EmptyContext(MigrationHarness.SqliteOptions(connection));
        var commands = context.GetService<IMigrationsSqlGenerator>()
                              .Generate([.. MigrationHarness.CoreDiff(connection, source, target)], target.Model);

        foreach (var command in commands)
        {
            using var sql = connection.CreateCommand();
            sql.CommandText = command.CommandText;
            sql.ExecuteNonQuery();
        }
    }

    private static List<string> Indexes(SqliteConnection connection)
    {
        using var sql = connection.CreateCommand();
        sql.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'members' AND sql IS NOT NULL";
        using var reader = sql.ExecuteReader();

        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    [TestMethod(DisplayName = "A change that rebuilds a table with a complex index fails at migrations add")]
    public void Rebuild_of_a_table_with_a_complex_index_is_refused()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => MigrationHarness.CoreDiff(connection, Model<V1Context>(connection), Model<V2Context>(connection)));

        StringAssert.Contains(exception.Message, "SQLite rebuilds table 'members'");
        StringAssert.Contains(exception.Message, "'ux_members_email'");
        StringAssert.Contains(exception.Message, "three migrations");
    }

    [TestMethod(DisplayName = "The three migrations the message proposes apply and keep the index")]
    public void The_way_out_applies_and_keeps_the_index()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        var v1 = Model<V1Context>(connection);
        Apply(connection, null, v1);
        CollectionAssert.Contains(Indexes(connection), "ux_members_email");

        var withoutIndex = Model<V1WithoutIndexContext>(connection);
        var changed      = Model<V2WithoutIndexContext>(connection);
        var v2           = Model<V2Context>(connection);
        Apply(connection, v1, withoutIndex);
        Apply(connection, withoutIndex, changed);
        Apply(connection, changed, v2);

        CollectionAssert.Contains(Indexes(connection), "ux_members_email");
        CollectionAssert.Contains(Indexes(connection), "IX_members_Nickname");
    }

    [TestMethod(DisplayName = "A rebuild of a table without a complex index is left alone")]
    public void Rebuild_of_another_table_is_allowed()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        var operations = MigrationHarness.CoreDiff(connection, Model<V1Context>(connection), Model<VisitChangedContext>(connection));

        Assert.IsNotEmpty(operations);
    }

    [TestMethod(DisplayName = "An unchanged model diffs to nothing")]
    public void Unchanged_model_is_empty()
    {
        using var connection = new SqliteConnection(MigrationHarness.SqliteConnection);
        connection.Open();

        Assert.IsEmpty(MigrationHarness.CoreDiff(connection, Model<V1Context>(connection), Model<V1Context>(connection)));
    }
}
