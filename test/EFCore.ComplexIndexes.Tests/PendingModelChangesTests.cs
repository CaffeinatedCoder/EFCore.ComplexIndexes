using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

#pragma warning disable EF1001

/// <summary>
/// EF Core's <c>MigrationsModelDiffer.HasDifferences</c> runs the protected <c>Diff</c>, not the
/// public <c>GetDifferences</c> this package overrides. Without an override of its own, every check
/// built on it reported "no changes" when only a declaration from this package had changed:
/// <c>dotnet ef migrations has-pending-model-changes</c>, the pending-model-changes warning
/// <c>Migrate()</c> raises, and the snapshot check in <c>migrations remove</c>. A CI gate built on the
/// first of those passed while a complex index was missing from the migrations.
/// </summary>
[TestClass]
public class PendingModelChangesTests
{
    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class Person
    {
        public Guid         Id    { get; set; }
        public EmailAddress Email { get; set; } = new();
    }

    private class WithoutIndexContext(DbContextOptions<WithoutIndexContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email);
            });
    }

    private class WithIndexContext(DbContextOptions<WithIndexContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email);
                b.HasComplexIndex(x => x.Email.Value, isUnique: true);
            });
    }

    private class Grant
    {
        public int                  Id        { get; set; }
        public int                  GranteeId { get; set; }
        public NpgsqlRange<DateOnly> Period   { get; set; }
    }

    private class WithoutConstraintContext(DbContextOptions<WithoutConstraintContext> options) : DbContext(options)
    {
        public DbSet<Grant> Grants => Set<Grant>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Grant>(b =>
            {
                b.ToTable("grants");
                b.HasKey(x => x.Id);
            });
    }

    private class WithConstraintContext(DbContextOptions<WithConstraintContext> options) : DbContext(options)
    {
        public DbSet<Grant> Grants => Set<Grant>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Grant>(b =>
            {
                b.ToTable("grants");
                b.HasKey(x => x.Id);
                b.HasExclusionConstraint(x => x.GranteeId, x => x.Period);
            });
    }

    private static CustomMigrationsModelDiffer CoreDiffer()
    {
        using var context = new MigrationHarness.EmptyContext(
            new DbContextOptionsBuilder().UseSqlite(MigrationHarness.SqliteConnection).Options);
        return MigrationHarness.CreateDiffer<CustomMigrationsModelDiffer>(context);
    }

    private static NpgsqlComplexIndexMigrationsModelDiffer NpgsqlDiffer()
    {
        using var context = new MigrationHarness.EmptyContext(MigrationHarness.NpgsqlOptions());
        return MigrationHarness.CreateDiffer<NpgsqlComplexIndexMigrationsModelDiffer>(context);
    }

    [TestMethod(DisplayName = "A complex-index-only change counts as a pending model change")]
    public void Complex_index_only_change_is_a_difference()
    {
        var source = MigrationHarness.SqliteModel<WithoutIndexContext>();
        var target = MigrationHarness.SqliteModel<WithIndexContext>();

        Assert.IsTrue(
            CoreDiffer().HasDifferences(source, target),
            "Adding a complex index is a model change. has-pending-model-changes and Migrate()'s "
          + "pending-changes check both rely on HasDifferences to see it.");
    }

    [TestMethod(DisplayName = "Removing a complex index counts as a pending model change")]
    public void Complex_index_removal_is_a_difference()
    {
        var source = MigrationHarness.SqliteModel<WithIndexContext>();
        var target = MigrationHarness.SqliteModel<WithoutIndexContext>();

        Assert.IsTrue(CoreDiffer().HasDifferences(source, target));
    }

    [TestMethod(DisplayName = "Identical models report no pending change")]
    public void Identical_models_are_not_a_difference()
    {
        var model = MigrationHarness.SqliteModel<WithIndexContext>();

        // Migrate() also calls HasDifferences with two builds of the same model to detect a
        // non-deterministic OnModelCreating; a false positive here would misreport every model.
        Assert.IsFalse(CoreDiffer().HasDifferences(model, MigrationHarness.SqliteModel<WithIndexContext>()));
        Assert.IsFalse(CoreDiffer().HasDifferences(model, model));
    }

    [TestMethod(DisplayName = "An exclusion-constraint-only change counts as a pending model change (Npgsql)")]
    public void Exclusion_constraint_only_change_is_a_difference()
    {
        var source = MigrationHarness.NpgsqlModel<WithoutConstraintContext>();
        var target = MigrationHarness.NpgsqlModel<WithConstraintContext>();

        // The satellite adds its constraint diffing in its own GetDifferences override; the core's
        // HasDifferences has to dispatch through that, not through the base Diff.
        Assert.IsTrue(NpgsqlDiffer().HasDifferences(source, target));
        Assert.IsFalse(NpgsqlDiffer().HasDifferences(target, MigrationHarness.NpgsqlModel<WithConstraintContext>()));
    }

    /// <summary>
    /// Documents the premise. EF's stock differ cannot see this package's declarations, which is why
    /// the runtime registration exists for <c>Migrate()</c>'s check and why the override above exists
    /// for the design-time commands. Should a future EF release start seeing them, this test says so.
    /// </summary>
    [TestMethod(DisplayName = "The stock EF differ does not see a complex-index-only change")]
    public void Stock_differ_does_not_see_the_change()
    {
        var source = MigrationHarness.SqliteModel<WithoutIndexContext>();
        var target = MigrationHarness.SqliteModel<WithIndexContext>();

        using var context = new MigrationHarness.EmptyContext(
            new DbContextOptionsBuilder().UseSqlite(MigrationHarness.SqliteConnection).Options);
        var stock = context.GetService<IMigrationsModelDiffer>();

        Assert.IsFalse(
            stock.HasDifferences(source, target),
            "EF's stock differ now sees complex-index declarations — revisit whether this package's "
          + "HasDifferences override and runtime registration are still needed.");
    }
}
