using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Paddockside.Application.Messaging;
using Paddockside.Infrastructure.Email;
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
        services.AddScoped<TenantScopedDb>();
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

    /// <summary>Outbound email through Postmark (settings "Email" and "Postmark").</summary>
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.Section));
        services.Configure<PostmarkOptions>(configuration.GetSection(PostmarkOptions.Section));
        services.AddHttpClient<IEmailSender, PostmarkEmailSender>(http =>
        {
            http.BaseAddress = new Uri("https://api.postmarkapp.com/");
            http.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton<OwnerEmailRenderer>();
        services.AddScoped<EmailDispatcher>();
        return services;
    }
}
