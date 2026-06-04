using System.Text.Json.Serialization;

namespace EFCore.ComplexIndexes;

/// <summary>
/// A fully resolved index part as carried on the <see cref="ComplexIndexAnnotations.IndexParts"/>
/// annotation of a <c>CreateIndexOperation</c>. Property paths have already been resolved to
/// column names by the differ; provider SQL generators consume this directly.
/// </summary>
/// <param name="IsExpression">True if <see cref="Value"/> is a verbatim SQL expression; false if it is a column name.</param>
/// <param name="Value">The resolved column name, or the verbatim SQL expression.</param>
/// <param name="Descending">Whether this part sorts descending. Defaults to ascending.</param>
public sealed record ResolvedIndexPart(
    [property: JsonPropertyName("e")] bool   IsExpression,
    [property: JsonPropertyName("v")] string Value,
    [property: JsonPropertyName("d"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Descending = false
);
