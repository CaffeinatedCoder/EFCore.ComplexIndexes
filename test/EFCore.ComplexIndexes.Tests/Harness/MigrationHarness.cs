using System.Data.Common;
using EFCore.ComplexIndexes.PostgreSQL;
using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace EFCore.ComplexIndexes.Tests;

#pragma warning disable EF1001

/// <summary>
/// Shared rig for driving a migration differ from a <see cref="DbContext"/> and, where it matters,
/// rendering the resulting operations to SQL.
/// </summary>
/// <remarks>
/// Constructing a differ means resolving five EF Core internal services by hand, and the difference
/// between the stock provider generator and this package's is load-bearing for several features —
/// so both belong in one place rather than being re-derived per test class. Investigating a
/// suspected differ bug should be a three-line exercise:
/// <code>
/// var ops = MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel&lt;MyContext&gt;());
/// </code>
/// </remarks>
internal static class MigrationHarness
{
    public const string NpgsqlConnection    = "Host=localhost;Database=test";
    public const string SqlServerConnection = "Server=localhost;Database=test;Trusted_Connection=True";
    public const string SqliteConnection    = "DataSource=:memory:";

    /// <summary>A context with no model, used only to resolve provider services.</summary>
    public sealed class EmptyContext(DbContextOptions options) : DbContext(options);

    // ── Models ──

    public static IRelationalModel NpgsqlModel<TContext>(string? connection = null) where TContext : DbContext
        => BuildModel<TContext>(new DbContextOptionsBuilder<TContext>().UseNpgsql(connection ?? NpgsqlConnection).Options);

    public static IRelationalModel SqlServerModel<TContext>() where TContext : DbContext
        => BuildModel<TContext>(new DbContextOptionsBuilder<TContext>().UseSqlServer(SqlServerConnection).Options);

    public static IRelationalModel SqliteModel<TContext>() where TContext : DbContext
        => BuildModel<TContext>(new DbContextOptionsBuilder<TContext>().UseSqlite(SqliteConnection).Options);

    /// <summary>
    /// Overload for suites that hold one open in-memory SQLite connection for the lifetime of the
    /// test — the model must be built against that same connection, not a fresh <c>:memory:</c> one.
    /// </summary>
    public static IRelationalModel SqliteModel<TContext>(DbConnection connection) where TContext : DbContext
        => BuildModel<TContext>(new DbContextOptionsBuilder<TContext>().UseSqlite(connection).Options);

    private static IRelationalModel BuildModel<TContext>(DbContextOptions<TContext> options) where TContext : DbContext
    {
        using var context = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        return context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
    }

    // ── Diffs ──

    public static IReadOnlyList<MigrationOperation> NpgsqlDiff(
        IRelationalModel? source, IRelationalModel? target, string? connection = null)
        => Diff<NpgsqlComplexIndexMigrationsModelDiffer>(NpgsqlOptions(connection), source, target);

    public static IReadOnlyList<MigrationOperation> SqlServerDiff(IRelationalModel? source, IRelationalModel? target)
        => Diff<SqlServerComplexIndexMigrationsModelDiffer>(
               new DbContextOptionsBuilder().UseSqlServer(SqlServerConnection).Options, source, target);

    /// <summary>Diffs with the provider-agnostic core differ (over SQLite, which has no satellite).</summary>
    public static IReadOnlyList<MigrationOperation> CoreDiff(IRelationalModel? source, IRelationalModel? target)
        => Diff<CustomMigrationsModelDiffer>(
               new DbContextOptionsBuilder().UseSqlite(SqliteConnection).Options, source, target);

    /// <inheritdoc cref="SqliteModel{TContext}(DbConnection)"/>
    public static IReadOnlyList<MigrationOperation> CoreDiff(
        DbConnection connection, IRelationalModel? source, IRelationalModel? target)
        => Diff<CustomMigrationsModelDiffer>(
               new DbContextOptionsBuilder().UseSqlite(connection).Options, source, target);

    public static IReadOnlyList<MigrationOperation> Diff<TDiffer>(
        DbContextOptions options, IRelationalModel? source, IRelationalModel? target)
        where TDiffer : CustomMigrationsModelDiffer
    {
        using var context = new EmptyContext(options);
        return CreateDiffer<TDiffer>(context).GetDifferences(source, target);
    }

    /// <summary>
    /// Diffs and hands back the differ itself, for tests that assert on a subclass's own state
    /// (e.g. recording which operations reached a validation hook).
    /// </summary>
    public static (TDiffer Differ, IReadOnlyList<MigrationOperation> Operations) DiffWith<TDiffer>(
        DbContextOptions options, IRelationalModel? source, IRelationalModel? target)
        where TDiffer : CustomMigrationsModelDiffer
    {
        using var context = new EmptyContext(options);
        var differ = CreateDiffer<TDiffer>(context);
        return (differ, differ.GetDifferences(source, target));
    }

    public static TDiffer CreateDiffer<TDiffer>(DbContext context) where TDiffer : CustomMigrationsModelDiffer
        => (TDiffer)Activator.CreateInstance(
               typeof(TDiffer),
               context.GetService<IRelationalTypeMappingSource>(),
               context.GetService<IMigrationsAnnotationProvider>(),
               context.GetService<IRelationalAnnotationProvider>(),
               context.GetService<IRowIdentityMapFactory>(),
               context.GetService<CommandBatchPreparerDependencies>())!;

    // ── SQL ──

    /// <summary>
    /// Renders operations to SQL. <paramref name="complexIndexWiring"/> selects between this
    /// package's generator and the stock Npgsql one — the distinction that decides whether a
    /// feature degrades silently when a consumer forgets <c>UseNpgsqlComplexIndexes()</c>.
    /// </summary>
    public static string NpgsqlSql(
        IEnumerable<MigrationOperation> operations, bool complexIndexWiring = true, string? connection = null)
    {
        using var context = new EmptyContext(NpgsqlOptions(connection, complexIndexWiring));
        return Render(context, operations);
    }

    public static string SqlServerSql(IEnumerable<MigrationOperation> operations)
    {
        using var context = new EmptyContext(new DbContextOptionsBuilder().UseSqlServer(SqlServerConnection).Options);
        return Render(context, operations);
    }

    private static string Render(DbContext context, IEnumerable<MigrationOperation> operations)
        => string.Join("\n", context.GetService<IMigrationsSqlGenerator>()
                                    .Generate([.. operations], model: null)
                                    .Select(command => command.CommandText));

    public static DbContextOptions SqliteOptions(DbConnection connection)
        => new DbContextOptionsBuilder().UseSqlite(connection).Options;

    public static DbContextOptions NpgsqlOptions(string? connection = null, bool complexIndexWiring = false)
    {
        var builder = new DbContextOptionsBuilder().UseNpgsql(connection ?? NpgsqlConnection);
        if (complexIndexWiring)
            builder.UseNpgsqlComplexIndexes();
        return builder.Options;
    }
}

#pragma warning restore EF1001
