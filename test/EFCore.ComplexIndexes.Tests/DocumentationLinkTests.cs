using System.Text.RegularExpressions;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// The documentation is split across the root README, <c>docs/</c>, and one packed README per
/// package, which means it is now held together by links — and a broken link is the one defect that
/// renders perfectly. Markdown does not resolve anything at write time, so a page renamed or a
/// section re-levelled leaves a live link that 404s, or an anchor that silently scrolls nowhere.
/// </summary>
/// <remarks>
/// The packed READMEs carry a second, worse failure mode, which is why they are checked separately.
/// nuget.org renders <c>PackageReadmeFile</c> with no base URL to resolve against, so a relative
/// link that works in the repository is dead on the package page — for every consumer arriving the
/// way most consumers arrive. There is no warning at pack time and no way to see it without
/// publishing, so the rule here is absolute: those files link out with full URLs or not at all.
/// </remarks>
[TestClass]
public class DocumentationLinkTests
{
    // [text](target) — inline links only. Reference-style definitions are not used in this repository.
    private static readonly Regex MarkdownLink = new(@"\[[^\]]*\]\(([^)\s]+)(?:\s+""[^""]*"")?\)", RegexOptions.Compiled);

    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.*?)\s*#*$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Markdown this repository authors: the root pages, <c>docs/</c>, and the packed READMEs.</summary>
    private static IEnumerable<string> AuthoredMarkdown()
    {
        foreach (var file in Directory.EnumerateFiles(RepositoryLayout.Root, "*.md", SearchOption.TopDirectoryOnly))
            yield return file;

        if (Directory.Exists(RepositoryLayout.DocsDirectory))
            foreach (var file in Directory.EnumerateFiles(RepositoryLayout.DocsDirectory, "*.md", SearchOption.AllDirectories))
                yield return file;

        // A missing file is ChangelogConsistencyTests' finding to report, with a message that says so.
        // Reading it here would bury that behind a FileNotFoundException in four unrelated tests.
        foreach (var project in RepositoryLayout.ShippingProjects)
        {
            yield return project.Readme;

            if (File.Exists(project.Changelog))
                yield return project.Changelog;
        }
    }

    [TestMethod(DisplayName = "Relative links point at files that exist")]
    public void Relative_links_resolve()
    {
        var broken = new List<string>();

        foreach (var file in AuthoredMarkdown())
        {
            foreach (var target in LinkTargets(file))
            {
                var path = target.Split('#')[0];
                if (path.Length == 0)
                    continue;

                var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, Uri.UnescapeDataString(path)));

                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                    broken.Add($"{Relative(file)} → {target}");
            }
        }

        Assert.IsEmpty(
            broken,
            $"Links point at files that do not exist: {string.Join(", ", broken)}. "
          + "A page was moved or renamed and the links to it were not updated.");
    }

    [TestMethod(DisplayName = "Anchors point at headings that exist")]
    public void Anchors_resolve()
    {
        var broken = new List<string>();

        foreach (var file in AuthoredMarkdown())
        {
            foreach (var target in LinkTargets(file))
            {
                var parts = target.Split('#', 2);
                if (parts.Length != 2 || parts[1].Length == 0)
                    continue;

                // An anchor into a file this repository does not author cannot be checked here.
                var page = parts[0].Length == 0
                    ? file
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, Uri.UnescapeDataString(parts[0])));

                if (!File.Exists(page) || !page.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!Anchors(page).Contains(parts[1]))
                    broken.Add($"{Relative(file)} → {target}");
            }
        }

        Assert.IsEmpty(
            broken,
            $"Links point at anchors no heading produces: {string.Join(", ", broken)}. "
          + "A heading was reworded or re-levelled; the link still renders, it just goes nowhere.");
    }

    /// <summary>
    /// The packed READMEs are rendered by nuget.org, which has no base to resolve a relative path
    /// against. Nothing about packing or restoring notices, so the link is dead only where it
    /// matters most.
    /// </summary>
    [TestMethod(DisplayName = "Packed READMEs link out with absolute URLs only")]
    public void Packed_readmes_have_no_relative_links()
    {
        var relative = new List<string>();

        foreach (var project in RepositoryLayout.ShippingProjects)
            foreach (var target in AllLinkTargets(project.Readme))
                if (!target.StartsWith('#') && !Uri.IsWellFormedUriString(target, UriKind.Absolute))
                    relative.Add($"{project.PackageId}'s README → {target}");

        Assert.IsEmpty(
            relative,
            $"Packed READMEs carry relative links: {string.Join(", ", relative)}. nuget.org renders "
          + "PackageReadmeFile with no base URL, so these resolve to nothing for anyone arriving from "
          + "the package page. Use the full https://github.com/... URL instead.");
    }

    /// <summary>Every inline link target in the file, absolute ones included.</summary>
    private static IEnumerable<string> AllLinkTargets(string file) =>
        MarkdownLink.Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value);

    /// <summary>The subset this repository is responsible for resolving.</summary>
    private static IEnumerable<string> LinkTargets(string file) =>
        AllLinkTargets(file).Where(target => !Uri.IsWellFormedUriString(target, UriKind.Absolute));

    /// <summary>
    /// GitHub's heading slugs: lowercased, inline markdown stripped, everything but letters, digits,
    /// spaces, hyphens and underscores removed, spaces to hyphens. Repeats get a numeric suffix,
    /// which this repository has no need for and this deliberately does not model — a duplicate
    /// heading would show up here as an unresolvable anchor rather than silently passing.
    /// </summary>
    private static HashSet<string> Anchors(string file)
    {
        var anchors = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match heading in Heading.Matches(File.ReadAllText(file)))
        {
            var text = heading.Groups[2].Value;

            text = Regex.Replace(text, @"\[([^\]]*)\]\([^)]*\)", "$1");   // links keep their text
            text = text.Replace("`", string.Empty).Replace("*", string.Empty);

            var slug = new string(text.ToLowerInvariant()
                                      .Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')
                                      .Select(c => c == ' ' ? '-' : c)
                                      .ToArray());

            anchors.Add(slug);
        }

        return anchors;
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(RepositoryLayout.Root, path).Replace(Path.DirectorySeparatorChar, '/');
}
