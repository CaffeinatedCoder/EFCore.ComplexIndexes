using System.Text.RegularExpressions;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Guards the parts of the release path that fail silently rather than loudly.
/// </summary>
/// <remarks>
/// Trusted Publishing matches on three names — the workflow <em>file name</em>, the environment,
/// and the repository owner/name — none of which live in this repository's build. Rename the
/// workflow or the environment and nothing here breaks: the next tag simply fails to publish,
/// with an authentication error that says nothing about the rename. The job split is the same
/// shape: nothing stops a later edit from handing the unattended <c>verify</c> job an
/// <c>id-token</c>, or a job both the token and write access to the repository, and the workflow
/// would keep passing. Ported from the sibling repository, which had these before this one did.
/// </remarks>
[TestClass]
public class ReleaseWiringConventionTests
{
    /// <summary>The workflow file name the nuget.org trusted-publishing policy names.</summary>
    private const string ReleaseWorkflowFileName = "release.yml";

    /// <summary>The GitHub environment the policy expects, and the approval gate.</summary>
    private const string PublishEnvironment = "nuget";

    /// <summary>The one job allowed to mint an OIDC token.</summary>
    private const string PublishJob = "publish";

    private static string ReleaseWorkflowPath =>
        Path.Combine(RepositoryLayout.Root, ".github", "workflows", ReleaseWorkflowFileName);

    private static string ReleaseWorkflowText => File.ReadAllText(ReleaseWorkflowPath);

    // A YAML key at any indentation — not the word inside a comment. The verify job's comment says
    // "id-token" precisely to explain why it does not request one, and a substring search would
    // read that as the permission it warns against.
    private static readonly Regex IdTokenWrite =
        new(@"(?m)^\s*id-token\s*:\s*write\b", RegexOptions.Compiled);

    private static readonly Regex IdTokenKey =
        new(@"(?m)^\s*id-token\s*:", RegexOptions.Compiled);

    private static readonly Regex ContentsWrite =
        new(@"(?m)^\s*contents\s*:\s*write\b", RegexOptions.Compiled);

    // Each job is a two-space-indented key under `jobs:`; its body runs to the next such key.
    // Two-space-indented comments between jobs start with '#', so they fall into the preceding
    // job's body, which is harmless because every assertion below matches YAML keys, not words.
    private static readonly Regex JobBlock =
        new(@"(?ms)^  (?<name>[A-Za-z_][\w-]*):\s*\n(?<body>.*?)(?=^  [A-Za-z_][\w-]*:\s*\n|\z)", RegexOptions.Compiled);

    private static Dictionary<string, string> Jobs()
    {
        var text  = ReleaseWorkflowText;
        var start = Regex.Match(text, @"(?m)^jobs:\s*$");

        Assert.IsTrue(start.Success, "The release workflow has no top-level `jobs:` key.");

        var jobs = JobBlock.Matches(text[(start.Index + start.Length)..])
                           .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["body"].Value, StringComparer.Ordinal);

        Assert.IsNotEmpty(jobs, "Could not find any jobs under `jobs:` in the release workflow.");

