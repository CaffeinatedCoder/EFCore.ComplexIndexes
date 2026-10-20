using EFCore.ComplexIndexes.PostgreSQL;
using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// The core differ forwards no provider index option, so a SQL Server or PostgreSQL model diffed by
/// it, with <c>UseComplexIndexes()</c> where the satellite's registration belongs or without the
/// satellite's <c>.targets</c>, lost every property-level option without a word:
/// <c>CREATE INDEX [cx_prop] ON [things] ([code])</c> for a clustered, fill-factored, compressed index,
/// and <c>EnsureCreated()</c> applied it (#92). It now refuses, naming the registration to use.
/// </summary>
[TestClass]
public class CoreDifferSatelliteOptionTests
{
    private class Address
    {
        public string Code { get; set; } = "";
        public string City { get; set; } = "";
    }

    private class Thing
    {
        public Guid    Id      { get; set; }
        public Address Address { get; set; } = new();
    }

    private static void MapThing(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Thing> b)
    {
        b.ToTable("things");
        b.HasKey(x => x.Id).IsClustered(false);
    }

    private class PropertyLevelSqlServerContext(DbContextOptions<PropertyLevelSqlServerContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Thing>(b =>
            {
                MapThing(b);
                b.ComplexProperty(x => x.Address, c => c.Property(x => x.Code).HasComplexIndex(ix => ix
                    .HasName("cx_prop").IsClustered().HasFillFactor(80).UseDataCompression(DataCompressionType.Page)));
            });
    }

    private class EntityLevelSqlServerContext(DbContextOptions<EntityLevelSqlServerContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Thing>(b =>
            {
                MapThing(b);
                b.ComplexProperty(x => x.Address);
                b.HasComplexIndex(x => x.Address.City, ix => SqlServerComplexIndexBuilderExtensions.IncludeProperties(ix.HasName("ix_city"), "Address.Code"));
            });
    }

    private class PropertyLevelNpgsqlContext(DbContextOptions<PropertyLevelNpgsqlContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Thing>(b =>
            {
                b.ToTable("things");
                b.ComplexProperty(x => x.Address, c => c.Property(x => x.Code).HasComplexIndex(ix => ix.HasName("cx_code").UseHash()));
            });
    }

    private class StorageParameterNpgsqlContext(DbContextOptions<StorageParameterNpgsqlContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Thing>(b =>
            {
                b.ToTable("things");
                b.ComplexProperty(x => x.Address);
                b.HasComplexIndex(x => x.Address.City, ix => ix.HasName("ix_city").HasStorageParameter("fillfactor", 70));
            });
    }

    // Without any provider option: what the satellite-less core differ is for.
    private class PlainContext(DbContextOptions<PlainContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Thing>(b =>
            {
                MapThing(b);
                b.ComplexProperty(x => x.Address, c => c.Property(x => x.Code).HasComplexIndex(indexName: "cx_prop"));
            });
    }

    private static DbContextOptions SqlServer => new DbContextOptionsBuilder().UseSqlServer(MigrationHarness.SqlServerConnection).Options;

    private static DbContextOptions Npgsql => MigrationHarness.NpgsqlOptions();

    private static DbContextOptions Sqlite => new DbContextOptionsBuilder().UseSqlite(MigrationHarness.SqliteConnection).Options;

