namespace EFCore.ComplexIndexes;

/// <summary>
/// Represents a single part of an index's column list that is rendered as a SQL
/// expression rather than a plain column reference (e.g., <c>lower(name)</c>).
/// </summary>
/// <remarks>
/// This is the extension seam for expression indexes. The only implementation shipped
/// today is <see cref="SqlIndexExpression"/>, which carries a verbatim SQL fragment.
/// Provider satellites (or a future LINQ add-on) may add implementations whose
/// <see cref="ToSql"/> produces the final SQL fragment; everything downstream — the
/// migration differ and the provider SQL generators — only ever sees that string.
/// </remarks>
public interface IIndexExpression
{
    /// <summary>
    /// Returns the raw SQL fragment for this part, exactly as it should appear inside
    /// the index's parenthesized column list (without surrounding parentheses).
    /// </summary>
    string ToSql();
}

/// <summary>
/// An <see cref="IIndexExpression"/> backed by a verbatim SQL fragment supplied by the user.
/// </summary>
/// <param name="Sql">
/// The final SQL fragment. It is emitted verbatim — no property-to-column resolution and
/// no identifier quoting is applied, so it must already reference real column names.
/// </param>
public sealed record SqlIndexExpression(string Sql) : IIndexExpression
{
    /// <inheritdoc />
    public string ToSql() => Sql;
}
