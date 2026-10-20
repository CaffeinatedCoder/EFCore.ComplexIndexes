using EFCore.ComplexIndexes.PostgreSQL;
using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// #92 made the core differ refuse a satellite's index options on that satellite's provider, and
/// stopped there. With <c>UseComplexIndexes()</c> in place of the satellite's registration (or
/// without its <c>.targets</c>), the core still dropped the PostgreSQL satellite's exclusion
/// constraints, temporal constraints and temporal foreign keys, and the SQL Server satellite's
/// <c>IS NOT NULL</c> filter on a unique index over nullable columns (#88), and
/// <c>EnsureCreated()</c> applied the rest cleanly (#150). It now refuses those too.
/// </summary>
[TestClass]
public class CoreDifferSatelliteDeclarationTests
{
    private class Grant
    {
        public int                    Id        { get; set; }
        public int                    GranteeId { get; set; }
        public int                    GrantId   { get; set; }
        public NpgsqlRange<DateOnly>  Period    { get; set; }
        public string                 Code      { get; set; } = "";
    }

    private class Allocation
    {
        public int                    Id      { get; set; }
        public int                    GrantId { get; set; }
        public NpgsqlRange<DateOnly>  Period  { get; set; }
    }

    private abstract class GrantContext(DbContextOptions options) : DbContext(options)
    {
        protected abstract void Declare(ModelBuilder m, EntityTypeBuilder<Grant> b);

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Grant>(b =>
            {
                b.ToTable("grants");
                b.HasComplexIndex(x => x.Code, isUnique: true, indexName: "ux_code");

                // The same model on SQLite or SQL Server, as in tests that share it: only Npgsql maps a range.
                if (!Database.IsNpgsql())
                    b.Property(x => x.Period).HasConversion(r => r.ToString(), s => NpgsqlRange<DateOnly>.Parse(s));

                Declare(modelBuilder, b);
            });
    }

    private class ExclusionContext(DbContextOptions<ExclusionContext> options) : GrantContext(options)
    {
        protected override void Declare(ModelBuilder m, EntityTypeBuilder<Grant> b)
            => b.HasExclusionConstraint(x => x.GranteeId, x => x.Period, name: "ex_grant_no_overlap");
    }

    private class TemporalContext(DbContextOptions<TemporalContext> options) : GrantContext(options)
    {
        protected override void Declare(ModelBuilder m, EntityTypeBuilder<Grant> b)
            => b.HasTemporalConstraint(x => x.GrantId, x => x.Period, name: "tc_grant");
    }

    private class TemporalForeignKeyContext(DbContextOptions<TemporalForeignKeyContext> options) : GrantContext(options)
    {
        // The principal needs its temporal constraint. Allocation comes before Grant in the model's
        // order, so the foreign key is the declaration the refusal names.
        protected override void Declare(ModelBuilder m, EntityTypeBuilder<Grant> b)
        {
            b.HasTemporalConstraint(x => x.GrantId, x => x.Period, name: "tc_grant");
            m.Entity<Allocation>(a =>
            {
                a.ToTable("allocations");
                if (!Database.IsNpgsql())
                    a.Property(x => x.Period).HasConversion(r => r.ToString(), s => NpgsqlRange<DateOnly>.Parse(s));
                a.HasTemporalForeignKey<Allocation, Grant>(x => x.GrantId, x => x.Period, x => x.GrantId, x => x.Period, name: "fk_allocation_grant");
            });
        }
    }

    // What the core differ is for on these providers: only what it renders itself.
    private class PlainContext(DbContextOptions<PlainContext> options) : GrantContext(options)
    {
        protected override void Declare(ModelBuilder m, EntityTypeBuilder<Grant> b) { }
    }

    private class Registration
    {
        public string Vin { get; set; } = "";
    }

    private class Car
    {
        public int           Id           { get; set; }
        public string        Code         { get; set; } = "";
        public Registration? Registration { get; set; }
    }

    private abstract class CarContext(DbContextOptions options) : DbContext(options)
    {
        protected abstract void Declare(EntityTypeBuilder<Car> b);

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Car>(b =>
            {
                b.ToTable("cars");
                b.Property(x => x.Code).HasMaxLength(20);
                b.ComplexProperty(x => x.Registration, c => c.Property(r => r.Vin).HasMaxLength(17));
                Declare(b);
            });
    }

