namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// Annotation key constants for PostgreSQL EXCLUDE constraints declared via
/// <c>HasExclusionConstraint</c>. These use the <c>CustomExclusion:</c> prefix so they never
/// collide with the <c>Npgsql:</c> keys validated on index operations.
/// </summary>
internal static class NpgsqlExclusionAnnotations
{
    /// <summary>
    /// Stamped on an entity type to hold the JSON-serialized list of exclusion constraints
    /// declared via <c>HasExclusionConstraint</c>.
    /// </summary>
    public const string Constraints = "CustomExclusion:Constraints";
}
