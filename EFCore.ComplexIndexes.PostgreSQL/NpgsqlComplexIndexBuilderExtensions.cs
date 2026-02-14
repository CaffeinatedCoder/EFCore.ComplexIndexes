namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// PostgreSQL-specific extension methods for <see cref="ComplexIndexBuilder"/>.
/// </summary>
public static class NpgsqlComplexIndexBuilderExtensions
{
    /// <summary>Creates a GIN index — ideal for full-text search, JSONB, and array columns.</summary>
    public static ComplexIndexBuilder UseGin(this ComplexIndexBuilder builder)
        => builder.HasAnnotation(NpgsqlAnnotations.IndexMethod, "gin");

    /// <summary>Creates a GiST index — ideal for geometric, range, and full-text search columns.</summary>
    public static ComplexIndexBuilder UseGist(this ComplexIndexBuilder builder)
        => builder.HasAnnotation(NpgsqlAnnotations.IndexMethod, "gist");

    /// <summary>Creates a BRIN index — ideal for large, naturally ordered tables (e.g., time-series data).</summary>
    public static ComplexIndexBuilder UseBrin(this ComplexIndexBuilder builder)
        => builder.HasAnnotation(NpgsqlAnnotations.IndexMethod, "brin");

    /// <summary>Creates a hash index — useful for simple equality comparisons.</summary>
    public static ComplexIndexBuilder UseHash(this ComplexIndexBuilder builder)
        => builder.HasAnnotation(NpgsqlAnnotations.IndexMethod, "hash");

    /// <summary>Creates an SP-GiST index — ideal for partitioned search trees (e.g., IP addresses, phone numbers).</summary>
    public static ComplexIndexBuilder UseSpGist(this ComplexIndexBuilder builder)
        => builder.HasAnnotation(NpgsqlAnnotations.IndexMethod, "spgist");

    /// <summary>
    /// Specifies per-column operator classes for the index (e.g., <c>jsonb_path_ops</c>).
    /// </summary>
    public static ComplexIndexBuilder HasOperators(this ComplexIndexBuilder builder, params string[] operators)
        => builder.HasAnnotation(NpgsqlAnnotations.IndexOperators, operators);

    /// <summary>
    /// Specifies non-key columns to include in the index (covering index).
    /// </summary>
    public static ComplexIndexBuilder IncludeProperties(this ComplexIndexBuilder builder, params string[] properties)
        => builder.HasAnnotation(NpgsqlAnnotations.IndexInclude, properties);

    /// <summary>
    /// Specifies that the index should be created concurrently (non-blocking).
    /// </summary>
    public static ComplexIndexBuilder IsCreatedConcurrently(this ComplexIndexBuilder builder, bool concurrent = true)
        => builder.HasAnnotation(NpgsqlAnnotations.CreatedConcurrently, concurrent);

    /// <summary>
    /// Specifies whether null values are considered distinct in a unique index.
    /// When <c>false</c>, multiple nulls violate the uniqueness constraint.
    /// </summary>
    public static ComplexIndexBuilder AreNullsDistinct(this ComplexIndexBuilder builder, bool nullsDistinct = true)
        => builder.HasAnnotation(NpgsqlAnnotations.NullsDistinct, nullsDistinct);
}