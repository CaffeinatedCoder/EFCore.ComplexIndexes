namespace EFCore.ComplexIndexes;

/// <summary>
/// Marker functions for declaring per-column sort direction inside a composite-index selector,
/// e.g. <c>x => new { x.Name, DbOrder.Desc(x.Created) }</c>. They are identity functions — at
/// runtime they return their argument unchanged; they exist only to be recognized in the
/// expression tree. Unwrapped members default to ascending.
/// </summary>
public static class DbOrder
{
    /// <summary>Marks the column as ascending (the default; usually omitted).</summary>
    public static T Asc<T>(T column) => column;

    /// <summary>Marks the column as descending.</summary>
    public static T Desc<T>(T column) => column;
}
