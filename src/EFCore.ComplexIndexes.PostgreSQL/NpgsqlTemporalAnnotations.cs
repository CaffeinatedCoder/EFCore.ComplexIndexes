namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// Annotation key constants for PostgreSQL 18 temporal constraints (<c>WITHOUT OVERLAPS</c>).
/// These use the <c>CustomTemporal:</c> prefix so they never collide with the <c>Npgsql:</c>
/// keys validated on index operations.
/// </summary>
internal static class NpgsqlTemporalAnnotations
{
    /// <summary>
    /// Stamped on an entity type to hold the JSON-serialized list of temporal UNIQUE constraints
    /// declared via <c>HasTemporalConstraint</c>. The period (range) column stays a plain mapped
    /// column — it is deliberately not part of any EF key, because EF forbids non-comparable range
    /// types in keys.
    /// </summary>
    public const string Constraints = "CustomTemporal:Constraints";

    /// <summary>
    /// Stamped by the differ onto an <c>AddUniqueConstraintOperation</c> to carry the resolved column
    /// name that must be rendered with <c>WITHOUT OVERLAPS</c>.
    /// </summary>
    public const string WithoutOverlaps = "CustomTemporal:WithoutOverlaps";

    /// <summary>
    /// Stamped on a dependent entity type to hold the JSON-serialized list of temporal foreign keys
    /// declared via <c>HasTemporalForeignKey</c>.
    /// </summary>
    public const string ForeignKeys = "CustomTemporal:ForeignKeys";

    /// <summary>
    /// Stamped by the differ onto an <c>AddForeignKeyOperation</c> to carry the dependent range column
    /// rendered as <c>PERIOD dependent_period</c>.
    /// </summary>
    public const string ForeignKeyDependentPeriod = "CustomTemporal:ForeignKeyDependentPeriod";

    /// <summary>
    /// Stamped by the differ onto an <c>AddForeignKeyOperation</c> to carry the principal range column
    /// rendered as <c>PERIOD principal_period</c>.
    /// </summary>
    public const string ForeignKeyPrincipalPeriod = "CustomTemporal:ForeignKeyPrincipalPeriod";

    /// <summary>
    /// Stamped on the model to opt out of automatic <c>CREATE EXTENSION btree_gist</c> injection.
    /// </summary>
    public const string SuppressAutoExtension = "CustomTemporal:SuppressAutoExtension";

    /// <summary>The PostgreSQL extension required by temporal constraints over scalar columns.</summary>
    public const string BtreeGistExtension = "btree_gist";
}
