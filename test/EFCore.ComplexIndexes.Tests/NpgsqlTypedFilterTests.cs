using System.Linq.Expressions;
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

    private class BraceLiteralContext(DbContextOptions<BraceLiteralContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Grant>(b =>
            {
                b.ToTable("grants");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Method);
                b.HasComplexIndex(x => x.Kind, x => x.Kind != "{draft}" && x.Email != "{{1,2}}", indexName: "ix_kind_final");
                b.HasExclusionConstraint(x => x.GranteeId, x => x.Period, x => x.Kind == "}{", name: "ex_braces");
            });
    }

    [TestMethod(DisplayName = "A brace in a string literal is compared as written")]
    public void Brace_in_literal_is_kept()
    {
        var operations = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<BraceLiteralContext>());

        // The filter resolver has no {{ escape, so a doubled brace stayed doubled and the index
        // compared against '{{draft}}', a different value.
        Assert.AreEqual("(\"Kind\" <> '{draft}' AND \"Email\" <> '{{1,2}}')",
                        operations.OfType<CreateIndexOperation>().Single(o => o.Name == "ix_kind_final").Filter);
        StringAssert.Contains(operations.OfType<SqlOperation>().Single(o => o.Sql.Contains("\"ex_braces\" EXCLUDE")).Sql,
                              "WHERE (\"Kind\" = '}{')");
    }

    // ── Null semantics ──

    private class Account
    {
        public Guid    Id        { get; set; }
        public string  Email     { get; set; } = "";
        public string? Kind      { get; set; }
        public bool?   Flag      { get; set; }
        public int     Count     { get; set; }
        public int?    Maybe     { get; set; }
        public string? Name      { get; set; }
        public bool    IsDeleted { get; set; }
    }

    private static readonly string? NullMarker = null;

    // Each predicate here once translated with SQL's three-valued logic: a filter that is NULL for a
    // row leaves the row out of the index, so `Kind != "federated"` stopped covering rows whose Kind
    // is null although the lambda is true for them, and a unique index over "live" rows enforced
    // nothing on exactly those rows.
    private static readonly Dictionary<string, Expression<Func<Account, bool>>> NullablePredicates = new()
    {
        ["ix_not_equal"]      = x => x.Kind != "federated",
        ["ix_bool_not_equal"] = x => x.Flag != true,
        ["ix_not_or"]         = x => !(x.Count == 1 || x.Maybe == 2),
        ["ix_captured_null"]  = x => x.Name == NullMarker,
        ["ix_columns"]        = x => x.Kind != x.Name,
    };

    private class NullSemanticsContext(DbContextOptions<NullSemanticsContext> options) : DbContext(options)
    {
        public DbSet<Account> Accounts => Set<Account>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Account>(b =>
            {
                b.ToTable("accounts");
                b.HasKey(x => x.Id);
                foreach (var (name, predicate) in NullablePredicates)
                    b.HasComplexIndex(x => x.Email, predicate, indexName: name);

                b.HasComplexIndex(x => x.Email, x => !(x.Maybe > 5), indexName: "ix_not_greater");
                b.HasComplexIndex(x => x.Email, x => x.Count != 1 && !(x.Count > 5) && !x.IsDeleted, indexName: "ix_not_nullable");
                b.HasComplexIndex(x => x.Email, x => x.Kind != NullMarker, indexName: "ix_captured_not_null");
            });
    }

    [TestMethod(DisplayName = "A comparison with a nullable operand keeps C#'s answer for null rows")]
    public void Nullable_operands_keep_csharp_semantics()
    {
        var filters = Filters<NullSemanticsContext>();

        Assert.AreEqual("(\"Kind\" <> 'federated' OR \"Kind\" IS NULL)", filters["ix_not_equal"]);
        Assert.AreEqual("(\"Flag\" <> TRUE OR \"Flag\" IS NULL)", filters["ix_bool_not_equal"]);
        Assert.AreEqual("(\"Count\" <> 1 AND (\"Maybe\" <> 2 OR \"Maybe\" IS NULL))", filters["ix_not_or"]);
        Assert.AreEqual("\"Name\" IS NULL", filters["ix_captured_null"]);
        Assert.AreEqual("\"Kind\" IS NOT NULL", filters["ix_captured_not_null"]);
        Assert.AreEqual("(\"Maybe\" <= 5 OR \"Maybe\" IS NULL)", filters["ix_not_greater"]);
    }

    [TestMethod(DisplayName = "A filter over nullable operands is the one EF Core writes for the same lambda")]
    public void Nullable_operands_match_ef_core_queries()
    {
        var filters = Filters<NullSemanticsContext>();

        using var context = new NullSemanticsContext(new DbContextOptionsBuilder<NullSemanticsContext>()
                                                    .UseNpgsql(MigrationHarness.NpgsqlConnection).Options);

        foreach (var (name, predicate) in NullablePredicates)
        {
            var query = context.Accounts.Where(predicate).ToQueryString();
            var where = query[(query.IndexOf("WHERE ", StringComparison.Ordinal) + "WHERE ".Length)..].Replace("a.", "");

            // Parentheses differ (the filter brackets every AND/OR), and EF writes the captured
            // null as a parameter-free IS NULL; the tokens and their order must not.
            Assert.AreEqual(Normalize(where), Normalize(filters[name]!), name);
        }

        static string Normalize(string sql) => string.Concat(sql.Where(c => c is not ('(' or ')' or ' ' or '\n')));
    }

    [TestMethod(DisplayName = "Operands that cannot be null translate as before")]
    public void Non_nullable_operands_are_unchanged()
        => Assert.AreEqual("((\"Count\" <> 1 AND NOT (\"Count\" > 5)) AND NOT (\"IsDeleted\"))",
                           Filters<NullSemanticsContext>()["ix_not_nullable"]);

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

    // ── Char, HasValue, Value and other members of scalar types (#80) ──

    private class Ticket
    {
        public Guid      Id        { get; set; }
        public char      Grade     { get; set; }
        public char?     Backup    { get; set; }
        public DateTime? RevokedAt { get; set; }
        public int?      Count     { get; set; }
        public bool?     Flag      { get; set; }
        public DateTime  CreatedAt { get; set; }
    }

    private static string Predicate(Expression<Func<Ticket, bool>> predicate) => NpgsqlLinqIndexTranslator.TranslatePredicate(predicate);

    [TestMethod(DisplayName = "A char comparison renders a char literal, not the char's code")]
    public void Char_comparison_renders_a_char()
    {
        // C# compares Convert(x.Grade, int) with 65; "Grade" = 65 failed at apply.
        Assert.AreEqual("{Grade} = 'A'", Predicate(x => x.Grade == 'A'));
        Assert.AreEqual("'A' < {Grade}", Predicate(x => 'A' < x.Grade));
        Assert.AreEqual("coalesce({Backup}, 'B') = 'A'", Predicate(x => (x.Backup ?? 'B') == 'A'));
        Assert.AreEqual("{Grade} = {Backup}", Predicate(x => x.Grade == x.Backup));
    }

    // .Value on a nullable is what the lambdas under test are written with.
