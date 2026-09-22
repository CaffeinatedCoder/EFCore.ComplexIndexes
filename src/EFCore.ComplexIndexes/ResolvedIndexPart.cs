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
/// <param name="NullSort">Null ordering for this part. Defaults to the database's default.</param>
public sealed record ResolvedIndexPart(
    [property: JsonPropertyName("e")] bool   IsExpression,
    [property: JsonPropertyName("v")] string Value,
    [property: JsonPropertyName("d"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Descending = false,
    [property: JsonPropertyName("n"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault), JsonConverter(typeof(JsonStringEnumConverter<DbNullSort>))] DbNullSort NullSort = DbNullSort.Default
)
{
    // Neither member is serialized: both matter to the differ only, never to a SQL generator.

    /// <summary>
    /// What a default index name is built from, when that should not be the rendered SQL. A JSON
    /// member sets its container column and path here, so a change in how the member is rendered —
    /// a cast added, a different path operator — never renames an index an application may be
    /// matching on by name.
    /// </summary>
    internal string? NameToken { get; init; }

    /// <summary>
    /// Set when a JSON member of a date or time type fell back to text: the path and the store type
    /// the provider's queries cast the member to. PostgreSQL cannot index that cast (the conversion
    /// from text is not IMMUTABLE), so no query ever uses such a part.
    /// </summary>
    internal (string Path, string StoreType)? TextFallback { get; init; }

    /// <summary>
    /// Compares the rendered part only. <see cref="NameToken"/> and <see cref="TextFallback"/> are
    /// facts about how the part was derived, and the differ compares parts to decide whether an index
    /// changed: counting them in would rebuild an index whose SQL is identical, as it did for a
    /// date member between a pre-5.4.0 snapshot and the current model.
    /// </summary>
    /// <param name="other">The part to compare with.</param>
    /// <returns>True when both render the same SQL with the same sort options.</returns>
    public bool Equals(ResolvedIndexPart? other)
        => other is not null
        && IsExpression == other.IsExpression
        && Value        == other.Value
        && Descending   == other.Descending
        && NullSort     == other.NullSort;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(IsExpression, Value, Descending, NullSort);
}
