using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Per-column index collations. The option is stored under Npgsql's model key
/// (<c>Npgsql:IndexCollation</c>) — on a property, EF's <c>Relational:Collation</c> would mean the
/// <em>column's</em> collation — and mapped to <c>Relational:Collation</c> on the operation, which is
/// where Npgsql's generator reads it. The reverse must never happen: a column's own collation is a
/// column facet and stays off the index.
/// </summary>
[TestClass]
public class NpgsqlCollationTests
{
    private const string OperationCollation = "Relational:Collation";

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

    private class CollationContext(DbContextOptions<CollationContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email, c =>
                {
                    c.Property(x => x.Value).HasColumnName("email");

                    // Property-level: the option travels through the whitelist.
                    c.Property(x => x.Value).HasComplexIndex(ix => ix.UseCollation("C").HasName("ix_people_email_c"));
                });

                // Entity-level, positional: only the first column is collated.
                b.HasComplexCompositeIndex(x => new { x.Name, x.Email.Value }, ix => ix
                    .UseCollation("C", "")
                    .HasName("ix_people_name_email"));

                // Expression index with a collation and an operator class: COLLATE goes first.
                b.HasExpressionIndex(ix => ix
                    .Expression("lower(email)")
                    .UseCollation("C")
                    .HasOperators("text_pattern_ops")
                    .HasName("ix_people_email_ci"));
            });
    }

    // The leak case: the column has a collation of its own, the index does not.
    private class ColumnCollationContext(DbContextOptions<ColumnCollationContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email, c =>
                {
                    c.Property(x => x.Value).HasColumnName("email").UseCollation("de-DE-x-icu");
                    c.Property(x => x.Value).HasComplexIndex(indexName: "ix_people_email");
                });
            });
    }

    // Only the collation option, entity-level so it reaches the operation unfiltered, so the SQL
    // Server rejection below is unambiguously about it.
    private class IndexCollationOnlyContext(DbContextOptions<IndexCollationOnlyContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("people");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email);
                b.HasComplexIndex(x => x.Email.Value, ix => ix.UseCollation("C"));
            });
    }

    private static Dictionary<string, CreateIndexOperation> Creates()
        => MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<CollationContext>())
                           .OfType<CreateIndexOperation>()
                           .ToDictionary(o => o.Name);

    [TestMethod(DisplayName = "A property-level index collation reaches the operation under Relational:Collation")]
    public void Property_level_collation_is_mapped_onto_the_operation()
    {
        var op = Creates()["ix_people_email_c"];

        CollectionAssert.AreEqual(new[] { "C" }, (string[])op[OperationCollation]!);
        Assert.IsNull(op["Npgsql:IndexCollation"], "The stored key must not leak onto the operation alongside the mapped one.");

        StringAssert.Contains(
            MigrationHarness.NpgsqlSql([op], complexIndexWiring: false),
            "CREATE INDEX ix_people_email_c ON people (email COLLATE \"C\")");
    }

    [TestMethod(DisplayName = "Entity-level collations are positional and render through the stock generator")]
    public void Entity_level_collation_is_positional()
    {
        var op = Creates()["ix_people_name_email"];

        StringAssert.Contains(
            MigrationHarness.NpgsqlSql([op], complexIndexWiring: false),
            "ON people (\"Name\" COLLATE \"C\", email)");
    }

    [TestMethod(DisplayName = "The custom generator renders COLLATE before the operator class on expression indexes")]
    public void Expression_index_collation_renders_before_operator_class()
    {
        var op = Creates()["ix_people_email_ci"];

        StringAssert.Contains(
            MigrationHarness.NpgsqlSql([op]),
            "CREATE INDEX ix_people_email_ci ON people ((lower(email)) COLLATE \"C\" text_pattern_ops)");
    }

    [TestMethod(DisplayName = "A column's own collation is never copied onto the index")]
    public void Column_collation_does_not_leak_onto_the_index()
    {
        var op = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<ColumnCollationContext>())
                                 .OfType<CreateIndexOperation>()
                                 .Single(o => o.Name == "ix_people_email");

        Assert.IsNull(op[OperationCollation]);
        Assert.IsNull(op["Npgsql:IndexCollation"]);

        StringAssert.Contains(
            MigrationHarness.NpgsqlSql([op], complexIndexWiring: false),
            "CREATE INDEX ix_people_email ON people (email);");
    }

    [TestMethod(DisplayName = "Collations do not churn between two builds of the same model")]
    public void Collations_do_not_churn()
    {
        var operations = MigrationHarness.NpgsqlDiff(
            MigrationHarness.NpgsqlModel<CollationContext>(),
            MigrationHarness.NpgsqlModel<CollationContext>());

        Assert.IsFalse(operations.OfType<CreateIndexOperation>().Any());
        Assert.IsFalse(operations.OfType<DropIndexOperation>().Any());
    }

    [TestMethod(DisplayName = "The SQL Server differ rejects a PostgreSQL collation option with a targeted error")]
    public void SqlServer_rejects_npgsql_collation()
    {
        var target = MigrationHarness.SqlServerModel<IndexCollationOnlyContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.SqlServerDiff(null, target));

        StringAssert.Contains(exception.Message, "Npgsql:");
    }
}
