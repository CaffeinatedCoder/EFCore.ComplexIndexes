using System.Globalization;
using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Typed LINQ expression indexes: the fluent call translates the lambda into a SQL template with
/// <c>{Property.Path}</c> placeholders; the Npgsql differ resolves the placeholders against the
/// finalized model — to quoted columns, or JSON extractions for <c>ToJson()</c> members.
/// </summary>
[TestClass]
public class NpgsqlLinqExpressionIndexTests
{
    // ── Harness ──

    private static IRelationalModel BuildRelationalModel<TContext>() where TContext : DbContext
        => MigrationHarness.NpgsqlModel<TContext>();

    private static IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
        => MigrationHarness.NpgsqlDiff(source, target);

    private static string SingleExpression(IReadOnlyList<MigrationOperation> operations)
    {
        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        var json        = createIndex.FindAnnotation(ComplexIndexAnnotations.IndexParts)?.Value as string;
        Assert.IsNotNull(json);
        var part = Assert.ContainsSingle(IndexPartsSerializer.Deserialize(json!));
        Assert.IsTrue(part.IsExpression);
        return part.Value;
    }

    private class EmptyContext(DbContextOptions options) : DbContext(options);

    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class CompanyName
    {
        public string ShortName { get; set; } = "";
    }

    private class Person
    {
        public Guid         Id       { get; set; }
        public string       First    { get; set; } = "";
        public string       Last     { get; set; } = "";
        public string?      Nickname { get; set; }
        public string       Code     { get; set; } = "";
        public EmailAddress Email    { get; set; } = new();
        public CompanyName  Employer { get; set; } = new();
    }

    private class Context<TSelf>(DbContextOptions options) : DbContext(options) where TSelf : Context<TSelf>
    {
        public DbSet<Person> People => Set<Person>();
    }

