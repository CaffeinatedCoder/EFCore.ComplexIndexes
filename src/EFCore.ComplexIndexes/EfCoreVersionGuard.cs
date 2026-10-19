using Microsoft.EntityFrameworkCore;

namespace EFCore.ComplexIndexes;

/// <summary>
/// Refuses to register this package on an EF Core major other than 10.
/// </summary>
/// <remarks>
/// The <c>[10.0.0, 11.0.0)</c> dependency ranges do not keep an EF Core 11 application out on
/// their own: a direct reference to Npgsql 11 (or EF Core 11) wins over the transitive ceiling with
/// NU1608, which is only a warning. The package then runs against a differ base class and
/// EF1001-marked internals it was never tested on, and the first sign is an issue filed about a
/// line this package does not serve. Every registration entry point calls
/// <see cref="ThrowIfUnsupported"/> so that application fails at startup or at
/// <c>migrations add</c> with the name of the package that does serve it.
/// <para>
/// The check is on EF Core's assembly major, never on the runtime: this <c>net10.0</c> package
/// installs into a <c>net11.0</c> application, and EF Core 10 on .NET 11 is a combination
/// Microsoft supports.
/// </para>
/// </remarks>
internal static class EfCoreVersionGuard
{
    internal const int SupportedMajor = 10;

    internal const string SuccessorPackage = "CodoMetis.EFCore.SchemaKit";

    private static readonly Version LoadedVersion = typeof(DbContext).Assembly.GetName().Version!;

    // Async-local rather than a plain static, so tests running in parallel each see their own.
    private static readonly AsyncLocal<Version?> Override = new();

    /// <summary>The EF Core version the guard checks: the loaded assembly's, unless a test fed one.</summary>
    internal static Version EfCoreVersion => Override.Value ?? LoadedVersion;

    /// <summary>
    /// Makes <see cref="EfCoreVersion"/> report <paramref name="version"/> on the current async flow
    /// until the returned scope is disposed. For tests only.
    /// </summary>
    internal static IDisposable UseVersion(Version version)
    {
        var previous = Override.Value;
        Override.Value = version;
        return new Scope(previous);
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when EF Core's major version is not
    /// <see cref="SupportedMajor"/>.
    /// </summary>
    internal static void ThrowIfUnsupported()
    {
        var version = EfCoreVersion;
        if (version.Major == SupportedMajor)
            return;

        throw new InvalidOperationException(
            $"EFCore.ComplexIndexes supports EF Core {SupportedMajor} only, but this application loaded "
          + $"EF Core {version.Major}.{version.Minor}. NuGet allows it because a direct reference to EF Core "
          + $"or its provider overrides this package's [{SupportedMajor}.0.0, {SupportedMajor + 1}.0.0) "
          + $"range with warning NU1608. For EF Core 11 and later, use {SuccessorPackage} instead.");
    }

    private sealed class Scope(Version? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }
}
