using System.Reflection;
using EFCore.ComplexIndexes.PostgreSQL;
using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EFCore.ComplexIndexes.Tests;

#pragma warning disable EF1001

/// <summary>
/// A consumer of a provider satellite also gets the core package's
/// <c>DesignTimeServicesReferenceAttribute</c> injected transitively. EF's
/// <c>DesignTimeServicesBuilder</c> runs every discovered <see cref="IDesignTimeServices"/> in
/// attribute order and resolves last-registration-wins, so these tests pin that the differ selected
/// does not depend on the order NuGet happens to produce.
/// </summary>
[TestClass]
public class DesignTimeServiceRegistrationTests
{
    // Mirrors EF: run each configurator over one collection, then resolve.
    private static Type ResolveDiffer(params IDesignTimeServices[] configurators)
    {
        var services = new ServiceCollection();

        foreach (var configurator in configurators)
            configurator.ConfigureDesignTimeServices(services);

        var resolved = services.Last(d => d.ServiceType == typeof(IMigrationsModelDiffer));
        return resolved.ImplementationType!;
    }

    [TestMethod(DisplayName = "Npgsql differ wins over the core differ in either registration order")]
    public void Npgsql_satellite_wins_regardless_of_order()
    {
        Assert.AreEqual(
            typeof(NpgsqlComplexIndexMigrationsModelDiffer),
            ResolveDiffer(new CustomDesignTimeServices(), new NpgsqlComplexIndexDesignTimeServices()));

        Assert.AreEqual(
            typeof(NpgsqlComplexIndexMigrationsModelDiffer),
            ResolveDiffer(new NpgsqlComplexIndexDesignTimeServices(), new CustomDesignTimeServices()));
    }

    [TestMethod(DisplayName = "SQL Server differ wins over the core differ in either registration order")]
    public void SqlServer_satellite_wins_regardless_of_order()
    {
        Assert.AreEqual(
            typeof(SqlServerComplexIndexMigrationsModelDiffer),
            ResolveDiffer(new CustomDesignTimeServices(), new SqlServerComplexIndexDesignTimeServices()));

        Assert.AreEqual(
            typeof(SqlServerComplexIndexMigrationsModelDiffer),
            ResolveDiffer(new SqlServerComplexIndexDesignTimeServices(), new CustomDesignTimeServices()));
    }

    [TestMethod(DisplayName = "A satellite leaves exactly one differ registration behind")]
    public void Satellite_replaces_rather_than_stacks()
    {
        var services = new ServiceCollection();
        new CustomDesignTimeServices().ConfigureDesignTimeServices(services);
        new NpgsqlComplexIndexDesignTimeServices().ConfigureDesignTimeServices(services);

        Assert.ContainsSingle(services.Where(d => d.ServiceType == typeof(IMigrationsModelDiffer)));
    }

    /// <summary>
    /// EF's <c>AddDbContextDesignTimeServices</c> seeds the design-time collection with the context's
    /// own differ as a factory registration — which, once a consumer opts into the runtime wiring, is
    /// already one of ours. The design-time registration has to win over that seed in every
    /// combination, or a runtime <c>UseComplexIndexes()</c> could change which differ scaffolds.
    /// </summary>
    [TestMethod(DisplayName = "A differ seeded from the context does not displace the design-time registration")]
    public void Context_seeded_differ_does_not_win()
    {
        foreach (var configurators in new IDesignTimeServices[][]
                 {
                     [new CustomDesignTimeServices()],
                     [new CustomDesignTimeServices(), new NpgsqlComplexIndexDesignTimeServices()],
                     [new SqlServerComplexIndexDesignTimeServices(), new CustomDesignTimeServices()]
                 })
        {
            var services = new ServiceCollection();
            services.TryAdd(ServiceDescriptor.Scoped<IMigrationsModelDiffer>(_ => throw new InvalidOperationException("seed")));

            foreach (var configurator in configurators)
                configurator.ConfigureDesignTimeServices(services);

            var winner = services.Last(d => d.ServiceType == typeof(IMigrationsModelDiffer));

            Assert.IsNotNull(winner.ImplementationType, "The context-seeded factory registration won.");
            Assert.IsTrue(typeof(CustomMigrationsModelDiffer).IsAssignableFrom(winner.ImplementationType));
        }
    }

    [TestMethod(DisplayName = "Core alone still registers the core differ")]
    public void Core_alone_registers_core_differ()
        => Assert.AreEqual(typeof(CustomMigrationsModelDiffer), ResolveDiffer(new CustomDesignTimeServices()));

    // Without ForProvider, a solution referencing both satellites would hand one provider's context
    // to the other provider's differ — EF filters on this before invoking the configurator at all.
    [TestMethod(DisplayName = "Satellite design-time attributes are scoped to their provider")]
    public void Satellite_attributes_declare_their_provider()
    {
        Assert.AreEqual("Npgsql.EntityFrameworkCore.PostgreSQL",
                        ForProviderOf(typeof(NpgsqlComplexIndexDesignTimeServices)));

        Assert.AreEqual("Microsoft.EntityFrameworkCore.SqlServer",
                        ForProviderOf(typeof(SqlServerComplexIndexDesignTimeServices)));

        // The core package is deliberately provider-agnostic.
        Assert.IsNull(ForProviderOf(typeof(CustomDesignTimeServices)));
    }

    // The .targets inject the attribute into consuming assemblies, not into the package assembly
    // itself, so read the declaration straight out of the shipped .targets file.
    private static string? ForProviderOf(Type designTimeServices)
    {
        var package = designTimeServices.Assembly.GetName().Name!;
        var targets = Path.Combine(RepositoryRoot(), "src", package, "build", $"{package}.targets");

        Assert.IsTrue(File.Exists(targets), $"Expected design-time targets at '{targets}'.");

        var document = System.Xml.Linq.XDocument.Load(targets);
        var attribute = document.Descendants()
                                .Single(e => e.Name.LocalName == "AssemblyAttribute");

        Assert.AreEqual(
            $"{designTimeServices.FullName}, {package}",
            attribute.Elements().Single(e => e.Name.LocalName == "_Parameter1").Value,
            "The targets file must reference this package's IDesignTimeServices implementation.");

        return attribute.Elements()
                        .SingleOrDefault(e => e.Name.LocalName == "_Parameter2")
                       ?.Value;
    }

    // Located by walking up to the solution file rather than counting directory levels, so moving
    // this project does not quietly turn the assertions above into a "file not found" failure.
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);

        while (directory is not null && !directory.EnumerateFiles("*.slnx").Any())
            directory = directory.Parent;

        Assert.IsNotNull(directory, "Could not locate the repository root: no .slnx in any parent directory.");
        return directory!.FullName;
    }
}

#pragma warning restore EF1001
