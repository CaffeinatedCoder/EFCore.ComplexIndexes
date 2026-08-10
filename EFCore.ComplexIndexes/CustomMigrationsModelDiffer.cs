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
        var operations = base.GetDifferences(source, target);

        var sourceIndexes = ExtractAllIndexDescriptors(source);
        var targetIndexes = ExtractAllIndexDescriptors(target);

        if (sourceIndexes.Count == 0 && targetIndexes.Count == 0)
            return operations;

        var droppedTables = operations
                           .OfType<DropTableOperation>()
                           .Select(o => (o.Name, o.Schema))
                           .ToHashSet();

        // Placed before the base operations: an index that moves between a native HasIndex and a
        // complex-index declaration surfaces as a base-emitted CreateIndex plus our DropIndex of the
        // same name, and a removed complex property surfaces as a base DropColumn that would take
        // the index down with it — in both cases the drop must run first.
        var drops = new List<MigrationOperation>();
        foreach (var src in sourceIndexes)
        {
            if (droppedTables.Contains((src.TableName, src.Schema)))
                continue;

            if (!targetIndexes.Contains(src))
            {
                drops.Add(new DropIndexOperation
                          {
                              Name   = src.IndexName,
                              Table  = src.TableName,
                              Schema = src.Schema
                          });
            }
        }

        // Placed after the base operations, so newly added columns exist before their indexes.
        var creates = new List<MigrationOperation>();
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

            // Forward the whitelisted provider annotations — provider SQL generators handle their own
            foreach (var (key, value) in tgt.ProviderAnnotations)
                op.AddAnnotation(key, value);

            // Ordered parts are needed when the stock generator can't render the index: expression
            // parts have no slot in Columns, and NULLS FIRST/LAST has no slot on the native
            // operation. Plain column indexes render from Columns and stay annotation-free.
            if (tgt.RequiresPartsAnnotation)
                op.AddAnnotation(ComplexIndexAnnotations.IndexParts, IndexPartsSerializer.Serialize(tgt.Parts));

            creates.Add(op);
        }

        if (drops.Count == 0 && creates.Count == 0)
            return operations;

        return [.. drops, .. operations, .. creates];
    }

    /// <summary>
    /// Decides whether a property-level annotation is carried onto the emitted
    /// <see cref="CreateIndexOperation"/> as a provider index option. The core package forwards
    /// nothing; provider satellites override this to whitelist exactly the index-option keys their
    /// SQL generator renders (e.g. <c>Npgsql:IndexMethod</c>). A whitelist keeps EF column facets
    /// (<c>Relational:ColumnName</c>, <c>Relational:ColumnType</c>, …) from leaking into index
    /// operations, where snapshot/code-model asymmetries caused phantom drop/create churn.
    /// </summary>
    protected virtual bool IsForwardedIndexAnnotation(string annotationName) => false;

    /// <summary>
    /// Called for an index part whose property path does not resolve to a table column — typically
    /// a member of a complex property mapped to JSON via <c>ToJson()</c>. Provider satellites can
    /// return an expression part (e.g. a PostgreSQL <c>-&gt;&gt;</c> extraction); the core returns
    /// null, which surfaces as a resolution error.
    /// </summary>
    protected virtual ResolvedIndexPart? ResolveUnmappedPart(
        IEntityType           entityType,
        IndexPartDefinition   part,
        StoreObjectIdentifier storeObject
    ) => null;

    /// <summary>
    /// Resolves a template part (<c>{Property.Path}</c> placeholders from a typed-expression
    /// translator) into a final SQL expression. Identifier quoting is provider-specific, so the
    /// core has no implementation — provider satellites override this.
    /// </summary>
    protected virtual ResolvedIndexPart ResolveTemplatePart(
        IEntityType           entityType,
        IndexPartDefinition   part,
        StoreObjectIdentifier storeObject
    ) => throw new InvalidOperationException(
             $"The index template '{part.Template}' on entity '{entityType.Name}' requires a provider " +
             "satellite differ (e.g. EFCore.ComplexIndexes.PostgreSQL) to resolve column references.");

    private HashSet<IndexDescriptor> ExtractAllIndexDescriptors(IRelationalModel? relationalModel)
    {
        var result = new HashSet<IndexDescriptor>();
        if (relationalModel is null) return result;

        foreach (var entityType in relationalModel.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            var schema    = entityType.GetSchema();
            if (tableName is null) continue;

            var storeObject = StoreObjectIdentifier.Table(tableName, schema);

            ScanForSingleColumnIndexes(entityType, entityType, pathPrefix: "", tableName, schema, storeObject, result);
            ScanForCompositeIndexes(entityType, tableName, schema, result);
        }

        return result;
    }

    private void ScanForSingleColumnIndexes(
        IEntityType              rootEntityType,
        ITypeBase                typeBase,
        string                   pathPrefix,
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

            // No table column — a JSON-mapped complex member, for example. Give the provider
            // satellite a chance to resolve it to an expression part before giving up.
            var part = columnName is not null
                           ? new ResolvedIndexPart(false, columnName)
                           : ResolveUnmappedPart(
                                 rootEntityType,
                                 new IndexPartDefinition { PropertyPath = pathPrefix + property.Name },
                                 storeObject)
                          ?? throw new InvalidOperationException(
                                 $"The property '{property.Name}' on '{typeBase.Name}' is marked with " +
                                 $"HasComplexIndex but has no column mapping for table '{tableName}'. " +
                                 "A property mapped to JSON (or not mapped to this table) cannot carry " +
                                 "a complex index here; use an expression index over the JSON column instead.");

            var isUnique = property.FindAnnotation(ComplexIndexAnnotations.IsUnique)?.Value is true;
            var filter   = property.FindAnnotation(ComplexIndexAnnotations.Filter)?.Value as string;
            var indexName = property.FindAnnotation(ComplexIndexAnnotations.IndexName)?.Value as string
                         ?? $"IX_{tableName}_{BuildPartToken(part)}";

            // Collect only whitelisted provider index options; everything else on the property is a
            // column facet that does not belong on an index operation.
            var providerAnnotations = new Dictionary<string, object?>();
            foreach (var ann in property.GetAnnotations())
            {
                if (IsForwardedIndexAnnotation(ann.Name))
                    providerAnnotations[ann.Name] = ann.Value;
            }

            results.Add(new IndexDescriptor(tableName, schema, [part], indexName, isUnique, filter, providerAnnotations));
        }

        foreach (var cp in typeBase.GetDeclaredComplexProperties())
            ScanForSingleColumnIndexes(rootEntityType, cp.ComplexType, $"{pathPrefix}{cp.Name}.", tableName, schema, storeObject, results);
    }

    private void ScanForCompositeIndexes(
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
                    parts.Add(new ResolvedIndexPart(true, part.Expression!, part.Descending, part.NullSort));
                    continue;
                }

                if (part.IsTemplate)
                {
                    parts.Add(ResolveTemplatePart(entityType, part, storeObject));
                    continue;
                }

                var col = ResolveColumnName(entityType, part.PropertyPath!, storeObject);
                if (col is not null)
                {
                    parts.Add(new ResolvedIndexPart(false, col, part.Descending, part.NullSort));
                    continue;
                }

                // No table column — give the provider satellite a chance (JSON members, …).
                var unmapped = ResolveUnmappedPart(entityType, part, storeObject)
                            ?? throw new InvalidOperationException(
                                   $"Could not resolve property path '{part.PropertyPath}' for index on entity {entityType.Name}."
                               );

                parts.Add(unmapped);
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
                   JsonValueKind.Number => NormalizeNumber(je),
                   JsonValueKind.Null   => null,
                   JsonValueKind.Array => je.EnumerateArray()
                                            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
                                            .ToArray(),
                   _ => je.ToString()
               };
    }

    // int first: provider generators read their numeric index options with `as int?` (e.g. SQL
    // Server's FILLFACTOR), which returns null for a boxed long or double — the option would be
    // silently dropped. (A ternary here would also coerce every integral to double.)
    private static object NormalizeNumber(JsonElement je)
    {
        if (je.TryGetInt32(out var i)) return i;
        if (je.TryGetInt64(out var l)) return l;
        return je.GetDouble();
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

        // True when a provider's custom generator must render the index from the parts annotation.
        public bool RequiresPartsAnnotation => Parts.Any(p => p.IsExpression || p.NullSort != DbNullSort.Default);

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