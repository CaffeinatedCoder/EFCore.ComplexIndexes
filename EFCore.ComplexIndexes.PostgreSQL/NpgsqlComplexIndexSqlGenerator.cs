using System.Collections;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;

namespace EFCore.ComplexIndexes.PostgreSQL;

#pragma warning disable EF1001

/// <summary>
/// Renders <c>CREATE INDEX</c> for expression indexes — those whose column list contains one or
/// more verbatim SQL expressions. Npgsql's base generator builds the column list from
/// <c>operation.Columns</c> (quoted identifiers) with no hook to inject an expression, so when the
/// <see cref="ComplexIndexAnnotations.IndexParts"/> annotation is present this generator renders the
/// statement itself; all other operations delegate to the base Npgsql generator unchanged.
/// </summary>
public class NpgsqlComplexIndexSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    INpgsqlSingletonOptions            npgsqlSingletonOptions
) : NpgsqlMigrationsSqlGenerator(dependencies, npgsqlSingletonOptions)
{
    protected override void Generate(
        CreateIndexOperation        operation,
        IModel?                     model,
        MigrationCommandListBuilder builder,
        bool                        terminate = true
    )
    {
        if (operation[ComplexIndexAnnotations.IndexParts] is not string partsJson)
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        var parts     = IndexPartsSerializer.Deserialize(partsJson);
        var sqlHelper = Dependencies.SqlGenerationHelper;

        var concurrently  = operation[NpgsqlAnnotations.CreatedConcurrently] is true;
        var method        = operation[NpgsqlAnnotations.IndexMethod] as string;
        var operators     = ToStringList(operation[NpgsqlAnnotations.IndexOperators]);
        var include       = ToStringList(operation[NpgsqlAnnotations.IndexInclude]);
        var nullsDistinct = operation[NpgsqlAnnotations.NullsDistinct];

        builder.Append("CREATE ");
        if (operation.IsUnique)
            builder.Append("UNIQUE ");
        builder.Append("INDEX ");
        if (concurrently)
            builder.Append("CONCURRENTLY ");

        builder
           .Append(sqlHelper.DelimitIdentifier(operation.Name))
           .Append(" ON ")
           .Append(sqlHelper.DelimitIdentifier(operation.Table, operation.Schema));

        if (!string.IsNullOrEmpty(method))
            builder.Append(" USING ").Append(method);

        builder.Append(" (");
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
                builder.Append(", ");

            builder.Append(parts[i].IsExpression
                               ? $"({parts[i].Value})"
                               : sqlHelper.DelimitIdentifier(parts[i].Value));

            if (operators is not null && i < operators.Count && !string.IsNullOrEmpty(operators[i]))
                builder.Append(" ").Append(operators[i]);
        }

        builder.Append(")");

        if (include is { Count: > 0 })
        {
            builder.Append(" INCLUDE (");
            builder.Append(string.Join(", ", include.Select(sqlHelper.DelimitIdentifier)));
            builder.Append(")");
        }

        // Default in PostgreSQL is NULLS DISTINCT; only the non-default needs emitting.
        if (nullsDistinct is false)
            builder.Append(" NULLS NOT DISTINCT");

        if (!string.IsNullOrEmpty(operation.Filter))
            builder.Append(" WHERE ").Append(operation.Filter);

        if (terminate)
        {
            builder.AppendLine(sqlHelper.StatementTerminator);
            EndStatement(builder, suppressTransaction: concurrently);
        }
    }

    /// <summary>
    /// Renders a temporal <c>UNIQUE</c> constraint (<c>… WITHOUT OVERLAPS</c>) declared via
    /// <c>HasTemporalConstraint</c>; otherwise delegates to the base Npgsql generator.
    /// </summary>
    protected override void Generate(
        AddUniqueConstraintOperation operation,
        IModel?                      model,
        MigrationCommandListBuilder  builder
    )
    {
        if (operation[NpgsqlTemporalAnnotations.WithoutOverlaps] is string period)
            GenerateTemporalConstraint(operation.Name, operation.Table, operation.Schema, operation.Columns, "UNIQUE", period, builder, terminate: true);
        else
            base.Generate(operation, model, builder);
    }

    // Emits ALTER TABLE … ADD CONSTRAINT … <keyword> (cols…, period WITHOUT OVERLAPS). PostgreSQL
    // requires the range column last, so the period column is always emitted at the end regardless of
    // its position in the key.
    private void GenerateTemporalConstraint(
        string                      name,
        string                      table,
        string?                     schema,
        IReadOnlyList<string>       columns,
        string                      keyword,
        string                      periodColumn,
        MigrationCommandListBuilder builder,
        bool                        terminate
    )
    {
        var sqlHelper = Dependencies.SqlGenerationHelper;

        var rendered = columns.Where(c => c != periodColumn)
                              .Select(sqlHelper.DelimitIdentifier)
                              .ToList();
        rendered.Add($"{sqlHelper.DelimitIdentifier(periodColumn)} WITHOUT OVERLAPS");

        builder
           .Append("ALTER TABLE ")
           .Append(sqlHelper.DelimitIdentifier(table, schema))
           .Append(" ADD CONSTRAINT ")
           .Append(sqlHelper.DelimitIdentifier(name))
           .Append(" ")
           .Append(keyword)
           .Append(" (")
           .Append(string.Join(", ", rendered))
           .Append(")");

        if (terminate)
        {
            builder.AppendLine(sqlHelper.StatementTerminator);
            EndStatement(builder);
        }
    }

    private static IReadOnlyList<string>? ToStringList(object? value) =>
        value switch
        {
            null          => null,
            string[] s    => s,
            IEnumerable e => [.. e.Cast<object?>().Select(o => o?.ToString() ?? string.Empty)],
            _             => null
        };
}

#pragma warning restore EF1001