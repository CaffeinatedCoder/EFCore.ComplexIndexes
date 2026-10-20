using System.Text.RegularExpressions;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Holds the CI inputs that change without a diff to the two answers #111 gave them: the SDK is
/// pinned by <c>global.json</c>, and what still floats is run every night, with a failure reported.
/// </summary>
/// <remarks>
/// Both decay quietly. A job that goes back to <c>dotnet-version: 10.0.x</c> builds with whatever
/// band the runner has that day and passes; a nightly whose report no longer sees one of its parts
/// stays green while that part fails every night.
/// </remarks>
[TestClass]
public class CiInputConventionTests
{
    private const string NightlyWorkflow = "nightly.yml";

    private const string BuildWorkflow = "dotnet.yml";

    private const string ReportJob = "report";

    private static IEnumerable<string> WorkflowFileNames =>
        Directory.EnumerateFiles(Path.Combine(RepositoryLayout.Root, ".github", "workflows"), "*.yml")
                 .Select(Path.GetFileName)
                 .OfType<string>()
                 .Order(StringComparer.Ordinal);

    [TestMethod(DisplayName = "global.json pins the SDK to a feature band")]
    public void Global_json_pins_the_sdk_to_a_feature_band()
    {
        var path = Path.Combine(RepositoryLayout.Root, "global.json");

        Assert.IsTrue(File.Exists(path), "No global.json at the repository root, so CI builds with whichever SDK the runner has.");

        var text = File.ReadAllText(path);

        StringAssert.Matches(
            text, new Regex(@"""version""\s*:\s*""\d+\.\d+\.\d00"""),
            "global.json's sdk.version is not the first release of a feature band (x.y.z00). With "
          + "rollForward latestPatch, that is what pins the band and lets its patches through.");

        StringAssert.Matches(
            text, new Regex(@"""rollForward""\s*:\s*""latestPatch"""),
            "global.json's rollForward is not latestPatch. A wider roll-forward lets a new feature band, "
          + "with its compiler and NuGet, arrive without a PR.");
    }

    [TestMethod(DisplayName = "Every job that builds the repository takes its SDK from global.json")]
    public void Every_job_that_builds_the_repository_takes_its_sdk_from_global_json()
    {
        var floating = new List<string>();

        foreach (var fileName in WorkflowFileNames)
        {
            var workflow = WorkflowFile.Load(fileName);

            foreach (var (job, body) in workflow.Jobs)
            {
                var script = workflow.Script(job);

                if (!script.Contains("actions/setup-dotnet@", StringComparison.Ordinal)
                 || !script.Contains("actions/checkout@", StringComparison.Ordinal))
                    continue;

                if (!script.Contains("global-json-file: global.json", StringComparison.Ordinal)
                 || script.Contains("dotnet-version:", StringComparison.Ordinal))
                    floating.Add($"{fileName}: {job}");
            }
        }

        Assert.IsEmpty(
            floating,
            $"{string.Join(", ", floating)} checks out the repository but does not set up .NET from global.json "
          + "alone, so it builds with an SDK that can change under it overnight.");
    }

    [TestMethod(DisplayName = "The nightly run calls every lane on a schedule")]
    public void Nightly_run_calls_every_lane_on_a_schedule()
    {
        var nightly = WorkflowFile.Load(NightlyWorkflow);
        var on      = nightly.Text[..nightly.Text.IndexOf("\njobs:", StringComparison.Ordinal)];

        Assert.IsTrue(Regex.IsMatch(on, @"(?m)^  schedule:\s*\n\s*- cron:"), $"{NightlyWorkflow} has no schedule.");

        Assert.IsTrue(
            nightly.Jobs.Keys.Any(job => nightly.Key(job, "uses") == $"./.github/workflows/{BuildWorkflow}"),
            $"No job in {NightlyWorkflow} calls ./.github/workflows/{BuildWorkflow}, so the nightly run does not "
          + "run the lanes, nor their gate.");
    }

    /// <summary>
    /// The report is the nightly's gate: it has to see every part and run whatever they did, or a part
    /// that fails every night is reported by nobody.
    /// </summary>
    [TestMethod(DisplayName = "The nightly run reports a failure of any of its parts")]
    public void Nightly_run_reports_a_failure_of_any_of_its_parts()
    {
        var nightly = WorkflowFile.Load(NightlyWorkflow);

        Assert.IsTrue(nightly.Jobs.ContainsKey(ReportJob), $"{NightlyWorkflow} has no `{ReportJob}` job.");

        var needs   = nightly.Needs(ReportJob);
        var missing = nightly.Jobs.Keys.Where(job => job != ReportJob && !needs.Contains(job)).ToList();

        Assert.IsEmpty(missing, $"{string.Join(", ", missing)} is not in the `{ReportJob}` job's needs, so its failures are reported by nobody.");

        Assert.IsTrue(
            nightly.Key(ReportJob, "if") is "always()" or "${{ always() }}",
            $"The `{ReportJob}` job does not run `if: always()`, so it is skipped exactly when a part fails.");

        var script = nightly.Script(ReportJob);

        StringAssert.Contains(script, "toJSON(needs)", $"The `{ReportJob}` job does not read every result from `toJSON(needs)`.");
        StringAssert.Contains(script, "!= \"success\"", $"The `{ReportJob}` job does not treat every result but success as a failure.");
        StringAssert.Matches(script, new Regex(@"(?m)^\s*issues:\s*write\b"), $"The `{ReportJob}` job cannot open the issue it reports with.");
    }
}
