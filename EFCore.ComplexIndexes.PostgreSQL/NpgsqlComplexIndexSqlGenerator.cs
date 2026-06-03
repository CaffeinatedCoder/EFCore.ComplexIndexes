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