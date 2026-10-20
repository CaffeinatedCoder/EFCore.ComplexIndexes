using System.Text.RegularExpressions;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// The jobs of a GitHub Actions workflow under <c>.github/workflows/</c>, read well enough for the
/// convention tests that hold them to a rule: which jobs exist, what each one needs, and its keys.
/// </summary>
/// <remarks>
/// Not a YAML parser. It reads the layout every workflow in this repository uses: jobs as
/// two-space-indented keys under a top-level <c>jobs:</c>, their keys at four spaces. Comments
/// between jobs fall into the preceding job's body, which is harmless as long as callers match
/// YAML keys rather than words.
/// </remarks>
internal sealed class WorkflowFile
{
    private static readonly Regex JobBlock =
        new(@"(?ms)^  (?<name>[A-Za-z_][\w-]*):\s*\n(?<body>.*?)(?=^  [A-Za-z_][\w-]*:\s*\n|\z)", RegexOptions.Compiled);

    private static readonly Regex InlineNeeds =
        new(@"(?m)^    needs:[ \t]*\[(?<list>[^\]]*)\]", RegexOptions.Compiled);

    private static readonly Regex ScalarNeeds =
        new(@"(?m)^    needs:[ \t]*(?<job>[A-Za-z_][\w-]*)[ \t]*(?:#.*)?$", RegexOptions.Compiled);

    private static readonly Regex BlockNeeds =
        new(@"(?m)^    needs:[ \t]*\n(?<list>(?:^      -[ \t]*[A-Za-z_][\w-]*[ \t]*\n)+)", RegexOptions.Compiled);

    private WorkflowFile(string fileName, string text, IReadOnlyDictionary<string, string> jobs)
    {
        FileName = fileName;
        Text     = text;
        Jobs     = jobs;
    }

    public string FileName { get; }

    public string Text { get; }

    /// <summary>Each job's id, mapped to its body.</summary>
    public IReadOnlyDictionary<string, string> Jobs { get; }

    public static string PathOf(string fileName) =>
        Path.Combine(RepositoryLayout.Root, ".github", "workflows", fileName);

    public static WorkflowFile Load(string fileName)
    {
        var path = PathOf(fileName);

        Assert.IsTrue(File.Exists(path), $"No .github/workflows/{fileName}.");

        var text  = File.ReadAllText(path).ReplaceLineEndings("\n");
        var start = Regex.Match(text, @"(?m)^jobs:\s*$");

        Assert.IsTrue(start.Success, $"{fileName} has no top-level `jobs:` key.");

        var jobs = JobBlock.Matches(text[(start.Index + start.Length)..])
                           .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["body"].Value, StringComparer.Ordinal);

        Assert.IsNotEmpty(jobs, $"Could not find any jobs under `jobs:` in {fileName}.");

        return new WorkflowFile(fileName, text, jobs);
    }

    /// <summary>The job ids a job lists under <c>needs</c>, in any of the three YAML spellings.</summary>
    public IReadOnlyList<string> Needs(string job)
    {
        var body = Jobs[job];

        if (InlineNeeds.Match(body) is { Success: true } inline)
            return [.. inline.Groups["list"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

        if (BlockNeeds.Match(body) is { Success: true } block)
            return [.. block.Groups["list"].Value.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                                                 .Select(line => line.TrimStart('-').Trim())];

        if (ScalarNeeds.Match(body) is { Success: true } scalar)
            return [scalar.Groups["job"].Value];

        return [];
    }

    /// <summary>A job's own four-space-indented key, such as <c>name</c> or <c>if</c>; null when absent.</summary>
    public string? Key(string job, string key)
    {
        var match = Regex.Match(Jobs[job], $@"(?m)^    {Regex.Escape(key)}:[ \t]*(?<value>.*?)[ \t]*$");
        return match.Success ? match.Groups["value"].Value : null;
    }

    /// <summary>A job's body without comment lines, for assertions about what runs rather than what is said.</summary>
    public string Script(string job) =>
        string.Join('\n', Jobs[job].Split('\n').Where(line => !line.TrimStart().StartsWith('#')));
}
