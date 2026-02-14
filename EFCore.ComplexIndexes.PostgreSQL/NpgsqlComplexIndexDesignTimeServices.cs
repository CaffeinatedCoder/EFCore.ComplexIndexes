using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace EFCore.ComplexIndexes.PostgreSQL;

public class NpgsqlComplexIndexDesignTimeServices : IDesignTimeServices
{
    public void ConfigureDesignTimeServices(IServiceCollection services)
    {
        services.AddSingleton<IMigrationsModelDiffer, NpgsqlComplexIndexMigrationsModelDiffer>();
    }
}