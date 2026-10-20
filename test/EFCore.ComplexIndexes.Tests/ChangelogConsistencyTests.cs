using System.Text.RegularExpressions;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// The changelog lives in four files — the root <c>CHANGELOG.md</c> plus one per shipping package —
/// so that NuGet shows package-specific history. Nothing about that arrangement keeps them in step,
/// and a release that updates three of the four is invisible until a user reads the stale one.
/// </summary>
/// <remarks>
/// The heading style is asserted, not merely parsed. <c>release.yml</c> extracts the release notes by
/// matching <c>## [&lt;version&gt;]</c> in the root changelog and reading to the next <c>##</c>; a
/// section in another style would be skipped by every test below while the release job published a
/// blank release. <see cref="Every_level_two_heading_is_a_version_heading"/> is what ties the two
/// together. The form itself (CONTRIBUTING.md, Changelog) is ported from CodoMetis.ValueRanges.
/// </remarks>
[TestClass]
public class ChangelogConsistencyTests
{
    /// <summary>
    /// The one heading style: <c>## [5.5.0] — 2026-10-20</c>, or a preview's,
    /// <c>## [11.0.0-preview.1] — 2026-11-11</c>.
    /// </summary>
    internal static readonly Regex VersionHeading =
        new($@"^## \[(?<version>{ReleaseVersion.Pattern})\] — (?<date>\d{{4}}-\d{{2}}-\d{{2}})\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

    private static ReleaseVersion PackageVersion => ReleaseVersion.Parse(RepositoryLayout.ShippedVersion);

    internal static List<ReleaseVersion> DocumentedVersions(string changelog) =>
        [.. VersionHeading.Matches(File.ReadAllText(changelog)).Select(m => ReleaseVersion.Parse(m.Groups["version"].Value))];

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
            $"Directory.Build.props ships {version}, but CHANGELOG.md has no '## [{version}] — yyyy-mm-dd' "
          + "section — which is also the text release.yml publishes as the release notes.");
    }

    /// <summary>
    /// Every package changelog accounts for the version being shipped, with an entry or with the line
    /// "No change in this package." The three packages share one <c>Version</c> and release together,
    /// so a package page on nuget.org without the version reads as a release that forgot it.
    /// </summary>
    [TestMethod(DisplayName = "Every package changelog documents the version being shipped")]
    public void Every_package_changelog_documents_current_version()
    {
        var version = PackageVersion;

        var undocumented = RepositoryLayout.ShippingProjects
                                           .Where(project => File.Exists(project.Changelog))
                                           .Where(project => !DocumentedVersions(project.Changelog).Contains(version))
                                           .Select(project => project.PackageId)
                                           .ToList();

        Assert.IsEmpty(
            undocumented,
            $"These packages ship at {version} without an entry for it: {string.Join(", ", undocumented)}. "
          + "Give each a section, with its changes or the line 'No change in this package.'");
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
              + "CHANGELOG.md does not cover. Either the root changelog is missing an entry or the version is a typo.");
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
    /// Every <c>##</c> heading of a changelog is a version heading in the one style. A heading in
    /// another style is no section to any test here, so its lines would go unchecked, and
    /// <c>release.yml</c> would not find it.
    /// </summary>
    /// <remarks>
    /// This is also what keeps entries out of a floating <c>## [Unreleased]</c>. Every commit on a
    /// release branch is a tag away from being published, and the release workflow publishes whatever
    /// <c>Directory.Build.props</c> names, so a heading that says "not released yet" becomes false the
    /// moment someone tags. Write entries under the version being prepared instead.
    /// </remarks>
    [TestMethod(DisplayName = "Every '##' heading of a changelog is a version heading")]
    public void Every_level_two_heading_is_a_version_heading()
    {
        var offenders = AllChangelogs()
                       .SelectMany(changelog => File.ReadAllLines(changelog)
                                                    .Select((line, index) => (Line: line.TrimEnd(), Number: index + 1))
                                                    .Where(entry => entry.Line.StartsWith("## ", StringComparison.Ordinal)
                                                                 && !VersionHeading.IsMatch(entry.Line))
                                                    .Select(entry => $"{Describe(changelog)}:{entry.Number}: '{entry.Line}'"))
                       .ToList();

        Assert.IsEmpty(
            offenders,
            "These changelog headings are not '## [x.y.z] — yyyy-mm-dd'. release.yml reads the release "
          + "notes from that heading, and the other changelog tests read nothing else:"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Every version of the root changelog has a reference definition pointing at its GitHub release,
    /// so its bracketed heading renders as a link rather than as literal brackets.
    /// </summary>
    [TestMethod(DisplayName = "Every root changelog version links its release")]
    public void Every_root_version_links_its_release()
    {
        var definitions = File.ReadAllLines(RepositoryLayout.RootChangelog).Select(line => line.TrimEnd()).ToHashSet(StringComparer.Ordinal);
        var missing     = DocumentedVersions(RepositoryLayout.RootChangelog)
                         .Where(version => !definitions.Contains($"[{version}]: {ReleasesPrefix}v{version}"))
                         .ToList();

        Assert.IsEmpty(
            missing,
            $"The root CHANGELOG.md has no '[x.y.z]: {ReleasesPrefix}vx.y.z' definition for "
          + $"{string.Join(", ", missing)}. Add one at the end of the file, newest first.");
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
                       .Where(readme => AnyVersionHeading.IsMatch(File.ReadAllText(readme)))
                       .Select(Describe)
                       .ToList();

        Assert.IsEmpty(
            offenders,
            $"{string.Join(", ", offenders)} contains changelog sections. The changelog moved to "
          + "CHANGELOG.md; a copy left in a README drifts silently and, for the packed ones, "
          + "drifts where consumers read it.");
    }

    /// <summary>A version heading in either style, today's or the one before 5.5.0 (<c>## 5.4.1</c>).</summary>
    private static readonly Regex AnyVersionHeading = new($@"^## \[?{ReleaseVersion.Pattern}\]?", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>The sections a version may have, in the order they must appear.</summary>
    private static readonly string[] SectionOrder = [ChangedAnswers, "Breaking", "Added", "Changed", "Fixed"];

    /// <summary>
    /// The longest a change may be, in words, not counting link targets. Past this a line is an
    /// explanation, which belongs in the guide the line links to or in <c>docs/upgrading.md</c>.
    /// </summary>
    private const int MaxWordsPerChange = 60;

    /// <summary>The longest a version's one-line introduction may be, in words.</summary>
    private const int MaxWordsPerIntro = 40;

    /// <summary>
    /// Every version section is terse: at most one introductory line, then <c>###</c> sections from
    /// <see cref="SectionOrder"/>, in that order, each holding one-line changes.
    /// </summary>
    /// <remarks>
    /// The root section is the GitHub release's notes, verbatim. Before 5.5.0 an entry ran to twenty
    /// lines of reasoning, measurements and upgrade notes, and a reader looking for what to change had
    /// to dig for it. The explanation belongs in <c>docs/upgrading.md</c> and the guides; the line says
    /// what happens now and what to do, and links there. A continuation line, a nested bullet, a code
    /// block or a second paragraph is how prose comes back, so each is refused here. Because
    /// <c>### Changed answers</c> is first in the order, this also keeps it at the top of its entry.
    /// </remarks>
    [TestMethod(DisplayName = "Every version section holds one-line changes under known sections")]
    public void Every_version_section_has_one_line_changes_under_known_sections()
    {
        var offenders = new List<string>();
        var sections  = VersionSections().ToList();

        // A section reader that finds nothing would pass everything below.
        Assert.AreEqual(
            AllChangelogs().Count(), sections.Select(section => section.Changelog).Distinct().Count(),
            "Some changelog has no '## [x.y.z] — yyyy-mm-dd' section this test could read, so its form went unchecked.");

        foreach (var (changelog, version, lines) in sections)
        {
            var intro   = 0;
            var current = -1;

            foreach (var (number, line) in lines)
            {
                var where = $"{Describe(changelog)}:{number} ({version})";

                if (line.Length == 0 || ReferenceDefinition.IsMatch(line)) continue;

                if (line.StartsWith("### ", StringComparison.Ordinal))
                {
                    var index = Array.IndexOf(SectionOrder, line[4..].Trim());

                    if (index < 0)
                        offenders.Add($"{where}: '{line}' is not one of {string.Join(", ", SectionOrder)}");
                    else if (index <= current)
                        offenders.Add($"{where}: '{line}' is repeated or out of order; sections go {string.Join(", ", SectionOrder)}");
                    else
                        current = index;
                }
                else if (line.StartsWith("- ", StringComparison.Ordinal))
                {
                    if (current < 0) offenders.Add($"{where}: a change sits outside a ### section");
                }
                else if (char.IsWhiteSpace(line[0]) || line.StartsWith("```", StringComparison.Ordinal))
                {
                    offenders.Add($"{where}: a continuation line, nested bullet or code block; say the change in one line");
                }
                else if (line.StartsWith('#'))
                {
                    offenders.Add($"{where}: '{Truncate(line)}' is a heading below the ### sections; use none");
                }
                else if (current >= 0 || ++intro > 1)
                {
                    offenders.Add($"{where}: a paragraph; a version has at most one introductory line, before its sections");
                }
            }
        }

        Assert.IsEmpty(
            offenders,
            "These changelog lines are not in the terse form (CONTRIBUTING.md, Changelog):"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// A change fits in <see cref="MaxWordsPerChange"/> words and an introduction in
    /// <see cref="MaxWordsPerIntro"/>, link targets not counted.
    /// </summary>
    [TestMethod(DisplayName = "Every changelog line is short")]
    public void Every_changelog_line_is_short()
    {
        var offenders = new List<string>();

        foreach (var (changelog, version, lines) in VersionSections())
        {
            foreach (var (number, line) in lines.Where(entry => entry.Line.Length > 0 && !entry.Line.StartsWith('#')
                                                             && !ReferenceDefinition.IsMatch(entry.Line)))
            {
                var limit = line.StartsWith("- ", StringComparison.Ordinal) ? MaxWordsPerChange : MaxWordsPerIntro;
                var words = LinkTarget.Replace(line, "]").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

                if (words > limit)
                    offenders.Add($"{Describe(changelog)}:{number} ({version}): {words} words, at most {limit}");
            }
        }

        Assert.IsEmpty(
            offenders,
            "These changelog lines are explanations rather than changes. Move the detail to the guide "
          + "the line links to, or to docs/upgrading.md (CONTRIBUTING.md, Changelog):"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>A link inside a version section is an absolute URL.</summary>
    /// <remarks>
    /// The root section is published as the GitHub release's notes, where a relative link resolves
    /// against the release page and lands nowhere. The package changelogs follow the same rule so an
    /// entry can be copied between them as it is. <c>DocumentationLinkTests</c> checks that the
    /// absolute links into this repository resolve.
    /// </remarks>
    [TestMethod(DisplayName = "Every changelog link is absolute")]
    public void Every_changelog_link_is_absolute()
    {
        var offenders = VersionSections()
                       .SelectMany(section => section.Lines.SelectMany(entry => LinkTarget.Matches(entry.Line)
                                                                    .Select(match => match.Groups["target"].Value)
                                                                    .Where(target => !target.StartsWith("https://", StringComparison.Ordinal))
                                                                    .Select(target => $"{Describe(section.Changelog)}:{entry.Number} ({section.Version}): {target}")))
                       .ToList();

        Assert.IsEmpty(
            offenders,
            "These changelog links are relative, and break on the GitHub release page. Use the "
          + "https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/blob/main/… form:"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// The section a minor opens with when it changes an answer: a migration that scaffolded and
    /// applied cleanly while the database ended up different from the model, whose fix changes what
    /// an existing model scaffolds (CONTRIBUTING.md, A fix that changes an answer).
    /// </summary>
    private const string ChangedAnswers = "Changed answers";

    /// <summary>
    /// The last version before the rule. Up to 5.4.1 a silent fix was listed under Fixed or Changed;
    /// every version after it that changes an answer says so under <see cref="ChangedAnswers"/>.
    /// </summary>
    private static readonly ReleaseVersion LastReleaseBeforeChangedAnswers = ReleaseVersion.Parse("5.4.1");

    private const string IssuesPrefix = "https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/";

    private const string ReleasesPrefix = "https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/releases/tag/";

    private static readonly Regex IssueLink = new($@"\]\({Regex.Escape(IssuesPrefix)}\d+\)", RegexOptions.Compiled);

    private static readonly Regex UpgradingLink =
        new(@"\]\(https://github\.com/CaffeinatedCoder/EFCore\.ComplexIndexes/blob/main/docs/upgrading\.md#(?<anchor>[^)\s]+)\)", RegexOptions.Compiled);

    /// <summary>
    /// <c>### Changed answers</c> appears only in a minor. A major lists a changed answer under
    /// Breaking, a patch may not change one at all, and a preview is a major's.
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
          + $"{string.Join(", ", offenders)}. A major lists a changed answer under Breaking, and a patch "
          + "may not change one: bump the minor (CONTRIBUTING.md, A fix that changes an answer).");
    }

    /// <summary>
    /// Every row under <c>### Changed answers</c> links the issue it closes and its entry in
    /// <c>docs/upgrading.md</c>, and that entry exists.
    /// </summary>
    /// <remarks>
    /// The issue link is what ties the row to the <c>silent</c> label: <c>.github/scripts/pr-hygiene.sh</c>
    /// fails a <c>silent</c> PR into a minor whose closing issue has no row here, and it finds the row
    /// by this link. A test does not see labels, so this is the half it can hold. The upgrading entry
    /// is where the consumer learns what the first <c>migrations add</c> re-creates and what to clean
    /// up first, which a one-line row cannot say.
    /// </remarks>
    [TestMethod(DisplayName = "Every '### Changed answers' row links its issue and its upgrading entry")]
    public void Every_changed_answer_links_its_issue_and_its_upgrading_entry()
    {
        var anchors   = DocumentationLinkTests.Anchors(UpgradingGuide);
        var offenders = new List<string>();

        foreach (var (changelog, version, lines) in VersionSections())
        {
            foreach (var (number, line) in ChangesUnder(lines, ChangedAnswers))
            {
                var where = $"{Describe(changelog)}:{number} ({version})";

                if (!IssueLink.IsMatch(line))
                    offenders.Add($"{where}: no link to the issue it closes ({IssuesPrefix}n)");

                var upgrading = UpgradingLink.Match(line);

                if (!upgrading.Success)
                    offenders.Add($"{where}: no link to its entry in docs/upgrading.md");
                else if (!anchors.Contains(upgrading.Groups["anchor"].Value))
                    offenders.Add($"{where}: docs/upgrading.md has no heading '#{upgrading.Groups["anchor"].Value}'");
            }
        }

        Assert.IsEmpty(
            offenders,
            $"These '### {ChangedAnswers}' rows do not lead a reader to the issue and the upgrading entry:"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// After 5.4.1, a change that says it used to produce something — "where it rendered …", "where
    /// it left them out" — sits under <c>### Changed answers</c>, in a minor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The form already makes every fix say what happened before. A fix whose "before" was an error
    /// is loud and belongs under Fixed; one whose "before" was a migration that applied is a changed
    /// answer, the kind a consumer cannot find by running their tests, and filed under Fixed it reads
    /// as harmless.
    /// </para>
    /// <para>
    /// The limit: this reads the wording, not the label. A changed answer phrased without the
    /// "where it …" clause the form prescribes passes here. The <c>silent</c> label is not reachable
    /// from a test; <c>.github/scripts/pr-hygiene.sh</c>, which sees the PR's labels, holds the other
    /// half by requiring the row for a <c>silent</c> PR's issue.
    /// </para>
    /// </remarks>
    [TestMethod(DisplayName = "After 5.4.1 a changed answer sits under '### Changed answers'")]
    public void After_5_4_1_a_changed_answer_is_under_changed_answers()
    {
        var offenders = new List<string>();

        foreach (var (changelog, version, lines) in VersionSections())
        {
            if (version <= LastReleaseBeforeChangedAnswers || version.IsPrerelease || version is { Minor: 0, Patch: 0 }) continue;

            var section = "";

            foreach (var (number, line) in lines)
            {
                if (line.StartsWith("### ", StringComparison.Ordinal)) section = line[4..].Trim();
                else if (section != ChangedAnswers && line.StartsWith("- ", StringComparison.Ordinal) && PriorValue.Match(line) is { Success: true } clause)
                    offenders.Add($"{Describe(changelog)}:{number} ({version}, ### {section}): '{clause.Value}'");
            }
        }

        Assert.IsEmpty(
            offenders,
            $"These changes say a migration used to produce something, so they changed an answer. Move "
          + $"each under '### {ChangedAnswers}' (a patch cannot carry one: bump the minor), and link its "
          + "issue and its docs/upgrading.md entry (CONTRIBUTING.md, A fix that changes an answer):"
          + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// <c>docs/upgrading.md</c> runs from the oldest version to the newest, so a reader starts at the
    /// one after theirs and reads down. "Changed answers in x.y.0" sits with its version.
    /// </summary>
    [TestMethod(DisplayName = "docs/upgrading.md runs from the oldest version to the newest")]
    public void Upgrading_sections_run_from_the_oldest_version_to_the_newest()
    {
        var versions = Regex.Matches(File.ReadAllText(UpgradingGuide),
                                     $@"^## (?:Upgrading to|Changed answers in) (?<version>{ReleaseVersion.Pattern})\s*$",
                                     RegexOptions.Multiline)
                            .Select(match => ReleaseVersion.Parse(match.Groups["version"].Value))
                            .ToList();

        Assert.IsGreaterThan(1, versions.Count, "docs/upgrading.md has no 'Upgrading to x.y.z' sections.");
        CollectionAssert.AreEqual(
            versions.Order().ToList(), versions,
            $"docs/upgrading.md files its sections out of order ({string.Join(", ", versions)}). They run "
          + "from the oldest version to the newest, so the newest is last.");
    }

    /// <summary>
    /// The "before" clause of a change whose previous behaviour was a value: "where it rendered",
    /// "where it left them out". The loud verbs — it threw, failed or was refused — are left out.
    /// </summary>
    private static readonly Regex PriorValue =
        new(@"\bwhere (?:it|they) (?!threw\b|failed\b|refused\b|was refused\b|were refused\b)[a-z]+(?: [a-z]+)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Matches a reference-style link definition: <c>[5.5.0]: https://…</c>.</summary>
    private static readonly Regex ReferenceDefinition = new(@"^\[[^\]]+\]:\s+\S+$", RegexOptions.Compiled);

    /// <summary>Matches the target of an inline link: <c>](target)</c>.</summary>
    private static readonly Regex LinkTarget = new(@"\]\((?<target>[^)\s]*)\)", RegexOptions.Compiled);

    private static string UpgradingGuide => Path.Combine(RepositoryLayout.DocsDirectory, "upgrading.md");

    private static bool IsSectionHeading(string line, string name) =>
        line.StartsWith("### ", StringComparison.Ordinal) && line[4..].Trim() == name;

    /// <summary>The change lines under one <c>###</c> section of a version.</summary>
    private static IEnumerable<(int Number, string Line)> ChangesUnder(List<(int Number, string Line)> lines, string name)
    {
        var inside = false;

        foreach (var (number, line) in lines)
        {
            if (line.StartsWith("### ", StringComparison.Ordinal)) inside = IsSectionHeading(line, name);
            else if (inside && line.StartsWith("- ", StringComparison.Ordinal)) yield return (number, line);
        }
    }

    private static string Truncate(string line) => line.Length <= 60 ? line : line[..57] + "...";

    /// <summary>
    /// Each version section of every changelog: the lines after its <c>## [x.y.z]</c> heading up to the
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
                    version = heading.Success ? ReleaseVersion.Parse(heading.Groups["version"].Value) : null;
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
