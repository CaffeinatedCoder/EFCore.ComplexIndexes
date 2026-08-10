using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

[TestClass]
public class NpgsqlExclusionConstraintDifferTests
{
    private const string CreateExtensionPrefix = "CREATE EXTENSION IF NOT EXISTS btree_gist";

    // ── Helpers ──

    private static IRelationalModel BuildRelationalModel<TContext>() where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
                     .UseNpgsql("Host=localhost;Database=test")
                     .Options;

        using var context = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        return context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
    }

    private static IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        var options = new DbContextOptionsBuilder()
                     .UseNpgsql("Host=localhost;Database=test")
                     .Options;

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

    private static List<string> ExclusionSql(IEnumerable<MigrationOperation> operations)
        => [.. operations.OfType<SqlOperation>().Select(o => o.Sql).Where(s => s.Contains("EXCLUDE") || s.Contains("DROP CONSTRAINT"))];

    private class EmptyContext(DbContextOptions options) : DbContext(options);

    // The AuditOffice shape: overlap protection per (grantee, role), ignoring revoked grants.
    private class RoleGrant
    {
        public int                   Id        { get; set; }
        public int                   GranteeId { get; set; }
        public int                   RoleId    { get; set; }
        public NpgsqlRange<DateOnly> Period    { get; set; }
        public DateOnly?             RevokedAt { get; set; }
    }

    private static void MapGrant(EntityTypeBuilder<RoleGrant> b)
    {
        b.ToTable("role_grants");
        b.HasKey(x => x.Id);
        b.Property(x => x.GranteeId).HasColumnName("grantee_id");
        b.Property(x => x.RoleId).HasColumnName("role_id");
        b.Property(x => x.Period).HasColumnName("period");
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
    }

    private class PlainContext(DbContextOptions<PlainContext> options) : DbContext(options)
    {
        public DbSet<RoleGrant> Grants => Set<RoleGrant>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RoleGrant>(MapGrant);
    }

    private class FilteredExclusionContext(DbContextOptions<FilteredExclusionContext> options) : DbContext(options)
    {
        public DbSet<RoleGrant> Grants => Set<RoleGrant>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RoleGrant>(b =>
            {
                MapGrant(b);
                b.HasExclusionConstraint(
                    equalityColumns: x => new { x.GranteeId, x.RoleId },
                    overlapsColumn:  x => x.Period,
                    filter:          "revoked_at IS NULL",
                    name:            "ex_role_grant_active_period");
            });
    }

    private class DefaultNameExclusionContext(DbContextOptions<DefaultNameExclusionContext> options) : DbContext(options)
    {
        public DbSet<RoleGrant> Grants => Set<RoleGrant>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RoleGrant>(b =>
            {
                MapGrant(b);
                b.HasExclusionConstraint(x => x.GranteeId, x => x.Period);
            });
    }

    private class BuilderExclusionContext(DbContextOptions<BuilderExclusionContext> options) : DbContext(options)
    {
        public DbSet<RoleGrant> Grants => Set<RoleGrant>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<RoleGrant>(b =>
            {
                MapGrant(b);
                b.HasExclusionConstraint(ex => ex
                    .WithExpression("lower(period::text)", "=")
                    .WithOverlaps(x => x.Period)
                    .UseMethod("gist")
                    .HasName("ex_builder_shape")
                    .IsDeferrable(initiallyDeferred: true));
            });
    }

    private class SuppressedExclusionContext(DbContextOptions<SuppressedExclusionContext> options) : DbContext(options)
    {
        public DbSet<RoleGrant> Grants => Set<RoleGrant>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.SuppressTemporalExtensionAutoInjection();
            modelBuilder.Entity<RoleGrant>(b =>
            {
                MapGrant(b);
                b.HasExclusionConstraint(x => x.GranteeId, x => x.Period);
            });
        }
    }

    // Exclusion over complex-property members — the package's home turf.
    private class Slot
    {
        public int                   Resource { get; set; }
        public NpgsqlRange<DateOnly> Period   { get; set; }
    }

    private class Booking
    {
        public int  Id   { get; set; }
        public Slot Slot { get; set; } = new();
    }

    private class ComplexMemberExclusionContext(DbContextOptions<ComplexMemberExclusionContext> options) : DbContext(options)
    {
        public DbSet<Booking> Bookings => Set<Booking>();
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Booking>(b =>
            {
                b.ToTable("bookings");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Slot, c =>
                {
                    c.Property(x => x.Resource).HasColumnName("resource");
                    c.Property(x => x.Period).HasColumnName("period");
                });
                b.HasExclusionConstraint(x => x.Slot.Resource, x => x.Slot.Period, name: "ex_booking_slot");
            });
    }

    private class EmptyModelContext(DbContextOptions<EmptyModelContext> options) : DbContext(options);

    // ── Tests ──

    [TestMethod(DisplayName = "Simple overload emits the full EXCLUDE DDL with predicate")]
    public void Simple_overload_emits_exclude_ddl()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<FilteredExclusionContext>());

        var sql = Assert.ContainsSingle(ExclusionSql(operations));
        Assert.AreEqual(
            "ALTER TABLE \"role_grants\" ADD CONSTRAINT \"ex_role_grant_active_period\" " +
            "EXCLUDE USING gist (\"grantee_id\" WITH =, \"role_id\" WITH =, \"period\" WITH &&) " +
            "WHERE (revoked_at IS NULL);",
            sql);
    }

    [TestMethod(DisplayName = "Exclusion constraint auto-injects CREATE EXTENSION btree_gist first")]
    public void Exclusion_injects_extension()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<FilteredExclusionContext>());

        var first = operations[0] as SqlOperation;
        Assert.IsNotNull(first, "First operation should be the CREATE EXTENSION SqlOperation.");
        StringAssert.Contains(first!.Sql, CreateExtensionPrefix);
    }

    [TestMethod(DisplayName = "Auto-injection can be suppressed")]
    public void Injection_can_be_suppressed()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<SuppressedExclusionContext>());

        Assert.IsFalse(operations.OfType<SqlOperation>().Any(o => o.Sql.Contains("btree_gist")));
        Assert.ContainsSingle(ExclusionSql(operations));
    }

    [TestMethod(DisplayName = "Default constraint name is derived from the resolved columns")]
    public void Default_name_is_derived_from_columns()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<DefaultNameExclusionContext>());

        var sql = Assert.ContainsSingle(ExclusionSql(operations));
        StringAssert.Contains(sql, "\"EX_role_grants_grantee_id_period\"");
    }

    [TestMethod(DisplayName = "Builder overload renders expression parts, method, and deferrability")]
    public void Builder_overload_renders_full_shape()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<BuilderExclusionContext>());

        var sql = Assert.ContainsSingle(ExclusionSql(operations));
        StringAssert.Contains(sql, "EXCLUDE USING gist ((lower(period::text)) WITH =, \"period\" WITH &&)");
        StringAssert.Contains(sql, "DEFERRABLE INITIALLY DEFERRED");
    }

    [TestMethod(DisplayName = "Complex-property members resolve to their mapped columns")]
    public void Complex_member_paths_resolve()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<ComplexMemberExclusionContext>());

        var sql = Assert.ContainsSingle(ExclusionSql(operations));
        StringAssert.Contains(sql, "EXCLUDE USING gist (\"resource\" WITH =, \"period\" WITH &&)");
    }

    [TestMethod(DisplayName = "No-op diff produces no exclusion operations")]
    public void No_op_diff_produces_nothing()
    {
        var operations = GetDifferences(
            BuildRelationalModel<FilteredExclusionContext>(),
            BuildRelationalModel<FilteredExclusionContext>());

        Assert.IsEmpty(ExclusionSql(operations));
        Assert.IsFalse(operations.OfType<SqlOperation>().Any(o => o.Sql.Contains("btree_gist")));
    }

    [TestMethod(DisplayName = "Removing an exclusion constraint drops it by name, before other operations")]
    public void Removing_exclusion_drops_it()
    {
        var operations = GetDifferences(
            source: BuildRelationalModel<FilteredExclusionContext>(),
            target: BuildRelationalModel<PlainContext>());

        var sql = Assert.ContainsSingle(ExclusionSql(operations));
        Assert.AreEqual("ALTER TABLE \"role_grants\" DROP CONSTRAINT \"ex_role_grant_active_period\";", sql);
    }

    [TestMethod(DisplayName = "Dropping the table does not emit a separate DROP CONSTRAINT")]
    public void Dropped_table_emits_no_drop()
    {
        var operations = GetDifferences(
            source: BuildRelationalModel<FilteredExclusionContext>(),
            target: BuildRelationalModel<EmptyModelContext>());

        Assert.IsNotEmpty(operations.OfType<DropTableOperation>());
        Assert.IsEmpty(ExclusionSql(operations));
    }

    [TestMethod(DisplayName = "Serializer round-trips all definition facets")]
    public void Serializer_roundtrips_definition()
    {
        var definition = new ExclusionConstraintDefinition
                         {
                             Parts =
                             [
                                 new ExclusionPartDefinition { PropertyPath = "Slot.Resource", Operator = "=" },
                                 new ExclusionPartDefinition { Expression   = "lower(code)",   Operator = "&&" }
                             ],
                             Method            = "spgist",
                             Filter            = "deleted_at IS NULL",
                             Name              = "ex_roundtrip",
                             Deferrable        = true,
                             InitiallyDeferred = true
                         };

        var roundtripped = Assert.ContainsSingle(
            ExclusionConstraintSerializer.Deserialize(ExclusionConstraintSerializer.Serialize([definition])));

        Assert.AreEqual(definition, roundtripped);
    }
}
