using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// An application that owes every unique index and exclusion constraint on a withdrawable
/// aggregate a live-rows filter had no way to check that it paid: the declarations were write-only
/// annotations, half of them behind internal types. <c>GetComplexIndexes</c> and
/// <c>GetExclusionConstraints</c> read them back — from the finalized model or from the mutable one
/// in <c>OnModelCreating</c> — through the same code the differ uses.
/// </summary>
[TestClass]
public class ReadModelTests
{
    private class EmailAddress
    {
        public string Value { get; set; } = "";
    }

    private class Grant
    {
        public Guid                  Id        { get; set; }
        public Guid                  GranteeId { get; set; }
        public string                Role      { get; set; } = "";
        public EmailAddress          Email     { get; set; } = new();
        public DateTime?             RevokedAt { get; set; }
        public NpgsqlRange<DateOnly> Period    { get; set; }
    }

    private class GrantContext(DbContextOptions<GrantContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Grant>(b =>
            {
                b.ToTable("grants");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email, c => c.Property(e => e.Value)
                                                      .HasComplexIndex(ix => ix.IsUnique().HasFilter("revoked_at IS NULL").HasName("ux_grant_email")));
                b.HasComplexIndex(x => x.Role, filter: "revoked_at IS NULL", indexName: "ix_grant_role_active");
                b.HasComplexCompositeIndex(x => new { x.GranteeId, x.Role },
                                           ix => ix.IsUnique().HasName("ux_grant_grantee_role").UseGin().HasStorageParameter("fillfactor", 70));
                b.HasExpressionIndex(x => x.Email.Value.ToLower(), indexName: "ix_grant_email_lower");
                b.HasExclusionConstraint(x => x.GranteeId, x => x.Period, filter: "revoked_at IS NULL", name: "ex_grant_active");
                b.HasExclusionConstraint(ex => ex.WithEquality(x => x.GranteeId)
                                                 .WithOverlaps(x => x.Period)
                                                 .HasFilter("revoked_at IS NOT NULL")
                                                 .HasName("ex_grant_revoked")
                                                 .IsDeferrable(initiallyDeferred: true));
            });
    }

    private class DefaultNamesContext(DbContextOptions<DefaultNamesContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<Grant>(b =>
            {
                b.ToTable("grants");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email);
                b.HasComplexIndex(x => x.Role);
                b.HasExclusionConstraint(x => x.GranteeId, x => x.Period);
            });
    }

    // The guard's shape: read the declarations while the model is still being built.
    private class GuardingContext(DbContextOptions<GuardingContext> options) : DbContext(options)
    {
        public List<string> Unfiltered { get; } = [];

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Grant>(b =>
            {
                b.ToTable("grants");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Email);
                b.HasComplexIndex(x => x.Role, isUnique: true, indexName: "ux_grant_role");                      // the obligation, forgotten
                b.HasComplexIndex(x => x.GranteeId, isUnique: true, filter: "revoked_at IS NULL", indexName: "ux_grant_grantee");
                b.HasExclusionConstraint(x => x.GranteeId, x => x.Period, name: "ex_grant_period");              // forgotten here too
            });

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                Unfiltered.AddRange(entityType.GetComplexIndexes()
                                              .Where(index => index.IsUnique && index.Filter is null)
                                              .Select(index => index.Name ?? "<default>"));
                Unfiltered.AddRange(entityType.GetExclusionConstraints()
                                              .Where(constraint => constraint.Filter is null)
                                              .Select(constraint => constraint.Name ?? "<default>"));
            }
        }
    }

    private class Animal
    {
        public Guid         Id   { get; set; }
        public EmailAddress Tag  { get; set; } = new();
        public string       Name { get; set; } = "";
    }

    private class Cat : Animal
    {
        public int Lives { get; set; }
    }

    private class HierarchyContext(DbContextOptions<HierarchyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Animal>(b =>
            {
                b.ToTable("animals");
                b.HasKey(x => x.Id);
                b.ComplexProperty(x => x.Tag, c => c.Property(t => t.Value).HasComplexIndex(indexName: "ix_animal_tag"));
            });
            modelBuilder.Entity<Cat>(b => b.HasComplexIndex(x => x.Name, indexName: "ix_cat_name"));
        }
    }

    private static IModel Model<TContext>() where TContext : DbContext
        => MigrationHarness.NpgsqlModel<TContext>().Model;

    [TestMethod(DisplayName = "Every declaration kind is reported: property-level, entity-level, composite, expression")]
    public void Entity_type_reports_every_declaration_kind()
    {
        var grant   = Model<GrantContext>().FindEntityType(typeof(Grant))!;
        var indexes = grant.GetComplexIndexes();

        Assert.HasCount(4, indexes);
        CollectionAssert.AreEqual(
            new[] { "ux_grant_email", "ix_grant_role_active", "ux_grant_grantee_role", "ix_grant_email_lower" },
            indexes.Select(i => i.Name).ToList());

        var propertyLevel = indexes[0];
        Assert.IsTrue(propertyLevel.IsPropertyLevel);
        Assert.AreEqual("Value", propertyLevel.Property!.Name);
        Assert.AreEqual("Email.Value", propertyLevel.Parts.Single().PropertyPath);
        Assert.IsTrue(propertyLevel.IsUnique);
        Assert.AreEqual("revoked_at IS NULL", propertyLevel.Filter);
        Assert.AreSame(grant, propertyLevel.EntityType);

        var single = indexes[1];
        Assert.IsFalse(single.IsPropertyLevel);
        Assert.AreEqual("Role", single.Parts.Single().PropertyPath);
        Assert.IsFalse(single.IsUnique);

        var composite = indexes[2];
        CollectionAssert.AreEqual(new[] { "GranteeId", "Role" }, composite.Parts.Select(p => p.PropertyPath).ToList());
        Assert.IsTrue(composite.IsUnique);
        Assert.IsNull(composite.Filter);

        Assert.IsTrue(indexes[3].Parts.Single().IsTemplate);
    }

    [TestMethod(DisplayName = "Provider options come back as plain values, not JSON elements")]
    public void Provider_annotations_are_normalized()
    {
        var composite = Model<GrantContext>().FindComplexIndex("ux_grant_grantee_role")!;

        Assert.AreEqual("gin", composite.ProviderAnnotations["Npgsql:IndexMethod"]);
        Assert.AreEqual(70, composite.ProviderAnnotations["Npgsql:StorageParameter:fillfactor"]);
    }

    [TestMethod(DisplayName = "FindComplexIndex matches explicit names and carries the entity type")]
    public void Find_by_explicit_name()
    {
        var model = Model<GrantContext>();

        var found = model.FindComplexIndex("ux_grant_email");

        Assert.IsNotNull(found);
        Assert.AreEqual(typeof(Grant), found.EntityType.ClrType);
        Assert.IsNull(model.FindComplexIndex("ix_nothing"));
        Assert.HasCount(4, model.GetComplexIndexes());
    }

    [TestMethod(DisplayName = "A default-named declaration has no name and is not found by the name the differ will derive")]
    public void Default_names_are_not_matched()
    {
        var model = Model<DefaultNamesContext>();

        Assert.IsNull(model.GetComplexIndexes().Single().Name);
        Assert.IsNull(model.FindComplexIndex("IX_grants_Role"));
        Assert.IsNull(model.GetExclusionConstraints().Single().Name);
        Assert.IsNull(model.FindExclusionConstraint("EX_grants_GranteeId_Period"));
    }

    [TestMethod(DisplayName = "Exclusion constraints are reported with method, filter, name and deferrability")]
    public void Exclusion_constraints_are_reported()
    {
        var model       = Model<GrantContext>();
        var constraints = model.FindEntityType(typeof(Grant))!.GetExclusionConstraints();

        Assert.HasCount(2, constraints);

        var active = constraints[0];
        Assert.AreEqual("ex_grant_active", active.Name);
        Assert.AreEqual("gist", active.Method);
        Assert.AreEqual("revoked_at IS NULL", active.Filter);
        Assert.IsFalse(active.Deferrable);
        CollectionAssert.AreEqual(new[] { "GranteeId", "Period" }, active.Parts.Select(p => p.PropertyPath).ToList());
        CollectionAssert.AreEqual(new[] { "=", "&&" }, active.Parts.Select(p => p.Operator).ToList());

        var revoked = model.FindExclusionConstraint("ex_grant_revoked")!;
        Assert.IsTrue(revoked.Deferrable);
        Assert.IsTrue(revoked.InitiallyDeferred);
        Assert.AreEqual(typeof(Grant), revoked.EntityType.ClrType);
    }

    [TestMethod(DisplayName = "Declarations can be read from the mutable model inside OnModelCreating")]
    public void Reads_from_the_mutable_model()
    {
        using var context = new GuardingContext(new DbContextOptionsBuilder<GuardingContext>().UseNpgsql(MigrationHarness.NpgsqlConnection).Options);

        _ = context.Model;

        CollectionAssert.AreEquivalent(new[] { "ux_grant_role", "ex_grant_period" }, context.Unfiltered);
    }

    [TestMethod(DisplayName = "Inherited declarations are reported by the declaring type only, unless asked for")]
    public void Inherited_declarations()
    {
        var model = Model<HierarchyContext>();
        var cat   = model.FindEntityType(typeof(Cat))!;

        Assert.AreEqual("ix_cat_name", cat.GetDeclaredComplexIndexes().Single().Name);
        CollectionAssert.AreEqual(new[] { "ix_animal_tag", "ix_cat_name" }, cat.GetComplexIndexes().Select(i => i.Name).ToList());
        Assert.HasCount(2, model.GetComplexIndexes());
    }
}
