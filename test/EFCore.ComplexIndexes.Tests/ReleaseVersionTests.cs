namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// <see cref="ReleaseVersion"/> is what every convention test orders versions by, so a mistake in it
/// would misreport every changelog as out of order — or, worse, in order.
/// </summary>
[TestClass]
public class ReleaseVersionTests
{
    [TestMethod(DisplayName = "Precedence follows SemVer: prerelease before release, numeric identifiers by value")]
    public void Precedence_is_semver()
    {
        var ordered = new[] { "10.0.0", "10.0.1", "10.1.0", "11.0.0-preview.1", "11.0.0-preview.2", "11.0.0-preview.10", "11.0.0-rc.1", "11.0.0" }
                     .Select(ReleaseVersion.Parse)
                     .ToList();

        var shuffled = ordered.AsEnumerable().Reverse().OrderBy(v => v.ToString().Length).ToList();

        CollectionAssert.AreEqual(ordered, shuffled.Order().ToList());
        Assert.IsTrue(ReleaseVersion.Parse("11.0.0-preview.1") < ReleaseVersion.Parse("11.0.0"));
        Assert.IsTrue(ReleaseVersion.Parse("11.0.0-preview.10") > ReleaseVersion.Parse("11.0.0-preview.9"));
        Assert.AreEqual(ReleaseVersion.Parse("5.4.1"), ReleaseVersion.Parse(" 5.4.1 "));
    }

    [TestMethod(DisplayName = "A minor is x.y.0 with y > 0 and no prerelease")]
    public void Minor_is_x_y_0()
    {
        Assert.IsTrue(ReleaseVersion.Parse("10.1.0").IsMinor);
        Assert.IsFalse(ReleaseVersion.Parse("10.0.0").IsMinor);
        Assert.IsFalse(ReleaseVersion.Parse("10.1.1").IsMinor);
        Assert.IsFalse(ReleaseVersion.Parse("10.1.0-preview.1").IsMinor);
    }

    [TestMethod(DisplayName = "Anything else is refused")]
    public void Other_text_is_refused()
    {
        Assert.ThrowsExactly<FormatException>(() => ReleaseVersion.Parse("10.1"));
        Assert.ThrowsExactly<FormatException>(() => ReleaseVersion.Parse("v10.1.0"));
        Assert.ThrowsExactly<FormatException>(() => ReleaseVersion.Parse("10.1.0+build"));
    }
}
