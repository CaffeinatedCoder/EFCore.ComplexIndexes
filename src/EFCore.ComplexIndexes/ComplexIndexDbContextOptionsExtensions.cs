using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ComplexIndexes;

/// <summary>
/// Runtime wiring of the complex-index differ, for providers without a satellite package (SQLite, …).
/// </summary>
/// <remarks>
/// Migrations are scaffolded by the <em>design-time</em> differ, which the packaged <c>.targets</c>
/// wire up automatically. Two things run the <em>runtime</em> differ instead and never see that
/// wiring: <c>EnsureCreated()</c> / <c>GenerateCreateScript()</c>, which build the schema straight
/// from the model, and the pending-model-changes check <c>Migrate()</c> performs. Without this
/// registration both use EF's stock differ, which cannot see this package's declarations —
/// <c>EnsureCreated()</c> creates the tables without their complex indexes, silently, and
/// <c>Migrate()</c> does not warn about a complex index that was never scaffolded.
/// <para>
/// With a provider satellite installed, call that package's method instead
/// (<c>UseNpgsqlComplexIndexes()</c>, <c>UseSqlServerComplexIndexes()</c>): the core differ registered
/// here would give <c>EnsureCreated()</c> a schema without the satellite's features, such as
/// exclusion constraints, and it refuses a complex index that carries the provider's index options.
/// </para>
/// <para>
/// On SQLite the registration also wraps the provider's migrations SQL generator, which is why it
/// must follow <c>UseSqlite()</c>: SQLite rebuilds a table to alter or drop a column or change a
/// constraint, after every other operation, and drops the complex indexes on it. The wrapper creates
/// them again after the rebuild, and the differ scaffolds such a migration only when the context's
/// generator is the wrapper. It must be registered wherever the migration is applied.
/// </para>
/// </remarks>
public static class ComplexIndexDbContextOptionsExtensions
{
    extension(DbContextOptionsBuilder optionsBuilder)
    {
        /// <summary>
        /// Registers the complex-index differ at runtime, so <c>EnsureCreated()</c>,
        /// <c>GenerateCreateScript()</c> and <c>Migrate()</c>'s pending-model-changes check see the
        /// indexes declared with this package, and on SQLite the generator that re-creates them after
        /// a table rebuild. Call it after the provider:
        /// <code>options.UseSqlite(connection).UseComplexIndexes();</code>
        /// </summary>
        /// <exception cref="InvalidOperationException">EF Core's major version is not 10. This package
        /// serves EF Core 10 only; for EF Core 11 and later, use <c>CodoMetis.EFCore.SchemaKit</c>.</exception>
        public DbContextOptionsBuilder UseComplexIndexes()
        {
            EfCoreVersionGuard.ThrowIfUnsupported();
            ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(
                optionsBuilder.Options.FindExtension<ComplexIndexOptionsExtension>() ?? new ComplexIndexOptionsExtension());
            return optionsBuilder.ReplaceService<IMigrationsModelDiffer, CustomMigrationsModelDiffer>();
        }
    }

    extension<TContext>(DbContextOptionsBuilder<TContext> optionsBuilder) where TContext : DbContext
    {
        /// <inheritdoc cref="UseComplexIndexes(DbContextOptionsBuilder)"/>
        public DbContextOptionsBuilder<TContext> UseComplexIndexes()
            => (DbContextOptionsBuilder<TContext>)((DbContextOptionsBuilder)optionsBuilder).UseComplexIndexes();
    }

    extension(IServiceCollection services)
    {
        /// <summary>
        /// Registers the complex-index differ on a custom internal service provider — the equivalent
        /// of <c>UseComplexIndexes()</c> for applications that build their own
        /// <c>IServiceProvider</c> and pass it to <c>UseInternalServiceProvider</c>. Call it after
        /// <c>AddEntityFrameworkSqlite()</c>, whose migrations SQL generator it wraps.
        /// </summary>
        /// <exception cref="InvalidOperationException">EF Core's major version is not 10. This package
        /// serves EF Core 10 only; for EF Core 11 and later, use <c>CodoMetis.EFCore.SchemaKit</c>.</exception>
        public IServiceCollection AddComplexIndexes()
        {
            EfCoreVersionGuard.ThrowIfUnsupported();
            SqliteRebuildMigrationsSqlGenerator.Decorate(services);
            return services.AddScoped<IMigrationsModelDiffer, CustomMigrationsModelDiffer>();
        }
    }
}
