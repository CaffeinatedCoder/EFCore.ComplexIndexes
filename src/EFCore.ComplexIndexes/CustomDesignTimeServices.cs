using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ComplexIndexes;

/// <summary>
/// Registered via the <c>.targets</c>-injected <c>DesignTimeServicesReferenceAttribute</c>;
/// replaces the migrations model differ with the provider-agnostic one. Backs off when a provider
/// satellite has already registered its own — see <see cref="ComplexIndexDesignTimeRegistration"/>.
/// </summary>
public class CustomDesignTimeServices : IDesignTimeServices
{
    /// <inheritdoc />
    public void ConfigureDesignTimeServices(IServiceCollection services)
        => ComplexIndexDesignTimeRegistration.AddCoreDiffer(services);
}
