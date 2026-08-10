using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ComplexIndexes.SqlServer;

/// <summary>
/// Registered via the <c>.targets</c>-injected <c>DesignTimeServicesReferenceAttribute</c>;
/// replaces the migrations model differ with the SQL Server-aware one.
/// </summary>
public class SqlServerComplexIndexDesignTimeServices : IDesignTimeServices
{
    public void ConfigureDesignTimeServices(IServiceCollection services)
    {
        services.AddSingleton<IMigrationsModelDiffer, SqlServerComplexIndexMigrationsModelDiffer>();
    }
}
