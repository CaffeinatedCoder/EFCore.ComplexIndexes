using System.Reflection;
using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Design.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;
using Microsoft.Extensions.DependencyInjection;
using Npgsql.EntityFrameworkCore.PostgreSQL.Design.Internal;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

#pragma warning disable EF1001

/// <summary>
/// Full-fidelity regression tests for the `dotnet ef migrations add` pipeline: the source model is
/// not a second in-memory context but a real model snapshot — generated as C# by the design-time
/// code generator, compiled with Roslyn, and rebuilt through <see cref="ModelSnapshot"/> exactly as
/// EF does at scaffold time. A model that survives this round trip and still diffs non-empty against
/// itself would re-emit operations on every `migrations add` (the phantom-churn class of bug).
/// </summary>
[TestClass]
public class SnapshotRoundTripTests
{
    // ── Harness ──

    private static TContext CreateContext<TContext>() where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
                     .UseNpgsql("Host=localhost;Database=test")
                     .Options;

        return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
    }

    /// <summary>
    /// Generates the model-snapshot C# for the context's model, compiles it in-memory, and rebuilds
    /// the model from the compiled <see cref="ModelSnapshot"/> — the exact source-model path of
    /// `dotnet ef migrations add`.
    /// </summary>
    private static IRelationalModel BuildModelViaSnapshot<TContext>() where TContext : DbContext
    {
        using var context = CreateContext<TContext>();

        var designServices = new ServiceCollection();
        designServices.AddEntityFrameworkDesignTimeServices();
        new NpgsqlDesignTimeServices().ConfigureDesignTimeServices(designServices);
        designServices.AddDbContextDesignTimeServices(context);

        using var designProvider = designServices.BuildServiceProvider();

        var code = designProvider.GetRequiredService<IMigrationsCodeGenerator>().GenerateSnapshot(
            modelSnapshotNamespace: "EFCore.ComplexIndexes.Tests.Generated",
            contextType: typeof(TContext),
            modelSnapshotName: "RoundTripSnapshot",
            model: context.GetService<IDesignTimeModel>().Model);

        var snapshot = CompileSnapshot(code);

        var model = designProvider.GetRequiredService<IModelRuntimeInitializer>()
                                  .Initialize(snapshot.Model, designTime: true, validationLogger: null);

        return model.GetRelationalModel();
    }

    private static ModelSnapshot CompileSnapshot(string code)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                        .Split(Path.PathSeparator)
                        .Concat(AppDomain.CurrentDomain.GetAssemblies()
                                         .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                                         .Select(a => a.Location))
                        .Distinct()
                        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                        .ToList();

        var compilation = CSharpCompilation.Create(
            "SnapshotRoundTrip.Generated",
            [CSharpSyntaxTree.ParseText(code)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);

        if (!result.Success)
        {
            var errors = string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            Assert.Fail($"Generated snapshot failed to compile:\n{errors}\n--- generated code ---\n{code}");
        }

        var assembly     = Assembly.Load(stream.ToArray());
        var snapshotType = assembly.GetTypes().Single(t => typeof(ModelSnapshot).IsAssignableFrom(t));

        return (ModelSnapshot)Activator.CreateInstance(snapshotType)!;
    }

    private static IRelationalModel BuildLiveModel<TContext>() where TContext : DbContext
    {
        using var context = CreateContext<TContext>();
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

    // ── Tests ──

    [TestMethod(DisplayName = "Exclusion constraints survive the snapshot round trip without churn")]
    public void Exclusion_constraint_roundtrip_is_noop()
    {
        var operations = GetDifferences(
            source: BuildModelViaSnapshot<RoundTripExclusionContext>(),
            target: BuildLiveModel<RoundTripExclusionContext>());

        Assert.IsEmpty(operations, string.Join("\n", operations.Select(o => o.GetType().Name)));
    }

    [TestMethod(DisplayName = "Temporal constraints and FKs survive the snapshot round trip without churn")]
    public void Temporal_constraint_roundtrip_is_noop()
    {
        var operations = GetDifferences(
            source: BuildModelViaSnapshot<RoundTripTemporalContext>(),
            target: BuildLiveModel<RoundTripTemporalContext>());

        Assert.IsEmpty(operations, string.Join("\n", operations.Select(o => o.GetType().Name)));
    }

    [TestMethod(DisplayName = "Complex indexes survive the snapshot round trip without churn")]
    public void Complex_index_roundtrip_is_noop()
    {
        var operations = GetDifferences(
            source: BuildModelViaSnapshot<RoundTripIndexContext>(),
            target: BuildLiveModel<RoundTripIndexContext>());

        Assert.IsEmpty(operations, string.Join("\n", operations.Select(o => o.GetType().Name)));
    }

    [TestMethod(DisplayName = "A filter change against the snapshot model is still detected")]
    public void Exclusion_filter_change_is_detected_against_snapshot()
    {
        var operations = GetDifferences(
            source: BuildModelViaSnapshot<RoundTripExclusionContext>(),
            target: BuildLiveModel<RoundTripChangedFilterContext>());

        var sql = operations.OfType<SqlOperation>()
                            .Select(o => o.Sql)
                            .Where(s => s.Contains("EXCLUDE") || s.Contains("DROP CONSTRAINT"))
                            .ToList();

        Assert.HasCount(2, sql);
        StringAssert.Contains(sql[0], "DROP CONSTRAINT IF EXISTS \"ex_roundtrip_active\"");
        StringAssert.Contains(sql[1], "ADD CONSTRAINT \"ex_roundtrip_active\"");
    }
}