    private static void MapPerson(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Person> b)
    {
        b.ToTable("people");
        b.HasKey(x => x.Id);
        b.Property(x => x.First).HasColumnName("first");
        b.Property(x => x.Last).HasColumnName("last");
        b.Property(x => x.Nickname).HasColumnName("nickname");
        b.Property(x => x.Code).HasColumnName("code");
        b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasColumnName("email"));
        b.ComplexProperty(x => x.Employer, c => c.ToJson("employer"));
    }

    private class LowerComplexMemberContext(DbContextOptions<LowerComplexMemberContext> options)
        : Context<LowerComplexMemberContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasExpressionIndex(x => x.Email.Value.ToLower(), isUnique: true, indexName: "ux_people_email_ci");
            });
    }

    private class ConcatAndCoalesceContext(DbContextOptions<ConcatAndCoalesceContext> options)
        : Context<ConcatAndCoalesceContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasExpressionIndex(x => (x.Nickname ?? x.First) + " " + x.Last, indexName: "ix_people_display");
            });
    }

    private class SubstringLengthContext(DbContextOptions<SubstringLengthContext> options)
        : Context<SubstringLengthContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasExpressionIndex(x => x.Code.Substring(0, 3).ToUpper(), indexName: "ix_people_code_prefix");
            });
    }

    private class JsonMemberLinqContext(DbContextOptions<JsonMemberLinqContext> options)
        : Context<JsonMemberLinqContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasExpressionIndex(x => x.Employer.ShortName.ToLower(), indexName: "ix_people_employer_ci");
            });
    }

    // ── Differ-level tests ──

    [TestMethod(DisplayName = "ToLower on a complex member resolves to lower(column)")]
    public void Lower_on_complex_member()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<LowerComplexMemberContext>());

        Assert.AreEqual("lower(\"email\")", SingleExpression(operations));
        Assert.IsTrue(Assert.ContainsSingle(operations.OfType<CreateIndexOperation>()).IsUnique);
    }

    [TestMethod(DisplayName = "Coalesce and concatenation translate to coalesce() and ||")]
    public void Coalesce_and_concat()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<ConcatAndCoalesceContext>());

        Assert.AreEqual("((coalesce(\"nickname\", \"first\") || ' ') || \"last\")", SingleExpression(operations));
    }

    [TestMethod(DisplayName = "Substring is 1-based and composes with ToUpper")]
    public void Substring_composes()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<SubstringLengthContext>());

        Assert.AreEqual("upper(substring(\"code\", 1, 3))", SingleExpression(operations));
    }

    [TestMethod(DisplayName = "Typed expressions compose with ToJson members")]
    public void Json_member_in_typed_expression()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<JsonMemberLinqContext>());

        Assert.AreEqual("lower((\"employer\" ->> 'ShortName'))", SingleExpression(operations));
    }

    [TestMethod(DisplayName = "Unchanged typed expression index produces no operations")]
    public void Unchanged_typed_index_is_noop()
    {
        var operations = GetDifferences(
            BuildRelationalModel<LowerComplexMemberContext>(),
            BuildRelationalModel<LowerComplexMemberContext>());

        Assert.IsEmpty(operations.OfType<CreateIndexOperation>());
        Assert.IsEmpty(operations.OfType<DropIndexOperation>());
    }

    // ── Agreement with EF Core's query translation ──

    private class Code
    {
        public int    Id    { get; set; }
        public string Value { get; set; } = "";
    }

    // PostgreSQL uses an expression index only for a query whose expression matches it, so each
    // typed expression must render exactly as Npgsql renders the same lambda in a query.
    // substr(…) and btrim(x) applied cleanly and served no query: substring(…) and
    // btrim(x, E' \t\n\r') are what EF Core sends.
    private static readonly Dictionary<string, System.Linq.Expressions.Expression<Func<Code, string>>> QueryShapedExpressions = new()
    {
        ["ix_substring_length"] = x => x.Value.Substring(1, 3),
        ["ix_substring"]        = x => x.Value.Substring(1),
        ["ix_trim"]             = x => x.Value.Trim(),
        ["ix_trim_start"]       = x => x.Value.TrimStart(),
        ["ix_trim_end"]         = x => x.Value.TrimEnd(),
        ["ix_lower"]            = x => x.Value.ToLower(),
        ["ix_replace"]          = x => x.Value.Replace("-", ""),
    };

    private class QueryShapedContext(DbContextOptions<QueryShapedContext> options) : DbContext(options)
    {
        public DbSet<Code> Codes => Set<Code>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Code>(b =>
            {
                b.ToTable("codes");
                foreach (var (name, expression) in QueryShapedExpressions)
                    b.HasExpressionIndex(expression, indexName: name);
            });
    }

    [TestMethod(DisplayName = "Typed expressions render as EF Core's queries render them")]
    public void Typed_expressions_match_ef_core_queries()
    {
        var expressions = GetDifferences(source: null, target: BuildRelationalModel<QueryShapedContext>())
                         .OfType<CreateIndexOperation>()
                         .ToDictionary(
                              o => o.Name,
                              o => IndexPartsSerializer.Deserialize((string)o[ComplexIndexAnnotations.IndexParts]!).Single().Value);

        using var context = new QueryShapedContext(new DbContextOptionsBuilder<QueryShapedContext>()
                                                  .UseNpgsql(MigrationHarness.NpgsqlConnection).Options);

        foreach (var (name, expression) in QueryShapedExpressions)
        {
            var select = context.Codes.Select(expression).ToQueryString().Split('\n')[0];
            Assert.AreEqual(select["SELECT ".Length..].Replace("c.", ""), expressions[name], name);
        }
    }

    // ── Translator-level tests ──

    private static string Translate<TResult>(System.Linq.Expressions.Expression<Func<Person, TResult>> expression)
        => NpgsqlLinqIndexTranslator.Translate(expression);

    [TestMethod(DisplayName = "Property paths become placeholders")]
    public void Paths_become_placeholders()
    {
        Assert.AreEqual("{Email.Value}",        Translate(x => x.Email.Value));
        Assert.AreEqual("lower({Email.Value})", Translate(x => x.Email.Value.ToLower()));
        Assert.AreEqual("length({Last})",       Translate(x => x.Last.Length));
        Assert.AreEqual("btrim({First}, E' \\t\\n\\r')", Translate(x => x.First.Trim()));
        Assert.AreEqual("replace({Code}, '-', '')", Translate(x => x.Code.Replace("-", "")));
    }

    [TestMethod(DisplayName = "Captured variables are evaluated and inlined invariantly")]
    public void Captured_variables_are_inlined_invariantly()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // A de-DE thread culture must not turn 1.5 into '1,5' in SQL.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var prefixLength = 3;
            Assert.AreEqual("substring({Code}, 1, 3)", Translate(x => x.Code.Substring(0, prefixLength)));

            var factor = 1.5;
            Assert.AreEqual("({Last} || '1.5')", Translate(x => x.Last + factor.ToString(CultureInfo.InvariantCulture)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestMethod(DisplayName = "Literal braces in string constants are escaped and unescaped")]
    public void Braces_in_literals_are_escaped()
    {
        Assert.AreEqual("({Last} || '{{tag}}')", Translate(x => x.Last + "{tag}"));

        // End to end: the differ must render the literal brace, not treat it as a placeholder.
        var operations = GetDifferences(source: null, target: BuildRelationalModel<BraceLiteralContext>());
        Assert.AreEqual("(\"last\" || '{tag}')", SingleExpression(operations));
    }

    private class BraceLiteralContext(DbContextOptions<BraceLiteralContext> options)
        : Context<BraceLiteralContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasExpressionIndex(x => x.Last + "{tag}", indexName: "ix_people_brace");
            });
    }

    [TestMethod(DisplayName = "A nullable operand of a concatenation is coalesced to an empty string")]
    public void Concatenation_coalesces_nullable_operands()
    {
        // C# concatenates null as "", PostgreSQL's || returns NULL: EF Core writes COALESCE(…, '').
        Assert.AreEqual("({Code} || coalesce({Nickname}, ''))", Translate(x => x.Code + x.Nickname));
        Assert.AreEqual("((coalesce({Nickname}, '') || ' ') || {Last})", Translate(x => x.Nickname + " " + x.Last));

        string? none = null;
        Assert.AreEqual("({Code} || '')", Translate(x => x.Code + none));

        // Non-nullable operands and a coalesce with a non-null fallback stay as they were.
        Assert.AreEqual("({Code} || {Last})", Translate(x => x.Code + x.Last));
        Assert.AreEqual("(coalesce({Nickname}, {First}) || {Last})", Translate(x => (x.Nickname ?? x.First) + x.Last));
    }

    // The template is stored at the declaration, so a model declared before 5.5.0 keeps the old one
    // in its snapshot and the next migrations add re-creates the index once; no rendering version.
    private class ConcatBeforeContext(DbContextOptions<ConcatBeforeContext> options) : Context<ConcatBeforeContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                ComplexIndexStorage.AddOrReplace(b, new CompositeIndexDefinition
                {
                    Parts     = [new IndexPartDefinition { Template = "({Code} || {Nickname})" }],
                    IndexName = "ix_people_label",
                });
            });
    }

    private class ConcatAfterContext(DbContextOptions<ConcatAfterContext> options) : Context<ConcatAfterContext>(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasExpressionIndex(x => x.Code + x.Nickname, indexName: "ix_people_label");
            });
    }

    [TestMethod(DisplayName = "An index over a nullable concatenation declared before 5.5.0 is re-created once")]
    public void Nullable_concatenation_recreates_once()
    {
        var operations = GetDifferences(BuildRelationalModel<ConcatBeforeContext>(), BuildRelationalModel<ConcatAfterContext>());

        Assert.AreEqual("ix_people_label", Assert.ContainsSingle(operations.OfType<DropIndexOperation>()).Name);
        Assert.AreEqual("(\"code\" || coalesce(\"nickname\", ''))", SingleExpression(operations));
        Assert.IsEmpty(GetDifferences(BuildRelationalModel<ConcatAfterContext>(), BuildRelationalModel<ConcatAfterContext>()));
    }

    [TestMethod(DisplayName = "Unsupported constructs throw NotSupportedException at declaration time")]
    public void Unsupported_constructs_throw()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => Translate(x => x.Id.GetHashCode()));
        Assert.ThrowsExactly<NotSupportedException>(() => Translate(x => x.First.PadLeft(4)));
    }
}
