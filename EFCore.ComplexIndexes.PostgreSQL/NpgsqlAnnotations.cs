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
}