// The round-trip contexts are internal top-level types: the generated snapshot's
// [DbContext(typeof(...))] attribute must be able to reference them from the compiled assembly.

internal class RoundTripSlot
{
    public int Resource { get; set; }
    public NpgsqlRange<DateOnly> Period { get; set; }
}

internal class RoundTripGrant
{
    public int Id { get; set; }
    public int GranteeId { get; set; }
    public NpgsqlRange<DateOnly> Period { get; set; }
    public DateOnly? RevokedAt { get; set; }
    public RoundTripSlot Slot { get; set; } = new();
}

internal class RoundTripExclusionContext(DbContextOptions<RoundTripExclusionContext> options) : DbContext(options)
{
    public DbSet<RoundTripGrant> Grants => Set<RoundTripGrant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<RoundTripGrant>(b =>
        {
            b.ToTable("rt_grants", "audit");
            b.HasKey(x => x.Id);
            b.Property(x => x.GranteeId).HasColumnName("grantee_id");
            b.Property(x => x.Period).HasColumnName("period");
            b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            b.ComplexProperty(x => x.Slot);
            b.HasExclusionConstraint(
                equalityColumns: x => x.GranteeId,
                overlapsColumn: x => x.Period,
                filter: "revoked_at IS NULL",
                name: "ex_roundtrip_active");
            b.HasExclusionConstraint(x => x.Slot.Resource, x => x.Slot.Period, name: "ex_roundtrip_slot");
        });
}

internal class RoundTripChangedFilterContext(DbContextOptions<RoundTripChangedFilterContext> options) : DbContext(options)
{
    public DbSet<RoundTripGrant> Grants => Set<RoundTripGrant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<RoundTripGrant>(b =>
        {
            b.ToTable("rt_grants", "audit");
            b.HasKey(x => x.Id);
            b.Property(x => x.GranteeId).HasColumnName("grantee_id");
            b.Property(x => x.Period).HasColumnName("period");
            b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            b.ComplexProperty(x => x.Slot);
            b.HasExclusionConstraint(
                equalityColumns: x => x.GranteeId,
                overlapsColumn: x => x.Period,
                filter: "revoked_at IS NULL AND grantee_id > 0",
                name: "ex_roundtrip_active");
            b.HasExclusionConstraint(x => x.Slot.Resource, x => x.Slot.Period, name: "ex_roundtrip_slot");
        });
}

internal class RoundTripSubscription
{
    public int RowId { get; set; }
    public int SubscriptionId { get; set; }
    public NpgsqlRange<DateOnly> ValidDuring { get; set; }
}

internal class RoundTripAddOn
{
    public int RowId { get; set; }
    public int SubscriptionId { get; set; }
    public NpgsqlRange<DateOnly> ActiveDuring { get; set; }
}

internal class RoundTripTemporalContext(DbContextOptions<RoundTripTemporalContext> options) : DbContext(options)
{
    public DbSet<RoundTripSubscription> Subscriptions => Set<RoundTripSubscription>();
    public DbSet<RoundTripAddOn> AddOns => Set<RoundTripAddOn>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RoundTripSubscription>(b =>
        {
            b.ToTable("rt_subscriptions");
            b.HasKey(x => x.RowId);
            b.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
            b.Property(x => x.ValidDuring).HasColumnName("valid_during");
            b.HasTemporalConstraint(x => x.SubscriptionId, x => x.ValidDuring);
        });

        modelBuilder.Entity<RoundTripAddOn>(b =>
        {
            b.ToTable("rt_addons");
            b.HasKey(x => x.RowId);
            b.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
            b.Property(x => x.ActiveDuring).HasColumnName("active_during");
            b.HasTemporalForeignKey<RoundTripAddOn, RoundTripSubscription>(
                x => x.SubscriptionId,
                x => x.ActiveDuring,
                x => x.SubscriptionId,
                x => x.ValidDuring);
        });
    }
}

internal class RoundTripOrigin
{
    public string Source { get; set; } = "";
    public string Detail { get; set; } = "";
}

internal class RoundTripDocument
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public RoundTripOrigin Origin { get; set; } = new();
}

internal class RoundTripIndexContext(DbContextOptions<RoundTripIndexContext> options) : DbContext(options)
{
    public DbSet<RoundTripDocument> Documents => Set<RoundTripDocument>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<RoundTripDocument>(b =>
        {
            b.ToTable("rt_documents");
            b.HasKey(x => x.Id);
            b.ComplexProperty(x => x.Origin, c =>
            {
                c.Property(x => x.Source).HasComplexIndex();
                c.Property(x => x.Detail);
            });
            b.HasComplexCompositeIndex(x => new { x.Title, x.Origin.Detail }, isUnique: true);
        });
}

#pragma warning restore EF1001
