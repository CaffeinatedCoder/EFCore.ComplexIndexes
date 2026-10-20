using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ComplexIndexes;

/// <summary>
/// Added by <c>UseComplexIndexes()</c> to wrap SQLite's migrations SQL generator in
/// <see cref="SqliteRebuildMigrationsSqlGenerator"/>. On any other provider it registers nothing.
/// </summary>
/// <remarks>
/// An extension rather than a <c>ReplaceService</c>, because a replacement names the type it puts in
/// and cannot see the one it takes out. EF Core applies extensions in the order they were first added,
/// so this one finds SQLite's registration only when <c>UseComplexIndexes()</c> follows
/// <c>UseSqlite()</c>; the differ says so when it refuses a rebuild for that reason.
/// </remarks>
internal sealed class ComplexIndexOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) => SqliteRebuildMigrationsSqlGenerator.Decorate(services);

    public void Validate(IDbContextOptions options) { }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using ComplexIndexes ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
            => debugInfo["ComplexIndexes:SqliteRebuild"] = "1";
    }
}
