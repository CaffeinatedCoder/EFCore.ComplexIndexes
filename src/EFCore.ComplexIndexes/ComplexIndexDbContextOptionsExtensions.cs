using Microsoft.EntityFrameworkCore;
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
/// exclusion constraints.
/// </para>
/// </remarks>
public static class ComplexIndexDbContextOptionsExtensions
{
    extension(DbContextOptionsBuilder optionsBuilder)
    {
        /// <summary>
        /// Registers the complex-index differ at runtime, so <c>EnsureCreated()</c>,
        /// <c>GenerateCreateScript()</c> and <c>Migrate()</c>'s pending-model-changes check see the
        /// indexes declared with this package. Call it after the provider:
        /// <code>options.UseSqlite(connection).UseComplexIndexes();</code>
        /// </summary>
        public DbContextOptionsBuilder UseComplexIndexes()
            => optionsBuilder.ReplaceService<IMigrationsModelDiffer, CustomMigrationsModelDiffer>();
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
        /// <c>IServiceProvider</c> and pass it to <c>UseInternalServiceProvider</c>.
        /// </summary>
        public IServiceCollection AddComplexIndexes()
            => services.AddScoped<IMigrationsModelDiffer, CustomMigrationsModelDiffer>();
    }
}
