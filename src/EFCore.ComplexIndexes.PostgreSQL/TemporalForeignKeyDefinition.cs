using System.Text.Json;
using System.Text.Json.Serialization;

namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// A temporal FOREIGN KEY declared via <c>HasTemporalForeignKey</c>: matching scalar key columns
/// plus dependent/principal range (period) columns rendered with PostgreSQL's <c>PERIOD</c> marker.
/// Stored as JSON on the dependent entity type.
/// </summary>
internal sealed class TemporalForeignKeyDefinition : IEquatable<TemporalForeignKeyDefinition>
{
    /// <summary>Dotted property paths of the dependent scalar key columns, in order.</summary>
    [JsonPropertyName("dependentKeys")] public List<string> DependentKeyProperties { get; init; } = [];

    /// <summary>Dotted property path of the dependent range (period) column.</summary>
    [JsonPropertyName("dependentPeriod")] public string DependentPeriodProperty { get; init; } = "";

    /// <summary>EF entity-type name of the principal entity.</summary>
    [JsonPropertyName("principalEntity")] public string PrincipalEntityType { get; init; } = "";

    /// <summary>Dotted property paths of the principal scalar key columns, in order.</summary>
    [JsonPropertyName("principalKeys")] public List<string> PrincipalKeyProperties { get; init; } = [];

    /// <summary>Dotted property path of the principal range (period) column.</summary>
    [JsonPropertyName("principalPeriod")] public string PrincipalPeriodProperty { get; init; } = "";

    /// <summary>Optional explicit constraint name.</summary>
    [JsonPropertyName("name")] public string? Name { get; init; }

    public bool Equals(TemporalForeignKeyDefinition? other) =>
        other is not null
     && DependentPeriodProperty == other.DependentPeriodProperty
     && PrincipalEntityType     == other.PrincipalEntityType
     && PrincipalPeriodProperty == other.PrincipalPeriodProperty
     && Name                    == other.Name
     && DependentKeyProperties.SequenceEqual(other.DependentKeyProperties)
     && PrincipalKeyProperties.SequenceEqual(other.PrincipalKeyProperties);

    public override bool Equals(object? obj) => Equals(obj as TemporalForeignKeyDefinition);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var key in DependentKeyProperties) hash.Add(key);
        hash.Add(DependentPeriodProperty);
        hash.Add(PrincipalEntityType);
        foreach (var key in PrincipalKeyProperties) hash.Add(key);
        hash.Add(PrincipalPeriodProperty);
        hash.Add(Name);
        return hash.ToHashCode();
    }
}

internal static class TemporalForeignKeySerializer
{
    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static string Serialize(IReadOnlyList<TemporalForeignKeyDefinition> definitions)
        => JsonSerializer.Serialize(definitions, Options);

    public static List<TemporalForeignKeyDefinition> Deserialize(string json)
        => JsonSerializer.Deserialize<List<TemporalForeignKeyDefinition>>(json, Options) ?? [];
}
