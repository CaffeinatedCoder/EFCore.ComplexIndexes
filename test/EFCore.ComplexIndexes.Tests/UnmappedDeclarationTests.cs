using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Every descriptor scan skipped entity types with no table. The abstract base of a TPC hierarchy is
/// one, so an index or constraint declared on it produced nothing — no DDL, no error. Under EF Core
/// 10 the base's complex columns are not mapped onto the concrete tables either, so the declaration
/// cannot be satisfied anywhere; failing at <c>migrations add</c> says so. View- and query-mapped
/// types keep being skipped: an index on a view is nothing this package could create.
/// </summary>
[TestClass]
public class UnmappedDeclarationTests
{
    private class Email
    {
        public string Value { get; set; } = "";
    }

    private abstract class Animal
    {
        public int                   Id      { get; set; }
        public Email                 Contact { get; set; } = new();
        public NpgsqlRange<DateOnly> Period  { get; set; }
    }

    private class Cat : Animal
    {
        public int Lives { get; set; }
    }

    private class Dog : Animal
    {
        public bool Barks { get; set; }
    }

    private class Person
    {
        public string Name  { get; set; } = "";
        public Email  Email { get; set; } = new();
    }

    private class TpcPropertyLevelContext(DbContextOptions<TpcPropertyLevelContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>(b =>
            {
                b.UseTpcMappingStrategy();
                b.Ignore(x => x.Period);
                b.ComplexProperty(x => x.Contact, c => c.Property(x => x.Value).HasComplexIndex(isUnique: true));
            });
            modelBuilder.Entity<Cat>().ToTable("cats");
            modelBuilder.Entity<Dog>().ToTable("dogs");
        }
    }

    private class TpcEntityLevelContext(DbContextOptions<TpcEntityLevelContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>(b =>
            {
                b.UseTpcMappingStrategy();
                b.Ignore(x => x.Period);
                b.ComplexProperty(x => x.Contact);
                b.HasComplexIndex(x => x.Contact.Value);
            });
            modelBuilder.Entity<Cat>().ToTable("cats");
            modelBuilder.Entity<Dog>().ToTable("dogs");
        }
    }

    private class TpcExclusionContext(DbContextOptions<TpcExclusionContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>(b =>
            {
                b.UseTpcMappingStrategy();
                b.ComplexProperty(x => x.Contact);
                b.HasExclusionConstraint(x => x.Id, x => x.Period);
            });
            modelBuilder.Entity<Cat>().ToTable("cats");
            modelBuilder.Entity<Dog>().ToTable("dogs");
        }
    }

    // TPT: the base has its own table, and that is where the columns live.
    private class TptContext(DbContextOptions<TptContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>(b =>
            {
                b.UseTptMappingStrategy();
                b.ToTable("animals");
                b.Ignore(x => x.Period);
                b.ComplexProperty(x => x.Contact, c => c.Property(x => x.Value).HasComplexIndex(isUnique: true));
            });
            modelBuilder.Entity<Cat>().ToTable("cats");
            modelBuilder.Entity<Dog>().ToTable("dogs");
        }
    }

    private class ViewContext(DbContextOptions<ViewContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.HasNoKey();
                b.ToView("v_people");
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasComplexIndex());
            });
    }

    [TestMethod(DisplayName = "A property-level index on a TPC base fails instead of producing nothing")]
    public void Tpc_property_level_index_throws()
    {
        var target = MigrationHarness.SqliteModel<TpcPropertyLevelContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.CoreDiff(null, target));

        StringAssert.Contains(exception.Message, "Animal");
        StringAssert.Contains(exception.Message, "not mapped to a table");
        StringAssert.Contains(exception.Message, "TPC");
    }

    [TestMethod(DisplayName = "An entity-level index on a TPC base fails instead of producing nothing")]
    public void Tpc_entity_level_index_throws()
    {
        var target = MigrationHarness.SqliteModel<TpcEntityLevelContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.CoreDiff(null, target));

        StringAssert.Contains(exception.Message, "complex indexes");
    }

    [TestMethod(DisplayName = "An exclusion constraint on a TPC base fails instead of producing nothing")]
    public void Tpc_exclusion_constraint_throws()
    {
        var target = MigrationHarness.NpgsqlModel<TpcExclusionContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(null, target));

        StringAssert.Contains(exception.Message, "exclusion constraints");
    }

    [TestMethod(DisplayName = "Under TPT the base table carries the index")]
    public void Tpt_base_table_gets_the_index()
    {
        var creates = MigrationHarness.CoreDiff(null, MigrationHarness.SqliteModel<TptContext>())
                                      .OfType<CreateIndexOperation>()
                                      .ToList();

        Assert.HasCount(1, creates);
        Assert.AreEqual("animals", creates[0].Table);
    }

    [TestMethod(DisplayName = "A declaration on a view-mapped type is still skipped without error")]
    public void View_mapped_type_is_skipped()
    {
        var operations = MigrationHarness.CoreDiff(null, MigrationHarness.SqliteModel<ViewContext>());

        Assert.IsFalse(operations.OfType<CreateIndexOperation>().Any());
    }
}
