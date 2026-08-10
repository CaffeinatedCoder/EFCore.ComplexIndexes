namespace EFCore.ComplexIndexes;

/// <summary>
/// Marker functions for declaring per-column sort options inside an index selector,
/// e.g. <c>x => new { x.Name, DbOrder.Desc(x.Created) }</c>. They are identity functions — at
/// runtime they return their argument unchanged; they exist only to be recognized in the
/// expression tree. Unwrapped members default to ascending with the database's default null
/// ordering. Markers compose in any order: <c>DbOrder.NullsLast(DbOrder.Desc(x.B))</c>.
/// </summary>
public static class DbOrder
{
    /// <summary>Marks the column as ascending (the default; usually omitted).</summary>
    public static T Asc<T>(T column) => column;

    /// <summary>Marks the column as descending.</summary>
    public static T Desc<T>(T column) => column;

    /// <summary>
    /// Sorts nulls before non-null values. Provider-specific: rendered by the PostgreSQL package
    /// (requires the <c>UseNpgsqlComplexIndexes()</c> runtime wiring); SQL Server has no
    /// <c>NULLS FIRST</c>/<c>NULLS LAST</c> syntax and its differ rejects it.
    /// </summary>
    public static T NullsFirst<T>(T column) => column;

    /// <summary>
    /// Sorts nulls after non-null values. Provider-specific: rendered by the PostgreSQL package
    /// (requires the <c>UseNpgsqlComplexIndexes()</c> runtime wiring); SQL Server has no
    /// <c>NULLS FIRST</c>/<c>NULLS LAST</c> syntax and its differ rejects it.
    /// </summary>
    public static T NullsLast<T>(T column) => column;
}

/// <summary>Per-part null ordering. <see cref="Default"/> leaves it to the database.</summary>
public enum DbNullSort
{
    /// <summary>Database default (PostgreSQL: NULLS LAST for ASC, NULLS FIRST for DESC).</summary>
    Default = 0,

    /// <summary>NULLS FIRST.</summary>
    First = 1,

    /// <summary>NULLS LAST.</summary>
    Last = 2
}
