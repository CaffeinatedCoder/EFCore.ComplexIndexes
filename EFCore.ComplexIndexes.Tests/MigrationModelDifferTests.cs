using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace EFCore.ComplexIndexes.Tests;

[TestClass]
public class MigrationsModelDifferTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public MigrationsModelDifferTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
    }

    public void Dispose() => _connection.Dispose();

    // ── Helper: Build a relational model from a DbContext configurator ──

    private IRelationalModel BuildRelationalModel<TContext>()
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
                     .UseSqlite(_connection)
                     .Options;

        using var context = (TContext)Activator.CreateInstance(typeof(TContext), options)!;
        var       model   = context.GetService<IDesignTimeModel>().Model;
        return model.GetRelationalModel();
    }

    // ── Helper: Run the differ ──

    private IReadOnlyList<MigrationOperation> GetDifferences(
        IRelationalModel? source,
        IRelationalModel? target
    )
    {
        var options = new DbContextOptionsBuilder()
                     .UseSqlite(_connection)
                     .Options;

        using var context = new EmptyContext(options);

        var differ = new CustomMigrationsModelDiffer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<IMigrationsAnnotationProvider>(),
            context.GetService<IRelationalAnnotationProvider>(),
            context.GetService<IRowIdentityMapFactory>(),
            context.GetService<CommandBatchPreparerDependencies>()
        );

        return differ.GetDifferences(source, target);
    }

    // ── Contexts for testing ──

    private class EmptyContext(DbContextOptions options) : DbContext(options);

    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class PersonV1
    {
        public Guid         Id           { get; set; }
        public string       Name         { get; set; } = "";
        public EmailAddress EmailAddress { get; set; } = new();
    }

    // V1: no indexes
    private class ContextV1(DbContextOptions<ContextV1> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.ComplexProperty(x => x.EmailAddress, c => { c.Property(x => x.Value).HasColumnName("email_address"); });
            });
        }
    }

    // V2: single-column index added
    private class ContextV2(DbContextOptions<ContextV2> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.ComplexProperty(x => x.EmailAddress, c =>
                {
                    c.Property(x => x.Value)
                     .HasColumnName("email_address")
                     .HasComplexIndex(isUnique: true);
                });
            });
        }
    }

    // V3: composite index
    private class ContextV3(DbContextOptions<ContextV3> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Name).HasColumnName("name");
                builder.ComplexProperty(x => x.EmailAddress, c => { c.Property(x => x.Value).HasColumnName("email_address"); });
                builder.HasComplexCompositeIndex(
                    x => new { x.Name, x.EmailAddress.Value },
                    isUnique: true);
            });
        }
    }

    private enum VacancySource
    {
        Source1,
        Source2,
        Source3
    }

    private class Vacancy
    {
        public Guid          Id     { get; set; }
        public VacancyOrigin Origin { get; set; } = new();
    }

    private class VacancyOrigin
    {
        public VacancySource Source     { get; set; }
        public string        ExternalId { get; set; } = "";
    }

    private class VacancyCompositeConventionColumnNamesContext(
        DbContextOptions<VacancyCompositeConventionColumnNamesContext> options) : DbContext(options)
    {
        public DbSet<Vacancy> Vacancies => Set<Vacancy>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Vacancy>(builder =>
            {
                builder.ToTable("Vacancies");
                builder.HasKey(x => x.Id);
                builder.ComplexProperty(x => x.Origin);

                builder.HasComplexCompositeIndex(
                    vacancy => new { vacancy.Origin.Source, vacancy.Origin.ExternalId },
                    isUnique: true);
            });
        }
    }

    private class VacancySingleConventionColumnNameContext(
        DbContextOptions<VacancySingleConventionColumnNameContext> options) : DbContext(options)
    {
        public DbSet<Vacancy> Vacancies => Set<Vacancy>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Vacancy>(builder =>
            {
                builder.ToTable("Vacancies");
                builder.HasKey(x => x.Id);

                builder.ComplexProperty(x => x.Origin, complex =>
                {
                    complex.Property(x => x.Source).HasComplexIndex(isUnique: true);
                });
            });
        }
    }

    private class VacancyCompositeExplicitColumnNamesContext(
        DbContextOptions<VacancyCompositeExplicitColumnNamesContext> options) : DbContext(options)
    {
        public DbSet<Vacancy> Vacancies => Set<Vacancy>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Vacancy>(builder =>
            {
                builder.ToTable("Vacancies");
                builder.HasKey(x => x.Id);

                builder.ComplexProperty(x => x.Origin, complex =>
                {
                    complex.Property(x => x.Source).HasColumnName("vacancy_source");
                    complex.Property(x => x.ExternalId).HasColumnName("external_vacancy_id");
                });

                builder.HasComplexCompositeIndex(
                    vacancy => new { vacancy.Origin.Source, vacancy.Origin.ExternalId },
                    isUnique: true);
            });
        }
    }

    // ── Tests ──

    [TestMethod(DisplayName = "Initial migration creates index")]
    public void Initial_migration_creates_index()
    {
        var target     = BuildRelationalModel<ContextV2>();
        var operations = GetDifferences(source: null, target: target);

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual("person",        createIndex.Table);
        Assert.AreEqual("email_address", Assert.ContainsSingle(createIndex.Columns));
        Assert.IsTrue(createIndex.IsUnique);
        Assert.AreEqual("IX_person_email_address", createIndex.Name);
    }

    [TestMethod(DisplayName = "Adding index to existing table")]
    public void Adding_index_to_existing_table()
    {
        var source     = BuildRelationalModel<ContextV1>();
        var target     = BuildRelationalModel<ContextV2>();
        var operations = GetDifferences(source, target);

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual("email_address", Assert.ContainsSingle(createIndex.Columns));
        Assert.IsTrue(createIndex.IsUnique);
    }

    [TestMethod(DisplayName = "Removing index from existing table")]
    public void Removing_index_from_existing_table()
    {
        var source     = BuildRelationalModel<ContextV2>();
        var target     = BuildRelationalModel<ContextV1>();
        var operations = GetDifferences(source, target);

        var dropIndex = Assert.ContainsSingle(operations.OfType<DropIndexOperation>());
        Assert.AreEqual("IX_person_email_address", dropIndex.Name);
    }

    [TestMethod(DisplayName = "No changes produces no index operations")]
    public void No_changes_produces_no_index_operations()
    {
        var source     = BuildRelationalModel<ContextV2>();
        var target     = BuildRelationalModel<ContextV2>();
        var operations = GetDifferences(source, target);

        Assert.IsEmpty(operations.OfType<CreateIndexOperation>());
        Assert.IsEmpty(operations.OfType<DropIndexOperation>());
    }

    [TestMethod(DisplayName = "Composite index creates multi-column")]
    public void Composite_index_creates_multi_column()
    {
        var target     = BuildRelationalModel<ContextV3>();
        var operations = GetDifferences(source: null, target: target);

        var      createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        string[] columnNames = ["name", "email_address"];
        Assert.IsTrue(createIndex.Columns.SequenceEqual(columnNames));
        Assert.IsTrue(createIndex.IsUnique);
    }

    [TestMethod(DisplayName = "Composite complex index uses convention-based complex column names")]
    public void Composite_complex_index_uses_convention_based_complex_column_names()
    {
        var target     = BuildRelationalModel<VacancyCompositeConventionColumnNamesContext>();
        var operations = GetDifferences(source: null, target: target);

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());

        string[] expectedColumnNames = ["Origin_Source", "Origin_ExternalId"];

        Assert.AreEqual("Vacancies", createIndex.Table);
        Assert.IsTrue(createIndex.Columns.SequenceEqual(expectedColumnNames));
        Assert.IsTrue(createIndex.IsUnique);
        Assert.AreEqual("IX_Vacancies_Origin_Source_Origin_ExternalId", createIndex.Name);
    }

    [TestMethod(DisplayName = "Single-column complex index uses convention-based complex column name")]
    public void Single_column_complex_index_uses_convention_based_complex_column_name()
    {
        var target     = BuildRelationalModel<VacancySingleConventionColumnNameContext>();
        var operations = GetDifferences(source: null, target: target);

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());

        Assert.AreEqual("Vacancies", createIndex.Table);
        Assert.AreEqual("Origin_Source", Assert.ContainsSingle(createIndex.Columns));
        Assert.IsTrue(createIndex.IsUnique);
        Assert.AreEqual("IX_Vacancies_Origin_Source", createIndex.Name);
    }

    [TestMethod(DisplayName = "Composite complex index uses explicit complex column names")]
    public void Composite_complex_index_uses_explicit_complex_column_names()
    {
        var target     = BuildRelationalModel<VacancyCompositeExplicitColumnNamesContext>();
        var operations = GetDifferences(source: null, target: target);

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());

        string[] expectedColumnNames = ["vacancy_source", "external_vacancy_id"];

        Assert.AreEqual("Vacancies", createIndex.Table);
        Assert.IsTrue(createIndex.Columns.SequenceEqual(expectedColumnNames));
        Assert.IsTrue(createIndex.IsUnique);
        Assert.AreEqual("IX_Vacancies_vacancy_source_external_vacancy_id", createIndex.Name);
    }

    [TestMethod(DisplayName = "Filtered index preserves filter")]
    public void Filtered_index_preserves_filter()
    {
        // Build a context with a filtered index
        // (using ContextV2 modified with filter, or add another context variant)
        var target     = BuildRelationalModel<ContextWithFilteredIndex>();
        var operations = GetDifferences(source: null, target: target);

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual("deleted_at IS NULL", createIndex.Filter);
    }

    private class ContextWithFilteredIndex(
        DbContextOptions<ContextWithFilteredIndex> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.ComplexProperty(x => x.EmailAddress, c =>
                {
                    c.Property(x => x.Value)
                     .HasColumnName("email_address")
                     .HasComplexIndex(
                          isUnique: true,
                          filter: "deleted_at IS NULL");
                });
            });
        }
    }

    // Composite index with a descending column
    private class ContextWithDescendingComposite(
        DbContextOptions<ContextWithDescendingComposite> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Name).HasColumnName("name");
                builder.ComplexProperty(x => x.EmailAddress, c => { c.Property(x => x.Value).HasColumnName("email_address"); });
                builder.HasComplexCompositeIndex(x => new { x.Name, Email = DbOrder.Desc(x.EmailAddress.Value) });
            });
        }
    }

    [TestMethod(DisplayName = "Composite index sets per-column descending order")]
    public void Composite_index_sets_descending_order()
    {
        var target     = BuildRelationalModel<ContextWithDescendingComposite>();
        var operations = GetDifferences(source: null, target: target);

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.IsTrue(createIndex.Columns.SequenceEqual(["name", "email_address"]));
        Assert.IsNotNull(createIndex.IsDescending);
        Assert.IsTrue(createIndex.IsDescending!.SequenceEqual([false, true]));
    }

    [TestMethod(DisplayName = "All-ascending composite leaves IsDescending null")]
    public void Ascending_composite_leaves_isdescending_null()
    {
        var target     = BuildRelationalModel<ContextV3>();
        var operations = GetDifferences(source: null, target: target);

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.IsNull(createIndex.IsDescending);
    }

    // V4: expression index on a regular column
    private class ContextWithExpressionIndex(
        DbContextOptions<ContextWithExpressionIndex> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Name).HasColumnName("name");
                builder.ComplexProperty(x => x.EmailAddress, c => { c.Property(x => x.Value).HasColumnName("email_address"); });
                builder.HasExpressionIndex("lower(name)");
            });
        }
    }

    [TestMethod(DisplayName = "Expression index emits IndexParts annotation and no columns")]
    public void Expression_index_emits_parts_annotation()
    {
        var target     = BuildRelationalModel<ContextWithExpressionIndex>();
        var operations = GetDifferences(source: null, target: target);

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual("person", createIndex.Table);
        // Columns must be non-empty (EF rejects an empty list); it carries the verbatim part value
        // plus the wiring sentinel, so a missing custom generator fails loudly instead of applying
        // a broken index.
        Assert.IsTrue(createIndex.Columns.SequenceEqual(["lower(name)", CustomMigrationsModelDiffer.RuntimeWiringSentinel]));
        Assert.AreEqual("IX_person_lowername", createIndex.Name);

        var partsJson = createIndex.FindAnnotation(ComplexIndexAnnotations.IndexParts)?.Value as string;
        Assert.IsNotNull(partsJson);

        var parts = IndexPartsSerializer.Deserialize(partsJson);
        var part  = Assert.ContainsSingle(parts);
        Assert.IsTrue(part.IsExpression);
        Assert.AreEqual("lower(name)", part.Value);
    }

    [TestMethod(DisplayName = "Unchanged expression index produces no operations")]
    public void Unchanged_expression_index_is_noop()
    {
        var source     = BuildRelationalModel<ContextWithExpressionIndex>();
        var target     = BuildRelationalModel<ContextWithExpressionIndex>();
        var operations = GetDifferences(source, target);

        Assert.IsEmpty(operations.OfType<CreateIndexOperation>());
        Assert.IsEmpty(operations.OfType<DropIndexOperation>());
    }

    [TestMethod(DisplayName = "Removing expression index drops it by name")]
    public void Removing_expression_index_drops_it()
    {
        var source     = BuildRelationalModel<ContextWithExpressionIndex>();
        var target     = BuildRelationalModel<ContextV1>();
        var operations = GetDifferences(source, target);

        var dropIndex = Assert.ContainsSingle(operations.OfType<DropIndexOperation>());
        Assert.AreEqual("IX_person_lowername", dropIndex.Name);
    }

    [TestMethod(DisplayName = "Dropping table does not emit separate drop index")]
    public void Dropping_table_does_not_emit_separate_drop_index()
    {
        var source = BuildRelationalModel<ContextV2>();
        // Target has no entities at all
        var target     = BuildRelationalModel<EmptyModelContext>();
        var operations = GetDifferences(source, target);

        // Table drop should exist, but no separate DropIndex
        Assert.IsNotEmpty(operations.OfType<DropTableOperation>());
        Assert.IsEmpty(operations.OfType<DropIndexOperation>());
    }

    private class EmptyModelContext(
        DbContextOptions<EmptyModelContext> options) : DbContext(options);

    // ── Entity-level single-column indexes and same-column dedup semantics (v5) ──

    private class EntityLevelSingleIndexContext(
        DbContextOptions<EntityLevelSingleIndexContext> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.ComplexProperty(x => x.EmailAddress, c => c.Property(x => x.Value).HasColumnName("email_address"));
                builder.HasComplexIndex(x => x.EmailAddress.Value, isUnique: true);
            });
    }

    [TestMethod(DisplayName = "Entity-level single-column index resolves the complex member")]
    public void Entity_level_single_column_index_is_created()
    {
        var target     = BuildRelationalModel<EntityLevelSingleIndexContext>();
        var operations = GetDifferences(source: null, target: target);

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual("email_address", Assert.ContainsSingle(createIndex.Columns));
        Assert.IsTrue(createIndex.IsUnique);
        Assert.AreEqual("IX_person_email_address", createIndex.Name);
    }

    private class EntityLevelDescendingIndexContext(
        DbContextOptions<EntityLevelDescendingIndexContext> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Name).HasColumnName("name");
                builder.ComplexProperty(x => x.EmailAddress, c => c.Property(x => x.Value).HasColumnName("email_address"));
                builder.HasComplexIndex(x => DbOrder.Desc(x.Name));
            });
    }

    [TestMethod(DisplayName = "Entity-level single-column index honors DbOrder.Desc")]
    public void Entity_level_single_column_index_supports_descending()
    {
        var target     = BuildRelationalModel<EntityLevelDescendingIndexContext>();
        var operations = GetDifferences(source: null, target: target);

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual("name", Assert.ContainsSingle(createIndex.Columns));
        Assert.IsNotNull(createIndex.IsDescending);
        Assert.IsTrue(createIndex.IsDescending!.SequenceEqual([true]));
    }

    // The soft-delete pattern: a unique filtered index and a plain index over the same column.
    private class TwoFilteredIndexesContext(
        DbContextOptions<TwoFilteredIndexesContext> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.ComplexProperty(x => x.EmailAddress, c => c.Property(x => x.Value).HasColumnName("email_address"));
                builder.HasComplexIndex(x => x.EmailAddress.Value, isUnique: true, filter: "deleted_at IS NULL", indexName: "ux_person_email_active");
                builder.HasComplexIndex(x => x.EmailAddress.Value, indexName: "ix_person_email_all");
            });
    }

    [TestMethod(DisplayName = "Same column with different filters yields two coexisting indexes")]
    public void Same_column_with_different_filters_coexists_when_named()
    {
        var target     = BuildRelationalModel<TwoFilteredIndexesContext>();
        var operations = GetDifferences(source: null, target: target);

        var creates = operations.OfType<CreateIndexOperation>().OrderBy(o => o.Name).ToList();
        Assert.HasCount(2, creates);
        Assert.AreEqual("ix_person_email_all",    creates[0].Name);
        Assert.IsNull(creates[0].Filter);
        Assert.AreEqual("ux_person_email_active", creates[1].Name);
        Assert.AreEqual("deleted_at IS NULL",     creates[1].Filter);
        Assert.IsTrue(creates[1].IsUnique);
    }

    private class UnnamedFilteredSiblingsContext(
        DbContextOptions<UnnamedFilteredSiblingsContext> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.ComplexProperty(x => x.EmailAddress, c => c.Property(x => x.Value).HasColumnName("email_address"));
                builder.HasComplexIndex(x => x.EmailAddress.Value, filter: "deleted_at IS NULL");
                builder.HasComplexIndex(x => x.EmailAddress.Value);
            });
    }

    [TestMethod(DisplayName = "Same column with different filters requires explicit names")]
    public void Same_column_with_different_filters_requires_names()
    {
        Assert.ThrowsExactly<ArgumentException>(() => BuildRelationalModel<UnnamedFilteredSiblingsContext>());
    }

    // Both named — but named the *same*, which the "must be named" guard alone happily accepted.
    private class DuplicateExplicitNameContext(
        DbContextOptions<DuplicateExplicitNameContext> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.ComplexProperty(x => x.EmailAddress, c => c.Property(x => x.Value).HasColumnName("email_address"));
                builder.HasComplexIndex(x => x.EmailAddress.Value, filter: "deleted_at IS NULL", indexName: "ix_dup");
                builder.HasComplexIndex(x => x.EmailAddress.Value, filter: "deleted_at IS NOT NULL", indexName: "ix_dup");
            });
    }

    [TestMethod(DisplayName = "Reusing one explicit index name for two indexes is rejected")]
    public void Duplicate_explicit_index_name_is_rejected()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(BuildRelationalModel<DuplicateExplicitNameContext>);

        StringAssert.Contains(ex.Message, "'ix_dup' is already used");
    }

    // The property-level store and the entity-level store cannot see each other, so both emit an
    // index over email_address under the same default name — two CREATE INDEX statements, 42P07.
    private class PropertyAndEntityLevelContext(
        DbContextOptions<PropertyAndEntityLevelContext> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.ComplexProperty(x => x.EmailAddress,
                                        c => c.Property(x => x.Value)
                                              .HasColumnName("email_address")
                                              .HasComplexIndex());
                builder.HasComplexIndex(x => x.EmailAddress.Value, isUnique: true);
            });
    }

    [TestMethod(DisplayName = "Property-level and entity-level indexes colliding on the default name are rejected")]
    public void Colliding_default_index_names_are_rejected()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => GetDifferences(source: null, target: BuildRelationalModel<PropertyAndEntityLevelContext>()));

        StringAssert.Contains(ex.Message, "both resolve to the name 'IX_person_email_address'");
        StringAssert.Contains(ex.Message, "UNIQUE");
    }

    // A collision already baked into the snapshot must not block diffing a fixed model.
    [TestMethod(DisplayName = "A colliding source model can still be diffed to a fixed target")]
    public void Colliding_source_model_stays_diffable()
    {
        var operations = GetDifferences(
            source: BuildRelationalModel<PropertyAndEntityLevelContext>(),
            target: BuildRelationalModel<EntityLevelSingleIndexContext>());

        Assert.IsNotNull(operations);
    }

    private class RedeclaredCompositeContext(
        DbContextOptions<RedeclaredCompositeContext> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Name).HasColumnName("name");
                builder.ComplexProperty(x => x.EmailAddress, c => c.Property(x => x.Value).HasColumnName("email_address"));
                builder.HasComplexCompositeIndex(x => new { x.Name, x.EmailAddress.Value });
                builder.HasComplexCompositeIndex(x => new { x.Name, x.EmailAddress.Value }, isUnique: true, indexName: "ux_person_name_email");
            });
    }

    [TestMethod(DisplayName = "Re-declaring the same column set with the same filter updates the index")]
    public void Redeclaring_same_column_set_updates_index()
    {
        var target     = BuildRelationalModel<RedeclaredCompositeContext>();
        var operations = GetDifferences(source: null, target: target);

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual("ux_person_name_email", createIndex.Name);
        Assert.IsTrue(createIndex.IsUnique);
    }

    // ── NULLS FIRST/LAST (v5) ──

    private class NullOrderedCompositeContext(
        DbContextOptions<NullOrderedCompositeContext> options) : DbContext(options)
    {
        public DbSet<PersonV1> People => Set<PersonV1>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<PersonV1>(builder =>
            {
                builder.ToTable("person");
                builder.HasKey(x => x.Id);
                builder.Property(x => x.Name).HasColumnName("name");
                builder.ComplexProperty(x => x.EmailAddress, c => c.Property(x => x.Value).HasColumnName("email_address"));
                builder.HasComplexCompositeIndex(x => new
                {
                    x.Name,
                    Email = DbOrder.NullsLast(DbOrder.Desc(x.EmailAddress.Value))
                });
            });
    }

    [TestMethod(DisplayName = "Null ordering routes a column-only index through the parts annotation")]
    public void Null_ordering_carries_parts_annotation()
    {
        var target     = BuildRelationalModel<NullOrderedCompositeContext>();
        var operations = GetDifferences(source: null, target: target);

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        // Nulls ordering needs the custom generator, so the wiring sentinel rides along in Columns —
        // without it, the stock generator would apply the index silently minus the NULLS clause.
        Assert.IsTrue(createIndex.Columns.SequenceEqual(["name", "email_address", CustomMigrationsModelDiffer.RuntimeWiringSentinel]));
        Assert.IsNotNull(createIndex.IsDescending);
        Assert.IsTrue(createIndex.IsDescending!.SequenceEqual([false, true, false]));

        // NULLS FIRST/LAST has no slot on the native operation, so the ordered parts must ride along.
        var partsJson = createIndex.FindAnnotation(ComplexIndexAnnotations.IndexParts)?.Value as string;
        Assert.IsNotNull(partsJson);

        var parts = IndexPartsSerializer.Deserialize(partsJson!);
        Assert.HasCount(2, parts);
        Assert.AreEqual(DbNullSort.Default, parts[0].NullSort);
        Assert.AreEqual(DbNullSort.Last,    parts[1].NullSort);
        Assert.IsTrue(parts[1].Descending);
    }
}