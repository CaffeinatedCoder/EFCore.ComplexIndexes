using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Ten filter strings in one application repeated the column names the model already knows. A
/// typed predicate is translated at declaration into a filter template — the placeholders the
/// differ resolves — so <c>x => x.RevokedAt == null</c> renders <c>"revoked_at" IS NULL</c> whatever
/// <c>HasColumnName</c> decided. The subset is small and refuses at the declaration what it cannot
/// translate faithfully: enums, whose storage the filter cannot see, and values without a portable
/// SQL spelling.
/// </summary>
[TestClass]
public class NpgsqlTypedFilterTests
{
    private enum Status { Active, Revoked }

    private class Method
    {
        public string Type { get; set; } = "";
    }

    private class Grant
    {
        public Guid                  Id        { get; set; }
        public Guid                  GranteeId { get; set; }
        public string                Email     { get; set; } = "";
        public string                Kind      { get; set; } = "";
        public bool                  IsDeleted { get; set; }
        public int?                  Count     { get; set; }
        public DateTime?             RevokedAt { get; set; }
        public DateTime              ValidTo   { get; set; }
        public Status                Status    { get; set; }
        public string[]              Tags      { get; set; } = [];
        public Method                Method    { get; set; } = new();
        public NpgsqlRange<DateOnly> Period    { get; set; }
    }

    private static readonly bool IncludeArchived = false;

    private class TypedFilterContext(DbContextOptions<TypedFilterContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Grant>(b =>
            {
                b.ToTable("grants");
                b.HasKey(x => x.Id);
                b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
                b.Property(x => x.IsDeleted).HasColumnName("is_deleted");
                b.ComplexProperty(x => x.Method, c => c.ToJson("method"));

                b.HasComplexIndex(x => x.Email, x => x.RevokedAt == null, isUnique: true, indexName: "ux_email_active");
                b.HasComplexIndex(x => x.Kind, x => x.RevokedAt != null && x.Kind == "federated", indexName: "ix_kind");
                b.HasComplexIndex(x => x.Count, x => !x.IsDeleted || x.Count > 0, indexName: "ix_count");
                b.HasComplexIndex(x => x.GranteeId, x => x.Email.ToLower() == "root" && x.Count <= 10 && x.Count >= 1 && x.Count < 5, indexName: "ix_operators");
                b.HasComplexIndex(x => x.Email, x => x.Method.Type == "federated", indexName: "ix_json");
                b.HasComplexIndex(x => x.Kind, x => x.IsDeleted == IncludeArchived, indexName: "ix_captured");
                b.HasComplexCompositeIndex(x => new { x.GranteeId, x.Kind }, x => x.RevokedAt == null, indexName: "ix_composite");
                b.HasExpressionIndex(x => x.Email.ToLower(), x => x.RevokedAt == null, indexName: "ix_email_lower_active");
                b.HasComplexIndex(x => x.Email, ix => ix.HasFilter<Grant>(g => g.IsDeleted).HasName("ix_builder"));
                b.HasExclusionConstraint(x => x.GranteeId, x => x.Period, x => x.RevokedAt == null, name: "ex_active");
                b.HasExclusionConstraint(ex => ex.WithEquality(x => x.Kind).WithOverlaps(x => x.Period)
                                                 .HasFilter(x => x.RevokedAt != null).HasName("ex_revoked"));
            });
    }

