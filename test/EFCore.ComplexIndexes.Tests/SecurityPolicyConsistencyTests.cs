using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// SECURITY.md promises fixes for the latest released minor of the line this repository ships and
/// tabulates which that is. The table is prose: nothing ties it to <c>Directory.Build.props</c>, so a
/// release that bumps the version and forgets the table leaves the policy pointing a reporter at a
/// line that no longer receives fixes. A supported-versions table that is wrong is worse than none —
/// it reads as a considered statement — and it is exactly the kind of line a version bump forgets;
/// the sibling repository shipped 6.2.0 with the table still saying 6.1.x.
/// </summary>
/// <remarks>
/// <para>
/// Until 5.5.0 the table was compared with <c>Version</c>, which names the version being
/// <em>prepared</em>, so opening a version meant promising fixes for a line that was not on NuGet
/// yet. The row is now held to the latest release, <c>PackageValidationBaselineVersion</c>
/// (CONTRIBUTING.md, Releases).
/// </para>
/// <para>
/// One row, and no more. From the <c>v5.5.0</c> tag until 2026-11-11 the plan was a second line
/// here, SchemaKit on EF Core 11, with a row of its own read from a <c>CompanionLineRelease</c>
/// property. SchemaKit has its own repository and its own policy now (#211), so a second supported
/// row would promise fixes for a package this repository does not ship.
/// </para>
/// </remarks>
[TestClass]
public class SecurityPolicyConsistencyTests
{
    private static string SecurityPolicy => Path.Combine(RepositoryLayout.Root, "SECURITY.md");

    // "| 5.4.x | ✅ |" — the line that receives fixes.
    private static readonly Regex SupportedRow =
        new(@"^\|\s*(?<major>\d+)\.(?<minor>\d+)\.x\s*\|\s*✅\s*\|", RegexOptions.Multiline | RegexOptions.Compiled);

    // "| < 5.4 | ❌ |" — everything before the supported minor.
    private static readonly Regex UnsupportedRow =
        new(@"^\|\s*<\s*(?<major>\d+)\.(?<minor>\d+)\s*\|\s*❌\s*\|", RegexOptions.Multiline | RegexOptions.Compiled);

    // "**5.x** targets **EF Core 10**" — the platform the line's support horizon is anchored to.
    private static readonly Regex LineTargets =
        new(@"\*\*(?<line>\d+)\.x\*\*[^\n]*?targets \*\*EF Core (?<efcore>\d+)\*\*", RegexOptions.Compiled);

    /// <summary>
    /// The supported line names the EF Core major it targets, and that is the one in the core
    /// project's dependency floor.
    /// </summary>
    /// <remarks>
    /// The horizon is phrased against the platform, so if the floor ever moved the sentence would
    /// have to move with it, or the policy promises support against a release the package no longer
    /// targets. The floor is compared only once the version being prepared has a row: a branch
    /// preparing a major's first release targets a platform the table does not name yet, which is
    /// right until the release ships.
    /// </remarks>
    [TestMethod(DisplayName = "SECURITY.md names the EF Core major the supported line targets")]
    public void Supported_line_names_the_ef_core_major_it_targets()
    {
        var text    = File.ReadAllText(SecurityPolicy);
        var targets = LineTargets.Matches(text)
                                 .GroupBy(match => int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture))
                                 .ToDictionary(group => group.Key, group => group.First().Groups["efcore"].Value);
        var rows    = SupportedRows(text);

        Assert.IsNotEmpty(rows, "SECURITY.md has no '| x.y.x | ✅ |' row, so no line was checked.");

        foreach (var (major, _) in rows)
        {
            Assert.IsTrue(
                targets.ContainsKey(major),
                $"SECURITY.md supports {major}.x but has no '**{major}.x** targets **EF Core N**' sentence in "
              + "its support horizon — a consumer doing due diligence needs to know which platform the "
              + "line's support is tied to.");
        }

        var shipped = ReleaseVersion.Parse(RepositoryLayout.ShippedVersion);

        if (shipped.IsPrerelease || rows.All(row => row.Major != shipped.Major)) return;

