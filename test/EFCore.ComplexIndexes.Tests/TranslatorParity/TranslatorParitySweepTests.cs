namespace EFCore.ComplexIndexes.Tests.TranslatorParity;

/// <summary>
/// The differential sweep of typed filters and typed expression indexes against EF Core's query
/// translation of the same lambda (#101). PostgreSQL uses an expression index only for a query
/// whose expression matches it, and a filter with other null semantics indexes other rows, so the
/// package must render each lambda as <c>ToQueryString()</c> does: <c>Where(…)</c> for filters,
/// <c>Select(…)</c> for expressions. Each divergence before this sweep (#68, #70, #72, #94) was found
/// by hand, one lambda at a time.
/// </summary>
/// <remarks>
/// The catalogue (<see cref="ParityCatalogue"/>) is read from the translator's own tables, crossed
/// with every operand shape <see cref="ParityRow"/> has, so a construct added to the translator is
/// swept with no edit here. A disagreement is fixed or listed in <c>waivers.txt</c> with its reason.
/// <c>PostgresIntegrationTests.Translator_parity_answers_alike_on_postgres</c> runs the same rows
/// against PostgreSQL 18, which catches a pair that renders alike and answers differently.
/// </remarks>
[TestClass]
public class TranslatorParitySweepTests
{
    private static readonly Lazy<ParityAssessment> Assessment = new(() => ParityAssessment.Of(ParitySweep.Results, ParityWaivers.Load()));

    [TestMethod(DisplayName = "Every typed filter and expression renders as EF Core's query, or is refused at the declaration, or is waived")]
    public void Every_disagreement_is_fixed_or_waived()
    {
        var unwaived = Assessment.Value.Unwaived;

        Assert.IsEmpty(unwaived,
            $"{unwaived.Count} rows of the parity sweep disagree with EF Core and are not in waivers.txt. Fix the " +
            "translator, or list each row under a block that gives the reason:\n\n" +
            string.Join("\n", unwaived.GroupBy(r => r.Outcome).Select(g => $"# {g.Key}\n" + string.Join("\n", g.Select(r => r.ToString())))));
    }

    [TestMethod(DisplayName = "No waiver outlives the disagreement it waives")]
    public void No_waiver_is_stale()
    {
        var stale = Assessment.Value.Stale;

        Assert.IsEmpty(stale, "waivers.txt lists what the sweep no longer finds; remove it:\n" + string.Join("\n", stale));
    }

    [TestMethod(DisplayName = "Every construct the translator accepts is compared with EF Core at least once")]
    public void Every_accepted_construct_is_swept()
    {
        var compared = ParitySweep.Results.Where(r => r.Outcome is ParityOutcome.Agree or ParityOutcome.Differ)
                                          .SelectMany(r => r.Case.Constructs)
                                          .ToHashSet();

        var missing = ParityCatalogue.AcceptedConstructs().Where(c => !compared.Contains(c)).ToList();

        Assert.IsEmpty(missing, "The translator accepts constructs no row compared with EF Core:\n" + string.Join("\n", missing));
    }

    [TestMethod(DisplayName = "Every operand shape reaches a comparison with EF Core")]
    public void Every_operand_shape_is_swept()
    {
        var compared = ParitySweep.Results.Where(r => r.Outcome is ParityOutcome.Agree or ParityOutcome.Differ)
                                          .SelectMany(r => r.Case.Constructs)
                                          .Where(c => c.StartsWith("shape:", StringComparison.Ordinal))
                                          .ToHashSet();

        CollectionAssert.IsSubsetOf(new[] { "shape:column", "shape:nullable", "shape:json", "shape:json nullable" }, compared.ToList());
    }

    // ── The canonical form the comparison rests on ──

    [TestMethod(DisplayName = "The canonical form keeps what PostgreSQL's parser keeps")]
    public void Canonical_form_keeps_the_tree()
    {
        // Aliases, parentheses, cast spellings and literal spellings are not in the tree.
        Assert.AreEqual(PostgresCanonicalForm.Of("lower(p.\"Name\") = 'a'"), PostgresCanonicalForm.Of("(lower(\"Name\")) = E'a'"));
        Assert.AreEqual(PostgresCanonicalForm.Of("CAST(\"Doc\" ->> 'Rank' AS int4) > 5"), PostgresCanonicalForm.Of("(\"Doc\" ->> 'Rank')::integer > 5"));
        Assert.AreEqual(PostgresCanonicalForm.Of("\"A\" || 'x'::text"), PostgresCanonicalForm.Of("\"A\" || 'x'"));

        // Precedence is: the same tokens grouped differently are different trees.
        Assert.AreNotEqual(PostgresCanonicalForm.Of("(\"A\" OR \"B\") AND \"C\""), PostgresCanonicalForm.Of("\"A\" OR \"B\" AND \"C\""));
        Assert.AreNotEqual(PostgresCanonicalForm.Of("(\"A\" || \"B\") || \"C\""), PostgresCanonicalForm.Of("\"A\" || (\"B\" || \"C\")"));
        Assert.AreEqual(PostgresCanonicalForm.Of("\"A\" ->> 'x' = 'y'"), PostgresCanonicalForm.Of("(\"A\" ->> 'x') = 'y'"));

        // A cast of a value is a node; 5 and 5::text are different constants.
        Assert.AreNotEqual(PostgresCanonicalForm.Of("\"A\" || 5"), PostgresCanonicalForm.Of("\"A\" || 5::text"));
    }

    [TestMethod(DisplayName = "The planner's rewrites apply only when switched on")]
    public void Rules_apply_only_when_switched_on()
    {
        HashSet<string> rules = ["negator", "boolean-equality", "integer-length"];

        Assert.AreNotEqual(PostgresCanonicalForm.Of("NOT (\"A\" < 5)"), PostgresCanonicalForm.Of("\"A\" >= 5"));
        Assert.AreEqual(PostgresCanonicalForm.Of("NOT (\"A\" < 5)", rules), PostgresCanonicalForm.Of("\"A\" >= 5", rules));
        Assert.AreEqual(PostgresCanonicalForm.Of("NOT (\"A\" = 1 OR \"B\" IS NULL)", rules), PostgresCanonicalForm.Of("\"A\" <> 1 AND \"B\" IS NOT NULL", rules));
        Assert.AreEqual(PostgresCanonicalForm.Of("\"F\" <> TRUE", rules), PostgresCanonicalForm.Of("NOT (\"F\")", rules));
        Assert.AreEqual(PostgresCanonicalForm.Of("length(\"A\")", rules), PostgresCanonicalForm.Of("length(\"A\")::int", rules));
        Assert.AreEqual(PostgresCanonicalForm.Of("CASE WHEN length(\"A\") < 5 THEN FALSE ELSE TRUE END", rules),
                        PostgresCanonicalForm.Of("CASE WHEN length(\"A\")::int < 5 THEN FALSE ELSE TRUE END", rules));

        // NULL compensation is not a rewrite PostgreSQL makes.
        Assert.AreNotEqual(PostgresCanonicalForm.Of("\"A\" <> 1 OR \"A\" IS NULL", rules), PostgresCanonicalForm.Of("\"A\" <> 1", rules));
    }
}
