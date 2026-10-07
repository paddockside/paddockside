using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Paddockside.Application.Messaging;
using Paddockside.Infrastructure.Email;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Inbound;
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
        // Interceptors registered elsewhere (e.g. the one that wakes the email sender) join every context.
        services.AddDbContext<PaddocksideDbContext>((provider, options) => options
            .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure())
            .AddInterceptors(provider.GetServices<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor>()));
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
        services.AddSingleton<EmailDispatchSignal>();
        services.AddSingleton<Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor, MailQueuedInterceptor>();

        // Owner sign-in: links and codes by email and text (identity-access.md §4.1).
        services.Configure<TwilioOptions>(configuration.GetSection(TwilioOptions.Section));
        services.AddHttpClient<ISmsSender, TwilioSmsSender>(http =>
        {
            http.BaseAddress = new Uri("https://api.twilio.com/");
            http.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddScoped<ClientAccess.ClientSignIn>();
        services.AddScoped<Identity.StaffInvitations>();
        return services;
    }

    /// <summary>
    /// Inbound email: Azure blob storage and the Storage Queue when Storage:BlobEndpoint and Storage:QueueEndpoint are
    /// set (always in Azure), otherwise files under <paramref name="contentRoot"/> and an in-memory queue.
    /// </summary>
    public static IServiceCollection AddInbound(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        var storage = configuration.GetSection(StorageOptions.Section).Get<StorageOptions>() ?? new StorageOptions();
        if (!string.IsNullOrEmpty(storage.BlobEndpoint) && !string.IsNullOrEmpty(storage.QueueEndpoint))
        {
            services.AddSingleton<Azure.Core.TokenCredential>(new Azure.Identity.DefaultAzureCredential());
            services.AddSingleton<IInboundStore, BlobInboundStore>();
            services.AddSingleton<IInboundQueue, StorageInboundQueue>();
        }
        else
        {
            services.AddSingleton<IInboundStore>(new FileInboundStore(Path.Combine(contentRoot, storage.LocalPath)));
            services.AddSingleton<IInboundQueue, MemoryInboundQueue>();
        }

        services.AddScoped<InboundReceiver>();
        services.AddScoped<InboundProcessor>();
        return services;
    }
}
