using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ComplexIndexes;

/// <summary>
/// Shared registration logic for the design-time <see cref="IMigrationsModelDiffer"/>.
/// </summary>
/// <remarks>
/// A consumer of a provider satellite gets <em>two</em> <c>DesignTimeServicesReferenceAttribute</c>s:
/// the satellite's own, plus the core package's, which rides along through its
/// <c>buildTransitive</c> targets. EF's <c>DesignTimeServicesBuilder</c> simply enumerates the
/// attributes and runs each <see cref="Microsoft.EntityFrameworkCore.Design.IDesignTimeServices"/>
/// in turn, and service resolution is last-registration-wins — so a plain
/// <c>AddSingleton</c> on both sides would pick a differ by luck of NuGet's restore order, and the
/// core differ winning silently drops every provider-specific feature (index options, exclusion and
/// temporal constraints, JSON member resolution).
/// <para>
/// Both sides therefore coordinate here rather than racing: a satellite drops any core registration
/// before appending its own, and the core backs off when a satellite differ is already present.
/// The outcome is the same whichever order the two configurators run in.
/// </para>
/// </remarks>
internal static class ComplexIndexDesignTimeRegistration
{
    /// <summary>
    /// Registers the provider-agnostic core differ, unless a provider satellite has already
    /// registered one derived from it.
    /// </summary>
    public static void AddCoreDiffer(IServiceCollection services)
    {
        if (services.Any(IsSatelliteDiffer))
            return;

        services.AddSingleton<IMigrationsModelDiffer, CustomMigrationsModelDiffer>();
    }

    /// <summary>
    /// Registers a provider satellite's differ, removing any core registration first so the more
    /// specific differ wins regardless of configurator order.
    /// </summary>
    public static void AddSatelliteDiffer<TDiffer>(IServiceCollection services)
        where TDiffer : CustomMigrationsModelDiffer
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType        == typeof(IMigrationsModelDiffer)
             && services[i].ImplementationType == typeof(CustomMigrationsModelDiffer))
                services.RemoveAt(i);
        }

        services.AddSingleton<IMigrationsModelDiffer, TDiffer>();
    }

    // A satellite differ derives from the core one without *being* the core one.
    private static bool IsSatelliteDiffer(ServiceDescriptor descriptor)
        => descriptor.ServiceType == typeof(IMigrationsModelDiffer)
        && descriptor.ImplementationType is { } implementation
        && implementation != typeof(CustomMigrationsModelDiffer)
        && typeof(CustomMigrationsModelDiffer).IsAssignableFrom(implementation);
}
