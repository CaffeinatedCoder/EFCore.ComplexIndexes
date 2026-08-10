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

    public override IReadOnlyList<MigrationOperation> GetDifferences(
        IRelationalModel? source,
        IRelationalModel? target
    )
    {
        var operations = base.GetDifferences(source, target);

        foreach (var op in operations.OfType<CreateIndexOperation>())
        {
            foreach (var annotation in op.GetAnnotations())
            {
                if (annotation.Name.StartsWith("Npgsql:", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Index '{op.Name}' carries the PostgreSQL annotation '{annotation.Name}', but the model " +
                        "is diffed with the SQL Server satellite. Use the EFCore.ComplexIndexes.SqlServer options instead."
                    );
                }
            }

            if (op[ComplexIndexAnnotations.IndexParts] is not string partsJson)
                continue;

            var parts = IndexPartsSerializer.Deserialize(partsJson);

            if (parts.Any(p => p.IsExpression))
            {
                throw new InvalidOperationException(
                    $"Index '{op.Name}' contains a SQL expression part. SQL Server has no expression-index DDL — " +
                    "model the expression as a persisted computed column and index that column instead."
                );
            }

            if (parts.Any(p => p.NullSort != DbNullSort.Default))
            {
                throw new InvalidOperationException(
                    $"Index '{op.Name}' declares NULLS FIRST/LAST ordering, which SQL Server does not support. " +
                    "Remove the DbOrder.NullsFirst/NullsLast marker."
                );
            }
        }

        return operations;
    }
}

#pragma warning restore EF1001
