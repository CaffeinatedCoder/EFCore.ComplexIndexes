using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace EFCore.ComplexIndexes.SqlServer;

#pragma warning disable EF1001

/// <summary>
/// Extends <see cref="CustomMigrationsModelDiffer"/> for SQL Server: forwards the SQL Server index
/// options (clustered, INCLUDE, online, fill factor, …) that the provider's own SQL generator
/// renders, and rejects features SQL Server cannot express — expression parts (model a persisted
/// computed column and index that instead) and <c>NULLS FIRST</c>/<c>NULLS LAST</c>.
/// </summary>
public class SqlServerComplexIndexMigrationsModelDiffer(
    IRelationalTypeMappingSource     typeMappingSource,
    IMigrationsAnnotationProvider    migrationsAnnotationProvider,
    IRelationalAnnotationProvider    relationalAnnotationProvider,
    IRowIdentityMapFactory           rowIdentityMapFactory,
    CommandBatchPreparerDependencies commandBatchPreparerDependencies
) : CustomMigrationsModelDiffer(
    typeMappingSource,
    migrationsAnnotationProvider,
    relationalAnnotationProvider,
    rowIdentityMapFactory,
    commandBatchPreparerDependencies
)
{
    private static readonly HashSet<string> SupportedSqlServerAnnotations =
    [
        SqlServerAnnotations.Clustered,
        SqlServerAnnotations.Include,
        SqlServerAnnotations.CreatedOnline,
        SqlServerAnnotations.FillFactor,
        SqlServerAnnotations.SortInTempDb,
        SqlServerAnnotations.DataCompression
    ];

    /// <summary>Forwards exactly the SQL Server index-option annotations the provider's SQL generator renders.</summary>
    protected override bool IsForwardedIndexAnnotation(string annotationName)
        => SupportedSqlServerAnnotations.Contains(annotationName);

    /// <summary>SQL Server renames indexes standalone (<c>sp_rename</c>).</summary>
    protected override bool CanRenameIndexes => true;

    /// <summary>
    /// Resolves property paths inside INCLUDE lists to column names (verbatim fallback), and
    /// restores the data-compression enum after its JSON round trip.
    /// </summary>
    protected override object? TransformIndexAnnotation(
        IEntityType           entityType,
        string                annotationName,
        object?               value,
        StoreObjectIdentifier storeObject
    ) => annotationName switch
         {
             SqlServerAnnotations.Include         => ResolveIncludeList(entityType, value, storeObject),
             SqlServerAnnotations.DataCompression => CoerceDataCompression(value),
             _                                    => base.TransformIndexAnnotation(entityType, annotationName, value, storeObject)
         };

    // Entity-level index definitions are stored as JSON, which flattens the enum to a number. SQL
    // Server's generator reads this option as DataCompressionType? — a boxed int reads back as null,
    // so the option would silently vanish from the generated DDL.
    private static object? CoerceDataCompression(object? value) => value switch
    {
        DataCompressionType                                                                   => value,
        int i                                                                                 => (DataCompressionType)i,
        long l                                                                                => (DataCompressionType)l,
        string s when Enum.TryParse<DataCompressionType>(s, ignoreCase: true, out var parsed) => parsed,
        _                                                                                     => value
    };

    /// <summary>
    /// Rejects complex-index declarations SQL Server cannot express: PostgreSQL index options,
    /// expression parts, and <c>NULLS FIRST</c>/<c>NULLS LAST</c> ordering.
    /// </summary>
    protected override void ValidateCreateIndexOperation(CreateIndexOperation operation)
    {
        foreach (var annotation in operation.GetAnnotations())
        {
            if (annotation.Name.StartsWith("Npgsql:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Complex index '{operation.Name}' carries the PostgreSQL annotation '{annotation.Name}', but the " +
                    "model is diffed with the SQL Server satellite. Use the EFCore.ComplexIndexes.SqlServer options instead."
                );
            }
        }

        if (operation[ComplexIndexAnnotations.IndexParts] is not string partsJson)
            return;

        var parts = IndexPartsSerializer.Deserialize(partsJson);

        if (parts.Any(p => p.IsExpression))
        {
            throw new InvalidOperationException(
                $"Complex index '{operation.Name}' contains a SQL expression part. SQL Server has no expression-index " +
                "DDL — model the expression as a persisted computed column and index that column instead."
            );
        }

        if (parts.Any(p => p.NullSort != DbNullSort.Default))
        {
            throw new InvalidOperationException(
                $"Complex index '{operation.Name}' declares NULLS FIRST/LAST ordering, which SQL Server does not " +
                "support. Remove the DbOrder.NullsFirst/NullsLast marker."
            );
        }
    }
}

#pragma warning restore EF1001
