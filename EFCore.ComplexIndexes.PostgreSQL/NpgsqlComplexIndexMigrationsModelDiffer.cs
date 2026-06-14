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

        return ApplyTemporalConstraints(operations, source, target, typeMappingSource);
    }

    // Diffs the temporal UNIQUE constraints and temporal FOREIGN KEY constraints declared on entity
    // types and emits standalone Add/Drop* operations. Temporal drops are placed before the base EF
    // operations; temporal adds are placed after, with UNIQUE constraints before FOREIGN KEYs.
    private static IReadOnlyList<MigrationOperation> ApplyTemporalConstraints(
        IReadOnlyList<MigrationOperation> operations,
        IRelationalModel?                 source,
        IRelationalModel?                 target,
        IRelationalTypeMappingSource      typeMappingSource
    )
    {
        var sourceConstraints = BuildDescriptors(source, typeMappingSource);
        var targetConstraints = BuildDescriptors(target, typeMappingSource);
        var sourceForeignKeys = BuildForeignKeyDescriptors(source, typeMappingSource, sourceConstraints);
        var targetForeignKeys = BuildForeignKeyDescriptors(target, typeMappingSource, targetConstraints);

        if (sourceConstraints.Count == 0 && targetConstraints.Count == 0
                                        && sourceForeignKeys.Count == 0 && targetForeignKeys.Count == 0)
            return operations;

        var result        = new List<MigrationOperation>();
        var droppedTables = operations
                           .OfType<DropTableOperation>()
                           .Select(o => (o.Name, o.Schema))
                           .ToHashSet();
        var addedTemporal = false;

        foreach (var src in sourceForeignKeys)
        {
            if (droppedTables.Contains((src.DependentTable, src.DependentSchema)))
                continue;

            if (targetForeignKeys.Contains(src) && !DependsOnChangedTemporalConstraint(src, sourceConstraints, targetConstraints))
                continue;

            result.Add(new DropForeignKeyOperation
                       {
                           Name   = src.Name,
                           Table  = src.DependentTable,
                           Schema = src.DependentSchema
                       });
        }

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

        result.AddRange(operations);

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

        foreach (var tgt in targetForeignKeys)
        {
            if (sourceForeignKeys.Contains(tgt) && !DependsOnChangedTemporalConstraint(tgt, sourceConstraints, targetConstraints))
                continue;

            var op = new AddForeignKeyOperation
                     {
                         Name             = tgt.Name,
                         Table            = tgt.DependentTable,
                         Schema           = tgt.DependentSchema,
                         Columns          = [.. tgt.DependentColumns, tgt.DependentPeriodColumn],
                         PrincipalTable   = tgt.PrincipalTable,
                         PrincipalSchema  = tgt.PrincipalSchema,
                         PrincipalColumns = [.. tgt.PrincipalColumns, tgt.PrincipalPeriodColumn],
                         OnDelete         = ReferentialAction.NoAction,
                         OnUpdate         = ReferentialAction.NoAction
                     };
            op.AddAnnotation(NpgsqlTemporalAnnotations.ForeignKeyDependentPeriod, tgt.DependentPeriodColumn);
            op.AddAnnotation(NpgsqlTemporalAnnotations.ForeignKeyPrincipalPeriod, tgt.PrincipalPeriodColumn);
            result.Add(op);
        }

        if (addedTemporal && ShouldInjectExtension(target))
            result.Insert(0, new SqlOperation { Sql = $"CREATE EXTENSION IF NOT EXISTS {NpgsqlTemporalAnnotations.BtreeGistExtension};" });

        return result;
    }

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

    private static bool DependsOnChangedTemporalConstraint(
        TemporalForeignKeyDescriptor foreignKey,
        HashSet<TemporalDescriptor>  sourceConstraints,
        HashSet<TemporalDescriptor>  targetConstraints)
    {
        var sourceConstraint = sourceConstraints.FirstOrDefault(c => IsPrincipalConstraintFor(foreignKey, c));
        var targetConstraint = targetConstraints.FirstOrDefault(c => IsPrincipalConstraintFor(foreignKey, c));

        return sourceConstraint is null
            || targetConstraint is null
            || !sourceConstraint.Equals(targetConstraint);
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
        => model.GetEntityTypes()
                .FirstOrDefault(e => e.Name == entityTypeName
                                  || e.ClrType.FullName == entityTypeName
                                  || e.ClrType.Name == entityTypeName);

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
