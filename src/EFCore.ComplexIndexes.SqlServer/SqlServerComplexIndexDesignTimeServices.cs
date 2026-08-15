using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ComplexIndexes.SqlServer;

/// <summary>
/// Registered via the <c>.targets</c>-injected <c>DesignTimeServicesReferenceAttribute</c> (scoped
/// to the SQL Server provider); replaces the migrations model differ with the SQL Server-aware one,
/// superseding the core registration — see <see cref="ComplexIndexDesignTimeRegistration"/>.
/// </summary>
public class SqlServerComplexIndexDesignTimeServices : IDesignTimeServices
{
    /// <inheritdoc />
    public void ConfigureDesignTimeServices(IServiceCollection services)
        => ComplexIndexDesignTimeRegistration.AddSatelliteDiffer<SqlServerComplexIndexMigrationsModelDiffer>(services);
}