#pragma warning disable CS8629
    [TestMethod(DisplayName = "HasValue is the null check and Value is the column")]
    public void HasValue_and_Value_translate()
    {
        // Both passed the declaration as the paths {RevokedAt.HasValue} and {Count.Value} and failed
        // at migrations add.
        Assert.AreEqual("{RevokedAt} IS NOT NULL", Predicate(x => x.RevokedAt.HasValue));
        Assert.AreEqual("{RevokedAt} IS NULL", Predicate(x => !x.RevokedAt.HasValue));
        Assert.AreEqual("({Count} > 5 AND {RevokedAt} IS NULL)", Predicate(x => x.Count.Value > 5 && !x.RevokedAt.HasValue));
        Assert.AreEqual("{Flag}", Predicate(x => x.Flag.Value));
        Assert.AreEqual("({Count} IS NOT NULL)", NpgsqlLinqIndexTranslator.Translate((Expression<Func<Ticket, bool>>)(x => x.Count.HasValue)));
    }

    [TestMethod(DisplayName = "Any other member of a scalar type is refused at the declaration")]
    public void Scalar_member_is_refused()
    {
        var exception = Assert.ThrowsExactly<NotSupportedException>(() => Predicate(x => x.CreatedAt.Year > 2020));
        StringAssert.Contains(exception.Message, "Year");

        Assert.ThrowsExactly<NotSupportedException>(() => Predicate(x => x.RevokedAt.Value.Month == 1));
        Assert.ThrowsExactly<NotSupportedException>(
            () => NpgsqlLinqIndexTranslator.Translate((Expression<Func<Ticket, int>>)(x => x.CreatedAt.Day)));
    }
#pragma warning restore CS8629

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
