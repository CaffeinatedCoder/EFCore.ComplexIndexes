using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Renames run after the base operations, so a rename whose old name the target model gives to
/// something else ran after that something had been created under the name: EF's
/// <c>CREATE INDEX ix_a</c> before <c>ALTER INDEX ix_a RENAME TO ix_b</c>, 42P07 at apply (#143).
/// Such a rename becomes a drop under the old name before the base operations and a create under
/// the new one after them.
/// </summary>
[TestClass]
public class RenameOldNameTakenTests
{
    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class Member
    {
        public int                   Id     { get; set; }
        public string                Code   { get; set; } = "";
        public int                   RoomId { get; set; }
        public NpgsqlRange<DateOnly> Period { get; set; }
        public EmailAddress          Email  { get; set; } = new();
    }

    private static void Map(EntityTypeBuilder<Member> b)
    {
        b.ToTable("members");
        b.Property(x => x.Code).HasMaxLength(20);
        b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasMaxLength(200));
    }

    // ── Indexes ──

    private abstract class IndexContext(DbContextOptions options, string complexName, string? nativeName) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Member>(b =>
            {
                Map(b);
                b.Ignore(x => x.Period);
                b.HasComplexIndex(x => x.Email.Value, indexName: complexName);
                if (nativeName is not null)
                    b.HasIndex(x => x.Code).HasDatabaseName(nativeName);
            });
    }

    private class IndexV1Context(DbContextOptions<IndexV1Context> options) : IndexContext(options, "ix_a", null);

    // The complex index moves to ix_b, and a native index takes ix_a.
    private class IndexV2Context(DbContextOptions<IndexV2Context> options) : IndexContext(options, "ix_b", "ix_a");

    [TestMethod(DisplayName = "PostgreSQL: a native index taking a renamed complex index's old name is created after the old one is gone (#143)")]
    public void Npgsql_native_index_takes_old_name()
        => AssertIndexNamesApply(
            MigrationHarness.NpgsqlDiff(MigrationHarness.NpgsqlModel<IndexV1Context>(), MigrationHarness.NpgsqlModel<IndexV2Context>()),
            existing: ["ix_a"]);

    [TestMethod(DisplayName = "SQL Server: a native index taking a renamed complex index's old name is created after the old one is gone (#143)")]
    public void SqlServer_native_index_takes_old_name()
        => AssertIndexNamesApply(
            MigrationHarness.SqlServerDiff(MigrationHarness.SqlServerModel<IndexV1Context>(), MigrationHarness.SqlServerModel<IndexV2Context>()),
            existing: ["ix_a"]);

    [TestMethod(DisplayName = "PostgreSQL: a rename whose old name nothing takes stays a rename")]
    public void Npgsql_plain_rename_stays_rename()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<IndexV1Context>(), MigrationHarness.NpgsqlModel<SwapV1Context>());

        Assert.IsTrue(operations.Any(o => o is RenameIndexOperation { Name: "ix_a", NewName: "ix_b" }));
        Assert.IsFalse(operations.Any(o => o is DropIndexOperation or CreateIndexOperation { Name: "ix_b" }));
    }

    private class IndexCaseV2Context(DbContextOptions<IndexCaseV2Context> options) : IndexContext(options, "IX_A", null);

    [TestMethod(DisplayName = "SQL Server: a rename that changes only the case stays a rename")]
    public void SqlServer_case_only_rename_stays_rename()
    {
        var operations = MigrationHarness.SqlServerDiff(
            MigrationHarness.SqlServerModel<IndexV1Context>(), MigrationHarness.SqlServerModel<IndexCaseV2Context>());

        Assert.IsTrue(operations.Any(o => o is RenameIndexOperation { Name: "ix_a", NewName: "IX_A" }));
        Assert.IsFalse(operations.Any(o => o is DropIndexOperation or CreateIndexOperation));
    }

    private abstract class SwapContext(DbContextOptions options, string emailName, string codeName) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Member>(b =>
            {
                Map(b);
                b.Ignore(x => x.Period);
                b.HasComplexIndex(x => x.Email.Value, indexName: emailName);
                b.HasComplexIndex(x => x.Code, indexName: codeName);
            });
    }

    private class SwapV1Context(DbContextOptions<SwapV1Context> options) : SwapContext(options, "ix_b", "ix_c");

    // Two complex indexes swap names: each rename's new name is the other's old one.
    private class SwapV2Context(DbContextOptions<SwapV2Context> options) : SwapContext(options, "ix_c", "ix_b");

    [TestMethod(DisplayName = "PostgreSQL: two complex indexes swapping names apply")]
    public void Npgsql_swapped_names_apply()
        => AssertIndexNamesApply(
            MigrationHarness.NpgsqlDiff(MigrationHarness.NpgsqlModel<SwapV1Context>(), MigrationHarness.NpgsqlModel<SwapV2Context>()),
            existing: ["ix_b", "ix_c"]);

    // Walks the operations as the database would, by index name, and fails where one would be
    // created or renamed into a name still in use, or renamed from one that no longer exists.
    private static void AssertIndexNamesApply(IReadOnlyList<MigrationOperation> operations, string[] existing)
    {
        var names   = new HashSet<string>(existing, StringComparer.Ordinal);
        var summary = string.Join(", ", operations.Select(Describe));

        foreach (var operation in operations)
        {
            switch (operation)
            {
                case DropIndexOperation drop:
                    Assert.IsTrue(names.Remove(drop.Name), $"DROP INDEX {drop.Name}: no such index. {summary}");
                    break;
                case CreateIndexOperation create:
                    Assert.IsTrue(names.Add(create.Name), $"CREATE INDEX {create.Name}: the name is in use. {summary}");
                    break;
                case RenameIndexOperation rename:
                    Assert.IsTrue(names.Remove(rename.Name), $"RENAME {rename.Name}: no such index. {summary}");
                    Assert.IsTrue(names.Add(rename.NewName), $"RENAME {rename.Name} TO {rename.NewName}: the name is in use. {summary}");
                    break;
            }
        }

        static string Describe(MigrationOperation o) => o switch
        {
            DropIndexOperation drop     => $"Drop {drop.Name}",
            CreateIndexOperation create => $"Create {create.Name}",
            RenameIndexOperation rename => $"Rename {rename.Name}→{rename.NewName}",
            _                           => o.GetType().Name.Replace("Operation", "")
        };
    }

    // ── Constraints ──

    private abstract class ExclusionContext(DbContextOptions options, string exclusionName, string? keyName, string? complexIndexName)
        : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Member>(b =>
            {
                Map(b);
                b.HasExclusionConstraint(x => x.RoomId, x => x.Period, name: exclusionName);
                if (keyName is not null)
                    b.HasAlternateKey(x => x.Code).HasName(keyName);
                if (complexIndexName is not null)
                    b.HasComplexIndex(x => x.Email.Value, indexName: complexIndexName);
            });
    }

    private class ExclusionV1Context(DbContextOptions<ExclusionV1Context> options) : ExclusionContext(options, "ex_a", null, null);

    // The exclusion constraint moves to ex_b, and a native unique constraint takes ex_a.
    private class ExclusionKeyV2Context(DbContextOptions<ExclusionKeyV2Context> options) : ExclusionContext(options, "ex_b", "ex_a", null);

    // The exclusion constraint moves to ex_b, and a complex index takes ex_a: both own a relation
    // in the schema, and the index is created with the base operations of the constraint stage.
    private class ExclusionIndexV2Context(DbContextOptions<ExclusionIndexV2Context> options) : ExclusionContext(options, "ex_b", null, "ex_a");

    [TestMethod(DisplayName = "PostgreSQL: a native unique constraint taking a renamed exclusion constraint's old name applies (#143)")]
    public void Npgsql_unique_constraint_takes_exclusion_old_name()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<ExclusionV1Context>(), MigrationHarness.NpgsqlModel<ExclusionKeyV2Context>()).ToList();

        var add = operations.FindIndex(o => o is AddUniqueConstraintOperation { Name: "ex_a" });
        AssertConstraintMovedOutOfTheWay(operations, "ex_a", "ex_b", add, "EXCLUDE");
    }

    [TestMethod(DisplayName = "PostgreSQL: a complex index taking a renamed exclusion constraint's old name applies")]
    public void Npgsql_complex_index_takes_exclusion_old_name()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<ExclusionV1Context>(), MigrationHarness.NpgsqlModel<ExclusionIndexV2Context>()).ToList();

        var add = operations.FindIndex(o => o is CreateIndexOperation { Name: "ex_a" });
        AssertConstraintMovedOutOfTheWay(operations, "ex_a", "ex_b", add, "EXCLUDE");
    }

    private class TemporalForeignKeyTarget
    {
        public int                   Id     { get; set; }
        public int                   RoomId { get; set; }
        public NpgsqlRange<DateOnly> During { get; set; }
    }

    private abstract class TemporalContext(DbContextOptions options, string temporalName, string? keyName) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Member>(b =>
            {
                Map(b);
                b.HasTemporalConstraint(x => x.RoomId, x => x.Period, name: temporalName);
                if (keyName is not null)
                    b.HasAlternateKey(x => x.Code).HasName(keyName);
            });

            modelBuilder.Entity<TemporalForeignKeyTarget>(b =>
            {
                b.ToTable("stays");
                b.HasTemporalForeignKey<TemporalForeignKeyTarget, Member>(
                    x => x.RoomId, x => x.During, x => x.RoomId, x => x.Period, name: "fk_stays_members");
            });
        }
    }

    private class TemporalV1Context(DbContextOptions<TemporalV1Context> options) : TemporalContext(options, "uq_a", null);

    // The temporal constraint moves to uq_b, and a native unique constraint takes uq_a.
    private class TemporalV2Context(DbContextOptions<TemporalV2Context> options) : TemporalContext(options, "uq_b", "uq_a");

    [TestMethod(DisplayName = "PostgreSQL: a native unique constraint taking a renamed temporal constraint's old name applies, its foreign key rebuilt around it")]
    public void Npgsql_unique_constraint_takes_temporal_old_name()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<TemporalV1Context>(), MigrationHarness.NpgsqlModel<TemporalV2Context>()).ToList();

        var add = operations.FindIndex(o => o is AddUniqueConstraintOperation { Name: "uq_a" });
        AssertConstraintMovedOutOfTheWay(operations, "uq_a", "uq_b", add, "WITHOUT OVERLAPS");

        // The foreign key depends on the constraint's index: dropping the constraint under it
        // fails with 2BP01, so the key goes first and comes back last.
        var summary        = Summary(operations);
        var dropConstraint = operations.FindIndex(o => o is DropUniqueConstraintOperation { Name: "uq_a" });
        var dropKey        = operations.FindIndex(o => o is DropForeignKeyOperation { Name: "fk_stays_members" });
        var addConstraint  = operations.FindIndex(o => o is SqlOperation sql && sql.Sql.Contains("ADD CONSTRAINT \"uq_b\""));
        var addKey         = operations.FindIndex(o => o is SqlOperation sql && sql.Sql.Contains("ADD CONSTRAINT \"fk_stays_members\""));

        Assert.IsTrue(dropKey >= 0 && dropKey < dropConstraint, summary);
        Assert.IsTrue(addKey > addConstraint, summary);
    }

    [TestMethod(DisplayName = "PostgreSQL: a renamed temporal constraint whose old name nothing takes keeps its foreign key")]
    public void Npgsql_plain_temporal_rename_keeps_foreign_key()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<TemporalV1Context>(), MigrationHarness.NpgsqlModel<TemporalRenameOnlyContext>());

        Assert.IsTrue(operations.Any(o => o is SqlOperation sql && sql.Sql.Contains("RENAME CONSTRAINT \"uq_a\" TO \"uq_b\"")), Summary(operations));
        Assert.IsFalse(operations.Any(o => o is DropForeignKeyOperation or DropUniqueConstraintOperation), Summary(operations));
    }

    private class TemporalRenameOnlyContext(DbContextOptions<TemporalRenameOnlyContext> options) : TemporalContext(options, "uq_b", null);

    // No RENAME CONSTRAINT under the old name, a drop under it before the native add, and the add
    // under the new name after it.
    private static void AssertConstraintMovedOutOfTheWay(
        List<MigrationOperation> operations, string oldName, string newName, int nativeAdd, string kind)
    {
        var summary = Summary(operations);

        var drop = operations.FindIndex(o => o is SqlOperation sql && sql.Sql.Contains($"DROP CONSTRAINT IF EXISTS \"{oldName}\"")
                                          || o is DropUniqueConstraintOperation { Name: var name } && name == oldName);
        var add  = operations.FindIndex(o => o is SqlOperation sql && sql.Sql.Contains($"ADD CONSTRAINT \"{newName}\"") && sql.Sql.Contains(kind));

        Assert.IsFalse(operations.Any(o => o is SqlOperation sql && sql.Sql.Contains($"RENAME CONSTRAINT \"{oldName}\"")), summary);
        Assert.IsTrue(nativeAdd >= 0, summary);
        Assert.IsTrue(drop >= 0 && drop < nativeAdd && nativeAdd < add, summary);
    }

    private static string Summary(IEnumerable<MigrationOperation> operations)
        => string.Join(" | ", operations.Select(o => o is SqlOperation sql ? sql.Sql : o.GetType().Name.Replace("Operation", "")));
}
