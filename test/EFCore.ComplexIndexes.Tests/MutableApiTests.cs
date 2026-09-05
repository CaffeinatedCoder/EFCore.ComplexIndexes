using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// A soft-delete helper that installs the query filter automatically had no way to install the
/// matching index filter, so every unique index and exclusion constraint on a withdrawable
/// aggregate repeated it by hand. <c>AddComplexIndexFilter</c> and
/// <c>AddExclusionConstraintFilter</c> AND a predicate onto what is declared, idempotently, on the
/// mutable model; <c>AddComplexIndex</c> adds a declaration with the fluent API's identity rules.
/// </summary>
[TestClass]
public class MutableApiTests
{
    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class Grant
    {
        public Guid                  Id        { get; set; }
        public Guid                  GranteeId { get; set; }
        public string                Role      { get; set; } = "";
        public EmailAddress          Email     { get; set; } = new();
        public DateTime?             RevokedAt { get; set; }
        public DateTime?             DeletedAt { get; set; }
        public NpgsqlRange<DateOnly> Period    { get; set; }
    }

    // The helper's shape: configurations declare, then one call at the end installs the obligation.
    private class AmendingContext(DbContextOptions<AmendingContext> options) : DbContext(options)
    {
        public int Amended { get; private set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Grant>(b =>
            {
                b.ToTable("grants");
                b.HasKey(x => x.Id);
                b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
                b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
                b.ComplexProperty(x => x.Email, c => c.Property(e => e.Value).HasComplexIndex(isUnique: true, indexName: "ux_grant_email"));
                b.HasComplexIndex(x => x.Role, isUnique: true, filter: "{DeletedAt} IS NULL", indexName: "ux_grant_role");
                b.HasComplexIndex(x => x.GranteeId, indexName: "ix_grant_grantee");
                b.HasExclusionConstraint(x => x.GranteeId, x => x.Period, name: "ex_grant_period");
                b.HasExclusionConstraint(x => x.Role, x => x.Period, filter: "{DeletedAt} IS NULL", name: "ex_grant_role_period");
            });

            var grant = modelBuilder.Model.FindEntityType(typeof(Grant))!;
            Amended += grant.AddComplexIndexFilter("{RevokedAt} IS NULL", ix => ix.IsUnique);
            Amended += grant.AddExclusionConstraintFilter("{RevokedAt} IS NULL");

            // Applying it again must change nothing.
            Amended += grant.AddComplexIndexFilter("{RevokedAt} IS NULL", ix => ix.IsUnique);
            Amended += grant.AddExclusionConstraintFilter("{RevokedAt} IS NULL");
        }
    }

    private class AddingContext(DbContextOptions<AddingContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Grant>(b =>
            {
                b.ToTable("grants");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email);
            });

