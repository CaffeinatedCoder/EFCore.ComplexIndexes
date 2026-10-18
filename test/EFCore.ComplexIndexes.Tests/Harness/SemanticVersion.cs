using System.Text.RegularExpressions;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// A package version as <c>Directory.Build.props</c> and the changelog headings spell it:
/// <c>5.4.1</c> or <c>11.0.0-preview.1</c>. <see cref="Version"/> rejects the prerelease suffix,
/// and the repository-convention tests parse every version they compare, so the first preview
/// of a new line crashed all of them with a <see cref="FormatException"/> instead of a finding.
/// </summary>
/// <remarks>
/// Ordering follows SemVer 2.0 §11: the release part first, then a release above any prerelease
/// of the same release part, then the dot-separated prerelease identifiers, numeric ones compared
/// as numbers. That is also NuGet's order, which is what "newer than the one being shipped" and
/// "the release before it" mean in practice.
/// </remarks>
internal sealed record SemanticVersion(Version Release, string? Prerelease) : IComparable<SemanticVersion>
{
    // "5.4.1" or "11.0.0-preview.1": three numeric parts, optionally a hyphen and identifiers.
    private static readonly Regex Pattern =
        new(@"^(\d+\.\d+\.\d+)(?:-([0-9A-Za-z.-]+))?$", RegexOptions.Compiled);

    /// <summary>
    /// The one changelog heading style, <c>## 5.4.1</c> or <c>## 11.0.0-preview.1</c>. Deliberately
    /// strict: <c>release.yml</c> extracts the release notes by matching <c>## &lt;version&gt;</c>
    /// literally, so any heading this does not match is one the release job cannot find either.
    /// </summary>
    public static readonly Regex ChangelogHeading =
        new(@"^\#\# (\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    public bool IsPrerelease => Prerelease is not null;

    public int Major => Release.Major;

    public int Minor => Release.Minor;

    public static SemanticVersion Parse(string text)
    {
        var match = Pattern.Match(text.Trim());

        if (!match.Success)
            throw new FormatException($"'{text}' is not a package version of the form x.y.z or x.y.z-suffix.");

        return new SemanticVersion(Version.Parse(match.Groups[1].Value),
                                   match.Groups[2].Success ? match.Groups[2].Value : null);
    }

    /// <summary>Every <c>## x.y.z</c> heading in a changelog, in document order.</summary>
    public static List<SemanticVersion> InChangelog(string path) =>
        [.. ChangelogHeading.Matches(File.ReadAllText(path)).Select(m => Parse(m.Groups[1].Value))];

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
            return 1;

        var byRelease = Release.CompareTo(other.Release);
        if (byRelease != 0)
            return byRelease;

        if (Prerelease is null || other.Prerelease is null)
            return (Prerelease is null ? 1 : 0) - (other.Prerelease is null ? 1 : 0);

        var mine   = Prerelease.Split('.');
        var theirs = other.Prerelease.Split('.');

        for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            var result = int.TryParse(mine[i], out var a) & int.TryParse(theirs[i], out var b)
                ? a.CompareTo(b)
                : string.CompareOrdinal(mine[i], theirs[i]);

            if (result != 0)
                return result;
        }

        return mine.Length.CompareTo(theirs.Length);
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        Prerelease is null ? Release.ToString(3) : $"{Release.ToString(3)}-{Prerelease}";
}
