using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

public partial class PostgresIntegrationTests
{
    // ── UseComplexIndexes() where UseNpgsqlComplexIndexes() belongs (#150) ──

    private class CoreGrant
    {
        public int                   Id        { get; set; }
        public int                   GranteeId { get; set; }
        public NpgsqlRange<DateOnly> Period    { get; set; }
    }

    private class CoreGrantUse
    {
        public int                   Id        { get; set; }
        public int                   GranteeId { get; set; }
        public NpgsqlRange<DateOnly> Period    { get; set; }
    }

    private abstract class CoreRegistrationContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<CoreGrant>(b =>
            {
                b.ToTable("ig_core_grants");
                b.HasComplexIndex(x => x.GranteeId, indexName: "ix_ig_core_grantee");
                Declare(modelBuilder);
            });

        protected abstract void Declare(ModelBuilder modelBuilder);
    }

    private class CoreExclusionContext(DbContextOptions<CoreExclusionContext> options) : CoreRegistrationContext(options)
    {
        protected override void Declare(ModelBuilder modelBuilder)
            => modelBuilder.Entity<CoreGrant>().HasExclusionConstraint(x => x.GranteeId, x => x.Period, name: "ig_core_constraint");
    }

    private class CoreTemporalContext(DbContextOptions<CoreTemporalContext> options) : CoreRegistrationContext(options)
    {
        protected override void Declare(ModelBuilder modelBuilder)
            => modelBuilder.Entity<CoreGrant>().HasTemporalConstraint(x => x.GranteeId, x => x.Period, name: "ig_core_constraint");
    }

    private class CoreTemporalForeignKeyContext(DbContextOptions<CoreTemporalForeignKeyContext> options) : CoreRegistrationContext(options)
    {
        protected override void Declare(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CoreGrant>().HasTemporalConstraint(x => x.GranteeId, x => x.Period, name: "ig_core_principal");
            modelBuilder.Entity<CoreGrantUse>(b =>
            {
                b.ToTable("ig_core_uses");
                b.HasTemporalForeignKey<CoreGrantUse, CoreGrant>(x => x.GranteeId, x => x.Period, x => x.GranteeId, x => x.Period, name: "ig_core_constraint");
            });
        }
    }

    /// <summary>
    /// With <c>UseComplexIndexes()</c> in place of <c>UseNpgsqlComplexIndexes()</c>, the core differ
    /// ran and knew nothing of the satellite's constraints: <c>EnsureCreated()</c> created the table
    /// and the complex index, applied cleanly, and left the constraint out. It now refuses before
    /// anything is created; the satellite's registration creates the constraint.
    /// </summary>
    [TestMethod(DisplayName = "UseComplexIndexes() refuses a model with a PostgreSQL constraint at EnsureCreated, where it applied without it")]
    [DataRow("exclusion")]
    [DataRow("temporal")]
    [DataRow("temporal_fk")]
    public void Core_registration_refuses_satellite_constraints(string kind)
    {
        switch (kind)
        {
            case "exclusion":   AssertCoreRegistrationRefuses<CoreExclusionContext>(kind, "x"); break;
            case "temporal":    AssertCoreRegistrationRefuses<CoreTemporalContext>(kind, "u"); break;
            case "temporal_fk": AssertCoreRegistrationRefuses<CoreTemporalForeignKeyContext>(kind, "f"); break;
            default:            Assert.Fail(kind); break;
        }
    }

    private static void AssertCoreRegistrationRefuses<TContext>(string kind, string constraintType) where TContext : DbContext
    {
        var core      = FreshDatabase($"ig_core_{kind}");
        var satellite = FreshDatabase($"ig_core_{kind}_satellite");
        var declared  = $"SELECT count(*) FROM pg_constraint WHERE conname = 'ig_core_constraint' AND contype = '{constraintType}'";

        using (var context = Create(new DbContextOptionsBuilder<TContext>().UseNpgsql(core).UseComplexIndexes().Options))
        {
            var refusal = Record(() => context.Database.EnsureCreated());

            Assert.IsNotNull(refusal,
                $"EnsureCreated() applied cleanly under UseComplexIndexes(), with {Count(core, declared)} of the declared constraint "
              + $"and {Count(core, "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_ig_core_grantee'")} complex index.");
            StringAssert.Contains(refusal.Message, "call UseNpgsqlComplexIndexes() instead of UseComplexIndexes()");
            Assert.AreEqual(0L, Count(core, "SELECT count(*) FROM pg_tables WHERE tablename LIKE 'ig_core_%'"), "The refusal came after tables were created.");
        }

        using (var context = Create(new DbContextOptionsBuilder<TContext>().UseNpgsql(satellite).UseNpgsqlComplexIndexes().Options))
            Assert.IsTrue(context.Database.EnsureCreated());

        Assert.AreEqual(1L, Count(satellite, declared), "UseNpgsqlComplexIndexes() did not create the constraint.");

        static TContext Create(DbContextOptions<TContext> options) => (TContext)Activator.CreateInstance(typeof(TContext), options)!;

        static InvalidOperationException? Record(Action action)
        {
            try { action(); return null; }
            catch (InvalidOperationException e) { return e; }
        }
    }

    private static string FreshDatabase(string name)
    {
        Sql($"CREATE DATABASE {name}");
        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

    private static long Count(string connectionString, string sql)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
