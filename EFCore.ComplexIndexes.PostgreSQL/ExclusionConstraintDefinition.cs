using System.Text.Json;
using System.Text.Json.Serialization;

namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// One element of an EXCLUDE constraint: a column (referenced by a dotted property path that the
/// differ resolves to a real column name) or a verbatim SQL expression, plus the operator it is
/// compared with (e.g. <c>=</c>, <c>&amp;&amp;</c>). Exactly one of <see cref="PropertyPath"/> /
/// <see cref="Expression"/> is set.
/// </summary>
internal sealed class ExclusionPartDefinition : IEquatable<ExclusionPartDefinition>
{
    /// <summary>Dotted property path (e.g. <c>Address.City</c>) resolved to a column name. Null for expression parts.</summary>
    [JsonPropertyName("path")] public string? PropertyPath { get; init; }

    /// <summary>Verbatim SQL fragment, emitted as-is inside parentheses. Null for column parts.</summary>
    [JsonPropertyName("expr")] public string? Expression { get; init; }

    /// <summary>The comparison operator rendered after <c>WITH</c>.</summary>
    [JsonPropertyName("op")] public string Operator { get; init; } = "";

    [JsonIgnore] public bool IsExpression => Expression is not null;

    public bool Equals(ExclusionPartDefinition? other) =>
        other is not null
     && PropertyPath == other.PropertyPath
     && Expression   == other.Expression
     && Operator     == other.Operator;

    public override bool Equals(object? obj) => Equals(obj as ExclusionPartDefinition);

    public override int GetHashCode() => HashCode.Combine(PropertyPath, Expression, Operator);
}

/// <summary>
/// An EXCLUDE constraint declared via <c>HasExclusionConstraint</c>: an ordered list of elements,
/// each compared with its own operator, optionally restricted by a <c>WHERE</c> predicate. Stored
/// as JSON on the entity type.
/// </summary>
internal sealed class ExclusionConstraintDefinition : IEquatable<ExclusionConstraintDefinition>
{
    /// <summary>The ordered constraint elements.</summary>
    [JsonPropertyName("parts")] public List<ExclusionPartDefinition> Parts { get; init; } = [];

    /// <summary>The index access method rendered after <c>USING</c>. Null means <c>gist</c>.</summary>
    [JsonPropertyName("method")] public string? Method { get; init; }

    /// <summary>Optional SQL predicate rendered as <c>WHERE (…)</c>.</summary>
    [JsonPropertyName("filter")] public string? Filter { get; init; }

    /// <summary>Optional explicit constraint name.</summary>
    [JsonPropertyName("name")] public string? Name { get; init; }

    /// <summary>Whether the constraint is <c>DEFERRABLE</c>.</summary>
    [JsonPropertyName("deferrable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Deferrable { get; init; }

    /// <summary>Whether a deferrable constraint is <c>INITIALLY DEFERRED</c>.</summary>
    [JsonPropertyName("initiallyDeferred")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool InitiallyDeferred { get; init; }

    public bool Equals(ExclusionConstraintDefinition? other) =>
        other is not null
     && Method            == other.Method
     && Filter            == other.Filter
     && Name              == other.Name
     && Deferrable        == other.Deferrable
     && InitiallyDeferred == other.InitiallyDeferred
     && Parts.SequenceEqual(other.Parts);

    public override bool Equals(object? obj) => Equals(obj as ExclusionConstraintDefinition);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var part in Parts) hash.Add(part);
        hash.Add(Method);
        hash.Add(Filter);
        hash.Add(Name);
        hash.Add(Deferrable);
        hash.Add(InitiallyDeferred);
        return hash.ToHashCode();
    }
}

internal static class ExclusionConstraintSerializer
{
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string Serialize(IReadOnlyList<ExclusionConstraintDefinition> definitions)
        => JsonSerializer.Serialize(definitions, Options);

    public static List<ExclusionConstraintDefinition> Deserialize(string json)
        => JsonSerializer.Deserialize<List<ExclusionConstraintDefinition>>(json, Options) ?? [];
}
