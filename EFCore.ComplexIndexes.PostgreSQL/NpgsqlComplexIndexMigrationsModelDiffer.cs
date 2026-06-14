using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace EFCore.ComplexIndexes.PostgreSQL;

#pragma warning disable EF1001

/// <summary>
/// Extends <see cref="CustomMigrationsModelDiffer"/> to validate that provider annotations on complex
/// index operations use recognized Npgsql keys, and to emit PostgreSQL 18 temporal <c>UNIQUE</c>
/// constraints (<c>WITHOUT OVERLAPS</c>) declared via <c>HasTemporalConstraint</c>.
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

        return ApplyTemporalConstraints(operations, source, target);
    }

    // Diffs the temporal UNIQUE constraints declared on entity types and emits standalone
    // Add/DropUniqueConstraintOperations (the period column is a plain column EF doesn't otherwise
    // constrain). Adds carry the WITHOUT OVERLAPS marker for the SQL generator; a CREATE EXTENSION
    // btree_gist is auto-injected when a temporal constraint is being created.
    private static IReadOnlyList<MigrationOperation> ApplyTemporalConstraints(
        IReadOnlyList<MigrationOperation> operations,
        IRelationalModel?                 source,
        IRelationalModel?                 target
    )
    {
        var sourceConstraints = BuildDescriptors(source);
        var targetConstraints = BuildDescriptors(target);

        if (sourceConstraints.Count == 0 && targetConstraints.Count == 0)
            return operations;

        var result        = new List<MigrationOperation>(operations);
        var addedTemporal = false;

        foreach (var tgt in targetConstraints)
        {
            if (sourceConstraints.Contains(tgt))
                continue;

            var op = new AddUniqueConstraintOperation
                     {
                         Name    = tgt.Name,
                         Table   = tgt.Table,
                         Schema  = tgt.Schema,
                         Columns = [.. tgt.KeyColumns, tgt.PeriodColumn]
                     };
            op.AddAnnotation(NpgsqlTemporalAnnotations.WithoutOverlaps, tgt.PeriodColumn);
            result.Add(op);
            addedTemporal = true;
        }

        var droppedTables = operations
                           .OfType<DropTableOperation>()
                           .Select(o => (o.Name, o.Schema))
                           .ToHashSet();

        foreach (var src in sourceConstraints)
        {
            if (targetConstraints.Contains(src) || droppedTables.Contains((src.Table, src.Schema)))
                continue;

            result.Add(new DropUniqueConstraintOperation
                       {
                           Name   = src.Name,
                           Table  = src.Table,
                           Schema = src.Schema
                       });
        }

        if (addedTemporal && ShouldInjectExtension(target))
            result.Insert(0, new SqlOperation { Sql = $"CREATE EXTENSION IF NOT EXISTS {NpgsqlTemporalAnnotations.BtreeGistExtension};" });

        return result;
    }

    private static HashSet<TemporalDescriptor> BuildDescriptors(IRelationalModel? model)
    {
        var set = new HashSet<TemporalDescriptor>();
        if (model is null) return set;

        foreach (var entityType in model.Model.GetEntityTypes())
        {
            if (entityType.FindAnnotation(NpgsqlTemporalAnnotations.Constraints)?.Value is not string json
             || string.IsNullOrEmpty(json))
                continue;

            var table = entityType.GetTableName();
            if (table is null) continue;

            var schema      = entityType.GetSchema();
            var storeObject = StoreObjectIdentifier.Table(table, schema);

            foreach (var def in TemporalConstraintSerializer.Deserialize(json))
            {
                var keyColumns = new List<string>(def.KeyProperties.Count);
                foreach (var keyProperty in def.KeyProperties)
                {
                    keyColumns.Add(
                        ResolveColumn(entityType, keyProperty, storeObject)
                     ?? throw new InvalidOperationException(
                            $"Could not resolve temporal constraint key column '{keyProperty}' on entity {entityType.Name}.")
                    );
                }

                var periodColumn = ResolveColumn(entityType, def.PeriodProperty, storeObject)
                                ?? throw new InvalidOperationException(
                                       $"Could not resolve temporal constraint period column '{def.PeriodProperty}' on entity {entityType.Name}.");

                var name = def.Name ?? $"AK_{table}_{string.Join("_", keyColumns)}_{periodColumn}";

                set.Add(new TemporalDescriptor(table, schema, name, keyColumns, periodColumn));
            }
        }

        return set;
    }

    private static string? ResolveColumn(IEntityType entityType, string dotPath, StoreObjectIdentifier storeObject)
    {
        var       parts   = dotPath.Split('.');
        ITypeBase current = entityType;

        for (var i = 0; i < parts.Length; i++)
        {
            if (i == parts.Length - 1)
                return current.FindProperty(parts[i])?.GetColumnName(storeObject);

            var cp = current.FindComplexProperty(parts[i]);
            if (cp is null) return null;
            current = cp.ComplexType;
        }

        return null;
    }

    private static bool ShouldInjectExtension(IRelationalModel? target)
    {
        if (target is null)
            return false;

        if (target.Model.FindAnnotation(NpgsqlTemporalAnnotations.SuppressAutoExtension)?.Value is true)
            return false;

        // If the extension is declared via HasPostgresExtension (e.g. UseBtreeGist()), Npgsql's own
        // differ already emits CREATE EXTENSION, so we must not duplicate it.
        var alreadyDeclared = target.Model
                                    .GetAnnotations()
                                    .Any(a => a.Name.StartsWith("Npgsql:PostgresExtension", StringComparison.Ordinal)
                                           && a.Name.Contains(NpgsqlTemporalAnnotations.BtreeGistExtension, StringComparison.Ordinal));

        return !alreadyDeclared;
    }

    private sealed record TemporalDescriptor(
        string                Table,
        string?               Schema,
        string                Name,
        IReadOnlyList<string> KeyColumns,
        string                PeriodColumn)
    {
        public bool Equals(TemporalDescriptor? other) =>
            other is not null
         && Table        == other.Table
         && Schema       == other.Schema
         && Name         == other.Name
         && PeriodColumn == other.PeriodColumn
         && KeyColumns.SequenceEqual(other.KeyColumns);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Table);
            hash.Add(Schema);
            hash.Add(Name);
            foreach (var column in KeyColumns) hash.Add(column);
            hash.Add(PeriodColumn);
            return hash.ToHashCode();
        }
    }
}

#pragma warning restore EF1001
