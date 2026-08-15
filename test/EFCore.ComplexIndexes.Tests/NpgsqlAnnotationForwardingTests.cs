using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// The v5 whitelist: the Npgsql differ forwards exactly the <c>Npgsql:*</c> index-option keys onto
/// the index operation — and nothing else from the property (column facets like
/// <c>Relational:ColumnName</c> leaked into scaffolded migrations before v5).
/// </summary>
[TestClass]
public class NpgsqlAnnotationForwardingTests
{
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

    private class EmptyContext(DbContextOptions options) : DbContext(options);

    private class Payload
    {
        public string Json { get; set; } = "";
    }

    private class Document
    {
        public Guid    Id      { get; set; }
        public Payload Payload { get; set; } = new();
    }

    private class GinIndexContext(DbContextOptions<GinIndexContext> options) : DbContext(options)
    {
        public DbSet<Document> Documents => Set<Document>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Document>(b =>
            {
                b.ToTable("documents");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Payload, c =>
                    c.Property(x => x.Json)
                     .HasColumnName("payload")
                     .HasColumnType("jsonb")
                     .HasComplexIndex(ix => ix.UseGin().HasOperators("jsonb_path_ops")));
            });
    }

    [TestMethod(DisplayName = "Npgsql index options are forwarded onto the operation")]
    public void Npgsql_index_options_are_forwarded()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<GinIndexContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual("gin", createIndex["Npgsql:IndexMethod"]);
        Assert.IsTrue(((string[])createIndex["Npgsql:IndexOperators"]!).SequenceEqual(["jsonb_path_ops"]));
    }

    [TestMethod(DisplayName = "Column facets are not forwarded onto the operation")]
    public void Column_facets_are_not_forwarded()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<GinIndexContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.IsNull(createIndex.FindAnnotation("Relational:ColumnName"));
        Assert.IsNull(createIndex.FindAnnotation("Relational:ColumnType"));
    }

    // ── INCLUDE path resolution (v5) ──

    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class Contact
    {
        public Guid         Id    { get; set; }
        public string       Name  { get; set; } = "";
        public EmailAddress Email { get; set; } = new();
    }

    private class IncludePathsContext(DbContextOptions<IncludePathsContext> options) : DbContext(options)
    {
        public DbSet<Contact> Contacts => Set<Contact>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Contact>(b =>
            {
                b.ToTable("contacts");
                b.HasKey(x => x.Id);
                b.Property(x => x.Name).HasColumnName("display_name");
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasColumnName("email"));
                // "Name" and "Email.Value" are property paths; "raw_col" resolves to nothing and
                // passes through verbatim as a column name.
                b.HasComplexIndex(x => x.Email.Value, ix => ix
                    .HasName("ix_contacts_email")
                    .IncludeProperties("Name", "Email.Value", "raw_col"));
            });
    }

    [TestMethod(DisplayName = "INCLUDE entries resolve as property paths with verbatim fallback")]
    public void Include_entries_resolve_property_paths()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<IncludePathsContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        var include     = (string[])createIndex["Npgsql:IndexInclude"]!;
        Assert.IsTrue(include.SequenceEqual(["display_name", "email", "raw_col"]));
    }

    // ── Validation is scoped to this package's own operations ──

    private class MixedIndexContext(DbContextOptions<MixedIndexContext> options) : DbContext(options)
    {
        public DbSet<Contact> Contacts => Set<Contact>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Contact>(b =>
            {
                b.ToTable("contacts");
                b.HasKey(x => x.Id);
                b.Property(x => x.Name).HasColumnName("display_name");
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasColumnName("email"));
                // A plain native index with a provider option — nothing to do with this package.
                b.HasIndex(x => x.Name).HasDatabaseName("ix_contacts_native").HasMethod("gin");
                b.HasComplexIndex(x => x.Email.Value, ix => ix.HasName("ix_contacts_complex").UseGin());
            });
    }

    // Records which operations reach the satellite validation hook.
    private sealed class RecordingDiffer(
        IRelationalTypeMappingSource     typeMappingSource,
        IMigrationsAnnotationProvider    migrationsAnnotationProvider,
        IRelationalAnnotationProvider    relationalAnnotationProvider,
        IRowIdentityMapFactory           rowIdentityMapFactory,
        CommandBatchPreparerDependencies commandBatchPreparerDependencies
    ) : NpgsqlComplexIndexMigrationsModelDiffer(
        typeMappingSource, migrationsAnnotationProvider, relationalAnnotationProvider,
        rowIdentityMapFactory, commandBatchPreparerDependencies)
    {
        public List<string> Validated { get; } = [];

        protected override void ValidateCreateIndexOperation(CreateIndexOperation operation)
        {
            Validated.Add(operation.Name);
            base.ValidateCreateIndexOperation(operation);
        }
    }

    [TestMethod(DisplayName = "Validation sees only this package's index operations, not native ones")]
    public void Validation_is_scoped_to_our_own_operations()
    {
        var options = new DbContextOptionsBuilder().UseNpgsql("Host=localhost;Database=test").Options;
        using var context = new EmptyContext(options);

        var differ = new RecordingDiffer(
            context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<IMigrationsAnnotationProvider>(),
            context.GetService<IRelationalAnnotationProvider>(),
            context.GetService<IRowIdentityMapFactory>(),
            context.GetService<CommandBatchPreparerDependencies>());

        var operations = differ.GetDifferences(null, BuildRelationalModel<MixedIndexContext>());

        // Both indexes are emitted, and the native one does carry Npgsql index options — exactly
        // what an operation-list sweep would have subjected to this package's whitelist.
        var creates = operations.OfType<CreateIndexOperation>().ToList();
        var native  = Assert.ContainsSingle(creates.Where(o => o.Name == "ix_contacts_native"));
        Assert.Contains("ix_contacts_complex", creates.Select(o => o.Name).ToList());
        Assert.IsTrue(native.GetAnnotations().Any(a => a.Name.StartsWith("Npgsql:", StringComparison.Ordinal)));

        // …but only the complex one is ours to police. Sweeping the finished operation list would
        // put the native index's provider options at the mercy of this package's whitelist.
        Assert.ContainsSingle(differ.Validated);
        Assert.AreEqual("ix_contacts_complex", differ.Validated[0]);
    }

    private class UnsupportedOptionContext(DbContextOptions<UnsupportedOptionContext> options) : DbContext(options)
    {
        public DbSet<Contact> Contacts => Set<Contact>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Contact>(b =>
            {
                b.ToTable("contacts");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasColumnName("email"));
                // Entity-level provider annotations bypass the property-level whitelist, so an
                // option the satellite cannot render has to be caught here.
                b.HasComplexIndex(x => x.Email.Value,
                                  ix => ix.HasName("ix_bad").HasAnnotation("Npgsql:NotARealOption", "v"));
            });
    }

    [TestMethod(DisplayName = "An unsupported Npgsql option on a complex index is still rejected")]
    public void Unsupported_option_on_complex_index_is_rejected()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => GetDifferences(source: null, target: BuildRelationalModel<UnsupportedOptionContext>()));

        StringAssert.Contains(ex.Message, "Npgsql:NotARealOption");
        StringAssert.Contains(ex.Message, "ix_bad");
    }

    private class SortOrderConflictContext(DbContextOptions<SortOrderConflictContext> options) : DbContext(options)
    {
        public DbSet<Contact> Contacts => Set<Contact>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Contact>(b =>
            {
                b.ToTable("contacts");
                b.HasKey(x => x.Id);
                b.Property(x => x.Name).HasColumnName("display_name");
                b.ComplexProperty(x => x.Email, c => c.Property(x => x.Value).HasColumnName("email"));
                // NULLS ordering routes this index through the parts annotation, which the package's
                // generator renders from — Npgsql's own sort annotation would go unread.
                b.HasComplexCompositeIndex(
                    x => new { x.Name, Email = DbOrder.NullsLast(x.Email.Value) },
                    ix => ix.HasName("ix_conflict").HasAnnotation("Npgsql:IndexNullSortOrder", new[] { "NullsFirst" }));
            });
    }

    [TestMethod(DisplayName = "Npgsql sort annotations alongside per-part sort options are rejected, not dropped")]
    public void Sort_annotation_conflicting_with_parts_is_rejected()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => GetDifferences(source: null, target: BuildRelationalModel<SortOrderConflictContext>()));

        StringAssert.Contains(ex.Message, "Npgsql:IndexNullSortOrder");
        StringAssert.Contains(ex.Message, "is not rendered for this index");
    }
}
