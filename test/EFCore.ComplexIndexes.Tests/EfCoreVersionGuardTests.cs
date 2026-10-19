using EFCore.ComplexIndexes.PostgreSQL;
using EFCore.ComplexIndexes.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ComplexIndexes.Tests;

/// <summary>
/// This package serves EF Core 10 only, and its <c>[10.0.0, 11.0.0)</c> ranges do not keep EF Core
/// 11 out: a direct provider reference overrides them with NU1608, a warning. So every registration
/// entry point refuses another major and names the successor package. Each entry point is a row,
/// because one that skips the check is exactly the hole an EF Core 11 application walks through.
/// </summary>
[TestClass]
public class EfCoreVersionGuardTests
{
    private class GuardContext : DbContext;

    public static IEnumerable<object[]> EntryPoints =>
    [
        ["UseComplexIndexes", (Action)(() => new DbContextOptionsBuilder().UseComplexIndexes())],
        ["UseComplexIndexes<TContext>", (Action)(() => new DbContextOptionsBuilder<GuardContext>().UseComplexIndexes())],
        ["AddComplexIndexes", (Action)(() => new ServiceCollection().AddComplexIndexes())],
        ["UseNpgsqlComplexIndexes", (Action)(() => new DbContextOptionsBuilder().UseNpgsqlComplexIndexes())],
        ["UseNpgsqlComplexIndexes<TContext>", (Action)(() => new DbContextOptionsBuilder<GuardContext>().UseNpgsqlComplexIndexes())],
        ["AddNpgsqlComplexIndexes", (Action)(() => new ServiceCollection().AddNpgsqlComplexIndexes())],
        ["UseSqlServerComplexIndexes", (Action)(() => new DbContextOptionsBuilder().UseSqlServerComplexIndexes())],
        ["UseSqlServerComplexIndexes<TContext>", (Action)(() => new DbContextOptionsBuilder<GuardContext>().UseSqlServerComplexIndexes())],
        ["AddSqlServerComplexIndexes", (Action)(() => new ServiceCollection().AddSqlServerComplexIndexes())],
        ["CustomDesignTimeServices", (Action)(() => new CustomDesignTimeServices().ConfigureDesignTimeServices(new ServiceCollection()))],
        ["NpgsqlComplexIndexDesignTimeServices", (Action)(() => new NpgsqlComplexIndexDesignTimeServices().ConfigureDesignTimeServices(new ServiceCollection()))],
        ["SqlServerComplexIndexDesignTimeServices", (Action)(() => new SqlServerComplexIndexDesignTimeServices().ConfigureDesignTimeServices(new ServiceCollection()))],
    ];

    [TestMethod(DisplayName = "EF Core 11 is refused, naming the successor package")]
    [DynamicData(nameof(EntryPoints))]
    public void EfCore11_is_refused(string entryPoint, Action register)
    {
        using var _ = EfCoreVersionGuard.UseVersion(new Version(11, 0, 0, 0));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(register, entryPoint);

        Assert.Contains("CodoMetis.EFCore.SchemaKit", ex.Message, entryPoint);
        Assert.Contains("EF Core 11.0", ex.Message, entryPoint);
    }

    [TestMethod(DisplayName = "EF Core 10 registers")]
    [DynamicData(nameof(EntryPoints))]
    public void EfCore10_registers(string entryPoint, Action register)
    {
        using var _ = EfCoreVersionGuard.UseVersion(new Version(10, 0, 0, 0));

        register();
    }

    /// <summary>
    /// The rows above feed the version, so this pins what the guard reads when nothing is fed: the
    /// EF Core this suite runs on, which is 10, so an unconfigured application is not refused. It
    /// cannot tell EF Core's assembly from the runtime's, since both report 10.0.0.0 here; that the
    /// guard reads EF Core's rests on <see cref="EfCoreVersionGuard"/> itself.
    /// </summary>
    [TestMethod(DisplayName = "Unfed, the guard reads the loaded EF Core assembly")]
    public void Reads_the_loaded_EfCore_assembly()
    {
        Assert.AreEqual(typeof(DbContext).Assembly.GetName().Version, EfCoreVersionGuard.EfCoreVersion);
        Assert.AreEqual(10, EfCoreVersionGuard.EfCoreVersion.Major);
    }
}