    [TestMethod(DisplayName = "The core differ refuses property-level SQL Server options, naming UseSqlServerComplexIndexes()")]
    public void Core_differ_refuses_property_level_sqlserver_options()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => MigrationHarness.Diff<CustomMigrationsModelDiffer>(SqlServer, null, MigrationHarness.SqlServerModel<PropertyLevelSqlServerContext>()));

        StringAssert.Contains(ex.Message, "The complex index 'cx_prop'");
        StringAssert.Contains(ex.Message, "SQL Server index option 'SqlServer:");
        StringAssert.Contains(ex.Message, "call UseSqlServerComplexIndexes() instead of UseComplexIndexes()");
    }

    [TestMethod(DisplayName = "The core differ refuses entity-level SQL Server options")]
    public void Core_differ_refuses_entity_level_sqlserver_options()
    {
        // Entity-level options reached the operation unfiltered, but unresolved: INCLUDE kept the
        // property path "Address.Code" as a column name, and nothing checked the clustered slot.
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => MigrationHarness.Diff<CustomMigrationsModelDiffer>(SqlServer, null, MigrationHarness.SqlServerModel<EntityLevelSqlServerContext>()));

        StringAssert.Contains(ex.Message, "'SqlServer:Include'");
    }

    [TestMethod(DisplayName = "The core differ refuses PostgreSQL options, naming UseNpgsqlComplexIndexes()")]
    [DataRow(typeof(PropertyLevelNpgsqlContext),   "Npgsql:IndexMethod")]
    [DataRow(typeof(StorageParameterNpgsqlContext), "Npgsql:StorageParameter:fillfactor")]
    public void Core_differ_refuses_npgsql_options(Type context, string option)
    {
        var target = (Microsoft.EntityFrameworkCore.Metadata.IRelationalModel)typeof(MigrationHarness)
                    .GetMethod(nameof(MigrationHarness.NpgsqlModel))!
                    .MakeGenericMethod(context)
                    .Invoke(null, [null])!;

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => MigrationHarness.Diff<CustomMigrationsModelDiffer>(Npgsql, null, target));

        StringAssert.Contains(ex.Message, $"PostgreSQL index option '{option}'");
        StringAssert.Contains(ex.Message, "call UseNpgsqlComplexIndexes()");
    }

    [TestMethod(DisplayName = "UseComplexIndexes() on SQL Server refuses at GenerateCreateScript, as EnsureCreated would")]
    public void Runtime_core_registration_on_sqlserver_refuses()
    {
        var options = new DbContextOptionsBuilder<PropertyLevelSqlServerContext>()
                     .UseSqlServer(MigrationHarness.SqlServerConnection)
                     .UseComplexIndexes()
                     .Options;

        using var context = new PropertyLevelSqlServerContext(options);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => context.Database.GenerateCreateScript());
        StringAssert.Contains(ex.Message, "UseSqlServerComplexIndexes()");
    }

    [TestMethod(DisplayName = "The satellites' own differs keep their options")]
    public void Satellite_differs_are_unaffected()
    {
        var create = Assert.ContainsSingle(
            MigrationHarness.SqlServerDiff(null, MigrationHarness.SqlServerModel<PropertyLevelSqlServerContext>()).OfType<CreateIndexOperation>());
        Assert.AreEqual(80, create["SqlServer:FillFactor"]);

        var npgsql = Assert.ContainsSingle(
            MigrationHarness.NpgsqlDiff(null, MigrationHarness.NpgsqlModel<PropertyLevelNpgsqlContext>()).OfType<CreateIndexOperation>());
        Assert.AreEqual("hash", npgsql["Npgsql:IndexMethod"]);
    }

    [TestMethod(DisplayName = "On SQLite, another provider's options are left alone, as in a model shared with SQLite tests")]
    [DataRow(typeof(PropertyLevelSqlServerContext))]
    [DataRow(typeof(PropertyLevelNpgsqlContext))]
    [DataRow(typeof(StorageParameterNpgsqlContext))]
    public void Other_providers_options_are_ignored_on_sqlite(Type context)
    {
        var target = (Microsoft.EntityFrameworkCore.Metadata.IRelationalModel)typeof(MigrationHarness)
                    .GetMethod(nameof(MigrationHarness.SqliteModel), [])!
                    .MakeGenericMethod(context)
                    .Invoke(null, [])!;

        Assert.ContainsSingle(MigrationHarness.Diff<CustomMigrationsModelDiffer>(Sqlite, null, target).OfType<CreateIndexOperation>());
    }

    [TestMethod(DisplayName = "Only the target is checked: a snapshot with options diffs against a model without them")]
    public void Source_with_options_is_not_checked()
    {
        var operations = MigrationHarness.Diff<CustomMigrationsModelDiffer>(
            SqlServer, MigrationHarness.SqlServerModel<PropertyLevelSqlServerContext>(), MigrationHarness.SqlServerModel<PlainContext>());

        // The core never saw the source's options, so the index compares equal and nothing changes.
        Assert.IsEmpty(operations.OfType<CreateIndexOperation>());
    }

    [TestMethod(DisplayName = "The core knows every option a satellite's builder can set")]
    [DataRow(typeof(SqlServerComplexIndexBuilderExtensions), "SQL Server")]
    [DataRow(typeof(NpgsqlComplexIndexBuilderExtensions),    "PostgreSQL")]
    public void Core_knows_every_satellite_option(Type builderExtensions, string provider)
    {
        var satellite = CustomMigrationsModelDiffer.Satellites.Single(s => s.Provider == provider);

        var unknown = BuilderApiParityTests.KeysSetBy(builderExtensions)
                                           .SelectMany(m => m.Keys.Where(k => !satellite.IsOption(k)).Select(k => $"{m.Method} ({k})"))
                                           .ToList();

        Assert.IsEmpty(unknown,
            $"{builderExtensions.Name} sets options the core differ does not recognise, so a model diffed by the core "
          + $"would drop them without a word: {string.Join(", ", unknown)}. Add them to CustomMigrationsModelDiffer.Satellites.");
    }
}
