using System.Text.Json.Serialization;

namespace EFCore.ComplexIndexes;

/// <summary>
/// One ordered entry in an index's column list. It is a column (referenced by a dotted property
/// path that the differ resolves to a real column name), a verbatim SQL expression, or a SQL
/// template with <c>{Property.Path}</c> placeholders produced by a typed-expression translator.
/// Exactly one of <see cref="PropertyPath"/> / <see cref="Expression"/> / <see cref="Template"/>
/// is set.
/// </summary>
public sealed class IndexPartDefinition : IEquatable<IndexPartDefinition>
{
    /// <summary>Dotted property path (e.g. <c>Address.City</c>) resolved to a column name. Null for expression parts.</summary>
    [JsonPropertyName("path")] public string? PropertyPath { get; init; }

    /// <summary>Verbatim SQL fragment, emitted as-is. Null for column parts.</summary>
    [JsonPropertyName("expr")] public string? Expression { get; init; }

    /// <summary>
    /// SQL template with <c>{Property.Path}</c> placeholders that the provider differ resolves to
    /// column references at migration time (literal braces escaped as <c>{{</c>/<c>}}</c>).
    /// Null for column and verbatim-expression parts.
    /// </summary>
    [JsonPropertyName("tmpl")] public string? Template { get; init; }

    /// <summary>Whether this part sorts descending. Defaults to ascending.</summary>
    [JsonPropertyName("desc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Descending { get; init; }

    /// <summary>Null ordering for this part. Defaults to the database's default.</summary>
    [JsonPropertyName("nulls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonConverter(typeof(JsonStringEnumConverter<DbNullSort>))]
    public DbNullSort NullSort { get; init; }

    /// <summary>Whether this part is a verbatim SQL expression rather than a column reference.</summary>
    [JsonIgnore] public bool IsExpression => Expression is not null;

    /// <summary>Whether this part is a SQL template whose placeholders still need resolving.</summary>
    [JsonIgnore] public bool IsTemplate => Template is not null;

    /// <summary>
    /// Returns a copy of this part with the given sort options applied; null arguments keep the
    /// current value.
    /// </summary>
    /// <remarks>
    /// Lives next to the properties on purpose. Callers that rebuild a part by hand silently drop
    /// whichever member they forget — <c>Template</c> was dropped this way — and the loss only
    /// surfaces much later as an index rendered without its expression.
    /// </remarks>
    internal IndexPartDefinition WithSortOptions(bool? descending = null, DbNullSort? nullSort = null)
        => new()
           {
               PropertyPath = PropertyPath,
               Expression   = Expression,
               Template     = Template,
               Descending   = descending ?? Descending,
               NullSort     = nullSort   ?? NullSort
           };

    /// <summary>Compares the path, expression, template and sort options.</summary>
    /// <param name="other">The part to compare with.</param>
    /// <returns><c>true</c> if the two describe the same column-list entry.</returns>
    public bool Equals(IndexPartDefinition? other) =>
        other is not null
     && PropertyPath == other.PropertyPath
     && Expression   == other.Expression
     && Template     == other.Template
     && Descending   == other.Descending
     && NullSort     == other.NullSort;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as IndexPartDefinition);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(PropertyPath, Expression, Template, Descending, NullSort);
}
