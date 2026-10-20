using System.IO.Compression;
using System.Xml.Linq;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// A satellite's nuspec pins the core to the version it was built with (#169).
/// </summary>
/// <remarks>
/// A ProjectReference packs as <c>&gt;=</c>, and the satellites bind to the core's internals, so any
/// later core satisfied them while breaking them: a consumer who updated only a direct core
/// reference ran a 5.4.1 satellite against core 5.5.0, and NuGet said nothing. The pin is set by a
/// target in <c>src/Directory.Build.targets</c> that rewrites an item of NuGet's own, and a target
/// that stops matching still packs a valid package, so this packs for real and reads the nuspec.
/// </remarks>
public partial class PackagingConventionTests
{
    // Every packing test writes the same obj/ nuspec, so they take turns.
    [TestMethod(DisplayName = "Each satellite's package depends on exactly the core version it was built with")]
    [DoNotParallelize]
    public void Satellites_pin_the_core_to_their_own_version()
    {
        var version = RepositoryLayout.ShippedVersion;
        var output  = Directory.CreateTempSubdirectory("dependencies-").FullName;

        try
        {
            foreach (var project in Projects.Where(p => p.IsSatellite))
            {
                var (exitCode, log) = DotNet(
                    "pack", project.ProjectFile, "--no-build", "-c", TestConfiguration, "-o", output,
                    "-p:EnablePackageValidation=false");

                Assert.AreEqual(0, exitCode, $"Packing {project} failed:\n{log}");

                Assert.AreEqual(
                    $"[{version}]",
                    PackedDependencyRange(Path.Combine(output, $"{project.PackageId}.{version}.nupkg"), Core.PackageId),
                    $"{project}'s package does not pin {Core.PackageId} to [{version}]. It binds to the core's internals, "
                  + "so a range lets NuGet run it against a core it was never built with. The target that pins it, "
                  + "PinProjectReferenceDependencies, is in src/Directory.Build.targets.");
            }
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    private static string PackedDependencyRange(string package, string dependency)
    {
        Assert.IsTrue(File.Exists(package), $"No package at {package}.");

        using var archive = ZipFile.OpenRead(package);
        using var stream  = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();

        var ranges = XDocument.Load(stream)
                              .Descendants()
                              .Where(e => e.Name.LocalName == "dependency" && (string?)e.Attribute("id") == dependency)
                              .Select(e => (string?)e.Attribute("version") ?? "(no version)")
                              .Distinct()
                              .ToList();

        Assert.HasCount(1, ranges, $"{package} declares {dependency} {ranges.Count} ways: {string.Join(", ", ranges)}.");
        return ranges[0];
    }
}
