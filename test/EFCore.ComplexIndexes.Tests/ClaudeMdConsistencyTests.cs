using System.Reflection;
using System.Text.RegularExpressions;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// CLAUDE.md is the repository's architectural record — why operation ordering is load-bearing, why
/// the annotation flow is a whitelist, which seam a feature must use. It is also the one document
/// nothing verifies, and its failure mode is worse than being out of date: it is read as ground
/// truth and acted on. Two examples caught by hand rather than by a test — a build command annotated
/// with behaviour that had become actively wrong, and "whitelists exactly its seven Npgsql keys"
/// left behind when 5.0.2 dropped two of them.
/// </summary>
/// <remarks>
/// Prose cannot be asserted, so this checks only the parts of it that are mechanically falsifiable:
/// paths that must exist, annotation keys this repository owns, and members of its own types. Those
/// are the claims that rot silently under a rename, and the rename is what the reader trusts.
/// </remarks>
[TestClass]
public class ClaudeMdConsistencyTests
{
    private static readonly Regex Ticked = new(@"`([^`\n]+)`", RegexOptions.Compiled);

    private static string ClaudeMd => Path.Combine(RepositoryLayout.Root, "CLAUDE.md");

    private static IReadOnlyList<string> TickedTokens { get; } =
        [.. Ticked.Matches(File.ReadAllText(ClaudeMd)).Select(m => m.Groups[1].Value).Distinct()];

    /// <summary>The assemblies whose types CLAUDE.md is describing.</summary>
    private static readonly Assembly[] OwnAssemblies =
    [
        typeof(CustomMigrationsModelDiffer).Assembly,
        typeof(PostgreSQL.NpgsqlComplexIndexMigrationsModelDiffer).Assembly,
        typeof(SqlServer.SqlServerComplexIndexMigrationsModelDiffer).Assembly
    ];

    [TestMethod(DisplayName = "Repository paths named in CLAUDE.md exist")]
    public void Referenced_paths_exist()
    {
        // Anchored on a real top-level entry so that prose like `DbOrder.NullsFirst/NullsLast` or
        // `<clear/>` is not mistaken for a path. Tokens with an ellipsis or wildcard are abbreviations.
        var topLevel = Directory.EnumerateFileSystemEntries(RepositoryLayout.Root)
                                .Select(Path.GetFileName)
                                .ToHashSet(StringComparer.Ordinal);

        var missing = new List<string>();

        foreach (var token in TickedTokens)
        {
            if (!token.Contains('/') || token.Contains('…') || token.Contains('*') || token.Contains(' '))
                continue;

            var segments = token.TrimEnd('/').Split('/');
            if (!topLevel.Contains(segments[0]))
                continue;

            var path = Path.Combine([RepositoryLayout.Root, .. segments]);
            if (!File.Exists(path) && !Directory.Exists(path))
                missing.Add(token);
        }

        Assert.IsEmpty(
            missing,
            $"CLAUDE.md points at paths that no longer exist: {string.Join(", ", missing)}. "
          + "Something was moved or renamed and the documentation was not updated.");
    }

    [TestMethod(DisplayName = "Annotation keys named in CLAUDE.md exist in the source")]
    public void Referenced_annotation_keys_exist()
    {
        // Only keys under a prefix this repository defines. EF's own keys (Relational:ColumnName and
        // friends) are named in CLAUDE.md too and are legitimately absent from these constants.
        var declared = DeclaredAnnotationKeys();
        var ownedPrefixes = declared.Select(key => key.Split(':')[0]).ToHashSet(StringComparer.Ordinal);

        var missing = TickedTokens
                     .Where(token => Regex.IsMatch(token, @"^[A-Za-z]+:[A-Za-z]+$"))
                     .Where(token => ownedPrefixes.Contains(token.Split(':')[0]))
                     .Where(token => !declared.Contains(token))
                     .ToList();

        Assert.IsEmpty(
            missing,
            $"CLAUDE.md names annotation keys that no constant declares: {string.Join(", ", missing)}. "
          + "A renamed key that the documentation still advertises is a key consumers will look for and not find.");
    }

