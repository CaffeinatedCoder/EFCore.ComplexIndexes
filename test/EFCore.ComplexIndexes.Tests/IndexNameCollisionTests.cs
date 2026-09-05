using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// A complex index and a native <c>HasIndex</c> on the same table can resolve to the same name. The
/// base differ emits one and this differ the other, neither seeing the other, so the migration
/// scaffolded two <c>CREATE INDEX</c> statements under one name and failed at apply time (42P07).
/// The differ's own name check only compared complex indexes with each other.
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

    // Same name on a different table is not a collision.
    private class SeparateTablesContext(DbContextOptions<SeparateTablesContext> options) : DbContext(options)
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

    [TestMethod(DisplayName = "A complex index named like a native HasIndex on the same table is rejected at migrations add")]
    public void Collision_with_native_index_throws()
    {
        var target = MigrationHarness.SqliteModel<CollisionContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.CoreDiff(null, target));

        StringAssert.Contains(exception.Message, "IX_dup");
        StringAssert.Contains(exception.Message, "HasIndex");
        StringAssert.Contains(exception.Message, "Name");
    }

    [TestMethod(DisplayName = "The satellite differs inherit the check")]
    public void Collision_is_rejected_by_the_npgsql_differ()
    {
        var target = MigrationHarness.NpgsqlModel<CollisionContext>();

        Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(null, target));
    }

    [TestMethod(DisplayName = "The same name on a different table is not a collision")]
    public void Same_name_on_another_table_is_fine()
    {
        var target = MigrationHarness.SqliteModel<SeparateTablesContext>();

        var creates = MigrationHarness.CoreDiff(null, target).OfType<CreateIndexOperation>().Where(o => o.Name == "IX_name").ToList();

        Assert.HasCount(2, creates);
        CollectionAssert.AreEquivalent(new[] { "companies", "people" }, creates.Select(o => o.Table).ToList());
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
}
