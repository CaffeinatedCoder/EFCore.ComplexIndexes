using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// Runtime wiring for expression indexes on PostgreSQL.
/// </summary>
public static class NpgsqlComplexIndexDbContextOptionsExtensions
{
    extension(DbContextOptionsBuilder optionsBuilder)
    {
        /// <summary>
        /// Replaces the migrations SQL generator with one that can render expression indexes
        /// defined via <c>HasExpressionIndex</c>. Call this after <c>UseNpgsql(...)</c>:
        /// <code>options.UseNpgsql(connectionString).UseNpgsqlComplexIndexes();</code>
        /// </summary>
        public DbContextOptionsBuilder UseNpgsqlComplexIndexes()
            => optionsBuilder.ReplaceService<IMigrationsSqlGenerator, NpgsqlComplexIndexSqlGenerator>();
    }
    
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the migrations SQL generator required for expression indexes 
        /// when using a custom internal service provider.
        /// </summary>
        public IServiceCollection AddNpgsqlComplexIndexes() 
            => services.AddScoped<IMigrationsSqlGenerator, NpgsqlComplexIndexSqlGenerator>();
    }
}
