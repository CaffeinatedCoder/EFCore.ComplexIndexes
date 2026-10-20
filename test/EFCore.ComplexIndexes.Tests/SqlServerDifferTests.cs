using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace EFCore.ComplexIndexes.Tests;

[TestClass]
public class SqlServerDifferTests
{
    // ── Harness ──

    private static IRelationalModel BuildRelationalModel<TContext>() where TContext : DbContext
        => MigrationHarness.SqlServerModel<TContext>();

    private static IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
        => MigrationHarness.SqlServerDiff(source, target);

    // End-to-end SQL through the stock SQL Server migrations generator — no runtime wiring needed.
    private static string GenerateSql(IReadOnlyList<MigrationOperation> operations)
    {
        var options = new DbContextOptionsBuilder()
                     .UseSqlServer("Server=localhost;Database=test;Trusted_Connection=True")
                     .Options;

        using var context   = new EmptyContext(options);
        var       generator = context.GetService<IMigrationsSqlGenerator>();

        return string.Join("\n", generator.Generate(operations, model: null).Select(c => c.CommandText));
    }

    private class EmptyContext(DbContextOptions options) : DbContext(options);

    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class Person
    {
        public Guid         Id    { get; set; }
        public string       Name  { get; set; } = "";
        public EmailAddress Email { get; set; } = new();
    }

