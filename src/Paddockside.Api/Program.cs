using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Paddockside.Api.Auth;
using Paddockside.Api.Development;
using Paddockside.Api.Horses;
using Paddockside.Api.Security;
using Paddockside.Api.Tenancy;
using Paddockside.Application.Tenancy;
using Paddockside.Infrastructure;
using Paddockside.Infrastructure.Identity;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ITenantContext, SessionTenantContext>();
// Which connection string to use: "Paddockside" (LocalDB locally; Key Vault in Azure) unless DatabaseConnection
// names another, e.g. the https-azure launch profile picks PaddocksideAzureDev.
var connectionName = builder.Configuration["DatabaseConnection"] ?? "Paddockside";
builder.Services.AddPaddocksideDatabase(
    builder.Configuration.GetConnectionString(connectionName)
    ?? throw new InvalidOperationException($"ConnectionStrings:{connectionName} is not configured."));
builder.Services.AddBreachedPasswordList();

// Staff sign-in: password + mandatory TOTP (identity-access.md §4.2).
builder.Services
    .AddIdentityCore<Person>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredUniqueChars = 1;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<PaddocksideIdentityDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<StaffClaimsPrincipalFactory>()
    .AddPasswordValidator<BreachedPasswordValidator>();

builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(StaffSessionCookie.Configure);
builder.Services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, StaffSessionCookie.ConfigurePending);

builder.Services.AddAuthorizationBuilder().AddPolicy(StaffPolicy.Name, StaffPolicy.Build);

// Rate limiting on every sign-in step, per client IP (non-functional.md §2).
var authRequestsPerMinute = builder.Configuration.GetValue("RateLimits:AuthPerMinute", 10);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthEndpoints.RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authRequestsPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseWebAssemblyDebugging();
    await DevelopmentSeeder.SeedAsync(app.Services, app.Configuration);
}
else
{
    app.UseHsts();
}

app.UseHttpsRedirection();

// The Blazor app is served from here, so the session cookie never has to cross origins.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<RequireApiRequestHeader>();

app.MapAuthEndpoints();
app.MapHorseEndpoints();
app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Visible to the integration tests.</summary>
public partial class Program;
