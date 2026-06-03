namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// PostgreSQL-specific index options. These work on any index option builder
/// (<see cref="ComplexIndexBuilder"/> for complex-property indexes and
/// <see cref="ExpressionIndexBuilder"/> for expression indexes) via <see cref="IIndexAnnotationBuilder"/>.
/// </summary>
public static class NpgsqlComplexIndexBuilderExtensions
{
    /// <summary>Creates a GIN index — ideal for full-text search, JSONB, and array columns.</summary>
    public static TBuilder UseGin<TBuilder>(this TBuilder builder) where TBuilder : IIndexAnnotationBuilder
        => builder.Set(NpgsqlAnnotations.IndexMethod, "gin");

    /// <summary>Creates a GiST index — ideal for geometric, range, and full-text search columns.</summary>
    public static TBuilder UseGist<TBuilder>(this TBuilder builder) where TBuilder : IIndexAnnotationBuilder
        => builder.Set(NpgsqlAnnotations.IndexMethod, "gist");

    /// <summary>Creates a BRIN index — ideal for large, naturally ordered tables (e.g., time-series data).</summary>
    public static TBuilder UseBrin<TBuilder>(this TBuilder builder) where TBuilder : IIndexAnnotationBuilder
        => builder.Set(NpgsqlAnnotations.IndexMethod, "brin");

    /// <summary>Creates a hash index — useful for simple equality comparisons.</summary>
    public static TBuilder UseHash<TBuilder>(this TBuilder builder) where TBuilder : IIndexAnnotationBuilder
        => builder.Set(NpgsqlAnnotations.IndexMethod, "hash");

    /// <summary>Creates an SP-GiST index — ideal for partitioned search trees (e.g., IP addresses, phone numbers).</summary>
    public static TBuilder UseSpGist<TBuilder>(this TBuilder builder) where TBuilder : IIndexAnnotationBuilder
        => builder.Set(NpgsqlAnnotations.IndexMethod, "spgist");

    /// <summary>Specifies per-column operator classes for the index (e.g., <c>jsonb_path_ops</c>).</summary>
    public static TBuilder HasOperators<TBuilder>(this TBuilder builder, params string[] operators) where TBuilder : IIndexAnnotationBuilder
        => builder.Set(NpgsqlAnnotations.IndexOperators, operators);

    /// <summary>Specifies non-key columns to include in the index (covering index).</summary>
    public static TBuilder IncludeProperties<TBuilder>(this TBuilder builder, params string[] properties) where TBuilder : IIndexAnnotationBuilder
        => builder.Set(NpgsqlAnnotations.IndexInclude, properties);

    /// <summary>Specifies that the index should be created concurrently (non-blocking).</summary>
    public static TBuilder IsCreatedConcurrently<TBuilder>(this TBuilder builder, bool concurrent = true) where TBuilder : IIndexAnnotationBuilder
        => builder.Set(NpgsqlAnnotations.CreatedConcurrently, concurrent);

    /// <summary>
    /// Specifies whether null values are considered distinct in a unique index.
    /// When <c>false</c>, multiple nulls violate the uniqueness constraint.
    /// </summary>
    public static TBuilder AreNullsDistinct<TBuilder>(this TBuilder builder, bool nullsDistinct = true) where TBuilder : IIndexAnnotationBuilder
        => builder.Set(NpgsqlAnnotations.NullsDistinct, nullsDistinct);

    private static TBuilder Set<TBuilder>(this TBuilder builder, string key, object? value) where TBuilder : IIndexAnnotationBuilder
    {
        builder.Annotations[key] = value;
        return builder;
    }
}