    private static Dictionary<string, string?> Filters<TContext>() where TContext : DbContext
        => MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<TContext>())
                           .OfType<CreateIndexOperation>()
                           .ToDictionary(o => o.Name, o => o.Filter);

    [TestMethod(DisplayName = "Null checks, boolean logic and comparisons translate, resolved against the mapping")]
    public void Predicates_translate()
    {
        var filters = Filters<TypedFilterContext>();

        Assert.AreEqual("\"revoked_at\" IS NULL", filters["ux_email_active"]);
        Assert.AreEqual("(\"revoked_at\" IS NOT NULL AND \"Kind\" = 'federated')", filters["ix_kind"]);
        Assert.AreEqual("(NOT (\"is_deleted\") OR \"Count\" > 0)", filters["ix_count"]);
        Assert.AreEqual("(((lower(\"Email\") = 'root' AND \"Count\" <= 10) AND \"Count\" >= 1) AND \"Count\" < 5)", filters["ix_operators"]);
        Assert.AreEqual("(\"method\" ->> 'Type') = 'federated'", filters["ix_json"]);
        Assert.AreEqual("\"is_deleted\" = FALSE", filters["ix_captured"]);
        Assert.AreEqual("\"revoked_at\" IS NULL", filters["ix_composite"]);
        Assert.AreEqual("\"is_deleted\"", filters["ix_builder"]);
    }

    [TestMethod(DisplayName = "Expression indexes and exclusion constraints take the same predicates")]
    public void Expression_indexes_and_constraints_take_predicates()
    {
        var operations = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<TypedFilterContext>());

        StringAssert.Contains(MigrationHarness.NpgsqlSql(operations), "ix_email_lower_active ON grants ((lower(\"Email\"))) WHERE \"revoked_at\" IS NULL;");

        var active = operations.OfType<SqlOperation>().Single(o => o.Sql.Contains("\"ex_active\" EXCLUDE"));
        StringAssert.Contains(active.Sql, "WHERE (\"revoked_at\" IS NULL)");

        var revoked = operations.OfType<SqlOperation>().Single(o => o.Sql.Contains("\"ex_revoked\" EXCLUDE"));
        StringAssert.Contains(revoked.Sql, "WHERE (\"revoked_at\" IS NOT NULL)");
    }

    [TestMethod(DisplayName = "The declaration stores a template; the model does not churn")]
    public void Stored_as_template_without_churn()
    {
        var model = MigrationHarness.NpgsqlModel<TypedFilterContext>();

        Assert.AreEqual("{RevokedAt} IS NULL", model.Model.FindComplexIndex("ux_email_active")!.Filter);
        Assert.IsEmpty(MigrationHarness.NpgsqlDiff(model, MigrationHarness.NpgsqlModel<TypedFilterContext>()));
    }

    [TestMethod(DisplayName = "An enum comparison is refused at the declaration")]
    public void Enum_comparison_is_refused()
    {
        var exception = Assert.ThrowsExactly<NotSupportedException>(
            () => new ComplexIndexBuilder().HasFilter<Grant>(g => g.Status == Status.Active));

        StringAssert.Contains(exception.Message, "enum");
    }

    [TestMethod(DisplayName = "A value without a portable SQL spelling is refused at the declaration")]
    public void Date_literal_is_refused()
    {
        var exception = Assert.ThrowsExactly<NotSupportedException>(
            () => new ComplexIndexBuilder().HasFilter<Grant>(g => g.ValidTo > DateTime.UtcNow));

        StringAssert.Contains(exception.Message, "DateTime");
    }

    [TestMethod(DisplayName = "An untranslatable construct is refused at the declaration, pointing at the string overload")]
    public void Unsupported_construct_is_refused()
    {
        var exception = Assert.ThrowsExactly<NotSupportedException>(
            () => new ComplexIndexBuilder().HasFilter<Grant>(g => g.Tags.Contains("urgent")));

        StringAssert.Contains(exception.Message, "string filter overload");
    }

    [TestMethod(DisplayName = "The typed amend calls translate and delegate")]
    public void Typed_amend_calls()
    {
        // Amended is written from OnModelCreating; a private service provider guarantees it runs for this
        // instance instead of EF reusing a model another test cached (see MutableApiTests).
        using var context = new AmendingContext(new DbContextOptionsBuilder<AmendingContext>()
                                               .UseNpgsql(MigrationHarness.NpgsqlConnection)
                                               .EnableServiceProviderCaching(false)
                                               .Options);
        var grant = context.Model.FindEntityType(typeof(Grant))!;

        Assert.AreEqual(2, context.Amended);
        Assert.AreEqual("{RevokedAt} IS NULL", grant.GetComplexIndexes().Single(i => i.Name == "ux_email").Filter);
        Assert.AreEqual("{RevokedAt} IS NULL", grant.GetExclusionConstraints().Single().Filter);
    }

    private class AmendingContext(DbContextOptions<AmendingContext> options) : DbContext(options)
    {
        public int Amended { get; private set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Grant>(b =>
            {
                b.ToTable("grants");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Method);
                b.HasComplexIndex(x => x.Email, isUnique: true, indexName: "ux_email");
                b.HasExclusionConstraint(x => x.GranteeId, x => x.Period, name: "ex_period");
            });

            var grant = modelBuilder.Model.FindEntityType(typeof(Grant))!;
            Amended += grant.AddComplexIndexFilter<Grant>(g => g.RevokedAt == null);
            Amended += grant.AddExclusionConstraintFilter<Grant>(g => g.RevokedAt == null);
        }
    }
}
