namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// Builds a PostgreSQL expression index: an ordered list of parts, each a verbatim SQL fragment.
/// Provider-specific options (e.g. <c>UseGin</c>) are available as extension methods via
/// <see cref="IIndexAnnotationBuilder"/>.
/// </summary>
/// <remarks>
/// Every part is supplied as raw SQL and emitted verbatim — no property-to-column resolution and
/// no identifier quoting.
/// </remarks>
public sealed class ExpressionIndexBuilder : IIndexAnnotationBuilder
{
    private readonly List<IndexPartDefinition> _parts = [];

    internal Dictionary<string, object?> Annotations { get; } = new();

    internal IReadOnlyList<IndexPartDefinition> Parts => _parts;

    Dictionary<string, object?> IIndexAnnotationBuilder.Annotations => Annotations;

    /// <summary>Adds a verbatim SQL fragment as the next part of the index's column list.</summary>
    public ExpressionIndexBuilder Expression(string sql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        _parts.Add(new IndexPartDefinition { Expression = sql });
        return this;
    }

    /// <summary>Adds a part from an <see cref="IIndexExpression"/>, resolving it to its SQL fragment.</summary>
    public ExpressionIndexBuilder Expression(IIndexExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return Expression(expression.ToSql());
    }

    /// <summary>Marks the index as unique.</summary>
    public ExpressionIndexBuilder IsUnique(bool unique = true)
    {
        Annotations[ComplexIndexAnnotations.IsUnique] = unique;
        return this;
    }

    /// <summary>Applies a SQL filter (partial index) expression.</summary>
    public ExpressionIndexBuilder HasFilter(string filter)
    {
        Annotations[ComplexIndexAnnotations.Filter] = filter;
        return this;
    }

    /// <summary>Sets a custom name for the index.</summary>
    public ExpressionIndexBuilder HasName(string name)
    {
        Annotations[ComplexIndexAnnotations.IndexName] = name;
        return this;
    }
}
