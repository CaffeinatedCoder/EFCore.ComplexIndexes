using Microsoft.EntityFrameworkCore;

namespace EFCore.ComplexIndexes.SqlServer;

/// <summary>
/// SQL Server-specific index options. These work on any index option builder implementing
/// <see cref="IIndexAnnotationBuilder"/> (e.g. <see cref="ComplexIndexBuilder"/>); the SQL Server
/// provider's own migrations SQL generator renders them, so no runtime wiring is required.
/// </summary>
public static class SqlServerComplexIndexBuilderExtensions
{
    /// <summary>Makes the index clustered (or explicitly nonclustered with <c>false</c>).</summary>
    public static TBuilder IsClustered<TBuilder>(this TBuilder builder, bool clustered = true) where TBuilder : IIndexAnnotationBuilder
        => builder.Set(SqlServerAnnotations.Clustered, clustered);

    /// <summary>Specifies non-key columns to include in the index (covering index).</summary>
    public static TBuilder IncludeProperties<TBuilder>(this TBuilder builder, params string[] properties) where TBuilder : IIndexAnnotationBuilder
        => builder.Set(SqlServerAnnotations.Include, properties);

    /// <summary>Builds the index online, keeping the table available during creation.</summary>
    public static TBuilder IsCreatedOnline<TBuilder>(this TBuilder builder, bool online = true) where TBuilder : IIndexAnnotationBuilder
        => builder.Set(SqlServerAnnotations.CreatedOnline, online);

    /// <summary>Sets the index fill factor (1–100).</summary>
    public static TBuilder HasFillFactor<TBuilder>(this TBuilder builder, int fillFactor) where TBuilder : IIndexAnnotationBuilder
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fillFactor, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fillFactor, 100);
        return builder.Set(SqlServerAnnotations.FillFactor, fillFactor);
    }

    /// <summary>Sorts intermediate results in tempdb during index creation.</summary>
    public static TBuilder SortInTempDb<TBuilder>(this TBuilder builder, bool sortInTempDb = true) where TBuilder : IIndexAnnotationBuilder
        => builder.Set(SqlServerAnnotations.SortInTempDb, sortInTempDb);

    /// <summary>Sets the index's data compression (<c>NONE</c>, <c>ROW</c>, or <c>PAGE</c>).</summary>
    public static TBuilder UseDataCompression<TBuilder>(this TBuilder builder, DataCompressionType dataCompression) where TBuilder : IIndexAnnotationBuilder
        => builder.Set(SqlServerAnnotations.DataCompression, dataCompression);

    private static TBuilder Set<TBuilder>(this TBuilder builder, string key, object? value) where TBuilder : IIndexAnnotationBuilder
    {
        builder.Annotations[key] = value;
        return builder;
    }
}
