using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// SECURITY.md promises fixes for the latest released minor of each supported line and tabulates
/// which those are. The table is prose: nothing ties it to <c>Directory.Build.props</c>, so a
/// release that bumps the version and forgets the table leaves the policy pointing a reporter at a
/// line that no longer receives fixes. A supported-versions table that is wrong is worse than none —
/// it reads as a considered statement — and it is exactly the kind of line a version bump forgets;
/// the sibling repository shipped 6.2.0 with the table still saying 6.1.x.
/// </summary>
/// <remarks>
/// Until the two-line policy the table was compared with <c>Version</c>, which names the version
/// being <em>prepared</em>, so opening a version meant promising fixes for a line that was not on
/// NuGet yet. Each row is now held to its line's latest release: this branch's from
/// <c>PackageValidationBaselineVersion</c>, the other line's from <c>CompanionLineRelease</c>
/// (CONTRIBUTING.md, Releases on two lines).
/// </remarks>
[TestClass]
public class SecurityPolicyConsistencyTests
{
    private static string SecurityPolicy => Path.Combine(RepositoryLayout.Root, "SECURITY.md");

    // "| 5.4.x | ✅ |" — a line that receives fixes, one row per supported line.
    private static readonly Regex SupportedRow =
        new(@"^\|\s*(?<major>\d+)\.(?<minor>\d+)\.x\s*\|\s*✅\s*\|", RegexOptions.Multiline | RegexOptions.Compiled);

    // "| < 5.4 | ❌ |" — everything before the oldest supported line.
    private static readonly Regex UnsupportedRow =
        new(@"^\|\s*<\s*(?<major>\d+)\.(?<minor>\d+)\s*\|\s*❌\s*\|", RegexOptions.Multiline | RegexOptions.Compiled);

    // "**10.x** targets **EF Core 10**" — the platform a line's support horizon is anchored to.
    private static readonly Regex LineTargets =
        new(@"\*\*(?<line>\d+)\.x\*\*[^\n]*?targets \*\*EF Core (?<efcore>\d+)\*\*", RegexOptions.Compiled);

    /// <summary>The most lines supported at once: the LTS line and the current one.</summary>
    private const int MaxSupportedLines = 2;

    /// <summary>
    /// Each supported line names the EF Core major it targets, and the line this branch prepares
    /// names the one in the core project's dependency floor.
    /// </summary>
    /// <remarks>
    /// The horizon is phrased against the platform, so when the floor moves to EF Core 11 the
    /// sentence has to move with it, or the policy promises support against a release the package
    /// no longer targets. Only this branch's line can be compared: the other line's floor is on the
    /// other branch. And only once that line has a row: a branch preparing a line's first release
    /// (main at 10.0.0 while 5.4.x is the supported row, or an 11.0.0 preview) targets a platform
    /// the table does not name yet, which is right until the release ships.
    /// </remarks>
    [TestMethod(DisplayName = "SECURITY.md names the EF Core major each supported line targets")]
    public void Every_supported_line_names_the_ef_core_major_it_targets()
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
              + "its support horizon — a consumer doing due diligence needs to know which platform each "
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
    /// The table has one supported row per line, each naming that line's latest release: this
    /// branch's from <c>PackageValidationBaselineVersion</c>, the other line's from
    /// <c>CompanionLineRelease</c>. The unsupported row meets the oldest of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The baseline names the release already out for as long as the next one is being prepared,
    /// and it has to move when a version is opened, so the table moves with it. The commit that gets
    /// tagged still names the release before it; opening the next version straight after the tag
    /// closes that gap.
    /// </para>
    /// <para>
    /// The table gains its second row when 11.0.0 ships, not when a preview does — a preview is
    /// fixed by the next preview, and SECURITY.md says so rather than giving it a row. Neither
    /// property may name a preview for the same reason.
    /// </para>
    /// <para>
    /// The limit: <c>CompanionLineRelease</c> is this branch's record of the other line, so a minor
    /// shipped there and not yet recorded here passes on this branch. The other branch's own run
    /// checks that row against its own baseline.
    /// </para>
    /// </remarks>
    [TestMethod(DisplayName = "SECURITY.md's supported-versions table names the latest release of each line")]
    public void Supported_versions_table_names_the_latest_release_of_each_line()
    {
        var text = File.ReadAllText(SecurityPolicy);
        var own  = ReleaseVersion.Parse(RepositoryLayout.LatestRelease);

        Assert.IsFalse(own.IsPrerelease, $"PackageValidationBaselineVersion is the preview {own}; a preview is never a supported line.");

        var lines = new List<ReleaseVersion> { own };

        if (RepositoryLayout.CompanionLineRelease is { } companionText)
        {
            var companion = ReleaseVersion.Parse(companionText);

            Assert.IsFalse(companion.IsPrerelease, $"CompanionLineRelease is the preview {companion}; a preview is never a supported line.");
            Assert.AreEqual(
                1, Math.Abs(companion.Major - own.Major),
                $"CompanionLineRelease is {companion} and this line's latest release is {own}. The two "
              + "supported lines are adjacent majors, one per EF Core major (CONTRIBUTING.md, Branches and releases).");

            lines.Add(companion);
        }

        var expected = lines.OrderByDescending(version => version)
                            .Select(version => (version.Major, version.Minor))
                            .ToList();
        var rows     = SupportedRows(text);

        Assert.IsLessThanOrEqualTo(
            MaxSupportedLines, rows.Count,
            $"SECURITY.md has {rows.Count} '| x.y.x | ✅ |' rows; at most {MaxSupportedLines} lines are supported at once.");

        CollectionAssert.AreEqual(
            expected, rows,
            $"SECURITY.md lists {Describe(rows)} as supported, but the latest release of each line is "
          + $"{Describe(expected)} (PackageValidationBaselineVersion {own}"
          + (RepositoryLayout.CompanionLineRelease is { } c ? $", CompanionLineRelease {c}" : ", no CompanionLineRelease")
          + "), newest first. A reporter reads the table to decide whether their version still receives "
          + "fixes, so it names what is on NuGet, not the version being prepared "
          + $"({RepositoryLayout.ShippedVersion}). Move a row when its line's release moves.");

        var unsupported = UnsupportedRow.Match(text);

        Assert.IsTrue(
            unsupported.Success,
            "SECURITY.md should have a '| < x.y | ❌ |' row saying that everything before the oldest "
          + "supported line is unmaintained.");

        var oldest           = expected[^1];
        var unsupportedBelow = (int.Parse(unsupported.Groups["major"].Value, CultureInfo.InvariantCulture),
                                int.Parse(unsupported.Groups["minor"].Value, CultureInfo.InvariantCulture));

        Assert.AreEqual(
            oldest, unsupportedBelow,
            $"SECURITY.md says versions below {unsupportedBelow.Item1}.{unsupportedBelow.Item2} are unsupported, "
          + $"but the oldest supported line is {oldest.Major}.{oldest.Minor}.x — the two rows should meet at "
          + "the same minor, or a range is left described by neither.");
    }

    private static List<(int Major, int Minor)> SupportedRows(string text) =>
        [.. SupportedRow.Matches(text)
                        .Select(match => (int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
                                          int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture)))];

    private static string Describe(IEnumerable<(int Major, int Minor)> rows) =>
        string.Join(" and ", rows.Select(row => $"{row.Major}.{row.Minor}.x")) is { Length: > 0 } listed ? listed : "nothing";
}
