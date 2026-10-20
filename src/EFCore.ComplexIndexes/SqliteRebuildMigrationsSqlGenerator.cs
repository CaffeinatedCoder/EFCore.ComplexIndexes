using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ComplexIndexes;

/// <summary>
/// Wraps SQLite's migrations SQL generator so that the complex indexes on a table the migration
/// rebuilds are created after the rebuild, which drops them.
/// </summary>
/// <remarks>
/// <c>SqliteMigrationsSqlGenerator</c> emits every table rebuild after all other operations of the
/// migration and re-creates only the indexes in EF Core's model, so no operation in the migration can
/// run after it. The differ marks the creates that must (<see cref="ComplexIndexAnnotations.AfterTableRebuild"/>)
/// and puts them before the base operations, with a call to a function that does not exist ANDed onto
/// their filter. This generator takes them out, generates the rest with the provider's generator, and
/// appends them without the marker, under the table's name after the migration's renames. Any other
/// generator runs them where they stand and fails on the marker before the rebuild has dropped
/// anything. The marker is in the filter, not the columns: with SQLite's double-quoted string literals
/// enabled (<c>SQLITE_DBCONFIG_DQS_DDL</c>, on in a default build of SQLite), a column that does not
/// exist is taken for a string and the index is created.
/// </remarks>
internal sealed class SqliteRebuildMigrationsSqlGenerator(IMigrationsSqlGenerator inner) : IMigrationsSqlGenerator
{
    private const string Marker = "__requires_UseComplexIndexes__()";
    private const string Suffix = ") AND " + Marker;

    /// <summary>The filter a marked create carries: the index's own, if any, ANDed with the marker.</summary>
    public static string Mark(string? filter) => filter is null ? Marker : $"({filter}{Suffix}";

    public IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel?                           model   = null,
        MigrationsSqlGenerationOptions    options = MigrationsSqlGenerationOptions.Default)
    {
        List<MigrationOperation>? rest = null;
        var afterRebuild = new List<CreateIndexOperation>();

        for (var i = 0; i < operations.Count; i++)
        {
            switch (operations[i])
            {
                case CreateIndexOperation create when create[ComplexIndexAnnotations.AfterTableRebuild] is true:
                    rest ??= [.. operations.Take(i)];
                    afterRebuild.Add(Unmark(create));
                    continue;

                // The rebuild runs under the table's new name, as EF's SQLite generator tracks it.
                case RenameTableOperation rename:
                    foreach (var create in afterRebuild.Where(c => c.Table == rename.Name && c.Schema == rename.Schema))
                        (create.Table, create.Schema) = (rename.NewName ?? rename.Name, rename.NewSchema);
                    break;
            }

            rest?.Add(operations[i]);
        }

        return rest is null
                   ? inner.Generate(operations, model, options)
                   : [.. inner.Generate(rest, model, options), .. inner.Generate(afterRebuild, model, options)];
    }

    // A copy: a migration's operations are generated more than once (Up for the script, again to apply).
    private static CreateIndexOperation Unmark(CreateIndexOperation marked)
    {
        var filter = marked.Filter switch
        {
            Marker => null,
            { } f when f.StartsWith('(') && f.EndsWith(Suffix, StringComparison.Ordinal) => f[1..^Suffix.Length],
            _ => throw new InvalidOperationException(
                     $"The complex index '{marked.Name}' is marked to be created after SQLite's table rebuild, but its filter "
                   + $"does not end in the marker {Marker} this package writes there. Restore the filter as scaffolded: "
                   + $"'{Marker}' for an index without one, '(<filter>{Suffix}' for one with."),
        };

        var create = new CreateIndexOperation
        {
            Name         = marked.Name,
            Table        = marked.Table,
            Schema       = marked.Schema,
            Columns      = marked.Columns,
            IsUnique     = marked.IsUnique,
            IsDescending = marked.IsDescending,
            Filter       = filter,
        };
        create.AddAnnotations(marked.GetAnnotations().Where(a => a.Name != ComplexIndexAnnotations.AfterTableRebuild));
        return create;
    }

    /// <summary>
    /// Replaces the last registration of <see cref="IMigrationsSqlGenerator"/> with this generator
    /// wrapped around it, when that registration is SQLite's generator or one derived from it.
    /// </summary>
    /// <remarks>
    /// Wrapping the registration rather than replacing the service keeps whatever another package
    /// registered before it. A <c>ReplaceService</c> keyed on the registered type no longer finds that
    /// registration once it is wrapped, so the wrapper builds the replacement instead; one keyed on
    /// the service alone replaces the wrapper, and the differ, which asks the context for the
    /// generator in effect, refuses the rebuild as it would without this package's registration.
    /// </remarks>
    public static void Decorate(IServiceCollection services)
    {
        var index = -1;
        for (var i = 0; i < services.Count; i++)
            if (services[i].ServiceType == typeof(IMigrationsSqlGenerator))
                index = i;

        if (index < 0 || services[index] is not { ImplementationType: { } registered } descriptor || !IsSqliteGenerator(registered))
            return;

        services[index] = ServiceDescriptor.Describe(
            typeof(IMigrationsSqlGenerator),
            provider => new SqliteRebuildMigrationsSqlGenerator(CreateInner(provider, registered)),
            descriptor.Lifetime);
    }

    private static IMigrationsSqlGenerator CreateInner(IServiceProvider provider, Type registered)
    {
        var replaced = provider.GetService<IDbContextOptions>()?.FindExtension<CoreOptionsExtension>()?.ReplacedServices;
        var type     = replaced?.TryGetValue((typeof(IMigrationsSqlGenerator), registered), out var replacement) == true
                           ? replacement
                           : registered;
        return (IMigrationsSqlGenerator)ActivatorUtilities.CreateInstance(provider, type);
    }

    /// <summary>Whether <paramref name="type"/> is EF Core's SQLite migrations SQL generator or derives from it.</summary>
    public static bool IsSqliteGenerator(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (t.FullName == "Microsoft.EntityFrameworkCore.Migrations.SqliteMigrationsSqlGenerator")
                return true;
        return false;
    }
}
