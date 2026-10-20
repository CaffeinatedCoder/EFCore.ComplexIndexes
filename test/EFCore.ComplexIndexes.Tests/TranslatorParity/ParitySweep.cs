#pragma warning disable EF1001
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;

namespace EFCore.ComplexIndexes.Tests.TranslatorParity;

internal enum ParityOutcome
{
    /// <summary>Both render, and PostgreSQL builds the same tree from both.</summary>
    Agree,

    /// <summary>The translator refuses the lambda at the declaration. Always allowed.</summary>
    RefusedAtDeclaration,

    /// <summary>Both render, differently.</summary>
    Differ,

    /// <summary>The package renders a lambda EF Core cannot translate on the server.</summary>
    PackageOnly,

    /// <summary>Accepted at the declaration, then fails at <c>migrations add</c>: never allowed unwaived.</summary>
    FailedAtMigrationsAdd,

    /// <summary>The declaration threw something other than <see cref="NotSupportedException"/>.</summary>
    CrashedAtDeclaration,
}

internal sealed record ParityResult(ParityCase Case, LambdaExpression? Lambda, ParityOutcome Outcome, string? Package, string? EfCore, string? Detail)
{
    public bool NeedsWaiver => Outcome is not (ParityOutcome.Agree or ParityOutcome.RefusedAtDeclaration);

    public override string ToString()
        => $"{Outcome}: {Case.Id}\n    package: {Package ?? "-"}\n    EF Core: {EfCore ?? "-"}{(Detail is null ? "" : "\n    " + Detail)}";
}

/// <summary>
/// Runs the catalogue: compiles each row with Roslyn, renders it through the package's declaration
/// and differ and through EF Core's query translation, and classifies the pair.
/// </summary>
internal static class ParitySweep
{
    private static readonly Lazy<IReadOnlyList<ParityResult>> Cached = new(() => Run(MigrationHarness.NpgsqlConnection));

    /// <summary>The sweep against the offline model, computed once per test run.</summary>
    public static IReadOnlyList<ParityResult> Results => Cached.Value;

    public static IReadOnlyList<ParityResult> Run(string connection)
    {
        var cases   = ParityCatalogue.Build();
        var lambdas = Compile(cases);

        // ── The package's side: the declaration, then the differ ──

        var templates = new Dictionary<int, string>();
        var refused   = new Dictionary<int, (ParityOutcome, string)>();

        for (var i = 0; i < cases.Count; i++)
        {
            if (lambdas[i] is not { } lambda)
                continue;

            try
            {
                templates[i] = cases[i].Kind == ParityKind.Filter
                                   ? NpgsqlLinqIndexTranslator.TranslatePredicate(lambda)
                                   : NpgsqlLinqIndexTranslator.Translate(lambda);
            }
            catch (NotSupportedException e)
            {
                refused[i] = (ParityOutcome.RefusedAtDeclaration, e.Message);
            }
            catch (Exception e)
            {
                refused[i] = (ParityOutcome.CrashedAtDeclaration, $"{e.GetType().Name}: {e.Message}");
            }
        }

        // One index per distinct template: two rows that translate alike would otherwise be one
        // declaration replacing the other.
        var declarations = templates.GroupBy(t => (cases[t.Key].Kind, t.Value))
                                    .Select((group, n) => new Declaration($"p{n}", cases[group.First().Key].Kind, lambdas[group.First().Key]!,
                                                                          group.Select(g => g.Key).ToList()))
                                    .ToList();

        var rendered = new Dictionary<int, string>();
        var failed   = new Dictionary<int, string>();
        Render(Screen(declarations, connection, failed), connection, rendered, failed);

        // ── EF Core's side: the same lambda in a query ──

        // Query translation is the slow half and each row is independent; one context per thread.
        var options = new DbContextOptionsBuilder<ParityQueryContext>().UseNpgsql(connection).Options;
        using var contexts = new ThreadLocal<ParityQueryContext>(() => new ParityQueryContext(options), trackAllValues: true);

        var results = new ParityResult?[cases.Count];
        Parallel.For(0, cases.Count, i =>
        {
            var @case  = cases[i];
            var lambda = lambdas[i];

            if (lambda is null)
                return;

            if (refused.TryGetValue(i, out var refusal))
            {
                results[i] = new ParityResult(@case, lambda, refusal.Item1, null, null, refusal.Item2);
                return;
            }

            if (failed.TryGetValue(i, out var failure))
            {
                results[i] = new ParityResult(@case, lambda, ParityOutcome.FailedAtMigrationsAdd, templates[i], null, failure);
                return;
            }

            var package = rendered[i];
            var efCore  = EfCoreSql(contexts.Value!, @case.Kind, lambda);

            if (efCore is null)
            {
                results[i] = new ParityResult(@case, lambda, ParityOutcome.PackageOnly, package, null, null);
                return;
            }

            string? detail = null;
            bool    agree;
            try
            {
                agree = PostgresCanonicalForm.Of(package) == PostgresCanonicalForm.Of(efCore);
            }
            catch (FormatException e)
            {
                agree  = false;
                detail = "could not parse: " + e.Message;
            }

            results[i] = new ParityResult(@case, lambda, agree ? ParityOutcome.Agree : ParityOutcome.Differ, package, efCore, detail);
        });

        foreach (var context in contexts.Values)
            context.Dispose();

        return results.OfType<ParityResult>().ToList();
    }