    private class NullableUniqueContext(DbContextOptions<NullableUniqueContext> options) : CarContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Car> b)
            => b.HasComplexIndex(x => x.Registration!.Vin, isUnique: true, indexName: "ux_vin");
    }

    // Nothing the SQL Server satellite would add: a non-nullable column, a non-unique index, and a
    // unique one that carries its own filter.
    private class NoImplicitFilterContext(DbContextOptions<NoImplicitFilterContext> options) : CarContext(options)
    {
        protected override void Declare(EntityTypeBuilder<Car> b)
        {
            b.HasComplexIndex(x => x.Code, isUnique: true, indexName: "ux_code");
            b.HasComplexIndex(x => x.Registration!.Vin, indexName: "ix_vin");
            b.HasComplexIndex(x => x.Registration!.Vin, isUnique: true, filter: "[Registration_Vin] <> N''", indexName: "ux_vin_set");
        }
    }

    private static DbContextOptions SqlServer => new DbContextOptionsBuilder().UseSqlServer(MigrationHarness.SqlServerConnection).Options;

    private static DbContextOptions Npgsql => MigrationHarness.NpgsqlOptions();

    private static DbContextOptions Sqlite => new DbContextOptionsBuilder().UseSqlite(MigrationHarness.SqliteConnection).Options;

    private static IRelationalModel Model(string provider, Type context)
        => (IRelationalModel)typeof(MigrationHarness)
                            .GetMethods()
                            .Single(m => m.Name == provider + "Model" && m.GetParameters().Length == (provider == "Npgsql" ? 1 : 0))
                            .MakeGenericMethod(context)
                            .Invoke(null, provider == "Npgsql" ? [null] : [])!;

    [TestMethod(DisplayName = "The core differ refuses the PostgreSQL satellite's constraints, naming UseNpgsqlComplexIndexes()")]
    [DataRow(typeof(ExclusionContext),          "exclusion constraint ('ex_grant_no_overlap')")]
    [DataRow(typeof(TemporalContext),           "temporal constraint ('tc_grant')")]
    [DataRow(typeof(TemporalForeignKeyContext), "temporal foreign key ('fk_allocation_grant')")]
    public void Core_differ_refuses_npgsql_constraints(Type context, string what)
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => MigrationHarness.Diff<CustomMigrationsModelDiffer>(Npgsql, null, Model("Npgsql", context)));

        StringAssert.Contains(ex.Message, $"declares a PostgreSQL {what}");
        StringAssert.Contains(ex.Message, "the table would be created without the");
        StringAssert.Contains(ex.Message, "call UseNpgsqlComplexIndexes() instead of UseComplexIndexes()");
    }

    [TestMethod(DisplayName = "The core differ refuses a unique index over nullable columns on SQL Server, naming UseSqlServerComplexIndexes()")]
    public void Core_differ_refuses_sqlserver_nullable_unique_index()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => MigrationHarness.Diff<CustomMigrationsModelDiffer>(SqlServer, null, MigrationHarness.SqlServerModel<NullableUniqueContext>()));

        StringAssert.Contains(ex.Message, "The unique complex index 'ux_vin' on table 'cars' covers the nullable column(s) 'Registration_Vin'");
        StringAssert.Contains(ex.Message, "call UseSqlServerComplexIndexes() instead of UseComplexIndexes()");
    }

    [TestMethod(DisplayName = "On SQL Server the core differ keeps unique indexes the satellite would not filter")]
    public void Core_differ_keeps_sqlserver_indexes_without_an_implicit_filter()
    {
        var creates = MigrationHarness.Diff<CustomMigrationsModelDiffer>(SqlServer, null, MigrationHarness.SqlServerModel<NoImplicitFilterContext>())
                                      .OfType<CreateIndexOperation>()
                                      .ToList();

        Assert.HasCount(3, creates);
    }

    [TestMethod(DisplayName = "UseComplexIndexes() on PostgreSQL refuses at GenerateCreateScript, as EnsureCreated would")]
    public void Runtime_core_registration_on_npgsql_refuses()
    {
        var options = new DbContextOptionsBuilder<ExclusionContext>()
                     .UseNpgsql(MigrationHarness.NpgsqlConnection)
                     .UseComplexIndexes()
                     .Options;

        using var context = new ExclusionContext(options);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => context.Database.GenerateCreateScript());
        StringAssert.Contains(ex.Message, "UseNpgsqlComplexIndexes()");
    }

    [TestMethod(DisplayName = "UseComplexIndexes() on SQL Server refuses a nullable unique index at GenerateCreateScript")]
    public void Runtime_core_registration_on_sqlserver_refuses_nullable_unique_index()
    {
        var options = new DbContextOptionsBuilder<NullableUniqueContext>()
                     .UseSqlServer(MigrationHarness.SqlServerConnection)
                     .UseComplexIndexes()
                     .Options;

        using var context = new NullableUniqueContext(options);

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => context.Database.GenerateCreateScript());
        StringAssert.Contains(ex.Message, "UseSqlServerComplexIndexes()");
    }

    [TestMethod(DisplayName = "The satellites' own registrations render what the core refuses")]
    public void Satellite_registrations_are_unaffected()
    {
        var npgsql = new DbContextOptionsBuilder<ExclusionContext>()
                    .UseNpgsql(MigrationHarness.NpgsqlConnection)
                    .UseNpgsqlComplexIndexes()
                    .Options;
        using (var context = new ExclusionContext(npgsql))
            StringAssert.Contains(context.Database.GenerateCreateScript(), "EXCLUDE USING gist");

        var sqlServer = new DbContextOptionsBuilder<NullableUniqueContext>()
                       .UseSqlServer(MigrationHarness.SqlServerConnection)
                       .UseSqlServerComplexIndexes()
                       .Options;
        using (var context = new NullableUniqueContext(sqlServer))
            StringAssert.Contains(context.Database.GenerateCreateScript(), "WHERE [Registration_Vin] IS NOT NULL");
    }

    [TestMethod(DisplayName = "The satellites' differs are unaffected")]
    [DataRow(typeof(ExclusionContext))]
    [DataRow(typeof(TemporalContext))]
    [DataRow(typeof(TemporalForeignKeyContext))]
    public void Satellite_differs_are_unaffected(Type context)
    {
        Assert.IsNotEmpty(MigrationHarness.NpgsqlDiff(null, Model("Npgsql", context)).OfType<SqlOperation>());
    }

    [TestMethod(DisplayName = "On SQLite, the same declarations are left alone, as in a model shared with SQLite tests")]
    [DataRow(typeof(ExclusionContext))]
    [DataRow(typeof(TemporalContext))]
    [DataRow(typeof(TemporalForeignKeyContext))]
    [DataRow(typeof(NullableUniqueContext))]
    public void Satellite_declarations_are_ignored_on_sqlite(Type context)
    {
        Assert.ContainsSingle(MigrationHarness.Diff<CustomMigrationsModelDiffer>(Sqlite, null, Model("Sqlite", context)).OfType<CreateIndexOperation>());
    }

    [TestMethod(DisplayName = "On SQL Server, the PostgreSQL satellite's constraints are left alone")]
    public void Npgsql_constraints_are_ignored_on_sqlserver()
    {
        Assert.ContainsSingle(MigrationHarness.Diff<CustomMigrationsModelDiffer>(SqlServer, null, Model("SqlServer", typeof(ExclusionContext))).OfType<CreateIndexOperation>());
    }

    [TestMethod(DisplayName = "Only the target is checked: a snapshot with satellite declarations diffs against a model without them")]
    [DataRow("Npgsql",    typeof(ExclusionContext),          typeof(PlainContext))]
    [DataRow("Npgsql",    typeof(TemporalContext),           typeof(PlainContext))]
    [DataRow("Npgsql",    typeof(TemporalForeignKeyContext), typeof(PlainContext))]
    [DataRow("SqlServer", typeof(NullableUniqueContext),     typeof(NoImplicitFilterContext))]
    public void Source_with_satellite_declarations_is_not_checked(string provider, Type source, Type target)
    {
        var options = provider == "Npgsql" ? Npgsql : SqlServer;

        MigrationHarness.Diff<CustomMigrationsModelDiffer>(options, Model(provider, source), Model(provider, target));
    }

    [TestMethod(DisplayName = "The core knows every constraint annotation the PostgreSQL satellite declares")]
    public void Core_knows_every_satellite_constraint_annotation()
    {
        var satellite = CustomMigrationsModelDiffer.Satellites.Single(s => s.Provider == "PostgreSQL");

        CollectionAssert.AreEquivalent(
            new[] { NpgsqlExclusionAnnotations.Constraints, NpgsqlTemporalAnnotations.Constraints, NpgsqlTemporalAnnotations.ForeignKeys },
            satellite.Declarations.Select(d => d.Annotation).ToArray());

        Assert.IsEmpty(CustomMigrationsModelDiffer.Satellites.Single(s => s.Provider == "SQL Server").Declarations);
    }

    [TestMethod(DisplayName = "Each satellite's differ, and only those, says it renders the satellite's declarations")]
    public void Only_satellite_differs_render_satellite_declarations()
    {
        using var npgsql = new DbContext(MigrationHarness.NpgsqlOptions());
        using var sqlServer = new DbContext(SqlServer);

        Assert.IsTrue(MigrationHarness.CreateDiffer<NpgsqlComplexIndexMigrationsModelDiffer>(npgsql).IsSatelliteDiffer);
        Assert.IsTrue(MigrationHarness.CreateDiffer<SqlServerComplexIndexMigrationsModelDiffer>(sqlServer).IsSatelliteDiffer);
        Assert.IsFalse(MigrationHarness.CreateDiffer<CustomMigrationsModelDiffer>(npgsql).IsSatelliteDiffer);
    }
}