            var grant = modelBuilder.Model.FindEntityType(typeof(Grant))!;
            grant.AddComplexIndex(new CompositeIndexDefinition { PropertyPaths = ["GranteeId", "Role"], IsUnique = false, IndexName = "ix_first" });
            // Same parts, same (null) filter: replaces — with the fluent API's identity rule.
            grant.AddComplexIndex(new CompositeIndexDefinition { PropertyPaths = ["GranteeId", "Role"], IsUnique = true, IndexName = "ux_grantee_role" });
        }
    }

    private static string FilterOf(IEnumerable<MigrationOperation> operations, string index)
        => operations.OfType<CreateIndexOperation>().Single(o => o.Name == index).Filter!;

    [TestMethod(DisplayName = "The predicate is ANDed onto selected declarations, property-level and entity-level, once")]
    public void Filters_are_amended_idempotently()
    {
        // Amended is instance state written from OnModelCreating, so this instance's OnModelCreating
        // has to run. Contexts with equal options share an internal service provider and its model
        // cache, and EF builds the runtime model from an already-cached design-time model without
        // calling OnModelCreating again — which the sibling test, building this context's
        // design-time model through the harness, does in parallel. A private provider starts empty.
        using var context = new AmendingContext(new DbContextOptionsBuilder<AmendingContext>()
                                               .UseNpgsql(MigrationHarness.NpgsqlConnection)
                                               .EnableServiceProviderCaching(false)
                                               .Options);
        var grant = context.Model.FindEntityType(typeof(Grant))!;

        // Two unique indexes and two constraints on the first pass; nothing on the second.
        Assert.AreEqual(4, context.Amended);

        var byName = grant.GetComplexIndexes().ToDictionary(i => i.Name!);
        Assert.AreEqual("{RevokedAt} IS NULL", byName["ux_grant_email"].Filter);
        Assert.AreEqual("({DeletedAt} IS NULL) AND ({RevokedAt} IS NULL)", byName["ux_grant_role"].Filter);
        Assert.IsNull(byName["ix_grant_grantee"].Filter, "the non-unique index was not selected");

        var constraints = grant.GetExclusionConstraints().ToDictionary(c => c.Name!);
        Assert.AreEqual("{RevokedAt} IS NULL", constraints["ex_grant_period"].Filter);
        Assert.AreEqual("({DeletedAt} IS NULL) AND ({RevokedAt} IS NULL)", constraints["ex_grant_role_period"].Filter);
    }

    [TestMethod(DisplayName = "Amended filters reach the migration, placeholders resolved")]
    public void Amended_filters_reach_the_migration()
    {
        var operations = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<AmendingContext>());

        Assert.AreEqual("\"revoked_at\" IS NULL", FilterOf(operations, "ux_grant_email"));
        Assert.AreEqual("(\"deleted_at\" IS NULL) AND (\"revoked_at\" IS NULL)", FilterOf(operations, "ux_grant_role"));

        var constraint = operations.OfType<SqlOperation>().Single(o => o.Sql.Contains("\"ex_grant_role_period\" EXCLUDE"));
        StringAssert.Contains(constraint.Sql, "WHERE ((\"deleted_at\" IS NULL) AND (\"revoked_at\" IS NULL))");
    }

    [TestMethod(DisplayName = "Conjoin: null takes the predicate, a filter gets AND, an applied predicate is left alone")]
    public void Conjoin_rules()
    {
        Assert.AreEqual("p", ComplexIndexStorage.Conjoin(null, "p"));
        Assert.AreEqual("(a) AND (p)", ComplexIndexStorage.Conjoin("a", "p"));
        Assert.IsNull(ComplexIndexStorage.Conjoin("p", "p"));
        Assert.IsNull(ComplexIndexStorage.Conjoin("(a) AND (p)", "p"));
        Assert.AreEqual("(NOT (p)) AND (p)", ComplexIndexStorage.Conjoin("NOT (p)", "p"), "a negation is not the predicate");
    }

    [TestMethod(DisplayName = "AddComplexIndex applies the fluent API's identity rule and reaches the migration")]
    public void Add_complex_index_on_the_mutable_type()
    {
        var target     = MigrationHarness.NpgsqlModel<AddingContext>();
        var operations = MigrationHarness.NpgsqlDiff(null, target);

        var indexes = target.Model.FindEntityType(typeof(Grant))!.GetComplexIndexes();
        Assert.HasCount(1, indexes);
        Assert.AreEqual("ux_grantee_role", indexes[0].Name);

        var create = operations.OfType<CreateIndexOperation>().Single();
        Assert.AreEqual("ux_grantee_role", create.Name);
        Assert.IsTrue(create.IsUnique);
        CollectionAssert.AreEqual(new[] { "GranteeId", "Role" }, create.Columns);
    }

    [TestMethod(DisplayName = "A reused explicit name is rejected at the call, as the fluent API does")]
    public void Reused_name_is_rejected()
    {
        using var context = new CollidingContext(new DbContextOptionsBuilder<CollidingContext>().UseNpgsql(MigrationHarness.NpgsqlConnection).Options);

        var exception = Assert.ThrowsExactly<ArgumentException>(() => _ = context.Model);

        StringAssert.Contains(exception.Message, "ix_dup");
    }

    private class CollidingContext(DbContextOptions<CollidingContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Grant>(b =>
            {
                b.ToTable("grants");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email);
            });

            var grant = modelBuilder.Model.FindEntityType(typeof(Grant))!;
            grant.AddComplexIndex(new CompositeIndexDefinition { PropertyPaths = ["GranteeId"], IndexName = "ix_dup" });
            grant.AddComplexIndex(new CompositeIndexDefinition { PropertyPaths = ["Role"],      IndexName = "ix_dup" });
        }
    }
}
