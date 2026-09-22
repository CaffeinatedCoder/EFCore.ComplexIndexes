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
            _container = new PostgreSqlBuilder("postgres:18").Build();
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
            if (_connectionString is not null)
                return _connectionString;

            var reason = _unavailableReason ?? "Container not started.";

            // Locally, no Docker means "skip" — that is a reasonable default for a laptop. On CI it
            // must be a hard failure: going inconclusive there lets a broken Docker setup quietly
            // retire the entire end-to-end layer while the build stays green, which is the exact
            // silent pass this suite exists to prevent.
            if (RunningOnCi)
                Assert.Fail($"Integration tests are mandatory on CI, but the database was unavailable. {reason}");

            Assert.Inconclusive(reason);
            return null!; // unreachable — Assert.Inconclusive throws
        }
    }

    private static bool RunningOnCi =>
        Environment.GetEnvironmentVariable("CI") is { Length: > 0 } value
     && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    // ── Harness: model → differ → SQL generator → live database ──

    // The live container's connection string is threaded through deliberately: Npgsql derives
    // server-version-dependent behaviour from it, so the model must be built against the same
    // server the DDL is then applied to.
    private static IRelationalModel BuildRelationalModel<TContext>() where TContext : DbContext
        => MigrationHarness.NpgsqlModel<TContext>(ConnectionString);

    private static IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
        => MigrationHarness.NpgsqlDiff(source, target, ConnectionString);

    private static void Apply(IReadOnlyList<MigrationOperation> operations, bool wireUpGenerator = true)
    {
        var builder = new DbContextOptionsBuilder().UseNpgsql(ConnectionString);
        if (wireUpGenerator)
            builder.UseNpgsqlComplexIndexes();

        using var context   = new MigrationHarness.EmptyContext(builder.Options);
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

    /// <summary>
    /// Applies with the <em>stock</em> Npgsql generator — no <c>UseNpgsqlComplexIndexes()</c>.
    /// Features whose DDL is rendered at design time (temporal and exclusion constraints) must
    /// still apply, and apply correctly.
    /// </summary>
    private static void MigrateWithoutRuntimeWiring<TContext>() where TContext : DbContext
        => Apply(GetDifferences(source: null, target: BuildRelationalModel<TContext>()), wireUpGenerator: false);

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

    // Two constraints over the same columns, partitioned by their filters — the shape that a
    // filter-blind identity rule used to collapse into one.
    private class SplitGrantContext(DbContextOptions<SplitGrantContext> options) : DbContext(options)
    {
        public DbSet<RoleGrant> Grants => Set<RoleGrant>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<RoleGrant>(b =>
            {
                b.ToTable("ig_split_grants");
                b.HasKey(x => x.Id);
                b.Property(x => x.GranteeId).HasColumnName("grantee_id");
                b.Property(x => x.RoleId).HasColumnName("role_id");
                b.Property(x => x.Period).HasColumnName("period");
                b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
                b.HasExclusionConstraint(x => x.GranteeId, x => x.Period,
                                         filter: "revoked_at IS NULL",     name: "ex_ig_split_active");
                b.HasExclusionConstraint(x => x.GranteeId, x => x.Period,
                                         filter: "revoked_at IS NOT NULL", name: "ex_ig_split_revoked");
            });
    }

    [TestMethod(DisplayName = "Two filtered exclusion constraints over the same columns both apply and enforce")]
    public void Split_exclusion_constraints_both_enforce()
    {
        Migrate<SplitGrantContext>();

        // Non-overlapping across the two partitions: an active grant and a revoked one may overlap.
        Sql("INSERT INTO ig_split_grants (\"Id\", grantee_id, role_id, period) VALUES (1, 1, 1, '[2024-01-01,2024-06-01)')");
        Sql("INSERT INTO ig_split_grants (\"Id\", grantee_id, role_id, period, revoked_at) VALUES (2, 1, 1, '[2024-03-01,2024-09-01)', '2024-04-01')");

        // Each constraint still guards its own partition.
        AssertRejected("INSERT INTO ig_split_grants (\"Id\", grantee_id, role_id, period) VALUES (3, 1, 1, '[2024-05-01,2024-08-01)')");
        AssertRejected("INSERT INTO ig_split_grants (\"Id\", grantee_id, role_id, period, revoked_at) VALUES (4, 1, 1, '[2024-08-01,2024-10-01)', '2024-09-01')");
    }

    // Same table as GrantContext but without the declarative constraint — the "before adoption"
    // model whose migration created the table while the constraint was hand-written SQL.
    private class PlainGrantContext(DbContextOptions<PlainGrantContext> options) : DbContext(options)
    {
        public DbSet<RoleGrant> Grants => Set<RoleGrant>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<RoleGrant>(b =>
            {
                b.ToTable("ig_adopted_grants");
                b.HasKey(x => x.Id);
                b.Property(x => x.GranteeId).HasColumnName("grantee_id");
                b.Property(x => x.RoleId).HasColumnName("role_id");
                b.Property(x => x.Period).HasColumnName("period");
                b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            });
    }

    private class AdoptedGrantContext(DbContextOptions<AdoptedGrantContext> options) : DbContext(options)
    {
        public DbSet<RoleGrant> Grants => Set<RoleGrant>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<RoleGrant>(b =>
            {
                b.ToTable("ig_adopted_grants");
                b.HasKey(x => x.Id);
                b.Property(x => x.GranteeId).HasColumnName("grantee_id");
                b.Property(x => x.RoleId).HasColumnName("role_id");
                b.Property(x => x.Period).HasColumnName("period");
                b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
                b.HasExclusionConstraint(
                    equalityColumns: x => new { x.GranteeId, x.RoleId },
                    overlapsColumn:  x => x.Period,
                    filter:          "revoked_at IS NULL",
                    name:            "ex_adopted_active");
            });
    }

    [TestMethod(DisplayName = "Adopting a pre-existing hand-written constraint applies without 42P07")]
    public void Exclusion_adoption_over_existing_constraint_applies()
    {
        // Original state: table created by migrations, constraint hand-written as raw SQL.
        Migrate<PlainGrantContext>();
        Sql("CREATE EXTENSION IF NOT EXISTS btree_gist");
        Sql("ALTER TABLE ig_adopted_grants ADD CONSTRAINT ex_adopted_active " +
            "EXCLUDE USING gist (grantee_id WITH =, role_id WITH =, period WITH &&) " +
            "WHERE (revoked_at IS NULL)");

        // Adoption migration: the declarative constraint diffs in over the existing one.
        Apply(GetDifferences(
            source: BuildRelationalModel<PlainGrantContext>(),
            target: BuildRelationalModel<AdoptedGrantContext>()));

        // Still enforced after the drop/re-add.
        Sql("INSERT INTO ig_adopted_grants (\"Id\", grantee_id, role_id, period) VALUES (1, 1, 1, '[2024-01-01,2024-06-01)')");
        AssertRejected("INSERT INTO ig_adopted_grants (\"Id\", grantee_id, role_id, period) VALUES (2, 1, 1, '[2024-03-01,2024-09-01)')");
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

    [TestMethod(DisplayName = "Temporal UNIQUE … WITHOUT OVERLAPS applies and enforces without runtime wiring")]
    public void Temporal_constraint_enforces_without_overlaps()
    {
        // Deliberately no UseNpgsqlComplexIndexes(): the temporal DDL is baked into the migration at
        // design time. Before that, the stock generator emitted a plain `UNIQUE (room_id,
        // booked_during)` — valid DDL that applied cleanly and let the overlapping row below through.
        MigrateWithoutRuntimeWiring<BookingContext>();

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

    // ── JSON-member index: unique index on a ToJson complex member ──

    private class CompanyName
    {
        public string ShortName { get; set; } = "";
        public string LegalName { get; set; } = "";
    }

    private class Employer
    {
        public int         Id   { get; set; }
        public CompanyName Name { get; set; } = new();
    }

    private class EmployerContext(DbContextOptions<EmployerContext> options) : DbContext(options)
    {
        public DbSet<Employer> Employers => Set<Employer>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Employer>(b =>
            {
                b.ToTable("ig_employers");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Name, c => c.ToJson("name"));
                b.HasComplexIndex(x => x.Name.ShortName, isUnique: true, indexName: "ux_ig_employers_short_name");
            });
    }

    [TestMethod(DisplayName = "Unique index on a ToJson complex member applies and enforces")]
    public void Json_member_index_applies_and_enforces()
    {
        Migrate<EmployerContext>();

        Sql("""INSERT INTO ig_employers ("Id", name) VALUES (1, '{"ShortName":"ACME","LegalName":"Acme Corp."}')""");
        AssertRejected("""INSERT INTO ig_employers ("Id", name) VALUES (2, '{"ShortName":"ACME","LegalName":"Acme Holdings"}')""");
        Sql("""INSERT INTO ig_employers ("Id", name) VALUES (3, '{"ShortName":"Globex","LegalName":"Globex GmbH"}')""");
    }

    // ── JSON-member indexes are used by EF Core's own queries ──

    private enum Plan { Basic, Pro }

    private class Location
    {
        public string City { get; set; } = "";
        public int    Zip  { get; set; }
    }

    private class Settings
    {
        public int      Rank     { get; set; }
        public decimal  Price    { get; set; }
        public bool     Active   { get; set; }
        public Plan     Plan     { get; set; }
        public DateTime At       { get; set; }
        public Location Location { get; set; } = new();
    }

    private class Tenant
    {
        public int      Id       { get; set; }
        public Settings Settings { get; set; } = new();
    }

    private class TenantContext(DbContextOptions<TenantContext> options) : DbContext(options)
    {
        public DbSet<Tenant> Tenants => Set<Tenant>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Tenant>(b =>
            {
                b.ToTable("ig_tenants");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Settings, s => { s.ToJson("settings"); s.ComplexProperty(x => x.Location); });
                b.HasComplexIndex(x => x.Settings.Location.City, isUnique: true, indexName: "ux_ig_tenants_city");
                b.HasComplexIndex(x => x.Settings.Location.Zip, indexName: "ix_ig_tenants_zip");
                b.HasComplexIndex(x => x.Settings.Rank, indexName: "ix_ig_tenants_rank");
                b.HasComplexIndex(x => x.Settings.Price, indexName: "ix_ig_tenants_price");
                b.HasComplexIndex(x => x.Settings.Active, indexName: "ix_ig_tenants_active");
                b.HasComplexIndex(x => x.Settings.Plan, indexName: "ix_ig_tenants_plan");
                b.HasComplexIndex(x => x.Settings.At, isUnique: true, indexName: "ux_ig_tenants_at");
            });
    }

    // What the fix is for: until 5.4.0 only a top-level string member was rendered the way Npgsql's
    // queries read it, so every index here applied and enforced, and not one was used by a query.
    // Sequential scans are disabled so that "not used" can only mean "cannot be used".
    [TestMethod(DisplayName = "EF Core's queries over JSON members use the indexes the differ created")]
    public void Json_member_indexes_serve_ef_queries()
    {
        Migrate<TenantContext>();

        using var context = new TenantContext(new DbContextOptionsBuilder<TenantContext>().UseNpgsql(ConnectionString).Options);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        context.Tenants.AddRange(Enumerable.Range(0, 200).Select(i => new Tenant
        {
            Settings = new Settings
            {
                Rank = i, Price = i + 0.5m, Active = i % 2 == 0, Plan = (Plan)(i % 2), At = start.AddMinutes(i),
                Location = new Location { City = $"city-{i}", Zip = 10000 + i }
            }
        }));
        context.SaveChanges();

        var queries = new (string Index, IQueryable<Tenant> Query)[]
        {
            ("ux_ig_tenants_city",   context.Tenants.Where(t => t.Settings.Location.City == "city-5")),
            ("ix_ig_tenants_zip",    context.Tenants.Where(t => t.Settings.Location.Zip == 10005)),
            ("ix_ig_tenants_rank",   context.Tenants.Where(t => t.Settings.Rank == 5)),
            ("ix_ig_tenants_price",  context.Tenants.Where(t => t.Settings.Price == 5.5m)),
            ("ix_ig_tenants_active", context.Tenants.Where(t => t.Settings.Active)),
            ("ix_ig_tenants_plan",   context.Tenants.Where(t => t.Settings.Plan == Plan.Pro))
        };

        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        using (var off = new NpgsqlCommand("SET enable_seqscan = off", connection))
            off.ExecuteNonQuery();

        foreach (var (index, query) in queries)
        {
            using var explain = new NpgsqlCommand("EXPLAIN (COSTS OFF) " + query.ToQueryString(), connection);
            using var reader  = explain.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read())
                plan.Add(reader.GetString(0));

            StringAssert.Contains(string.Join("\n", plan), index, $"{index} is not used:\n{string.Join("\n", plan)}");
        }

        // Uniqueness still holds, for a nested member and for a date member kept as text — the latter
        // compared on the text EF itself wrote.
        string storedAt;
        using (var read = new NpgsqlCommand("SELECT settings ->> 'At' FROM ig_tenants WHERE settings ->> 'Rank' = '5'", connection))
            storedAt = (string)read.ExecuteScalar()!;

        AssertRejected("""INSERT INTO ig_tenants (settings) VALUES ('{"Rank":1,"Price":1,"Active":true,"Plan":0,"At":"2030-01-01T00:00:00Z","Location":{"City":"city-5","Zip":1}}')""");
        AssertRejected($$$"""INSERT INTO ig_tenants (settings) VALUES ('{"Rank":1,"Price":1,"Active":true,"Plan":0,"At":"{{{storedAt}}}","Location":{"City":"elsewhere","Zip":1}}')""");
    }

    // ── The regression: native HasIndex ⇄ HasComplexIndex round-trips cleanly ──

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

    // ── EnsureCreated: the runtime differ registration, end to end ──

    private class EnsureCreatedContext(DbContextOptions<EnsureCreatedContext> options) : DbContext(options)
    {
        public DbSet<RoleGrant> Grants => Set<RoleGrant>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<RoleGrant>(b =>
            {
                b.ToTable("ig_ensure_grants");
                b.HasKey(x => x.Id);
                b.Property(x => x.GranteeId).HasColumnName("grantee_id");
                b.Property(x => x.RoleId).HasColumnName("role_id");
                b.Property(x => x.Period).HasColumnName("period");
                b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
                b.HasComplexCompositeIndex(x => new { x.GranteeId, x.RoleId }, indexName: "ix_ig_ensure_grantee_role");
                b.HasExclusionConstraint(x => x.GranteeId, x => x.Period, name: "ex_ig_ensure_grantee_period");
            });
    }

    /// <summary>
    /// <c>EnsureCreated()</c> runs the context's <em>runtime</em> differ, so it never saw the
    /// design-time registration; without <c>UseNpgsqlComplexIndexes()</c> it creates the table and
    /// silently nothing else. A fresh database is used because <c>EnsureCreated()</c> is a no-op on a
    /// database that already has tables — which the shared one does by the time this runs.
    /// </summary>
    [TestMethod(DisplayName = "EnsureCreated builds the complex index and exclusion constraint once the runtime differ is registered")]
    public void EnsureCreated_includes_declarations_with_runtime_registration()
    {
        Sql("CREATE DATABASE ig_ensure_created");

        var fresh = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "ig_ensure_created" }.ConnectionString;
        var options = new DbContextOptionsBuilder<EnsureCreatedContext>()
                     .UseNpgsql(fresh)
                     .UseNpgsqlComplexIndexes()
                     .Options;

        using (var context = new EnsureCreatedContext(options))
            Assert.IsTrue(context.Database.EnsureCreated(), "EnsureCreated should have created the schema in the fresh database.");

        using var connection = new NpgsqlConnection(fresh);
        connection.Open();

        using (var indexes = new NpgsqlCommand("SELECT count(*) FROM pg_indexes WHERE tablename = 'ig_ensure_grants' AND indexname = 'ix_ig_ensure_grantee_role'", connection))
            Assert.AreEqual(1L, Convert.ToInt64(indexes.ExecuteScalar()), "The complex index was not created by EnsureCreated.");

        using (var constraints = new NpgsqlCommand("SELECT count(*) FROM pg_constraint WHERE conname = 'ex_ig_ensure_grantee_period' AND contype = 'x'", connection))
            Assert.AreEqual(1L, Convert.ToInt64(constraints.ExecuteScalar()), "The exclusion constraint was not created by EnsureCreated.");
    }
}
