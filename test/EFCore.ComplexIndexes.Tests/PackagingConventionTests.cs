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
