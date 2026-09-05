namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// Npgsql annotation key constants for PostgreSQL index features.
/// These mirror <c>NpgsqlAnnotationNames</c> from the Npgsql provider.
/// </summary>
internal static class NpgsqlAnnotations
{
    public const string IndexMethod         = "Npgsql:IndexMethod";
    public const string IndexOperators      = "Npgsql:IndexOperators";
    public const string IndexInclude        = "Npgsql:IndexInclude";
    public const string IndexSortOrder      = "Npgsql:IndexSortOrder";
    public const string IndexNullSortOrder  = "Npgsql:IndexNullSortOrder";
    public const string CreatedConcurrently = "Npgsql:CreatedConcurrently";
    public const string NullsDistinct       = "Npgsql:NullsDistinct";

    /// <summary>
    /// Prefix of the per-parameter storage-parameter keys (<c>Npgsql:StorageParameter:fillfactor</c>, …).
    /// Npgsql's generator renders every operation annotation under this prefix as <c>WITH (name=value)</c>.
    /// </summary>
    public const string StorageParameterPrefix = "Npgsql:StorageParameter:";

    public static bool IsStorageParameter(string annotationName)
        => annotationName.StartsWith(StorageParameterPrefix, StringComparison.Ordinal);
}