using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// The release notes on nuget.org and in Visual Studio's Updates tab are the version's section of
/// each package's own <c>CHANGELOG.md</c>, set at pack by <c>src/Directory.Build.targets</c>.
/// </summary>
/// <remarks>
/// Nothing else notices when they go wrong: a pack that reads the wrong file, stops at the wrong
/// heading or loses its hook into <c>GenerateNuspec</c> still produces a valid package, and the
/// mistake is first seen on the package page after the release. So these tests pack for real and
/// read the nuspec out of the <c>.nupkg</c>, against a renderer of their own written line by line
/// rather than with the target's regular expressions.
/// </remarks>
public partial class PackagingConventionTests
{
    private const string ReleaseNotesTarget = "SetPackageReleaseNotesFromChangelog";

    // Both packing tests write the same obj/ nuspec, so they take turns.
    [TestMethod(DisplayName = "Each package's release notes are its changelog's section for the shipped version, and its README links the docs at the tag")]
    [DoNotParallelize]
    public void Packed_release_notes_are_the_versions_changelog_section()
    {
        var version = RepositoryLayout.ShippedVersion;
        var output  = Directory.CreateTempSubdirectory("release-notes-").FullName;

        try
        {
            foreach (var project in Projects)
            {
                // --no-build packs what the test run already built, in its configuration. Package
                // validation is CI's pack job's concern and needs the baseline from nuget.org.
                var (exitCode, log) = DotNet(
                    "pack", project.ProjectFile, "--no-build", "-c", TestConfiguration, "-o", output,
                    "-p:EnablePackageValidation=false");

                Assert.AreEqual(0, exitCode, $"Packing {project} failed:\n{log}");

                var expected = ExpectedReleaseNotes(File.ReadAllText(project.Changelog), version, project.PackageId);
                Assert.IsNotNull(expected, $"{project.Changelog} has no section for {version}.");

                var package = Path.Combine(output, $"{project.PackageId}.{version}.nupkg");

                Assert.AreEqual(
                    expected,
                    PackedReleaseNotes(package),
                    $"{project}'s packed release notes are not the {version} section of its CHANGELOG.md. "
                  + "The target that sets them is in src/Directory.Build.targets.");

                // A published package cannot change, and main moves on after the tag (#151).
                var source = File.ReadAllText(project.Readme).Replace("\r", "");

                StringAssert.Contains(source, $"{RepositoryUrl}/blob/main/", $"{project.Readme} no longer links the docs at main, so this check checks nothing.");
                Assert.AreEqual(
                    FrozenLinks(source, version).TrimEnd('\n'),
                    PackedReadme(package).Replace("\r", "").TrimEnd('\n'),
                    $"{project}'s packed README is not its README.md with the docs linked at v{version}. "
                  + "The target that rewrites it is in src/Directory.Build.targets.");
            }
        }
        finally
        {
            Directory.Delete(output, recursive: true);
        }
    }

