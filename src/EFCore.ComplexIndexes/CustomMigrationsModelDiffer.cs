using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace EFCore.ComplexIndexes;

#pragma warning disable EF1001

/// <summary>
/// Replaces EF Core's <see cref="MigrationsModelDiffer"/> at design time so that indexes declared on
/// complex-type properties produce <c>CreateIndexOperation</c>/<c>DropIndexOperation</c>. EF Core can
/// model complex properties but does not diff indexes over their nested columns; this fills that gap.
/// </summary>
/// <remarks>
/// Registered through <see cref="CustomDesignTimeServices"/>, which the packaged <c>.targets</c> file
/// wires up automatically — consumers do not construct this type. Providers extend it by overriding
/// <c>IsForwardedIndexAnnotation</c>, <c>IndexNameScope</c>, <c>ValidateCreateIndexOperation</c>,
/// <c>ResolveUnmappedPart</c> and <c>ResolveTemplatePart</c>.
/// </remarks>
/// <param name="typeMappingSource">EF Core relational type mapping source.</param>
/// <param name="migrationsAnnotationProvider">EF Core migrations annotation provider.</param>
/// <param name="relationalAnnotationProvider">EF Core relational annotation provider.</param>
/// <param name="rowIdentityMapFactory">EF Core row identity map factory.</param>
/// <param name="commandBatchPreparerDependencies">EF Core command batch preparer dependencies.</param>
public class CustomMigrationsModelDiffer(
    IRelationalTypeMappingSource     typeMappingSource,
    IMigrationsAnnotationProvider    migrationsAnnotationProvider,
    IRelationalAnnotationProvider    relationalAnnotationProvider,
    IRowIdentityMapFactory           rowIdentityMapFactory,
    CommandBatchPreparerDependencies commandBatchPreparerDependencies
)
    : MigrationsModelDiffer(
        // The EF Core major check, for an application that registers this differ (or a
        // satellite's, which constructs this one) by hand rather than through an entry point.
        EfCoreVersionGuard.Guarded(typeMappingSource),
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

    // Read from the type mapping source because the differ has no other view of the provider; the
    // core serves SQLite without a satellite.
    private readonly bool _rebuildsTablesLikeSqlite =
        typeMappingSource.GetType().Namespace?.StartsWith("Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal) == true;

    // The satellite whose differ this provider's models need, when one exists.
    private readonly Satellite? _satellite =
        Satellites.FirstOrDefault(s => typeMappingSource.GetType().Namespace?.StartsWith(s.ProviderNamespace, StringComparison.Ordinal) == true);

    /// <summary>
    /// The index options each satellite forwards — its <c>IsForwardedIndexAnnotation</c> whitelist —
    /// and the registration that selects its differ. <c>SatelliteOptionCoverageTests</c> holds these
    /// lists to the satellites' builders, so an option added there cannot be missing here.
    /// </summary>
    internal static readonly Satellite[] Satellites =
    [
        new("Microsoft.EntityFrameworkCore.SqlServer", "SQL Server", "EFCore.ComplexIndexes.SqlServer", "UseSqlServerComplexIndexes()",
            ["SqlServer:Clustered", "SqlServer:Include", "SqlServer:Online", "SqlServer:FillFactor", "SqlServer:SortInTempDb", "SqlServer:DataCompression"],
            OptionPrefix: null),
        new("Npgsql.EntityFrameworkCore.PostgreSQL", "PostgreSQL", "EFCore.ComplexIndexes.PostgreSQL", "UseNpgsqlComplexIndexes()",
            ["Npgsql:IndexMethod", "Npgsql:IndexOperators", "Npgsql:IndexInclude", "Npgsql:CreatedConcurrently", "Npgsql:NullsDistinct", "Npgsql:IndexCollation"],
            OptionPrefix: "Npgsql:StorageParameter:"),
    ];

    internal sealed record Satellite(
        string                ProviderNamespace,
        string                Provider,
        string                Package,
        string                Registration,
        HashSet<string>       Options,
        string?               OptionPrefix)
    {
        public bool IsOption(string annotationName)
            => Options.Contains(annotationName)
            || OptionPrefix is not null && annotationName.StartsWith(OptionPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs EF Core's own diff, then adds the operations for complex-type indexes.
    /// </summary>
    /// <param name="source">The model migrated from — typically the snapshot.</param>
    /// <param name="target">The model migrated to — the current <c>OnModelCreating</c> result.</param>
    /// <returns>
    /// The base operations with this package's drops prepended and creates appended. The order is
    /// load-bearing: an index moving between a native <c>HasIndex</c> and a complex-index declaration
    /// yields a base create plus a same-named drop from here, and only drops-first avoids a collision
    /// at apply time.
    /// </returns>
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
        ValidateNoNativeIndexNameCollision(target, targetIndexes);
        ValidateIndexNameLengths(target, targetIndexes);
        ValidateQueryUsableLeadingParts(targetIndexes);
        ValidateNoSatelliteOptions(target);
        if (target is not null)
            ValidateTargetIndexes(target, targetIndexes);

        if (sourceIndexes.Count == 0 && targetIndexes.Count == 0)
            return operations;

        if (_rebuildsTablesLikeSqlite)
            ThrowIfSqliteRebuildDropsIndexes(operations, targetIndexes);

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

        // An unchanged index can still be in the way of a base operation, because EF does not know
        // it exists and so never moves it: SQL Server refuses to alter a column an index uses. Such
        // an index is dropped before the base operations and created after them, like a changed one.
        if (target is not null)
        {
            foreach (var (kept, origin) in normalizedSource)
            {
                if (!targetIndexes.TryGetValue(kept, out var current) || droppedTables.Contains(origin))
                    continue;

                var columns = ColumnsReadBy(target, current);
                if (!operations.Any(o => o is ITableMigrationOperation table
                                      && table.Table  == current.TableName
                                      && table.Schema == current.Schema
                                      && RequiresIndexRecreation(o, columns)))
                    continue;

                pendingDrops.Add(kept);
                pendingCreates.Add(current);
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
                op.AddAnnotation(ToOperationAnnotationName(key), value);

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
    /// Fails when SQLite would rebuild a table that carries a complex index.
    /// </summary>
    /// <remarks>
    /// SQLite runs an <c>ALTER COLUMN</c>, a <c>DROP COLUMN</c> and every key, foreign key, unique or
    /// check constraint change by rebuilding the table (<c>CREATE TABLE "ef_temp_…"</c>, copy, drop,
    /// rename), and EF Core re-creates only the indexes in its own model. Every complex index on the
    /// table vanished, unique ones included, the migration applied cleanly, and the snapshot still
    /// listed the index. Dropping it before and creating it after does not help here:
    /// <c>SqliteMigrationsSqlGenerator</c> emits every rebuild after all other operations of the
    /// migration, so any <c>CREATE INDEX</c> from this differ runs first and goes down with the old
    /// table. The list mirrors that generator's <c>RewriteOperations</c> (EF Core 10).
    /// </remarks>
    private static void ThrowIfSqliteRebuildDropsIndexes(
        IReadOnlyList<MigrationOperation> operations, HashSet<IndexDescriptor> targetIndexes)
    {
        var created = operations.OfType<CreateTableOperation>().Select(o => (o.Name, o.Schema)).ToHashSet();

        // Operations before a RenameTable name the old table; the rebuild runs under the new name.
        var renamed = new Dictionary<(string Name, string? Schema), (string Name, string? Schema)>();
        var rebuilt = new Dictionary<(string Name, string? Schema), MigrationOperation>();

        foreach (var operation in operations)
        {
            if (operation is RenameTableOperation rename)
            {
                var to = (rename.NewName ?? rename.Name, rename.NewSchema);
                foreach (var key in renamed.Where(r => r.Value == (rename.Name, rename.Schema)).Select(r => r.Key).ToList())
                    renamed[key] = to;
                renamed[(rename.Name, rename.Schema)] = to;
                if (rebuilt.Remove((rename.Name, rename.Schema), out var trigger))
                    rebuilt[to] = trigger;
                continue;
            }

            var rebuilds = operation switch
            {
                AddForeignKeyOperation foreignKey => !created.Contains((foreignKey.Table, foreignKey.Schema)),
                AddColumnOperation addColumn      => addColumn.Comment is not null,
                AlterColumnOperation or DropColumnOperation or AlterTableOperation
                    or AddPrimaryKeyOperation or DropPrimaryKeyOperation
                    or AddUniqueConstraintOperation or DropUniqueConstraintOperation
                    or AddCheckConstraintOperation or DropCheckConstraintOperation
                    or DropForeignKeyOperation => true,
                _ => false,
            };

            if (rebuilds && operation is ITableMigrationOperation table)
                rebuilt.TryAdd((table.Table, table.Schema), operation);
            else if (rebuilds && operation is AlterTableOperation alterTable)
                rebuilt.TryAdd((alterTable.Name, alterTable.Schema), operation);
        }

        foreach (var ((table, schema), trigger) in rebuilt)
        {
            var lost = targetIndexes.Where(i => i.TableName == table && i.Schema == schema)
                                    .Select(i => $"'{i.IndexName}'")
                                    .ToList();
            if (lost.Count == 0)
                continue;

            var what = trigger switch
            {
                AlterColumnOperation alter => $"altering column '{alter.Name}'",
                DropColumnOperation drop   => $"dropping column '{drop.Name}'",
                _                          => $"a {trigger.GetType().Name.Replace("Operation", "")}",
            };

            throw new InvalidOperationException(
                $"SQLite rebuilds table '{table}' for this migration ({what}) and re-creates only the indexes EF Core "
              + $"knows, so the complex index{(lost.Count == 1 ? "" : "es")} {string.Join(", ", lost)} on it would be dropped "
              + "and not created again, while the migration applies cleanly. No operation in the same migration can "
              + "re-create it: EF Core's SQLite generator runs every table rebuild after all other operations. Split the "
              + $"change into three migrations: remove the complex index declarations on '{table}' and add a migration; "
              + "make this change and add a migration; declare the indexes again and add a migration.");
        }
    }

    /// <summary>
    /// Reports whether the two models differ, including in the declarations this package owns.
    /// </summary>
    /// <remarks>
    /// EF Core's implementation runs its protected <c>Diff</c> directly rather than the public
    /// <see cref="GetDifferences"/> this class overrides, so it never saw a complex index, exclusion
    /// constraint or temporal constraint change. Everything built on it then reported "no changes"
    /// for exactly those changes: <c>dotnet ef migrations has-pending-model-changes</c>, the
    /// pending-model-changes warning <c>Migrate()</c> raises, and the snapshot check in
    /// <c>migrations remove</c>. Routing through <see cref="GetDifferences"/> also picks up whatever
    /// a provider satellite adds in its own override.
    /// </remarks>
    /// <param name="source">The model migrated from — typically the snapshot.</param>
    /// <param name="target">The model migrated to — the current <c>OnModelCreating</c> result.</param>
    /// <returns><c>true</c> if migrating from <paramref name="source"/> to <paramref name="target"/> needs any operation.</returns>
    public override bool HasDifferences(IRelationalModel? source, IRelationalModel? target)
        => GetDifferences(source, target).Count > 0;

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
    /// Called once with every complex index the <em>target</em> model declares, changed or not, for
    /// the checks that depend on what the table holds after the migration rather than on what the
    /// migration creates.
    /// </summary>
    /// <remarks>
    /// The SQL Server satellite counts clustered indexes here: a second clustered index added in a
    /// later migration is not among that migration's creates, and checking the source model as
    /// well would refuse the very migration that fixes a snapshot holding a conflict. Internal
    /// because the descriptor is; the satellites see it through <c>InternalsVisibleTo</c>.
    /// </remarks>
    internal virtual void ValidateTargetIndexes(IRelationalModel target, IReadOnlyCollection<IndexDescriptor> indexes) { }

    /// <summary>
    /// Whether a base operation on an index's table cannot run while the index exists, so that an
    /// unchanged complex index has to be dropped before the base operations and created again after
    /// them.
    /// </summary>
    /// <remarks>
    /// EF Core moves its own indexes out of the way of its operations, and a complex index is not
    /// one of them. Off in the core: PostgreSQL, SQLite's in-place operations and MySQL need nothing.
    /// The SQL Server satellite turns it on for <c>ALTER COLUMN</c>, which SQL Server refuses on a
    /// column an index uses.
    /// </remarks>
    /// <param name="operation">A base operation on the index's table.</param>
    /// <param name="columns">Every column the index reads: its key columns, included columns, and the columns its filter or expressions name.</param>
    /// <returns><c>true</c> to drop the index before the base operations and create it after them.</returns>
    protected virtual bool RequiresIndexRecreation(MigrationOperation operation, IReadOnlySet<string> columns) => false;

    // Key columns, every included column (an array-valued provider option), and the table's
    // columns that the filter or an expression part names. The text match over-approximates, which
    // costs at most a needless rebuild.
    private static HashSet<string> ColumnsReadBy(IRelationalModel target, IndexDescriptor index)
    {
        var columns = index.ColumnNames.ToHashSet(StringComparer.Ordinal);

        foreach (var value in index.ProviderAnnotations.Values)
            if (value is string[] names)
                columns.UnionWith(names);

        var text = string.Join(" ", index.Parts.Where(p => p.IsExpression).Select(p => p.Value).Append(index.Filter ?? ""));
        if (text.Length > 0 && target.FindTable(index.TableName, index.Schema) is { } table)
            columns.UnionWith(table.Columns.Select(c => c.Name).Where(name => text.Contains(name, StringComparison.Ordinal)));

        return columns;
    }

    /// <summary>
    /// Whether name-only index changes are emitted as <see cref="RenameIndexOperation"/> instead of
    /// drop + create. Off in the core: not every provider can rename these indexes standalone
    /// (SQLite's generator recreates a renamed index from the relational model, where
    /// annotation-declared indexes don't exist). The PostgreSQL and SQL Server satellites enable it.
    /// </summary>
    protected virtual bool CanRenameIndexes => false;

    /// <summary>
    /// Where the provider requires index names to be unique, which decides what a complex index
    /// name is checked against at <c>migrations add</c>. The core default is
    /// <see cref="EFCore.ComplexIndexes.IndexNameScope.Database"/>, SQLite's rule: SQLite rejects a
    /// second <c>CREATE INDEX</c> under a name used on any other table, and ignores the schemas a
    /// model configures. The SQL Server satellite narrows it to
    /// <see cref="EFCore.ComplexIndexes.IndexNameScope.Table"/>, the PostgreSQL one to
    /// <see cref="EFCore.ComplexIndexes.IndexNameScope.Schema"/>.
    /// </summary>
    /// <remarks>
    /// The core also serves providers without a satellite, and the default is the widest scope on
    /// purpose: on a provider that scopes names more narrowly it costs a rename the database would
    /// not have needed, where a narrower default approves a migration that fails when applied.
    /// </remarks>
    protected virtual IndexNameScope IndexNameScope => IndexNameScope.Database;

    /// <summary>
    /// How the provider compares index names, which decides whether two names that differ only in
    /// case collide. The core default is <see cref="StringComparer.OrdinalIgnoreCase"/>: SQLite
    /// compares identifiers without regard to case even when they are quoted, and SQL Server does
    /// under its default collation. The PostgreSQL satellite compares ordinally, because EF Core
    /// quotes every identifier and PostgreSQL keeps the case of a quoted one.
    /// </summary>
    /// <remarks>
    /// Ignoring case is the default for the reason the widest <see cref="IndexNameScope"/> is: where
    /// the database tells the two names apart — a case-sensitive SQL Server collation, a letter
    /// outside ASCII on SQLite, which folds ASCII only — it costs a rename, where comparing
    /// ordinally approves a migration that fails when applied.
    /// </remarks>
    protected virtual StringComparer IndexNameComparer => StringComparer.OrdinalIgnoreCase;

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
    /// Maps the key an index option is <em>stored</em> under to the key the provider's SQL generator
    /// <em>reads</em> from the operation, when the two differ. The default keeps the key.
    /// </summary>
    /// <remarks>
    /// Options are stored under provider model keys (<c>Npgsql:IndexCollation</c>) because the
    /// property-level API writes them onto the property, where an EF relational key such as
    /// <c>Relational:Collation</c> would be read as a <em>column</em> facet. Npgsql's generator,
    /// however, reads index collations from <c>Relational:Collation</c> on the operation — the model
    /// annotation provider does that mapping for native indexes, and this hook does it for ours.
    /// Comparison happens on the stored key, so the mapping never affects diffing.
    /// </remarks>
    protected virtual string ToOperationAnnotationName(string annotationName) => annotationName;

    /// <summary>
    /// The filter a provider gives an index declared without one, the way EF Core's conventions give
    /// it to the provider's own indexes. Null in the core.
    /// </summary>
    /// <remarks>
    /// Resolved on both sides of the diff, like every other part of the descriptor, so a provider that
    /// starts adding one must gate it on <see cref="ComplexIndexAnnotations.RenderingVersion"/>:
    /// otherwise the source would resolve alike and an existing index would never change.
    /// </remarks>
    /// <param name="entityType">The entity type that declares the index.</param>
    /// <param name="storeObject">The index's table.</param>
    /// <param name="columns">The index's key columns, expression parts left out.</param>
    /// <param name="isUnique">Whether the index is unique.</param>
    /// <param name="providerAnnotations">The index's forwarded provider options.</param>
    /// <returns>The filter, or null for none.</returns>
    protected virtual string? ImplicitFilter(
        IEntityType                           entityType,
        StoreObjectIdentifier                 storeObject,
        IReadOnlyList<string>                 columns,
        bool                                  isUnique,
        IReadOnlyDictionary<string, object?>  providerAnnotations
    ) => null;

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
    /// translator) into a final SQL expression: each placeholder becomes a quoted column reference
    /// — or, through <see cref="ResolveUnmappedPart"/>, a parenthesized expression such as a JSON
    /// extraction — and <c>{{</c>/<c>}}</c> unescape to literal braces. Quoting goes through
    /// <see cref="QuoteIdentifier"/>, so a satellite normally has nothing to override here.
    /// </summary>
    protected virtual ResolvedIndexPart ResolveTemplatePart(
        IEntityType           entityType,
        IndexPartDefinition   part,
        StoreObjectIdentifier storeObject
    )
    {
        var template = part.Template!;
        var sql      = new StringBuilder(template.Length);

        // The default-name token is built alongside the SQL, from each placeholder's own token, so
        // that it depends on what the template names rather than on how a member is rendered.
        var token    = new StringBuilder(template.Length);

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

                var (placeholderSql, placeholderToken, _) =
                    ResolvePlaceholder(entityType, template[(i + 1)..end], storeObject, "an index expression", renderingVersion: null);
                sql.Append(placeholderSql);
                token.Append(placeholderToken);
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
            token.Append(ch);
        }

        return new ResolvedIndexPart(true, sql.ToString(), part.Descending, part.NullSort) { NameToken = token.ToString() };
    }

    /// <summary>
    /// Resolves the <c>{Property.Path}</c> placeholders in a filter predicate to quoted column
    /// references — or parenthesized JSON extractions — the way <see cref="ResolveTemplatePart"/>
    /// does for expression parts, so a filter can name properties instead of column names. The
    /// resolved text is what the operation carries, rendered by the stock generator with no runtime
    /// wiring, and what source and target are compared on, so a filter written with placeholders
    /// diffs exactly like one written with column names.
    /// </summary>
    /// <remarks>
    /// Filters are existing SQL, so the rule is narrower than for templates: only a brace pair
    /// whose content is a dotted identifier path, outside a single-quoted string literal, is a
    /// placeholder. Everything else stays verbatim — <c>'{urgent}'</c> is a PostgreSQL array
    /// literal, <c>'{"a": 1}'</c> a JSON document, <c>'{{1,2},{3,4}}'</c> a two-dimensional array —
    /// which is also why there is no <c>{{</c> escape here. A placeholder that names no property
    /// throws: outside a literal, braces are never valid SQL, so it can only be a mistake.
    /// </remarks>
    /// <param name="entityType">The entity type the placeholders are resolved against.</param>
    /// <param name="filter">The filter as declared, or null.</param>
    /// <param name="storeObject">The table whose column names are used.</param>
    /// <returns>The filter with every placeholder replaced, or <paramref name="filter"/> unchanged when it has none.</returns>
    protected string? ResolveFilter(IEntityType entityType, string? filter, StoreObjectIdentifier storeObject)
        => ResolveFilter(entityType, filter, storeObject, renderingVersion: null);

    /// <inheritdoc cref="ResolveFilter(IEntityType, string?, StoreObjectIdentifier)"/>
    /// <param name="entityType">The entity type the placeholders are resolved against.</param>
    /// <param name="filter">The filter as declared, or null.</param>
    /// <param name="storeObject">The table whose column names are used.</param>
    /// <param name="renderingVersion">
    /// Renders JSON members under this <see cref="ComplexIndexAnnotations.RenderingVersion"/> instead of
    /// the model's; exclusion constraint filters pass 1 so that their DDL never changes.
    /// </param>
    internal string? ResolveFilter(IEntityType entityType, string? filter, StoreObjectIdentifier storeObject, int? renderingVersion)
    {
        if (filter is null || !filter.Contains('{'))
            return filter;

        var sql        = new StringBuilder(filter.Length);
        var inLiteral  = false;
        var fallbacks  = new List<(int Start, int End, string Path, string StoreType)>();
        var typedSpans = new List<(int Start, int End)>();

        for (var i = 0; i < filter.Length; i++)
        {
            var ch = filter[i];

            // A doubled quote inside a literal toggles twice and lands back inside it.
            if (ch == '\'')
                inLiteral = !inLiteral;

            if (ch == '{' && !inLiteral)
            {
                var end = filter.IndexOf('}', i + 1);
                if (end > i + 1 && IsPropertyPath(filter.AsSpan(i + 1, end - i - 1)))
                {
                    var (placeholderSql, _, fallback) = ResolvePlaceholder(entityType, filter[(i + 1)..end], storeObject, "a filter", renderingVersion);
                    var start = sql.Length;
                    sql.Append(placeholderSql);

                    if (fallback is { } member)
                        fallbacks.Add((start, sql.Length, member.Path, member.StoreType));
                    else
                        typedSpans.Add((start, sql.Length));

                    i = end;
                    continue;
                }
            }

            sql.Append(ch);
        }

        var resolved = sql.ToString();
        ThrowIfFallbackComparedWithColumn(entityType, resolved, fallbacks, typedSpans);
        return resolved;
    }

    // A JSON member whose type has no IMMUTABLE cast from text (a date or time on PostgreSQL)
    // resolves to its text. Compared with a column, which has its own type, the predicate is
    // refused at apply ("operator does not exist: text < timestamp with time zone"), and the cast
    // EF Core's queries use cannot appear in an index or constraint. The comparison is refused here,
    // at migrations add. A null test and a comparison with a string literal still apply, as before.
    private static void ThrowIfFallbackComparedWithColumn(
        IEntityType                                                      entityType,
        string                                                           sql,
        IReadOnlyList<(int Start, int End, string Path, string StoreType)> fallbacks,
        IReadOnlyList<(int Start, int End)>                              typedSpans)
    {
        foreach (var fallback in fallbacks)
        {
            var after  = FilterComparisonAfter.Match(sql, fallback.End);
            var before = FilterComparisonBefore.Match(sql[..fallback.Start]);

            var withColumn = after.Success && (sql[(fallback.End + after.Length)..].StartsWith('"') || StartsTypedSpan(fallback.End + after.Length))
                          || before.Success && (sql[..before.Index].EndsWith('"') || EndsTypedSpan(before.Index));

            if (withColumn)
                throw new InvalidOperationException(
                    $"A filter on entity '{entityType.DisplayName()}' compares the JSON member '{fallback.Path}' with a column. "
                  + $"The member is {fallback.StoreType} stored as text in its document, and PostgreSQL can neither compare that text "
                  + "with a typed column nor index the cast EF Core's queries use (it is not IMMUTABLE), so the migration would fail "
                  + "at apply. Test the member with IS NULL or compare it with a string literal in the string filter overload, or "
                  + "map it to a regular column.");
        }

        bool StartsTypedSpan(int index) => typedSpans.Any(span => span.Start == index);
        bool EndsTypedSpan(int index) => typedSpans.Any(span => span.End == index);
    }

    private static readonly System.Text.RegularExpressions.Regex FilterComparisonAfter =
        new(@"\G\)*\s*(?:=|<>|!=|<=|>=|<|>)\s*\(*", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex FilterComparisonBefore =
        new(@"\)*\s*(?:=|<>|!=|<=|>=|<|>)\s*\(*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Dotted identifier segments: letters, digits and underscores, each starting with a letter or
    // an underscore. Anything else between braces is SQL text, not a placeholder.
    private static bool IsPropertyPath(ReadOnlySpan<char> text)
    {
        var segmentStart = true;

        foreach (var ch in text)
        {
            if (ch == '.')
            {
                if (segmentStart) return false;
                segmentStart = true;
                continue;
            }

            var valid = segmentStart ? char.IsLetter(ch) || ch == '_' : char.IsLetterOrDigit(ch) || ch == '_';
            if (!valid) return false;

            segmentStart = false;
        }

        return !segmentStart;
    }

    // Returns the SQL for the placeholder and the token it contributes to a default index name.
    private (string Sql, string Token, (string Path, string StoreType)? TextFallback) ResolvePlaceholder(
        IEntityType           entityType,
        string                path,
        StoreObjectIdentifier storeObject,
        string                usage,
        int?                  renderingVersion
    )
    {
        var column = ResolveColumnName(entityType, path, storeObject);
        if (column is not null)
            return (QuoteIdentifier(column), column, null);

        var part     = new IndexPartDefinition { PropertyPath = path, RenderingVersion = renderingVersion };
        var unmapped = ResolveUnmappedPart(entityType, part, storeObject);
        if (unmapped is not null)
            return (unmapped.IsExpression ? $"({unmapped.Value})" : QuoteIdentifier(unmapped.Value),
                    unmapped.NameToken ?? unmapped.Value,
                    unmapped.TextFallback);

        throw new InvalidOperationException(
            $"Could not resolve property path '{path}' referenced by {usage} on entity '{entityType.Name}'.");
    }

    /// <summary>
    /// Delimits an identifier for the SQL this differ renders itself: resolved template parts and
    /// filter placeholders. ANSI double quotes by default, which PostgreSQL and SQLite use; the SQL
    /// Server satellite overrides this with brackets.
    /// </summary>
    /// <param name="identifier">The raw column name.</param>
    /// <returns>The delimited identifier.</returns>
    protected virtual string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    /// <summary>
    /// Fails when two distinct declarations resolve to the same index name within the provider's
    /// <see cref="IndexNameScope"/>.
    /// </summary>
    /// <remarks>
    /// Such a pair emits two <c>CREATE INDEX</c> statements under one name — a migration that
    /// scaffolds happily and then fails at apply time (PostgreSQL 42P07; SQLite "index … already
    /// exists", even when the two sit on different tables). This is the first point where the
    /// collision is visible: the two stores (per-property annotations and the entity-level
    /// definition list) cannot see each other, and default names are only known once property paths
    /// have been resolved to real columns. Identical declarations are already collapsed by the
    /// descriptor set, so only genuinely different indexes reach this check.
    /// </remarks>
    private void ValidateUniqueIndexNames(HashSet<IndexDescriptor> descriptors)
    {
        var collision = descriptors
                       .GroupBy(d => NameKey(d.TableName, d.Schema, d.IndexName), NameKeys)
                       .FirstOrDefault(g => g.Count() > 1);

        if (collision is null)
            return;

        var members  = collision.ToList();
        var oneTable = members.All(d => d.TableName == members[0].TableName);

        if (members[0].Origin is { } origin && members.All(d => d.Origin == origin))
            throw new InvalidOperationException(
                origin.RepeatedName("complex index", members[0].IndexName, UniquenessRule(sameTable: false, ignoringCase: false) + "."));

        var names    = members.Select(d => d.IndexName).Distinct(StringComparer.Ordinal).ToList();
        var named    = names.Count == 1
                           ? $"the name '{names[0]}'"
                           : $"the names {string.Join(" and ", names.Select(n => $"'{n}'"))}, which differ only in case";

        throw new InvalidOperationException(
            (oneTable
                 ? $"Two complex indexes on table '{members[0].TableName}' both resolve to {named}: "
                 + string.Join(" and ", members.Select(Describe))
                 : $"Two complex indexes both resolve to {named}: "
                 + string.Join(" and ", members.Select(d => $"{Describe(d)} on table '{d.TableName}'")))
          + $". {UniquenessRule(oneTable, ignoringCase: names.Count > 1)} — give each declaration an explicit, distinct name.");

        static string Describe(IndexDescriptor descriptor)
        {
            var parts = string.Join(", ", descriptor.Parts.Select(p => p.Value));
            var facets = descriptor.Filter is null ? "" : $" WHERE {descriptor.Filter}";
            return $"({parts}){(descriptor.IsUnique ? " UNIQUE" : "")}{facets}";
        }
    }

    /// <summary>
    /// Fails when a declaration carries an index option of the provider's satellite that this
    /// differ does not forward: the model is being diffed by the core differ where the satellite's
    /// should run.
    /// </summary>
    /// <remarks>
    /// That happens with <c>UseComplexIndexes()</c> instead of the satellite's registration at
    /// runtime, or without the satellite's <c>.targets</c> at design time. The core forwards no
    /// property-level option, so <c>INCLUDE</c>, <c>FILLFACTOR</c>, a clustered index or a GIN
    /// method vanished from the DDL and <c>EnsureCreated()</c> applied the rest cleanly; entity-level
    /// options went through unchecked and unresolved. Only the provider the differ serves is
    /// checked: a model shared between PostgreSQL and SQLite tests carries Npgsql options that
    /// SQLite rightly ignores. Target only, like every check here.
    /// </remarks>
    private void ValidateNoSatelliteOptions(IRelationalModel? target)
    {
        if (target is null || _satellite is null)
            return;

        foreach (var declaration in target.Model.GetComplexIndexes())
        {
            var options = declaration.Property is { } property
                              ? property.GetAnnotations().Select(a => a.Name)
                              : declaration.ProviderAnnotations.Keys;

            var option = options.FirstOrDefault(name => _satellite.IsOption(name) && !IsForwardedIndexAnnotation(name));
            if (option is null)
                continue;

            var index = declaration.Name is { } name ? $"'{name}'" : $"on ({string.Join(", ", declaration.Parts.Select(p => p.PropertyPath ?? p.Expression ?? p.Template))})";

            throw new InvalidOperationException(
                $"The complex index {index} on '{declaration.EntityType.DisplayName()}' declares the {_satellite.Provider} index option "
              + $"'{option}', but the model is diffed by the provider-agnostic core differ, which does not render it: the "
              + $"index would be created without it. Use the {_satellite.Package} satellite's differ: call {_satellite.Registration} "
              + "instead of UseComplexIndexes() at runtime, and for `dotnet ef`, reference the satellite package so its .targets "
              + "selects its differ.");
        }
    }

    /// <summary>
    /// Fails when a complex index resolves to the name of a native <c>HasIndex</c> within the
    /// provider's <see cref="IndexNameScope"/>.
    /// </summary>
    /// <remarks>
    /// The base differ emits the native index and this differ emits the complex one, neither seeing
    /// the other, so the migration scaffolded two <c>CREATE INDEX</c> statements under one name and
    /// failed at apply time (PostgreSQL 42P07). Only the target model's native indexes are consulted:
    /// an index <em>moving</em> between a native declaration and a complex one under the same name
    /// is a legitimate drop-and-create, not a collision, and must keep diffing. Two native indexes
    /// sharing a name are EF Core's to report, not this package's.
    /// </remarks>
    private void ValidateNoNativeIndexNameCollision(IRelationalModel? target, HashSet<IndexDescriptor> descriptors)
    {
        if (target is null || descriptors.Count == 0)
            return;

        var native = new Dictionary<(string? Schema, string? Table, string Name), (string Table, string Name, string Properties)>(NameKeys);

        foreach (var entityType in target.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName is null) continue;

            var schema      = entityType.GetSchema();
            var storeObject = StoreObjectIdentifier.Table(tableName, schema);

            // GetIndexes includes inherited ones; under TPH the base and derived types share the table
            // and report the same index, which TryAdd collapses.
            foreach (var index in entityType.GetIndexes())
            {
                var name = index.GetDatabaseName(storeObject);
                if (name is null) continue;

                native.TryAdd(NameKey(tableName, schema, name), (tableName, name, string.Join(", ", index.Properties.Select(p => p.Name))));
            }
        }

        foreach (var descriptor in descriptors)
        {
            if (!native.TryGetValue(NameKey(descriptor.TableName, descriptor.Schema, descriptor.IndexName), out var other))
                continue;

            var where     = other.Table == descriptor.TableName ? "" : $" on table '{other.Table}'";
            var caseOnly  = !string.Equals(other.Name, descriptor.IndexName, StringComparison.Ordinal);
            var otherName = caseOnly ? $" '{other.Name}'" : "";

            throw new InvalidOperationException(
                $"The complex index '{descriptor.IndexName}' on table '{descriptor.TableName}' has the same name as "
              + $"the native index{otherName} on ({other.Properties}) declared with HasIndex{where}. {UniquenessRule(where == "", caseOnly)} — "
              + "the migration would scaffold two CREATE INDEX statements under one name and fail when applied. "
              + "Give one of them a different name.");
        }
    }

    // Two index names clash exactly when their keys are equal under NameKeys: the parts outside the
    // provider's scope are blanked out, and the name is compared as the provider compares it.
    private (string? Schema, string? Table, string Name) NameKey(string table, string? schema, string name)
        => IndexNameScope switch
           {
               IndexNameScope.Table  => (schema, table, name),
               IndexNameScope.Schema => (schema, null, name),
               _                     => (null, null, name)
           };

    private NameKeyComparer NameKeys => new(IndexNameComparer);

    private string UniquenessRule(bool sameTable, bool ignoringCase)
        => (sameTable, IndexNameScope) switch
           {
               (true, _)                  => "Index names must be unique per table",
               (_, IndexNameScope.Schema) => "Index names must be unique per schema here, not just per table",
               _                          => "Index names must be unique across the database here, not just per table or schema"
           }
         + (ignoringCase ? ", whatever their case" : "");

    private sealed class NameKeyComparer(StringComparer names) : IEqualityComparer<(string? Schema, string? Table, string Name)>
    {
        public bool Equals((string? Schema, string? Table, string Name) x, (string? Schema, string? Table, string Name) y)
            => x.Schema == y.Schema && x.Table == y.Table && names.Equals(x.Name, y.Name);

        public int GetHashCode((string? Schema, string? Table, string Name) key)
            => HashCode.Combine(key.Schema, key.Table, names.GetHashCode(key.Name));
    }

    /// <summary>
    /// Fails when a non-unique index leads with a JSON member that had to fall back to text.
    /// </summary>
    /// <remarks>
    /// The provider's queries compare such a member through a cast PostgreSQL cannot index (text to
    /// <c>timestamptz</c>, <c>date</c>, … is not IMMUTABLE), so no query ever uses an index that
    /// starts with it: it scaffolds, applies, costs every write and speeds up nothing. A unique index
    /// is still allowed, since enforcing uniqueness on the stored text is its purpose; a member in a
    /// later position leaves the index usable through the parts before it.
    /// </remarks>
    private static void ValidateQueryUsableLeadingParts(HashSet<IndexDescriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
        {
            if (descriptor.IsUnique || descriptor.Parts[0].TextFallback is not { } fallback)
                continue;

            throw new InvalidOperationException(
                $"The complex index '{descriptor.IndexName}' on table '{descriptor.TableName}' starts with the JSON member "
              + $"'{fallback.Path}', which EF Core's queries compare as {fallback.StoreType}. PostgreSQL cannot index that conversion from "
              + "the stored text (it is not IMMUTABLE), so no query would ever use this index. Declare it unique if it "
              + "exists to enforce uniqueness, put another part first, or map the member to a regular column.");
        }
    }

    private void ValidateIndexNameLengths(IRelationalModel? target, HashSet<IndexDescriptor> descriptors)
    {
        if (target is null)
            return;

        foreach (var descriptor in descriptors)
            ThrowIfIdentifierTooLong(target.Model, descriptor.IndexName, "complex index", descriptor.TableName, "indexName");
    }

    /// <summary>
    /// Fails when <paramref name="name"/> is longer than the provider's identifier limit
    /// (<see cref="RelationalModelExtensions.GetMaxIdentifierLength"/>). Satellites call it for the
    /// names of the constraints they emit; the core calls it for every complex index name.
    /// </summary>
    /// <remarks>
    /// The names this package derives are never truncated, unlike EF's own default names. PostgreSQL
    /// cuts a longer identifier down to 63 bytes with a NOTICE and applies the migration cleanly, so
    /// the index exists under a name that neither the declaration nor a later constraint-violation
    /// error ever matches — a slice that dispatches on the constraint name falls through in silence.
    /// SQL Server rejects the statement instead. Both are caught here, at <c>migrations add</c>.
    /// Validate the <em>target</em> model only: a snapshot already carrying such a name has to stay
    /// diffable, or the model could never be fixed.
    /// </remarks>
    /// <param name="model">The target model, whose provider sets the identifier limit.</param>
    /// <param name="name">The resolved index or constraint name.</param>
    /// <param name="kind">What the name belongs to, for the message — <c>"complex index"</c>, <c>"exclusion constraint"</c>, …</param>
    /// <param name="table">The table the declaration targets, for the message.</param>
    /// <param name="parameter">The declaration parameter that sets an explicit name, for the message.</param>
    protected void ThrowIfIdentifierTooLong(IReadOnlyModel model, string name, string kind, string table, string parameter)
    {
        var limit            = model.GetMaxIdentifierLength();
        var (length, unit)   = MeasureIdentifier(name);

        if (length <= limit)
            return;

        throw new InvalidOperationException(
            $"The {kind} name '{name}' on table '{table}' is {length} {unit} long, but the provider allows at most {limit}. "
          + $"A longer name is truncated or rejected when the migration is applied, so the {kind} would exist under a "
          + "name that neither this declaration nor a constraint-violation error ever reports. "
          + $"Give the declaration an explicit, shorter name ({parameter}).");
    }

    /// <summary>
    /// Measures an identifier the way the provider does when enforcing
    /// <see cref="RelationalModelExtensions.GetMaxIdentifierLength"/>. The core counts characters,
    /// which is what SQL Server's 128-character limit means; PostgreSQL's 63 is a byte count, so its
    /// satellite overrides this to measure UTF-8 bytes.
    /// </summary>
    /// <param name="identifier">The identifier to measure.</param>
    /// <returns>The length and the unit it is expressed in, for error messages.</returns>
    protected virtual (int Length, string Unit) MeasureIdentifier(string identifier)
        => (identifier.Length, "characters");

    // Declarations come from the same reader an application uses (ComplexIndexModelExtensions),
    // so the read model and the migration cannot disagree about what was declared.
    internal HashSet<IndexDescriptor> ExtractAllIndexDescriptors(IRelationalModel? relationalModel)
    {
        var result = new HashSet<IndexDescriptor>();
        if (relationalModel is null) return result;

        foreach (var entityType in relationalModel.Model.GetEntityTypes())
        {
            var declarations = entityType.GetDeclaredComplexIndexes();
            if (declarations.Count == 0)
                continue;

            var tables = DeclarationTables(entityType);
            if (tables.Count == 0)
            {
                ThrowIfDeclaredOnUnmappedType(entityType, "complex indexes");
                continue;
            }

            foreach (var declaration in declarations)
            {
                var origin = TpcDeclaration.For(entityType, tables);

                foreach (var (tableType, storeObject) in tables)
                {
                    ThrowIfMissingFromDerivedTable(relationalModel, entityType, tableType, storeObject, PathsOf(declaration), "a complex index");

                    var descriptor = declaration.Property is { } property
                                         ? ResolvePropertyLevelIndex(tableType, declaration, property, storeObject.Name, storeObject.Schema, storeObject)
                                         : ResolveEntityLevelIndex(tableType, declaration, storeObject.Name, storeObject.Schema, storeObject);

                    result.Add(descriptor with { Origin = origin });
                }
            }
        }

        return result;
    }

    // Every property path a declaration reads: its parts, the placeholders of its templates and
    // filter, and its INCLUDE entries, which resolve as paths too.
    private static IEnumerable<string> PathsOf(ComplexIndexDeclaration declaration)
    {
        foreach (var part in declaration.Parts)
        {
            if (part.PropertyPath is { } path)
                yield return path;

            foreach (var placeholder in PlaceholderPaths(part.Template))
                yield return placeholder;
        }

        foreach (var placeholder in PlaceholderPaths(declaration.Filter))
            yield return placeholder;

        var options = declaration.Property?.GetAnnotations().Select(a => a.Value)
                   ?? declaration.ProviderAnnotations.Values;

        foreach (var value in options)
        {
            if (value is string || value is not System.Collections.IEnumerable entries)
                continue;

            foreach (var entry in entries.OfType<string>())
                yield return entry;
        }
    }

    /// <summary>
    /// The tables a declaration on <paramref name="entityType"/> is created on, each with the entity
    /// type mapped to it: the type's own table and, under TPC, the table of every type derived from
    /// it, which repeats the inherited columns. Empty when none of them is mapped to a table.
    /// </summary>
    /// <remarks>
    /// This is where EF Core puts a <c>HasIndex</c> declared on a TPC type: on every concrete table
    /// of the hierarchy below it, each under its own default name. Until 5.5.0 every declaration of
    /// this package landed on the declaring type's table only, so a unique index or an exclusion
    /// constraint on a concrete TPC base held among its own rows and not among a derived type's.
    /// Under TPH and TPT the inherited columns live in the declaring type's table, the only one.
    /// <para>
    /// A model declared before rendering version 3 — a snapshot scaffolded before 5.5.0 — keeps the
    /// old answer. Resolved alike, the snapshot and the model would never differ, and an existing
    /// database would never get the derived tables' copies; resolved by the old rule, the first
    /// <c>migrations add</c> after upgrading creates them.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<(IEntityType EntityType, StoreObjectIdentifier Table)> DeclarationTables(IEntityType entityType)
    {
        var everyTable = entityType.GetMappingStrategy() == RelationalAnnotationNames.TpcMappingStrategy
                      && ComplexIndexStorage.GetRenderingVersion(entityType.Model) >= 3;

        IEnumerable<IEntityType> types = everyTable ? entityType.GetDerivedTypesInclusive() : [entityType];

        var tables = new List<(IEntityType, StoreObjectIdentifier)>();
        foreach (var type in types)
        {
            if (type.GetTableName() is { } table)
                tables.Add((type, StoreObjectIdentifier.Table(table, type.GetSchema())));
        }

        return tables;
    }

    /// <summary>
    /// Fails when a declaration on a TPC type reads a member that EF Core maps to no column in the
    /// table of a derived type. Call it for each table of <see cref="DeclarationTables"/>; it checks
    /// the derived types' tables only.
    /// </summary>
    /// <remarks>
    /// EF Core 10 keeps the complex properties of a TPC type in that type's own table: the table of a
    /// derived type has no column for them, table-split or JSON. Resolving such a member there gave a
    /// column the table does not have, or worse, the name of one it does: a table-split member
    /// resolves to its bare property name on a derived table, so <c>Contact.Email</c> came out as
    /// <c>"Email"</c>, which a derived type with an <c>Email</c> property of its own would have
    /// indexed instead.
    /// </remarks>
    internal static void ThrowIfMissingFromDerivedTable(
        IRelationalModel      model,
        IEntityType           declaringType,
        IEntityType           tableType,
        StoreObjectIdentifier storeObject,
        IEnumerable<string>   paths,
        string                declaration)
    {
        if (tableType == declaringType || model.FindTable(storeObject.Name, storeObject.Schema) is not { } table)
            return;

        foreach (var path in paths)
        {
            if (IsMappedTo(table, tableType, path))
                continue;

            throw new InvalidOperationException(
                $"'{declaringType.DisplayName()}' declares {declaration} over '{path}'. Declared on a TPC type, it is created on "
              + $"the table of every type derived from it too, but EF Core maps no column for '{path}' in '{table.Name}', the table "
              + $"of '{tableType.DisplayName()}': it keeps the complex properties of a TPC type in that type's own table only. Map "
              + "the member as a regular property, or remove the declaration.");
        }
    }

    // True unless the path reaches into a complex property that has no column in the table. The
    // entity type's scalars are always there: TPC repeats them in every table of the hierarchy.
    private static bool IsMappedTo(ITable table, IEntityType entityType, string path)
    {
        ITypeBase current   = entityType;
        var       traversed = false;

        foreach (var segment in path.Split('.'))
        {
            var complexProperty = current.FindComplexProperty(segment);
            if (complexProperty is null)
                break;

            if (complexProperty.ComplexType.IsMappedToJson())
                return complexProperty.ComplexType.GetContainerColumnName() is { } container && table.FindColumn(container) is not null;

            current   = complexProperty.ComplexType;
            traversed = true;
        }

        if (!traversed || ResolveProperty(entityType, path) is not { } property)
            return true;

        return property.GetTableColumnMappings().Any(m => m.TableMapping.Table.Name   == table.Name
                                                        && m.TableMapping.Table.Schema == table.Schema);
    }

    // The {Property.Path} placeholders of a template or a filter. A brace pair that is no
    // placeholder ('{urgent}' inside a literal, an escaped {{x}}) adds at worst a path that reaches
    // into no complex property, which the column check passes.
    internal static IEnumerable<string> PlaceholderPaths(string? text)
        => text is null
               ? []
               : PlaceholderPattern.Matches(text).Select(m => m.Groups[1].Value);

    private static readonly System.Text.RegularExpressions.Regex PlaceholderPattern =
        new(@"\{([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// One declaration on a TPC type that is created on more than one table. Its copies carry the
    /// same instance, which lets a name check tell a name repeated by one declaration from two
    /// declarations that happen to share it.
    /// </summary>
    internal sealed class TpcDeclaration
    {
        private readonly IEntityType                          _declaredOn;
        private readonly IReadOnlyList<StoreObjectIdentifier> _tables;

        private TpcDeclaration(IEntityType declaredOn, IReadOnlyList<StoreObjectIdentifier> tables)
        {
            _declaredOn = declaredOn;
            _tables     = tables;
        }

        /// <summary>The origin of a declaration on <paramref name="declaredOn"/>, or null when it lands on one table.</summary>
        public static TpcDeclaration? For(IEntityType declaredOn, IReadOnlyList<(IEntityType EntityType, StoreObjectIdentifier Table)> tables)
            => tables.Count > 1 ? new TpcDeclaration(declaredOn, [.. tables.Select(t => t.Table)]) : null;

        /// <summary>The message for an explicit name that collides with itself across the copies.</summary>
        public string RepeatedName(string kind, string name, string rule)
            => $"The {kind} '{name}' is declared on '{_declaredOn.DisplayName()}', a TPC type, so it is created on the table of every "
             + $"type derived from it too ({string.Join(", ", _tables.Select(t => $"'{t.Name}'"))}), each time under the same explicit "
             + $"name. {rule} Leave the name out: each table's copy then takes a default name that includes its table.";
    }

    /// <summary>
    /// Fails when <paramref name="entityType"/> carries declarations only a table can satisfy, yet
    /// neither it nor, under TPC, any type derived from it is mapped to one. Call it when
    /// <see cref="DeclarationTables"/> is empty; satellites use it for their own descriptors.
    /// </summary>
    /// <remarks>
    /// Such declarations produced nothing: no DDL, no error. Types mapped to a view, a SQL query or
    /// a function are left alone: an index on those is nothing this package could create, and
    /// models have carried the annotation there harmlessly.
    /// </remarks>
    protected static void ThrowIfDeclaredOnUnmappedType(IEntityType entityType, string declarations)
    {
        if (entityType.GetViewName() is not null
         || entityType.GetSqlQuery() is not null
         || entityType.GetFunctionName() is not null)
            return;

        throw new InvalidOperationException(
            $"'{entityType.DisplayName()}' declares {declarations} but is not mapped to a table, and no type derived from it "
          + "under TPC is either, so they cannot be created. Map it or a derived type to a table, or declare them on an "
          + "entity type that is.");
    }

    private IndexDescriptor ResolvePropertyLevelIndex(
        IEntityType             entityType,
        ComplexIndexDeclaration declaration,
        IReadOnlyProperty       property,
        string                  tableName,
        string?                 schema,
        StoreObjectIdentifier   storeObject
    )
    {
        var columnName = property.GetColumnName(storeObject);

        // No table column — a JSON-mapped complex member, for example. Give the provider
        // satellite a chance to resolve it to an expression part before giving up.
        var part = columnName is not null
                       ? new ResolvedIndexPart(false, columnName)
                       : ResolveUnmappedPart(entityType, declaration.Parts[0], storeObject)
                      ?? throw new InvalidOperationException(
                             $"The property '{property.Name}' on '{property.DeclaringType.Name}' is marked with " +
                             $"HasComplexIndex but has no column mapping for table '{tableName}'. " +
                             "A property mapped to JSON (or not mapped to this table) cannot carry " +
                             "a complex index here; use an expression index over the JSON column instead.");

        var indexName = declaration.Name ?? $"IX_{tableName}_{BuildPartToken(part)}";

        // Collect only whitelisted provider index options; everything else on the property is a
        // column facet that does not belong on an index operation.
        var providerAnnotations = new Dictionary<string, object?>();
        foreach (var annotation in property.GetAnnotations())
        {
            if (IsForwardedIndexAnnotation(annotation.Name))
                providerAnnotations[annotation.Name] = TransformIndexAnnotation(entityType, annotation.Name, annotation.Value, storeObject);
        }

        var filter = ResolveFilter(entityType, declaration.Filter, storeObject)
                  ?? ImplicitFilter(entityType, storeObject, part.IsExpression ? [] : [part.Value], declaration.IsUnique, providerAnnotations);

        return new IndexDescriptor(tableName, schema, [part], indexName, declaration.IsUnique, filter, providerAnnotations);
    }

    private IndexDescriptor ResolveEntityLevelIndex(
        IEntityType             entityType,
        ComplexIndexDeclaration declaration,
        string                  tableName,
        string?                 schema,
        StoreObjectIdentifier   storeObject
    )
    {
        var parts = new List<ResolvedIndexPart>(declaration.Parts.Count);

        foreach (var part in declaration.Parts)
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

        var indexName = declaration.Name ?? $"IX_{tableName}_{string.Join("_", parts.Select(BuildPartToken))}";

        var providerAnnotations = new Dictionary<string, object?>(declaration.ProviderAnnotations.Count);
        foreach (var (key, value) in declaration.ProviderAnnotations)
            providerAnnotations[key] = TransformIndexAnnotation(entityType, key, value, storeObject);

        var filter = ResolveFilter(entityType, declaration.Filter, storeObject)
                  ?? ImplicitFilter(entityType, storeObject, [.. parts.Where(p => !p.IsExpression).Select(p => p.Value)], declaration.IsUnique, providerAnnotations);

        return new IndexDescriptor(tableName, schema, parts, indexName, declaration.IsUnique, filter, providerAnnotations);
    }

    // Builds a default index-name token for a part: column names pass through; expressions are
    // reduced to their alphanumeric characters (e.g. lower("Email") -> "lowerEmail"), starting from
    // the part's name token where it has one, so a rendering change never renames the index.
    private static string BuildPartToken(ResolvedIndexPart part)
    {
        if (!part.IsExpression)
            return part.Value;

        var token = new string([.. (part.NameToken ?? part.Value).Where(char.IsLetterOrDigit)]);
        return token.Length > 0 ? token : "expr";
    }

    /// <summary>Resolves a dotted property path (complex members included) to its column name, or null.</summary>
    protected static string? ResolveColumnName(
        IEntityType           entityType,
        string                dotPath,
        StoreObjectIdentifier storeObject
    )
        => ResolveProperty(entityType, dotPath)?.GetColumnName(storeObject);

    /// <summary>
    /// Resolves a dotted property path to the scalar property it names, walking complex
    /// properties, or null when there is no such property.
    /// </summary>
    /// <remarks>
    /// A path may also end one step <em>past</em> a scalar property, at a member of its CLR type:
    /// <c>Email.Value</c> where <c>Email</c> is a value object mapped through a value converter.
    /// The member unwraps to the property when the property has a converter and the member's type
    /// is the converter's provider type — the column holds exactly that member. Without the type
    /// check, <c>CreatedAt.Year</c> would resolve to the whole column and index something other
    /// than what was written. A model snapshot cannot be checked that way: it persists the
    /// property as its provider type on a property-bag type and drops the converter, so there the
    /// path — already validated against the configured model when it was scaffolded — unwraps to
    /// the persisted scalar unconditionally.
    /// </remarks>
    /// <param name="typeBase">The entity or complex type the path starts from.</param>
    /// <param name="dotPath">The path, e.g. <c>Address.City</c>.</param>
    /// <returns>The property, or null.</returns>
    protected static IProperty? ResolveProperty(ITypeBase typeBase, string dotPath)
    {
        var parts   = dotPath.Split('.');
        var current = typeBase;

        for (var i = 0; i < parts.Length; i++)
        {
            if (i == parts.Length - 1)
                return current.FindProperty(parts[i]);

            var complexProperty = current.FindComplexProperty(parts[i]);
            if (complexProperty is not null)
            {
                current = complexProperty.ComplexType;
                continue;
            }

            return i == parts.Length - 2 ? FindConvertedMember(current, parts[i], parts[i + 1]) : null;
        }

        return null;
    }

    private static IProperty? FindConvertedMember(ITypeBase typeBase, string propertyName, string memberName)
    {
        var property = typeBase.FindProperty(propertyName);
        if (property is null)
            return null;

        var converter = property.FindTypeMapping()?.Converter ?? property.GetValueConverter();
        if (converter is null)
        {
            // A model snapshot rebuilds entity and complex types alike as property bags and persists
            // a converted property as its provider type, without the converter — there is no member
            // left to check. The path was validated against the configured model when the snapshot
            // was scaffolded, so resolve it to the persisted scalar. Every diff whose source is the
            // snapshot depends on this: the next `migrations add`, `has-pending-model-changes`, and
            // the pending-changes check `Migrate()` runs before applying anything.
            return typeBase.IsPropertyBag && property.IsIndexerProperty() ? property : null;
        }

        var memberType = property.ClrType.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance)?.PropertyType
                      ?? property.ClrType.GetField(memberName, BindingFlags.Public | BindingFlags.Instance)?.FieldType;
        if (memberType is null)
            return null;

        return Unwrap(memberType) == Unwrap(converter.ProviderClrType) ? property : null;

        static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;
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
        // For messages only, outside equality: the copies of one declaration on a TPC type share it.
        public TpcDeclaration? Origin { get; init; }

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
