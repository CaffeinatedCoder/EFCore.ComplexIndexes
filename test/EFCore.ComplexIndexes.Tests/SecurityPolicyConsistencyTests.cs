using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// SECURITY.md promises fixes for "the latest released minor version" and tabulates which line that
/// is. The table is prose: nothing ties it to <c>Directory.Build.props</c>, so a release that bumps
/// the version and forgets the table leaves the policy pointing a reporter at a line that no longer
/// receives fixes. A supported-versions table that is wrong is worse than none — it reads as a
/// considered statement — and it is exactly the kind of line a version bump forgets.
/// </summary>
[TestClass]
public class SecurityPolicyConsistencyTests
{
    private static string SecurityPolicy => Path.Combine(RepositoryLayout.Root, "SECURITY.md");

    // "| 5.0.x | ✅ |" — the one line that receives fixes.
    private static readonly Regex SupportedRow =
        new(@"^\|\s*(\d+)\.(\d+)\.x\s*\|\s*✅\s*\|", RegexOptions.Multiline | RegexOptions.Compiled);

    // "| < 5.0 | ❌ |" — everything before it.
    private static readonly Regex UnsupportedRow =
        new(@"^\|\s*<\s*(\d+)\.(\d+)\s*\|\s*❌\s*\|", RegexOptions.Multiline | RegexOptions.Compiled);

    private static Version PackageVersion =>
        Version.Parse(XDocument.Load(RepositoryLayout.BuildProps)
                               .Descendants("Version")
                               .Single()
                               .Value);

    [TestMethod(DisplayName = "SECURITY.md's supported-versions table names the minor being shipped")]
    public void Supported_versions_table_names_the_shipped_minor()
    {
        var text     = File.ReadAllText(SecurityPolicy);
        var shipped  = PackageVersion;
        var expected = $"{shipped.Major}.{shipped.Minor}";

        var supported = SupportedRow.Matches(text);

        Assert.HasCount(
            1, supported,
            "SECURITY.md should have exactly one '| x.y.x | ✅ |' row — the policy is that fixes land "
          + "on the latest released minor only, so there is one supported line to name.");

        var supportedLine = $"{supported[0].Groups[1].Value}.{supported[0].Groups[2].Value}";

        Assert.AreEqual(
            expected, supportedLine,
            $"SECURITY.md lists {supportedLine}.x as the supported line, but Directory.Build.props ships "
          + $"{shipped}. The table is part of the version bump: a reporter reads it to decide whether "
          + "their version still receives fixes.");

        var unsupported = UnsupportedRow.Match(text);

        Assert.IsTrue(
            unsupported.Success,
            "SECURITY.md should have a '| < x.y | ❌ |' row saying that everything before the supported "
          + "line is unmaintained.");

        var unsupportedBelow = $"{unsupported.Groups[1].Value}.{unsupported.Groups[2].Value}";

        Assert.AreEqual(
            expected, unsupportedBelow,
            $"SECURITY.md says versions below {unsupportedBelow} are unsupported, but the supported line "
          + $"is {expected}.x — the two rows should meet at the same minor, or a range is left "
          + "described by neither.");
    }
}
