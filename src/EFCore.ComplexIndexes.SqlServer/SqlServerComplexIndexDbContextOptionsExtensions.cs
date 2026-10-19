using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ComplexIndexes.SqlServer;

/// <summary>
/// Optional runtime wiring for SQL Server. Migrations need none: every option renders through the
/// provider's own SQL generator. This registers the SQL Server differ at runtime so that
/// <c>EnsureCreated()</c> and <c>GenerateCreateScript()</c> include the complex indexes, and so that
/// <c>Migrate()</c>'s pending-model-changes check sees a complex index that was never scaffolded.
/// </summary>
public static class SqlServerComplexIndexDbContextOptionsExtensions
{
    extension(DbContextOptionsBuilder optionsBuilder)
    {
        /// <summary>
        /// Registers the SQL Server complex-index differ at runtime. Call it after the provider:
        /// <code>options.UseSqlServer(connectionString).UseSqlServerComplexIndexes();</code>
        /// </summary>
        /// <exception cref="InvalidOperationException">EF Core's major version is not 10. This package
        /// serves EF Core 10 only; for EF Core 11 and later, use <c>CodoMetis.EFCore.SchemaKit</c>.</exception>
        public DbContextOptionsBuilder UseSqlServerComplexIndexes()
        {
            EfCoreVersionGuard.ThrowIfUnsupported();
            return optionsBuilder.ReplaceService<IMigrationsModelDiffer, SqlServerComplexIndexMigrationsModelDiffer>();
        }
    }

    extension<TContext>(DbContextOptionsBuilder<TContext> optionsBuilder) where TContext : DbContext
    {
        /// <inheritdoc cref="UseSqlServerComplexIndexes(DbContextOptionsBuilder)"/>
        public DbContextOptionsBuilder<TContext> UseSqlServerComplexIndexes()
            => (DbContextOptionsBuilder<TContext>)((DbContextOptionsBuilder)optionsBuilder).UseSqlServerComplexIndexes();
    }

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the SQL Server complex-index differ on a custom internal service provider — the
        /// equivalent of <c>UseSqlServerComplexIndexes()</c> for applications that build their own
        /// <c>IServiceProvider</c> and pass it to <c>UseInternalServiceProvider</c>.
        /// </summary>
        /// <exception cref="InvalidOperationException">EF Core's major version is not 10. This package
        /// serves EF Core 10 only; for EF Core 11 and later, use <c>CodoMetis.EFCore.SchemaKit</c>.</exception>
        public IServiceCollection AddSqlServerComplexIndexes()
        {
            EfCoreVersionGuard.ThrowIfUnsupported();
            return services.AddScoped<IMigrationsModelDiffer, SqlServerComplexIndexMigrationsModelDiffer>();
        }
    }
}
