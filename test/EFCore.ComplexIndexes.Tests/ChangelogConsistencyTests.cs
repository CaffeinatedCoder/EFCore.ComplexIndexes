using System.Text.RegularExpressions;

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
    // The one heading style: "## 5.0.2", or a preview's "## 11.0.0-preview.1". Deliberately strict —
    // see the remarks above.
    private static readonly Regex VersionHeading =
        new($@"^\#\# ({ReleaseVersion.Pattern})\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static ReleaseVersion PackageVersion => ReleaseVersion.Parse(RepositoryLayout.ShippedVersion);

    private static List<ReleaseVersion> DocumentedVersions(string changelog) =>
        [.. VersionHeading.Matches(File.ReadAllText(changelog)).Select(m => ReleaseVersion.Parse(m.Groups[1].Value))];

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


    /// <summary>
    /// The section a minor opens with when it changes an answer: a migration that scaffolded and
    /// applied cleanly while the database ended up different from the model, whose fix changes what
    /// an existing model scaffolds (CONTRIBUTING.md, A fix that changes an answer).
    /// </summary>
    private const string ChangedAnswers = "Changed answers";

    private const string IssuesPrefix = "https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/";

    private static readonly Regex IssueLink = new($@"\]\({Regex.Escape(IssuesPrefix)}\d+\)", RegexOptions.Compiled);

    /// <summary>
    /// <c>### Changed answers</c> appears only in a minor. A major lists a changed answer as it
    /// always has, a patch may not change one at all, and a preview is a major's.
    /// </summary>
    /// <remarks>
    /// The section exists so that a silent fix can ship on the LTS line, which has no major number
    /// left to spend on it. A patch carrying one would be read as "nothing to check" by every
    /// consumer who upgrades patches unattended, which is the reason the rule says minor.
    /// </remarks>
    [TestMethod(DisplayName = "'### Changed answers' appears only in a minor")]
    public void Changed_answers_appear_only_in_a_minor()
    {
        var offenders = VersionSections()
                       .Where(section => section.Lines.Any(entry => IsSectionHeading(entry.Line, ChangedAnswers)))
                       .Where(section => !section.Version.IsMinor)
                       .Select(section => $"{Describe(section.Changelog)} ({section.Version})")
                       .ToList();

        Assert.IsEmpty(
            offenders,
            $"These versions have a '### {ChangedAnswers}' section but are not a minor (x.y.0, y > 0): "
          + $"{string.Join(", ", offenders)}. A major lists a changed answer as it always has, and a patch "
          + "may not change one: bump the minor (CONTRIBUTING.md, A fix that changes an answer).");
    }

    /// <summary>
    /// <c>### Changed answers</c> is the top of its entry: after the introduction, before any other
    /// change and any other heading. A reader deciding whether to upgrade must not have to find it.
    /// </summary>
    [TestMethod(DisplayName = "'### Changed answers' leads its entry")]
    public void Changed_answers_lead_their_entry()
    {
        var offenders = new List<string>();

        foreach (var (changelog, version, lines) in VersionSections())
        {
            var heading = lines.FindIndex(entry => IsSectionHeading(entry.Line, ChangedAnswers));
            if (heading < 0) continue;

            var before = lines.Take(heading)
                              .Where(entry => entry.Line.StartsWith("- ", StringComparison.Ordinal)
                                           || entry.Line.StartsWith("### ", StringComparison.Ordinal))
                              .Select(entry => $"{Describe(changelog)}:{entry.Number} ({version}): '{Truncate(entry.Line)}'")
                              .ToList();

            offenders.AddRange(before);
        }

        Assert.IsEmpty(
            offenders,
            $"These lines come before '### {ChangedAnswers}' in their entry. The section goes directly "
          + "after the entry's introduction, so a consumer sees the changed answers first "
          + "(CONTRIBUTING.md, A fix that changes an answer):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Every row under <c>### Changed answers</c> links the issue it closes. The rows run to the
    /// next <c>###</c> heading, so an entry's other changes follow under one of their own.
    /// </summary>
    /// <remarks>
    /// The issue link is what ties the row to the <c>silent</c> label: <c>.github/scripts/pr-hygiene.sh</c>
    /// fails a <c>silent</c> PR into a minor whose closing issue has no row here, and it finds the row
    /// by this link. A test does not see labels, so this is the half it can hold.
    /// </remarks>
    [TestMethod(DisplayName = "Every '### Changed answers' row links its issue")]
    public void Every_changed_answer_links_its_issue()
    {
        var offenders = new List<string>();

        foreach (var (changelog, version, lines) in VersionSections())
        {
            var inside = false;

            foreach (var (number, line) in lines)
            {
                if (line.StartsWith("### ", StringComparison.Ordinal))
                    inside = IsSectionHeading(line, ChangedAnswers);
                else if (inside && line.StartsWith("- ", StringComparison.Ordinal) && !IssueLink.IsMatch(line))
                    offenders.Add($"{Describe(changelog)}:{number} ({version}): '{Truncate(line)}'");
            }
        }

        Assert.IsEmpty(
            offenders,
            $"These '### {ChangedAnswers}' rows link no issue ({IssuesPrefix}n). The rows run to the next "
          + "### heading, so a change that is not a changed answer goes under a heading of its own "
          + "(CONTRIBUTING.md, A fix that changes an answer):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static bool IsSectionHeading(string line, string name) =>
        line.StartsWith("### ", StringComparison.Ordinal) && line[4..].Trim() == name;

    private static string Truncate(string line) => line.Length <= 60 ? line : line[..57] + "...";

    /// <summary>
    /// Each version section of every changelog: the lines after its <c>## x.y.z</c> heading up to the
    /// next <c>##</c> heading, with their line numbers. The file's own header is not a section.
    /// </summary>
    private static IEnumerable<(string Changelog, ReleaseVersion Version, List<(int Number, string Line)> Lines)> VersionSections()
    {
        foreach (var changelog in AllChangelogs())
        {
            ReleaseVersion? version = null;
            var lines = new List<(int Number, string Line)>();
            var all   = File.ReadAllLines(changelog);

            for (var index = 0; index < all.Length; index++)
            {
                var line = all[index].TrimEnd();

                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    if (version is not null) yield return (changelog, version, lines);

                    var heading = VersionHeading.Match(line);
                    version = heading.Success ? ReleaseVersion.Parse(heading.Groups[1].Value) : null;
                    lines   = [];
                }
                else if (version is not null)
                {
                    lines.Add((index + 1, line));
                }
            }

            if (version is not null) yield return (changelog, version, lines);
        }
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
