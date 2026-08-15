using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ComplexIndexes.PostgreSQL;

/// <summary>
/// Registered via the <c>.targets</c>-injected <c>DesignTimeServicesReferenceAttribute</c> (scoped
/// to the Npgsql provider); replaces the migrations model differ with the PostgreSQL-aware one,
/// superseding the core registration — see <see cref="ComplexIndexDesignTimeRegistration"/>.
/// </summary>
public class NpgsqlComplexIndexDesignTimeServices : IDesignTimeServices
{
    /// <inheritdoc />
    public void ConfigureDesignTimeServices(IServiceCollection services)
        => ComplexIndexDesignTimeRegistration.AddSatelliteDiffer<NpgsqlComplexIndexMigrationsModelDiffer>(services);
}
