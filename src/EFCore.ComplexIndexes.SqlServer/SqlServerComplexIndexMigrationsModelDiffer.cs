using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace EFCore.ComplexIndexes.SqlServer;

#pragma warning disable EF1001

/// <summary>
/// Extends <see cref="CustomMigrationsModelDiffer"/> for SQL Server: forwards the SQL Server index
/// options (clustered, INCLUDE, online, fill factor, …) that the provider's own SQL generator
/// renders, and rejects features SQL Server cannot express — expression parts (model a persisted
/// computed column and index that instead) and <c>NULLS FIRST</c>/<c>NULLS LAST</c>.
/// </summary>
public class SqlServerComplexIndexMigrationsModelDiffer(
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
    private static readonly HashSet<string> SupportedSqlServerAnnotations =
    [
        SqlServerAnnotations.Clustered,
        SqlServerAnnotations.Include,
        SqlServerAnnotations.CreatedOnline,
        SqlServerAnnotations.FillFactor,
        SqlServerAnnotations.SortInTempDb,
        SqlServerAnnotations.DataCompression
    ];

    /// <summary>
    /// This differ renders what only the satellite does — its index options and the <c>IS NOT NULL</c> filter of a unique index — so the core's refusal of them
    /// (<c>UseComplexIndexes()</c> where this satellite's registration belongs) does not apply.
    /// </summary>
    internal override bool IsSatelliteDiffer => true;

    /// <summary>
    /// Forwards exactly the SQL Server index-option annotations the provider's SQL generator renders —
    /// and every <c>Npgsql:*</c> key, which is forwarded only so that
    /// <see cref="ValidateCreateIndexOperation"/> rejects it. A property-level <c>.UseGin()</c> on a
    /// model diffed by this satellite would otherwise be dropped by the whitelist without a word,
    /// leaving a plain B-tree where the entity-level declaration of the same option fails loudly.
    /// </summary>
    protected override bool IsForwardedIndexAnnotation(string annotationName)
        => SupportedSqlServerAnnotations.Contains(annotationName)
        || annotationName.StartsWith("Npgsql:", StringComparison.Ordinal);

    /// <summary>SQL Server renames indexes standalone (<c>sp_rename</c>).</summary>
    protected override bool CanRenameIndexes => true;

    /// <summary>
    /// An <c>ALTER COLUMN</c> on a column the index reads: SQL Server refuses it while the index
    /// exists (Msg 5074), and EF drops only its own indexes around it.
    /// </summary>
    protected override bool RequiresIndexRecreation(MigrationOperation operation, IReadOnlySet<string> columns)
        => operation is AlterColumnOperation alter && columns.Contains(alter.Name);

    /// <summary>
    /// <c>[c] IS NOT NULL AND …</c> over the nullable key columns of a unique, nonclustered index
    /// declared without a filter: what EF Core's <c>SqlServerIndexConvention</c> gives its own unique
    /// indexes. SQL Server, unlike the SQL standard, treats NULLs as equal in a unique index, so
    /// without it an optional complex property admitted one row without a value and refused the
    /// second. Models declared before rendering version 3 keep the old answer, so that the change
    /// reaches existing databases as one rebuild per index instead of never.
    /// </summary>
    protected override string? ImplicitFilter(
        IEntityType                           entityType,
        StoreObjectIdentifier                 storeObject,
        IReadOnlyList<string>                 columns,
        bool                                  isUnique,
        IReadOnlyDictionary<string, object?>  providerAnnotations
    )
    {
        if (!isUnique
         || providerAnnotations.TryGetValue(SqlServerAnnotations.Clustered, out var clustered) && clustered is true
         || ComplexIndexStorage.GetRenderingVersion(entityType.Model) < 3)
            return null;

        var table    = entityType.Model.GetRelationalModel().FindTable(storeObject.Name, storeObject.Schema);
        var nullable = columns.Where(column => table?.FindColumn(column)?.IsNullable == true).ToList();

        return nullable.Count == 0
                   ? null
                   : string.Join(" AND ", nullable.Select(column => $"{QuoteIdentifier(column)} IS NOT NULL"));
    }

    /// <summary>
    /// SQL Server keeps index names unique per table, so two tables may each carry an index of the
    /// same name.
    /// </summary>
    protected override IndexNameScope IndexNameScope => IndexNameScope.Table;

    /// <summary>
    /// SQL Server compares index names without regard to case under its default collation, so
    /// <c>ix_code</c> and <c>IX_CODE</c> on one table are one name.
    /// </summary>
    protected override StringComparer IndexNameComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>SQL Server delimits identifiers with brackets.</summary>
    protected override string QuoteIdentifier(string identifier)
        => "[" + identifier.Replace("]", "]]") + "]";

    /// <summary>
    /// Resolves property paths inside INCLUDE lists to column names (verbatim fallback), and
    /// restores the data-compression enum after its JSON round trip.
    /// </summary>
    protected override object? TransformIndexAnnotation(
        IEntityType           entityType,
        string                annotationName,
        object?               value,
        StoreObjectIdentifier storeObject
    )
        => annotationName switch
               {
                   SqlServerAnnotations.Include         => ResolveIncludeColumns(entityType, value, storeObject),
                   SqlServerAnnotations.DataCompression => CoerceDataCompression(value),
                   _                                    => base.TransformIndexAnnotation(entityType, annotationName, value, storeObject)
               };

    // Entity-level index definitions are stored as JSON, which flattens the enum to a number. SQL
    // Server's generator reads this option as DataCompressionType? — a boxed int reads back as null,
    // so the option would silently vanish from the generated DDL.
    private static object? CoerceDataCompression(object? value) => value switch
    {
        DataCompressionType                                                                   => value,
        int i                                                                                 => (DataCompressionType)i,
        long l                                                                                => (DataCompressionType)l,
        string s when Enum.TryParse<DataCompressionType>(s, ignoreCase: true, out var parsed) => parsed,
        _                                                                                     => value
    };

    /// <summary>
    /// Rejects complex-index declarations SQL Server cannot express: PostgreSQL index options,
    /// expression parts, and <c>NULLS FIRST</c>/<c>NULLS LAST</c> ordering.
    /// </summary>
    protected override void ValidateCreateIndexOperation(CreateIndexOperation operation)
    {
        foreach (var annotation in operation.GetAnnotations())
        {
            if (annotation.Name.StartsWith("Npgsql:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Complex index '{operation.Name}' carries the PostgreSQL annotation '{annotation.Name}', but the " +
                    "model is diffed with the SQL Server satellite. Use the EFCore.ComplexIndexes.SqlServer options instead."
                );
            }
        }

        ValidateClusteredCombination(operation);

        if (operation[ComplexIndexAnnotations.IndexParts] is not string partsJson)
            return;

        var parts = IndexPartsSerializer.Deserialize(partsJson);

        if (parts.Any(p => p.IsExpression))
        {
            throw new InvalidOperationException(
                $"Complex index '{operation.Name}' contains a SQL expression part. SQL Server has no expression-index " +
                "DDL — model the expression as a persisted computed column and index that column instead."
            );
        }

        if (parts.Any(p => p.NullSort != DbNullSort.Default))
        {
            throw new InvalidOperationException(
                $"Complex index '{operation.Name}' declares NULLS FIRST/LAST ordering, which SQL Server does not " +
                "support. Remove the DbOrder.NullsFirst/NullsLast marker."
            );
        }
    }

    /// <summary>
    /// Rejects clustered-index combinations SQL Server does not accept. These render into perfectly
    /// well-formed-looking DDL that the server refuses at apply time, so catching them at
    /// <c>migrations add</c> turns a late, cryptic failure into an actionable one.
    /// </summary>
    private static void ValidateClusteredCombination(CreateIndexOperation operation)
    {
        if (operation[SqlServerAnnotations.Clustered] is not true)
            return;

        // INCLUDE covers non-key columns of a nonclustered index; a clustered index already stores
        // every column in its leaf level, so the syntax is rejected outright.
        if (operation[SqlServerAnnotations.Include] is System.Collections.IEnumerable include
         && include.Cast<object?>().Any())
        {
            throw new InvalidOperationException(
                $"Complex index '{operation.Name}' is clustered and declares INCLUDE columns. SQL Server allows "
              + "included columns only on nonclustered indexes — a clustered index already stores every column. "
              + "Drop IncludeProperties(...) or IsClustered().");
        }

        // Filtered indexes must be nonclustered.
        if (!string.IsNullOrEmpty(operation.Filter))
        {
            throw new InvalidOperationException(
                $"Complex index '{operation.Name}' is clustered and declares a filter. SQL Server allows filtered "
              + "indexes only on nonclustered indexes. Drop the filter or IsClustered().");
        }
    }

    /// <summary>
    /// Checks each table as the target model leaves it: at most one clustered index, and no complex
    /// index named like one of the table's keys.
    /// </summary>
    /// <remarks>
    /// Both checks read the whole target table, not the operations of this migration. A clustered
    /// complex index declared in an earlier migration is not among the creates of a later one, so
    /// counting only those scaffolded <c>CREATE CLUSTERED INDEX</c> for a second one, which failed
    /// at apply. The source model is never checked: a snapshot that already holds a conflict has
    /// to stay diffable, or the migration that resolves it would be refused too.
    /// </remarks>
    internal override void ValidateTargetIndexes(IRelationalModel target, IReadOnlyCollection<IndexDescriptor> indexes)
    {
        foreach (var onTable in indexes.GroupBy(index => (index.TableName, index.Schema)))
        {
            var table = target.FindTable(onTable.Key.TableName, onTable.Key.Schema);
            if (table is null)
                continue;

            ValidateClusteredSlot(table, [.. onTable.Where(IsClustered)]);
            ValidateNoKeyNameCollision(table, onTable);
        }

        static bool IsClustered(IndexDescriptor index)
            => index.ProviderAnnotations.TryGetValue(SqlServerAnnotations.Clustered, out var clustered) && clustered is true;
    }

    /// <summary>
    /// Rejects a clustered complex index on a table whose clustered slot is taken: by another
    /// clustered complex index, by the primary key or a unique constraint, or by a native index.
    /// </summary>
    /// <remarks>
    /// The primary key matters most: the SQL Server provider makes it clustered unless told
    /// otherwise, so on a conventionally mapped entity <em>every</em> clustered complex index
    /// conflicts, and without this check the failure only appears when the migration is applied.
    /// </remarks>
    private static void ValidateClusteredSlot(ITable table, IReadOnlyList<IndexDescriptor> clustered)
    {
        if (clustered.Count == 0)
            return;

        if (clustered.Count > 1)
            throw new InvalidOperationException(
                $"Table '{table.Name}' declares {clustered.Count} clustered complex indexes "
              + $"({string.Join(", ", clustered.Select(index => $"'{index.IndexName}'"))}). SQL Server allows at most one "
              + "clustered index per table — the others must be nonclustered.");

        var storeObject = StoreObjectIdentifier.Table(table.Name, table.Schema);
        var name        = clustered[0].IndexName;

        foreach (var constraint in table.UniqueConstraints)
        {
            var isPrimaryKey = constraint.GetIsPrimaryKey();

            // SQL Server clusters a primary key unless told otherwise; a unique constraint only on request.
            if (!constraint.MappedKeys.Any(key => isPrimaryKey ? key.IsClustered(storeObject) != false : key.IsClustered(storeObject) == true))
                continue;

            throw new InvalidOperationException(
                isPrimaryKey
                    ? $"The clustered complex index '{name}' is declared on table '{table.Name}', but its primary key is clustered "
                    + "(the SQL Server default). A table can carry only one clustered index — declare the key with "
                    + "HasKey(...).IsClustered(false), or make the complex index nonclustered."
                    : $"The clustered complex index '{name}' is declared on table '{table.Name}', but its unique constraint "
                    + $"'{constraint.Name}' is already clustered. A table can carry only one clustered index.");
        }

        var clusteredIndex = table.Indexes.FirstOrDefault(index => index.MappedIndexes.Any(i => i.IsClustered(storeObject) == true));

        if (clusteredIndex is not null)
            throw new InvalidOperationException(
                $"The clustered complex index '{name}' is declared on table '{table.Name}', but the native index on "
              + $"({string.Join(", ", clusteredIndex.Columns.Select(c => c.Name))}) is already clustered. "
              + "A table can carry only one clustered index.");
    }

    /// <summary>
    /// Rejects a complex index named like its table's primary key or one of its unique constraints.
    /// </summary>
    /// <remarks>
    /// SQL Server builds an index under each of those names, so <c>CREATE INDEX [PK_things]</c> on
    /// <c>things</c> scaffolded and failed at apply. The core checks complex indexes against native
    /// <c>HasIndex</c> declarations only, since keys own no index name on SQLite. Compared under
    /// <see cref="IndexNameComparer"/>, as the core compares index names.
    /// </remarks>
    private void ValidateNoKeyNameCollision(ITable table, IEnumerable<IndexDescriptor> indexes)
    {
        foreach (var index in indexes)
        {
            var constraint = table.UniqueConstraints.FirstOrDefault(c => IndexNameComparer.Equals(c.Name, index.IndexName));
            if (constraint is null)
                continue;

            throw new InvalidOperationException(
                $"The complex index '{index.IndexName}' on table '{table.Name}' has the same name as the table's "
              + (constraint.GetIsPrimaryKey() ? "primary key" : $"unique constraint on ({string.Join(", ", constraint.Columns.Select(c => c.Name))})")
              + $" '{constraint.Name}'. SQL Server creates an index under that name, so the migration would fail when applied. "
              + "Give the complex index a different name.");
        }
    }
}

#pragma warning restore EF1001
