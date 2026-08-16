using System.Reflection;
using System.Text.RegularExpressions;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// The documentation is a set of promises about an API surface, and a promise about a method that
/// does not exist fails only in the reader's editor. Package validation already fails the pack when
/// a public member is removed (CP0002), so a deletion surfaces at release — but it surfaces as an
/// API-surface decision, and nothing then walks the prose. A rename fixed in the source and forgotten
/// in the docs, or a method name simply typed wrong, has nothing at all catching it.
/// </summary>
/// <remarks>
/// <para>
/// The second test guards a specific false promise. Expression indexes live in the PostgreSQL
/// satellite because SQL Server has no functional-index DDL — documenting `HasExpressionIndex` on a
/// SQL Server page would advertise something the provider cannot do, and the differ's rejection
/// (correct, deliberate) would read as a bug. Provider-exclusive means exclusive after subtracting
/// what core and the other satellite also declare: `IsUnique`, `HasName` and `IncludeProperties`
/// exist on all three builders and say nothing about scope.
/// </para>
/// <para>
/// The changelogs are deliberately out of scope. They are a historical record: an entry that says
/// 5.0.0 shipped `HasExclusionConstraint` stays true after a later rename, and asserting over them
/// would turn every rename into pressure to rewrite history.
/// </para>
/// </remarks>
[TestClass]
public class DocumentationApiTests
{
    /// <summary>
    /// A method-call-shaped mention: <c>HasComplexIndex(</c>, in prose or in a snippet. The optional
    /// type-argument list is not decoration — <c>HasTemporalForeignKey&lt;Subscription&gt;(…)</c> is
    /// how every generic API in these docs is written, and without it the whole generic surface went
    /// unchecked while the test reported green.
    /// </summary>
    private static readonly Regex Invocation = new(@"\b([A-Z][A-Za-z0-9]*)(?:<[^<>()]*>)?\(", RegexOptions.Compiled);

    private static readonly Regex InlineCode = new(@"`([^`\n]+)`", RegexOptions.Compiled);

    private static readonly Assembly Core       = typeof(CustomMigrationsModelDiffer).Assembly;
    private static readonly Assembly PostgreSql = typeof(PostgreSQL.NpgsqlComplexIndexMigrationsModelDiffer).Assembly;
    private static readonly Assembly SqlServer  = typeof(SqlServer.SqlServerComplexIndexMigrationsModelDiffer).Assembly;

    /// <summary>
    /// Calls into EF Core, Npgsql, the BCL and DI that the examples legitimately make. Everything not
    /// listed here has to be ours and has to exist — which is what makes the assertion mean anything.
    /// A new external call in an example belongs here, and the failure names the exact token to add.
    /// </summary>
    private static readonly HashSet<string> ExternalApi = new(StringComparer.Ordinal)
    {
        // EF Core
        "ComplexProperty", "Property", "HasColumnName", "HasKey", "ToJson", "Entity",
        "MigrationsAssembly", "UseInternalServiceProvider",
        // Npgsql
        "UseNpgsql", "AddEntityFrameworkNpgsql",
        // Dependency injection
        "ServiceCollection", "BuildServiceProvider", "AddDbContext",
        // BCL
        "ToLower", "Trim"
    };

    [TestMethod(DisplayName = "Every API the documentation names exists")]
    public void Documented_api_exists()
    {
        var own = OwnMembers(Core, PostgreSql, SqlServer);
        var missing = new List<string>();

        foreach (var page in DocumentationPages())
            foreach (var mention in ApiMentions(page).Where(name => !ExternalApi.Contains(name) && !own.Contains(name)))
                missing.Add($"{Relative(page)} → {mention}()");

        Assert.IsEmpty(
            missing.Distinct(),
            $"The documentation names API that does not exist: {string.Join(", ", missing.Distinct())}. "
          + "Either it was renamed and the prose was not updated, or the name is a typo — both read as "
          + "a working example until someone types it. If the call belongs to EF Core, Npgsql or the "
          + "BCL, add it to ExternalApi instead.");
    }

