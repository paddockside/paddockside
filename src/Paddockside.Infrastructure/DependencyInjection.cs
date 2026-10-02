using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the database against Azure SQL. The host must also register an
    /// <see cref="Application.Tenancy.ITenantContext"/> that reads the session's active tenant.
    /// </summary>
    public static IServiceCollection AddPaddocksideDatabase(this IServiceCollection services, string connectionString) =>
        services.AddDbContext<PaddocksideDbContext>(options =>
            options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
}
