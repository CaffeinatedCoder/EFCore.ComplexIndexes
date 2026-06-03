using System.Text.Json.Serialization;

namespace EFCore.ComplexIndexes;

/// <summary>
/// One ordered entry in an index's column list. It is either a column (referenced by a
/// dotted property path that the differ resolves to a real column name) or a verbatim
/// SQL expression. Exactly one of <see cref="PropertyPath"/> / <see cref="Expression"/>
/// is set.
/// </summary>
public sealed class IndexPartDefinition : IEquatable<IndexPartDefinition>
{
    /// <summary>Dotted property path (e.g. <c>Address.City</c>) resolved to a column name. Null for expression parts.</summary>
    [JsonPropertyName("path")] public string? PropertyPath { get; init; }

    /// <summary>Verbatim SQL fragment, emitted as-is. Null for column parts.</summary>
    [JsonPropertyName("expr")] public string? Expression { get; init; }

    [JsonIgnore] public bool IsExpression => Expression is not null;

    public bool Equals(IndexPartDefinition? other) =>
        other is not null
     && PropertyPath == other.PropertyPath
     && Expression   == other.Expression;

    public override bool Equals(object? obj) => Equals(obj as IndexPartDefinition);

    public override int GetHashCode() => HashCode.Combine(PropertyPath, Expression);
}
