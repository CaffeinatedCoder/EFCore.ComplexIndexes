using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// End-to-end proof against a real PostgreSQL 18 (Testcontainers): the differ's operations render
/// to DDL that actually applies, and the resulting constraints/indexes actually enforce. Marked
/// Integration; the whole class goes inconclusive when Docker is unavailable.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize] // shared container/database; concurrent CREATE EXTENSION IF NOT EXISTS races in PG
public class PostgresIntegrationTests
{
    private static PostgreSqlContainer? _container;
    private static string?              _connectionString;
    private static string?              _unavailableReason;

    [ClassInitialize]
    public static async Task StartContainer(TestContext _)
    {
        try
        {
            _container = new PostgreSqlBuilder().WithImage("postgres:18").Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
        }
        catch (Exception e)
        {
            _unavailableReason = $"Docker/PostgreSQL container unavailable: {e.Message}";
        }
    }

    [ClassCleanup]
    public static async Task StopContainer()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private static string ConnectionString
    {
        get
        {
            if (_connectionString is null)
                Assert.Inconclusive(_unavailableReason ?? "Container not started.");
            return _connectionString!;
        }
    }

    // ── Harness: model → differ → SQL generator → live database ──

    private static IRelationalModel BuildRelationalModel<TContext>() where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
                     .UseNpgsql(ConnectionString)
                     .Options;

