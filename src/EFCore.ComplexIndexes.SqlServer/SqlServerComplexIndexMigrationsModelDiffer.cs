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
    )
    {
        if (annotationName == SqlServerAnnotations.Clustered && value is true)
            ValidateClusteredSlotIsFree(entityType);

        return annotationName switch
               {
                   SqlServerAnnotations.Include         => ResolveIncludeList(entityType, value, storeObject),
                   SqlServerAnnotations.DataCompression => CoerceDataCompression(value),
                   _                                    => base.TransformIndexAnnotation(entityType, annotationName, value, storeObject)
               };
    }

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

        ValidateClusteredCombination(operation);

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

    /// <summary>
    /// Rejects clustered-index combinations SQL Server does not accept. These render into perfectly
    /// well-formed-looking DDL that the server refuses at apply time, so catching them at
    /// <c>migrations add</c> turns a late, cryptic failure into an actionable one.
    /// </summary>
    private static void ValidateClusteredCombination(CreateIndexOperation operation)
    {
        if (operation[SqlServerAnnotations.Clustered] is not true)
            return;

        // INCLUDE covers non-key columns of a nonclustered index; a clustered index already stores
        // every column in its leaf level, so the syntax is rejected outright.
        if (operation[SqlServerAnnotations.Include] is System.Collections.IEnumerable include
         && include.Cast<object?>().Any())
        {
            throw new InvalidOperationException(
                $"Complex index '{operation.Name}' is clustered and declares INCLUDE columns. SQL Server allows "
              + "included columns only on nonclustered indexes — a clustered index already stores every column. "
              + "Drop IncludeProperties(...) or IsClustered().");
        }

        // Filtered indexes must be nonclustered.
        if (!string.IsNullOrEmpty(operation.Filter))
        {
            throw new InvalidOperationException(
                $"Complex index '{operation.Name}' is clustered and declares a filter. SQL Server allows filtered "
              + "indexes only on nonclustered indexes. Drop the filter or IsClustered().");
        }
    }

    /// <summary>
    /// A table can carry at most one clustered index, so two clustered complex indexes on one table
    /// cannot both be created.
    /// </summary>
    protected override void ValidateCreatedIndexes(IReadOnlyList<CreateIndexOperation> operations)
    {
        var collision = operations
                       .Where(o => o[SqlServerAnnotations.Clustered] is true)
                       .GroupBy(o => (o.Table, o.Schema))
                       .FirstOrDefault(g => g.Count() > 1);

        if (collision is null)
            return;

        throw new InvalidOperationException(
            $"Table '{collision.Key.Table}' declares {collision.Count()} clustered complex indexes "
          + $"({string.Join(", ", collision.Select(o => $"'{o.Name}'"))}). SQL Server allows at most one "
          + "clustered index per table — the others must be nonclustered.");
    }

    /// <summary>
    /// Rejects a clustered complex index on a table whose clustered slot is already taken by the
    /// primary key or a native index.
    /// </summary>
    /// <remarks>
    /// This lives on the annotation-transform path because that is the only hook with access to the
    /// entity type; the operation alone cannot see the table's keys and native indexes. It matters
    /// disproportionately: the SQL Server provider makes a primary key clustered unless told
    /// otherwise, so on a conventionally mapped entity <em>every</em> clustered complex index
    /// conflicts, and without this check the failure only appears when the migration is applied.
    /// </remarks>
    private static void ValidateClusteredSlotIsFree(IEntityType entityType)
    {
        var primaryKey = entityType.FindPrimaryKey();

        if (primaryKey is not null && primaryKey.IsClustered() != false)
            throw new InvalidOperationException(
                $"A clustered complex index is declared on '{entityType.Name}', but its primary key is clustered "
              + "(the SQL Server default). A table can carry only one clustered index — declare the key with "
              + "HasKey(...).IsClustered(false), or make the complex index nonclustered.");

        var clusteredIndex = entityType.GetIndexes().FirstOrDefault(i => i.IsClustered() == true);

        if (clusteredIndex is not null)
            throw new InvalidOperationException(
                $"A clustered complex index is declared on '{entityType.Name}', but the native index on "
              + $"({string.Join(", ", clusteredIndex.Properties.Select(p => p.Name))}) is already clustered. "
              + "A table can carry only one clustered index.");
    }
}

#pragma warning restore EF1001