    private static void MapPerson(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Person> b)
    {
        b.ToTable("person");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasColumnName("name");
        b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasColumnName("email").HasMaxLength(256));
    }

    private class CoveringIndexContext(DbContextOptions<CoveringIndexContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                // Include entries are property paths since v5 ("Name" resolves to its column);
                // unknown entries still pass through verbatim as column names.
                b.HasComplexIndex(x => x.Email.Value, ix => ix
                    .IsUnique()
                    .HasName("ux_person_email")
                    .IncludeProperties("Name")
                    .IsCreatedOnline()
                    .HasFillFactor(80));
            });
    }

    private class FilteredDescendingContext(DbContextOptions<FilteredDescendingContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasComplexCompositeIndex(
                    x => new { x.Name, Email = DbOrder.Desc(x.Email.Value) },
                    filter:    "[name] IS NOT NULL",
                    indexName: "ix_person_name_email");
            });
    }

    private class NullsOrderingContext(DbContextOptions<NullsOrderingContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasComplexIndex(x => DbOrder.NullsFirst(x.Name), indexName: "ix_person_name");
            });
    }

    // ── Tests ──

    [TestMethod(DisplayName = "SQL Server index options are forwarded and rendered by the stock generator")]
    public void SqlServer_options_are_forwarded_and_rendered()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<CoveringIndexContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual("ux_person_email", createIndex.Name);
        Assert.AreEqual(true, createIndex["SqlServer:Online"]);
        Assert.AreEqual(80,   createIndex["SqlServer:FillFactor"]);
        Assert.IsTrue(((string[])createIndex["SqlServer:Include"]!).SequenceEqual(["name"]));

        var sql = GenerateSql([createIndex]);
        StringAssert.Contains(sql, "CREATE UNIQUE INDEX [ux_person_email] ON [person] ([email])");
        StringAssert.Contains(sql, "INCLUDE ([name])");
        StringAssert.Contains(sql, "ONLINE = ON");
        StringAssert.Contains(sql, "FILLFACTOR = 80");
    }

    [TestMethod(DisplayName = "Column facets are not forwarded onto the operation")]
    public void Column_facets_are_not_forwarded()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<CoveringIndexContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.IsNull(createIndex.FindAnnotation("Relational:ColumnName"));
        Assert.IsNull(createIndex.FindAnnotation("MaxLength"));
    }

    [TestMethod(DisplayName = "Filtered descending composite renders WHERE and DESC")]
    public void Filtered_descending_composite_renders()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<FilteredDescendingContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.IsNotNull(createIndex.IsDescending);

        var sql = GenerateSql([createIndex]);
        StringAssert.Contains(sql, "[name], [email] DESC");
        StringAssert.Contains(sql, "WHERE [name] IS NOT NULL");
    }

    [TestMethod(DisplayName = "NULLS FIRST/LAST is rejected with a clear error")]
    public void Nulls_ordering_is_rejected()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => GetDifferences(source: null, target: BuildRelationalModel<NullsOrderingContext>()));

        StringAssert.Contains(exception.Message, "NULLS FIRST/LAST");
    }

    private class DataCompressionContext(DbContextOptions<DataCompressionContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasComplexIndex(x => x.Email.Value, ix => ix
                    .HasName("ix_person_email_compressed")
                    .UseDataCompression(DataCompressionType.Page));
            });
    }

    [TestMethod(DisplayName = "Data compression is forwarded and rendered by the stock generator")]
    public void Data_compression_is_forwarded()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<DataCompressionContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual(DataCompressionType.Page, createIndex["SqlServer:DataCompression"]);

        StringAssert.Contains(GenerateSql(operations), "DATA_COMPRESSION = PAGE");
    }

    // ── Clustered-index combinations SQL Server rejects ──

    private class ClusteredIncludeContext(DbContextOptions<ClusteredIncludeContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasKey(x => x.Id).IsClustered(false);   // free the clustered slot, so INCLUDE is what fails
                b.HasComplexIndex(x => x.Email.Value, ix => ix
                    .HasName("ix_clustered_include").IsClustered().IncludeProperties("Name"));
            });
    }

    private class ClusteredFilterContext(DbContextOptions<ClusteredFilterContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasKey(x => x.Id).IsClustered(false);
                b.HasComplexIndex(x => x.Email.Value,
                                  ix => ix.HasName("ix_clustered_filtered").IsClustered().HasFilter("[name] IS NOT NULL"));
            });
    }

    private class TwoClusteredContext(DbContextOptions<TwoClusteredContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasKey(x => x.Id).IsClustered(false);
                b.HasComplexIndex(x => x.Email.Value, ix => ix.HasName("ix_one").IsClustered());
                b.HasComplexIndex(x => x.Name,        ix => ix.HasName("ix_two").IsClustered());
            });
    }

    private class ClusteredWithClusteredKeyContext(DbContextOptions<ClusteredWithClusteredKeyContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasKey(x => x.Id);   // clustered by default on SQL Server
                b.HasComplexIndex(x => x.Email.Value, ix => ix.HasName("ix_clustered").IsClustered());
            });
    }

    private class ClusteredWithNonclusteredKeyContext(DbContextOptions<ClusteredWithNonclusteredKeyContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasKey(x => x.Id).IsClustered(false);
                b.HasComplexIndex(x => x.Email.Value, ix => ix.HasName("ix_clustered").IsClustered());
            });
    }

    [TestMethod(DisplayName = "A clustered index with INCLUDE columns is rejected")]
    public void Clustered_with_include_is_rejected()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => GetDifferences(source: null, target: BuildRelationalModel<ClusteredIncludeContext>()));

        StringAssert.Contains(ex.Message, "included columns only on nonclustered indexes");
    }

    [TestMethod(DisplayName = "A clustered filtered index is rejected")]
    public void Clustered_with_filter_is_rejected()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => GetDifferences(source: null, target: BuildRelationalModel<ClusteredFilterContext>()));

        StringAssert.Contains(ex.Message, "filtered indexes only on nonclustered indexes");
    }

    [TestMethod(DisplayName = "Two clustered complex indexes on one table are rejected")]
    public void Two_clustered_indexes_are_rejected()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => GetDifferences(source: null, target: BuildRelationalModel<TwoClusteredContext>()));

        StringAssert.Contains(ex.Message, "at most one clustered index per table");
    }

    [TestMethod(DisplayName = "A clustered index is rejected when the primary key already holds the clustered slot")]
    public void Clustered_index_against_clustered_key_is_rejected()
    {
        // The common case: SQL Server makes the primary key clustered unless told otherwise, so a
        // conventionally mapped entity has no clustered slot left.
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => GetDifferences(source: null, target: BuildRelationalModel<ClusteredWithClusteredKeyContext>()));

        StringAssert.Contains(ex.Message, "its primary key is clustered");
    }

    [TestMethod(DisplayName = "A clustered index is allowed when the key is explicitly nonclustered")]
    public void Clustered_index_with_nonclustered_key_is_allowed()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<ClusteredWithNonclusteredKeyContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual(true, createIndex["SqlServer:Clustered"]);
        StringAssert.Contains(GenerateSql(operations), "CREATE CLUSTERED INDEX [ix_clustered]");
    }

    // ── The target table as a whole (#90) ──

    // One clustered complex index; the next context adds a second in a later migration.
    private class OneClusteredContext(DbContextOptions<OneClusteredContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasKey(x => x.Id).IsClustered(false);
                b.HasComplexIndex(x => x.Email.Value, ix => ix.HasName("cx_one").IsClustered());
            });
    }

    private class SecondClusteredLaterContext(DbContextOptions<SecondClusteredLaterContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasKey(x => x.Id).IsClustered(false);
                b.HasComplexIndex(x => x.Email.Value, ix => ix.HasName("cx_one").IsClustered());
                b.HasComplexIndex(x => x.Name,        ix => ix.HasName("cx_two").IsClustered());
            });
    }

    // The clustered slot moves from cx_one to cx_two within one migration: the drop runs first.
    private class ClusteredMovedContext(DbContextOptions<ClusteredMovedContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasKey(x => x.Id).IsClustered(false);
                b.HasComplexIndex(x => x.Email.Value, ix => ix.HasName("cx_one"));
                b.HasComplexIndex(x => x.Name,        ix => ix.HasName("cx_two").IsClustered());
            });
    }

    // What a snapshot scaffolded before 5.0.2 can hold: a clustered key and a clustered complex
    // index. The next context is the model that resolves it.
    private class ClusteredNonclusteredFixContext(DbContextOptions<ClusteredNonclusteredFixContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasComplexIndex(x => x.Email.Value, ix => ix.HasName("ix_clustered"));
            });
    }

    private class ClusteredWithClusteredNativeIndexContext(DbContextOptions<ClusteredWithClusteredNativeIndexContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasKey(x => x.Id).IsClustered(false);
                b.HasIndex(x => x.Name).IsClustered();
                b.HasComplexIndex(x => x.Email.Value, ix => ix.HasName("ix_clustered").IsClustered());
            });
    }

    private class ClusteredWithClusteredAlternateKeyContext(DbContextOptions<ClusteredWithClusteredAlternateKeyContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasKey(x => x.Id).IsClustered(false);
                b.HasAlternateKey(x => x.Name).HasName("AK_person_name").IsClustered();
                b.HasComplexIndex(x => x.Email.Value, ix => ix.HasName("ix_clustered").IsClustered());
            });
    }

    private class NamedLikeKeyContext(DbContextOptions<NamedLikeKeyContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasComplexIndex(x => x.Email.Value, indexName: "pk_person");
            });
    }

    private class NamedLikeAlternateKeyContext(DbContextOptions<NamedLikeAlternateKeyContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasAlternateKey(x => x.Name).HasName("AK_person_name");
                b.HasComplexIndex(x => x.Email.Value, indexName: "AK_person_name");
            });
    }

    // Index names are per table on SQL Server: another table's key name is free here.
    private class Pet
    {
        public Guid   Id   { get; set; }
        public string Name { get; set; } = "";
    }

    private class NamedLikeOtherTablesKeyContext(DbContextOptions<NamedLikeOtherTablesKeyContext> options) : DbContext(options)
    {
        public DbSet<Person> People => Set<Person>();
        public DbSet<Pet>    Pets   => Set<Pet>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Pet>(b => b.ToTable("pet"));
            modelBuilder.Entity<Person>(b =>
            {
                MapPerson(b);
                b.HasComplexIndex(x => x.Email.Value, indexName: "PK_pet");
            });
        }
    }

    [TestMethod(DisplayName = "A second clustered complex index added in a later migration is rejected")]
    public void Second_clustered_index_in_later_migration_is_rejected()
    {
        // cx_one already exists, so the migration creates only cx_two: counting the creates alone
        // scaffolded CREATE CLUSTERED INDEX [cx_two], which failed at apply.
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => GetDifferences(BuildRelationalModel<OneClusteredContext>(), BuildRelationalModel<SecondClusteredLaterContext>()));

        StringAssert.Contains(ex.Message, "at most one clustered index per table");
        StringAssert.Contains(ex.Message, "'cx_one'");
        StringAssert.Contains(ex.Message, "'cx_two'");
    }

    [TestMethod(DisplayName = "Moving the clustered slot between complex indexes in one migration is allowed")]
    public void Clustered_slot_moving_between_complex_indexes_is_allowed()
    {
        var operations = GetDifferences(BuildRelationalModel<OneClusteredContext>(), BuildRelationalModel<ClusteredMovedContext>());

        var drops = operations.OfType<DropIndexOperation>().Select(o => o.Name).ToList();
        CollectionAssert.AreEquivalent(new[] { "cx_one" }, drops);
        var create = operations.OfType<CreateIndexOperation>().Single(o => o.Name == "cx_two");
        Assert.AreEqual(true, create["SqlServer:Clustered"]);
    }

    [TestMethod(DisplayName = "A snapshot holding a clustered-slot conflict stays diffable")]
    public void Clustered_conflict_in_the_source_model_is_not_validated()
    {
        // The source carries a clustered key and a clustered complex index, which only a snapshot
        // scaffolded before 5.0.2 can hold. The migration that makes the index nonclustered was
        // refused, because the check ran on the source too.
        var operations = GetDifferences(BuildRelationalModel<ClusteredWithClusteredKeyContext>(),
                                        BuildRelationalModel<ClusteredNonclusteredFixContext>());

        Assert.AreEqual("ix_clustered", Assert.ContainsSingle(operations.OfType<DropIndexOperation>()).Name);
        var create = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.IsNull(create.FindAnnotation("SqlServer:Clustered"));
    }

    [TestMethod(DisplayName = "A clustered index is rejected when a native index already holds the clustered slot")]
    public void Clustered_index_against_clustered_native_index_is_rejected()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => GetDifferences(source: null, target: BuildRelationalModel<ClusteredWithClusteredNativeIndexContext>()));

        StringAssert.Contains(ex.Message, "the native index on (name) is already clustered");
    }

    [TestMethod(DisplayName = "A clustered index is rejected when a unique constraint already holds the clustered slot")]
    public void Clustered_index_against_clustered_alternate_key_is_rejected()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => GetDifferences(source: null, target: BuildRelationalModel<ClusteredWithClusteredAlternateKeyContext>()));

        StringAssert.Contains(ex.Message, "unique constraint 'AK_person_name' is already clustered");
    }

    [TestMethod(DisplayName = "A complex index named like its table's primary key is rejected, whatever the case")]
    public void Index_named_like_primary_key_is_rejected()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => GetDifferences(source: null, target: BuildRelationalModel<NamedLikeKeyContext>()));

        StringAssert.Contains(ex.Message, "'pk_person' on table 'person' has the same name as the table's primary key 'PK_person'");
    }

    [TestMethod(DisplayName = "A complex index named like a unique constraint of its table is rejected")]
    public void Index_named_like_alternate_key_is_rejected()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => GetDifferences(source: null, target: BuildRelationalModel<NamedLikeAlternateKeyContext>()));

        StringAssert.Contains(ex.Message, "same name as the table's unique constraint on (name) 'AK_person_name'");
    }

    [TestMethod(DisplayName = "A complex index may share its name with another table's key")]
    public void Index_named_like_another_tables_key_is_allowed()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<NamedLikeOtherTablesKeyContext>());

        Assert.AreEqual("PK_pet", Assert.ContainsSingle(operations.OfType<CreateIndexOperation>()).Name);
    }
}