    [TestMethod(DisplayName = "Members named in CLAUDE.md exist on the types they are attributed to")]
    public void Referenced_members_exist()
    {
        var ownTypes = OwnAssemblies
                      .SelectMany(assembly => assembly.GetTypes())
                      .GroupBy(type => type.Name, StringComparer.Ordinal)
                      .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var missing = new List<string>();

        foreach (var token in TickedTokens)
        {
            // `Type.Member`, optionally written as a call — `ComplexIndexStorage.AddOrReplace` or
            // `DbOrder.Desc(...)`. The member must be PascalCase, which is what separates a member
            // reference from a file name: `NpgsqlAnnotations.cs` has the same shape. File names are
            // covered by Referenced_file_names_exist instead.
            var match = Regex.Match(token.Trim(), @"^([A-Z][A-Za-z0-9_]*)\.([A-Z][A-Za-z0-9_]*)(\(.*\))?$");
            if (!match.Success)
                continue;

            if (!ownTypes.TryGetValue(match.Groups[1].Value, out var candidates))
                continue;

            var member = match.Groups[2].Value;

            var found = candidates.Any(type => type.GetMember(
                                                   member,
                                                   BindingFlags.Public | BindingFlags.NonPublic
                                                 | BindingFlags.Instance | BindingFlags.Static
                                                 | BindingFlags.FlattenHierarchy).Length > 0);

            if (!found)
                missing.Add(token);
        }

        Assert.IsEmpty(
            missing,
            $"CLAUDE.md attributes members to types that do not have them: {string.Join(", ", missing)}. "
          + "The type still exists, so nothing else fails — the documentation just describes an API that is gone.");
    }

    [TestMethod(DisplayName = "File names cited in CLAUDE.md exist somewhere in the repository")]
    public void Referenced_file_names_exist()
    {
        // CLAUDE.md cites source files by bare name — `CustomMigrationsModelDiffer.cs` — so the path
        // check never sees them, and a rename leaves the architecture notes pointing at nothing.
        var present = Directory
                     .EnumerateFiles(RepositoryLayout.Root, "*", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                 && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
                     .Select(Path.GetFileName)
                     .ToHashSet(StringComparer.Ordinal);

        var missing = TickedTokens
                     .Where(token => Regex.IsMatch(token, @"^[A-Za-z0-9_.]+\.(cs|targets|props|slnx|sh|json|yml)$"))
                     .Where(token => !present.Contains(token))
                     .ToList();

        Assert.IsEmpty(
            missing,
            $"CLAUDE.md cites files that no longer exist: {string.Join(", ", missing)}.");
    }

    [TestMethod(DisplayName = "The documented size of the Npgsql whitelist matches the whitelist")]
    public void Documented_npgsql_whitelist_size_is_correct()
    {
        // Targeted at one sentence, because that sentence has already rotted once: it said "seven"
        // from before 5.0.2 dropped the two sort-order keys. A count in prose has nothing holding it
        // to the code. Asserting the match failed first keeps a reword from quietly voiding the test.
        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6,
            ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12
        };

        var match = Regex.Match(
            File.ReadAllText(ClaudeMd),
            @"whitelists exactly its (\w+) `Npgsql:\*` index-option keys");

        Assert.IsTrue(
            match.Success,
            "The sentence documenting the size of the Npgsql whitelist was reworded. Update this test "
          + "to match, or drop it — a test that no longer finds its subject asserts nothing.");

        var documented = numbers[match.Groups[1].Value];

        var whitelist = (HashSet<string>)typeof(PostgreSQL.NpgsqlComplexIndexMigrationsModelDiffer)
                       .GetField("SupportedNpgsqlAnnotations", BindingFlags.NonPublic | BindingFlags.Static)!
                       .GetValue(null)!;

        Assert.AreEqual(
            whitelist.Count,
            documented,
            $"CLAUDE.md says the Npgsql differ whitelists {documented} keys; it whitelists {whitelist.Count}: "
          + string.Join(", ", whitelist.Order(StringComparer.Ordinal)));
    }

    /// <summary>Every <c>"Prefix:Name"</c> literal declared as a constant across the shipping projects.</summary>
    private static HashSet<string> DeclaredAnnotationKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in OwnAssemblies.SelectMany(assembly => assembly.GetTypes()))
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (field is { IsLiteral: true, IsInitOnly: false }
                 && field.GetRawConstantValue() is string value
                 && Regex.IsMatch(value, @"^[A-Za-z]+:[A-Za-z]+$"))
                    keys.Add(value);
            }
        }

        return keys;
    }
}
