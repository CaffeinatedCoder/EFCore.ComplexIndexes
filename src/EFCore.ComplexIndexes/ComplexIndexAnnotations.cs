namespace EFCore.ComplexIndexes;

public static class ComplexIndexAnnotations
{
    public const string IsIndexed        = "CustomIndex:IsIndexed";
    public const string IsUnique         = "CustomIndex:IsUnique";
    public const string Filter           = "CustomIndex:Filter";
    public const string IndexName        = "CustomIndex:Name";
    public const string CompositeIndexes = "CustomIndex:CompositeIndexes";

    /// <summary>
    /// Stamped onto a <c>CreateIndexOperation</c> by the differ to carry the ordered
    /// list of index parts (columns and/or raw SQL expressions) as JSON. Provider SQL
    /// generators read this to render expression indexes.
    /// </summary>
    public const string IndexParts = "CustomIndex:IndexParts";
}