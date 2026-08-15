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
/// constraints (<c>WITHOUT OVERLAPS</c>) declared via <c>HasTemporalConstraint</c>. Temporal and
/// exclusion constraint DDL is rendered at design time, so neither needs runtime SQL-generator wiring.
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

    /// <summary>Forwards exactly the Npgsql index-option annotations Npgsql's SQL generator renders.</summary>
    protected override bool IsForwardedIndexAnnotation(string annotationName)
        => SupportedNpgsqlAnnotations.Contains(annotationName);

    /// <summary>PostgreSQL renames indexes standalone (<c>ALTER INDEX … RENAME TO</c>).</summary>
    protected override bool CanRenameIndexes => true;

    /// <summary>
    /// Rejects <c>Npgsql:*</c> index options this package does not render — typically an entity-level
    /// declaration carrying an option the satellite has no support for, since entity-level provider
    /// annotations reach the operation unfiltered (the property-level path is already whitelisted by
    /// <see cref="IsForwardedIndexAnnotation"/>).
    /// </summary>
    protected override void ValidateCreateIndexOperation(CreateIndexOperation operation)
    {
        foreach (var annotation in operation.GetAnnotations())
        {
            if (annotation.Name.StartsWith("Npgsql:", StringComparison.Ordinal)
             && !SupportedNpgsqlAnnotations.Contains(annotation.Name))
            {
                throw new InvalidOperationException(
                    $"Unrecognized Npgsql index annotation '{annotation.Name}' on complex index '{operation.Name}'. " +
                    $"Supported annotations: {string.Join(", ", SupportedNpgsqlAnnotations)}."
                );
            }
        }

        // Npgsql's own sort-order annotations and this package's per-part sort options are two ways
        // to say the same thing, and an index routed through the parts annotation is rendered by
        // this package's generator, which reads only the parts. Rather than silently dropping the
        // annotation's half, refuse the ambiguity.
        if (operation[ComplexIndexAnnotations.IndexParts] is null)
            return;

        foreach (var key in (string[])[NpgsqlAnnotations.IndexSortOrder, NpgsqlAnnotations.IndexNullSortOrder])
        {
            if (operation[key] is not null)
                throw new InvalidOperationException(
                    $"Complex index '{operation.Name}' carries '{key}' alongside per-part sort options. " +
                    "Declare direction and null ordering with DbOrder.Asc/Desc/NullsFirst/NullsLast (or the " +
                    $"ExpressionIndexBuilder equivalents) instead — '{key}' is not rendered for this index."
                );
        }
    }

    /// <summary>Resolves property paths inside INCLUDE lists to column names (verbatim fallback).</summary>
    protected override object? TransformIndexAnnotation(
        IEntityType           entityType,
        string                annotationName,
        object?               value,
        StoreObjectIdentifier storeObject
    ) => annotationName == NpgsqlAnnotations.IndexInclude
             ? ResolveIncludeList(entityType, value, storeObject)
             : base.TransformIndexAnnotation(entityType, annotationName, value, storeObject);

    /// <summary>
    /// Resolves an index part whose path traverses a complex property mapped to JSON
    /// (<c>ToJson()</c>) into a PostgreSQL extraction expression, e.g.
    /// <c>"name" -&gt; 'Inner' -&gt;&gt; 'Leaf'</c>. Members are extracted as text
    /// (<c>-&gt;&gt;</c>) and honor <c>HasJsonPropertyName</c>; for typed semantics use
    /// <c>HasExpressionIndex</c> with an explicit cast. Like all expression parts, rendering
    /// requires the <c>UseNpgsqlComplexIndexes()</c> runtime wiring.
    /// </summary>
    protected override ResolvedIndexPart? ResolveUnmappedPart(
        IEntityType           entityType,
        IndexPartDefinition   part,
        StoreObjectIdentifier storeObject
    )
    {
        if (part.PropertyPath is null)
            return null;

        var       segments        = part.PropertyPath.Split('.');
        ITypeBase current         = entityType;
        string?   containerColumn = null;
        var       jsonPath        = new List<string>();

        for (var i = 0; i < segments.Length - 1; i++)
        {
            var complexProperty = current.FindComplexProperty(segments[i]);
            if (complexProperty is null)
                return null;

            // The first JSON-mapped complex property on the path starts the document; complex
            // properties nested inside it become JSON path segments.
            if (containerColumn is null)
                containerColumn = complexProperty.ComplexType.GetContainerColumnName();
            else
                jsonPath.Add(complexProperty.GetJsonPropertyName() ?? complexProperty.Name);

            current = complexProperty.ComplexType;
        }

        if (containerColumn is null)
            return null;

        var leaf = current.FindProperty(segments[^1]);
        if (leaf is null)
            return null;

        jsonPath.Add(leaf.GetJsonPropertyName() ?? leaf.Name);

        var sql = new System.Text.StringBuilder(Quote(containerColumn));
        for (var i = 0; i < jsonPath.Count; i++)
        {
            sql.Append(i == jsonPath.Count - 1 ? " ->> '" : " -> '")
               .Append(jsonPath[i].Replace("'", "''"))
               .Append('\'');
        }

        return new ResolvedIndexPart(true, sql.ToString(), part.Descending, part.NullSort);
    }

    /// <summary>
    /// Resolves a typed-expression template into final SQL: <c>{Property.Path}</c> placeholders
    /// become quoted column references — or parenthesized JSON extractions for <c>ToJson()</c>
    /// members; <c>{{</c>/<c>}}</c> unescape to literal braces.
    /// </summary>
    protected override ResolvedIndexPart ResolveTemplatePart(
        IEntityType           entityType,
        IndexPartDefinition   part,
        StoreObjectIdentifier storeObject
    )
    {
        var template = part.Template!;
        var sql      = new System.Text.StringBuilder(template.Length);

        for (var i = 0; i < template.Length; i++)
        {
            var ch = template[i];

            if (ch == '{')
            {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    sql.Append('{');
                    i++;
                    continue;
                }

                var end = template.IndexOf('}', i + 1);
                if (end < 0)
                    throw new InvalidOperationException($"Malformed index expression template '{template}' on entity '{entityType.Name}'.");

                sql.Append(ResolvePlaceholder(entityType, template[(i + 1)..end], storeObject));
                i = end;
                continue;
            }

            if (ch == '}')
            {
                if (i + 1 < template.Length && template[i + 1] == '}')
                {
                    sql.Append('}');
                    i++;
                    continue;
                }

                throw new InvalidOperationException($"Malformed index expression template '{template}' on entity '{entityType.Name}'.");
            }

            sql.Append(ch);
        }

        return new ResolvedIndexPart(true, sql.ToString(), part.Descending, part.NullSort);
    }

    private string ResolvePlaceholder(IEntityType entityType, string path, StoreObjectIdentifier storeObject)
    {
        var column = ResolveProperty(entityType, path)?.GetColumnName(storeObject);
        if (column is not null)
            return Quote(column);

        var jsonPart = ResolveUnmappedPart(entityType, new IndexPartDefinition { PropertyPath = path }, storeObject);
        if (jsonPart is not null)
            return $"({jsonPart.Value})";

        throw new InvalidOperationException(
            $"Could not resolve property path '{path}' referenced by an index expression on entity '{entityType.Name}'.");
    }

    public override IReadOnlyList<MigrationOperation> GetDifferences(
        IRelationalModel? source,
        IRelationalModel? target
    )
    {
        var operations = base.GetDifferences(source, target);

        operations = ApplyTemporalConstraints(operations, source, target, typeMappingSource, out var temporalNeedsExtension);
        operations = ApplyExclusionConstraints(operations, source, target, out var exclusionNeedsExtension);

        // One shared CREATE EXTENSION for temporal and exclusion constraints alike.
        if ((temporalNeedsExtension || exclusionNeedsExtension) && ShouldInjectExtension(target))
        {
            var withExtension = operations.ToList();
            withExtension.Insert(0, new SqlOperation { Sql = $"CREATE EXTENSION IF NOT EXISTS {NpgsqlTemporalAnnotations.BtreeGistExtension};" });
            operations = withExtension;
        }

        return operations;
    }

    // Diffs the temporal UNIQUE constraints and temporal FOREIGN KEY constraints declared on entity
    // types. Drops are emitted as EF's own Drop* operations (the stock generator renders those
    // correctly) and placed before the base EF operations; adds are fully rendered DDL emitted as
    // SqlOperations after them, with UNIQUE constraints before FOREIGN KEYs.
    private static IReadOnlyList<MigrationOperation> ApplyTemporalConstraints(
        IReadOnlyList<MigrationOperation> operations,
        IRelationalModel?                 source,
        IRelationalModel?                 target,
        IRelationalTypeMappingSource      typeMappingSource,
        out bool                          needsBtreeGist
    )
    {
        needsBtreeGist = false;

        var sourceConstraints = BuildDescriptors(source, typeMappingSource);
        var targetConstraints = BuildDescriptors(target, typeMappingSource);
        var sourceForeignKeys = BuildForeignKeyDescriptors(source, typeMappingSource, sourceConstraints);
        var targetForeignKeys = BuildForeignKeyDescriptors(target, typeMappingSource, targetConstraints);

        if (sourceConstraints.Count == 0 && targetConstraints.Count == 0
                                        && sourceForeignKeys.Count == 0 && targetForeignKeys.Count == 0)
            return operations;

        var droppedTables = operations
                           .OfType<DropTableOperation>()
                           .Select(o => (o.Name, o.Schema))
                           .ToHashSet();

        // Source descriptors on tables the base operations rename are compared under their new
        // table identity; drops still target the old name — they run before the base RenameTable.
        var renamedTables = BuildRenamedTables(operations);

        var normalizedConstraints = new Dictionary<TemporalDescriptor, (string Table, string? Schema)>();
        foreach (var src in sourceConstraints)
        {
            var normalized = renamedTables.TryGetValue((src.Table, src.Schema), out var to)
                                 ? src with { Table = to.Name, Schema = to.Schema }
                                 : src;
            normalizedConstraints.TryAdd(normalized, (src.Table, src.Schema));
        }

        var normalizedForeignKeys = new Dictionary<TemporalForeignKeyDescriptor, (string Table, string? Schema)>();
        foreach (var src in sourceForeignKeys)
        {
            var normalized = src;
            if (renamedTables.TryGetValue((src.DependentTable, src.DependentSchema), out var dependent))
                normalized = normalized with { DependentTable = dependent.Name, DependentSchema = dependent.Schema };
            if (renamedTables.TryGetValue((src.PrincipalTable, src.PrincipalSchema), out var principal))
                normalized = normalized with { PrincipalTable = principal.Name, PrincipalSchema = principal.Schema };
            normalizedForeignKeys.TryAdd(normalized, (src.DependentTable, src.DependentSchema));
        }

        var pendingForeignKeyDrops = normalizedForeignKeys.Keys
            .Where(src => !droppedTables.Contains(normalizedForeignKeys[src])
                       && (!targetForeignKeys.Contains(src)
                        || DependsOnChangedTemporalConstraint(src, normalizedConstraints.Keys, targetConstraints)))
            .ToList();

        var pendingConstraintDrops = normalizedConstraints.Keys
            .Where(src => !targetConstraints.Contains(src)
                       && !droppedTables.Contains(normalizedConstraints[src]))
            .ToList();

        var pendingConstraintAdds = targetConstraints.Where(tgt => !normalizedConstraints.ContainsKey(tgt)).ToList();

        var pendingForeignKeyAdds = targetForeignKeys
            .Where(tgt => !normalizedForeignKeys.ContainsKey(tgt)
                       || DependsOnChangedTemporalConstraint(tgt, normalizedConstraints.Keys, targetConstraints))
            .ToList();

        // Drop/add pairs identical except for the name — typically default-named constraints on a
        // renamed table — become RENAME CONSTRAINT. Dependent foreign keys survive a rename, so
        // they don't churn (DependsOnChangedTemporalConstraint compares names insensitively).
        var renames = new List<MigrationOperation>();

        for (var i = pendingConstraintDrops.Count - 1; i >= 0; i--)
        {
            var dropped = pendingConstraintDrops[i];
            var renamed = pendingConstraintAdds.FirstOrDefault(a => a.Name != dropped.Name
                                                                 && (dropped with { Name = a.Name }).Equals(a));
            if (renamed is null)
                continue;

            renames.Add(new SqlOperation
                        {
                            Sql = $"ALTER TABLE {QuoteQualified(renamed.Table, renamed.Schema)} " +
                                  $"RENAME CONSTRAINT {Quote(dropped.Name)} TO {Quote(renamed.Name)};"
                        });
            pendingConstraintDrops.RemoveAt(i);
            pendingConstraintAdds.Remove(renamed);
        }

        for (var i = pendingForeignKeyDrops.Count - 1; i >= 0; i--)
        {
            var dropped = pendingForeignKeyDrops[i];
            var renamed = pendingForeignKeyAdds.FirstOrDefault(a => a.Name != dropped.Name
                                                                 && (dropped with { Name = a.Name }).Equals(a));
            if (renamed is null)
                continue;

            renames.Add(new SqlOperation
                        {
                            Sql = $"ALTER TABLE {QuoteQualified(renamed.DependentTable, renamed.DependentSchema)} " +
                                  $"RENAME CONSTRAINT {Quote(dropped.Name)} TO {Quote(renamed.Name)};"
                        });
            pendingForeignKeyDrops.RemoveAt(i);
            pendingForeignKeyAdds.Remove(renamed);
        }

        var result = new List<MigrationOperation>();

        foreach (var src in pendingForeignKeyDrops)
        {
            var (table, schema) = normalizedForeignKeys[src];
            result.Add(new DropForeignKeyOperation
                       {
                           Name   = src.Name,
                           Table  = table,
                           Schema = schema
                       });
        }

        foreach (var src in pendingConstraintDrops)
        {
            var (table, schema) = normalizedConstraints[src];
            result.Add(new DropUniqueConstraintOperation
                       {
                           Name   = src.Name,
                           Table  = table,
                           Schema = schema
                       });
        }

        result.AddRange(operations);
        result.AddRange(renames);

        foreach (var tgt in pendingConstraintAdds)
        {
            result.Add(new SqlOperation { Sql = BuildAddTemporalConstraintSql(tgt) });
            needsBtreeGist = true;
        }

        foreach (var tgt in pendingForeignKeyAdds)
            result.Add(new SqlOperation { Sql = BuildAddTemporalForeignKeySql(tgt) });

        return result;
    }

    // PostgreSQL requires the period (range) column last in the constraint's column list.
    //
    // Rendered here at design time — as raw SQL baked into the migration — rather than as an
    // AddUniqueConstraintOperation the runtime SQL generator specializes. The operation-based route
    // needed the UseNpgsqlComplexIndexes() wiring, and without it the stock Npgsql generator emitted
    // a plain `UNIQUE (key, period)`: valid DDL that applies cleanly and silently drops the entire
    // non-overlap guarantee. Exclusion constraints already render at design time for the same reason.
    private static string BuildAddTemporalConstraintSql(TemporalDescriptor constraint)
    {
        var columns = constraint.KeyColumns
                                .Select(Quote)
                                .Append($"{Quote(constraint.PeriodColumn)} WITHOUT OVERLAPS");

        return $"ALTER TABLE {QuoteQualified(constraint.Table, constraint.Schema)} " +
               $"ADD CONSTRAINT {Quote(constraint.Name)} UNIQUE ({string.Join(", ", columns)});";
    }

    // Same reasoning as BuildAddTemporalConstraintSql: PostgreSQL requires the period column last on
    // both sides, marked PERIOD. Temporal foreign keys are always NO ACTION.
    private static string BuildAddTemporalForeignKeySql(TemporalForeignKeyDescriptor foreignKey)
    {
        var dependentColumns = foreignKey.DependentColumns
                                         .Select(Quote)
                                         .Append($"PERIOD {Quote(foreignKey.DependentPeriodColumn)}");

        var principalColumns = foreignKey.PrincipalColumns
                                         .Select(Quote)
                                         .Append($"PERIOD {Quote(foreignKey.PrincipalPeriodColumn)}");

        return $"ALTER TABLE {QuoteQualified(foreignKey.DependentTable, foreignKey.DependentSchema)} " +
               $"ADD CONSTRAINT {Quote(foreignKey.Name)} " +
               $"FOREIGN KEY ({string.Join(", ", dependentColumns)}) " +
               $"REFERENCES {QuoteQualified(foreignKey.PrincipalTable, foreignKey.PrincipalSchema)} " +
               $"({string.Join(", ", principalColumns)});";
    }

    // Diffs the EXCLUDE constraints declared via HasExclusionConstraint and emits their DDL as raw
    // SQL operations (EF has no exclusion-constraint operation type). The DDL is fully rendered at
    // design time, so no runtime SQL-generator wiring is needed. Drops are placed before the base EF
    // operations, adds after — mirroring the index and temporal ordering.
    private static IReadOnlyList<MigrationOperation> ApplyExclusionConstraints(
        IReadOnlyList<MigrationOperation> operations,
        IRelationalModel?                 source,
        IRelationalModel?                 target,
        out bool                          needsBtreeGist
    )
    {
        needsBtreeGist = false;

        var sourceConstraints = BuildExclusionDescriptors(source);
        var targetConstraints = BuildExclusionDescriptors(target);

        // Target only — a snapshot that already contains a collision must stay diffable.
        ValidateUniqueExclusionNames(targetConstraints);

        if (sourceConstraints.Count == 0 && targetConstraints.Count == 0)
            return operations;

        var droppedTables = operations
                           .OfType<DropTableOperation>()
                           .Select(o => (o.Name, o.Schema))
                           .ToHashSet();

        // Source constraints on tables the base operations rename are compared under their new
        // table identity so the rename doesn't churn every constraint the table carries. Drops
        // still target the old name — they run before the base RenameTable.
        var renamedTables = BuildRenamedTables(operations);

        var normalizedSource = new Dictionary<ExclusionDescriptor, (string Table, string? Schema)>();
        foreach (var src in sourceConstraints)
        {
            var normalized = renamedTables.TryGetValue((src.Table, src.Schema), out var to)
                                 ? src with { Table = to.Name, Schema = to.Schema }
                                 : src;
            normalizedSource.TryAdd(normalized, (src.Table, src.Schema));
        }

        var pendingDrops = normalizedSource.Keys
                                           .Where(src => !targetConstraints.Contains(src)
                                                      && !droppedTables.Contains(normalizedSource[src]))
                                           .ToList();

        var pendingAdds = targetConstraints.Where(tgt => !normalizedSource.ContainsKey(tgt)).ToList();

        // A drop/add pair identical except for the name — typically a default-named constraint on a
        // renamed table — becomes RENAME CONSTRAINT: a catalog update instead of a gist rebuild.
        // Placed after the base operations, which may themselves rename the table.
        var renames = new List<MigrationOperation>();
        for (var i = pendingDrops.Count - 1; i >= 0; i--)
        {
            var dropped = pendingDrops[i];
            var renamed = pendingAdds.FirstOrDefault(a => a.Name != dropped.Name
                                                       && (dropped with { Name = a.Name }).Equals(a));
            if (renamed is null)
                continue;

            renames.Add(new SqlOperation
                        {
                            Sql = $"ALTER TABLE {QuoteQualified(renamed.Table, renamed.Schema)} " +
                                  $"RENAME CONSTRAINT {Quote(dropped.Name)} TO {Quote(renamed.Name)};"
                        });
            pendingDrops.RemoveAt(i);
            pendingAdds.Remove(renamed);
        }

        var drops = new List<MigrationOperation>();
        foreach (var src in pendingDrops)
        {
            var (table, schema) = normalizedSource[src];
            drops.Add(new SqlOperation
                      {
                          Sql = $"ALTER TABLE {QuoteQualified(table, schema)} DROP CONSTRAINT IF EXISTS {Quote(src.Name)};"
                      });
        }

        var adds = new List<MigrationOperation>();
        foreach (var tgt in pendingAdds)
        {
            adds.Add(new SqlOperation { Sql = BuildAddExclusionSql(tgt) });

            // Scalar equality elements under gist need the btree_gist operator classes.
            if (tgt.Method == "gist" && tgt.Parts.Any(p => p.Operator == "="))
                needsBtreeGist = true;
        }

        if (drops.Count == 0 && renames.Count == 0 && adds.Count == 0)
            return operations;

        return [.. drops, .. operations, .. renames, .. adds];
    }

    /// <summary>
    /// Fails when two distinct exclusion constraints on one table resolve to the same name.
    /// </summary>
    /// <remarks>
    /// Because every ADD is preceded by <c>DROP CONSTRAINT IF EXISTS</c>, such a pair does not fail
    /// at apply time — the migration runs clean and the second constraint silently replaces the
    /// first, leaving one of the declared guarantees unenforced. Catches collisions the declaration-
    /// time check cannot see, such as two default names derived from different paths that resolve to
    /// the same columns.
    /// </remarks>
    private static void ValidateUniqueExclusionNames(HashSet<ExclusionDescriptor> descriptors)
    {
        var collision = descriptors
                       .GroupBy(d => (d.Table, d.Schema, d.Name))
                       .FirstOrDefault(g => g.Count() > 1);

        if (collision is null)
            return;

        throw new InvalidOperationException(
            $"Two exclusion constraints on table '{collision.Key.Table}' both resolve to the name "
          + $"'{collision.Key.Name}': {string.Join(" and ", collision.Select(Describe))}. "
          + "Constraint names must be unique per table — give each declaration an explicit, distinct name.");

        static string Describe(ExclusionDescriptor descriptor)
        {
            var elements = string.Join(", ", descriptor.Parts.Select(p => $"{p.Value} WITH {p.Operator}"));
            return $"({elements}){(descriptor.Filter is null ? "" : $" WHERE {descriptor.Filter}")}";
        }
    }

    // Tables the base operations rename, keyed by old identity.
    private static Dictionary<(string Name, string? Schema), (string Name, string? Schema)> BuildRenamedTables(
        IReadOnlyList<MigrationOperation> operations)
    {
        var renamed = new Dictionary<(string Name, string? Schema), (string Name, string? Schema)>();
        foreach (var rename in operations.OfType<RenameTableOperation>())
            renamed[(rename.Name, rename.Schema)] = (rename.NewName ?? rename.Name, rename.NewSchema ?? rename.Schema);
        return renamed;
    }

    private static HashSet<ExclusionDescriptor> BuildExclusionDescriptors(IRelationalModel? model)
    {
        var set = new HashSet<ExclusionDescriptor>();
        if (model is null) return set;

        foreach (var entityType in model.Model.GetEntityTypes())
        {
            if (entityType.FindAnnotation(NpgsqlExclusionAnnotations.Constraints)?.Value is not string json
             || string.IsNullOrEmpty(json))
                continue;

            var table = entityType.GetTableName();
            if (table is null) continue;

            var schema      = entityType.GetSchema();
            var storeObject = StoreObjectIdentifier.Table(table, schema);

            foreach (var def in ExclusionConstraintSerializer.Deserialize(json))
            {
                var parts = new List<ResolvedExclusionPart>(def.Parts.Count);
                foreach (var part in def.Parts)
                {
                    if (part.IsExpression)
                    {
                        parts.Add(new ResolvedExclusionPart(true, part.Expression!, part.Operator));
                        continue;
                    }

                    var property = ResolveProperty(entityType, part.PropertyPath!)
                                ?? throw new InvalidOperationException(
                                       $"Could not resolve exclusion constraint property '{part.PropertyPath}' on entity '{entityType.Name}'.");

                    var column = property.GetColumnName(storeObject)
                              ?? throw new InvalidOperationException(
                                     $"Exclusion constraint property '{part.PropertyPath}' on entity '{entityType.Name}' has no column mapping for table '{table}'.");

                    parts.Add(new ResolvedExclusionPart(false, column, part.Operator));
                }

                var name = def.Name ?? $"EX_{table}_{string.Join("_", parts.Select(ExclusionPartToken))}";

                set.Add(new ExclusionDescriptor(
                    table,
                    schema,
                    name,
                    parts,
                    def.Method ?? "gist",
                    def.Filter,
                    def.Deferrable,
                    def.InitiallyDeferred));
            }
        }

        return set;
    }

    // The ADD is preceded by DROP CONSTRAINT IF EXISTS so the migration also applies cleanly on
    // databases where the constraint already exists — typically hand-written DDL being adopted
    // into a declarative HasExclusionConstraint (the drop is a no-op everywhere else).
    private static string BuildAddExclusionSql(ExclusionDescriptor constraint)
    {
        var elements = constraint.Parts.Select(p =>
            $"{(p.IsExpression ? $"({p.Value})" : Quote(p.Value))} WITH {p.Operator}");

        var table = QuoteQualified(constraint.Table, constraint.Schema);

        var sql = $"ALTER TABLE {table} DROP CONSTRAINT IF EXISTS {Quote(constraint.Name)};\n" +
                  $"ALTER TABLE {table} " +
                  $"ADD CONSTRAINT {Quote(constraint.Name)} EXCLUDE USING {constraint.Method} " +
                  $"({string.Join(", ", elements)})";

        if (!string.IsNullOrEmpty(constraint.Filter))
            sql += $" WHERE ({constraint.Filter})";

        if (constraint.Deferrable)
            sql += constraint.InitiallyDeferred ? " DEFERRABLE INITIALLY DEFERRED" : " DEFERRABLE";

        return sql + ";";
    }

    // Reduces an element to its alphanumeric characters for the default constraint name
    // (e.g. lower(email) -> loweremail); column names pass through.
    private static string ExclusionPartToken(ResolvedExclusionPart part)
    {
        if (!part.IsExpression)
            return part.Value;

        var token = new string([.. part.Value.Where(char.IsLetterOrDigit)]);
        return token.Length > 0 ? token : "expr";
    }

    private static string Quote(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string QuoteQualified(string table, string? schema)
        => schema is null ? Quote(table) : $"{Quote(schema)}.{Quote(table)}";

    private static HashSet<TemporalDescriptor> BuildDescriptors(
        IRelationalModel?            model,
        IRelationalTypeMappingSource typeMappingSource
    )
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
                    var property = ResolveProperty(entityType, keyProperty)
                                ?? throw new InvalidOperationException(
                                       $"Could not resolve temporal constraint key property '{keyProperty}' on entity '{entityType.Name}'.");

                    keyColumns.Add(
                        property.GetColumnName(storeObject)
                     ?? throw new InvalidOperationException(
                            $"Temporal constraint key property '{keyProperty}' on entity '{entityType.Name}' has no column mapping for table '{table}'.")
                    );
                }

                var periodProperty = ResolveProperty(entityType, def.PeriodProperty)
                                  ?? throw new InvalidOperationException(
                                         $"Could not resolve temporal constraint period column '{def.PeriodProperty}' on entity {entityType.Name}.");

                ValidatePeriodIsRangeOrMultirangeType(periodProperty, def.PeriodProperty, entityType.Name, typeMappingSource, "temporal constraint period property");

                var periodColumn = periodProperty.GetColumnName(storeObject)
                                   ?? throw new InvalidOperationException(
                                          $"Temporal constraint period property '{def.PeriodProperty}' on entity '{entityType.Name}' has no column mapping for table '{table}'.");

                var name = def.Name ?? $"AK_{table}_{string.Join("_", keyColumns)}_{periodColumn}";

                set.Add(new TemporalDescriptor(table, schema, name, keyColumns, periodColumn));
            }
        }

        return set;
    }

    private static HashSet<TemporalForeignKeyDescriptor> BuildForeignKeyDescriptors(
        IRelationalModel?            model,
        IRelationalTypeMappingSource typeMappingSource,
        HashSet<TemporalDescriptor>  temporalConstraints
    )
    {
        var set = new HashSet<TemporalForeignKeyDescriptor>();
        if (model is null) return set;

        foreach (var dependentEntityType in model.Model.GetEntityTypes())
        {
            if (dependentEntityType.FindAnnotation(NpgsqlTemporalAnnotations.ForeignKeys)?.Value is not string json
             || string.IsNullOrEmpty(json))
                continue;

            var dependentTable = dependentEntityType.GetTableName();
            if (dependentTable is null) continue;

            var dependentSchema      = dependentEntityType.GetSchema();
            var dependentStoreObject = StoreObjectIdentifier.Table(dependentTable, dependentSchema);

            foreach (var def in TemporalForeignKeySerializer.Deserialize(json))
            {
                var principalEntityType = ResolveEntityType(model.Model, def.PrincipalEntityType)
                                       ?? throw new InvalidOperationException(
                                              $"Could not resolve temporal foreign key principal entity '{def.PrincipalEntityType}' from dependent entity '{dependentEntityType.Name}'.");

                var principalTable = principalEntityType.GetTableName()
                                  ?? throw new InvalidOperationException(
                                         $"Temporal foreign key principal entity '{principalEntityType.Name}' is not mapped to a table.");

                var principalSchema      = principalEntityType.GetSchema();
                var principalStoreObject = StoreObjectIdentifier.Table(principalTable, principalSchema);

                var dependentColumns = ResolveColumns(
                    dependentEntityType,
                    def.DependentKeyProperties,
                    dependentStoreObject,
                    "temporal foreign key dependent key",
                    dependentTable);

                var principalColumns = ResolveColumns(
                    principalEntityType,
                    def.PrincipalKeyProperties,
                    principalStoreObject,
                    "temporal foreign key principal key",
                    principalTable);

                var dependentPeriodProperty = ResolveProperty(dependentEntityType, def.DependentPeriodProperty)
                                           ?? throw new InvalidOperationException(
                                                  $"Could not resolve temporal foreign key dependent period column '{def.DependentPeriodProperty}' on entity '{dependentEntityType.Name}'.");
                ValidatePeriodIsRangeOrMultirangeType(dependentPeriodProperty, def.DependentPeriodProperty, dependentEntityType.Name, typeMappingSource, "temporal foreign key dependent period property");
                var dependentPeriodColumn = dependentPeriodProperty.GetColumnName(dependentStoreObject)
                                         ?? throw new InvalidOperationException(
                                                $"Temporal foreign key dependent period property '{def.DependentPeriodProperty}' on entity '{dependentEntityType.Name}' has no column mapping for table '{dependentTable}'.");

                var principalPeriodProperty = ResolveProperty(principalEntityType, def.PrincipalPeriodProperty)
                                           ?? throw new InvalidOperationException(
                                                  $"Could not resolve temporal foreign key principal period column '{def.PrincipalPeriodProperty}' on entity '{principalEntityType.Name}'.");
                ValidatePeriodIsRangeOrMultirangeType(principalPeriodProperty, def.PrincipalPeriodProperty, principalEntityType.Name, typeMappingSource, "temporal foreign key principal period property");
                var principalPeriodColumn = principalPeriodProperty.GetColumnName(principalStoreObject)
                                         ?? throw new InvalidOperationException(
                                                $"Temporal foreign key principal period property '{def.PrincipalPeriodProperty}' on entity '{principalEntityType.Name}' has no column mapping for table '{principalTable}'.");

                if (!HasMatchingPrincipalTemporalConstraint(temporalConstraints, principalTable, principalSchema, principalColumns, principalPeriodColumn))
                {
                    throw new InvalidOperationException(
                        $"Temporal foreign key '{def.Name ?? DefaultForeignKeyName(dependentTable, principalTable, dependentColumns, dependentPeriodColumn)}' " +
                        $"references '{principalTable}' ({string.Join(", ", principalColumns)}, PERIOD {principalPeriodColumn}), " +
                        "but no matching HasTemporalConstraint was found on the principal entity. " +
                        "PostgreSQL requires the referenced table to have a UNIQUE or PRIMARY KEY constraint with WITHOUT OVERLAPS."
                    );
                }

                var name = def.Name ?? DefaultForeignKeyName(dependentTable, principalTable, dependentColumns, dependentPeriodColumn);

                set.Add(new TemporalForeignKeyDescriptor(
                    dependentTable,
                    dependentSchema,
                    principalTable,
                    principalSchema,
                    name,
                    dependentColumns,
                    dependentPeriodColumn,
                    principalColumns,
                    principalPeriodColumn));
            }
        }

        return set;
    }

    private static List<string> ResolveColumns(
        ITypeBase             entityType,
        IReadOnlyList<string> propertyPaths,
        StoreObjectIdentifier storeObject,
        string                usage,
        string                table)
    {
        var columns = new List<string>(propertyPaths.Count);
        foreach (var propertyPath in propertyPaths)
        {
            var property = ResolveProperty(entityType, propertyPath)
                        ?? throw new InvalidOperationException(
                               $"Could not resolve {usage} property '{propertyPath}' on entity '{entityType.Name}'.");

            columns.Add(
                property.GetColumnName(storeObject)
             ?? throw new InvalidOperationException(
                    $"{usage} property '{propertyPath}' on entity '{entityType.Name}' has no column mapping for table '{table}'.")
            );
        }

        return columns;
    }

    private static bool HasMatchingPrincipalTemporalConstraint(
        HashSet<TemporalDescriptor> constraints,
        string                      table,
        string?                     schema,
        IReadOnlyList<string>       keyColumns,
        string                      periodColumn)
        => constraints.Any(c => c.Table == table
                             && c.Schema == schema
                             && c.PeriodColumn == periodColumn
                             && c.KeyColumns.SequenceEqual(keyColumns));

    // Name differences are ignored: a name-only change surfaces as RENAME CONSTRAINT, which keeps
    // dependent foreign keys intact — only structural changes force the FK to be rebuilt.
    private static bool DependsOnChangedTemporalConstraint(
        TemporalForeignKeyDescriptor           foreignKey,
        IReadOnlyCollection<TemporalDescriptor> sourceConstraints,
        IReadOnlyCollection<TemporalDescriptor> targetConstraints)
    {
        var sourceConstraint = sourceConstraints.FirstOrDefault(c => IsPrincipalConstraintFor(foreignKey, c));
        var targetConstraint = targetConstraints.FirstOrDefault(c => IsPrincipalConstraintFor(foreignKey, c));

        return sourceConstraint is null
            || targetConstraint is null
            || !sourceConstraint.Equals(targetConstraint with { Name = sourceConstraint.Name });
    }

    private static bool IsPrincipalConstraintFor(TemporalForeignKeyDescriptor foreignKey, TemporalDescriptor constraint)
        => constraint.Table == foreignKey.PrincipalTable
        && constraint.Schema == foreignKey.PrincipalSchema
        && constraint.PeriodColumn == foreignKey.PrincipalPeriodColumn
        && constraint.KeyColumns.SequenceEqual(foreignKey.PrincipalColumns);

    private static string DefaultForeignKeyName(
        string                dependentTable,
        string                principalTable,
        IReadOnlyList<string> dependentColumns,
        string                dependentPeriodColumn)
        => $"FK_{dependentTable}_{principalTable}_{string.Join("_", dependentColumns)}_{dependentPeriodColumn}";

    private static IEntityType? ResolveEntityType(IModel model, string entityTypeName)
    {
        var exact = model.GetEntityTypes()
                         .Where(e => e.Name == entityTypeName || e.ClrType.FullName == entityTypeName)
                         .ToList();

        if (exact.Count > 0)
            return exact[0];

        // Bare CLR-name fallback: refuse to guess when several entities share the short name.
        var byShortName = model.GetEntityTypes()
                               .Where(e => e.ClrType.Name == entityTypeName)
                               .ToList();

        if (byShortName.Count > 1)
            throw new InvalidOperationException(
                $"The temporal foreign key principal entity name '{entityTypeName}' is ambiguous; it matches: " +
                $"{string.Join(", ", byShortName.Select(e => e.Name))}. Use the full CLR type name.");

        return byShortName.SingleOrDefault();
    }

    private static void ValidatePeriodIsRangeOrMultirangeType(
        IProperty                    property,
        string                       propertyName,
        string                       entityName,
        IRelationalTypeMappingSource typeMappingSource,
        string                       usage
    )
    {
        var clrType   = property.ClrType;
        var storeType = typeMappingSource.FindMapping(property)?.StoreType ?? property.GetColumnType();

        var isValidPeriod = IsRangeClrType(clrType)
                         || IsMultirangeClrType(clrType)
                         || (storeType is not null && storeType.EndsWith("range", StringComparison.OrdinalIgnoreCase));

        if (!isValidPeriod)
            throw new InvalidOperationException(
                $"The {usage} '{propertyName}' on entity " +
                $"'{entityName}' does not appear to be a range or multirange type. " +
                $"Found CLR type '{clrType.Name}'" +
                (storeType is not null ? $" (store type: '{storeType}')" : "") +
                ". Expected NpgsqlRange<T>, a PostgreSQL range/multirange column type, " +
                "or a store type ending in 'range' (e.g., daterange, int4multirange)."
            );
    }

    private static bool IsRangeClrType(Type type)
        => type.IsGenericType
        && type.GetGenericTypeDefinition().FullName is "NpgsqlTypes.NpgsqlRange`1";

    private static bool IsMultirangeClrType(Type type)
        => type.Namespace is "NpgsqlTypes"
        && type.Name.EndsWith("Multirange", StringComparison.Ordinal);

    private static IProperty? ResolveProperty(ITypeBase entityType, string dotPath)
    {
        var       parts   = dotPath.Split('.');
        ITypeBase current = entityType;

        for (var i = 0; i < parts.Length; i++)
        {
            if (i == parts.Length - 1)
                return current.FindProperty(parts[i]);

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

    private sealed record ResolvedExclusionPart(bool IsExpression, string Value, string Operator);

    private sealed record ExclusionDescriptor(
        string                               Table,
        string?                              Schema,
        string                               Name,
        IReadOnlyList<ResolvedExclusionPart> Parts,
        string                               Method,
        string?                              Filter,
        bool                                 Deferrable,
        bool                                 InitiallyDeferred)
    {
        public bool Equals(ExclusionDescriptor? other) =>
            other is not null
         && Table             == other.Table
         && Schema            == other.Schema
         && Name              == other.Name
         && Method            == other.Method
         && Filter            == other.Filter
         && Deferrable        == other.Deferrable
         && InitiallyDeferred == other.InitiallyDeferred
         && Parts.SequenceEqual(other.Parts);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Table);
            hash.Add(Schema);
            hash.Add(Name);
            foreach (var part in Parts) hash.Add(part);
            hash.Add(Method);
            hash.Add(Filter);
            hash.Add(Deferrable);
            hash.Add(InitiallyDeferred);
            return hash.ToHashCode();
        }
    }

    private sealed record TemporalForeignKeyDescriptor(
        string                DependentTable,
        string?               DependentSchema,
        string                PrincipalTable,
        string?               PrincipalSchema,
        string                Name,
        IReadOnlyList<string> DependentColumns,
        string                DependentPeriodColumn,
        IReadOnlyList<string> PrincipalColumns,
        string                PrincipalPeriodColumn)
    {
        public bool Equals(TemporalForeignKeyDescriptor? other) =>
            other is not null
         && DependentTable        == other.DependentTable
         && DependentSchema       == other.DependentSchema
         && PrincipalTable        == other.PrincipalTable
         && PrincipalSchema       == other.PrincipalSchema
         && Name                  == other.Name
         && DependentPeriodColumn == other.DependentPeriodColumn
         && PrincipalPeriodColumn == other.PrincipalPeriodColumn
         && DependentColumns.SequenceEqual(other.DependentColumns)
         && PrincipalColumns.SequenceEqual(other.PrincipalColumns);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(DependentTable);
            hash.Add(DependentSchema);
            hash.Add(PrincipalTable);
            hash.Add(PrincipalSchema);
            hash.Add(Name);
            foreach (var column in DependentColumns) hash.Add(column);
            hash.Add(DependentPeriodColumn);
            foreach (var column in PrincipalColumns) hash.Add(column);
            hash.Add(PrincipalPeriodColumn);
            return hash.ToHashCode();
        }
    }
}

#pragma warning restore EF1001
