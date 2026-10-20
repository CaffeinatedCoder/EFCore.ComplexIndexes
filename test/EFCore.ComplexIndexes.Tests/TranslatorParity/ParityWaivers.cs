namespace EFCore.ComplexIndexes.Tests.TranslatorParity;

internal enum WaiverKind
{
    /// <summary>A planner rewrite (<see cref="PostgresCanonicalForm.Rules"/>) switched on for every row.</summary>
    Rule,

    /// <summary>Rows that render differently and answer alike; the live sweep checks the claim.</summary>
    Same,

    /// <summary>Rows that cannot be compared, or differ knowingly; the reason names why or links the issue.</summary>
    Unchecked,
}

internal sealed record Waiver(WaiverKind Kind, string Title, string Reason, IReadOnlyList<string> Rows);

/// <summary>
/// The waiver file, <c>TranslatorParity/waivers.txt</c>. Every disagreement the sweep finds is either
/// fixed or listed there under a block with its reason; none is skipped silently, and a waiver that no
/// longer matches a disagreement fails the sweep, so the file shrinks as fixes land.
/// </summary>
/// <remarks>
/// A block starts with <c>rule: name</c>, <c>same: title</c> or <c>unchecked: title</c> at the start of
/// a line. Indented lines under it are its reason, and indented lines starting with <c>- </c> are the
/// ids of the rows it covers, exactly as the sweep prints them. Lines starting with <c>#</c> are comments.
/// </remarks>
internal static class ParityWaivers
{
    public static string Path => System.IO.Path.Combine(RepositoryLayout.Root, "test", "EFCore.ComplexIndexes.Tests", "TranslatorParity", "waivers.txt");

    public static IReadOnlyList<Waiver> Load() => Parse(File.ReadAllLines(Path));

    public static IReadOnlyList<Waiver> Parse(IEnumerable<string> lines)
    {
        var waivers = new List<Waiver>();
        (WaiverKind Kind, string Title, List<string> Reason, List<string> Rows)? current = null;

        foreach (var line in lines)
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (!char.IsWhiteSpace(line[0]))
            {
                Close();
                var colon = line.IndexOf(':');
                var kind = colon < 0 ? null : line[..colon] switch
                {
                    "rule"      => (WaiverKind?)WaiverKind.Rule,
                    "same"      => WaiverKind.Same,
                    "unchecked" => WaiverKind.Unchecked,
                    _           => null,
                };

                if (kind is null)
                    throw new FormatException($"waivers.txt: a block starts with 'rule:', 'same:' or 'unchecked:', not '{line}'.");

                current = (kind.Value, line[(colon + 1)..].Trim(), [], []);
                continue;
            }

            if (current is null)
                throw new FormatException($"waivers.txt: '{line.Trim()}' is indented under no block.");

            var text = line.Trim();
            if (text.StartsWith("- ", StringComparison.Ordinal))
                current.Value.Rows.Add(text[2..]);
            else
                current.Value.Reason.Add(text);
        }

        Close();
        return waivers;

        void Close()
        {
            if (current is not { } block)
                return;

            if (block.Reason.Count == 0)
                throw new FormatException($"waivers.txt: '{block.Title}' gives no reason.");

            waivers.Add(new Waiver(block.Kind, block.Title, string.Join(" ", block.Reason), block.Rows));
            current = null;
        }
    }
}
