using EFCore.ComplexIndexes.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Testcontainers.MsSql;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// End-to-end proof against a real SQL Server 2022 (Testcontainers): the SQL Server differ's
/// operations, rendered by the context's runtime generator with the target model as
/// <c>database update</c> renders them, apply, and the indexes they leave are the ones declared,
/// read back from <c>sys.indexes</c> and <c>sys.index_columns</c>.
/// </summary>
/// <remarks>
/// Every other SQL Server test compares rendered SQL or the operation list, which cannot see what
/// the server refuses or accepts. Three 5.5.0 defects lived exactly there: an <c>ALTER COLUMN</c>
/// under a complex index failed with Msg 5074 (#84), a unique index over nullable columns admitted
/// one row without a value and refused the second (#88), and a second clustered index or an index
/// named like the table's key failed only at apply (#90). So did two index names that differ only
/// in case (#116). Each test runs in a database of its own.
/// Marked Integration and SqlServer: CI runs this class in its own job, and without Docker it goes
/// inconclusive locally and fails on CI.
/// </remarks>
[TestClass]
[TestCategory("Integration")]
[TestCategory("SqlServer")]
[DoNotParallelize]
public class SqlServerIntegrationTests
{
    private static MsSqlContainer? _container;
    private static string?         _serverConnectionString;
    private static string?         _unavailableReason;

    private string? _database;

