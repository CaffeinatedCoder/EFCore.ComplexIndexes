using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace EFCore.ComplexIndexes.PostgreSQL;

#pragma warning disable EF1001

/// <summary>
/// Extends <see cref="CustomMigrationsModelDiffer"/> to validate that
/// provider annotations on complex index operations use recognized Npgsql keys.
/// </summary>
public class NpgsqlComplexIndexMigrationsModelDiffer(
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
    private static readonly HashSet<string> SupportedNpgsqlAnnotations =
    [
        NpgsqlAnnotations.IndexMethod,
        NpgsqlAnnotations.IndexOperators,
        NpgsqlAnnotations.IndexInclude,
        NpgsqlAnnotations.IndexSortOrder,
        NpgsqlAnnotations.IndexNullSortOrder,
        NpgsqlAnnotations.CreatedConcurrently,
        NpgsqlAnnotations.NullsDistinct
    ];

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
                if (annotation.Name.StartsWith("Npgsql:", StringComparison.Ordinal)
                 && !SupportedNpgsqlAnnotations.Contains(annotation.Name))
                {
                    throw new InvalidOperationException(
                        $"Unrecognized Npgsql index annotation '{annotation.Name}' on index '{op.Name}'. " +
                        $"Supported annotations: {string.Join(", ", SupportedNpgsqlAnnotations)}."
                    );
                }
            }
        }

        return operations;
    }
}

#pragma warning restore EF1001