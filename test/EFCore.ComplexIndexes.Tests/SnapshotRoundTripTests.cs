using System.Reflection;
using EFCore.ComplexIndexes.PostgreSQL;
using EFCore.ComplexIndexes.SqlServer;
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
using Microsoft.EntityFrameworkCore.SqlServer.Design.Internal;
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

    private static TContext CreateContext<TContext>(bool sqlServer = false) where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        var options = sqlServer
                          ? builder.UseSqlServer(MigrationHarness.SqlServerConnection).Options
                          : builder.UseNpgsql("Host=localhost;Database=test").Options;

        return (TContext)Activator.CreateInstance(typeof(TContext), options)!;
    }

    /// <summary>
    /// Generates the model-snapshot C# for the context's model, compiles it in-memory, and rebuilds
    /// the model from the compiled <see cref="ModelSnapshot"/> — the exact source-model path of
    /// `dotnet ef migrations add`.
    /// </summary>
    private static IRelationalModel BuildModelViaSnapshot<TContext>(Func<string, string>? editSnapshot = null, bool sqlServer = false) where TContext : DbContext
    {
        using var context = CreateContext<TContext>(sqlServer);

        var designServices = new ServiceCollection();
        designServices.AddEntityFrameworkDesignTimeServices();
        if (sqlServer)
            new SqlServerDesignTimeServices().ConfigureDesignTimeServices(designServices);
        else
            new NpgsqlDesignTimeServices().ConfigureDesignTimeServices(designServices);
        designServices.AddDbContextDesignTimeServices(context);

        using var designProvider = designServices.BuildServiceProvider();

        var code = designProvider.GetRequiredService<IMigrationsCodeGenerator>().GenerateSnapshot(
            modelSnapshotNamespace: "EFCore.ComplexIndexes.Tests.Generated",
            contextType: typeof(TContext),
            modelSnapshotName: "RoundTripSnapshot",
            model: context.GetService<IDesignTimeModel>().Model);

        var snapshot = CompileSnapshot(editSnapshot?.Invoke(code) ?? code);

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

    private static IRelationalModel BuildLiveModel<TContext>(bool sqlServer = false) where TContext : DbContext
    {
        using var context = CreateContext<TContext>(sqlServer);
        return context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
    }

    private static IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
        => MigrationHarness.NpgsqlDiff(source, target);

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

    // A temporal foreign key stores its principal's CLR FullName, which for a generic type is
    // "Room`1[[System.Int32, …]]" where EF names the entity type "Room<int>". A snapshot's
    // property-bag type has no CLR type to fall back on, so the principal did not resolve and every
    // diff from the snapshot threw (#197).
    [TestMethod(DisplayName = "A temporal foreign key to a generic entity type survives the snapshot round trip (#197)")]
    public void Temporal_foreign_key_to_a_generic_principal_roundtrip_is_noop()
    {
        var operations = GetDifferences(
            source: BuildModelViaSnapshot<RoundTripGenericTemporalContext>(),
            target: BuildLiveModel<RoundTripGenericTemporalContext>());

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

    /// <summary>
    /// The SQL Server satellite's options survive the snapshot: every option, property-level and
    /// entity-level, a descending part and a placeholder filter. A property-level option is a plain
    /// property annotation the snapshot writes in its own form (the data-compression enum, the
    /// INCLUDE array), and one that came back different would rebuild the index on every
    /// <c>migrations add</c>.
    /// </summary>
    [TestMethod(DisplayName = "SQL Server complex indexes survive the snapshot round trip without churn")]
    public void SqlServer_complex_index_roundtrip_is_noop()
    {
        var source = BuildModelViaSnapshot<RoundTripSqlServerContext>(sqlServer: true);
        var target = BuildLiveModel<RoundTripSqlServerContext>(sqlServer: true);

        // Not vacuous: from nothing, the target creates every declared index.
        Assert.HasCount(4, MigrationHarness.SqlServerDiff(null, target).OfType<CreateIndexOperation>());

        var operations = MigrationHarness.SqlServerDiff(source, target);
        Assert.IsEmpty(operations, string.Join("\n", operations.Select(o => o.GetType().Name)));
    }

    [TestMethod(DisplayName = "Converter-member paths survive the snapshot round trip without churn")]
    public void Converter_member_roundtrip_is_noop()
    {
        var source = BuildModelViaSnapshot<RoundTripConverterContext>();

        // The snapshot's premise: the value object is gone, only its provider type and column remain.
        var entity = source.Model.GetEntityTypes().Single();
        var email  = entity.FindProperty(nameof(RoundTripAccount.Email))!;
        var handle = entity.FindComplexProperty(nameof(RoundTripAccount.Profile))!.ComplexType.FindProperty(nameof(RoundTripProfile.Handle))!;

        foreach (var property in new[] { email, handle })
        {
            Assert.AreEqual(typeof(string), property.ClrType, property.Name);
            Assert.IsTrue(property.DeclaringType.IsPropertyBag, property.Name);
            Assert.IsTrue(property.IsIndexerProperty(), property.Name);
            Assert.IsNull(property.FindTypeMapping()?.Converter ?? property.GetValueConverter(), property.Name);
        }

        var operations = GetDifferences(source, BuildLiveModel<RoundTripConverterContext>());

        Assert.IsEmpty(operations, string.Join("\n", operations.Select(o => o.GetType().Name)));
    }

    [TestMethod(DisplayName = "Migrate's runtime differ accepts a snapshot with converter-member paths")]
    public void Runtime_differ_accepts_converter_member_snapshot()
    {
        var source = BuildModelViaSnapshot<RoundTripConverterContext>();

        using var provider = new ServiceCollection()
                            .AddEntityFrameworkNpgsql()
                            .AddNpgsqlComplexIndexes()
                            .BuildServiceProvider();
        using var context = new EmptyContext(
            new DbContextOptionsBuilder()
               .UseNpgsql("Host=localhost;Database=test")
               .UseInternalServiceProvider(provider)
               .Options);
        var differ = context.GetService<IMigrationsModelDiffer>();

        Assert.IsInstanceOfType<NpgsqlComplexIndexMigrationsModelDiffer>(differ);
        Assert.IsFalse(
            differ.HasDifferences(source, BuildLiveModel<RoundTripConverterContext>()),
            "Migrate() uses this runtime pending-model-changes check before applying migrations.");
    }

    [TestMethod(DisplayName = "JSON-member indexes of every type survive the snapshot round trip without churn")]
    public void Json_member_roundtrip_is_noop()
    {
        var operations = GetDifferences(
            source: BuildModelViaSnapshot<RoundTripJsonContext>(),
            target: BuildLiveModel<RoundTripJsonContext>());

        Assert.IsEmpty(operations, string.Join("\n", operations.Select(o => o.GetType().Name + " " + (o as CreateIndexOperation)?.Name)));
    }

    // The rollout's premise, checked on a real snapshot: the rendering version is written into it, and
    // a snapshot without it — every snapshot scaffolded before 5.4.0 — rebuilds exactly the indexes
    // whose rendering changed. If the key never reached the snapshot, the first test above would pass
    // for the wrong reason and every later `migrations add` would rebuild these indexes again.
    [TestMethod(DisplayName = "A snapshot scaffolded before 5.4.0 rebuilds exactly the affected JSON-member indexes")]
    public void Pre_54_snapshot_rebuilds_affected_json_indexes()
    {
        var sawKey = false;
        var source = BuildModelViaSnapshot<RoundTripJsonContext>(code =>
        {
            sawKey = code.Contains($"\"{ComplexIndexAnnotations.RenderingVersion}\", {ComplexIndexStorage.CurrentRenderingVersion}");
            return System.Text.RegularExpressions.Regex.Replace(
                code, $@"\s*\.HasAnnotation\(""{ComplexIndexAnnotations.RenderingVersion}"", {ComplexIndexStorage.CurrentRenderingVersion}\)", "");
        });

        Assert.IsTrue(sawKey, "The rendering version must be written into the snapshot.");

        var operations = GetDifferences(source, BuildLiveModel<RoundTripJsonContext>());

        var affected = NpgsqlJsonMemberRenderingTests.Members.Select(m => m.Name)
                                                     .Concat(NpgsqlJsonMemberRenderingTests.TextFallbackMembers.Select(m => m.Name))
                                                     .Where(n => !NpgsqlJsonMemberRenderingTests.UnaffectedByRollout.Contains(n))
                                                     .Append("ix_filtered_city")
                                                     .Append("ix_filtered_rank")
                                                     .Order()
                                                     .ToList();

        Assert.AreEqual(string.Join(", ", affected), string.Join(", ", operations.OfType<DropIndexOperation>().Select(o => o.Name).Order()));
        Assert.AreEqual(string.Join(", ", affected), string.Join(", ", operations.OfType<CreateIndexOperation>().Select(o => o.Name).Order()));
        Assert.IsFalse(operations.OfType<SqlOperation>().Any(), "Exclusion constraints keep their rendering.");
    }

    // Each declaration on the TPC base is resolved once per table of the hierarchy, on both sides of
    // the diff: the snapshot must keep the mapping strategy, or its side would hold the base's table
    // only and every `migrations add` would create the derived tables' copies again.
    [TestMethod(DisplayName = "Declarations on a TPC base survive the snapshot round trip without churn")]
    public void Tpc_declarations_roundtrip_is_noop()
    {
        var target = BuildLiveModel<RoundTripTpcContext>();

        // Not vacuous: from nothing, each declaration lands on all three tables (the tenth constraint
        // is the room's own).
        var created = GetDifferences(null, target);
        Assert.HasCount(3, created.OfType<CreateIndexOperation>());
        Assert.HasCount(10, created.OfType<SqlOperation>().Where(o => o.Sql.Contains("ADD CONSTRAINT")));

        var operations = GetDifferences(BuildModelViaSnapshot<RoundTripTpcContext>(), target);
        Assert.IsEmpty(operations, string.Join("\n", operations.Select(o => o.GetType().Name)));
    }

    // #145: a derived table's long name puts each copy's default name past 63 bytes, and the copy is
    // shortened. The snapshot side must shorten alike, or every `migrations add` would rename them.
    [TestMethod(DisplayName = "Shortened names of TPC copies on a long derived table survive the snapshot round trip without churn")]
    public void Tpc_shortened_copy_names_roundtrip_is_noop()
    {
        var target = BuildLiveModel<RoundTripLongTpcContext>();

        // Not vacuous: the copies on the long table carry shortened names.
        var created = GetDifferences(null, target);
        Assert.AreEqual(
            $"IX_{RoundTripLongTpcContext.LongTable}_C~",
            created.OfType<CreateIndexOperation>().Single(o => o.Table == RoundTripLongTpcContext.LongTable).Name);
        var adds = created.OfType<SqlOperation>().Select(o => o.Sql).Where(s => s.Contains("ADD CONSTRAINT")).ToList();
        Assert.IsTrue(adds.Any(s => s.Contains($"\"EX_{RoundTripLongTpcContext.LongTable}_R~\"")), string.Join("\n", adds));
        Assert.IsTrue(adds.Any(s => s.Contains($"\"AK_{RoundTripLongTpcContext.LongTable}_C~\"")), string.Join("\n", adds));
        Assert.IsTrue(adds.Any(s => s.Contains($"\"{$"FK_{RoundTripLongTpcContext.LongTable}_rt_rooms_RoomId_During"[..62]}~\"")), string.Join("\n", adds));

        var operations = GetDifferences(BuildModelViaSnapshot<RoundTripLongTpcContext>(), target);
        Assert.IsEmpty(operations, string.Join("\n", operations.Select(o => o.GetType().Name)));
    }

    // A snapshot scaffolded before 5.5.0 holds the base table's declarations only: resolved by that
    // rule, the first migration after upgrading creates exactly the derived tables' copies.
    [TestMethod(DisplayName = "A snapshot scaffolded before 5.5.0 creates exactly the copies on a TPC base's derived tables")]
    public void Pre_55_snapshot_creates_tpc_copies()
    {
        var source = BuildModelViaSnapshot<RoundTripTpcContext>(code => System.Text.RegularExpressions.Regex.Replace(
            code, $@"\s*\.HasAnnotation\(""{ComplexIndexAnnotations.RenderingVersion}"", {ComplexIndexStorage.CurrentRenderingVersion}\)", ""));

        var operations = GetDifferences(source, BuildLiveModel<RoundTripTpcContext>());

        Assert.IsFalse(operations.Any(o => o is DropIndexOperation or DropTableOperation or DropUniqueConstraintOperation or DropForeignKeyOperation));
        CollectionAssert.AreEquivalent(
            new[] { "IX_rt_special_bookings_Code", "IX_rt_vip_bookings_Code" },
            operations.OfType<CreateIndexOperation>().Select(o => o.Name).ToArray());

        var adds = operations.OfType<SqlOperation>().Select(o => o.Sql).Where(s => s.Contains("ADD CONSTRAINT")).ToList();
        Assert.HasCount(6, adds);
        Assert.IsTrue(adds.All(s => !s.Contains("ALTER TABLE \"rt_bookings\"")), string.Join("\n", adds));
    }

    // #167: named partial siblings on a TPC base; each table's copy takes a name of its own, which
    // the snapshot side must resolve alike.
    [TestMethod(DisplayName = "Named partial siblings on a TPC base survive the snapshot round trip without churn (#167)")]
    public void Tpc_named_partial_siblings_roundtrip_is_noop()
    {
        var target = BuildLiveModel<RoundTripNamedTpcContext>();

        // Not vacuous: six indexes and six exclusion constraints, each under a name of its own.
        var created = GetDifferences(null, target);
        var indexes = created.OfType<CreateIndexOperation>().Select(o => o.Name).ToList();
        Assert.HasCount(6, indexes);
        Assert.HasCount(6, indexes.Distinct());
        Assert.Contains("ix_rt_code_active_rt_special_bookings", indexes);
        var adds = created.OfType<SqlOperation>().Select(o => o.Sql).Where(s => s.Contains("EXCLUDE")).ToList();
        Assert.HasCount(6, adds);
        Assert.IsTrue(adds.Any(s => s.Contains("\"ex_rt_revoked_rt_vip_bookings\"")), string.Join("\n", adds));

        var operations = GetDifferences(BuildModelViaSnapshot<RoundTripNamedTpcContext>(), target);
        Assert.IsEmpty(operations, string.Join("\n", operations.Select(o => o.GetType().Name)));
    }

    // A 5.4.1 snapshot of the same model: the base's table keeps its names, the copies are created.
    [TestMethod(DisplayName = "Named partial siblings from a snapshot scaffolded before 5.5.0 keep their names and gain the copies (#167)")]
    public void Pre_55_snapshot_named_partial_siblings_create_only_the_copies()
    {
        var source = BuildModelViaSnapshot<RoundTripNamedTpcContext>(code => System.Text.RegularExpressions.Regex.Replace(
            code, $@"\s*\.HasAnnotation\(""{ComplexIndexAnnotations.RenderingVersion}"", {ComplexIndexStorage.CurrentRenderingVersion}\)", ""));

        var operations = GetDifferences(source, BuildLiveModel<RoundTripNamedTpcContext>());

        Assert.IsFalse(operations.Any(o => o is DropIndexOperation or RenameIndexOperation or DropTableOperation or DropUniqueConstraintOperation));
        Assert.IsFalse(operations.OfType<SqlOperation>().Any(o => o.Sql.Contains("ALTER TABLE \"rt_bookings\"")),
                       string.Join("\n", operations.OfType<SqlOperation>().Select(o => o.Sql)));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "ix_rt_code_active_rt_special_bookings", "ix_rt_code_archived_rt_special_bookings",
                "ix_rt_code_active_rt_vip_bookings", "ix_rt_code_archived_rt_vip_bookings"
            },
            operations.OfType<CreateIndexOperation>().Select(o => o.Name).ToArray());
        Assert.HasCount(4, operations.OfType<SqlOperation>().Where(o => o.Sql.Contains("EXCLUDE")));
    }

    // #168: a 5.4.1 snapshot of a temporal foreign key on a TPC base with a long derived table. The
    // first migration after upgrading adds the copies, the long table's under a shortened name.
    [TestMethod(DisplayName = "A temporal foreign key's long copy name from a snapshot scaffolded before 5.5.0 is shortened (#168)")]
    public void Pre_55_snapshot_long_foreign_key_copy_is_shortened()
    {
        var source = BuildModelViaSnapshot<RoundTripLongTpcContext>(code => System.Text.RegularExpressions.Regex.Replace(
            code, $@"\s*\.HasAnnotation\(""{ComplexIndexAnnotations.RenderingVersion}"", {ComplexIndexStorage.CurrentRenderingVersion}\)", ""));

        var operations = GetDifferences(source, BuildLiveModel<RoundTripLongTpcContext>());

        Assert.IsFalse(operations.Any(o => o is DropForeignKeyOperation or DropTableOperation or DropUniqueConstraintOperation));
        var foreignKeys = operations.OfType<SqlOperation>().Select(o => o.Sql).Where(s => s.Contains("FOREIGN KEY")).ToList();
        Assert.HasCount(2, foreignKeys);
        var full = $"FK_{RoundTripLongTpcContext.LongTable}_rt_rooms_RoomId_During";
        Assert.IsTrue(foreignKeys.Any(s => s.Contains($"ALTER TABLE \"{RoundTripLongTpcContext.LongTable}\" ADD CONSTRAINT \"{full[..62]}~\"")),
                      string.Join("\n", foreignKeys));
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

internal readonly record struct RoundTripEmailAddress(string Value);

internal class RoundTripProfile
{
    public RoundTripEmailAddress Handle { get; set; }
}

internal class RoundTripAccount
{
    public int Id { get; set; }
    public RoundTripEmailAddress Email { get; set; }
    public RoundTripProfile Profile { get; set; } = new();
    public NpgsqlRange<DateOnly> Period { get; set; }
}

// Every place a converter-member path can appear: an expression-index template, an entity-level
// column part, a composite part, a filter placeholder, an exclusion element — at the top level and
// nested inside a complex type. The snapshot persists the provider type and drops the converter,
// so each of these is a way the runtime differ could fail to resolve what design time resolved.
internal class RoundTripConverterContext(DbContextOptions<RoundTripConverterContext> options) : DbContext(options)
{
    public DbSet<RoundTripAccount> Accounts => Set<RoundTripAccount>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<RoundTripAccount>(b =>
        {
            b.ToTable("rt_accounts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Email)
             .HasConversion(email => email.Value, value => new RoundTripEmailAddress(value))
             .HasColumnName("email");
            b.Property(x => x.Period).HasColumnName("period");
            b.ComplexProperty(x => x.Profile, c => c.Property(x => x.Handle)
                                                    .HasConversion(handle => handle.Value, value => new RoundTripEmailAddress(value))
                                                    .HasColumnName("handle"));

            b.HasExpressionIndex(x => x.Email.Value.ToLower(), indexName: "ix_rt_accounts_email_lower");
            b.HasExpressionIndex(x => x.Profile.Handle.Value.ToLower(), indexName: "ix_rt_accounts_handle_lower");
            b.HasComplexIndex(x => x.Email.Value, filter: "{Profile.Handle.Value} <> ''", indexName: "ix_rt_accounts_email_with_handle");
            b.HasComplexCompositeIndex(x => new { x.Id, x.Profile.Handle.Value }, indexName: "ix_rt_accounts_id_handle");
            b.HasExclusionConstraint(x => x.Email.Value, x => x.Period, filter: "{Email.Value} <> ''", name: "ex_rt_accounts_email_period");
        });
}

// Every JSON member type the differ renders, plus index and exclusion filters over JSON members.
internal class RoundTripJsonContext(DbContextOptions<RoundTripJsonContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<NpgsqlJsonMemberRenderingTests.Account>(b =>
        {
            NpgsqlJsonMemberRenderingTests.DeclareMatrix(b);
            NpgsqlJsonMemberRenderingTests.DeclareFilters(b);
        });
}

#pragma warning restore EF1001

internal class RoundTripSqlServerContact
{
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
}

internal class RoundTripSqlServerCustomer
{
    public Guid                      Id        { get; set; }
    public string                    Name      { get; set; } = "";
    public DateTime?                 RevokedAt { get; set; }
    public RoundTripSqlServerContact Contact   { get; set; } = new();
}

internal class RoundTripSqlServerContext(DbContextOptions<RoundTripSqlServerContext> options) : DbContext(options)
{
    public DbSet<RoundTripSqlServerCustomer> Customers => Set<RoundTripSqlServerCustomer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.Entity<RoundTripSqlServerCustomer>(b =>
        {
            b.ToTable("rt_customers");
            b.HasKey(x => x.Id).IsClustered(false);
            b.Property(x => x.Name).HasMaxLength(100);
            b.ComplexProperty(x => x.Contact, c =>
            {
                c.Property(x => x.Email).HasMaxLength(256).HasComplexIndex(ix => ix
                    .HasName("cx_rt_email").IsClustered().HasFillFactor(70).SortInTempDb().UseDataCompression(DataCompressionType.Row));
                c.Property(x => x.Phone).HasMaxLength(32).HasComplexIndex(ix => SqlServerComplexIndexBuilderExtensions
                    .IncludeProperties(ix.HasName("ix_rt_phone"), "Name").IsCreatedOnline());
            });
            b.HasComplexCompositeIndex(x => new { x.Name, Phone = DbOrder.Desc(x.Contact.Phone) }, ix => SqlServerComplexIndexBuilderExtensions
                .IncludeProperties(ix.HasName("ix_rt_name_phone"), "Contact.Email")
                .HasFillFactor(80).IsCreatedOnline().SortInTempDb().UseDataCompression(DataCompressionType.Page));
            b.HasComplexIndex(x => x.Contact.Email, isUnique: true, filter: "{RevokedAt} IS NULL", indexName: "ux_rt_email_active");
        });
}

internal class RoundTripRoom
{
    public int                   Id        { get; set; }
    public int                   RoomId    { get; set; }
    public NpgsqlRange<DateOnly> Available { get; set; }
}

internal class RoundTripBooking
{
    public int                   Id     { get; set; }
    public int                   RoomId { get; set; }
    public string                Code   { get; set; } = "";
    public NpgsqlRange<DateOnly> During { get; set; }
}

internal class RoundTripSpecialBooking : RoundTripBooking
{
    public string Note { get; set; } = "";
}

internal class RoundTripVipBooking : RoundTripSpecialBooking
{
    public int Level { get; set; }
}

// A concrete TPC base with a derived type and a derived type of that, declaring every kind.
internal class RoundTripTpcContext(DbContextOptions<RoundTripTpcContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RoundTripRoom>(b =>
        {
            b.ToTable("rt_rooms");
            b.HasTemporalConstraint(x => x.RoomId, x => x.Available);
        });

        modelBuilder.Entity<RoundTripBooking>(b =>
        {
            b.UseTpcMappingStrategy();
            b.ToTable("rt_bookings");
            b.HasComplexIndex(x => x.Code, isUnique: true);
            b.HasExclusionConstraint(x => x.RoomId, x => x.During);
            b.HasTemporalConstraint(x => x.Code, x => x.During);
            b.HasTemporalForeignKey<RoundTripBooking, RoundTripRoom>(x => x.RoomId, x => x.During, r => r.RoomId, r => r.Available);
        });

        modelBuilder.Entity<RoundTripSpecialBooking>().ToTable("rt_special_bookings");
        modelBuilder.Entity<RoundTripVipBooking>().ToTable("rt_vip_bookings");
    }
}

// The TPC base of RoundTripTpcContext with a derived table whose name puts each copy's default name
// past PostgreSQL's 63 bytes (#145), the temporal foreign key's included (#168).
internal class RoundTripLongTpcContext(DbContextOptions<RoundTripLongTpcContext> options) : DbContext(options)
{
    public const string LongTable = "rt_special_bookings_for_corporate_customer_accounts_xyz12";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RoundTripBooking>(b =>
        {
            b.UseTpcMappingStrategy();
            b.ToTable("rt_bookings");
            b.HasComplexIndex(x => x.Code, isUnique: true);
            b.HasExclusionConstraint(x => x.RoomId, x => x.During);
            b.HasTemporalConstraint(x => x.Code, x => x.During);
            b.HasTemporalForeignKey<RoundTripBooking, RoundTripRoom>(x => x.RoomId, x => x.During, r => r.RoomId, r => r.Available);
        });

        modelBuilder.Entity<RoundTripRoom>(b =>
        {
            b.ToTable("rt_rooms");
            b.HasTemporalConstraint(x => x.RoomId, x => x.Available);
        });

        modelBuilder.Entity<RoundTripSpecialBooking>().ToTable(LongTable);
        modelBuilder.Entity<RoundTripVipBooking>().ToTable("rt_vip_bookings");
    }
}

