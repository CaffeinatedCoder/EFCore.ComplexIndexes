using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Locates the repository and the shipping projects inside it, for tests that assert on repository
/// conventions (packaging metadata, changelogs, design-time targets) rather than on runtime
/// behaviour.
/// </summary>
/// <remarks>
/// The root is found by walking up to the solution file rather than by counting directories from
/// the test binaries. A positional walk silently retargets when the project moves — which is exactly
/// what happened when these projects were relocated under <c>test/</c>, turning a real assertion
/// into a file-not-found failure.
/// </remarks>
internal static class RepositoryLayout
{
    public static string Root { get; } = FindRoot();

    public static string SourceDirectory => Path.Combine(Root, "src");

    public static string RootReadme => Path.Combine(Root, "README.md");

    public static string RootChangelog => Path.Combine(Root, "CHANGELOG.md");

    public static string DocsDirectory => Path.Combine(Root, "docs");

    public static string BuildProps => Path.Combine(Root, "Directory.Build.props");

    /// <summary>The single source of truth for the version all packages ship at, from <c>Directory.Build.props</c>.</summary>
    public static string ShippedVersion =>
        SharedProperty("Version") ?? throw new InvalidOperationException("Directory.Build.props has no <Version>.");

    /// <summary>
    /// The latest release of this branch's line on nuget.org: <c>PackageValidationBaselineVersion</c>.
    /// Between a version bump and its tag, <see cref="ShippedVersion"/> names the release being
    /// prepared and the baseline names the one already out; <c>PackagingConventionTests</c> keeps the
    /// baseline at that release or the shipped version. When the baseline is empty — a package id
    /// with no release under it yet, as at the rename — the newest stable version the root changelog
    /// documents below the shipped one stands in, which is the same release.
    /// </summary>
    public static string LatestRelease =>
        SharedProperty("PackageValidationBaselineVersion")
     ?? Regex.Matches(File.ReadAllText(RootChangelog), $@"^## ({ReleaseVersion.Pattern})\s*$", RegexOptions.Multiline)
             .Select(match => ReleaseVersion.Parse(match.Groups[1].Value))
             .Where(version => !version.IsPrerelease && version < ReleaseVersion.Parse(ShippedVersion))
             .DefaultIfEmpty()
             .Max()
            ?.ToString()
     ?? throw new InvalidOperationException(
            "Directory.Build.props has no PackageValidationBaselineVersion and CHANGELOG.md documents no "
          + "stable release below the shipped version, so the latest release of this line is unknown.");

    /// <summary>
    /// The latest release of the other supported line, from <c>CompanionLineRelease</c>, or
    /// <see langword="null"/> while only one line has a release. No branch can read the other
    /// branch's props, so this is the one place a branch records what the other line last shipped.
    /// </summary>
    public static string? CompanionLineRelease => SharedProperty("CompanionLineRelease");

    private static string? SharedProperty(string name)
    {
        var value = XDocument.Load(BuildProps).Descendants(name).FirstOrDefault()?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Every packable project under <c>src/</c>, discovered rather than hard-coded.</summary>
    public static IReadOnlyList<ShippingProject> ShippingProjects { get; } =
    [
        .. Directory.EnumerateDirectories(SourceDirectory)
                    .Select(directory => new ShippingProject(Path.GetFileName(directory), directory))
                    .Where(project => File.Exists(project.ProjectFile))
                    .OrderBy(project => project.PackageId, StringComparer.Ordinal)
    ];

    public sealed record ShippingProject(string PackageId, string Directory)
    {
        public string ProjectFile => Path.Combine(Directory, $"{PackageId}.csproj");
        public string Readme      => Path.Combine(Directory, "README.md");
        public string Changelog   => Path.Combine(Directory, "CHANGELOG.md");
        public string Targets     => Path.Combine(Directory, "build", $"{PackageId}.targets");

        /// <summary>True for the provider satellites, false for the provider-agnostic core.</summary>
        public bool IsSatellite => PackageId != "EFCore.ComplexIndexes";

        public override string ToString() => PackageId;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);

        while (directory is not null && !directory.EnumerateFiles("*.slnx").Any())
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root: no .slnx in any parent directory.");
    }
}
