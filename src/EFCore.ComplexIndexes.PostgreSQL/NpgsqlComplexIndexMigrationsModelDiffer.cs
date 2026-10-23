using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace EFCore.ComplexIndexes.PostgreSQL;

#pragma warning disable EF1001

/// <summary>
/// Extends <see cref="CustomMigrationsModelDiffer"/> to validate that provider annotations on complex
/// index operations use recognized Npgsql keys, and to emit PostgreSQL 18 temporal <c>UNIQUE</c>
/// constraints (<c>WITHOUT OVERLAPS</c>) declared via <c>HasTemporalConstraint</c>. Temporal and
/// exclusion constraint DDL is rendered at design time, so neither needs runtime SQL-generator wiring.
/// </summary>
/// <param name="typeMappingSource">EF Core relational type mapping source.</param>
/// <param name="migrationsAnnotationProvider">EF Core migrations annotation provider.</param>
/// <param name="relationalAnnotationProvider">EF Core relational annotation provider.</param>
/// <param name="rowIdentityMapFactory">EF Core row identity map factory.</param>
/// <param name="commandBatchPreparerDependencies">EF Core command batch preparer dependencies.</param>
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
    // Held in a field rather than captured from the primary constructor: the same parameter is passed
    // to the base constructor, and capturing it as well would store it twice (CS9107).
    private readonly IRelationalTypeMappingSource _typeMappingSource = typeMappingSource;

    // Set while an exclusion constraint's filter is resolved; see ResolveUnmappedPart.
    private bool _resolvingExclusionFilter;

    private static readonly HashSet<string> SupportedNpgsqlAnnotations =
    [
        NpgsqlAnnotations.IndexMethod,
        NpgsqlAnnotations.IndexOperators,
        NpgsqlAnnotations.IndexInclude,
        NpgsqlAnnotations.CreatedConcurrently,
        NpgsqlAnnotations.NullsDistinct,
        NpgsqlAnnotations.IndexCollation
    ];

    /// <summary>
    /// Npgsql's own sort-order annotations, deliberately <em>not</em> forwarded: this package owns
    /// sort direction (<c>DbOrder.Desc</c>, rendered through EF's native <c>IsDescending</c>) and
    /// null ordering (<c>DbOrder.NullsFirst</c>/<c>NullsLast</c>, rendered from the parts
    /// annotation). Forwarding these too would make two mechanisms describe one property of an
    /// index, with no rule for which wins.
    /// </summary>
    private static readonly HashSet<string> SupersededNpgsqlAnnotations =
    [
        NpgsqlAnnotations.IndexSortOrder,
        NpgsqlAnnotations.IndexNullSortOrder
    ];

    /// <summary>
    /// This differ renders what only the satellite does — its index options, exclusion constraints, temporal constraints and temporal foreign keys — so the core's refusal of them
    /// (<c>UseComplexIndexes()</c> where this satellite's registration belongs) does not apply.
    /// </summary>
    internal override bool IsSatelliteDiffer => true;

    /// <summary>
    /// Forwards exactly the Npgsql index-option annotations Npgsql's SQL generator renders: the
    /// whitelisted keys plus every <c>Npgsql:StorageParameter:*</c> key, which is per-parameter. Every
    /// <c>SqlServer:*</c> key is forwarded too, only so that <see cref="ValidateCreateIndexOperation"/>
    /// rejects it — a property-level <c>.IsClustered()</c> on a model diffed by this satellite would
    /// otherwise be dropped by the whitelist without a word.
    /// </summary>
    protected override bool IsForwardedIndexAnnotation(string annotationName)
        => SupportedNpgsqlAnnotations.Contains(annotationName)
        || NpgsqlAnnotations.IsStorageParameter(annotationName)
        || annotationName.StartsWith("SqlServer:", StringComparison.Ordinal);

    /// <summary>PostgreSQL renames indexes standalone (<c>ALTER INDEX … RENAME TO</c>).</summary>
    protected override bool CanRenameIndexes => true;

    /// <summary>
    /// An <c>ALTER COLUMN</c> that Npgsql renders as <c>DROP COLUMN</c> plus <c>ADD COLUMN</c>
    /// (<see cref="RecreatesColumn"/>), on a column the index reads: <c>DROP COLUMN</c> takes the
    /// index with it, and Npgsql re-creates only EF Core's own (#184).
    /// </summary>
    protected override bool RequiresIndexRecreation(MigrationOperation operation, IReadOnlySet<string> columns)
        => operation is AlterColumnOperation alter && columns.Contains(alter.Name) && RecreatesColumn(alter);

    /// <summary>
    /// Whether Npgsql renders the operation as <c>DROP COLUMN</c> plus <c>ADD COLUMN</c>: PostgreSQL
    /// cannot change a generated column's expression or storage in place. Npgsql decides on
    /// <c>ComputedColumnSql</c> and <c>IsStored</c>, after filling them in for a <c>tsvector</c>
    /// column from its configuration and properties, so those annotations count too
    /// (<c>NpgsqlMigrationsSqlGenerator.Generate(AlterColumnOperation, …)</c>, Npgsql 10).
    /// </summary>
    private static bool RecreatesColumn(AlterColumnOperation alter)
        => alter.ComputedColumnSql != alter.OldColumn.ComputedColumnSql
        || alter.IsStored          != alter.OldColumn.IsStored
        || !AnnotationValues.ValuesEqual(alter[NpgsqlAnnotations.TsVectorConfig],     alter.OldColumn[NpgsqlAnnotations.TsVectorConfig])
        || !AnnotationValues.ValuesEqual(alter[NpgsqlAnnotations.TsVectorProperties], alter.OldColumn[NpgsqlAnnotations.TsVectorProperties]);

    // The columns the base operations drop and add again, by table as the target names it.
    private static HashSet<(string Table, string? Schema, string Column)> RecreatedColumns(IReadOnlyList<MigrationOperation> operations)
        => [.. operations.OfType<AlterColumnOperation>().Where(RecreatesColumn).Select(o => (o.Table, o.Schema, o.Name))];

    // The columns whose type the base operations change, by table as the target names it.
    private static HashSet<(string Table, string? Schema, string Column)> RetypedColumns(IReadOnlyList<MigrationOperation> operations)
        => [.. operations.OfType<AlterColumnOperation>()
                         .Where(o => o.ColumnType != o.OldColumn.ColumnType || o.ClrType != o.OldColumn.ClrType)
                         .Select(o => (o.Table, o.Schema, o.Name))];

    // Whether a constraint of the target model reads one of the given columns: one of its columns,
    // or a column its expressions or filter name (ColumnsNamedIn, an over-approximation that costs
    // at most a needless rebuild).
    private static bool ReadsAnyOf(
        IRelationalModel                                         target,
        HashSet<(string Table, string? Schema, string Column)> changed,
        string                                                   table,
        string?                                                  schema,
        IEnumerable<string>                                      columns,
        string                                                   text = "")
        => changed.Count > 0
        && columns.Concat(ColumnsNamedIn(target, table, schema, text)).Any(column => changed.Contains((table, schema, column)));

    /// <summary>
    /// PostgreSQL keeps index names unique among the relations of a schema, so the core's own checks
    /// already reject two complex indexes, or a complex and a native index, sharing a name anywhere
    /// in one schema. Everything else PostgreSQL shares that namespace with is left to
    /// <c>ValidateNamesAcrossKinds</c>.
    /// </summary>
    protected override IndexNameScope IndexNameScope => IndexNameScope.Schema;

    /// <summary>
    /// EF Core quotes every identifier, and PostgreSQL keeps the case of a quoted one, so
    /// <c>ix_code</c> and <c>IX_CODE</c> are two names.
    /// </summary>
    protected override StringComparer IndexNameComparer => StringComparer.Ordinal;

    /// <summary>
    /// PostgreSQL's identifier limit (<c>NAMEDATALEN - 1</c>, 63) is a byte count: a name made of
    /// non-ASCII characters hits it well before its 63rd character.
    /// </summary>
    protected override (int Length, string Unit) MeasureIdentifier(string identifier)
        => (System.Text.Encoding.UTF8.GetByteCount(identifier), "bytes");

    /// <summary>
    /// Npgsql's generator reads index collations from <c>Relational:Collation</c> on the operation;
    /// the option is stored under Npgsql's model key so that a property-level declaration is never
    /// mistaken for the column's collation.
    /// </summary>
    protected override string ToOperationAnnotationName(string annotationName)
        => annotationName == NpgsqlAnnotations.IndexCollation
               ? RelationalAnnotationNames.Collation
               : base.ToOperationAnnotationName(annotationName);

    /// <summary>
    /// Rejects <c>Npgsql:*</c> index options this package does not render — typically an entity-level
    /// declaration carrying an option the satellite has no support for, since entity-level provider
    /// annotations reach the operation unfiltered (the property-level path is already whitelisted by
    /// <see cref="IsForwardedIndexAnnotation"/>) — and every <c>SqlServer:*</c> option, which belongs
    /// to the other satellite.
    /// </summary>
    protected override void ValidateCreateIndexOperation(CreateIndexOperation operation)
    {
        foreach (var annotation in operation.GetAnnotations())
        {
            // The other satellite's options: Npgsql's generator would ignore them, so a clustered or
            // fill-factor declaration would apply as a plain index without a word.
            if (annotation.Name.StartsWith("SqlServer:", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Complex index '{operation.Name}' carries the SQL Server annotation '{annotation.Name}', but the " +
                    "model is diffed with the PostgreSQL satellite. Use the EFCore.ComplexIndexes.PostgreSQL options instead.");

            if (!annotation.Name.StartsWith("Npgsql:", StringComparison.Ordinal)
             || SupportedNpgsqlAnnotations.Contains(annotation.Name)
             || NpgsqlAnnotations.IsStorageParameter(annotation.Name))
                continue;

            // Superseded keys get their own message: they are not unknown, they are the wrong way
            // to say something this package already expresses per column.
            throw SupersededNpgsqlAnnotations.Contains(annotation.Name)
                      ? new InvalidOperationException(
                            $"Complex index '{operation.Name}' carries '{annotation.Name}'. Declare direction and " +
                            "null ordering with DbOrder.Asc/Desc/NullsFirst/NullsLast (or the ExpressionIndexBuilder " +
                            $"equivalents) — this package renders sort options per column and never forwards " +
                            $"'{annotation.Name}'.")
                      : new InvalidOperationException(
                            $"Unrecognized Npgsql index annotation '{annotation.Name}' on complex index '{operation.Name}'. " +
                            $"Supported annotations: {string.Join(", ", SupportedNpgsqlAnnotations)}.");
        }
    }

    /// <summary>Resolves property paths inside INCLUDE lists to column names (verbatim fallback).</summary>
    protected override object? TransformIndexAnnotation(
        IEntityType           entityType,
        string                annotationName,
        object?               value,
        StoreObjectIdentifier storeObject
    ) => annotationName == NpgsqlAnnotations.IndexInclude
             ? ResolveIncludeColumns(entityType, value, storeObject)
             : base.TransformIndexAnnotation(entityType, annotationName, value, storeObject);

    /// <summary>
    /// Resolves an index part whose path has no table column: a member of a complex property mapped
    /// to JSON via <c>ToJson()</c>, or the JSON-mapped complex property (or complex collection)
    /// itself.
    /// </summary>
    /// <remarks>
    /// A member is rendered exactly as Npgsql's query translation renders it, because PostgreSQL only
    /// uses an expression index for a query whose expression matches it:
    /// <c>"doc" -&gt;&gt; 'A'</c> for one step and <c>"doc" #&gt;&gt; '{A,B}'</c> for more, cast to
    /// the member's store type unless that is a string (<c>CAST("doc" -&gt;&gt; 'Rank' AS integer)</c>),
    /// <c>decode(…, 'base64')</c> for <c>bytea</c>, and <c>jsonb</c> for a primitive collection.
    /// <c>HasJsonPropertyName</c> is honored. A date or time member stays text: the cast EF Core's
    /// queries apply to it is not IMMUTABLE and cannot appear in an index, so the part records the
    /// fallback and the differ rejects a non-unique index that leads with it.
    /// <para>
    /// A path ending at the JSON-mapped complex property resolves to its container column — a plain
    /// column index, typically <c>USING gin</c>, that the stock generator renders with no runtime
    /// wiring. A complex property nested inside the document resolves to a <c>jsonb</c> extraction,
    /// which GIN indexes too. Expression parts require the <c>UseNpgsqlComplexIndexes()</c> runtime
    /// wiring.
    /// </para>
    /// <para>
    /// A model without <see cref="ComplexIndexAnnotations.RenderingVersion"/> — a snapshot written
    /// before 5.4.0 — is rendered the original way, <c>"doc" -&gt; 'A' -&gt;&gt; 'B'</c> as text with no
    /// cast, which matched the query translation only for a top-level string. Resolving the snapshot
    /// by its own rules is what turns the change into one drop-and-create per affected index; resolved
    /// alike, the two sides would never differ and existing databases would keep the unused index.
    /// </para>
    /// </remarks>
    protected override ResolvedIndexPart? ResolveUnmappedPart(
        IEntityType           entityType,
        IndexPartDefinition   part,
        StoreObjectIdentifier storeObject
    )
    {
        if (part.PropertyPath is null)
            return null;

        var queryAligned = (part.RenderingVersion ?? ComplexIndexStorage.GetRenderingVersion(entityType.Model)) >= 2;

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

        var leaf = current.FindProperty(segments[^1]);
        if (leaf is null)
            return ResolveComplexLeaf(current, segments[^1], containerColumn, jsonPath, part, queryAligned, storeObject);

        if (containerColumn is null)
            return null;

        jsonPath.Add(leaf.GetJsonPropertyName() ?? leaf.Name);

        // Default index names come from the container and the path, never from the rendered SQL,
        // and from the container as the declaring model names it (RenderedColumn).
        var nameToken = containerColumn + string.Concat(jsonPath);
        containerColumn = RenderedColumn(containerColumn, storeObject);

        // Exclusion constraint filters are pinned to rendering 1, which renders every member as
        // text. A member the current rendering casts — a number, boolean, uuid or enum — compared
        // as text fails at apply (text > integer with 42883, a text WHERE with 42804), so it takes
        // the current rendering; the cast keeps NULL as NULL, so a filter that did apply, such as
        // IS NOT NULL, still holds for the same rows. Every other member keeps rendering 1.
        if (!queryAligned && _resolvingExclusionFilter && RendersAsCast(leaf))
            queryAligned = true;

        // An exclusion filter's member that falls back to text under the current rendering is text
        // here too; saying so lets the filter resolution refuse comparing it with a column (#119).
        if (!queryAligned)
            return new ResolvedIndexPart(true, BuildLegacyJsonExtraction(containerColumn, jsonPath, asText: true), part.Descending, part.NullSort)
                   {
                       NameToken    = nameToken,
                       TextFallback = _resolvingExclusionFilter && RenderJsonScalar(leaf, containerColumn, jsonPath).FallbackStoreType is { } storeType
                                          ? (part.PropertyPath, storeType)
                                          : null
                   };

        var (sql, fallbackStoreType) = RenderJsonScalar(leaf, containerColumn, jsonPath);

        return new ResolvedIndexPart(true, sql, part.Descending, part.NullSort)
               {
                   NameToken    = nameToken,
                   TextFallback = fallbackStoreType is null ? null : (part.PropertyPath, fallbackStoreType)
               };
    }

    // Whether RenderJsonScalar renders the member as a CAST of its text.
    private bool RendersAsCast(IProperty leaf)
    {
        var mapping = leaf.FindRelationalTypeMapping() ?? _typeMappingSource.FindMapping(leaf);

        return !leaf.IsPrimitiveCollection
            && mapping is not (null or StringTypeMapping)
            && mapping.StoreTypeNameBase is not ("jsonb" or "json" or "bytea")
            && !StableTextInputTypes.Contains(mapping.StoreTypeNameBase);
    }

    // Types whose text input function is STABLE rather than IMMUTABLE — the conversion depends on
    // DateStyle, TimeZone or lc_monetary — so a cast to them from text cannot appear in an index.
    private static readonly HashSet<string> StableTextInputTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "date", "time", "time without time zone", "time with time zone", "timetz",
        "timestamp", "timestamp without time zone", "timestamp with time zone", "timestamptz",
        "interval", "money"
    };

    // Mirrors NpgsqlQuerySqlGenerator.VisitJsonScalar, identical in Npgsql 10 and 11, case for case
    // and in its order: jsonb for a JSON-mapped value (a primitive collection, or a json/jsonb
    // scalar such as a JsonDocument), no cast for a string mapping, decode() for bytea, and a CAST to
    // the store type for everything else — except where that cast is not IMMUTABLE, which falls
    // back to text and reports the store type the queries use instead.
    private (string Sql, string? FallbackStoreType) RenderJsonScalar(IProperty leaf, string containerColumn, List<string> jsonPath)
    {
        var mapping = leaf.FindRelationalTypeMapping() ?? _typeMappingSource.FindMapping(leaf);

        if (leaf.IsPrimitiveCollection || mapping?.StoreTypeNameBase is "jsonb" or "json")
            return (BuildJsonPath(containerColumn, jsonPath, returnsText: false), null);

        var text = BuildJsonPath(containerColumn, jsonPath, returnsText: true);

        return mapping switch
        {
            null or StringTypeMapping                                     => (text, null),
            _ when mapping.StoreTypeNameBase == "bytea"                    => ($"decode({text}, 'base64')", null),
            _ when StableTextInputTypes.Contains(mapping.StoreTypeNameBase) => (text, mapping.StoreType),
            _                                                             => ($"CAST({text} AS {mapping.StoreType})", null)
        };
    }

    // Mirrors NpgsqlQuerySqlGenerator.GenerateJsonPath: -> / ->> for a single step; #> / #>> for
    // more, with a '{A,B}' literal when every segment is ASCII letters and digits and an
    // ARRAY['A','B']::text[] otherwise. PostgreSQL matches an index to a query by expression, so the
    // choice has to be the same one.
    private static string BuildJsonPath(string containerColumn, List<string> jsonPath, bool returnsText)
    {
        var column = Quote(containerColumn);

        if (jsonPath.Count == 1)
            return $"{column} {(returnsText ? "->>" : "->")} {Literal(jsonPath[0])}";

        var path = jsonPath.All(segment => segment.All(char.IsAsciiLetterOrDigit))
                       ? $"'{{{string.Join(",", jsonPath)}}}'"
                       : $"ARRAY[{string.Join(",", jsonPath.Select(Literal))}]::text[]";

        return $"{column} {(returnsText ? "#>>" : "#>")} {path}";

        static string Literal(string value) => $"'{value.Replace("'", "''")}'";
    }

    // The path ends at a complex property rather than a scalar: the whole document, or a
    // sub-document. At the top of a ToJson() mapping — and a complex collection is always JSON —
    // that is the container column itself, so the index is a plain column index the stock generator
    // renders. Nested inside a document it is an extraction yielding jsonb rather than text.
    // A table-split complex property has no single column to stand for it, so that stays unresolved.
    private ResolvedIndexPart? ResolveComplexLeaf(
        ITypeBase             current,
        string                name,
        string?               containerColumn,
        List<string>          jsonPath,
        IndexPartDefinition   part,
        bool                  queryAligned,
        StoreObjectIdentifier storeObject
    )
    {
        var complexProperty = current.FindComplexProperty(name);
        if (complexProperty is null)
            return null;

        if (containerColumn is null)
        {
            var column = complexProperty.ComplexType.GetContainerColumnName();
            return column is null
                       ? null
                       : new ResolvedIndexPart(false, RenderedColumn(column, storeObject), part.Descending, part.NullSort) { NameToken = column };
        }

        jsonPath.Add(complexProperty.GetJsonPropertyName() ?? complexProperty.Name);

        var rendered = RenderedColumn(containerColumn, storeObject);
        var sql = queryAligned
                      ? BuildJsonPath(rendered, jsonPath, returnsText: false)
                      : BuildLegacyJsonExtraction(rendered, jsonPath, asText: false);

        return new ResolvedIndexPart(true, sql, part.Descending, part.NullSort) { NameToken = containerColumn + string.Concat(jsonPath) };
    }

    // Rendering version 1, kept for snapshots written before 5.4.0: "col" -> 'A' -> 'B' (jsonb) or,
    // with asText, "col" -> 'A' ->> 'B' (text) for the last step.
    private static string BuildLegacyJsonExtraction(string containerColumn, List<string> jsonPath, bool asText)
    {
        var sql = new System.Text.StringBuilder(Quote(containerColumn));
        for (var i = 0; i < jsonPath.Count; i++)
        {
            var last = i == jsonPath.Count - 1;
            sql.Append(last && asText ? " ->> '" : " -> '")
               .Append(jsonPath[i].Replace("'", "''"))
               .Append('\'');
        }

        return sql.ToString();
    }

    /// <summary>
    /// Runs the core complex-index diff, then adds PostgreSQL-specific DDL: temporal <c>UNIQUE …
    /// WITHOUT OVERLAPS</c> constraints, temporal foreign keys, exclusion constraints, and a single
    /// shared <c>CREATE EXTENSION btree_gist</c> when any of them needs it.
    /// </summary>
    /// <param name="source">The model migrated from — typically the snapshot.</param>
    /// <param name="target">The model migrated to — the current <c>OnModelCreating</c> result.</param>
    /// <returns>The full operation list, constraint DDL included as design-time <c>SqlOperation</c>s.</returns>
    public override IReadOnlyList<MigrationOperation> GetDifferences(
        IRelationalModel? source,
        IRelationalModel? target
    )
    {
        var operations = base.GetDifferences(source, target);

        operations = ApplyTemporalConstraints(operations, source, target, _typeMappingSource, out var temporalNeedsExtension);
        operations = ApplyExclusionConstraints(operations, source, target, out var exclusionNeedsExtension);

        // One shared CREATE EXTENSION for temporal and exclusion constraints alike.
        if ((temporalNeedsExtension || exclusionNeedsExtension) && ShouldInjectExtension(target))
        {
            var withExtension = operations.ToList();
            withExtension.Insert(0, new SqlOperation { Sql = $"CREATE EXTENSION IF NOT EXISTS {NpgsqlTemporalAnnotations.BtreeGistExtension};" });
            operations = withExtension;
        }

        // Last, so the per-kind checks before it keep their own messages.
        ValidateNamesAcrossKinds(target);

        return operations;
    }

    /// <summary>
    /// Fails when a name this package introduces is already taken in the PostgreSQL namespace it
    /// lands in — by another declaration of any kind, or by one of EF Core's own.
    /// </summary>
    /// <remarks>
    /// PostgreSQL has two namespaces here. A constraint name is unique among the constraints of its
    /// table: a clash fails the migration with 42710, or — every exclusion constraint being added
    /// after <c>DROP CONSTRAINT IF EXISTS</c> — silently replaces the other constraint. And an index
    /// name, including the index behind every primary key, unique, exclusion and temporal
    /// constraint, is unique among all relations of its schema, not its table — tables, views and
    /// sequences included: a clash fails with 42P07. Each kind used to be checked against its own kind on the same table at most, so a
    /// temporal constraint named like an exclusion constraint, or like an index anywhere in the
    /// schema, scaffolded cleanly. Collisions between two of EF Core's own objects are not this
    /// package's to police. Target model only: a snapshot that already holds a collision must stay
    /// diffable, or the model could never be fixed.
    /// <para>
    /// A complex index clashing with another complex index or a native one never gets this far: the
    /// core rejects it first under <see cref="IndexNameScope"/>, with a message naming both
    /// declarations. Complex indexes stay in the list for the pairs only this check sees — against a
    /// key or a constraint's index, and a table left in the default schema against one that names
    /// <c>public</c> explicitly — so each collision is reported once, by whichever check sees it first.
    /// </para>
    /// </remarks>
    private void ValidateNamesAcrossKinds(IRelationalModel? target)
    {
        if (target is null)
            return;

        var names = TargetNames(target);

        ThrowOnFirstCollision(
            names.Where(n => n.IsConstraint).GroupBy(n => (n.Schema, n.Table, n.Name)),
            other => "PostgreSQL keeps constraint names unique per table, so the migration would fail when applied (42710)"
                   + (other.Any(o => o.Kind == "exclusion constraint")
                          ? " — or, since every exclusion constraint is added after DROP CONSTRAINT IF EXISTS, silently replace the other constraint."
                          : "."));

        ThrowOnFirstCollision(
            names.Where(n => n.InRelationNamespace).GroupBy(n => (n.Schema, "", n.Name)),
            other => "PostgreSQL keeps index names — including the index behind every primary key, unique, exclusion and "
                   + $"temporal constraint — unique per schema ('{other[0].Schema}'), a namespace they share with its tables, "
                   + "views and sequences, so the migration would fail when applied (42P07).");

        static void ThrowOnFirstCollision(
            IEnumerable<IGrouping<(string Schema, string Table, string Name), NamedObject>> groups,
            Func<IReadOnlyList<NamedObject>, string>                                        rule)
        {
            foreach (var group in groups)
            {
                var members = group.ToList();
                var ours    = members.FirstOrDefault(m => m.Ours);
                if (ours is null || members.Count < 2)
                    continue;

                var other = members.First(m => !ReferenceEquals(m, ours));

                var otherDescription = other.Description
                                    ?? (other.IsRelation
                                           ? $"the {other.Kind} '{other.Name}'"
                                           : $"the {other.Kind} on table '{other.Table}'");

                throw new InvalidOperationException(
                    $"The {ours.Kind} '{ours.Name}' on table '{ours.Table}' has the same name as {otherDescription}. "
                  + $"{rule([ours, other])} Give one of them a different name.");
            }
        }
    }

    /// <summary>
    /// Every name the target model gives in PostgreSQL's two namespaces: this package's
    /// declarations, EF Core's indexes, keys, foreign keys and check constraints, and the tables,
    /// views and sequences of each schema.
    /// </summary>
    private List<NamedObject> TargetNames(IRelationalModel target)
    {
        // A list, not a set: two declarations with one name on one table produce identical entries,
        // and a set would merge exactly the pair the cross-kind check exists to find. Every source
        // below is already free of duplicates.
        var defaultSchema = DefaultSchema(target);
        var names         = new List<NamedObject>();

        void Add(string name, string table, string? schema, string kind, bool ours, bool constraint, bool indexBacked)
            => names.Add(new NamedObject(name, table, schema ?? defaultSchema, kind, ours, constraint, indexBacked));

        // The relations themselves: an index named like a table, view or sequence of its schema
        // fails with 42P07 just as one named like another index does.
        void AddRelation(string name, string? schema, string kind, string? description = null)
            => names.Add(new NamedObject(name, name, schema ?? defaultSchema, kind, Ours: false, IsConstraint: false, InRelationNamespace: true)
                         {
                             IsRelation  = true,
                             Description = description
                         });

        foreach (var index in ExtractAllIndexDescriptors(target))
            Add(index.IndexName, index.TableName, index.Schema, "complex index", ours: true, constraint: false, indexBacked: true);

        var temporal = BuildDescriptors(target, _typeMappingSource);
        foreach (var constraint in temporal)
            Add(constraint.Name, constraint.Table, constraint.Schema, "temporal constraint", ours: true, constraint: true, indexBacked: true);

        foreach (var foreignKey in BuildForeignKeyDescriptors(target, _typeMappingSource, temporal))
            Add(foreignKey.Name, foreignKey.DependentTable, foreignKey.DependentSchema, "temporal foreign key", ours: true, constraint: true, indexBacked: false);

        foreach (var exclusion in BuildExclusionDescriptors(target))
            Add(exclusion.Name, exclusion.Table, exclusion.Schema, "exclusion constraint", ours: true, constraint: true, indexBacked: true);

        foreach (var table in target.Tables)
        {
            foreach (var index in table.Indexes)
                Add(index.Name, table.Name, table.Schema, "index", ours: false, constraint: false, indexBacked: true);

            foreach (var key in table.UniqueConstraints)
                Add(key.Name, table.Name, table.Schema, key.GetIsPrimaryKey() ? "primary key" : "unique constraint", ours: false, constraint: true, indexBacked: true);

            foreach (var foreignKey in table.ForeignKeyConstraints)
                Add(foreignKey.Name, table.Name, table.Schema, "foreign key", ours: false, constraint: true, indexBacked: false);

            // A check constraint without a name gets one from PostgreSQL, which never collides.
            foreach (var check in table.CheckConstraints.Where(c => c.Name is not null))
                Add(check.Name!, table.Name, table.Schema, "check constraint", ours: false, constraint: true, indexBacked: false);

            AddRelation(table.Name, table.Schema, "table");

            // The sequence PostgreSQL creates for an identity or serial column is in no model, but it
            // takes its name in the schema all the same.
            foreach (var column in table.Columns.Where(c => HasImplicitSequence(table, c)))
            {
                var sequence = ImplicitSequenceName(table.Name, column.Name);
                AddRelation(sequence, table.Schema, "sequence",
                            $"the sequence '{sequence}', which PostgreSQL creates for the identity or serial column '{column.Name}' of table '{table.Name}'");
            }
        }

        foreach (var view in target.Views)
            AddRelation(view.Name, view.Schema, "view");

        foreach (var sequence in target.Model.GetSequences())
            AddRelation(sequence.Name, sequence.Schema, "sequence");

        return names;
    }

    private static string DefaultSchema(IRelationalModel target) => target.Model.GetDefaultSchema() ?? "public";

    /// <summary>
    /// Whether the target model gives a name to an object a rename's old name would clash with: a
    /// constraint of the same table, or — for an object that owns an index — a relation of the same
    /// schema. Such a rename cannot run after the base operations, which may already have created
    /// that object under the name (#143), so it becomes a drop before them and an add after them.
    /// </summary>
    /// <remarks>
    /// Over-approximates as the core's <see cref="IsIndexNameInUse"/> does: an object this package
    /// adds after the renames costs a rebuild the rename did not need, never a failed apply.
    /// </remarks>
    private static bool IsNameInUse(List<NamedObject> names, string defaultSchema, string name, string table, string? schema, bool indexBacked)
    {
        schema ??= defaultSchema;
        return names.Any(n => n.Name == name
                           && n.Schema == schema
                           && (n.IsConstraint && n.Table == table || indexBacked && n.InRelationNamespace));
    }

    // The core checks complex indexes, native indexes and keys under its own scope; PostgreSQL's
    // relation namespace also holds the tables, views and sequences of the schema, and the indexes
    // behind its exclusion and temporal constraints.
    internal override bool IsIndexNameInUse(
        IRelationalModel target, IReadOnlyCollection<IndexDescriptor> targetIndexes, IndexDescriptor renamed, string name)
        => base.IsIndexNameInUse(target, targetIndexes, renamed, name)
        || IsNameInUse(TargetNames(target), DefaultSchema(target), name, renamed.TableName, renamed.Schema, indexBacked: true);

    // InRelationNamespace: an index, a constraint backed by one, or a table, view or sequence —
    // everything PostgreSQL names in pg_class, whose names are unique per schema.
    private sealed record NamedObject(
        string Name,
        string Table,
        string Schema,
        string Kind,
        bool   Ours,
        bool   IsConstraint,
        bool   InRelationNamespace)
    {
        public bool IsRelation { get; init; }

        // How the collision message names this object, where its kind and name are not enough.
        public string? Description { get; init; }
    }

    // An identity column — Npgsql's default for an int or long key — and a serial column each get a
    // sequence from PostgreSQL. A serial store type declared by hand is one too.
    private static bool HasImplicitSequence(ITable table, IColumn column)
    {
        if (column.StoreType.ToLowerInvariant() is "serial" or "serial2" or "serial4" or "serial8" or "smallserial" or "bigserial")
            return true;

        var storeObject = StoreObjectIdentifier.Table(table.Name, table.Schema);
        return column.PropertyMappings.Any(m => m.Property.GetValueGenerationStrategy(storeObject)
                                                    is NpgsqlValueGenerationStrategy.IdentityByDefaultColumn
                                                    or NpgsqlValueGenerationStrategy.IdentityAlwaysColumn
                                                    or NpgsqlValueGenerationStrategy.SerialColumn);
    }

    /// <summary>
    /// The name PostgreSQL gives the sequence of an identity or serial column, as its
    /// <c>ChooseRelationName</c> builds it: <c>{table}_{column}_seq</c>, cut to 63 bytes by
    /// shortening the longer of table and column first, one byte at a time, then back to a whole
    /// character.
    /// </summary>
    /// <remarks>
    /// When that name is already taken as the table is created, PostgreSQL appends a number instead
    /// of failing (<c>rows_Id_seq1</c>), which no model can predict. So an index created in an earlier
    /// migration than the table it is named after would apply, and is reported all the same; the
    /// rename costs less than a check that guesses the order in which the database was built.
    /// </remarks>
    internal static string ImplicitSequenceName(string table, string column)
    {
        const int maxBytes  = 63; // NAMEDATALEN - 1
        const int available = maxBytes - 1 - 4; // "_" and "_seq"

        // PostgreSQL has already cut each identifier to 63 bytes when it created the table.
        table  = Clip(table, maxBytes);
        column = Clip(column, maxBytes);

        var tableBytes  = Encoding.UTF8.GetByteCount(table);
        var columnBytes = Encoding.UTF8.GetByteCount(column);
        while (tableBytes + columnBytes > available)
        {
            if (tableBytes > columnBytes)
                tableBytes--;
            else
                columnBytes--;
        }

        return $"{Clip(table, tableBytes)}_{Clip(column, columnBytes)}_seq";

        // pg_mbcliplen: the longest run of whole characters within the byte budget.
        static string Clip(string value, int bytes)
        {
            var clipped = new StringBuilder();
            foreach (var rune in value.EnumerateRunes())
            {
                bytes -= rune.Utf8SequenceLength;
                if (bytes < 0)
                    break;
                clipped.Append(rune.ToString());
            }

            return clipped.ToString();
        }
    }

    // Diffs the temporal UNIQUE constraints and temporal FOREIGN KEY constraints declared on entity
    // types. Drops are emitted as EF's own Drop* operations (the stock generator renders those
    // correctly) and placed before the base EF operations; adds are fully rendered DDL emitted as
    // SqlOperations after them, with UNIQUE constraints before FOREIGN KEYs.
    private IReadOnlyList<MigrationOperation> ApplyTemporalConstraints(
        IReadOnlyList<MigrationOperation> operations,
        IRelationalModel?                 source,
        IRelationalModel?                 target,
        IRelationalTypeMappingSource      typeMappingSource,
        out bool                          needsBtreeGist
    )
    {
        needsBtreeGist = false;

        var sourceConstraints = ResolveSource(operations, () => BuildDescriptors(source, typeMappingSource));
        var targetConstraints = BuildDescriptors(target, typeMappingSource);
        var sourceForeignKeys = ResolveSource(operations, () => BuildForeignKeyDescriptors(source, typeMappingSource, sourceConstraints));
        var targetForeignKeys = BuildForeignKeyDescriptors(target, typeMappingSource, targetConstraints);

        // Target only — the source is history.
        if (target is not null)
        {
            foreach (var constraint in targetConstraints)
                ThrowIfIdentifierTooLong(target.Model, constraint.Name, "temporal constraint", constraint.Table, "name");

            foreach (var foreignKey in targetForeignKeys)
                ThrowIfIdentifierTooLong(target.Model, foreignKey.Name, "temporal foreign key", foreignKey.DependentTable, "name");
        }

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
        var principalDropped      = new HashSet<TemporalForeignKeyDescriptor>();
        foreach (var src in sourceForeignKeys)
        {
            var normalized = src;
            if (renamedTables.TryGetValue((src.DependentTable, src.DependentSchema), out var dependent))
                normalized = normalized with { DependentTable = dependent.Name, DependentSchema = dependent.Schema };
            if (renamedTables.TryGetValue((src.PrincipalTable, src.PrincipalSchema), out var principal))
                normalized = normalized with { PrincipalTable = principal.Name, PrincipalSchema = principal.Schema };
            if (normalizedForeignKeys.TryAdd(normalized, (src.DependentTable, src.DependentSchema))
             && droppedTables.Contains((src.PrincipalTable, src.PrincipalSchema)))
                principalDropped.Add(normalized);
        }

        var pendingConstraintDrops = normalizedConstraints.Keys
            .Where(src => !targetConstraints.Contains(src)
                       && !droppedTables.Contains(normalizedConstraints[src]))
            .ToList();

        var pendingConstraintAdds = targetConstraints.Where(tgt => !normalizedConstraints.ContainsKey(tgt)).ToList();

        // A column the base operations drop and add again takes the constraint, or the foreign key,
        // with it (#184); a principal column that goes fails the drop instead (2BP01) while a
        // foreign key references the constraint. A key column whose type changes is in the way of
        // the foreign key on either side: PostgreSQL re-checks the key after each ALTER COLUMN,
        // and a GiST key has no cross-type operator, so a dependent altered before its principal
        // fails (42804, #188). The constraint's own index follows its column in place.
        var recreated = RecreatedColumns(operations);
        var retyped   = RetypedColumns(operations);
        bool ConstraintInTheWay(TemporalDescriptor constraint)
            => target is not null
            && ReadsAnyOf(target, recreated, constraint.Table, constraint.Schema, constraint.KeyColumns.Append(constraint.PeriodColumn));
        bool ForeignKeyInTheWay(TemporalForeignKeyDescriptor foreignKey)
        {
            if (target is null)
                return false;

            var dependent = foreignKey.DependentColumns.Append(foreignKey.DependentPeriodColumn).ToList();
            var principal = foreignKey.PrincipalColumns.Append(foreignKey.PrincipalPeriodColumn).ToList();

            return ReadsAnyOf(target, recreated, foreignKey.DependentTable, foreignKey.DependentSchema, dependent)
                || ReadsAnyOf(target, retyped,   foreignKey.DependentTable, foreignKey.DependentSchema, dependent)
                || ReadsAnyOf(target, retyped,   foreignKey.PrincipalTable, foreignKey.PrincipalSchema, principal);
        }

        // Drop/add pairs identical except for the name — typically default-named constraints on a
        // renamed table — become RENAME CONSTRAINT. Dependent foreign keys survive a rename, so
        // they don't churn (DependsOnChangedTemporalConstraint compares names insensitively). A
        // rename runs after the base operations, so one whose old name the target model gives to
        // something else stays a drop and an add (#143) — and its foreign keys, which the drop
        // would fail under (2BP01), are rebuilt around it.
        List<NamedObject>? names   = null;
        var                renames = new List<MigrationOperation>();
        var                rebuilt = new List<TemporalDescriptor>();

        for (var i = pendingConstraintDrops.Count - 1; i >= 0; i--)
        {
            var dropped = pendingConstraintDrops[i];
            var renamed = pendingConstraintAdds.FirstOrDefault(a => a.Name != dropped.Name
                                                                 && (dropped with { Name = a.Name }).Equals(a));
            if (renamed is null)
                continue;

            if (target is not null
             && (ConstraintInTheWay(renamed)
              || IsNameInUse(names ??= TargetNames(target), DefaultSchema(target), dropped.Name, renamed.Table, renamed.Schema, indexBacked: true)))
            {
                rebuilt.Add(renamed);
                continue;
            }

            renames.Add(new SqlOperation
                        {
                            Sql = $"ALTER TABLE {QuoteQualified(renamed.Table, renamed.Schema)} " +
                                  $"RENAME CONSTRAINT {Quote(dropped.Name)} TO {Quote(renamed.Name)};"
                        });
            pendingConstraintDrops.RemoveAt(i);
            pendingConstraintAdds.Remove(renamed);
        }

        // An unchanged constraint in the way is dropped before the base operations and added after,
        // and its foreign keys with it.
        foreach (var (kept, origin) in normalizedConstraints)
        {
            if (targetConstraints.TryGetValue(kept, out var current) && !droppedTables.Contains(origin) && ConstraintInTheWay(current))
            {
                pendingConstraintDrops.Add(kept);
                pendingConstraintAdds.Add(current);
                rebuilt.Add(current);
            }
        }

        bool DependsOnRebuiltConstraint(TemporalForeignKeyDescriptor foreignKey)
            => rebuilt.Any(c => IsPrincipalConstraintFor(foreignKey, c));

        // A foreign key whose dependent table is dropped goes with it — unless something before the
        // base operations needs it gone first. Its principal table dropped too: EF orders the
        // DropTable operations without knowing about the temporal foreign key, so when the
        // principal's comes first it fails with 2BP01 (Down() of the migration that created both
        // tables has exactly this shape). Its principal constraint dropped, changed or rebuilt:
        // that drop runs before the base operations, while the key still references it (2BP01,
        // #187). A table drop does not need the key, so dropping it first is always safe.
        var pendingForeignKeyDrops = normalizedForeignKeys.Keys
            .Where(src => (!droppedTables.Contains(normalizedForeignKeys[src])
                        || principalDropped.Contains(src)
                        || DependsOnChangedTemporalConstraint(src, normalizedConstraints.Keys, targetConstraints)
                        || DependsOnRebuiltConstraint(src))
                       && (!targetForeignKeys.Contains(src)
                        || DependsOnChangedTemporalConstraint(src, normalizedConstraints.Keys, targetConstraints)
                        || DependsOnRebuiltConstraint(src)
                        || ForeignKeyInTheWay(src)))
            .ToList();

        var pendingForeignKeyAdds = targetForeignKeys
            .Where(tgt => !normalizedForeignKeys.ContainsKey(tgt)
                       || DependsOnChangedTemporalConstraint(tgt, normalizedConstraints.Keys, targetConstraints)
                       || DependsOnRebuiltConstraint(tgt)
                       || ForeignKeyInTheWay(tgt))
            .ToList();

        for (var i = pendingForeignKeyDrops.Count - 1; i >= 0; i--)
        {
            var dropped = pendingForeignKeyDrops[i];
            var renamed = pendingForeignKeyAdds.FirstOrDefault(a => a.Name != dropped.Name
                                                                 && (dropped with { Name = a.Name }).Equals(a));
            if (renamed is null)
                continue;

            if (target is not null
             && IsNameInUse(names ??= TargetNames(target), DefaultSchema(target), dropped.Name, renamed.DependentTable, renamed.DependentSchema, indexBacked: false))
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
    private IReadOnlyList<MigrationOperation> ApplyExclusionConstraints(
        IReadOnlyList<MigrationOperation> operations,
        IRelationalModel?                 source,
        IRelationalModel?                 target,
        out bool                          needsBtreeGist
    )
    {
        needsBtreeGist = false;

        var sourceConstraints = ResolveSource(operations, () => BuildExclusionDescriptors(source));
        var targetConstraints = BuildExclusionDescriptors(target);

        // Target only — a snapshot that already contains a collision, or a filter refused since it
        // was scaffolded, must stay diffable.
        ValidateUniqueExclusionNames(targetConstraints);

        foreach (var constraint in targetConstraints)
        {
            if (constraint.FilterRefusal is { } refusal)
                throw new InvalidOperationException(refusal);
        }

        if (target is not null)
        {
            foreach (var constraint in targetConstraints)
                ThrowIfIdentifierTooLong(target.Model, constraint.Name, "exclusion constraint", constraint.Table, "name");
        }

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

        // A column the base operations drop and add again takes the constraint with it (#184).
        var recreated = RecreatedColumns(operations);
        bool InTheWay(ExclusionDescriptor constraint)
            => target is not null
            && ReadsAnyOf(target, recreated, constraint.Table, constraint.Schema,
                                    constraint.Parts.Where(p => !p.IsExpression).Select(p => p.Value),
                                    string.Join(" ", constraint.Parts.Where(p => p.IsExpression).Select(p => p.Value).Append(constraint.Filter ?? "")));

        // A drop/add pair identical except for the name — typically a default-named constraint on a
        // renamed table — becomes RENAME CONSTRAINT: a catalog update instead of a gist rebuild.
        // Placed after the base operations, which may themselves rename the table — and may create
        // whatever the target model names like the constraint used to be named, so such a rename
        // stays a drop and an add (#143).
        List<NamedObject>? names   = null;
        var                renames = new List<MigrationOperation>();
        for (var i = pendingDrops.Count - 1; i >= 0; i--)
        {
            var dropped = pendingDrops[i];
            var renamed = pendingAdds.FirstOrDefault(a => a.Name != dropped.Name
                                                       && (dropped with { Name = a.Name }).Equals(a));
            if (renamed is null)
                continue;

            if (target is not null
             && (InTheWay(renamed)
              || IsNameInUse(names ??= TargetNames(target), DefaultSchema(target), dropped.Name, renamed.Table, renamed.Schema, indexBacked: true)))
                continue;

            renames.Add(new SqlOperation
                        {
                            Sql = $"ALTER TABLE {QuoteQualified(renamed.Table, renamed.Schema)} " +
                                  $"RENAME CONSTRAINT {Quote(dropped.Name)} TO {Quote(renamed.Name)};"
                        });
            pendingDrops.RemoveAt(i);
            pendingAdds.Remove(renamed);
        }

        // An unchanged constraint in the way is dropped before the base operations and added after.
        foreach (var (kept, origin) in normalizedSource)
        {
            if (targetConstraints.TryGetValue(kept, out var current) && !droppedTables.Contains(origin) && InTheWay(current))
            {
                pendingDrops.Add(kept);
                pendingAdds.Add(current);
            }
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

            if (NeedsBtreeGist(tgt))
                needsBtreeGist = true;
        }

        if (drops.Count == 0 && renames.Count == 0 && adds.Count == 0)
            return operations;

        return [.. drops, .. operations, .. renames, .. adds];
    }

    // Store types with a default GiST operator class of their own: the geometric and full-text
    // search types in core, and the types of the extensions that define them. Ranges and
    // multiranges are matched by suffix.
    private static readonly HashSet<string> NativeGistTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "box", "circle", "point", "polygon", "tsvector", "tsquery",
        "geometry", "geography", "box2d", "box3d", "ltree", "cube", "hstore", "seg"
    };

    private static readonly HashSet<string> BtreeOperators = ["=", "<>", "!=", "<", "<=", ">", ">="];

    // Under gist, every element needs a default GiST operator class for its type, whatever its
    // operator: a scalar column has one only through btree_gist, and PostgreSQL rejects the
    // constraint without it ("data type integer has no default operator class for access method
    // gist"). A column is checked by its store type; an expression's type is unknown here, so it
    // counts when it is compared with a B-tree operator. The method is compared case-insensitively,
    // as PostgreSQL reads it: UseMethod("GiST") is gist.
    private static bool NeedsBtreeGist(ExclusionDescriptor constraint)
        => string.Equals(constraint.Method, "gist", StringComparison.OrdinalIgnoreCase)
        && constraint.Parts.Any(p => p.IsExpression
                                         ? BtreeOperators.Contains(p.Operator)
                                         : !HasNativeGistOperatorClass(p.StoreType));

    private static bool HasNativeGistOperatorClass(string? storeType)
    {
        if (storeType is null)
            return false;

        var parenthesis = storeType.IndexOf('(');
        var baseType    = (parenthesis < 0 ? storeType : storeType[..parenthesis]).Trim();

        return !baseType.EndsWith("[]", StringComparison.Ordinal)
            && (baseType.EndsWith("range", StringComparison.OrdinalIgnoreCase) || NativeGistTypes.Contains(baseType));
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

    // Tables the base operations rename, keyed by old identity. NewSchema is the target's schema,
    // null for the default one, as EF Core writes it (#185).
    private static Dictionary<(string Name, string? Schema), (string Name, string? Schema)> BuildRenamedTables(
        IReadOnlyList<MigrationOperation> operations)
    {
        var renamed = new Dictionary<(string Name, string? Schema), (string Name, string? Schema)>();
        foreach (var rename in operations.OfType<RenameTableOperation>())
            renamed[(rename.Name, rename.Schema)] = (rename.NewName ?? rename.Name, rename.NewSchema);
        return renamed;
    }

    // The name this package gives a TPC copy (CustomMigrationsModelDiffer.CopyName) over the
    // identifier limit, shortened as the core shortens a complex index's (ShortenIdentifier). A
    // constraint's name is taken among the constraints of its table, and, when an index stands
    // behind it (indexBacked: every kind but the temporal foreign key), among the relations of its
    // schema too. Sorted, so both sides of a diff assign alike; copies sharing a full
    // name share the shortened one, so the name checks still report two declarations that resolve
    // alike. Other kinds start with other prefixes, and ValidateNamesAcrossKinds still sees the
    // shortened names.
    private List<T> ShortenConstraintCopyNames<T>(
        IRelationalModel                                     model,
        HashSet<T>                                           copies,
        IEnumerable<T>                                       others,
        Func<T, (string Table, string? Schema, string Name)> identity,
        Func<T, string, T>                                   rename,
        bool                                                 indexBacked)
    {
        var shortened = new List<T>(copies.Count);
        if (copies.Count == 0)
            return shortened;

        var inSchema = new HashSet<(string? Schema, string Name)>();
        var onTable  = new HashSet<(string? Schema, string Table, string Name)>();

        void Take(string table, string? schema, string name, bool indexBacked)
        {
            onTable.Add((schema, table, name));
            if (indexBacked)
                inSchema.Add((schema, name));
        }

        foreach (var table in model.Tables)
        {
            foreach (var index in table.Indexes)
                inSchema.Add((table.Schema, index.Name));

            foreach (var key in table.UniqueConstraints)
                Take(table.Name, table.Schema, key.Name, indexBacked: true);

            foreach (var foreignKey in table.ForeignKeyConstraints)
                Take(table.Name, table.Schema, foreignKey.Name, indexBacked: false);

            foreach (var check in table.CheckConstraints.Where(c => c.Name is not null))
                Take(table.Name, table.Schema, check.Name!, indexBacked: false);
        }

        foreach (var other in others)
        {
            var (table, schema, name) = identity(other);
            Take(table, schema, name, indexBacked);
        }

        var limit    = model.Model.GetMaxIdentifierLength();
        var assigned = new Dictionary<(string? Schema, string Table, string Name), string>();

        foreach (var copy in copies.OrderBy(c => identity(c).Schema, StringComparer.Ordinal)
                                   .ThenBy(c => identity(c).Table, StringComparer.Ordinal)
                                   .ThenBy(c => identity(c).Name, StringComparer.Ordinal))
        {
            var (table, schema, full) = identity(copy);
            if (!assigned.TryGetValue((schema, table, full), out var name))
            {
                name = ShortenIdentifier(full, limit, candidate => indexBacked && inSchema.Contains((schema, candidate)) || onTable.Contains((schema, table, candidate)));
                assigned[(schema, table, full)] = name;
                Take(table, schema, name, indexBacked);
            }

            shortened.Add(rename(copy, name));
        }

        return shortened;
    }

    private HashSet<ExclusionDescriptor> BuildExclusionDescriptors(IRelationalModel? model)
    {
        var set = new HashSet<ExclusionDescriptor>();
        if (model is null) return set;

        var longCopies = new HashSet<ExclusionDescriptor>();

        // Declarations come from the same reader an application uses (NpgsqlExclusionModelExtensions).
        foreach (var entityType in model.Model.GetEntityTypes())
        {
            var declarations = entityType.GetDeclaredExclusionConstraints();
            if (declarations.Count == 0)
                continue;

            var tables = DeclarationTables(entityType);
            if (tables.Count == 0)
            {
                ThrowIfDeclaredOnUnmappedType(entityType, "exclusion constraints");
                continue;
            }

            foreach (var def in declarations)
            {
                var paths = def.Parts.Select(p => p.PropertyPath).OfType<string>().Concat(PlaceholderPaths(def.Filter)).ToList();

                foreach (var (tableType, storeObject) in tables)
                {
                    ThrowIfMissingFromDerivedTable(model, entityType, tableType, storeObject, paths, "an exclusion constraint");

                    var descriptor        = ResolveExclusion(tableType, def, storeObject);
                    var (name, generated) = CopyName(entityType, tables, tableType, storeObject, def.Name, descriptor.Name);
                    (generated && IsTooLong(model.Model, name) ? longCopies : set).Add(descriptor with { Name = name });
                }
            }
        }

        foreach (var copy in ShortenConstraintCopyNames(model, longCopies, set, c => (c.Table, c.Schema, c.Name), (c, name) => c with { Name = name }, indexBacked: true))
            set.Add(copy);

        return set;
    }

    private ExclusionDescriptor ResolveExclusion(IEntityType entityType, ExclusionConstraintDeclaration def, StoreObjectIdentifier storeObject)
    {
        var (table, schema) = (storeObject.Name, storeObject.Schema);

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

            parts.Add(new ResolvedExclusionPart(false, RenderedColumn(column, storeObject), part.Operator)
                      {
                          StoreType = property.GetColumnType(storeObject),
                          NameToken = column
                      });
        }

        var name = def.Name ?? $"EX_{table}_{string.Join("_", parts.Select(ExclusionPartToken))}";

        return new ExclusionDescriptor(
            table,
            schema,
            name,
            parts,
            def.Method,
            // Pinned to the original JSON rendering: the constraint enforces the same rows
            // either way, so the 5.4.0 rendering change would only drop and re-add it.
            // Non-string members are the exception (ResolveUnmappedPart).
            ResolveExclusionFilter(entityType, def.Filter, storeObject, out var filterRefusal),
            def.Deferrable,
            def.InitiallyDeferred)
        {
            FilterRefusal = filterRefusal
        };
    }

    private string? ResolveExclusionFilter(IEntityType entityType, string? filter, StoreObjectIdentifier storeObject, out string? refusal)
    {
        _resolvingExclusionFilter = true;
        try
        {
            return ResolveFilter(entityType, filter, storeObject, renderingVersion: 1, out refusal);
        }
        finally
        {
            _resolvingExclusionFilter = false;
        }
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
            return part.NameToken ?? part.Value;

        var token = new string([.. part.Value.Where(char.IsLetterOrDigit)]);
        return token.Length > 0 ? token : "expr";
    }

    private static string Quote(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string QuoteQualified(string table, string? schema)
        => schema is null ? Quote(table) : $"{Quote(schema)}.{Quote(table)}";

    private HashSet<TemporalDescriptor> BuildDescriptors(
        IRelationalModel?            model,
        IRelationalTypeMappingSource typeMappingSource
    )
    {
        var set = new HashSet<TemporalDescriptor>();
        if (model is null) return set;

        var longCopies = new HashSet<TemporalDescriptor>();

        foreach (var entityType in model.Model.GetEntityTypes())
        {
            if (entityType.FindAnnotation(NpgsqlTemporalAnnotations.Constraints)?.Value is not string json
             || string.IsNullOrEmpty(json))
                continue;

            var tables = DeclarationTables(entityType);
            if (tables.Count == 0)
            {
                ThrowIfDeclaredOnUnmappedType(entityType, "temporal constraints");
                continue;
            }

            foreach (var def in TemporalConstraintSerializer.Deserialize(json))
            {
                foreach (var (tableType, storeObject) in tables)
                {
                    ThrowIfMissingFromDerivedTable(model, entityType, tableType, storeObject, [.. def.KeyProperties, def.PeriodProperty], "a temporal constraint");

                    var descriptor        = ResolveTemporal(tableType, def, storeObject, typeMappingSource);
                    var (name, generated) = CopyName(entityType, tables, tableType, storeObject, def.Name, descriptor.Name);
                    (generated && IsTooLong(model.Model, name) ? longCopies : set).Add(descriptor with { Name = name });
                }
            }
        }

        foreach (var copy in ShortenConstraintCopyNames(model, longCopies, set, c => (c.Table, c.Schema, c.Name), (c, name) => c with { Name = name }, indexBacked: true))
            set.Add(copy);

        return set;
    }

    private TemporalDescriptor ResolveTemporal(
        IEntityType                  entityType,
        TemporalConstraintDefinition def,
        StoreObjectIdentifier        storeObject,
        IRelationalTypeMappingSource typeMappingSource)
    {
        var (table, schema) = (storeObject.Name, storeObject.Schema);

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

        // Named from the columns as the declaring model has them, compared as the base operations
        // leave them (RenderedColumn).
        return new TemporalDescriptor(
            table,
            schema,
            name,
            keyColumns.ConvertAll(column => RenderedColumn(column, storeObject)),
            RenderedColumn(periodColumn, storeObject));
    }

    private HashSet<TemporalForeignKeyDescriptor> BuildForeignKeyDescriptors(
        IRelationalModel?            model,
        IRelationalTypeMappingSource typeMappingSource,
        HashSet<TemporalDescriptor>  temporalConstraints
    )
    {
        var set = new HashSet<TemporalForeignKeyDescriptor>();
        if (model is null) return set;

        var longCopies = new HashSet<TemporalForeignKeyDescriptor>();

        foreach (var declaringType in model.Model.GetEntityTypes())
        {
            if (declaringType.FindAnnotation(NpgsqlTemporalAnnotations.ForeignKeys)?.Value is not string json
             || string.IsNullOrEmpty(json))
                continue;

            var dependentTables = DeclarationTables(declaringType);
            if (dependentTables.Count == 0)
            {
                ThrowIfDeclaredOnUnmappedType(declaringType, "temporal foreign keys");
                continue;
            }

            foreach (var def in TemporalForeignKeySerializer.Deserialize(json))
            {
                var principalEntityType = ResolveEntityType(model.Model, def.PrincipalEntityType)
                                       ?? throw new InvalidOperationException(
                                              $"Could not resolve temporal foreign key principal entity '{def.PrincipalEntityType}' from dependent entity '{declaringType.Name}'.");

                var (principalType, principalStoreObject) = PrincipalTable(declaringType, principalEntityType);
                ThrowIfMissingFromDerivedTable(model, principalEntityType, principalType, principalStoreObject,
                                               [.. def.PrincipalKeyProperties, def.PrincipalPeriodProperty], "the principal key of a temporal foreign key");

                // Every table the dependent's rows live in references the principal.
                foreach (var (dependentType, dependentStoreObject) in dependentTables)
                {
                    ThrowIfMissingFromDerivedTable(model, declaringType, dependentType, dependentStoreObject,
                                                   [.. def.DependentKeyProperties, def.DependentPeriodProperty], "a temporal foreign key");

                    var foreignKey = ResolveForeignKey(def, dependentType, dependentStoreObject, principalType, principalStoreObject, typeMappingSource, temporalConstraints);

                    // An explicit name stays on every table: a foreign key owns no index, and its
                    // name is unique among the constraints of its table only, as EF Core repeats its own.
                    var generated = def.Name is null && dependentTables.Count > 1;
                    (generated && IsTooLong(model.Model, foreignKey.Name) ? longCopies : set).Add(foreignKey);
                }
            }
        }

        foreach (var copy in ShortenConstraintCopyNames(model, longCopies, set, c => (c.DependentTable, c.DependentSchema, c.Name), (c, name) => c with { Name = name }, indexBacked: false))
            set.Add(copy);

        return set;
    }

    /// <summary>
    /// The one table a temporal foreign key to <paramref name="principal"/> references.
    /// </summary>
    /// <remarks>
    /// A TPC type with derived types keeps its rows in several tables, and a foreign key references
    /// one: pointed at the declaring type's table, it refused every row of a derived type. EF Core
    /// creates no foreign key at all for a relationship with such a principal. An abstract principal
    /// with a single concrete table has all its rows there, so that table is the one.
    /// </remarks>
    private static (IEntityType EntityType, StoreObjectIdentifier Table) PrincipalTable(IEntityType dependent, IEntityType principal)
    {
        var tables = DeclarationTables(principal);

        return tables.Count switch
               {
                   0 => throw new InvalidOperationException(
                            $"Temporal foreign key principal entity '{principal.Name}' is not mapped to a table."),
                   1 => tables[0],
                   _ => throw new InvalidOperationException(
                            $"'{dependent.DisplayName()}' declares a temporal foreign key to '{principal.DisplayName()}', a TPC type whose rows "
                          + $"are spread over the tables {string.Join(", ", tables.Select(t => $"'{t.Table.Name}'"))}. A foreign key references "
                          + "one table, so it would refuse every row kept in another; EF Core creates no foreign key for such a relationship "
                          + "either. Reference an entity type with no derived types.")
               };
    }

    private TemporalForeignKeyDescriptor ResolveForeignKey(
        TemporalForeignKeyDefinition def,
        IEntityType                  dependentEntityType,
        StoreObjectIdentifier        dependentStoreObject,
        IEntityType                  principalEntityType,
        StoreObjectIdentifier        principalStoreObject,
        IRelationalTypeMappingSource typeMappingSource,
        HashSet<TemporalDescriptor>  temporalConstraints)
    {
        var (dependentTable, dependentSchema) = (dependentStoreObject.Name, dependentStoreObject.Schema);
        var (principalTable, principalSchema) = (principalStoreObject.Name, principalStoreObject.Schema);

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

        // The principal's constraints are compared as the base operations leave their columns.
        var renderedPrincipalColumns = principalColumns.ConvertAll(column => RenderedColumn(column, principalStoreObject));
        var renderedPrincipalPeriod  = RenderedColumn(principalPeriodColumn, principalStoreObject);

        if (!HasMatchingPrincipalTemporalConstraint(temporalConstraints, principalTable, principalSchema, renderedPrincipalColumns, renderedPrincipalPeriod))
        {
            throw new InvalidOperationException(
                $"Temporal foreign key '{def.Name ?? DefaultForeignKeyName(dependentTable, principalTable, dependentColumns, dependentPeriodColumn)}' " +
                $"references '{principalTable}' ({string.Join(", ", principalColumns)}, PERIOD {principalPeriodColumn}), " +
                "but no matching HasTemporalConstraint was found on the principal entity. " +
                "PostgreSQL requires the referenced table to have a UNIQUE or PRIMARY KEY constraint with WITHOUT OVERLAPS."
            );
        }

        var name = def.Name ?? DefaultForeignKeyName(dependentTable, principalTable, dependentColumns, dependentPeriodColumn);

        return new TemporalForeignKeyDescriptor(
            dependentTable,
            dependentSchema,
            principalTable,
            principalSchema,
            name,
            dependentColumns.ConvertAll(column => RenderedColumn(column, dependentStoreObject)),
            RenderedColumn(dependentPeriodColumn, dependentStoreObject),
            renderedPrincipalColumns,
            renderedPrincipalPeriod);
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

    private sealed record ResolvedExclusionPart(bool IsExpression, string Value, string Operator)
    {
        // Decides btree_gist only. Kept out of equality: a column type change is the base
        // operations' business, and PostgreSQL rebuilds the constraint's index with the column.
        public string? StoreType { get; init; }

        // The column as the declaring model names it, for a default name: Value is renamed while
        // the source is resolved (RenderedColumn). Kept out of equality.
        public string? NameToken { get; init; }

        public bool Equals(ResolvedExclusionPart? other) =>
            other is not null
         && IsExpression == other.IsExpression
         && Value        == other.Value
         && Operator     == other.Operator;

        public override int GetHashCode() => HashCode.Combine(IsExpression, Value, Operator);
    }

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
        // Outside equality: why the filter cannot apply, raised for the target model only.
        public string? FilterRefusal { get; init; }

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
