using EFCore.ComplexIndexes.PostgreSQL;
using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// An index option from the other satellite must fail at <c>migrations add</c> whichever way it was
/// declared. Entity-level options reach the operation unfiltered and were always rejected; the
/// property-level path goes through the forwarding whitelist, which dropped them without a word — a
/// property-level <c>.UseGin()</c> diffed by the SQL Server satellite scaffolded a plain B-tree.
/// The PostgreSQL differ had the mirror gap for both paths: <c>SqlServer:*</c> options passed
/// through to Npgsql's generator, which ignored them.
/// </summary>
[TestClass]
public class CrossProviderOptionRejectionTests
{
    private class Payload
    {
        public string Json { get; set; } = "";
    }

    private class Document
    {
        public Guid    Id      { get; set; }
        public string  Title   { get; set; } = "";
        public Payload Payload { get; set; } = new();
    }

    private class PropertyLevelGinContext(DbContextOptions<PropertyLevelGinContext> options) : DbContext(options)
    {
        public DbSet<Document> Documents => Set<Document>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Document>(b =>
            {
                b.ToTable("docs");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Payload, c => c.Property(x => x.Json).HasComplexIndex(ix => ix.UseGin()));
            });
    }

    private class PropertyLevelClusteredContext(DbContextOptions<PropertyLevelClusteredContext> options) : DbContext(options)
    {
        public DbSet<Document> Documents => Set<Document>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Document>(b =>
            {
                b.ToTable("docs");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Payload, c => c.Property(x => x.Json).HasComplexIndex(ix => ix.HasFillFactor(80)));
            });
    }

    private class EntityLevelFillFactorContext(DbContextOptions<EntityLevelFillFactorContext> options) : DbContext(options)
    {
        public DbSet<Document> Documents => Set<Document>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Document>(b =>
            {
                b.ToTable("docs");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Payload);
                b.HasComplexIndex(x => x.Payload.Json, ix => ix.HasFillFactor(80));
            });
    }

    [TestMethod(DisplayName = "SQL Server rejects a property-level PostgreSQL option instead of dropping it")]
    public void SqlServer_rejects_property_level_npgsql_option()
    {
        var target = MigrationHarness.SqlServerModel<PropertyLevelGinContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.SqlServerDiff(null, target));

        StringAssert.Contains(exception.Message, "Npgsql:IndexMethod");
        StringAssert.Contains(exception.Message, "SQL Server satellite");
    }

    [TestMethod(DisplayName = "PostgreSQL rejects a property-level SQL Server option instead of dropping it")]
    public void Npgsql_rejects_property_level_sqlserver_option()
    {
        var target = MigrationHarness.NpgsqlModel<PropertyLevelClusteredContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(null, target));

        StringAssert.Contains(exception.Message, "SqlServer:FillFactor");
        StringAssert.Contains(exception.Message, "PostgreSQL satellite");
    }

    [TestMethod(DisplayName = "PostgreSQL rejects an entity-level SQL Server option instead of passing it to a generator that ignores it")]
    public void Npgsql_rejects_entity_level_sqlserver_option()
    {
        var target = MigrationHarness.NpgsqlModel<EntityLevelFillFactorContext>();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => MigrationHarness.NpgsqlDiff(null, target));

        StringAssert.Contains(exception.Message, "SqlServer:FillFactor");
    }
}
