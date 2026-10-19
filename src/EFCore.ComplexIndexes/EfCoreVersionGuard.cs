using Microsoft.EntityFrameworkCore;

namespace EFCore.ComplexIndexes;

/// <summary>
/// Refuses to register this package on an EF Core major other than 10.
/// </summary>
/// <remarks>
/// The <c>[10.0.0, 11.0.0)</c> dependency ranges do not keep an EF Core 11 application out on
/// their own. An EF Core 11 prerelease sorts below 11.0.0 and is inside them, with no warning at
/// all (measured with 11.0.0-rc.2), and a direct reference to a release of Npgsql 11 (or EF Core
/// 11) wins over the transitive ceiling with NU1608, which is only a warning. The package then runs against a differ base class and
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

    // Until the successor has a release on nuget.org, its name alone leads nowhere.
    internal const string SuccessorTracking = "https://github.com/CaffeinatedCoder/EFCore.ComplexIndexes/issues/37";

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

        // The major alone: EF Core's assembly version is N.0.0.0 for every N.x release.
        throw new InvalidOperationException(
            $"EFCore.ComplexIndexes supports EF Core {SupportedMajor} only, but this application loaded "
          + $"EF Core {version.Major}. NuGet lets it through: an EF Core {SupportedMajor + 1} prerelease falls inside "
          + $"this package's [{SupportedMajor}.0.0, {SupportedMajor + 1}.0.0) range, and a direct reference to a "
          + $"release overrides that range with only warning NU1608. For EF Core {SupportedMajor + 1} and later, use "
          + $"{SuccessorPackage} instead: {SuccessorTracking}");
    }

    private sealed class Scope(Version? previous) : IDisposable
    {
        public void Dispose() => Override.Value = previous;
    }
}
