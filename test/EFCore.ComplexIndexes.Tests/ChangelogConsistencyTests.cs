using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// The changelog lives in four files — the root README plus one per shipping package — so that
/// NuGet shows package-specific history. Nothing about that arrangement keeps them in step, and a
/// release that updates three of the four is invisible until a user reads the stale one.
/// </summary>
[TestClass]
public class ChangelogConsistencyTests
{
    // Matches both changelog heading styles in use: "## What changed in 5.0.2" and "### 5.0.2".
    private static readonly Regex VersionHeading =
        new(@"^\#{2,4} (?:.*\s)?(\d+\.\d+\.\d+)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static Version PackageVersion =>
        Version.Parse(XDocument.Load(RepositoryLayout.BuildProps)
                               .Descendants("Version")
                               .Single()
                               .Value);

    private static List<Version> DocumentedVersions(string readme) =>
        [.. VersionHeading.Matches(File.ReadAllText(readme)).Select(m => Version.Parse(m.Groups[1].Value))];

    [TestMethod(DisplayName = "The root README documents the version being shipped")]
    public void Root_readme_documents_current_version()
    {
        var version = PackageVersion;

        Assert.Contains(
            version,
            DocumentedVersions(RepositoryLayout.RootReadme),
            $"Directory.Build.props ships {version}, but README.md has no '## What changed in {version}' section.");
    }

    [TestMethod(DisplayName = "No README documents a version newer than the one being shipped")]
    public void No_readme_runs_ahead_of_the_package_version()
    {
        var version = PackageVersion;

        foreach (var readme in AllReadmes())
        {
            var ahead = DocumentedVersions(readme).Where(v => v > version).ToList();

            Assert.IsEmpty(
                ahead,
                $"{Describe(readme)} documents {string.Join(", ", ahead)}, which is newer than the "
              + $"{version} in Directory.Build.props — the version bump was probably forgotten.");
        }
    }

    [TestMethod(DisplayName = "Package changelogs only mention versions the root README also covers")]
    public void Package_changelogs_are_a_subset_of_the_root_changelog()
    {
        var root = DocumentedVersions(RepositoryLayout.RootReadme).ToHashSet();

        foreach (var project in RepositoryLayout.ShippingProjects)
        {
            var unknown = DocumentedVersions(project.Readme).Where(v => !root.Contains(v)).ToList();

            Assert.IsEmpty(
                unknown,
                $"{project.PackageId}'s README documents {string.Join(", ", unknown)}, which the root "
              + "README does not cover. Either the root changelog is missing an entry or the version is a typo. "
              + "(A package needing no entry for a release is fine — it just omits the section.)");
        }
    }

    [TestMethod(DisplayName = "Changelog sections are ordered newest first")]
    public void Changelog_sections_are_descending()
    {
        foreach (var readme in AllReadmes())
        {
            var documented = DocumentedVersions(readme);

            CollectionAssert.AreEqual(
                documented.OrderByDescending(v => v).ToList(),
                documented,
                $"{Describe(readme)} lists changelog sections out of order: {string.Join(", ", documented)}.");
        }
    }

    private static IEnumerable<string> AllReadmes() =>
        RepositoryLayout.ShippingProjects.Select(p => p.Readme).Prepend(RepositoryLayout.RootReadme);

    private static string Describe(string readme) =>
        readme == RepositoryLayout.RootReadme ? "The root README" : $"{Path.GetFileName(Path.GetDirectoryName(readme))}'s README";
}
