using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace EFCore.ComplexIndexes;

#pragma warning disable EF1001

public class CustomMigrationsModelDiffer(
    IRelationalTypeMappingSource     typeMappingSource,
    IMigrationsAnnotationProvider    migrationsAnnotationProvider,
    IRelationalAnnotationProvider    relationalAnnotationProvider,
    IRowIdentityMapFactory           rowIdentityMapFactory,
    CommandBatchPreparerDependencies commandBatchPreparerDependencies
)
    : MigrationsModelDiffer(
        typeMappingSource,
        migrationsAnnotationProvider,
        relationalAnnotationProvider,
        rowIdentityMapFactory,
        commandBatchPreparerDependencies
    )
{
    public override IReadOnlyList<MigrationOperation> GetDifferences(
        IRelationalModel? source,
        IRelationalModel? target
    )
    {
        var operations = base.GetDifferences(source, target).ToList();

        var sourceIndexes = ExtractAllIndexDescriptors(source);
        var targetIndexes = ExtractAllIndexDescriptors(target);
        var droppedTables = operations
                           .OfType<DropTableOperation>()
                           .Select(o => (o.Name, o.Schema))
                           .ToHashSet();

        foreach (var src in sourceIndexes)
        {
            if (droppedTables.Contains((src.TableName, src.Schema)))
                continue;

            if (!targetIndexes.Contains(src))
            {
                operations.Add(new DropIndexOperation
                               {
                                   Name   = src.IndexName,
                                   Table  = src.TableName,
                                   Schema = src.Schema
                               });
            }
        }

        foreach (var tgt in targetIndexes)
        {
            if (!sourceIndexes.Contains(tgt))
            {
                operations.Add(new CreateIndexOperation
                               {
                                   Name     = tgt.IndexName,
                                   Table    = tgt.TableName,
                                   Schema   = tgt.Schema,
                                   Columns  = [.. tgt.ColumnNames],
                                   IsUnique = tgt.IsUnique,
                                   Filter   = tgt.Filter
                               });
            }
        }

        return operations;
    }

    private static HashSet<IndexDescriptor> ExtractAllIndexDescriptors(IRelationalModel? relationalModel)
    {
        var result = new HashSet<IndexDescriptor>();
        if (relationalModel is null) return result;

        foreach (var entityType in relationalModel.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            var schema    = entityType.GetSchema();
            if (tableName is null) continue;

            ScanForSingleColumnIndexes(entityType, tableName, schema, result);
            ScanForCompositeIndexes(entityType, tableName, schema, result);
        }

        return result;
    }

    private static void ScanForSingleColumnIndexes(
        ITypeBase                typeBase,
        string                   tableName,
        string?                  schema,
        HashSet<IndexDescriptor> results
    )
    {
        foreach (var property in typeBase.GetDeclaredProperties())
        {
            if (property.FindAnnotation(ComplexIndexAnnotations.IsIndexed)?.Value is not true)
                continue;

            var columnName = property.GetColumnName();
            var isUnique   = property.FindAnnotation(ComplexIndexAnnotations.IsUnique)?.Value is true;
            var filter     = property.FindAnnotation(ComplexIndexAnnotations.Filter)?.Value as string;
            var indexName = property.FindAnnotation(ComplexIndexAnnotations.IndexName)?.Value as string
                         ?? $"IX_{tableName}_{columnName}";

            results.Add(new IndexDescriptor(tableName, schema, [columnName], indexName, isUnique, filter));
        }

        foreach (var cp in typeBase.GetDeclaredComplexProperties())
            ScanForSingleColumnIndexes(cp.ComplexType, tableName, schema, results);
    }

    private static void ScanForCompositeIndexes(
        IEntityType              entityType,
        string                   tableName,
        string?                  schema,
        HashSet<IndexDescriptor> results
    )
    {
        var annotation = entityType.FindAnnotation(ComplexIndexAnnotations.CompositeIndexes);

        if (annotation?.Value is not string json || string.IsNullOrEmpty(json))
            return;

        var definitions = CompositeIndexSerializer.Deserialize(json);

        foreach (var def in definitions)
        {
            var columnNames = new List<string>(def.PropertyPaths.Count);
            var allResolved = true;

            foreach (var path in def.PropertyPaths)
            {
                var col = ResolveColumnName(entityType, path);
                if (col is null)
                {
                    allResolved = false;
                    break;
                }

                columnNames.Add(col);
            }

            if (!allResolved)
            {
                throw new InvalidOperationException(
                    $"Could not resolve property path for composite index on entity {entityType.Name}. " +
                    $"Invalid path: {string.Join(".", def.PropertyPaths)}"
                );
            }

            var indexName = def.IndexName ?? $"IX_{tableName}_{string.Join("_", columnNames)}";

            results.Add(new IndexDescriptor(tableName, schema, columnNames, indexName, def.IsUnique, def.Filter));
        }
    }

    private static string? ResolveColumnName(IEntityType entityType, string dotPath)
    {
        var       parts   = dotPath.Split('.');
        ITypeBase current = entityType;

        for (var i = 0; i < parts.Length; i++)
        {
            if (i == parts.Length - 1)
                return current.FindProperty(parts[i])?.GetColumnName();

            var cp = current.FindComplexProperty(parts[i]);
            if (cp is null) return null;
            current = cp.ComplexType;
        }

        return null;
    }

    internal sealed record IndexDescriptor(
        string                TableName,
        string?               Schema,
        IReadOnlyList<string> ColumnNames,
        string                IndexName,
        bool                  IsUnique,
        string?               Filter)
    {
        public bool Equals(IndexDescriptor? other)
        {
            if (other is null) return false;
            return TableName == other.TableName
                && Schema    == other.Schema
                && ColumnNames.SequenceEqual(other.ColumnNames)
                && IndexName == other.IndexName
                && IsUnique  == other.IsUnique
                && Filter    == other.Filter;
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(TableName);
            hash.Add(Schema);
            foreach (var col in ColumnNames) hash.Add(col);
            hash.Add(IndexName);
            hash.Add(IsUnique);
            hash.Add(Filter);
            return hash.ToHashCode();
        }
    }
}

#pragma warning restore EF1001