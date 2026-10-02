using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers both databases against Azure SQL. The host must also register an
    /// <see cref="Application.Tenancy.ITenantContext"/> that reads the session's active tenant.
    /// </summary>
    public static IServiceCollection AddPaddocksideDatabase(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<PaddocksideDbContext>(options =>
            options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
        services.AddDbContext<PaddocksideIdentityDbContext>(options =>
            options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()));
        return services;
    }

    /// <summary>The breached-password list used when staff passwords are set.</summary>
    public static IServiceCollection AddBreachedPasswordList(this IServiceCollection services)
    {
        services.AddHttpClient<IBreachedPasswordList, PwnedPasswordsList>(http =>
        {
            http.BaseAddress = new Uri("https://api.pwnedpasswords.com/");
            http.Timeout = TimeSpan.FromSeconds(5);
        });
        return services;
    }
}
