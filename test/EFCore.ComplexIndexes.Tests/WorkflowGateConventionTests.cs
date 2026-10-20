namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Holds the build workflow's <c>CI</c> gate to the one rule branch protection now rests on: it
/// sees every lane, and it passes only when each of them succeeded.
/// </summary>
/// <remarks>
/// Branch protection requires <c>CI</c> and no lane by name (#110), because GitHub reports a skipped
/// job as Success even on a required check. That moves the guard into the workflow, where nothing
/// else notices it decay: a lane left out of the gate's <c>needs</c> runs, fails, and merges anyway,
/// and a gate without <c>if: always()</c> is itself skipped when a lane fails — which branch
/// protection reads as Success.
/// </remarks>
[TestClass]
public class WorkflowGateConventionTests
{
    /// <summary>The build workflow, which every push and PR runs.</summary>
    private const string BuildWorkflow = "dotnet.yml";

    /// <summary>The gate's job id.</summary>
    private const string GateJob = "ci";

    /// <summary>The gate's name: the context branch protection requires.</summary>
    private const string GateContext = "CI";

    private static WorkflowFile Build => WorkflowFile.Load(BuildWorkflow);

    [TestMethod(DisplayName = "The build workflow's gate reports under the context branch protection requires")]
    public void Gate_reports_under_the_required_context()
    {
        var workflow = Build;

        Assert.IsTrue(workflow.Jobs.ContainsKey(GateJob), $"{BuildWorkflow} has no `{GateJob}` gate job.");

        Assert.AreEqual(
            GateContext, workflow.Key(GateJob, "name"),
            $"The `{GateJob}` job no longer reports as '{GateContext}'. Branch protection requires that "
          + "context on main and release/5.x; a renamed gate leaves it required and never reported, "
          + "which hangs every PR instead of failing it.");

        var others = workflow.Jobs.Keys.Where(job => job != GateJob && workflow.Key(job, "name") == GateContext).ToList();

        Assert.IsEmpty(others, $"{string.Join(", ", others)} also reports as '{GateContext}', so the required context no longer names the gate alone.");
    }

    [TestMethod(DisplayName = "The gate runs whatever happened to the lanes")]
    public void Gate_runs_whatever_happened_to_the_lanes()
    {
        var condition = Build.Key(GateJob, "if");

        Assert.IsTrue(
            condition is "always()" or "${{ always() }}",
            $"The `{GateJob}` job's condition is '{condition ?? "(none)"}', not always(). Without it the "
          + "gate is skipped when a lane fails, and GitHub reports a skipped required check as Success.");
    }

    [TestMethod(DisplayName = "Every lane in the build workflow is in the gate's needs")]
    public void Every_lane_is_in_the_gates_needs()
    {
        var workflow = Build;
        var needs    = workflow.Needs(GateJob);
        var missing  = workflow.Jobs.Keys.Where(job => job != GateJob && !needs.Contains(job)).ToList();

        Assert.IsEmpty(
            missing,
            $"{string.Join(", ", missing)} is not in the `{GateJob}` job's needs. Branch protection "
          + $"requires only '{GateContext}', so a lane the gate does not need can fail and the PR still "
          + "merges.");
    }

    /// <summary>
    /// The gate reads <c>needs</c> whole, so a lane added there is checked without a second list to
    /// keep in step, and it accepts <c>success</c> alone: skipped and cancelled are what a required
    /// check cannot tell from a pass, which is the reason the gate exists.
    /// </summary>
    [TestMethod(DisplayName = "The gate fails on any result but success, for every job it needs")]
    public void Gate_fails_on_any_result_but_success()
    {
        var script = Build.Script(GateJob);

        StringAssert.Contains(
            script, "toJSON(needs)",
            $"The `{GateJob}` job does not read the results from `toJSON(needs)`, so a lane added to its "
          + "needs would not be checked without a second edit.");

        StringAssert.Contains(
            script, "!= \"success\"",
            $"The `{GateJob}` job does not fail every result other than success. A skipped or cancelled "
          + "lane must fail it.");
    }
}