    [TestMethod(DisplayName = "A changelog edit reaches a package packed again into the same folder")]
    [DoNotParallelize]
    public void Repacking_picks_up_a_changelog_edit()
    {
        // GenerateNuspec skips itself when none of its inputs is newer than the package, and the
        // changelog is an input only because the target adds it. Without that, the second pack
        // below keeps the first one's notes and reports success.
        var version = RepositoryLayout.ShippedVersion;
        var output  = Directory.CreateTempSubdirectory("release-notes-").FullName;
        var path    = SeedChangelog($"## [{version}] — 2026-10-20\n\n- Indexes: the first pack.\n");

        try
        {
            string Pack()
            {
                var (exitCode, log) = DotNet(
                    "pack", Core.ProjectFile, "--no-build", "-c", TestConfiguration, "-o", output,
                    "-p:EnablePackageValidation=false", $"-p:ReleaseNotesChangelog={path}");

                Assert.AreEqual(0, exitCode, log);
                return PackedReleaseNotes(Path.Combine(output, $"{Core.PackageId}.{version}.nupkg"));
            }

            StringAssert.StartsWith(Pack(), "- Indexes: the first pack.");

            File.WriteAllText(path, $"## [{version}] — 2026-10-20\n\n- Indexes: the second pack.\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

            StringAssert.StartsWith(Pack(), "- Indexes: the second pack.", "The second pack kept the first one's release notes.");
        }
        finally
        {
            Directory.Delete(output, recursive: true);
            File.Delete(path);
        }
    }

    [TestMethod(DisplayName = "Release notes render links, code and headings as plain text, whatever the section holds")]
    public void Release_notes_render_awkward_content_as_written()
    {
        // MSBuild reads '$(…)', '@(…)', '%xx' and ';' as syntax, and a preview's version has a hyphen
        // and more dots for the heading's pattern to escape. The real changelogs hold none of that
        // today, so a seeded one does.
        const string version = "11.0.0-preview.2";

        var changelog = string.Join(
            "\r\n",
            "# Changelog",
            "",
            "## [11.0.0-preview.20] — 2026-12-01",
            "",
            "- Indexes: not this one.",
            "",
            $"## [{version}] — 2026-11-01",
            "",
            "The introduction, with a [link](https://example.com/a_b#c-d) and EF Core's `$(Version)`.",
            "",
            "### Added",
            "",
            "- **`Type.Member`**, a hook: `@(Compile)` and 100%3B stay as written; so does 50 % of it.",
            "- Docs: [`HasExclusionConstraint`](https://example.com/docs.md#exclusion-constraints-exclude), see also [#1](https://example.com/issues/1).",
            $"- Docs: [the guide]({RepositoryUrl}/blob/main/docs/upgrading.md#changed-answers-in-550), [the folder]({RepositoryUrl}/tree/main/docs/), [the issues]({RepositoryUrl}/issues/151).",
            $"- Docs: [another repository]({RepositoryUrl}.Extra/blob/main/README.md) and [another branch]({RepositoryUrl}/blob/release/5.x/README.md) stay as written.",
            "",
            "## [11.0.0-preview.1] — 2026-10-01",
            "",
            "- Indexes: nor this one.",
            "");

        var path = SeedChangelog(changelog);

        try
        {
            var (exitCode, log) = DotNet(
                "msbuild", Core.ProjectFile, $"-t:{ReleaseNotesTarget}", "-getProperty:PackageReleaseNotes",
                $"-p:Version={version}", $"-p:ReleaseNotesChangelog={path}");

            Assert.AreEqual(0, exitCode, log);
            Assert.AreEqual(ExpectedReleaseNotes(changelog, version, Core.PackageId), log.TrimEnd('\r', '\n'));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod(DisplayName = "The pack fails, naming the changelog, when the release notes cannot be set")]
    [DataRow("no file",          null)]
    [DataRow("no section",       "## [0.0.1] — 2020-01-01\n\n- Indexes: an older version.\n")]
    [DataRow("an empty section", "## [{0}] — 2026-10-20\n\n## [0.0.1] — 2020-01-01\n\n- Indexes: an older version.\n")]
    [DataRow("over the limit",   "## [{0}] — 2026-10-20\n\n- Indexes: {1}\n")]
    public void Release_notes_fail_closed(string seeded, string? template)
    {
        var path = template is null
            ? Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "CHANGELOG.md")
            : SeedChangelog(string.Format(template, RepositoryLayout.ShippedVersion, new string('x', 35_001)));

        try
        {
            var (exitCode, log) = DotNet(
                "msbuild", Core.ProjectFile, $"-t:{ReleaseNotesTarget}", $"-p:ReleaseNotesChangelog={path}");

            Assert.AreNotEqual(0, exitCode, $"With {seeded}, the release notes target passed:\n{log}");
            Assert.Contains($"Cannot set the release notes of {Core.PackageId}", log, log);
            Assert.Contains(path, log, $"The error must name the changelog:\n{log}");
        }
        finally
        {
            if (template is not null)
                File.Delete(path);
        }
    }

    /// <summary>
    /// The section under <c>## [version]</c>, up to the next <c>##</c> heading, as nuget.org shows it:
    /// plain text, which keeps line breaks and turns bare URLs into links but renders no Markdown.
    /// <see langword="null"/> when the changelog has no section for <paramref name="version"/>.
    /// </summary>
    internal static string? ExpectedReleaseNotes(string changelog, string version, string packageId)
    {
        var lines   = changelog.Replace("\r", "").Split('\n');
        var heading = Array.FindIndex(lines, line => line.StartsWith($"## [{version}]", StringComparison.Ordinal));

        if (heading < 0)
            return null;

        var section = lines.Skip(heading + 1)
                           .TakeWhile(line => !line.StartsWith("## ", StringComparison.Ordinal))
                           .Select(PlainText);

        return FrozenLinks(string.Join("\n", section).Trim(), version)
             + "\n\nFull changelog: "
             + $"{RepositoryUrl}/blob/v{version}/src/{packageId}/CHANGELOG.md";

        static string PlainText(string line)
        {
            if (line.StartsWith("### ", StringComparison.Ordinal))
                line = line["### ".Length..];

            line = Regex.Replace(line, @"\[([^\]]+)\]\((https?://[^)\s]+)\)", "$1 ($2)");

            return line.Replace("`", "").Replace("**", "");
        }
    }

    private const string RepositoryUrl = "https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes";

    /// <summary>
    /// Links to this repository at <c>main</c> as they read in a package of <paramref name="version"/>:
    /// at its tag, since a published package cannot follow <c>main</c> (#151).
    /// </summary>
    private static string FrozenLinks(string text, string version) =>
        text.Replace($"{RepositoryUrl}/blob/main/", $"{RepositoryUrl}/blob/v{version}/", StringComparison.Ordinal)
            .Replace($"{RepositoryUrl}/tree/main/", $"{RepositoryUrl}/tree/v{version}/", StringComparison.Ordinal);

    private static string PackedReadme(string package)
    {
        using var archive = ZipFile.OpenRead(package);
        var readme = archive.GetEntry("README.md");

        Assert.IsNotNull(readme, $"{package} has no README.md at its root.");

        using var reader = new StreamReader(readme.Open());
        return reader.ReadToEnd();
    }

    private static string PackedReleaseNotes(string package)
    {
        Assert.IsTrue(File.Exists(package), $"No package at {package}.");

        using var archive = ZipFile.OpenRead(package);
        var nuspec = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));

        using var stream = nuspec.Open();
        return XDocument.Load(stream).Descendants().SingleOrDefault(e => e.Name.LocalName == "releaseNotes")?.Value
            ?? "(no releaseNotes element)";
    }

    private static RepositoryLayout.ShippingProject Core => Projects.Single(project => !project.IsSatellite);

    private static string TestConfiguration =>
        typeof(PackagingConventionTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;

    private static string SeedChangelog(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"CHANGELOG-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Runs the SDK the test run itself runs on, and returns its exit code and all its output.</summary>
    private static (int ExitCode, string Output) DotNet(params string[] arguments)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory       = RepositoryLayout.Root,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        // No MSBuild node outlives the test, and messages are in one language whatever the machine's.
        start.ArgumentList.Add("-nodeReuse:false");
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"dotnet {string.Join(' ', arguments)} did not finish within five minutes.");
        }

        return (process.ExitCode, stdout.Result + stderr.Result);
    }
}
