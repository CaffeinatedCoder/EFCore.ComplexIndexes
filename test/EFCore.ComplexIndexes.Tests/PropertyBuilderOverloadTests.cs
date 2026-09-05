using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// EF hands back the non-generic <c>ComplexTypePropertyBuilder</c> for properties configured by name
/// or by type. The property-level API only existed on the generic builder, so those properties could
/// not carry a complex index at all — the call simply did not compile.
/// </summary>
[TestClass]
public class PropertyBuilderOverloadTests
{
    private class Name
    {
        public string First { get; set; } = "";
        public string Last  { get; set; } = "";
        public string Nick  { get; set; } = "";
    }

    private class Person
    {
        public Guid Id   { get; set; }
        public Name Name { get; set; } = new();
    }

    private class ByNameContext(DbContextOptions<ByNameContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Name, c =>
                {
                    // Non-generic builders: by name, and by type and name.
                    c.Property("First").HasComplexIndex(isUnique: true, indexName: "ux_people_first");
                    c.Property(typeof(string), "Last").HasComplexIndex(ix => ix.HasName("ix_people_last").HasFilter("\"Name_Last\" <> ''"));

                    // The generic overload still returns the generic builder, so typed chaining keeps working.
                    ComplexTypePropertyBuilder<string> typed = c.Property(x => x.Nick).HasComplexIndex(indexName: "ix_people_nick");
                    typed.HasMaxLength(50);
                });
            });
    }

    [TestMethod(DisplayName = "Properties configured by name or type carry complex indexes like typed ones")]
    public void Non_generic_builders_produce_indexes()
    {
        var creates = MigrationHarness.CoreDiff(null, MigrationHarness.SqliteModel<ByNameContext>())
                                      .OfType<CreateIndexOperation>()
                                      .ToDictionary(o => o.Name);

        Assert.IsTrue(creates["ux_people_first"].IsUnique);
        CollectionAssert.AreEqual(new[] { "Name_First" }, creates["ux_people_first"].Columns);

        Assert.AreEqual("\"Name_Last\" <> ''", creates["ix_people_last"].Filter);
        CollectionAssert.AreEqual(new[] { "Name_Last" }, creates["ix_people_last"].Columns);

        CollectionAssert.AreEqual(new[] { "Name_Nick" }, creates["ix_people_nick"].Columns);
    }
}
