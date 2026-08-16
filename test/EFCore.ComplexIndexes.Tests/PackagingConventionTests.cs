using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Packaging conventions every shipping project must follow. These are invisible at compile time and
/// at test time — a missing <c>PackageReadmeFile</c>, a <c>.targets</c> that ships to <c>build/</c>
/// but not <c>buildTransitive/</c>, or a satellite whose design-time attribute is not scoped to its
/// provider all produce a package that builds fine and misbehaves only once a consumer installs it.
/// </summary>
[TestClass]
public class PackagingConventionTests
{
    private static IEnumerable<RepositoryLayout.ShippingProject> Projects => RepositoryLayout.ShippingProjects;

    [TestMethod(DisplayName = "Every shipping project is discovered under src/")]
    public void Shipping_projects_are_discovered()
    {
        // Guards the discovery itself: if src/ is restructured again, the tests below must not
        // silently pass by iterating an empty list.
        Assert.HasCount(3, Projects.ToList(), $"Found: {string.Join(", ", Projects)}");
    }

    [TestMethod(DisplayName = "Every package ships its own README as the NuGet landing page")]
    public void Every_package_ships_its_own_readme()
    {
        foreach (var project in Projects)
        {
            Assert.IsTrue(File.Exists(project.Readme), $"{project} has no README.md next to its project file.");

            var csproj = XDocument.Load(project.ProjectFile);

            Assert.AreEqual(
                "README.md",
                csproj.Descendants("PackageReadmeFile").SingleOrDefault()?.Value,
                $"{project} must declare <PackageReadmeFile>README.md</PackageReadmeFile>, or NuGet shows no description.");

            Assert.IsTrue(
                Packs(csproj, "README.md", @"\"),
                $"{project} declares PackageReadmeFile but never packs README.md — packing fails with NU5019.");
        }
    }

    [TestMethod(DisplayName = "Design-time targets ship to both build/ and buildTransitive/")]
    public void Targets_ship_to_both_locations()
    {
        foreach (var project in Projects)
        {
            Assert.IsTrue(File.Exists(project.Targets), $"{project} has no build/{project.PackageId}.targets.");

            var csproj   = XDocument.Load(project.ProjectFile);
            var relative = $"build/{project.PackageId}.targets";

            // build/ applies to direct references; buildTransitive/ carries the attribute through a
            // project that references the package on the consumer's behalf. Both are needed.
            Assert.IsTrue(Packs(csproj, relative, "build/"),   $"{project} does not pack its targets to build/.");
            Assert.IsTrue(Packs(csproj, relative, "buildTransitive/"), $"{project} does not pack its targets to buildTransitive/.");
        }
    }

    [TestMethod(DisplayName = "Design-time targets reference a real IDesignTimeServices in their own package")]
    public void Targets_reference_a_real_design_time_services_type()
    {
        foreach (var project in Projects)
        {
            var parameter = DesignTimeAttribute(project).Elements()
                                                        .Single(e => e.Name.LocalName == "_Parameter1")
                                                        .Value;

            var parts = parameter.Split(',', 2, StringSplitOptions.TrimEntries);
            Assert.HasCount(2, parts, $"{project}'s _Parameter1 must be 'Namespace.Type, AssemblyName'.");
            Assert.AreEqual(project.PackageId, parts[1], $"{project}'s targets must point at its own assembly.");

            var type = Type.GetType(parameter);
            Assert.IsNotNull(type, $"{project}'s targets reference '{parts[0]}', which does not exist.");
            Assert.IsTrue(
                typeof(Microsoft.EntityFrameworkCore.Design.IDesignTimeServices).IsAssignableFrom(type),
                $"{parts[0]} must implement IDesignTimeServices.");
        }
    }

    [TestMethod(DisplayName = "Satellites scope their design-time services to a provider; the core does not")]
    public void Only_satellites_declare_a_provider()
    {
        foreach (var project in Projects)
        {
            var forProvider = DesignTimeAttribute(project).Elements()
                                                          .SingleOrDefault(e => e.Name.LocalName == "_Parameter2")
                                                          ?.Value;

            if (project.IsSatellite)
                Assert.IsNotNull(
                    forProvider,
                    $"{project} must set _Parameter2 (ForProvider). Without it, EF applies this satellite's "
                  + "differ to every provider, so a solution using two providers gets whichever the restore "
                  + "order happened to register last.");
            else
                Assert.IsNull(
                    forProvider,
                    $"{project} is provider-agnostic and must not declare ForProvider.");
        }
    }

    [TestMethod(DisplayName = "The SBOM exclude filter covers every build-only package reference")]
    public void Sbom_filter_covers_every_private_reference()
    {
        // The published SBOMs describe what a consumer takes on, so references marked
        // PrivateAssets=all — which never reach consumers — are filtered out of them. Add another
        // such reference without extending the filter and the SBOM silently gains that package's
        // whole subtree, telling consumers they depend on code they never receive.
        var buildOnly = Projects
                       .SelectMany(project => XDocument.Load(project.ProjectFile)
                                                       .Descendants("PackageReference")
                                                       .Where(IsPrivate)
                                                       .Select(r => r.Attribute("Include")!.Value))
                       .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.IsNotEmpty(buildOnly, "Expected at least one PrivateAssets=all reference — has the packaging changed?");

        var filtered = Regex.Matches(File.ReadAllText(ReleaseWorkflow), @"--exclude-filter\s+(\S+)")
                            .SelectMany(m => m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unfiltered = buildOnly.Except(filtered).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.IsEmpty(
            unfiltered,
            $"{string.Join(", ", unfiltered)} is referenced with PrivateAssets=all but is not in the "
          + "SBOM --exclude-filter in release.yml, so the generated SBOM would list dependencies "
          + "consumers never receive.");
    }

    [TestMethod(DisplayName = "A reference marked build-only in one project is build-only in every project")]
    public void Build_only_references_are_private_in_every_project()
    {
        // The test above flattens all three projects into one set of ids, so it only ever asks
        // whether a name is *somewhere* private. Drop PrivateAssets=all from a single csproj and the
        // other two keep that name in the set: the SBOM test stays green while that one package's
        // nuspec starts declaring the dependency, and its consumers restore the whole subtree behind
        // it. For Microsoft.EntityFrameworkCore.Design that is ~45 MSBuild/Roslyn components — and
        // "consumers never receive it" is the stated reason NU1903 stays a warning
        // (Directory.Build.props) and the reason .github/dependabot.yml ignores
        // System.Security.Cryptography.Xml under src/. Nothing else checks it per project.
        var references = Projects
                        .Select(project => (
                             project,
                             refs: XDocument.Load(project.ProjectFile)
                                            .Descendants("PackageReference")
                                            .Where(reference => reference.Attribute("Include") is not null)
                                            .Select(reference => (Id: reference.Attribute("Include")!.Value,
                                                                  IsBuildOnly: IsPrivate(reference)))
                                            .ToList()))
                        .ToList();

        var buildOnly = references.SelectMany(entry => entry.refs)
                                  .Where(reference => reference.IsBuildOnly)
                                  .Select(reference => reference.Id)
                                  .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.IsNotEmpty(buildOnly, "Expected at least one PrivateAssets=all reference — has the packaging changed?");

        var shipped = references
                     .SelectMany(entry => entry.refs
                                               .Where(reference => !reference.IsBuildOnly && buildOnly.Contains(reference.Id))
                                               .Select(reference => $"{entry.project} -> {reference.Id}"))
                     .OrderBy(entry => entry, StringComparer.Ordinal)
                     .ToList();

        Assert.IsEmpty(
            shipped,
            $"{string.Join("; ", shipped)} — marked PrivateAssets=all elsewhere under src/ but not here, "
          + "so this package's nuspec declares it and its consumers restore that dependency's entire "
          + "subtree. Mark it PrivateAssets=all here too; if a consumer genuinely needs it at runtime, "
          + "make it public in every project and drop it from the SBOM --exclude-filter, because it is "
          + "then a dependency they really do take on.");
    }

    /// <summary>
    /// Package validation compares each pack against <c>PackageValidationBaselineVersion</c>. A
    /// baseline left behind stops seeing API added since it — a member introduced in 5.1 and
    /// removed in 5.2 is invisible to a 5.0 baseline — so it may be the version being shipped
    /// (between releases, when <c>Version</c> is the last release) or the release directly before
    /// it (once <c>Version</c> is bumped for the next one), and never older. That lags by at most
    /// one release and forces the move at every version bump.
    /// </summary>
    [TestMethod(DisplayName = "The package-validation baseline is the shipped version or the release before it")]
    public void Package_validation_baseline_is_the_shipped_version_or_the_release_before_it()
    {
        var props = XDocument.Load(RepositoryLayout.BuildProps);

        Assert.AreEqual(
            "true", props.Descendants("EnablePackageValidation").SingleOrDefault()?.Value.Trim(),
            "Directory.Build.props does not enable package validation, so a removed public member "
          + "packs cleanly and whether a release breaks anyone rests on reading the diff.");

        var shipped  = Version.Parse(props.Descendants("Version").Single().Value);
        var previous = ChangelogVersions().Where(version => version < shipped).DefaultIfEmpty().Max();
        var baseline = props.Descendants("PackageValidationBaselineVersion").SingleOrDefault()?.Value.Trim();

        Assert.IsNotNull(baseline, "Directory.Build.props sets no PackageValidationBaselineVersion.");

        var allowed = new[] { shipped, previous }.Where(version => version is not null).Distinct().ToList();

        Assert.IsTrue(
            allowed.Contains(Version.Parse(baseline)),
            $"PackageValidationBaselineVersion is {baseline}, but Directory.Build.props ships {shipped} "
          + $"and the release before it is {previous?.ToString() ?? "none"}. The baseline must be one of "
          + "those two — move it to the release just superseded when bumping Version, or API added "
          + "since the old baseline goes unvalidated.");
    }

    // The root README's "## What changed in x.y.z" headings — the same source ChangelogConsistencyTests reads.
    private static IEnumerable<Version> ChangelogVersions() =>
        Regex.Matches(File.ReadAllText(RepositoryLayout.RootReadme), @"^## What changed in (\d+\.\d+\.\d+)\s*$", RegexOptions.Multiline)
             .Select(match => Version.Parse(match.Groups[1].Value));

    private static string ReleaseWorkflow =>
        Path.Combine(RepositoryLayout.Root, ".github", "workflows", "release.yml");

    private static bool IsPrivate(XElement reference) =>
        string.Equals(reference.Element("PrivateAssets")?.Value ?? reference.Attribute("PrivateAssets")?.Value,
                      "all", StringComparison.OrdinalIgnoreCase);

    private static XElement DesignTimeAttribute(RepositoryLayout.ShippingProject project)
    {
        Assert.IsTrue(File.Exists(project.Targets), $"{project} has no build/{project.PackageId}.targets.");

        return XDocument.Load(project.Targets)
                        .Descendants()
                        .Single(e => e.Name.LocalName == "AssemblyAttribute");
    }

    private static bool Packs(XDocument csproj, string include, string packagePath) =>
        csproj.Descendants("None")
              .Any(n => string.Equals(n.Attribute("Include")?.Value?.Replace('\\', '/'), include.Replace('\\', '/'),
                                      StringComparison.OrdinalIgnoreCase)
                     && n.Attribute("Pack")?.Value == "true"
                     && n.Attribute("PackagePath")?.Value == packagePath);
}
