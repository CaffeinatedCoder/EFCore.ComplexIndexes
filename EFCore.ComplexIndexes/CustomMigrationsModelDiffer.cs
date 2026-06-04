using System.Text.Json;
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
            if (sourceIndexes.Contains(tgt)) continue;

            var op = new CreateIndexOperation
                     {
                         Name     = tgt.IndexName,
                         Table    = tgt.TableName,
                         Schema   = tgt.Schema,
                         // EF's MigrationBuilder.CreateIndex rejects an empty column list, so for
                         // expression indexes we fill Columns with the verbatim part values (the
                         // provider SQL generator renders from the IndexParts annotation instead).
                         Columns  = [.. tgt.Parts.Select(p => p.Value)],
                         IsUnique = tgt.IsUnique,
                         Filter   = tgt.Filter
                     };

            // null means all-ascending — leave it so existing ascending indexes don't churn.
            if (tgt.Parts.Any(p => p.Descending))
                op.IsDescending = [.. tgt.Parts.Select(p => p.Descending)];

            // Forward all extra annotations — provider SQL generators handle their own
            foreach (var (key, value) in tgt.ProviderAnnotations)
                op.AddAnnotation(key, value);

            // Only expression indexes need the ordered parts; column-only indexes render from Columns.
            if (tgt.HasExpression)
                op.AddAnnotation(ComplexIndexAnnotations.IndexParts, IndexPartsSerializer.Serialize(tgt.Parts));

            operations.Add(op);
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

    private static readonly HashSet<string> CoreAnnotationKeys =
    [
        ComplexIndexAnnotations.IsIndexed,
        ComplexIndexAnnotations.IsUnique,
        ComplexIndexAnnotations.Filter,
        ComplexIndexAnnotations.IndexName,
        ComplexIndexAnnotations.CompositeIndexes
    ];

    // EF relational column-facet annotations that are meaningless on an index operation. The
    // snapshot model serializes the column type (HasColumnType) so it carries Relational:ColumnType,
    // while the code model leaves it implicit (no annotation). Collecting it would make the source
    // and target descriptors differ for every complex index, producing phantom drop/create churn.
    private static readonly HashSet<string> NonIndexColumnAnnotationKeys =
    [
        "Relational:ColumnType"
    ];

    private static void ScanForSingleColumnIndexes(
        ITypeBase                typeBase,
        string                   tableName,
        string?                  schema,
        HashSet<IndexDescriptor> results
    )
    {
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);

        ScanForSingleColumnIndexes(typeBase, tableName, schema, storeObject, results);
    }

    private static void ScanForSingleColumnIndexes(
        ITypeBase                typeBase,
        string                   tableName,
        string?                  schema,
        StoreObjectIdentifier    storeObject,
        HashSet<IndexDescriptor> results
    )
    {
        foreach (var property in typeBase.GetDeclaredProperties())
        {
            if (property.FindAnnotation(ComplexIndexAnnotations.IsIndexed)?.Value is not true)
                continue;

            var columnName = property.GetColumnName(storeObject);
            if (columnName is null)
                continue;

            var isUnique = property.FindAnnotation(ComplexIndexAnnotations.IsUnique)?.Value is true;
            var filter   = property.FindAnnotation(ComplexIndexAnnotations.Filter)?.Value as string;
            var indexName = property.FindAnnotation(ComplexIndexAnnotations.IndexName)?.Value as string
                         ?? $"IX_{tableName}_{columnName}";

            // Collect provider annotations, skipping core keys and non-index column facets
            var providerAnnotations = new Dictionary<string, object?>();
            foreach (var ann in property.GetAnnotations())
            {
                if (!CoreAnnotationKeys.Contains(ann.Name) && !NonIndexColumnAnnotationKeys.Contains(ann.Name))
                    providerAnnotations[ann.Name] = ann.Value;
            }

            results.Add(new IndexDescriptor(tableName, schema, [new ResolvedIndexPart(false, columnName)], indexName, isUnique, filter, providerAnnotations));
        }

        foreach (var cp in typeBase.GetDeclaredComplexProperties())
            ScanForSingleColumnIndexes(cp.ComplexType, tableName, schema, storeObject, results);
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
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);

        foreach (var def in definitions)
        {
            var parts = new List<ResolvedIndexPart>(def.EffectiveParts.Count);

            foreach (var part in def.EffectiveParts)
            {
                if (part.IsExpression)
                {
                    parts.Add(new ResolvedIndexPart(true, part.Expression!, part.Descending));
                    continue;
                }

                var col = ResolveColumnName(entityType, part.PropertyPath!, storeObject);
                if (col is null)
                {
                    throw new InvalidOperationException(
                        $"Could not resolve property path '{part.PropertyPath}' for index on entity {entityType.Name}."
                    );
                }

                parts.Add(new ResolvedIndexPart(false, col, part.Descending));
            }

            var indexName = def.IndexName ?? $"IX_{tableName}_{string.Join("_", parts.Select(BuildPartToken))}";

            var normalized = NormalizeProviderAnnotations(def.ProviderAnnotations);

            results.Add(
                new IndexDescriptor(
                    tableName,
                    schema,
                    parts,
                    indexName,
                    def.IsUnique,
                    def.Filter,
                    normalized
                )
            );
        }
    }

    private static Dictionary<string, object?> NormalizeProviderAnnotations(Dictionary<string, object?>? annotations)
    {
        if (annotations is null) return [];

        var result = new Dictionary<string, object?>(annotations.Count);

        foreach (var (key, value) in annotations)
        {
            result[key] = value is JsonElement je
                              ? NormalizeJsonElement(je)
                              : value;
        }

        return result;
    }

    private static object? NormalizeJsonElement(JsonElement je)
    {
        return je.ValueKind switch
               {
                   JsonValueKind.String => je.GetString(),
                   JsonValueKind.True   => true,
                   JsonValueKind.False  => false,
                   JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
                   JsonValueKind.Null   => null,
                   JsonValueKind.Array => je.EnumerateArray()
                                            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
                                            .ToArray(),
                   _ => je.ToString()
               };
    }

    // Builds a default index-name token for a part: column names pass through; expressions are
    // reduced to their alphanumeric characters (e.g. lower("Email") -> "lowerEmail").
    private static string BuildPartToken(ResolvedIndexPart part)
    {
        if (!part.IsExpression)
            return part.Value;

        var token = new string([.. part.Value.Where(char.IsLetterOrDigit)]);
        return token.Length > 0 ? token : "expr";
    }

    private static string? ResolveColumnName(
        IEntityType           entityType,
        string                dotPath,
        StoreObjectIdentifier storeObject
    )
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

    internal sealed record IndexDescriptor(
        string                        TableName,
        string?                       Schema,
        IReadOnlyList<ResolvedIndexPart> Parts,
        string                        IndexName,
        bool                          IsUnique,
        string?                       Filter,
        Dictionary<string, object?>   ProviderAnnotations)
    {
        public IEnumerable<string> ColumnNames => Parts.Where(p => !p.IsExpression).Select(p => p.Value);

        public bool HasExpression => Parts.Any(p => p.IsExpression);

        public bool Equals(IndexDescriptor? other)
        {
            if (other is null) return false;
            return TableName == other.TableName
                && Schema    == other.Schema
                && Parts.SequenceEqual(other.Parts)
                && IndexName == other.IndexName
                && IsUnique  == other.IsUnique
                && Filter    == other.Filter
                && ProviderAnnotationsEqual(other);
        }

        private bool ProviderAnnotationsEqual(IndexDescriptor other)
        {
            if (ProviderAnnotations.Count != other.ProviderAnnotations.Count) return false;
            foreach (var (key, value) in ProviderAnnotations)
            {
                if (!other.ProviderAnnotations.TryGetValue(key, out var otherValue)) return false;
                if (!AnnotationValueEquals(value, otherValue)) return false;
            }

            return true;
        }

        // Annotation values may be arrays (e.g. operator classes / included columns). object.Equals
        // compares arrays by reference, so structurally-equal values from two model builds never
        // match — compare such values by sequence instead.
        private static bool AnnotationValueEquals(object? a, object? b)
        {
            if (a is string || b is string) return Equals(a, b);
            if (a is System.Collections.IEnumerable ea && b is System.Collections.IEnumerable eb)
                return ea.Cast<object?>().SequenceEqual(eb.Cast<object?>());
            return Equals(a, b);
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(TableName);
            hash.Add(Schema);

            foreach (var part in Parts)
                hash.Add(part);

            hash.Add(IndexName);
            hash.Add(IsUnique);
            hash.Add(Filter);

            foreach (var (key, value) in ProviderAnnotations.OrderBy(kv => kv.Key))
            {
                hash.Add(key);

                // Hash array contents (not the reference) to stay consistent with AnnotationValueEquals.
                if (value is not string && value is System.Collections.IEnumerable seq)
                    foreach (var item in seq) hash.Add(item);
                else
                    hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}

#pragma warning restore EF1001