        var core      = RepositoryLayout.ShippingProjects.Single(p => !p.IsSatellite);
        var reference = XDocument.Load(core.ProjectFile)
                                 .Descendants("PackageReference")
                                 .Single(r => r.Attribute("Include")?.Value == "Microsoft.EntityFrameworkCore.Abstractions");
        var floor     = Regex.Match(reference.Attribute("Version")!.Value, @"\d+").Value;

        Assert.AreEqual(
            floor, targets[shipped.Major],
            $"SECURITY.md says {shipped.Major}.x targets EF Core {targets[shipped.Major]}, but this branch "
          + $"prepares {shipped} and the core project's EF Core floor is {floor}. Move the horizon statement "
          + "with the floor.");
    }

    /// <summary>
    /// The table has exactly one supported row, naming the latest release from
    /// <c>PackageValidationBaselineVersion</c>, and the unsupported row meets it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The baseline names the release already out for as long as the next one is being prepared,
    /// and it has to move when a version is opened, so the table moves with it. The commit that gets
    /// tagged still names the release before it; opening the next version straight after the tag
    /// closes that gap.
    /// </para>
    /// <para>
    /// A preview is never the supported row, so the baseline may not name one. A second row fails
    /// whatever it names: the only other line there has been a plan for, SchemaKit, states its
    /// supported versions in its own repository (#211).
    /// </para>
    /// </remarks>
    [TestMethod(DisplayName = "SECURITY.md's supported-versions table names the latest release, in its one supported row")]
    public void Supported_versions_table_names_the_latest_release_in_one_row()
    {
        var text   = File.ReadAllText(SecurityPolicy);
        var latest = ReleaseVersion.Parse(RepositoryLayout.LatestRelease);

        Assert.IsFalse(latest.IsPrerelease, $"PackageValidationBaselineVersion is the preview {latest}; a preview is never a supported line.");

        var expected = new List<(int Major, int Minor)> { (latest.Major, latest.Minor) };
        var rows     = SupportedRows(text);

        Assert.IsLessThanOrEqualTo(
            1, rows.Count,
            $"SECURITY.md has {rows.Count} '| x.y.x | ✅ |' rows ({Describe(rows)}). This repository ships one "
          + "line, so one minor receives fixes; SchemaKit's supported versions are in its own repository's "
          + "policy (CONTRIBUTING.md, Branches and releases).");

        CollectionAssert.AreEqual(
            expected, rows,
            $"SECURITY.md lists {Describe(rows)} as supported, but the latest release is "
          + $"{Describe(expected)} (PackageValidationBaselineVersion {latest}). A reporter reads the table "
          + "to decide whether their version still receives fixes, so it names what is on NuGet, not the "
          + $"version being prepared ({RepositoryLayout.ShippedVersion}). Move the row when the baseline moves.");

        var unsupported = UnsupportedRow.Match(text);

        Assert.IsTrue(
            unsupported.Success,
            "SECURITY.md should have a '| < x.y | ❌ |' row saying that everything before the "
          + "supported minor is unmaintained.");

        var supported        = expected[0];
        var unsupportedBelow = (int.Parse(unsupported.Groups["major"].Value, CultureInfo.InvariantCulture),
                                int.Parse(unsupported.Groups["minor"].Value, CultureInfo.InvariantCulture));

        Assert.AreEqual(
            supported, unsupportedBelow,
            $"SECURITY.md says versions below {unsupportedBelow.Item1}.{unsupportedBelow.Item2} are unsupported, "
          + $"but the supported minor is {supported.Major}.{supported.Minor}.x — the two rows should meet at "
          + "the same minor, or a range is left described by neither.");
    }

    private static List<(int Major, int Minor)> SupportedRows(string text) =>
        [.. SupportedRow.Matches(text)
                        .Select(match => (int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
                                          int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture)))];

    private static string Describe(IEnumerable<(int Major, int Minor)> rows) =>
        string.Join(" and ", rows.Select(row => $"{row.Major}.{row.Minor}.x")) is { Length: > 0 } listed ? listed : "nothing";
}
