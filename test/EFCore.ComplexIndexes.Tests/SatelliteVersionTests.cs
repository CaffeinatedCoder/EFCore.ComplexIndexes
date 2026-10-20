using System.Reflection;
using EFCore.ComplexIndexes.PostgreSQL;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NpgsqlTypes;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// A satellite runs only against the core it was built with (#169).
/// </summary>
/// <remarks>
/// Up to 5.4.1 the satellites declare <c>&gt;=</c> on the core, so a consumer who updates a direct
/// core reference alone runs a 5.4.1 satellite against this core, and NuGet says nothing. That
/// satellite predates <see cref="CustomMigrationsModelDiffer.IsSatelliteDiffer"/>, so it was refused
/// as the core differ, with advice to call the registration the consumer already called. The
/// satellites a test can compile are stand-ins: an assembly with a satellite's name and another
/// version, deriving from the core differ the way a published satellite does.
/// </remarks>
[TestClass]
public class SatelliteVersionTests
{
    private class Order
    {
        public int                    Id     { get; set; }
        public Address                Ship   { get; set; } = new();
        public NpgsqlRange<DateTime>  Period { get; set; }
    }

    private class Address
    {
        public string City { get; set; } = "";
    }

    private class ExclusionContext(DbContextOptions options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Order>(entity =>
               {
                   entity.ComplexProperty(o => o.Ship);
                   entity.HasExclusionConstraint(o => o.Ship.City, o => o.Period, name: "ex_city_period");
               });
    }

    private static IRelationalModel Model => MigrationHarness.NpgsqlModel<ExclusionContext>();

    [TestMethod(DisplayName = "A satellite built as another version is refused with both versions, not as the core differ")]
    public void Satellite_of_another_version_is_refused_naming_both_versions()
    {
        var differ = Compile("EFCore.ComplexIndexes.PostgreSQL", "5.4.1", typeof(CustomMigrationsModelDiffer).FullName!);

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => Diff(differ, Model));

        var core = RepositoryLayout.ShippedVersion;
        StringAssert.Contains(error.Message, $"EFCore.ComplexIndexes.PostgreSQL 5.4.1 is running against EFCore.ComplexIndexes {core}");
        StringAssert.Contains(error.Message, $"EFCore.ComplexIndexes.PostgreSQL {core} with this core");
        Assert.DoesNotContain("core differ", error.Message);
    }

    [TestMethod(DisplayName = "A satellite built as the core's version diffs as the satellite")]
    public void Satellite_of_the_cores_version_is_not_refused()
    {
        var differ = Compile("EFCore.ComplexIndexes.PostgreSQL", RepositoryLayout.ShippedVersion, typeof(CustomMigrationsModelDiffer).FullName!);

        // The stand-in is no satellite differ, so the core's own refusal is what it gets: the
        // version check let it through.
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => Diff(differ, Model));

        StringAssert.Contains(error.Message, "provider-agnostic core differ");
    }

    [TestMethod(DisplayName = "A provider's own subclass of the core differ, in an assembly that is no satellite, is not checked")]
    public void Subclass_outside_the_satellites_is_not_checked()
    {
        var differ = Compile("Contoso.Migrations", "1.0.0", typeof(CustomMigrationsModelDiffer).FullName!);

        Assert.IsEmpty(Diff(differ, null));
    }

    [TestMethod(DisplayName = "An application's subclass of a satellite differ is checked by the satellite's version, not its own")]
    public void Subclass_of_a_satellite_differ_is_checked_by_the_satellite()
    {
        var differ = Compile("Contoso.Migrations", "1.0.0", typeof(NpgsqlComplexIndexMigrationsModelDiffer).FullName!);

        Assert.Contains(
            o => o is Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation sql && sql.Sql.Contains("EXCLUDE"),
            Diff(differ, Model));
    }

    private static IReadOnlyList<Microsoft.EntityFrameworkCore.Migrations.Operations.MigrationOperation> Diff(Type differ, IRelationalModel? target)
    {
        using var context = new DbContext(MigrationHarness.NpgsqlOptions());
        return MigrationHarness.CreateDiffer(differ, context).GetDifferences(null, target);
    }

    // A differ deriving from baseType, in an assembly of that name and package version.
    private static Type Compile(string assemblyName, string version, string baseType)
    {
        var code =
            $$"""
              using Microsoft.EntityFrameworkCore.Migrations;
              using Microsoft.EntityFrameworkCore.Metadata;
              using Microsoft.EntityFrameworkCore.Storage;
              using Microsoft.EntityFrameworkCore.Update.Internal;

              [assembly: System.Reflection.AssemblyVersion("{{version}}.0")]
              [assembly: System.Reflection.AssemblyInformationalVersion("{{version}}+0123456789abcdef")]

              #pragma warning disable EF1001
              public sealed class StandInDiffer(
                  IRelationalTypeMappingSource     typeMappingSource,
                  IMigrationsAnnotationProvider    migrationsAnnotationProvider,
                  IRelationalAnnotationProvider    relationalAnnotationProvider,
                  IRowIdentityMapFactory           rowIdentityMapFactory,
                  CommandBatchPreparerDependencies commandBatchPreparerDependencies)
                  : {{baseType}}(typeMappingSource, migrationsAnnotationProvider, relationalAnnotationProvider,
                                 rowIdentityMapFactory, commandBatchPreparerDependencies);
              """;

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                        .Split(Path.PathSeparator)
                        .Concat(AppDomain.CurrentDomain.GetAssemblies()
                                         .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                                         .Select(a => a.Location))
                        .Append(typeof(NpgsqlComplexIndexMigrationsModelDiffer).Assembly.Location)
                        .Distinct()
                        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                        .ToList();

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [CSharpSyntaxTree.ParseText(code)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);

        Assert.IsTrue(
            result.Success,
            string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        // Load(byte[]) gives each assembly a load context of its own, so a stand-in may share a
        // name with the real satellite the test run has loaded.
        var assembly = Assembly.Load(stream.ToArray());

        Assert.AreEqual(assemblyName, assembly.GetName().Name);
        return assembly.GetType("StandInDiffer", throwOnError: true)!;
    }
}
