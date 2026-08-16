using System.Reflection;

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
