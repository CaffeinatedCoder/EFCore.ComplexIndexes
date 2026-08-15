using System.Reflection;
using EFCore.ComplexIndexes.PostgreSQL;
using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

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
        var repoRoot = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "..", "..", "..", ".."));

        var package  = designTimeServices.Assembly.GetName().Name!;
        var targets  = Path.Combine(repoRoot, package, "build", $"{package}.targets");

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
}

#pragma warning restore EF1001
