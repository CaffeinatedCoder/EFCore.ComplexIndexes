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
    /// <summary>
    /// Appended as a fake trailing column when an index requires the package's custom SQL generator
    /// (expression parts or NULLS ordering). The custom generator renders from the parts annotation
    /// and ignores <c>Columns</c>; if the stock generator gets the operation instead — i.e. the
    /// runtime wiring is missing — <c>CREATE INDEX</c> fails loudly with this name in the error
    /// message rather than applying a silently wrong index.
    /// </summary>
    public const string RuntimeWiringSentinel = "__requires_UseNpgsqlComplexIndexes__";

    public override IReadOnlyList<MigrationOperation> GetDifferences(
        IRelationalModel? source,
        IRelationalModel? target
    )
    {
        var operations = base.GetDifferences(source, target);

        var sourceIndexes = ExtractAllIndexDescriptors(source);
        var targetIndexes = ExtractAllIndexDescriptors(target);

        // Target only: the source is history. A snapshot that already contains a collision must
        // still be diffable, or the model could never be fixed.
        ValidateUniqueIndexNames(targetIndexes);

        if (sourceIndexes.Count == 0 && targetIndexes.Count == 0)
            return operations;

        var droppedTables = operations
                           .OfType<DropTableOperation>()
                           .Select(o => (o.Name, o.Schema))
                           .ToHashSet();

        // Tables the base operations rename: compare source indexes under their *new* table
        // identity so a renamed table doesn't drop and recreate every index it carries. Drops still
        // execute against the old name — they run before the rename.
        var renamedTables = new Dictionary<(string Name, string? Schema), (string Name, string? Schema)>();
        foreach (var rename in operations.OfType<RenameTableOperation>())
            renamedTables[(rename.Name, rename.Schema)] = (rename.NewName ?? rename.Name, rename.NewSchema ?? rename.Schema);

        // Normalized descriptor → the original (table, schema) a drop must target.
        var normalizedSource = new Dictionary<IndexDescriptor, (string Table, string? Schema)>();
        foreach (var src in sourceIndexes)
        {
            var normalized = renamedTables.TryGetValue((src.TableName, src.Schema), out var to)
                                 ? src with { TableName = to.Name, Schema = to.Schema }
                                 : src;
            normalizedSource.TryAdd(normalized, (src.TableName, src.Schema));
        }

        var pendingDrops = normalizedSource.Keys
                                           .Where(src => !targetIndexes.Contains(src)
                                                      && !droppedTables.Contains(normalizedSource[src]))
                                           .ToList();

        var pendingCreates = targetIndexes.Where(tgt => !normalizedSource.ContainsKey(tgt)).ToList();

        // A drop/create pair that differs only by name is a rename — cheap DDL instead of an index
        // rebuild — on providers whose generator can rename these indexes standalone.
        var renames = new List<MigrationOperation>();
        if (CanRenameIndexes)
        {
            for (var i = pendingDrops.Count - 1; i >= 0; i--)
            {
                var dropped = pendingDrops[i];
                var renamed = pendingCreates.FirstOrDefault(c => (dropped with { IndexName = c.IndexName }).Equals(c));
                if (renamed is null)
                    continue;

                renames.Add(new RenameIndexOperation
                            {
                                Name    = dropped.IndexName,
                                NewName = renamed.IndexName,
                                Table   = renamed.TableName,
                                Schema  = renamed.Schema
                            });
                pendingDrops.RemoveAt(i);
                pendingCreates.Remove(renamed);
            }
        }

        // Placed before the base operations: an index that moves between a native HasIndex and a
        // complex-index declaration surfaces as a base-emitted CreateIndex plus our DropIndex of the
        // same name, and a removed complex property surfaces as a base DropColumn that would take
        // the index down with it — in both cases the drop must run first.
        var drops = new List<MigrationOperation>();
        foreach (var src in pendingDrops)
        {
            var (table, schema) = normalizedSource[src];
            drops.Add(new DropIndexOperation
                      {
                          Name   = src.IndexName,
                          Table  = table,
                          Schema = schema
                      });
        }

        // Placed after the base operations, so newly added columns exist before their indexes.
        var creates = new List<CreateIndexOperation>();
        foreach (var tgt in pendingCreates)
        {
            var op = new CreateIndexOperation
                     {
                         Name     = tgt.IndexName,
                         Table    = tgt.TableName,
                         Schema   = tgt.Schema,
                         // EF's MigrationBuilder.CreateIndex rejects an empty column list, so for
                         // expression indexes we fill Columns with the verbatim part values (the
                         // provider SQL generator renders from the IndexParts annotation instead).
                         // The sentinel makes a missing runtime wiring fail loudly at apply time.
                         Columns = tgt.RequiresPartsAnnotation
                                       ? [.. tgt.Parts.Select(p => p.Value), RuntimeWiringSentinel]
                                       : [.. tgt.Parts.Select(p => p.Value)],
                         IsUnique = tgt.IsUnique,
                         Filter   = tgt.Filter
                     };

            // null means all-ascending — leave it so existing ascending indexes don't churn.
            if (tgt.Parts.Any(p => p.Descending))
                op.IsDescending = tgt.RequiresPartsAnnotation
                                      ? [.. tgt.Parts.Select(p => p.Descending), false]
                                      : [.. tgt.Parts.Select(p => p.Descending)];

            // Forward the whitelisted provider annotations — provider SQL generators handle their own
            foreach (var (key, value) in tgt.ProviderAnnotations)
                op.AddAnnotation(key, value);

            // Ordered parts are needed when the stock generator can't render the index: expression
            // parts have no slot in Columns, and NULLS FIRST/LAST has no slot on the native
            // operation. Plain column indexes render from Columns and stay annotation-free.
            if (tgt.RequiresPartsAnnotation)
                op.AddAnnotation(ComplexIndexAnnotations.IndexParts, IndexPartsSerializer.Serialize(tgt.Parts));

            ValidateCreateIndexOperation(op);

            creates.Add(op);
        }

        ValidateCreatedIndexes(creates);

        if (drops.Count == 0 && renames.Count == 0 && creates.Count == 0)
            return operations;

        return [.. drops, .. operations, .. renames, .. creates];
    }

    /// <summary>
    /// Called for each <see cref="CreateIndexOperation"/> this differ emits, before it joins the
    /// operation list. Provider satellites override this to reject declarations their provider
    /// cannot express.
    /// </summary>
    /// <remarks>
    /// Only operations built from complex-index declarations reach this method. Satellites must not
    /// instead sweep the finished operation list: it also contains the operations the base EF differ
    /// emitted for native <c>HasIndex</c> declarations, which are none of this package's business —
    /// validating those turns any provider index option the satellite does not happen to know about
    /// into a hard failure of the consumer's whole <c>migrations add</c>, for a model that never
    /// touched this package.
    /// </remarks>
    protected virtual void ValidateCreateIndexOperation(CreateIndexOperation operation) { }

    /// <summary>
    /// Called once with every <see cref="CreateIndexOperation"/> this differ emitted, after they are
    /// all built. Satellites override this to reject combinations that are only visible across
    /// several indexes — for instance, a provider allowing at most one clustered index per table.
    /// </summary>
    /// <remarks>
    /// The list holds only this package's operations, for the same reason as
    /// <see cref="ValidateCreateIndexOperation"/>: the base EF differ's operations for native
    /// <c>HasIndex</c> declarations are not this package's to police.
    /// </remarks>
    protected virtual void ValidateCreatedIndexes(IReadOnlyList<CreateIndexOperation> operations) { }

    /// <summary>
    /// Whether name-only index changes are emitted as <see cref="RenameIndexOperation"/> instead of
    /// drop + create. Off in the core: not every provider can rename these indexes standalone
    /// (SQLite's generator recreates a renamed index from the relational model, where
    /// annotation-declared indexes don't exist). The PostgreSQL and SQL Server satellites enable it.
    /// </summary>
    protected virtual bool CanRenameIndexes => false;

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
    /// Transforms a forwarded provider-annotation value before it is compared and stamped onto the
    /// index operation. Satellites use this to resolve property paths inside their option values —
    /// e.g. INCLUDE lists — to column names. The default returns the value unchanged.
    /// </summary>
    protected virtual object? TransformIndexAnnotation(
        IEntityType           entityType,
        string                annotationName,
        object?               value,
        StoreObjectIdentifier storeObject
    ) => value;

    /// <summary>
    /// Resolves each entry of an INCLUDE-style column list: an entry that matches a property path
    /// (including complex members) becomes its mapped column name; anything else passes through
    /// verbatim as a column name, so pre-v5 declarations keep working.
    /// </summary>
    protected static object? ResolveIncludeList(IEntityType entityType, object? value, StoreObjectIdentifier storeObject)
    {
        if (value is string || value is not System.Collections.IEnumerable enumerable)
            return value;

        return enumerable.Cast<object?>()
                         .Select(entry => entry?.ToString() ?? "")
                         .Select(entry => ResolveColumnName(entityType, entry, storeObject) ?? entry)
                         .ToArray();
    }

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

    /// <summary>
    /// Fails when two distinct declarations resolve to the same index name on the same table.
    /// </summary>
    /// <remarks>
    /// Such a pair emits two <c>CREATE INDEX</c> statements under one name — a migration that
    /// scaffolds happily and then fails at apply time (PostgreSQL 42P07). This is the first point
    /// where the collision is visible: the two stores (per-property annotations and the entity-level
    /// definition list) cannot see each other, and default names are only known once property paths
    /// have been resolved to real columns. Identical declarations are already collapsed by the
    /// descriptor set, so only genuinely different indexes reach this check.
    /// </remarks>
    private static void ValidateUniqueIndexNames(HashSet<IndexDescriptor> descriptors)
    {
        var collision = descriptors
                       .GroupBy(d => (d.TableName, d.Schema, d.IndexName))
                       .FirstOrDefault(g => g.Count() > 1);

        if (collision is null)
            return;

        throw new InvalidOperationException(
            $"Two complex indexes on table '{collision.Key.TableName}' both resolve to the name "
          + $"'{collision.Key.IndexName}': {string.Join(" and ", collision.Select(Describe))}. "
          + "Index names must be unique per table — give each declaration an explicit, distinct name.");

        static string Describe(IndexDescriptor descriptor)
        {
            var parts = string.Join(", ", descriptor.Parts.Select(p => p.Value));
            var facets = descriptor.Filter is null ? "" : $" WHERE {descriptor.Filter}";
            return $"({parts}){(descriptor.IsUnique ? " UNIQUE" : "")}{facets}";
        }
    }

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
                    providerAnnotations[ann.Name] = TransformIndexAnnotation(rootEntityType, ann.Name, ann.Value, storeObject);
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
            foreach (var key in normalized.Keys.ToList())
                normalized[key] = TransformIndexAnnotation(entityType, key, normalized[key], storeObject);

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

    /// <summary>Resolves a dotted property path (complex members included) to its column name, or null.</summary>
    protected static string? ResolveColumnName(
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
                if (!AnnotationValues.ValuesEqual(value, otherValue)) return false;
            }

            return true;
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
                AnnotationValues.AddValue(ref hash, value);
            }

            return hash.ToHashCode();
        }
    }
}

#pragma warning restore EF1001