    [TestMethod(DisplayName = "A provider's pages name no other provider's exclusive API")]
    public void Provider_pages_document_only_their_own_api()
    {
        var postgresOnly  = Exclusive(PostgreSql, Core, SqlServer);
        var sqlServerOnly = Exclusive(SqlServer, Core, PostgreSql);

        var misplaced = new List<string>();

        foreach (var page in DocumentationPages())
        {
            var (foreign, provider) = ScopeOf(page) switch
            {
                Provider.PostgreSql => (sqlServerOnly, "SQL Server"),
                Provider.SqlServer  => (postgresOnly, "PostgreSQL"),

                // The core package's README documents the provider-agnostic surface only: a satellite
                // API there promises something the package a reader installed does not contain.
                Provider.Core => ([.. postgresOnly.Concat(sqlServerOnly)], "a satellite"),

                _ => (null, string.Empty)
            };

            if (foreign is null)
                continue;

            foreach (var mention in ApiMentions(page).Where(foreign.Contains))
                misplaced.Add($"{Relative(page)} → {mention}() ({provider})");
        }

        Assert.IsEmpty(
            misplaced.Distinct(),
            $"Pages document another provider's API: {string.Join(", ", misplaced.Distinct())}. A reader "
          + "on that page cannot call it, and the differ's refusal to render it will read as a bug "
          + "rather than as the deliberate scoping it is.");
    }

    private enum Provider { Unscoped, Core, PostgreSql, SqlServer }

    /// <summary>
    /// The package READMEs are discovered, so a new satellite is scoped without touching this test.
    /// Pages under <c>docs/</c> are scoped by the provider token in their file name — the convention
    /// the split established; a page named after neither documents both and is left unscoped.
    /// </summary>
    private static Provider ScopeOf(string page)
    {
        foreach (var project in RepositoryLayout.ShippingProjects)
            if (page == project.Readme)
                return project.PackageId switch
                {
                    var id when id.EndsWith(".PostgreSQL", StringComparison.Ordinal) => Provider.PostgreSql,
                    var id when id.EndsWith(".SqlServer", StringComparison.Ordinal)  => Provider.SqlServer,
                    _                                                               => Provider.Core
                };

        var name = Path.GetFileNameWithoutExtension(page).ToLowerInvariant();

        if (Path.GetDirectoryName(page) == RepositoryLayout.DocsDirectory)
            return name switch
            {
                var n when n.Contains("postgresql") => Provider.PostgreSql,
                var n when n.Contains("sqlserver")  => Provider.SqlServer,
                _                                   => Provider.Unscoped
            };

        return Provider.Unscoped;   // the root README documents everything
    }

    /// <summary>The user-facing documentation set. Changelogs excluded — see the remarks above.</summary>
    private static IEnumerable<string> DocumentationPages()
    {
        yield return RepositoryLayout.RootReadme;

        if (Directory.Exists(RepositoryLayout.DocsDirectory))
            foreach (var page in Directory.EnumerateFiles(RepositoryLayout.DocsDirectory, "*.md", SearchOption.AllDirectories))
                yield return page;

        foreach (var project in RepositoryLayout.ShippingProjects)
            yield return project.Readme;
    }

    /// <summary>
    /// Names read from C# contexts only: inline code spans in prose, and the code lines of a
    /// <c>csharp</c> fence with any trailing <c>//</c> comment cut off. The comments hold the
    /// generated SQL and the <c>sql</c> fences are SQL outright — reading either would file
    /// <c>UNIQUE(</c> and friends as missing API.
    /// </summary>
    private static IEnumerable<string> ApiMentions(string page)
    {
        var fenceLanguage = (string?)null;

        foreach (var line in File.ReadLines(page))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenceLanguage = fenceLanguage is null ? line.Trim().TrimStart('`').Trim().ToLowerInvariant() : null;
                continue;
            }

            if (fenceLanguage is null)
            {
                foreach (Match span in InlineCode.Matches(line))
                    foreach (Match invocation in Invocation.Matches(span.Groups[1].Value))
                        yield return invocation.Groups[1].Value;

                continue;
            }

            if (fenceLanguage is not "csharp")
                continue;

            var comment = line.IndexOf("//", StringComparison.Ordinal);
            var code    = comment >= 0 ? line[..comment] : line;

            foreach (Match invocation in Invocation.Matches(code))
                yield return invocation.Groups[1].Value;
        }
    }

    private static HashSet<string> OwnMembers(params Assembly[] assemblies)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var assembly in assemblies)
            names.UnionWith(PublicNames(assembly));

        return names;
    }

    /// <summary>What only <paramref name="assembly"/> declares, once the others are subtracted.</summary>
    private static HashSet<string> Exclusive(Assembly assembly, params Assembly[] others)
    {
        var names = PublicNames(assembly);
        names.ExceptWith(OwnMembers(others));

        return names;
    }

    private static HashSet<string> PublicNames(Assembly assembly)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in assembly.GetExportedTypes())
        {
            names.Add(type.Name);

            // DeclaredOnly: an inherited member belongs to the assembly that declares it, or every
            // satellite would "declare" the core builder's IsUnique and nothing would be exclusive.
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance
                                                 | BindingFlags.Static | BindingFlags.DeclaredOnly))
                names.Add(member.Name);
        }

        return names;
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(RepositoryLayout.Root, path).Replace(Path.DirectorySeparatorChar, '/');
}
