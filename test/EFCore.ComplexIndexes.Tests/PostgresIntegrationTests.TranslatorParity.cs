using EFCore.ComplexIndexes.PostgreSQL;
using EFCore.ComplexIndexes.Tests.TranslatorParity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EFCore.ComplexIndexes.Tests;

public partial class PostgresIntegrationTests
{
    // ── Translator parity on a live server (#101) ──

    /// <summary>
    /// The unit sweep compares renderings; this runs them. A pair the canonical form calls equal,
    /// or a waiver calls equivalent, must select the same rows (filters) and compute the same values
    /// (expressions) as EF Core's SQL on PostgreSQL 18, over rows where every nullable operand is
    /// both null and set. An expression a waiver calls equivalent must also serve EF Core's query
    /// from an index built on the package's SQL, which is the point of rendering it alike.
    /// </summary>
    [TestMethod(DisplayName = "Typed filters and expressions answer on PostgreSQL as EF Core's queries do")]
    public void Translator_parity_answers_alike_on_postgres()
    {
        var connection = ConnectionString;
        var results    = ParitySweep.Run(connection);
        var assessment = ParityAssessment.Of(results, ParityWaivers.Load());

        Migrate<ParityQueryContext>();
        using (var context = new ParityQueryContext(new DbContextOptionsBuilder<ParityQueryContext>().UseNpgsql(connection).Options))
        {
            context.Rows.AddRange(ParitySeed.Rows());
            context.SaveChanges();
        }

        var claimed = results.Where(r => r.Package is not null && r.EfCore is not null)
                             .Where(r => r.Outcome == ParityOutcome.Agree
                                      || assessment.Waived.TryGetValue(r, out var waiver) && waiver.Kind is WaiverKind.Rule or WaiverKind.Same)
                             .ToList();

        using var database = new NpgsqlConnection(connection);
        database.Open();

        var failures = new List<string>();
        foreach (var result in claimed)
        {
            var (package, efCore) = result.Case.Kind == ParityKind.Filter
                                        ? ($"SELECT \"Id\"::text FROM {ParityModel.Table} WHERE {result.Package} ORDER BY 1",
                                           $"SELECT p.\"Id\"::text FROM {ParityModel.Table} AS p WHERE {result.EfCore} ORDER BY 1")
                                        : ($"SELECT \"Id\" || ': ' || coalesce(({result.Package})::text, 'NULL') FROM {ParityModel.Table} ORDER BY \"Id\"",
                                           $"SELECT p.\"Id\" || ': ' || coalesce(({result.EfCore})::text, 'NULL') FROM {ParityModel.Table} AS p ORDER BY p.\"Id\"");

            try
            {
                var packageAnswer = Answer(database, package);
                var efCoreAnswer  = Answer(database, efCore);
                if (packageAnswer != efCoreAnswer)
                    failures.Add($"{result}\n    answers: package [{packageAnswer}], EF Core [{efCoreAnswer}]");
            }
            catch (PostgresException e)
            {
                failures.Add($"{result}\n    fails on PostgreSQL: {e.MessageText}");
            }
        }

        // Rendered differently and called equivalent: PostgreSQL must match the index to the query.
        foreach (var result in claimed.Where(r => r.Case.Kind == ParityKind.Expression && r.Outcome == ParityOutcome.Differ))
        {
            try
            {
                Sql($"CREATE INDEX parity_probe ON {ParityModel.Table} (({result.Package}))");
                var plan = Answer(database, $"SET enable_seqscan = off; EXPLAIN SELECT p.\"Id\" FROM {ParityModel.Table} AS p WHERE ({result.EfCore}) IS NULL");
                if (!plan.Contains("parity_probe", StringComparison.Ordinal))
                    failures.Add($"{result}\n    EF Core's query does not use an index on the package's expression:\n{plan}");
            }
            catch (PostgresException e)
            {
                failures.Add($"{result}\n    index fails on PostgreSQL: {e.MessageText}");
            }
            finally
            {
                Sql("DROP INDEX IF EXISTS parity_probe");
            }
        }

        Assert.IsEmpty(failures, $"{failures.Count} of {claimed.Count} rows answer differently on PostgreSQL:\n\n" + string.Join("\n\n", failures));
    }

    // ── Typed concatenation over a nullable operand (#94) ──

    private class Badge
    {
        public int     Id     { get; set; }
        public string  Code   { get; set; } = "";
        public string? Suffix { get; set; }
    }

    private class BadgeContext(DbContextOptions<BadgeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Badge>(b =>
            {
                b.ToTable("ig_badges");
                b.HasKey(x => x.Id);
                b.HasExpressionIndex(x => x.Code + x.Suffix, isUnique: true, indexName: "ux_ig_badges_label");
            });
    }

    [TestMethod(DisplayName = "A unique typed concatenation over a nullable operand refuses duplicates where it is null")]
    public void Typed_concatenation_coalesces_a_nullable_operand()
    {
        Migrate<BadgeContext>();

        // "Code" || "Suffix" was NULL for a null Suffix, and a unique index ignores NULL keys, so
        // both rows went in although Code + Suffix is "A" for each.
        Sql("INSERT INTO ig_badges (\"Id\", \"Code\") VALUES (1, 'A')");
        AssertRejected("INSERT INTO ig_badges (\"Id\", \"Code\") VALUES (2, 'A')");
    }

    private static string Answer(NpgsqlConnection connection, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection);
        using var reader  = command.ExecuteReader();
        var rows = new List<string>();
        do
        {
            while (reader.Read())
                rows.Add(reader.GetString(0));
        } while (reader.NextResult());

        return string.Join(" | ", rows);
    }
}
