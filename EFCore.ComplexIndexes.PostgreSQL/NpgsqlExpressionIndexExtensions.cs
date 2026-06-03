using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// PostgreSQL expression-index API. Expression indexes are provider-specific (PostgreSQL renders
/// <c>CREATE INDEX … ((expr))</c> natively; other providers model the same intent differently, e.g.
/// SQL Server via persisted computed columns), so the entry point lives in the provider package
/// rather than the provider-agnostic core.
/// </summary>
public static class NpgsqlExpressionIndexExtensions
{
    /// <summary>
    /// Configures an index whose single entry is a verbatim SQL expression (e.g. <c>lower(name)</c>).
    /// The expression is emitted exactly as given — it must reference real column names.
    /// Requires runtime wiring via <c>UseNpgsqlComplexIndexes()</c>.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasExpressionIndex<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        string                          expression,
        bool                            isUnique  = false,
        string?                         filter    = null,
        string?                         indexName = null
    )
        where TEntity : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        var definition = new CompositeIndexDefinition
                         {
                             Parts     = [new IndexPartDefinition { Expression = expression }],
                             IsUnique  = isUnique,
                             Filter    = filter,
                             IndexName = indexName
                         };

        ComplexIndexStorage.AddOrReplace(builder, definition);
        return builder;
    }

    /// <summary>
    /// Configures an index from an ordered list of verbatim SQL parts using a builder callback.
    /// Provider-specific options (GIN, INCLUDE, …) are available as extension methods on
    /// <see cref="ExpressionIndexBuilder"/>. Requires runtime wiring via <c>UseNpgsqlComplexIndexes()</c>.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasExpressionIndex<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Action<ExpressionIndexBuilder>  configure
    )
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(configure);

        var indexBuilder = new ExpressionIndexBuilder();
        configure(indexBuilder);

        if (indexBuilder.Parts.Count == 0)
            throw new ArgumentException(
                "An expression index requires at least one part. Call Expression(...) at least once."
            );

        var annotations = indexBuilder.Annotations;

        var providerAnnotations = annotations
                                 .Where(kv => kv.Key != ComplexIndexAnnotations.IsUnique
                                           && kv.Key != ComplexIndexAnnotations.Filter
                                           && kv.Key != ComplexIndexAnnotations.IndexName)
                                 .ToDictionary(kv => kv.Key, kv => kv.Value);

        var definition = new CompositeIndexDefinition
                         {
                             Parts               = [.. indexBuilder.Parts],
                             IsUnique            = annotations.TryGetValue(ComplexIndexAnnotations.IsUnique, out var u) && u is true,
                             Filter              = annotations.GetValueOrDefault(ComplexIndexAnnotations.Filter) as string,
                             IndexName           = annotations.GetValueOrDefault(ComplexIndexAnnotations.IndexName) as string,
                             ProviderAnnotations = providerAnnotations.Count > 0 ? providerAnnotations : null
                         };

        ComplexIndexStorage.AddOrReplace(builder, definition);
        return builder;
    }
}
