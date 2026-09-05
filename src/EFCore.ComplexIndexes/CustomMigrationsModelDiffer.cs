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
/// <c>IsForwardedIndexAnnotation</c>, <c>ValidateCreateIndexOperation</c>, <c>ResolveUnmappedPart</c>
/// and <c>ResolveTemplatePart</c>.
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

                sql.Append(ResolvePlaceholder(entityType, template[(i + 1)..end], storeObject, "an index expression"));
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
    {
        if (filter is null || !filter.Contains('{'))
            return filter;

        var sql       = new StringBuilder(filter.Length);
        var inLiteral = false;

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
                    sql.Append(ResolvePlaceholder(entityType, filter[(i + 1)..end], storeObject, "a filter"));
                    i = end;
                    continue;
                }
            }

            sql.Append(ch);
        }

        return sql.ToString();
    }

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

    private string ResolvePlaceholder(IEntityType entityType, string path, StoreObjectIdentifier storeObject, string usage)
    {
        var column = ResolveColumnName(entityType, path, storeObject);
        if (column is not null)
            return QuoteIdentifier(column);

        var unmapped = ResolveUnmappedPart(entityType, new IndexPartDefinition { PropertyPath = path }, storeObject);
        if (unmapped is not null)
            return unmapped.IsExpression ? $"({unmapped.Value})" : QuoteIdentifier(unmapped.Value);

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

    /// <summary>
    /// Fails when a complex index resolves to the name of a native <c>HasIndex</c> on the same table.
    /// </summary>
    /// <remarks>
    /// The base differ emits the native index and this differ emits the complex one, neither seeing
    /// the other, so the migration scaffolded two <c>CREATE INDEX</c> statements under one name and
    /// failed at apply time (PostgreSQL 42P07). Only the target model's native indexes are consulted:
    /// an index <em>moving</em> between a native declaration and a complex one under the same name
    /// is a legitimate drop-and-create, not a collision, and must keep diffing.
    /// </remarks>
    private static void ValidateNoNativeIndexNameCollision(IRelationalModel? target, HashSet<IndexDescriptor> descriptors)
    {
        if (target is null || descriptors.Count == 0)
            return;

        var native = new Dictionary<(string Table, string? Schema, string Name), string>();

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

                native.TryAdd((tableName, schema, name), string.Join(", ", index.Properties.Select(p => p.Name)));
            }
        }

        foreach (var descriptor in descriptors)
        {
            if (!native.TryGetValue((descriptor.TableName, descriptor.Schema, descriptor.IndexName), out var properties))
                continue;

            throw new InvalidOperationException(
                $"The complex index '{descriptor.IndexName}' on table '{descriptor.TableName}' has the same name as "
              + $"the native index on ({properties}) declared with HasIndex. Index names must be unique per table — "
              + "the migration would scaffold two CREATE INDEX statements under one name and fail when applied. "
              + "Give one of them a different name.");
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
    private HashSet<IndexDescriptor> ExtractAllIndexDescriptors(IRelationalModel? relationalModel)
    {
        var result = new HashSet<IndexDescriptor>();
        if (relationalModel is null) return result;

        foreach (var entityType in relationalModel.Model.GetEntityTypes())
        {
            var declarations = entityType.GetDeclaredComplexIndexes();
            if (declarations.Count == 0)
                continue;

            var tableName = entityType.GetTableName();
            var schema    = entityType.GetSchema();
            if (tableName is null)
            {
                ThrowIfDeclaredOnUnmappedType(entityType, "complex indexes");
                continue;
            }

            var storeObject = StoreObjectIdentifier.Table(tableName, schema);

            foreach (var declaration in declarations)
            {
                result.Add(declaration.Property is { } property
                               ? ResolvePropertyLevelIndex(entityType, declaration, property, tableName, schema, storeObject)
                               : ResolveEntityLevelIndex(entityType, declaration, tableName, schema, storeObject));
            }
        }

        return result;
    }

    /// <summary>
    /// Fails when <paramref name="entityType"/> is mapped to no table yet carries declarations only a
    /// table can satisfy. Call it after establishing both; satellites use it for their own descriptors.
    /// </summary>
    /// <remarks>
    /// The usual shape is the abstract base of a TPC hierarchy: it has no table of its own, so its
    /// declarations produced nothing — no DDL, no error. Types mapped to a view, a SQL query or a
    /// function are left alone: an index on those is nothing this package could create, and models
    /// have carried the annotation there harmlessly.
    /// </remarks>
    protected static void ThrowIfDeclaredOnUnmappedType(IEntityType entityType, string declarations)
    {
        if (entityType.GetViewName() is not null
         || entityType.GetSqlQuery() is not null
         || entityType.GetFunctionName() is not null)
            return;

        throw new InvalidOperationException(
            $"'{entityType.DisplayName()}' declares {declarations} but is not mapped to a table, so they cannot be "
          + "created. This is typically the abstract base of a TPC hierarchy, whose columns live on each concrete "
          + "table: declare them on the concrete entity types instead.");
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

        return new IndexDescriptor(tableName, schema, [part], indexName, declaration.IsUnique, ResolveFilter(entityType, declaration.Filter, storeObject), providerAnnotations);
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

        return new IndexDescriptor(tableName, schema, parts, indexName, declaration.IsUnique, ResolveFilter(entityType, declaration.Filter, storeObject), providerAnnotations);
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
    /// than what was written.
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
        var property  = typeBase.FindProperty(propertyName);
        var converter = property?.FindTypeMapping()?.Converter ?? property?.GetValueConverter();
        if (property is null || converter is null)
            return null;

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