        return jobs;
    }

    [TestMethod(DisplayName = "The release workflow lives at the file name the trusted-publishing policy names")]
    public void Release_workflow_lives_at_the_policy_file_name()
    {
        Assert.IsTrue(
            File.Exists(ReleaseWorkflowPath),
            $"No .github/workflows/{ReleaseWorkflowFileName}. The nuget.org trusted-publishing policy "
          + "names the workflow by file name, so renaming or moving this file stops publishing "
          + "working — with an authentication error that does not mention the rename.");
    }

    [TestMethod(DisplayName = "The publish job is gated on the environment the policy expects")]
    public void Publish_job_is_gated_on_the_policy_environment()
    {
        var jobs = Jobs();

        Assert.IsTrue(jobs.ContainsKey(PublishJob), $"The release workflow has no `{PublishJob}` job.");

        Assert.IsTrue(
            Regex.IsMatch(jobs[PublishJob], $@"(?m)^\s*environment\s*:\s*{PublishEnvironment}\s*$"),
            $"The `{PublishJob}` job is not gated on the '{PublishEnvironment}' environment. The OIDC "
          + "token carries the environment as a claim; a policy that expects it will not match a "
          + "token without it, and the environment is also the approval gate.");
    }

    /// <summary>
    /// The split exists so a reviewer is asked only after the suite has passed, and so nothing that
    /// runs unattended is able to mint a publishing token.
    /// </summary>
    [TestMethod(DisplayName = "Only the publish job may mint an OIDC token")]
    public void Only_the_publish_job_may_mint_an_oidc_token()
    {
        var jobs = Jobs();

        Assert.IsTrue(
            jobs.TryGetValue(PublishJob, out var publish) && IdTokenWrite.IsMatch(publish),
            $"The `{PublishJob}` job does not request id-token: write, so OIDC login cannot work.");

        var others = jobs.Where(job => job.Key != PublishJob && IdTokenKey.IsMatch(job.Value))
                         .Select(job => job.Key)
                         .ToList();

        Assert.IsEmpty(
            others,
            $"{string.Join(", ", others)} requests an id-token permission. Only `{PublishJob}` may: "
          + "everything else runs unattended or with write access, and nothing there may be able to "
          + "mint a token that can publish.");
    }

    /// <summary>
    /// The token and repository write access are kept in different jobs on purpose: a compromised
    /// step in the publishing job cannot rewrite the repository, and a compromised step in the
    /// release job cannot publish.
    /// </summary>
    [TestMethod(DisplayName = "No job holds both id-token: write and contents: write")]
    public void No_job_holds_both_the_token_and_repository_write_access()
    {
        var both = Jobs().Where(job => IdTokenWrite.IsMatch(job.Value) && ContentsWrite.IsMatch(job.Value))
                         .Select(job => job.Key)
                         .ToList();

        Assert.IsEmpty(
            both,
            $"{string.Join(", ", both)} holds both id-token: write and contents: write. The release "
          + "path is split so that the job that can publish cannot write to the repository and the "
          + "job that can write to the repository cannot publish — keep them apart.");
    }

    [TestMethod(DisplayName = "The release workflow checks the tag against the version property")]
    public void Release_workflow_checks_the_tag_against_the_version_property()
    {
        StringAssert.Contains(
            ReleaseWorkflowText, "does not match Directory.Build.props",
            "The release workflow does not verify the pushed tag against the Version property. "
          + "Deriving the version from the tag instead would publish whatever was typed.");
    }

    /// <summary>
    /// Both workflows run <c>dotnet cyclonedx</c> after <c>dotnet tool restore</c>, which silently
    /// does nothing without a manifest.
    /// </summary>
    [TestMethod(DisplayName = "The SBOM tool is pinned in the tool manifest")]
    public void Sbom_tool_is_pinned_in_the_tool_manifest()
    {
        var manifest = Path.Combine(RepositoryLayout.Root, ".config", "dotnet-tools.json");

        Assert.IsTrue(File.Exists(manifest), $"No tool manifest at {manifest}.");

        StringAssert.Contains(
            File.ReadAllText(manifest), "cyclonedx",
            "The tool manifest does not pin cyclonedx, but both workflows run `dotnet cyclonedx` "
          + "after `dotnet tool restore`.");
    }

    /// <summary>
    /// Publishing goes through the workflow and Trusted Publishing only. A second path holding an
    /// API key is the one that will skip a check.
    /// </summary>
    [TestMethod(DisplayName = "There is exactly one publishing path")]
    public void There_is_exactly_one_publishing_path()
    {
        var scripts = Directory.Exists(Path.Combine(RepositoryLayout.Root, "scripts"))
                          ? Directory.EnumerateFiles(Path.Combine(RepositoryLayout.Root, "scripts"), "*publish*", SearchOption.AllDirectories)
                                     .Select(Path.GetFileName)
                                     .ToList()
                          : [];

        Assert.IsEmpty(
            scripts,
            $"Found a publishing script alongside the release workflow: {string.Join(", ", scripts)}. "
          + "Publishing goes through release.yml and Trusted Publishing; a second path holding an "
          + "API key is the one that will skip a check.");
    }
}