// #167: two partial indexes and two exclusion constraints over the same parts on a TPC base, which
// must be named, plus a named temporal constraint.
internal class RoundTripNamedTpcContext(DbContextOptions<RoundTripNamedTpcContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RoundTripBooking>(b =>
        {
            b.UseTpcMappingStrategy();
            b.ToTable("rt_bookings");
            b.HasComplexIndex(x => x.Code, isUnique: true, filter: "{RoomId} > 0", indexName: "ix_rt_code_active");
            b.HasComplexIndex(x => x.Code, isUnique: true, filter: "{RoomId} <= 0", indexName: "ix_rt_code_archived");
            b.HasExclusionConstraint(x => x.RoomId, x => x.During, filter: "{Code} <> 'revoked'", name: "ex_rt_active");
            b.HasExclusionConstraint(x => x.RoomId, x => x.During, filter: "{Code} = 'revoked'", name: "ex_rt_revoked");
            b.HasTemporalConstraint(x => x.Code, x => x.During, name: "ak_rt_code_during");
        });

        modelBuilder.Entity<RoundTripSpecialBooking>().ToTable("rt_special_bookings");
        modelBuilder.Entity<RoundTripVipBooking>().ToTable("rt_vip_bookings");
    }
}

internal class RoundTripGenericRoom<TKey>
{
    public int                   Id          { get; set; }
    public TKey                  RoomId      { get; set; } = default!;
    public NpgsqlRange<DateOnly> ValidDuring { get; set; }
}

internal class RoundTripGenericBooking<TKey>
{
    public int                   Id           { get; set; }
    public TKey                  RoomId       { get; set; } = default!;
    public NpgsqlRange<DateOnly> BookedDuring { get; set; }
}

internal class RoundTripGenericTemporalContext(DbContextOptions<RoundTripGenericTemporalContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RoundTripGenericRoom<int>>(b =>
        {
            b.ToTable("rt_generic_rooms");
            b.HasTemporalConstraint(x => x.RoomId, x => x.ValidDuring, name: "rt_generic_tc_rooms");
        });

        modelBuilder.Entity<RoundTripGenericBooking<int>>(b =>
        {
            b.ToTable("rt_generic_bookings");
            b.HasTemporalForeignKey<RoundTripGenericBooking<int>, RoundTripGenericRoom<int>>(
                x => x.RoomId, x => x.BookedDuring, x => x.RoomId, x => x.ValidDuring, name: "rt_generic_fk_bookings");
        });
    }
}
