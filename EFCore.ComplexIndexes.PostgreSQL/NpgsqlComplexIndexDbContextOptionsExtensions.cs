using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// Runtime wiring for expression indexes on PostgreSQL.
/// </summary>
public static class NpgsqlComplexIndexDbContextOptionsExtensions
{
    /// <summary>
    /// Replaces the migrations SQL generator with one that can render expression indexes
    /// defined via <c>HasExpressionIndex</c>. Call this after <c>UseNpgsql(...)</c>:
    /// <code>options.UseNpgsql(connectionString).UseNpgsqlComplexIndexes();</code>
    /// </summary>
    public static DbContextOptionsBuilder UseNpgsqlComplexIndexes(this DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.ReplaceService<IMigrationsSqlGenerator, NpgsqlComplexIndexSqlGenerator>();
}
