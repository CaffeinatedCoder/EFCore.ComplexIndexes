using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Every descriptor scan skipped entity types with no table. The abstract base of a TPC hierarchy is
/// one, so an index or constraint declared on it produced nothing — no DDL, no error. Since 5.5.0 a
/// declaration there is created on every concrete table, as EF Core creates its own indexes
/// (<see cref="TpcHierarchyTests"/>), except over a complex member: EF Core 10 maps the base's
/// complex columns onto none of the concrete tables, so that declaration cannot be satisfied
/// anywhere, and failing at <c>migrations add</c> says so. A type no table holds the rows of still
/// fails. View- and query-mapped types keep being skipped: an index on a view is nothing this
/// package could create.
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

    private class TpcScalarIndexContext(DbContextOptions<TpcScalarIndexContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>(b =>
            {
                b.UseTpcMappingStrategy();
                b.Ignore(x => x.Period);
                b.Ignore(x => x.Contact);
                b.HasComplexIndex(x => x.Id, isUnique: true);
            });
            modelBuilder.Entity<Cat>().ToTable("cats");
            modelBuilder.Entity<Dog>().ToTable("dogs");
        }
    }

    // An explicit name on the abstract base, which has no table of its own to keep it on.
    private class TpcNamedIndexContext(DbContextOptions<TpcNamedIndexContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>(b =>
            {
                b.UseTpcMappingStrategy();
                b.Ignore(x => x.Period);
                b.Ignore(x => x.Contact);
                b.HasComplexIndex(x => x.Id, isUnique: true, indexName: "ix_animal_id");
            });
            modelBuilder.Entity<Cat>().ToTable("cats");
            modelBuilder.Entity<Dog>().ToTable("dogs");
        }
    }

    // The only concrete type maps to a view: no table holds the base's rows.
    private class TpcViewOnlyContext(DbContextOptions<TpcViewOnlyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>(b =>
            {
                b.UseTpcMappingStrategy();
                b.Ignore(x => x.Period);
                b.Ignore(x => x.Contact);
                b.HasComplexIndex(x => x.Id);
            });
            modelBuilder.Entity<Cat>().ToView("v_cats");
            modelBuilder.Ignore<Dog>();
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

    [TestMethod(DisplayName = "A property-level index over a complex member of an abstract TPC base fails instead of producing nothing")]
    public void Tpc_property_level_index_throws()
    {
        var target = MigrationHarness.SqliteModel<TpcPropertyLevelContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.CoreDiff(null, target));

        StringAssert.Contains(exception.Message, "'Animal' declares a complex index over 'Contact.Value'");
        StringAssert.Contains(exception.Message, "EF Core maps no column for 'Contact.Value' in 'cats'");
        StringAssert.Contains(exception.Message, "TPC");
    }

    [TestMethod(DisplayName = "An entity-level index over a complex member of an abstract TPC base fails instead of producing nothing")]
    public void Tpc_entity_level_index_throws()
    {
        var target = MigrationHarness.SqliteModel<TpcEntityLevelContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.CoreDiff(null, target));

        StringAssert.Contains(exception.Message, "EF Core maps no column for 'Contact.Value' in 'cats'");
    }

    [TestMethod(DisplayName = "An index on an abstract TPC base is created on every concrete table")]
    public void Tpc_scalar_index_lands_on_every_concrete_table()
    {
        var creates = MigrationHarness.CoreDiff(null, MigrationHarness.SqliteModel<TpcScalarIndexContext>())
                                      .OfType<CreateIndexOperation>()
                                      .OrderBy(c => c.Table, StringComparer.Ordinal)
                                      .ToList();

        CollectionAssert.AreEqual(new[] { "cats", "dogs" }, creates.Select(c => c.Table).ToArray());
        CollectionAssert.AreEqual(new[] { "IX_cats_Id", "IX_dogs_Id" }, creates.Select(c => c.Name).ToArray());
    }

    [TestMethod(DisplayName = "An explicit name on an abstract TPC base is followed by each concrete table's name (#167)")]
    public void Tpc_named_index_takes_each_concrete_table()
    {
        var creates = MigrationHarness.CoreDiff(null, MigrationHarness.SqliteModel<TpcNamedIndexContext>())
                                      .OfType<CreateIndexOperation>()
                                      .OrderBy(c => c.Table, StringComparer.Ordinal)
                                      .ToList();

        CollectionAssert.AreEqual(new[] { "ix_animal_id_cats", "ix_animal_id_dogs" }, creates.Select(c => c.Name).ToArray());
    }

    [TestMethod(DisplayName = "An exclusion constraint on an abstract TPC base is added to every concrete table")]
    public void Tpc_exclusion_constraint_lands_on_every_concrete_table()
    {
        var adds = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<TpcExclusionContext>())
                                   .OfType<SqlOperation>()
                                   .Select(o => o.Sql)
                                   .Where(s => s.Contains("EXCLUDE"))
                                   .ToList();

        Assert.HasCount(2, adds);
        Assert.IsTrue(adds.Any(s => s.Contains("ALTER TABLE \"cats\" ADD CONSTRAINT \"EX_cats_Id_Period\"")));
        Assert.IsTrue(adds.Any(s => s.Contains("ALTER TABLE \"dogs\" ADD CONSTRAINT \"EX_dogs_Id_Period\"")));
    }

    [TestMethod(DisplayName = "A declaration on a TPC base whose rows no table holds still fails")]
    public void Tpc_base_without_a_table_throws()
    {
        var target = MigrationHarness.SqliteModel<TpcViewOnlyContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.CoreDiff(null, target));

        StringAssert.Contains(exception.Message, "'Animal' declares complex indexes but is not mapped to a table");
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
