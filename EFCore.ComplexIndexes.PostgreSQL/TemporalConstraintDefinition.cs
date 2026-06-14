using System.Text.Json;
using System.Text.Json.Serialization;

namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// A temporal UNIQUE constraint declared via <c>HasTemporalConstraint</c>: a set of scalar key
/// columns plus a trailing range (period) column rendered with <c>WITHOUT OVERLAPS</c>. Stored as
/// JSON on the entity type; the period column is a plain mapped column, never part of an EF key.
/// </summary>
internal sealed class TemporalConstraintDefinition : IEquatable<TemporalConstraintDefinition>
{
    /// <summary>Dotted property paths of the scalar key columns, in order.</summary>
    [JsonPropertyName("keys")] public List<string> KeyProperties { get; init; } = [];

    /// <summary>Dotted property path of the range (period) column — emitted last with WITHOUT OVERLAPS.</summary>
    [JsonPropertyName("period")] public string PeriodProperty { get; init; } = "";

    /// <summary>Optional explicit constraint name.</summary>
    [JsonPropertyName("name")] public string? Name { get; init; }

    public bool Equals(TemporalConstraintDefinition? other) =>
        other is not null
     && PeriodProperty == other.PeriodProperty
     && Name           == other.Name
     && KeyProperties.SequenceEqual(other.KeyProperties);

    public override bool Equals(object? obj) => Equals(obj as TemporalConstraintDefinition);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var key in KeyProperties) hash.Add(key);
        hash.Add(PeriodProperty);
        hash.Add(Name);
        return hash.ToHashCode();
    }
}

internal static class TemporalConstraintSerializer
{
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string Serialize(IReadOnlyList<TemporalConstraintDefinition> definitions)
        => JsonSerializer.Serialize(definitions, Options);

    public static List<TemporalConstraintDefinition> Deserialize(string json)
        => JsonSerializer.Deserialize<List<TemporalConstraintDefinition>>(json, Options) ?? [];
}
