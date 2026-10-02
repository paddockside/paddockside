using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>The real Api, in memory, over the isolation database.</summary>
public sealed class ApiFactory(IsolationDatabase db) : WebApplicationFactory<Program>
{
    public const string BreachedPassword = "correct horse battery staple";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Paddockside", db.ConnectionString);
        builder.UseSetting("RateLimits:AuthPerMinute", "100000");
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IBreachedPasswordList>(new FakeBreachedList()));
    }

    /// <summary>A browser-like client: HTTPS (the session cookie is Secure) and a cookie jar.</summary>
    public HttpClient Browser() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });

    /// <summary>Creates a person with a membership; returns the email. Passwords are generated per person.</summary>
    public async Task<(string Email, string Password)> CreatePersonAsync(Guid tenantId, MemberRole role)
    {
        var email = $"{role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}@tenant.test";
        var password = $"test-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8))}";

        await using var scope = Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Person>>();
        var person = new Person { UserName = email, Email = email, EmailConfirmed = true };
        var created = await users.CreateAsync(person, password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

        var identity = scope.ServiceProvider.GetRequiredService<PaddocksideIdentityDbContext>();
        identity.Memberships.Add(new Membership { PersonId = person.Id, TenantId = tenantId, Role = role });
        await identity.SaveChangesAsync();
        return (email, password);
    }

    private sealed class FakeBreachedList : IBreachedPasswordList
    {
        public Task<bool> ContainsAsync(string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(password == BreachedPassword);
    }
}

/// <summary>Sends requests the way the Web app does: JSON, with the API request header on writes.</summary>
public static class BrowserRequests
{
    public static Task<HttpResponseMessage> PostApiAsync(this HttpClient client, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Paddockside-Request", "1");
        return client.SendAsync(request);
    }
}

/// <summary>RFC 6238 codes, as an authenticator app would show them.</summary>
public static class Totp
{
    public static string Code(string base32Key, DateTimeOffset? at = null)
    {
        var step = (long)Math.Floor((at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30.0);
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);

        var hash = HMACSHA1.HashData(Base32(base32Key), counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>The secret from an otpauth:// URI.</summary>
    public static string SecretFrom(string authenticatorUri) =>
        authenticatorUri.Split('?', 2)[1].Split('&').Single(p => p.StartsWith("secret=", StringComparison.Ordinal))["secret=".Length..];

    private static byte[] Base32(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(input.TrimEnd('=').ToUpperInvariant().Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        return Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
    }
}
