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
/// Members of complex properties mapped to JSON (<c>ToJson()</c>) have no table column; the Npgsql
/// differ resolves them into <c>-&gt;&gt;</c> extraction expressions so the same
/// <c>HasComplexIndex</c>/<c>HasComplexCompositeIndex</c> declarations keep working when a complex
/// property moves between scalar columns and a JSON document.
/// </summary>
[TestClass]
public class NpgsqlJsonMemberIndexTests
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

    private static List<ResolvedIndexPart> PartsOf(CreateIndexOperation operation)
    {
        var json = operation.FindAnnotation(ComplexIndexAnnotations.IndexParts)?.Value as string;
        Assert.IsNotNull(json, "Expected the IndexParts annotation on a JSON-member index.");
        return IndexPartsSerializer.Deserialize(json!);
    }

    private class EmptyContext(DbContextOptions options) : DbContext(options);

    private class CompanyName
    {
        public string ShortName { get; set; } = "";
        public string LegalName { get; set; } = "";
    }

    private class Employer
    {
        public Guid        Id   { get; set; }
        public string      Code { get; set; } = "";
        public CompanyName Name { get; set; } = new();
    }

    // Entity-level declaration over a JSON member.
    private class EntityLevelJsonIndexContext(DbContextOptions<EntityLevelJsonIndexContext> options) : DbContext(options)
    {
        public DbSet<Employer> Employers => Set<Employer>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Employer>(b =>
            {
                b.ToTable("employers");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Name, c => c.ToJson("name"));
                b.HasComplexIndex(x => x.Name.ShortName, isUnique: true, indexName: "ux_employer_short_name");
            });
    }

    // The AuditOffice shape: property-level declaration inside the ToJson complex property.
    private class PropertyLevelJsonIndexContext(DbContextOptions<PropertyLevelJsonIndexContext> options) : DbContext(options)
    {
        public DbSet<Employer> Employers => Set<Employer>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Employer>(b =>
            {
                b.ToTable("employers");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Name, c =>
                {
                    c.ToJson("name");
                    c.Property(x => x.ShortName).HasComplexIndex(isUnique: true);
                });
            });
    }

    private class JsonPropertyNameContext(DbContextOptions<JsonPropertyNameContext> options) : DbContext(options)
    {
        public DbSet<Employer> Employers => Set<Employer>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Employer>(b =>
            {
                b.ToTable("employers");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Name, c =>
                {
                    c.ToJson("name");
                    c.Property(x => x.ShortName).HasJsonPropertyName("short");
                });
                b.HasComplexIndex(x => x.Name.ShortName, indexName: "ix_employer_short");
            });
    }

    // Mixed composite: a plain column plus a JSON member.
    private class MixedCompositeContext(DbContextOptions<MixedCompositeContext> options) : DbContext(options)
    {
        public DbSet<Employer> Employers => Set<Employer>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Employer>(b =>
            {
                b.ToTable("employers");
                b.HasKey(x => x.Id);
                b.Property(x => x.Code).HasColumnName("code");
                b.ComplexProperty(x => x.Name, c => c.ToJson("name"));
                b.HasComplexCompositeIndex(x => new { x.Code, x.Name.ShortName }, indexName: "ix_employer_code_short");
            });
    }

    // Nested complex type inside the JSON document.
    private class Address
    {
        public string City { get; set; } = "";
    }

    private class Profile
    {
        public string  DisplayName { get; set; } = "";
        public Address Address     { get; set; } = new();
    }

    private class Member
    {
        public Guid    Id      { get; set; }
        public Profile Profile { get; set; } = new();
    }

    private class NestedJsonIndexContext(DbContextOptions<NestedJsonIndexContext> options) : DbContext(options)
    {
        public DbSet<Member> Members => Set<Member>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Member>(b =>
            {
                b.ToTable("members");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Profile, c => c.ToJson("profile"));
                b.HasComplexIndex(x => x.Profile.Address.City, indexName: "ix_member_city");
            });
    }

    [TestMethod(DisplayName = "Entity-level index on a JSON member resolves to a ->> extraction")]
    public void Entity_level_json_member_resolves()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<EntityLevelJsonIndexContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.AreEqual("ux_employer_short_name", createIndex.Name);
        Assert.IsTrue(createIndex.IsUnique);

        var part = Assert.ContainsSingle(PartsOf(createIndex));
        Assert.IsTrue(part.IsExpression);
        Assert.AreEqual("\"name\" ->> 'ShortName'", part.Value);
    }

    [TestMethod(DisplayName = "Property-level index inside a ToJson complex type resolves the same way")]
    public void Property_level_json_member_resolves()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<PropertyLevelJsonIndexContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        Assert.IsTrue(createIndex.IsUnique);

        var part = Assert.ContainsSingle(PartsOf(createIndex));
        Assert.AreEqual("\"name\" ->> 'ShortName'", part.Value);
        Assert.AreEqual("IX_employers_nameShortName", createIndex.Name);
    }

    [TestMethod(DisplayName = "HasJsonPropertyName is honored in the extraction path")]
    public void Json_property_name_is_honored()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<JsonPropertyNameContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        var part        = Assert.ContainsSingle(PartsOf(createIndex));
        Assert.AreEqual("\"name\" ->> 'short'", part.Value);
    }

    [TestMethod(DisplayName = "Mixed composite keeps column parts and resolves JSON parts")]
    public void Mixed_composite_resolves()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<MixedCompositeContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        var parts       = PartsOf(createIndex);

        Assert.HasCount(2, parts);
        Assert.IsFalse(parts[0].IsExpression);
        Assert.AreEqual("code", parts[0].Value);
        Assert.IsTrue(parts[1].IsExpression);
        Assert.AreEqual("\"name\" ->> 'ShortName'", parts[1].Value);
    }

    [TestMethod(DisplayName = "Nested complex members render intermediate -> segments")]
    public void Nested_json_member_resolves()
    {
        var operations = GetDifferences(source: null, target: BuildRelationalModel<NestedJsonIndexContext>());

        var createIndex = Assert.ContainsSingle(operations.OfType<CreateIndexOperation>());
        var part        = Assert.ContainsSingle(PartsOf(createIndex));
        Assert.AreEqual("\"profile\" -> 'Address' ->> 'City'", part.Value);
    }

    [TestMethod(DisplayName = "Unchanged JSON-member index produces no operations")]
    public void Unchanged_json_member_index_is_noop()
    {
        var operations = GetDifferences(
            BuildRelationalModel<EntityLevelJsonIndexContext>(),
            BuildRelationalModel<EntityLevelJsonIndexContext>());

        Assert.IsEmpty(operations.OfType<CreateIndexOperation>());
        Assert.IsEmpty(operations.OfType<DropIndexOperation>());
    }
}
