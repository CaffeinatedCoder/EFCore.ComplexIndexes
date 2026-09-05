using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// Runtime wiring for PostgreSQL: the SQL generator that renders expression indexes and
/// <c>NULLS FIRST/LAST</c> when migrations are applied, and the differ that lets
/// <c>EnsureCreated()</c>, <c>GenerateCreateScript()</c> and <c>Migrate()</c>'s
/// pending-model-changes check see this package's declarations.
/// </summary>
public static class NpgsqlComplexIndexDbContextOptionsExtensions
{
    extension(DbContextOptionsBuilder optionsBuilder)
    {
        /// <summary>
        /// Replaces the migrations SQL generator with one that can render expression indexes
        /// defined via <c>HasExpressionIndex</c> and per-column null ordering, and registers the
        /// PostgreSQL complex-index differ at runtime so <c>EnsureCreated()</c> builds the declared
        /// indexes and constraints. Call this after <c>UseNpgsql(...)</c>:
        /// <code>options.UseNpgsql(connectionString).UseNpgsqlComplexIndexes();</code>
        /// </summary>
        public DbContextOptionsBuilder UseNpgsqlComplexIndexes()
            => optionsBuilder
              .ReplaceService<IMigrationsSqlGenerator, NpgsqlComplexIndexSqlGenerator>()
              .ReplaceService<IMigrationsModelDiffer, NpgsqlComplexIndexMigrationsModelDiffer>();
    }

    extension<TContext>(DbContextOptionsBuilder<TContext> optionsBuilder) where TContext : DbContext
    {
        /// <inheritdoc cref="UseNpgsqlComplexIndexes(DbContextOptionsBuilder)"/>
        public DbContextOptionsBuilder<TContext> UseNpgsqlComplexIndexes()
            => (DbContextOptionsBuilder<TContext>)((DbContextOptionsBuilder)optionsBuilder).UseNpgsqlComplexIndexes();
    }

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the migrations SQL generator and the PostgreSQL complex-index differ on a
        /// custom internal service provider — the equivalent of <c>UseNpgsqlComplexIndexes()</c>
        /// for applications that build their own <c>IServiceProvider</c>.
        /// </summary>
        public IServiceCollection AddNpgsqlComplexIndexes()
            => services
              .AddScoped<IMigrationsSqlGenerator, NpgsqlComplexIndexSqlGenerator>()
              .AddScoped<IMigrationsModelDiffer, NpgsqlComplexIndexMigrationsModelDiffer>();
    }
}