    // ── Compilation ──

    // Each row is one method on one line, so a compiler error names its row. Rows that are not C#
    // (x.Bool < true) are dropped; the coverage check guards against dropping a whole construct.
    private static LambdaExpression?[] Compile(IReadOnlyList<ParityCase> cases)
    {
        var excluded = new HashSet<int>();

        while (true)
        {
            var source = new StringBuilder();
            source.AppendLine("#nullable enable");
            source.AppendLine("using System; using System.Linq.Expressions; using EFCore.ComplexIndexes.Tests.TranslatorParity;");
            source.AppendLine("public static class ParityRows {");
            source.AppendLine("static LambdaExpression E<T>(Expression<Func<ParityRow, T>> e) => e;");
            source.AppendLine("static LambdaExpression P(Expression<Func<ParityRow, bool>> p) => p;");
            const int firstRowLine = 5;

            for (var i = 0; i < cases.Count; i++)
            {
                var helper = cases[i].Kind == ParityKind.Filter ? "P" : "E";
                source.AppendLine(excluded.Contains(i)
                                      ? "//"
                                      : $"public static LambdaExpression R{i}() {{ {cases[i].Captures} return {helper}({cases[i].Lambda}); }}");
            }

            source.AppendLine("}");

            var compilation = CSharpCompilation.Create(
                "TranslatorParity.Generated",
                [CSharpSyntaxTree.ParseText(source.ToString())],
                References.Value,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using var stream = new MemoryStream();
            var       result = compilation.Emit(stream);

            if (!result.Success)
            {
                var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
                var rows   = errors.Select(d => d.Location.GetLineSpan().StartLinePosition.Line - firstRowLine).ToHashSet();

                if (rows.Any(r => r < 0 || r >= cases.Count || excluded.Contains(r)))
                    throw new InvalidOperationException("The sweep's generated source does not compile:\n" + string.Join("\n", errors));

                excluded.UnionWith(rows);
                continue;
            }

            var type = Assembly.Load(stream.ToArray()).GetType("ParityRows")!;
            return cases.Select((_, i) => excluded.Contains(i)
                                              ? null
                                              : (LambdaExpression)type.GetMethod($"R{i}")!.Invoke(null, null)!)
                        .ToArray();
        }
    }

    private static readonly Lazy<List<MetadataReference>> References = new(
        () => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
             .Split(Path.PathSeparator)
             .Append(typeof(ParityRow).Assembly.Location)
             .Distinct()
             .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
             .ToList());

    // ── The package's rendering ──

    private sealed record Declaration(string Name, ParityKind Kind, LambdaExpression Lambda, IReadOnlyList<int> Rows)
    {
        public void Declare(EntityTypeBuilder<ParityRow> b)
        {
            if (Kind == ParityKind.Filter)
                b.HasComplexIndex(x => x.Id, (Expression<Func<ParityRow, bool>>)Lambda, indexName: Name);
            else
                // Unique, so the differ's guard against non-unique indexes on a date member's text
                // fallback stays out of a sweep about rendering.
                HasExpressionIndex.MakeGenericMethod(typeof(ParityRow), Lambda.ReturnType)
                                  .Invoke(null, [b, Lambda, true, null, Name]);
        }
    }

    private static readonly MethodInfo HasExpressionIndex = typeof(NpgsqlExpressionIndexExtensions)
                                                           .GetMethods()
                                                           .Single(m => m.Name == nameof(NpgsqlExpressionIndexExtensions.HasExpressionIndex)
                                                                     && m.GetGenericArguments().Length == 2);

    // Resolves each template against the plain model first, the way the differ does, so that a
    // template that fails at `migrations add` is found without diffing one model per declaration.
    private static IReadOnlyList<Declaration> Screen(IReadOnlyList<Declaration> declarations, string connection, Dictionary<int, string> failed)
    {
        using var context = new ParityQueryContext(new DbContextOptionsBuilder<ParityQueryContext>().UseNpgsql(connection).Options);
        var model      = context.GetService<IDesignTimeModel>().Model;
        var entityType = model.FindEntityType(typeof(ParityRow))!;
        var table      = StoreObjectIdentifier.Table(ParityModel.Table);
        var differ     = MigrationHarness.CreateDiffer<ScreeningDiffer>(context);

        var passed = new List<Declaration>();
        foreach (var declaration in declarations)
        {
            try
            {
                var template = declaration.Kind == ParityKind.Filter
                                   ? NpgsqlLinqIndexTranslator.TranslatePredicate(declaration.Lambda)
                                   : NpgsqlLinqIndexTranslator.Translate(declaration.Lambda);
                differ.Resolve(entityType, declaration.Kind, template, table);
                passed.Add(declaration);
            }
            catch (InvalidOperationException e)
            {
                foreach (var row in declaration.Rows)
                    failed[row] = $"{e.GetType().Name}: {e.Message}";
            }
        }

        return passed;
    }

    private sealed class ScreeningDiffer(
        IRelationalTypeMappingSource                                     typeMappingSource,
        IMigrationsAnnotationProvider                                    migrationsAnnotationProvider,
        IRelationalAnnotationProvider                                    relationalAnnotationProvider,
        Microsoft.EntityFrameworkCore.Update.Internal.IRowIdentityMapFactory rowIdentityMapFactory,
        Microsoft.EntityFrameworkCore.Update.Internal.CommandBatchPreparerDependencies commandBatchPreparerDependencies
    ) : NpgsqlComplexIndexMigrationsModelDiffer(typeMappingSource, migrationsAnnotationProvider, relationalAnnotationProvider,
                                                rowIdentityMapFactory, commandBatchPreparerDependencies)
    {
        public void Resolve(IEntityType entityType, ParityKind kind, string template, StoreObjectIdentifier table)
        {
            if (kind == ParityKind.Filter)
                ResolveFilter(entityType, template, table);
            else
                ResolveTemplatePart(entityType, new IndexPartDefinition { Template = template }, table);
        }
    }

    // Diffs all screened declarations at once. Should the full diff still throw where the screen
    // did not, halves the set until each failing declaration is found, so one bad row cannot hide
    // the rest.
    private static void Render(IReadOnlyList<Declaration> declarations, string connection,
                               Dictionary<int, string> rendered, Dictionary<int, string> failed)
    {
        if (declarations.Count == 0)
            return;

        IReadOnlyList<MigrationOperation> operations;
        try
        {
            var options = new DbContextOptionsBuilder<ParityIndexContext>().UseNpgsql(connection)
                                                                          .EnableServiceProviderCaching(false)
                                                                          .Options;
            using var context = new ParityIndexContext(options, declarations.Select(d => (Action<EntityTypeBuilder<ParityRow>>)d.Declare).ToList());
            operations = MigrationHarness.NpgsqlDiff(null, context.GetService<IDesignTimeModel>().Model.GetRelationalModel(), connection);
        }
        catch (Exception) when (declarations.Count > 1)
        {
            var half = declarations.Count / 2;
            Render(declarations.Take(half).ToList(), connection, rendered, failed);
            Render(declarations.Skip(half).ToList(), connection, rendered, failed);
            return;
        }
        catch (Exception e)
        {
            var message = $"{e.GetType().Name}: {(e is TargetInvocationException { InnerException: { } inner } ? inner.Message : e.Message)}";
            foreach (var row in declarations[0].Rows)
                failed[row] = message;
            return;
        }

        var indexes = operations.OfType<CreateIndexOperation>().ToDictionary(o => o.Name);
        foreach (var declaration in declarations)
        {
            var index = indexes[declaration.Name];
            var sql   = declaration.Kind == ParityKind.Filter ? index.Filter! : ExpressionOf(index);
            foreach (var row in declaration.Rows)
                rendered[row] = sql;
        }
    }

    private static string ExpressionOf(CreateIndexOperation index)
        => index.FindAnnotation(ComplexIndexAnnotations.IndexParts)?.Value is string json
               ? IndexPartsSerializer.Deserialize(json).Single() is var part && part.IsExpression ? part.Value : $"\"{part.Value}\""
               : $"\"{index.Columns.Single()}\"";

    // ── EF Core's rendering ──

    // Captured values are inlined first, as constants: the package inlines them, and EF Core would
    // otherwise render a parameter.
    private static string? EfCoreSql(ParityQueryContext context, ParityKind kind, LambdaExpression lambda)
    {
        lambda = (LambdaExpression)new CaptureInliner().Visit(lambda);

        try
        {
            if (kind == ParityKind.Filter)
            {
                var sql   = context.Rows.Where((Expression<Func<ParityRow, bool>>)lambda).ToQueryString();
                var where = sql.IndexOf("\nWHERE ", StringComparison.Ordinal);
                return where < 0 ? "TRUE" : sql[(where + "\nWHERE ".Length)..];
            }

            // Select evaluates an untranslatable top-level projection on the client and still shows
            // a query; ORDER BY has no client fallback, so it says whether EF Core translates it.
            var key = Expression.Lambda(lambda.Body, lambda.Parameters);
            ((IQueryable)context.Rows).Provider.CreateQuery<ParityRow>(
                       Expression.Call(typeof(Queryable), nameof(Queryable.OrderBy), [typeof(ParityRow), lambda.ReturnType],
                                       context.Rows.AsQueryable().Expression, Expression.Quote(key)))
                   .ToQueryString();

            var select = ((IQueryable)context.Rows).Provider.CreateQuery(
                                    Expression.Call(typeof(Queryable), nameof(Queryable.Select), [typeof(ParityRow), lambda.ReturnType],
                                                    context.Rows.AsQueryable().Expression, Expression.Quote(lambda)))
                                .ToQueryString();
            var from = select.IndexOf("\nFROM ", StringComparison.Ordinal);
            return select["SELECT ".Length..from];
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private sealed class CaptureInliner : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
            => node.Expression is ConstantExpression closure
                   ? Expression.Constant(node.Member is FieldInfo field ? field.GetValue(closure.Value) : ((PropertyInfo)node.Member).GetValue(closure.Value),
                                         node.Type)
                   : base.VisitMember(node);
    }
}

/// <summary>The sweep's results held against the waiver file.</summary>
internal sealed record ParityAssessment(
    IReadOnlyList<ParityResult>                Unwaived,
    IReadOnlyList<string>                      Stale,
    IReadOnlyDictionary<ParityResult, Waiver> Waived)
{
    public static ParityAssessment Of(IReadOnlyList<ParityResult> results, IReadOnlyList<Waiver> waivers)
    {
        var rules   = waivers.Where(w => w.Kind == WaiverKind.Rule).ToDictionary(w => w.Title);
        var unknown = rules.Keys.Except(PostgresCanonicalForm.Rules).ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException($"waivers.txt switches on rules the sweep does not know: {string.Join(", ", unknown)}.");

        var rows = new Dictionary<string, Waiver>();
        foreach (var waiver in waivers.Where(w => w.Kind != WaiverKind.Rule))
        foreach (var row in waiver.Rows)
            if (!rows.TryAdd(row, waiver))
                throw new InvalidOperationException($"waivers.txt lists '{row}' twice.");

        var unwaived  = new List<ParityResult>();
        var waived    = new Dictionary<ParityResult, Waiver>();
        var usedRules = new HashSet<string>();
        var usedRows  = new HashSet<string>();
        var all       = rules.Keys.ToHashSet();

        foreach (var result in results.Where(r => r.NeedsWaiver))
        {
            if (result.Outcome == ParityOutcome.Differ && all.Count > 0 && AgreeUnder(result, all))
            {
                // Each rule this row cannot do without is in use; the first one is its waiver.
                var needed = all.Where(rule => !AgreeUnder(result, all.Where(r => r != rule).ToHashSet())).ToList();
                usedRules.UnionWith(needed);
                waived[result] = rules[needed.FirstOrDefault() ?? all.First()];
                continue;
            }

            if (rows.TryGetValue(result.Case.Id, out var waiver))
            {
                waived[result] = waiver;
                usedRows.Add(result.Case.Id);
                continue;
            }

            unwaived.Add(result);
        }

        var stale = rules.Keys.Where(rule => !usedRules.Contains(rule)).Select(rule => $"rule: {rule}")
                         .Concat(rows.Keys.Where(row => !usedRows.Contains(row)).Select(row => $"- {row}  (under '{rows[row].Title}')"))
                         .ToList();

        return new ParityAssessment(unwaived, stale, waived);
    }

    private static bool AgreeUnder(ParityResult result, IReadOnlySet<string> rules)
    {
        try
        {
            return PostgresCanonicalForm.Of(result.Package!, rules) == PostgresCanonicalForm.Of(result.EfCore!, rules);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
