using System.Diagnostics;
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

    /// <summary>The build workflow, which the release workflow calls for its lanes.</summary>
    private const string BuildWorkflowFileName = "dotnet.yml";

    // The functions that, in a job's `if:`, replace the implicit success() check on its needs.
    private static readonly Regex StatusFunction =
        new(@"\b(always|failure|cancelled)\s*\(\s*\)", RegexOptions.Compiled);

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

    /// <summary>
    /// <c>publish</c> runs only after every lane of <c>dotnet.yml</c> passed on the tagged commit.
    /// </summary>
    /// <remarks>
    /// Before #111 the release trusted that the tagged commit had passed its PR checks, which a tag
    /// on a commit whose push run failed never had, and <c>verify</c> ran a test step of its own
    /// that no lane added to <c>dotnet.yml</c> reached. Now a job calls <c>dotnet.yml</c>, whose
    /// <c>CI</c> gate fails on any lane that did not succeed (<see cref="WorkflowGateConventionTests"/>),
    /// and <c>publish</c> depends on that job through <c>needs</c>. A status function in an
    /// <c>if:</c> on that path (<c>always()</c>, <c>failure()</c>, <c>cancelled()</c>) would replace
    /// the implicit <c>success()</c> and let <c>publish</c> run after a failed lane, so none may
    /// appear there.
    /// </remarks>
    [TestMethod(DisplayName = "The release workflow cannot publish without the full CI")]
    public void Release_cannot_publish_without_the_full_ci()
    {
        var build = WorkflowFile.Load(BuildWorkflowFileName);

        Assert.IsTrue(
            Regex.IsMatch(build.Text[..build.Text.IndexOf("\njobs:", StringComparison.Ordinal)], @"(?m)^  workflow_call:"),
            $"{BuildWorkflowFileName} has no `workflow_call` trigger, so the release workflow cannot run its lanes.");

        var release = WorkflowFile.Load(ReleaseWorkflowFileName);
        var ciJobs  = release.Jobs.Keys.Where(job => release.Key(job, "uses") == $"./.github/workflows/{BuildWorkflowFileName}").ToList();

        Assert.IsNotEmpty(
            ciJobs,
            $"No job in {ReleaseWorkflowFileName} calls ./.github/workflows/{BuildWorkflowFileName}, so a release "
          + "runs none of the lanes a PR runs and nothing stops a tag on a commit whose CI failed.");

        Assert.IsTrue(release.Jobs.ContainsKey(PublishJob), $"The release workflow has no `{PublishJob}` job.");

        var path    = new HashSet<string>(StringComparer.Ordinal) { PublishJob };
        var pending = new Queue<string>(release.Needs(PublishJob));

        while (pending.TryDequeue(out var job))
            if (path.Add(job) && release.Jobs.ContainsKey(job))
                foreach (var need in release.Needs(job))
                    pending.Enqueue(need);

        Assert.IsTrue(
            ciJobs.Any(path.Contains),
            $"`{PublishJob}` does not depend on {string.Join(", ", ciJobs)} through `needs`, so it can publish "
          + "before, or without, every lane passing.");

        var overriding = path.Where(job => release.Jobs.ContainsKey(job)
                                        && release.Key(job, "if") is { } condition
                                        && StatusFunction.IsMatch(condition))
                             .ToList();

        Assert.IsEmpty(
            overriding,
            $"{string.Join(", ", overriding)} has a status function in its `if:`, which replaces the implicit "
          + $"success() and lets `{PublishJob}` run after a lane failed.");
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
    /// The GitHub release keeps an older line and every preview off Latest.
    /// </summary>
    /// <remarks>
    /// GitHub marks each new release Latest unless told otherwise, and Latest is what the repository
    /// page and <c>gh release view</c> show. With two supported lines, a 10.x patch tagged after
    /// 11.0.0 is the ordinary case; left to the default it would make the LTS line the front page's
    /// answer. A preview marked only <c>--prerelease</c> relies on the API refusing to make a
    /// prerelease Latest, so it says <c>--latest=false</c> as well. Only script lines are read: the
    /// comment above the step explains both rules, and a test matching it would pass with the code
    /// deleted. Each flag is looked for on its own branch, so moving <c>--latest=false</c> out of the
    /// older-than-Latest guard — which would keep every stable release off Latest — fails too.
    /// Ported from the sibling repository.
    /// </remarks>
    [TestMethod(DisplayName = "The GitHub release keeps an older line and previews off Latest")]
    public void Github_release_keeps_an_older_line_and_previews_off_latest()
    {
        var step = Regex.Match(ReleaseWorkflowText, @"(?ms)^      - name: Create the release if it does not exist\n.*?(?=^      - name: |\z)").Value;

        Assert.AreNotEqual(0, step.Length, "Could not find the 'Create the release if it does not exist' step in the release workflow.");

        var script = step.Split('\n')
                         .Select(line => line.Trim())
                         .Where(line => line.Length > 0 && !line.StartsWith('#'))
                         .ToList();

        int IndexOf(Predicate<string> predicate, int from = 0) => script.FindIndex(Math.Max(from, 0), predicate);

        var preview = IndexOf(line => line.StartsWith("if ", StringComparison.Ordinal) && line.Contains("\"$VERSION\" == *-*", StringComparison.Ordinal));

        Assert.IsTrue(
            preview >= 0,
            "The release step no longer tells a prerelease version (x.y.z-suffix) apart, so a preview tag "
          + "would be published as a full release.");

        var stable = IndexOf(line => line == "else", preview);

        Assert.IsTrue(stable > preview, "The release step's prerelease check has no `else` branch for a stable tag.");

        var previewFlags = IndexOf(line => line.Contains("flags+=(--prerelease --latest=false)", StringComparison.Ordinal), preview);

        Assert.IsTrue(
            previewFlags > preview && previewFlags < stable,
            "The release step does not publish a preview as a prerelease that is not Latest.");

        var olderGuard = IndexOf(
            line => line.StartsWith("if ", StringComparison.Ordinal)
                 && line.Contains("sort -V", StringComparison.Ordinal)
                 && line.Contains("!= \"$VERSION\"", StringComparison.Ordinal),
            stable);
        var olderFlags = olderGuard < 0 ? -1 : IndexOf(line => line == "flags+=(--latest=false)", olderGuard);
        var guardEnd   = olderGuard < 0 ? -1 : IndexOf(line => line == "fi", olderGuard);

        Assert.IsTrue(
            olderGuard > stable && olderFlags > olderGuard && olderFlags < guardEnd,
            "The release step no longer compares a stable version with the current Latest by `sort -V` "
          + "and passes --latest=false only for an older one, so a 10.x tag pushed after 11.0.0 would "
          + "take Latest (or no stable release ever would).");

        Assert.IsTrue(
            script.Any(line => line.StartsWith("latest=\"$(gh release view --json tagName", StringComparison.Ordinal)),
            "The release step does not read the current Latest release with a tagless `gh release view`.");

        Assert.IsTrue(
            IndexOf(line => line.StartsWith("gh release create", StringComparison.Ordinal) && line.Contains("\"${flags[@]}\"", StringComparison.Ordinal), guardEnd) > guardEnd,
            "The release step's `gh release create` does not pass the flags it computes.");
    }

    /// <summary>
    /// The GitHub release's notes link the docs at the tag, not at <c>main</c>.
    /// </summary>
    /// <remarks>
    /// The changelog links this repository at <c>blob/main</c>, and <c>main</c> becomes another line
    /// after the <c>v5.5.0</c> tag (#151). This runs the step's own lines, from the extraction up to
    /// the empty-notes check, against a seeded changelog: matching the text would pass with a rewrite
    /// that never matches, and the bash on a contributor's Mac (3.2) and on the runner (5.x) read a
    /// quoted replacement differently.
    /// </remarks>
    [TestMethod(DisplayName = "The GitHub release's notes link the docs at the tag")]
    public void Github_release_notes_link_the_docs_at_the_tag()
    {
        const string repository = "https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes";

        var step = Regex.Match(ReleaseWorkflowText, @"(?ms)^      - name: Create the release if it does not exist\n.*?(?=^      - name: |\z)").Value;
        var run  = step.Split('\n').SkipWhile(line => line.Trim() != "run: |").Skip(1).ToList();

        var start = run.FindIndex(line => line.TrimStart().StartsWith("notes=\"$(awk", StringComparison.Ordinal));
        var end   = run.FindIndex(Math.Max(start, 0), line => line.Trim() == "if [[ -z \"$notes\" ]]; then");

        Assert.IsTrue(start >= 0 && end > start, "Could not find the notes extraction in the 'Create the release if it does not exist' step.");

        var directory = Directory.CreateTempSubdirectory("release-notes-").FullName;

        try
        {
            File.WriteAllText(Path.Combine(directory, "CHANGELOG.md"), string.Join("\n",
                "# Changelog",
                "",
                "## [5.5.1] — 2026-12-01",
                "",
                $"- Docs: not this one ({repository}/blob/main/docs/sqlserver.md).",
                "",
                "## [5.5.0] — 2026-11-01",
                "",
                $"The introduction, with [the guide]({repository}/blob/main/docs/upgrading.md#changed-answers-in-550).",
                "",
                "### Fixed",
                "",
                $"- Docs: [the folder]({repository}/tree/main/docs/), [the issue]({repository}/issues/151), [main twice]({repository}/blob/main/README.md) ({repository}/blob/main/CHANGELOG.md).",
                $"- Docs: [another repository]({repository}.Extra/blob/main/README.md) and [another branch]({repository}/blob/release/5.x/README.md) stay as written.",
                "",
                "## [5.4.1] — 2026-10-01",
                "",
                "- Indexes: nor this one.",
                ""));

            var script = string.Join("\n", run.Take(end).Skip(start).Select(line => line.StartsWith("          ", StringComparison.Ordinal) ? line[10..] : line))
                       + "\nprintf '%s' \"$notes\"\n";

            var bash = new ProcessStartInfo("bash") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
            bash.ArgumentList.Add("-c");
            bash.ArgumentList.Add(script);
            bash.Environment["VERSION"]  = "5.5.0";
            bash.Environment["REPO_URL"] = repository;

            using var process = Process.Start(bash)!;
            var output = process.StandardOutput.ReadToEnd();
            var error  = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.AreEqual(0, process.ExitCode, $"The notes extraction failed:\n{error}\n{script}");
            Assert.AreEqual(
                string.Join("\n",
                    $"The introduction, with [the guide]({repository}/blob/v5.5.0/docs/upgrading.md#changed-answers-in-550).",
                    "",
                    "### Fixed",
                    "",
                    $"- Docs: [the folder]({repository}/tree/v5.5.0/docs/), [the issue]({repository}/issues/151), [main twice]({repository}/blob/v5.5.0/README.md) ({repository}/blob/v5.5.0/CHANGELOG.md).",
                    $"- Docs: [another repository]({repository}.Extra/blob/main/README.md) and [another branch]({repository}/blob/release/5.x/README.md) stay as written."),
                output.TrimEnd('\n'),
                "The GitHub release's notes do not link the docs at the tag. They are built in release.yml's "
              + "'Create the release if it does not exist' step.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
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
