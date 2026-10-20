using EFCore.ComplexIndexes.PostgreSQL;
using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// A complex index is not in EF Core's model, so EF never moves it out of the way of its own
/// operations. On SQL Server, <c>ALTER COLUMN</c> on a column a complex index uses failed at apply
/// time (Msg 5074, the index depends on the column) while EF dropped and re-created its own indexes
/// around the same statement. An unchanged complex index is therefore dropped before such an
/// operation and created again after it, where the provider needs that.
/// </summary>
[TestClass]
public class AlterColumnUnderIndexTests
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

    private abstract class MemberContext(DbContextOptions options) : DbContext(options)
    {
        protected abstract void Vary(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Member> b);

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Member>(b =>
            {
                b.ToTable("members");
                b.Property(x => x.Nickname).HasMaxLength(50);
                b.HasIndex(x => x.Nickname);
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasMaxLength(200));
                b.HasComplexIndex(x => x.Email.Value, isUnique: true, indexName: "ux_members_email");
                Vary(b);
            });
    }

    private class V1Context(DbContextOptions<V1Context> options) : MemberContext(options)
    {
        protected override void Vary(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Member> b)
            => b.Property(x => x.Nickname).IsRequired();
    }

    // Only an unrelated column changes: Nickname becomes optional, and Note goes away.
    private class V2Context(DbContextOptions<V2Context> options) : MemberContext(options)
    {
        protected override void Vary(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Member> b)
            => b.Ignore(x => x.Note);
    }

    // The column under the complex index changes its length.
    private class V3Context(DbContextOptions<V3Context> options) : MemberContext(options)
    {
        protected override void Vary(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Member> b)
        {
            b.Property(x => x.Nickname).IsRequired();
            b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasMaxLength(100));
        }
    }

    [TestMethod(DisplayName = "SQL Server: ALTER COLUMN under a complex index runs with the index out of the way")]
    public void SqlServer_alter_column_under_index()
    {
        var operations = MigrationHarness.SqlServerDiff(
            MigrationHarness.SqlServerModel<V1Context>(), MigrationHarness.SqlServerModel<V3Context>()).ToList();

        var drop   = operations.FindIndex(o => o is DropIndexOperation { Name: "ux_members_email" });
        var alter  = operations.FindIndex(o => o is AlterColumnOperation { Name: "Email_Value" });
        var create = operations.FindIndex(o => o is CreateIndexOperation { Name: "ux_members_email" });

        // Without the drop, SQL Server refuses the ALTER COLUMN: Msg 5074, the index depends on it.
        Assert.IsTrue(drop >= 0 && drop < alter && alter < create, string.Join(", ", operations.Select(o => o.GetType().Name)));
    }

    [TestMethod(DisplayName = "SQL Server: ALTER COLUMN on another column leaves the complex index alone")]
    public void SqlServer_alter_other_column_leaves_index()
    {
        var operations = MigrationHarness.SqlServerDiff(
            MigrationHarness.SqlServerModel<V1Context>(), MigrationHarness.SqlServerModel<V2Context>());

        Assert.IsFalse(operations.Any(o => o is DropIndexOperation { Name: "ux_members_email" }));
    }

    [TestMethod(DisplayName = "PostgreSQL: ALTER COLUMN leaves the complex index alone, PostgreSQL rebuilds it itself")]
    public void Npgsql_alter_column_leaves_index()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<V1Context>(), MigrationHarness.NpgsqlModel<V3Context>());

        Assert.IsTrue(operations.Any(o => o is AlterColumnOperation));
        Assert.IsFalse(operations.Any(o => o is DropIndexOperation or CreateIndexOperation));
    }
}
