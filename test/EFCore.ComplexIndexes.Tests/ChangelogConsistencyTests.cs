using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// The changelog lives in four files — the root <c>CHANGELOG.md</c> plus one per shipping package —
/// so that NuGet shows package-specific history. Nothing about that arrangement keeps them in step,
/// and a release that updates three of the four is invisible until a user reads the stale one.
/// </summary>
/// <remarks>
/// The heading style is asserted, not merely parsed. <c>release.yml</c> extracts the release notes by
/// matching <c>## &lt;version&gt;</c> literally in the root changelog and reading to the next
/// <c>##</c>; a section demoted to <c>###</c> would still read as documented here while the release
/// job published a blank release. Keeping the pattern strict is what ties the two together.
/// </remarks>
[TestClass]
public class ChangelogConsistencyTests
{
    // The one heading style: "## 5.0.2". Deliberately strict — see the remarks above.
    private static readonly Regex VersionHeading =
        new(@"^\#\# (\d+\.\d+\.\d+)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static Version PackageVersion =>
        Version.Parse(XDocument.Load(RepositoryLayout.BuildProps)
                               .Descendants("Version")
                               .Single()
                               .Value);

    private static List<Version> DocumentedVersions(string changelog) =>
        [.. VersionHeading.Matches(File.ReadAllText(changelog)).Select(m => Version.Parse(m.Groups[1].Value))];

    [TestMethod(DisplayName = "Every shipping package carries its own changelog")]
    public void Every_package_has_a_changelog()
    {
        var missing = RepositoryLayout.ShippingProjects
                                      .Where(project => !File.Exists(project.Changelog))
                                      .Select(project => project.PackageId)
                                      .ToList();

        Assert.IsEmpty(
            missing,
            $"{string.Join(", ", missing)} has no CHANGELOG.md. Its README links to one on GitHub, so "
          + "the link 404s for anyone arriving from nuget.org.");
    }

    [TestMethod(DisplayName = "The root changelog documents the version being shipped")]
    public void Root_changelog_documents_current_version()
    {
        var version = PackageVersion;

        Assert.Contains(
            version,
            DocumentedVersions(RepositoryLayout.RootChangelog),
            $"Directory.Build.props ships {version}, but CHANGELOG.md has no '## {version}' section — "
          + "which is also the text release.yml publishes as the release notes.");
    }

    [TestMethod(DisplayName = "No changelog documents a version newer than the one being shipped")]
    public void No_changelog_runs_ahead_of_the_package_version()
    {
        var version = PackageVersion;

        foreach (var changelog in AllChangelogs())
        {
            var ahead = DocumentedVersions(changelog).Where(v => v > version).ToList();

            Assert.IsEmpty(
                ahead,
                $"{Describe(changelog)} documents {string.Join(", ", ahead)}, which is newer than the "
              + $"{version} in Directory.Build.props — the version bump was probably forgotten.");
        }
    }

    [TestMethod(DisplayName = "Package changelogs only mention versions the root changelog also covers")]
    public void Package_changelogs_are_a_subset_of_the_root_changelog()
    {
        var root = DocumentedVersions(RepositoryLayout.RootChangelog).ToHashSet();

        foreach (var project in RepositoryLayout.ShippingProjects.Where(p => File.Exists(p.Changelog)))
        {
            var unknown = DocumentedVersions(project.Changelog).Where(v => !root.Contains(v)).ToList();

            Assert.IsEmpty(
                unknown,
                $"{project.PackageId}'s changelog documents {string.Join(", ", unknown)}, which the root "
              + "CHANGELOG.md does not cover. Either the root changelog is missing an entry or the version is a typo. "
              + "(A package needing no entry for a release is fine — it just omits the section.)");
        }
    }

    [TestMethod(DisplayName = "Changelog sections are ordered newest first")]
    public void Changelog_sections_are_descending()
    {
        foreach (var changelog in AllChangelogs())
        {
            var documented = DocumentedVersions(changelog);

            CollectionAssert.AreEqual(
                documented.OrderByDescending(v => v).ToList(),
                documented,
                $"{Describe(changelog)} lists changelog sections out of order: {string.Join(", ", documented)}.");
        }
    }

    /// <summary>
    /// The READMEs are the landing pages now, not the changelog. A version section left behind in one
    /// is a second copy nothing keeps in step — and the packed READMEs are what nuget.org renders, so
    /// the stale copy is the one most consumers would read.
    /// </summary>
    [TestMethod(DisplayName = "No README carries a changelog of its own")]
    public void Readmes_do_not_duplicate_the_changelog()
    {
        var offenders = AllReadmes()
                       .Where(readme => VersionHeading.IsMatch(File.ReadAllText(readme)))
                       .Select(Describe)
                       .ToList();

        Assert.IsEmpty(
            offenders,
            $"{string.Join(", ", offenders)} contains '## <version>' changelog sections. The changelog "
          + "moved to CHANGELOG.md; a copy left in a README drifts silently and, for the packed ones, "
          + "drifts where consumers read it.");
    }

    // A package changelog that is missing entirely is Every_package_has_a_changelog's finding, and its
    // message is the actionable one. Reading it here too would bury that under three file-not-found
    // crashes in tests that are asking a different question.
    private static IEnumerable<string> AllChangelogs() =>
        RepositoryLayout.ShippingProjects.Select(p => p.Changelog)
                        .Where(File.Exists)
                        .Prepend(RepositoryLayout.RootChangelog);

    private static IEnumerable<string> AllReadmes() =>
        RepositoryLayout.ShippingProjects.Select(p => p.Readme).Prepend(RepositoryLayout.RootReadme);

    private static string Describe(string path) =>
        Path.GetDirectoryName(path) == RepositoryLayout.Root
            ? $"The root {Path.GetFileName(path)}"
            : $"{Path.GetFileName(Path.GetDirectoryName(path))}'s {Path.GetFileName(path)}";
}