        using var context = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        return context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
    }

    private static IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        var options = new DbContextOptionsBuilder().UseNpgsql(ConnectionString).Options;
        using var context = new EmptyContext(options);

        var differ = new NpgsqlComplexIndexMigrationsModelDiffer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<IMigrationsAnnotationProvider>(),
            context.GetService<IRelationalAnnotationProvider>(),
            context.GetService<IRowIdentityMapFactory>(),
            context.GetService<CommandBatchPreparerDependencies>()
        );

        return differ.GetDifferences(source, target);
    }

    private static void Apply(IReadOnlyList<MigrationOperation> operations)
    {
        var options = new DbContextOptionsBuilder()
                     .UseNpgsql(ConnectionString)
                     .UseNpgsqlComplexIndexes()
                     .Options;

        using var context   = new EmptyContext(options);
        var       generator = context.GetService<IMigrationsSqlGenerator>();
        var       commands  = generator.Generate(operations, model: null);

        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        foreach (var command in commands)
        {
            using var cmd = new NpgsqlCommand(command.CommandText, connection);
            cmd.ExecuteNonQuery();
        }
    }

    private static void Migrate<TContext>() where TContext : DbContext
        => Apply(GetDifferences(source: null, target: BuildRelationalModel<TContext>()));

    private static void Sql(string sql)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static PostgresException AssertRejected(string sql)
    {
        var exception = Assert.ThrowsExactly<PostgresException>(() => Sql(sql));
        StringAssert.StartsWith(exception.SqlState, "23", "Expected an integrity-constraint violation.");
        return exception;
    }

    private class EmptyContext(DbContextOptions options) : DbContext(options);

    // ── Exclusion constraint: enforces overlap protection, respects the WHERE predicate ──

    private class RoleGrant
    {
        public int                   Id        { get; set; }
        public int                   GranteeId { get; set; }
        public int                   RoleId    { get; set; }
        public NpgsqlRange<DateOnly> Period    { get; set; }
        public DateOnly?             RevokedAt { get; set; }
    }

    private class GrantContext(DbContextOptions<GrantContext> options) : DbContext(options)
    {
        public DbSet<RoleGrant> Grants => Set<RoleGrant>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<RoleGrant>(b =>
            {
                b.ToTable("ig_role_grants");
                b.HasKey(x => x.Id);
                b.Property(x => x.GranteeId).HasColumnName("grantee_id");
                b.Property(x => x.RoleId).HasColumnName("role_id");
                b.Property(x => x.Period).HasColumnName("period");
                b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
                b.HasExclusionConstraint(
                    equalityColumns: x => new { x.GranteeId, x.RoleId },
                    overlapsColumn:  x => x.Period,
                    filter:          "revoked_at IS NULL",
                    name:            "ex_ig_role_grants_active");
            });
    }

    [TestMethod(DisplayName = "Exclusion constraint enforces overlap protection and respects the filter")]
    public void Exclusion_constraint_enforces_and_respects_filter()
    {
        Migrate<GrantContext>();

        Sql("INSERT INTO ig_role_grants (\"Id\", grantee_id, role_id, period) VALUES (1, 1, 1, '[2024-01-01,2024-06-01)')");

        // Same grantee+role, overlapping period → rejected by the constraint.
        AssertRejected("INSERT INTO ig_role_grants (\"Id\", grantee_id, role_id, period) VALUES (2, 1, 1, '[2024-03-01,2024-09-01)')");

        // Different role → no conflict.
        Sql("INSERT INTO ig_role_grants (\"Id\", grantee_id, role_id, period) VALUES (3, 1, 2, '[2024-03-01,2024-09-01)')");

        // Overlapping but revoked → excluded by the WHERE predicate, so it is allowed.
        Sql("INSERT INTO ig_role_grants (\"Id\", grantee_id, role_id, period, revoked_at) VALUES (4, 1, 1, '[2024-03-01,2024-09-01)', '2024-04-01')");
    }

    // ── Temporal constraint (PG 18 WITHOUT OVERLAPS) ──

    private class RoomBooking
    {
        public int                   Id           { get; set; }
        public int                   RoomId       { get; set; }
        public NpgsqlRange<DateOnly> BookedDuring { get; set; }
    }

    private class BookingContext(DbContextOptions<BookingContext> options) : DbContext(options)
    {
        public DbSet<RoomBooking> Bookings => Set<RoomBooking>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<RoomBooking>(b =>
            {
                b.ToTable("ig_bookings");
                b.HasKey(x => x.Id);
                b.Property(x => x.RoomId).HasColumnName("room_id");
                b.Property(x => x.BookedDuring).HasColumnName("booked_during");
                b.HasTemporalConstraint(x => x.RoomId, x => x.BookedDuring);
            });
    }

    [TestMethod(DisplayName = "Temporal UNIQUE … WITHOUT OVERLAPS applies and enforces")]
    public void Temporal_constraint_enforces_without_overlaps()
    {
        Migrate<BookingContext>();

        Sql("INSERT INTO ig_bookings (\"Id\", room_id, booked_during) VALUES (1, 7, '[2024-01-01,2024-02-01)')");
        Sql("INSERT INTO ig_bookings (\"Id\", room_id, booked_during) VALUES (2, 7, '[2024-02-01,2024-03-01)')");

        AssertRejected("INSERT INTO ig_bookings (\"Id\", room_id, booked_during) VALUES (3, 7, '[2024-01-15,2024-02-15)')");
    }

    // ── Expression index: renders, applies, and enforces case-insensitive uniqueness ──

    private class Person
    {
        public int    Id    { get; set; }
        public string Email { get; set; } = "";
    }

    private class PersonContext(DbContextOptions<PersonContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                b.ToTable("ig_people");
                b.HasKey(x => x.Id);
                b.Property(x => x.Email).HasColumnName("email");
                b.HasExpressionIndex("lower(email)", isUnique: true, indexName: "ux_ig_people_email_ci");
            });
    }

    [TestMethod(DisplayName = "Unique expression index applies and enforces case-insensitively")]
    public void Expression_index_applies_and_enforces_uniqueness()
    {
        Migrate<PersonContext>();

        Sql("INSERT INTO ig_people (\"Id\", email) VALUES (1, 'A@example.com')");
        AssertRejected("INSERT INTO ig_people (\"Id\", email) VALUES (2, 'a@EXAMPLE.com')");
    }

    // ── The AuditOffice regression: native HasIndex ⇄ HasComplexIndex round-trips cleanly ──

    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class CustomerFlat
    {
        public int    Id    { get; set; }
        public string Email { get; set; } = "";
    }

    private class CustomerComplex
    {
        public int          Id           { get; set; }
        public EmailAddress EmailAddress { get; set; } = new();
    }

    private class CustomerV1Context(DbContextOptions<CustomerV1Context> options) : DbContext(options)
    {
        public DbSet<CustomerFlat> Customers => Set<CustomerFlat>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<CustomerFlat>(b =>
            {
                b.ToTable("ig_customers");
                b.HasKey(x => x.Id);
                b.Property(x => x.Email).HasColumnName("email");
                b.HasIndex(x => x.Email).HasDatabaseName("ix_ig_customers_email");
            });
    }

    private class CustomerV2Context(DbContextOptions<CustomerV2Context> options) : DbContext(options)
    {
        public DbSet<CustomerComplex> Customers => Set<CustomerComplex>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<CustomerComplex>(b =>
            {
                b.ToTable("ig_customers");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.EmailAddress, c =>
                    c.Property(x => x.Value).HasColumnName("email")
                     .HasComplexIndex(indexName: "ix_ig_customers_email"));
            });
    }

    [TestMethod(DisplayName = "Native ⇄ complex index migration applies cleanly in both directions")]
    public void Native_and_complex_index_roundtrip_applies_cleanly()
    {
        var v1 = BuildRelationalModel<CustomerV1Context>();
        var v2 = BuildRelationalModel<CustomerV2Context>();

        Apply(GetDifferences(source: null, target: v1));

        // Native → complex: base drops the native index, we create ours.
        Apply(GetDifferences(source: v1, target: v2));

        // Complex → native: the v4 collision direction — our drop must precede the base create.
        Apply(GetDifferences(source: v2, target: v1));

        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_indexes WHERE indexname = 'ix_ig_customers_email'", connection);
        Assert.AreEqual(1L, cmd.ExecuteScalar());
    }
}
