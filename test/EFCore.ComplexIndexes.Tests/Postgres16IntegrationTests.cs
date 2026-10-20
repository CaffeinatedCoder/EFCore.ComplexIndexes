using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// Exclusion constraints against a real PostgreSQL 16, the oldest version they were reported
/// failing on. Each test applies into a database of its own, created empty: in the shared
/// database of <see cref="PostgresIntegrationTests"/> an earlier test has usually installed
/// <c>btree_gist</c> already, which hides a migration that would not have installed it itself.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public class Postgres16IntegrationTests
{
    private static PostgreSqlContainer? _container;
    private static string?              _connectionString;
    private static string?              _unavailableReason;

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static async Task StartContainer(TestContext _)
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:16").Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception e)
        {
            _unavailableReason = $"Docker/PostgreSQL 16 container unavailable: {e.Message}";
        }
    }

    [ClassCleanup]
    public static async Task StopContainer()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    // A fresh database per test, named after it, so no extension carries over between tests.
    private string FreshDatabase()
    {
        var server   = PostgresIntegrationTests.RequireDatabase(_connectionString, _unavailableReason);
        var database = "pg16_" + TestContext.TestName!.ToLowerInvariant();

        using (var connection = new NpgsqlConnection(server))
        {
            connection.Open();
            using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {database}", connection);
            drop.ExecuteNonQuery();
            using var create = new NpgsqlCommand($"CREATE DATABASE {database}", connection);
            create.ExecuteNonQuery();
        }

        return new NpgsqlConnectionStringBuilder(server) { Database = database }.ConnectionString;
    }

    // Applied with the stock generator: exclusion DDL is rendered at design time.
    private static void Migrate<TContext>(string connectionString) where TContext : DbContext
    {
        var operations = MigrationHarness.NpgsqlDiff(
            source: null, target: MigrationHarness.NpgsqlModel<TContext>(connectionString), connectionString);

        using var context  = new MigrationHarness.EmptyContext(MigrationHarness.NpgsqlOptions(connectionString));
        var       commands = context.GetService<IMigrationsSqlGenerator>().Generate([.. operations], model: null);

        foreach (var command in commands)
            Sql(connectionString, command.CommandText);
    }

    private static void Sql(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static bool HasBtreeGist(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_extension WHERE extname = 'btree_gist'", connection);
        return (long)cmd.ExecuteScalar()! > 0;
    }

    private class Shift
    {
        public int                   Id       { get; set; }
        public int                   DeskId   { get; set; }
        public NpgsqlRange<DateOnly> Period   { get; set; }
    }

    private static void MapShift(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Shift> b)
    {
        b.ToTable("shifts");
        b.HasKey(x => x.Id);
        b.Property(x => x.DeskId).HasColumnName("desk_id");
        b.Property(x => x.Period).HasColumnName("period");
    }

    // "One desk at a time": overlapping periods must share a desk.
    private class NotEqualContext(DbContextOptions<NotEqualContext> options) : DbContext(options)
    {
        public DbSet<Shift> Shifts => Set<Shift>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Shift>(b =>
            {
                MapShift(b);
                b.HasExclusionConstraint(ex => ex.WithColumn(x => x.DeskId, "<>").WithOverlaps(x => x.Period));
            });
    }

    private class MixedCaseMethodContext(DbContextOptions<MixedCaseMethodContext> options) : DbContext(options)
    {
        public DbSet<Shift> Shifts => Set<Shift>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Shift>(b =>
            {
                MapShift(b);
                b.HasExclusionConstraint(ex => ex.WithEquality(x => x.DeskId).WithOverlaps(x => x.Period).UseMethod("GiST"));
            });
    }

    private class RangeOnlyContext(DbContextOptions<RangeOnlyContext> options) : DbContext(options)
    {
        public DbSet<Shift> Shifts => Set<Shift>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Shift>(b =>
            {
                MapShift(b);
                b.HasExclusionConstraint(ex => ex.WithEquality(x => x.Period).WithOverlaps(x => x.Period));
            });
    }

    [TestMethod(DisplayName = "PostgreSQL 16: a scalar element compared with <> applies and enforces")]
    public void Not_equal_scalar_element_applies()
    {
        var database = FreshDatabase();

        // Failed with "data type integer has no default operator class for access method gist"
        // while btree_gist was injected only for '='.
        Migrate<NotEqualContext>(database);

        Sql(database, "INSERT INTO shifts (\"Id\", desk_id, period) VALUES (1, 1, '[2024-01-01,2024-02-01)')");
        Sql(database, "INSERT INTO shifts (\"Id\", desk_id, period) VALUES (2, 1, '[2024-01-15,2024-02-15)')");

        var rejected = Assert.ThrowsExactly<PostgresException>(() =>
            Sql(database, "INSERT INTO shifts (\"Id\", desk_id, period) VALUES (3, 2, '[2024-01-20,2024-01-25)')"));
        Assert.AreEqual(PostgresErrorCodes.ExclusionViolation, rejected.SqlState);
    }

    [TestMethod(DisplayName = "PostgreSQL 16: UseMethod(\"GiST\") gets btree_gist and applies")]
    public void Mixed_case_method_applies()
    {
        var database = FreshDatabase();

        Migrate<MixedCaseMethodContext>(database);

        Assert.IsTrue(HasBtreeGist(database));
    }

    [TestMethod(DisplayName = "PostgreSQL 16: range elements apply without btree_gist")]
    public void Range_elements_apply_without_extension()
    {
        var database = FreshDatabase();

        Migrate<RangeOnlyContext>(database);

        Assert.IsFalse(HasBtreeGist(database), "Ranges have a GiST operator class of their own.");
    }
}