    [ClassInitialize]
    public static async Task StartContainer(TestContext _)
    {
        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
            _serverConnectionString = _container.GetConnectionString();
        }
        catch (Exception e)
        {
            _unavailableReason = $"Docker/SQL Server container unavailable: {e.Message}";
        }
    }

    [ClassCleanup]
    public static async Task StopContainer()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    private static string ServerConnectionString
    {
        get
        {
            if (_serverConnectionString is not null)
                return _serverConnectionString;

            var reason = _unavailableReason ?? "Container not started.";

            // As in PostgresIntegrationTests: inconclusive on a laptop without Docker, a failure on
            // CI, where a quietly skipped lane would report green.
            if (RunningOnCi)
                Assert.Fail($"Integration tests are mandatory on CI, but the database was unavailable. {reason}");

            Assert.Inconclusive(reason);
            return null!;
        }
    }

    private static bool RunningOnCi =>
        Environment.GetEnvironmentVariable("CI") is { Length: > 0 } value
     && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    // A database per test, created on first use.
    private string ConnectionString
    {
        get
        {
            if (_database is null)
            {
                var name = "lane_" + Guid.NewGuid().ToString("N")[..12];
                Execute(ServerConnectionString, $"CREATE DATABASE [{name}]");
                _database = new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = name }.ConnectionString;
            }

            return _database;
        }
    }

    // ── Harness: model → SQL Server differ → runtime generator with the target model → server ──

    private static IRelationalModel Model<TContext>() where TContext : DbContext
        => MigrationHarness.SqlServerModel<TContext>();

    /// <summary>
    /// Diffs as <c>migrations add</c> does and applies as <c>database update</c> does: the context's
    /// own services, wired with <c>UseSqlServerComplexIndexes()</c>, render the operations with the
    /// target model and execute them through EF's command executor.
    /// </summary>
    private void Migrate(IRelationalModel? source, IRelationalModel target)
    {
        var operations = MigrationHarness.SqlServerDiff(source, target);

        var options = new DbContextOptionsBuilder().UseSqlServer(ConnectionString).UseSqlServerComplexIndexes().Options;
        using var context = new MigrationHarness.EmptyContext(options);

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(operations, target.Model);
        context.GetService<IMigrationCommandExecutor>().ExecuteNonQuery(commands, context.GetService<IRelationalConnection>());
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    private void Sql(string sql) => Execute(ConnectionString, sql);

    private void AssertDuplicateRefused(string sql)
    {
        var exception = Assert.ThrowsExactly<SqlException>(() => Sql(sql));
        // 2601: duplicate key row in a unique index; 2627: in a unique constraint.
        Assert.IsTrue(exception.Number is 2601 or 2627, $"Expected a duplicate-key refusal, got {exception.Number}: {exception.Message}");
    }

    private sealed record IndexInfo(
        string   Type,
        bool     IsUnique,
        string?  Filter,
        int      FillFactor,
        string   Compression,
        string[] Keys,
        string[] Included);

    /// <summary>Reads an index back from the catalog views, or null when the table has none of that name.</summary>
    private IndexInfo? ReadIndex(string table, string name)
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();

        using var index = new SqlCommand(
            """
            SELECT i.type_desc, i.is_unique, i.filter_definition, i.fill_factor,
                   (SELECT TOP 1 p.data_compression_desc FROM sys.partitions p
                     WHERE p.object_id = i.object_id AND p.index_id = i.index_id)
              FROM sys.indexes i
             WHERE i.object_id = OBJECT_ID(@table) AND i.name = @name
            """, connection);
        index.Parameters.AddWithValue("@table", table);
        index.Parameters.AddWithValue("@name", name);

        string type, compression; bool unique; string? filter; int fillFactor;
        using (var reader = index.ExecuteReader())
        {
            if (!reader.Read())
                return null;

            type        = reader.GetString(0);
            unique      = reader.GetBoolean(1);
            filter      = reader.IsDBNull(2) ? null : reader.GetString(2);
            fillFactor  = reader.GetByte(3);
            compression = reader.GetString(4);
        }

        using var columns = new SqlCommand(
            """
            SELECT c.name, ic.is_descending_key, ic.is_included_column
              FROM sys.index_columns ic
              JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
             WHERE i.object_id = OBJECT_ID(@table) AND i.name = @name
             ORDER BY ic.is_included_column, ic.key_ordinal, ic.index_column_id
            """, connection);
        columns.Parameters.AddWithValue("@table", table);
        columns.Parameters.AddWithValue("@name", name);

        var keys     = new List<string>();
        var included = new List<string>();
        using (var reader = columns.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetBoolean(2))
                    included.Add(reader.GetString(0));
                else
                    keys.Add(reader.GetString(0) + (reader.GetBoolean(1) ? " DESC" : ""));
            }
        }

        return new IndexInfo(type, unique, filter, fillFactor, compression, [.. keys], [.. included]);
    }

    private IndexInfo AssertIndex(string table, string name)
        => ReadIndex(table, name) ?? throw new AssertFailedException($"Index '{name}' does not exist on table '{table}'.");

    // ── The model ──

    private class Contact
    {
        public string Email { get; set; } = "";
        public string Phone { get; set; } = "";
    }

    private class Customer
    {
        public Guid      Id        { get; set; }
        public string    Name      { get; set; } = "";
        public DateTime? RevokedAt { get; set; }
        public Contact   Contact   { get; set; } = new();
    }

    private abstract class CustomerContext(DbContextOptions options) : DbContext(options)
    {
        protected virtual string Table       => "customers";
        protected virtual int    PhoneLength => 32;
        protected virtual int    EmailLength => 256;
        protected virtual int    NameLength  => 100;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Customer>(b =>
            {
                b.ToTable(Table);
                b.HasKey(x => x.Id).IsClustered(false);
                b.Property(x => x.Name).HasColumnName("name").HasMaxLength(NameLength);
                b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
                b.ComplexProperty(x => x.Contact, c =>
                {
                    var email = c.Property(x => x.Email).HasColumnName("email").HasMaxLength(EmailLength);
                    var phone = c.Property(x => x.Phone).HasColumnName("phone").HasMaxLength(PhoneLength);
                    DeclarePropertyLevel(email, phone);
                });
                Declare(b);
            });

        protected virtual void DeclarePropertyLevel(ComplexTypePropertyBuilder<string> email, ComplexTypePropertyBuilder<string> phone) { }

        protected virtual void Declare(EntityTypeBuilder<Customer> b) { }
    }

    // ── Every whitelisted option, property-level and entity-level ──

    private class PropertyLevelOptionsContext(DbContextOptions<PropertyLevelOptionsContext> options) : CustomerContext(options)
    {
        protected override void DeclarePropertyLevel(ComplexTypePropertyBuilder<string> email, ComplexTypePropertyBuilder<string> phone)
        {
            email.HasComplexIndex(ix => ix.HasName("cx_email").IsClustered().HasFillFactor(70).SortInTempDb().UseDataCompression(DataCompressionType.Row));
            phone.HasComplexIndex(ix => ix.HasName("ix_phone").IncludeProperties("Name", "Contact.Email").IsCreatedOnline());
        }
    }

    [TestMethod(DisplayName = "Property-level options apply as declared")]
    public void Property_level_options_apply()
    {
        Migrate(null, Model<PropertyLevelOptionsContext>());

        var clustered = AssertIndex("customers", "cx_email");
        Assert.AreEqual("CLUSTERED", clustered.Type);
        Assert.AreEqual(70,          clustered.FillFactor);
        Assert.AreEqual("ROW",       clustered.Compression);
        CollectionAssert.AreEqual(new[] { "email" }, clustered.Keys);

        // ONLINE and SORT_IN_TEMPDB shape how the index is built and are not stored; that the
        // statement carrying them applied is what this proves of them.
        var covering = AssertIndex("customers", "ix_phone");
        Assert.AreEqual("NONCLUSTERED", covering.Type);
        CollectionAssert.AreEqual(new[] { "phone" },         covering.Keys);
        CollectionAssert.AreEqual(new[] { "name", "email" }, covering.Included);
    }

    private class EntityLevelOptionsContext(DbContextOptions<EntityLevelOptionsContext> options) : CustomerContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Customer> b)
        {
            b.HasComplexIndex(x => x.Contact.Email, ix => ix.HasName("cx_email").IsClustered().HasFillFactor(60));
            b.HasComplexCompositeIndex(x => new { x.Name, Phone = DbOrder.Desc(x.Contact.Phone) }, ix => ix
                .HasName("ix_name_phone").IncludeProperties("Contact.Email").HasFillFactor(80).IsCreatedOnline().SortInTempDb()
                .UseDataCompression(DataCompressionType.Page));
        }
    }

    [TestMethod(DisplayName = "Entity-level options, with a descending part, apply as declared")]
    public void Entity_level_options_apply()
    {
        Migrate(null, Model<EntityLevelOptionsContext>());

        var clustered = AssertIndex("customers", "cx_email");
        Assert.AreEqual("CLUSTERED", clustered.Type);
        Assert.AreEqual(60,          clustered.FillFactor);

        var composite = AssertIndex("customers", "ix_name_phone");
        Assert.AreEqual("NONCLUSTERED", composite.Type);
        Assert.AreEqual(80,             composite.FillFactor);
        Assert.AreEqual("PAGE",         composite.Compression);
        CollectionAssert.AreEqual(new[] { "name", "phone DESC" }, composite.Keys);
        CollectionAssert.AreEqual(new[] { "email" },              composite.Included);
    }

    // ── Filters and uniqueness ──

    private class PlaceholderFilterContext(DbContextOptions<PlaceholderFilterContext> options) : CustomerContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Customer> b)
            => b.HasComplexIndex(x => x.Contact.Email, isUnique: true, filter: "{RevokedAt} IS NULL", indexName: "ux_email_active");
    }

    [TestMethod(DisplayName = "A placeholder filter applies and enforces uniqueness among the rows it covers")]
    public void Placeholder_filter_applies_and_enforces()
    {
        Migrate(null, Model<PlaceholderFilterContext>());

        var index = AssertIndex("customers", "ux_email_active");
        Assert.IsTrue(index.IsUnique);
        Assert.AreEqual("([revoked_at] IS NULL)", index.Filter);

        Sql("INSERT INTO customers (Id, name, email, phone, revoked_at) VALUES (NEWID(), N'a', N'a@x', N'1', '2026-01-01')");
        Sql("INSERT INTO customers (Id, name, email, phone, revoked_at) VALUES (NEWID(), N'b', N'a@x', N'2', NULL)");
        AssertDuplicateRefused("INSERT INTO customers (Id, name, email, phone, revoked_at) VALUES (NEWID(), N'c', N'a@x', N'3', NULL)");
    }

    private class Registration
    {
        public string Vin { get; set; } = "";
    }

    private class Car
    {
        public int           Id           { get; set; }
        public Registration? Registration { get; set; }
    }

    private class OptionalComplexContext(DbContextOptions<OptionalComplexContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Car>(b =>
            {
                b.ToTable("cars");
                b.ComplexProperty(x => x.Registration, c => c.Property(r => r.Vin).HasMaxLength(17));
                b.HasComplexIndex(x => x.Registration!.Vin, isUnique: true, indexName: "ux_vin");
            });
    }

    [TestMethod(DisplayName = "A unique index over an optional complex property admits any number of rows without a value (#88)")]
    public void Unique_index_over_nullable_columns_admits_rows_without_a_value()
    {
        Migrate(null, Model<OptionalComplexContext>());

        Assert.AreEqual("([Registration_Vin] IS NOT NULL)", AssertIndex("cars", "ux_vin").Filter);

        Sql("INSERT INTO cars (Registration_Vin) VALUES (NULL)");
        Sql("INSERT INTO cars (Registration_Vin) VALUES (NULL)");
        Sql("INSERT INTO cars (Registration_Vin) VALUES (N'WVW00000000000001')");
        AssertDuplicateRefused("INSERT INTO cars (Registration_Vin) VALUES (N'WVW00000000000001')");
    }

    private class PropertyLevelOptionalContext(DbContextOptions<PropertyLevelOptionalContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Car>(b =>
            {
                b.ToTable("cars");
                b.ComplexProperty(x => x.Registration, c => c.Property(r => r.Vin).HasMaxLength(17).HasComplexIndex(isUnique: true, indexName: "ux_vin"));
            });
    }

    [TestMethod(DisplayName = "Declared on the property, a unique index over an optional complex property admits rows without a value (#155)")]
    public void Property_level_unique_index_over_nullable_columns_admits_rows_without_a_value()
    {
        Migrate(null, Model<PropertyLevelOptionalContext>());

        Assert.AreEqual("([Registration_Vin] IS NOT NULL)", AssertIndex("cars", "ux_vin").Filter);

        Sql("INSERT INTO cars (Registration_Vin) VALUES (NULL)");
        Sql("INSERT INTO cars (Registration_Vin) VALUES (NULL)");
        Sql("INSERT INTO cars (Registration_Vin) VALUES (N'WVW00000000000001')");
        AssertDuplicateRefused("INSERT INTO cars (Registration_Vin) VALUES (N'WVW00000000000001')");
    }

    /// <summary>
    /// With <c>UseComplexIndexes()</c> in place of <c>UseSqlServerComplexIndexes()</c>, the core
    /// differ ran, left out #88's filter, and <c>EnsureCreated()</c> applied cleanly: the index
    /// admitted one row without a value and refused the second (#150). It now refuses before
    /// anything is created.
    /// </summary>
    [TestMethod(DisplayName = "UseComplexIndexes() refuses a unique index over nullable columns at EnsureCreated, where it applied without the filter")]
    public void Core_registration_refuses_a_unique_index_over_nullable_columns()
    {
        var options = new DbContextOptionsBuilder<OptionalComplexContext>()
                     .UseSqlServer(ConnectionString)
                     .UseComplexIndexes()
                     .Options;

        InvalidOperationException? refusal = null;
        using (var context = new OptionalComplexContext(options))
        {
            try { context.Database.EnsureCreated(); }
            catch (InvalidOperationException e) { refusal = e; }
        }

        if (refusal is null)
        {
            var filter = AssertIndex("cars", "ux_vin").Filter ?? "none";
            Sql("INSERT INTO cars (Registration_Vin) VALUES (NULL)");
            var second = "admitted";
            try { Sql("INSERT INTO cars (Registration_Vin) VALUES (NULL)"); }
            catch (SqlException e) { second = $"refused it with {e.Number}"; }

            Assert.Fail($"EnsureCreated() applied cleanly under UseComplexIndexes(): ux_vin has filter {filter}, and a second row without a VIN was {second}.");
        }

        StringAssert.Contains(refusal.Message, "call UseSqlServerComplexIndexes() instead of UseComplexIndexes()");
        Assert.IsNull(ReadIndex("cars", "ux_vin"), "The refusal came after the index was created.");
    }

    private class NullsOrderedContext(DbContextOptions<NullsOrderedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Car>(b =>
            {
                b.ToTable("cars");
                b.ComplexProperty(x => x.Registration, c => c.Property(r => r.Vin).HasMaxLength(17));
                b.HasComplexCompositeIndex(x => new { x.Id, Vin = DbOrder.NullsLast(x.Registration!.Vin) }, indexName: "ix_cars_vin");
            });
    }

    /// <summary>
    /// With <c>UseComplexIndexes()</c> in place of <c>UseSqlServerComplexIndexes()</c>, the core
    /// differ scaffolded a NULLS-ordered index with the runtime-wiring sentinel: <c>EnsureCreated()</c>
    /// created the table and then failed on an invalid column named after
    /// <c>UseNpgsqlComplexIndexes()</c>, while the core README said <c>migrations add</c> fails (#172).
    /// It now refuses before anything is created.
    /// </summary>
    [TestMethod(DisplayName = "UseComplexIndexes() refuses a NULLS-ordered index at EnsureCreated, before creating the table")]
    public void Core_registration_refuses_a_nulls_ordered_index()
    {
        var options = new DbContextOptionsBuilder<NullsOrderedContext>()
                     .UseSqlServer(ConnectionString)
                     .UseComplexIndexes()
                     .Options;

        Exception? failure = null;
        using (var context = new NullsOrderedContext(options))
        {
            try { context.Database.EnsureCreated(); }
            catch (Exception e) { failure = e; }
        }

        Assert.IsInstanceOfType<InvalidOperationException>(
            failure, $"EnsureCreated() reached the server: {failure?.GetType().Name}: {failure?.Message}");
        StringAssert.Contains(failure.Message, "the EFCore.ComplexIndexes.SqlServer satellite refuses it as well");

        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using var table = new SqlCommand("SELECT OBJECT_ID(N'cars')", connection);
        Assert.IsInstanceOfType<DBNull>(table.ExecuteScalar(), "The refusal came after the table was created.");
    }

    // ── TPC: a declaration on a concrete base holds on every table of the hierarchy (#93) ──

    private class Voucher
    {
        public int    Id   { get; set; }
        public string Code { get; set; } = "";
    }

    private class GiftVoucher : Voucher
    {
        public string Recipient { get; set; } = "";
    }

    private abstract class VoucherContextBase(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Voucher>(b =>
            {
                b.UseTpcMappingStrategy();
                b.ToTable("vouchers");
                b.Property(x => x.Code).HasMaxLength(32);
                // One name on every table, as EF names its own: SQL Server scopes index names per table.
                b.HasComplexIndex(x => x.Code, isUnique: true, indexName: "ux_voucher_code");
            });
            modelBuilder.Entity<GiftVoucher>().ToTable("gift_vouchers");
            ConfigureModel(modelBuilder);
        }

        protected virtual void ConfigureModel(ModelBuilder modelBuilder) { }
    }

    private class VoucherContext(DbContextOptions<VoucherContext> options) : VoucherContextBase(options);

    // The same model as a snapshot scaffolded before 5.5.0 holds it: rendering version 2.
    private class VoucherBefore55Context(DbContextOptions<VoucherBefore55Context> options) : VoucherContextBase(options)
    {
        protected override void ConfigureModel(ModelBuilder modelBuilder)
            => modelBuilder.HasAnnotation(ComplexIndexAnnotations.RenderingVersion, 2);
    }

    [TestMethod(DisplayName = "On a concrete TPC base, the upgrade migration makes the derived table enforce the unique index")]
    public void Tpc_derived_table_enforces_after_upgrade()
    {
        // What 5.4 scaffolded: the base table only.
        Migrate(null, Model<VoucherBefore55Context>());
        Assert.IsNull(ReadIndex("gift_vouchers", "ux_voucher_code"));

        AssertDuplicateRefused("INSERT INTO vouchers (Id, Code) VALUES (1, N'A'), (2, N'A')");
        Sql("INSERT INTO gift_vouchers (Id, Code, Recipient) VALUES (3, N'B', N'x'), (4, N'B', N'y')");

        // The duplicate has to go before the upgrade migration can create the index.
        Sql("DELETE FROM gift_vouchers WHERE Id = 4");
        Migrate(Model<VoucherBefore55Context>(), Model<VoucherContext>());

        var index = AssertIndex("gift_vouchers", "ux_voucher_code");
        Assert.IsTrue(index.IsUnique);
        CollectionAssert.AreEqual(new[] { "Code" }, index.Keys);

        AssertDuplicateRefused("INSERT INTO gift_vouchers (Id, Code, Recipient) VALUES (5, N'B', N'z')");
    }

    // #167: two partial indexes over the same column on a TPC base, which must both be named. SQL
    // Server keeps index names per table, so each table takes both names, as EF names its own.
    private class Pass
    {
        public int    Id     { get; set; }
        public int    ZoneId { get; set; }
        public string Code   { get; set; } = "";
    }

    private class SeasonPass : Pass;

    private class PassContext(DbContextOptions<PassContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Pass>(b =>
            {
                b.UseTpcMappingStrategy();
                b.ToTable("passes");
                b.Property(x => x.Code).HasMaxLength(32);
                b.HasComplexIndex(x => x.Code, isUnique: true, filter: "{ZoneId} > 0", indexName: "ux_pass_active");
                b.HasComplexIndex(x => x.Code, isUnique: true, filter: "{ZoneId} <= 0", indexName: "ux_pass_archived");
            });
            modelBuilder.Entity<SeasonPass>().ToTable("season_passes");
        }
    }

    [TestMethod(DisplayName = "Two named partial indexes on a TPC base apply under their names on every table and enforce there (#167)")]
    public void Tpc_named_partial_siblings_apply_on_every_table()
    {
        Migrate(null, Model<PassContext>());

        foreach (var table in new[] { "passes", "season_passes" })
        {
            Assert.AreEqual("([ZoneId]>(0))", AssertIndex(table, "ux_pass_active").Filter, table);
            Assert.AreEqual("([ZoneId]<=(0))", AssertIndex(table, "ux_pass_archived").Filter, table);
        }

        Sql("INSERT INTO season_passes (Id, ZoneId, Code) VALUES (1, 5, N'A'), (2, -1, N'B'), (3, -2, N'A')");
        AssertDuplicateRefused("INSERT INTO season_passes (Id, ZoneId, Code) VALUES (4, 6, N'A')");
        AssertDuplicateRefused("INSERT INTO season_passes (Id, ZoneId, Code) VALUES (5, -3, N'B')");
    }

    // ── Changes under an existing index ──

    private class RenameV1Context(DbContextOptions<RenameV1Context> options) : CustomerContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Customer> b) => b.HasComplexIndex(x => x.Contact.Phone, indexName: "ix_phone_v1");
    }

    private class RenameV2Context(DbContextOptions<RenameV2Context> options) : CustomerContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Customer> b) => b.HasComplexIndex(x => x.Contact.Phone, indexName: "ix_phone_v2");
    }

    [TestMethod(DisplayName = "A renamed index is renamed in place")]
    public void Index_rename_applies()
    {
        var v1 = Model<RenameV1Context>();
        Migrate(null, v1);
        Migrate(v1, Model<RenameV2Context>());

        Assert.IsNull(ReadIndex("customers", "ix_phone_v1"));
        CollectionAssert.AreEqual(new[] { "phone" }, AssertIndex("customers", "ix_phone_v2").Keys);
    }

    private class TableBeforeContext(DbContextOptions<TableBeforeContext> options) : CustomerContext(options)
    {
        protected override string Table => "customers_before";

        protected override void Declare(EntityTypeBuilder<Customer> b) => b.HasComplexIndex(x => x.Contact.Phone, indexName: "ix_moving");
    }

    private class TableAfterContext(DbContextOptions<TableAfterContext> options) : CustomerContext(options)
    {
        protected override string Table => "customers_after";

        protected override void Declare(EntityTypeBuilder<Customer> b) => b.HasComplexIndex(x => x.Contact.Phone, indexName: "ix_moving");
    }

    [TestMethod(DisplayName = "A renamed table keeps its complex index")]
    public void Table_rename_keeps_the_index()
    {
        var before = Model<TableBeforeContext>();
        Migrate(null, before);
        Migrate(before, Model<TableAfterContext>());

        CollectionAssert.AreEqual(new[] { "phone" }, AssertIndex("customers_after", "ix_moving").Keys);
    }

    private class AlterV1Context(DbContextOptions options) : CustomerContext(options)
    {
        protected override int PhoneLength => 64;
        protected override int EmailLength => 320;

        protected override void Declare(EntityTypeBuilder<Customer> b)
        {
            b.HasComplexIndex(x => x.Contact.Phone, isUnique: true, indexName: "ux_phone");
            b.HasComplexIndex(x => x.Name, ix => ix.HasName("ix_name_covering").IncludeProperties("Contact.Email"));
        }
    }

    // The key column of one index and the included column of the other shrink. Widening an
    // nvarchar under an index is one ALTER COLUMN SQL Server allows (a metadata-only change), so it
    // cannot show #84; shrinking it, like a change of type or nullability, is refused with Msg 5074.
    private class AlterV2Context(DbContextOptions<AlterV2Context> options) : AlterV1Context(options)
    {
        protected override int PhoneLength => 32;
        protected override int EmailLength => 256;
    }

    [TestMethod(DisplayName = "ALTER COLUMN under a complex index applies (#84)")]
    public void Alter_column_under_an_index_applies()
    {
        var v1 = Model<AlterV1Context>();
        Migrate(null, v1);
        Sql("INSERT INTO customers (Id, name, email, phone) VALUES (NEWID(), N'a', N'a@x', N'1')");

        // Failed with Msg 5074 while the index existed: EF moves only its own indexes out of the way.
        Migrate(v1, Model<AlterV2Context>());

        Assert.IsTrue(AssertIndex("customers", "ux_phone").IsUnique);
        CollectionAssert.AreEqual(new[] { "email" }, AssertIndex("customers", "ix_name_covering").Included);
        AssertDuplicateRefused("INSERT INTO customers (Id, name, email, phone) VALUES (NEWID(), N'b', N'b@x', N'1')");
    }

    // A renamed index under the shrinking column: once with its default name, which changes with
    // the table, and once renamed explicitly on the same table.
    private class RenameUnderAlterV1Context(DbContextOptions<RenameUnderAlterV1Context> options) : CustomerContext(options)
    {
        protected override int PhoneLength => 64;

        protected override void Declare(EntityTypeBuilder<Customer> b)
        {
            b.HasComplexIndex(x => x.Contact.Phone, isUnique: true);
            b.HasComplexIndex(x => x.Contact.Email, indexName: "ix_email_v1");
        }
    }

    private class RenameUnderAlterV2Context(DbContextOptions<RenameUnderAlterV2Context> options) : CustomerContext(options)
    {
        protected override string Table       => "clients";
        protected override int    EmailLength => 128;

        protected override void Declare(EntityTypeBuilder<Customer> b)
        {
            b.HasComplexIndex(x => x.Contact.Phone, isUnique: true);
            b.HasComplexIndex(x => x.Contact.Email, indexName: "ix_email_v2");
        }
    }

    [TestMethod(DisplayName = "ALTER COLUMN under a renamed complex index applies (#141)")]
    public void Alter_column_under_a_renamed_index_applies()
    {
        var v1 = Model<RenameUnderAlterV1Context>();
        Migrate(null, v1);
        Sql("INSERT INTO customers (Id, name, email, phone) VALUES (NEWID(), N'a', N'a@x', N'1')");

        // Failed with Msg 5074: the renames ran after the ALTER COLUMN, with the indexes still in place.
        Migrate(v1, Model<RenameUnderAlterV2Context>());

        Assert.IsNull(ReadIndex("clients", "IX_customers_phone"));
        Assert.IsNull(ReadIndex("clients", "ix_email_v1"));
        Assert.IsTrue(AssertIndex("clients", "IX_clients_phone").IsUnique);
        CollectionAssert.AreEqual(new[] { "email" }, AssertIndex("clients", "ix_email_v2").Keys);
        AssertDuplicateRefused("INSERT INTO clients (Id, name, email, phone) VALUES (NEWID(), N'b', N'b@x', N'1')");
    }

    // The index reads name only through its filter, and name shrinks (#156).
    private class FilterColumnV1Context(DbContextOptions options) : CustomerContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Customer> b)
            => b.HasComplexIndex(x => x.Contact.Email, isUnique: true, filter: "{Name} <> N''", indexName: "ux_email_named");
    }

    private class FilterColumnV2Context(DbContextOptions<FilterColumnV2Context> options) : FilterColumnV1Context(options)
    {
        protected override int NameLength => 50;
    }

    [TestMethod(DisplayName = "ALTER COLUMN on a column only a filter names applies (#156)")]
    public void Alter_column_named_only_in_a_filter_applies()
    {
        var v1 = Model<FilterColumnV1Context>();
        Migrate(null, v1);
        Sql("INSERT INTO customers (Id, name, email, phone) VALUES (NEWID(), N'a', N'a@x', N'1')");

        // A filtered index depends on the columns its filter names: Msg 5074 while it exists.
        Migrate(v1, Model<FilterColumnV2Context>());

        Assert.AreEqual("([name]<>N'')", AssertIndex("customers", "ux_email_named").Filter);
        AssertDuplicateRefused("INSERT INTO customers (Id, name, email, phone) VALUES (NEWID(), N'b', N'a@x', N'2')");
    }

    // ── A column rename keeps the indexes on the column (#182) ──

    private class OptionalContact
    {
        public string? Email { get; set; }
    }

    private class Subscriber
    {
        public Guid            Id        { get; set; }
        public string          Name      { get; set; } = "";
        public DateTime?       RevokedAt { get; set; }
        public OptionalContact Contact   { get; set; } = new();
    }

    private abstract class ColumnRenameContext(DbContextOptions options, string suffix) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Subscriber>(b =>
            {
                b.ToTable("subscribers");
                b.HasKey(x => x.Id).IsClustered(false);
                b.Property(x => x.Name).HasColumnName("name" + suffix).HasMaxLength(100);
                b.Property(x => x.RevokedAt).HasColumnName("revoked_at" + suffix);

                // Unique over a nullable column: its implicit filter names the column too.
                b.ComplexProperty(x => x.Contact, c => c.Property(x => x.Email).HasColumnName("email" + suffix).HasMaxLength(256).HasComplexIndex(isUnique: true));

                b.HasComplexCompositeIndex(x => new { x.Name, x.Contact.Email }, filter: "{RevokedAt} IS NULL", indexName: "ix_name_email_active");
                b.HasComplexIndex(x => x.Name, ix => ix.HasName("ix_name_covering").IncludeProperties("Contact.Email"));
            });
    }

    private class ColumnRenameV1Context(DbContextOptions<ColumnRenameV1Context> options) : ColumnRenameContext(options, "");

    private class ColumnRenameV2Context(DbContextOptions<ColumnRenameV2Context> options) : ColumnRenameContext(options, "_v2");

    [TestMethod(DisplayName = "A column rename keeps an unfiltered index and rebuilds a filtered one around it (#182)")]
    public void Column_rename_keeps_the_indexes()
    {
        var v1 = Model<ColumnRenameV1Context>();
        Migrate(null, v1);

        // sp_rename rejects a column a filtered index reads ("The index … is dependent on column
        // …"), the implicit IS NOT NULL filter included; a key or included column it renames.
        var operations = MigrationHarness.SqlServerDiff(v1, Model<ColumnRenameV2Context>());
        CollectionAssert.AreEquivalent(
            new[] { "IX_subscribers_email", "ix_name_email_active" },
            operations.OfType<DropIndexOperation>().Select(o => o.Name).ToArray());

        Migrate(v1, Model<ColumnRenameV2Context>());

        Assert.AreEqual("([email_v2] IS NOT NULL)", AssertIndex("subscribers", "IX_subscribers_email_v2").Filter);
        Assert.AreEqual("([revoked_at_v2] IS NULL)", AssertIndex("subscribers", "ix_name_email_active").Filter);
        CollectionAssert.AreEqual(new[] { "name_v2", "email_v2" }, AssertIndex("subscribers", "ix_name_email_active").Keys);
        CollectionAssert.AreEqual(new[] { "email_v2" }, AssertIndex("subscribers", "ix_name_covering").Included);
    }

    // ── A move into the default schema keeps the indexes (#185) ──

    private abstract class SchemaMoveContext(DbContextOptions options, string? schema) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Subscriber>(b =>
            {
                b.ToTable("moved_subscribers", schema);
                b.HasKey(x => x.Id).IsClustered(false);
                b.Property(x => x.Name).HasMaxLength(100);
                b.ComplexProperty(x => x.Contact, c => c.Property(x => x.Email).HasMaxLength(256));
                b.HasComplexCompositeIndex(x => new { x.Name, x.Contact.Email }, indexName: "ix_moved_name_email");
            });
    }

    private class SchemaMoveNorthContext(DbContextOptions<SchemaMoveNorthContext> options) : SchemaMoveContext(options, "north");

    private class SchemaMoveDefaultContext(DbContextOptions<SchemaMoveDefaultContext> options) : SchemaMoveContext(options, null);

    [TestMethod(DisplayName = "A move into the default schema keeps the indexes, with no rebuild (#185)")]
    public void Move_into_default_schema_keeps_the_indexes()
    {
        var north = Model<SchemaMoveNorthContext>();
        Migrate(null, north);

        // ALTER SCHEMA … TRANSFER keeps a table's indexes.
        var operations = MigrationHarness.SqlServerDiff(north, Model<SchemaMoveDefaultContext>());
        Assert.IsFalse(operations.Any(o => o is DropIndexOperation or CreateIndexOperation));

        Migrate(north, Model<SchemaMoveDefaultContext>());

        CollectionAssert.AreEqual(new[] { "Name", "Contact_Email" }, AssertIndex("moved_subscribers", "ix_moved_name_email").Keys);
    }

    // ── A default or comment change keeps the index (#186) ──

    private abstract class FacetContext(DbContextOptions options, bool withDefault) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Subscriber>(b =>
            {
                b.ToTable("facet_subscribers");
                b.HasKey(x => x.Id).IsClustered(false);
                var name = b.Property(x => x.Name).HasMaxLength(100);
                if (withDefault)
                    name.HasDefaultValue("anonymous").HasComment("shown when nothing else is");
                b.ComplexProperty(x => x.Contact, c => c.Property(x => x.Email).HasMaxLength(256));
                b.HasComplexCompositeIndex(x => new { x.Name, x.Contact.Email }, indexName: "ix_facet_name_email");
            });
    }

    private class FacetV1Context(DbContextOptions<FacetV1Context> options) : FacetContext(options, withDefault: false);

    private class FacetV2Context(DbContextOptions<FacetV2Context> options) : FacetContext(options, withDefault: true);

    [TestMethod(DisplayName = "A default and comment change applies with the index in place (#186)")]
    public void Default_and_comment_change_keeps_the_index()
    {
        var v1 = Model<FacetV1Context>();
        Migrate(null, v1);

        // ADD DEFAULT and sp_addextendedproperty, no ALTER COLUMN: the index stays.
        var operations = MigrationHarness.SqlServerDiff(v1, Model<FacetV2Context>());
        Assert.IsTrue(operations.OfType<AlterColumnOperation>().Any());
        Assert.IsFalse(operations.Any(o => o is DropIndexOperation or CreateIndexOperation));

        Migrate(v1, Model<FacetV2Context>());

        CollectionAssert.AreEqual(new[] { "Name", "Contact_Email" }, AssertIndex("facet_subscribers", "ix_facet_name_email").Keys);
    }

    // ── The clustered slot and key names (#90) ──

    private class OneClusteredContext(DbContextOptions options) : CustomerContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Customer> b) => b.HasComplexIndex(x => x.Contact.Email, ix => ix.HasName("cx_one").IsClustered());
    }

    private class SecondClusteredContext(DbContextOptions<SecondClusteredContext> options) : OneClusteredContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Customer> b)
        {
            base.Declare(b);
            b.HasComplexIndex(x => x.Contact.Phone, ix => ix.HasName("cx_two").IsClustered());
        }
    }

    private class ClusteredMovedContext(DbContextOptions<ClusteredMovedContext> options) : CustomerContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Customer> b)
        {
            b.HasComplexIndex(x => x.Contact.Email, ix => ix.HasName("cx_one"));
            b.HasComplexIndex(x => x.Contact.Phone, ix => ix.HasName("cx_two").IsClustered());
        }
    }

    [TestMethod(DisplayName = "A second clustered index in a later migration is refused at migrations add, not at apply")]
    public void Second_clustered_index_is_refused_before_apply()
    {
        var v1 = Model<OneClusteredContext>();
        Migrate(null, v1);
        Assert.AreEqual("CLUSTERED", AssertIndex("customers", "cx_one").Type);

        // Without the whole-table count the migration scaffolded and SQL Server refused it, which
        // this assertion reports as the SqlException it got instead.
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Migrate(v1, Model<SecondClusteredContext>()));
        StringAssert.Contains(exception.Message, "at most one clustered index per table");
    }

    [TestMethod(DisplayName = "The clustered slot moves between complex indexes in one migration")]
    public void Clustered_slot_moves_between_complex_indexes()
    {
        var v1 = Model<OneClusteredContext>();
        Migrate(null, v1);
        Migrate(v1, Model<ClusteredMovedContext>());

        Assert.AreEqual("NONCLUSTERED", AssertIndex("customers", "cx_one").Type);
        Assert.AreEqual("CLUSTERED",    AssertIndex("customers", "cx_two").Type);
    }

    private class NamedLikeKeyContext(DbContextOptions<NamedLikeKeyContext> options) : CustomerContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Customer> b) => b.HasComplexIndex(x => x.Contact.Phone, indexName: "pk_customers");
    }

    [TestMethod(DisplayName = "An index named like the table's key, in another case, is refused at migrations add, not at apply")]
    public void Index_named_like_the_key_is_refused_before_apply()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Migrate(null, Model<NamedLikeKeyContext>()));
        StringAssert.Contains(exception.Message, "same name as the table's primary key 'PK_customers'");
    }

    // ── Index names that differ only in case (#116) ──

    private class NamesDifferingInCaseContext(DbContextOptions<NamesDifferingInCaseContext> options) : CustomerContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Customer> b)
        {
            b.HasComplexIndex(x => x.Contact.Phone, indexName: "ix_case");
            b.HasComplexIndex(x => x.Contact.Email, indexName: "IX_CASE");
        }
    }

    [TestMethod(DisplayName = "Two index names that differ only in case are refused at migrations add, not at apply")]
    public void Index_names_differing_in_case_are_refused_before_apply()
    {
        // Compared ordinally, the pair scaffolded and the default collation refused the second
        // CREATE INDEX, which this assertion reports as the SqlException it got instead.
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => Migrate(null, Model<NamesDifferingInCaseContext>()));
        StringAssert.Contains(exception.Message, "which differ only in case");
    }

    // ── The runtime seam: EnsureCreated with the satellite's registration ──

    [TestMethod(DisplayName = "EnsureCreated builds the complex indexes with their options")]
    public void EnsureCreated_builds_the_indexes()
    {
        var options = new DbContextOptionsBuilder<EntityLevelOptionsContext>()
                     .UseSqlServer(ConnectionString)
                     .UseSqlServerComplexIndexes()
                     .Options;

        using (var context = new EntityLevelOptionsContext(options))
            Assert.IsTrue(context.Database.EnsureCreated());

        Assert.AreEqual("CLUSTERED", AssertIndex("customers", "cx_email").Type);
        CollectionAssert.AreEqual(new[] { "email" }, AssertIndex("customers", "ix_name_phone").Included);
    }
}
