using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Paddockside.Api.Webhooks;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Identity;
using Paddockside.Infrastructure.Inbound;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>The real Api, in memory, over the isolation database.</summary>
public sealed class ApiFactory(IsolationDatabase db) : WebApplicationFactory<Program>
{
    public const string BreachedPassword = "correct horse battery staple";
    public const string WebhookUsername = "postmark-test";
    public const string WebhookPassword = "webhook-test-password";

    /// <summary>Records every email instead of sending it.</summary>
    public FakeEmailSender Emails { get; } = new();

    /// <summary>Records every text instead of sending it.</summary>
    public FakeSmsSender Texts { get; } = new();

    public const string PortalBaseUrl = "https://portal.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // As strict as Development: a singleton holding a scoped service (one request's database) fails at start-up.
        builder.UseDefaultServiceProvider(options => (options.ValidateScopes, options.ValidateOnBuild) = (true, true));
        builder.UseSetting("ConnectionStrings:Paddockside", db.ConnectionString);
        builder.UseSetting("RateLimits:AuthPerMinute", "100000");
        builder.UseSetting("Email:DispatchEnabled", "false"); // tests run the dispatcher themselves
        builder.UseSetting("Postmark:WebhookUsername", WebhookUsername);
        builder.UseSetting("Postmark:WebhookPassword", WebhookPassword);
        builder.UseSetting("Email:PortalBaseUrl", PortalBaseUrl);
        // Recheck every session on every request, so each test also proves sessions survive the check unchanged.
        builder.UseSetting("Sessions:CheckSeconds", "0");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IBreachedPasswordList>(new FakeBreachedList());
            services.AddSingleton<IEmailSender>(Emails);
            services.AddSingleton<ISmsSender>(Texts);

            // Inbound: files in a temp folder, the in-memory queue, and no background processor (tests drain it).
            services.AddSingleton<IInboundStore>(new FileInboundStore(InboundStoreRoot));
            services.AddSingleton<IInboundQueue, MemoryInboundQueue>();
            foreach (var hosted in services.Where(s => s.ImplementationType == typeof(InboundProcessingService)).ToList())
                services.Remove(hosted);
        });
    }

    public string InboundStoreRoot { get; } = Path.Combine(Path.GetTempPath(), $"paddockside-inbound-{Guid.NewGuid():N}");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(InboundStoreRoot)) Directory.Delete(InboundStoreRoot, recursive: true);
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

    /// <summary>A browser signed in as a new staff member of the tenant, authenticator enrolled.</summary>
    public async Task<HttpClient> SignedInAsync(Guid tenantId, MemberRole role)
    {
        var (email, password) = await CreatePersonAsync(tenantId, role);
        return await SignedInWithAsync(email, password);
    }

    /// <summary>A browser signed in as an existing person, authenticator enrolled.</summary>
    public async Task<HttpClient> SignedInWithAsync(string email, string password)
    {
        var browser = Browser();
        await browser.PostApiAsync("/api/auth/password", new { email, password });
        var enrolment = await browser.GetFromJsonAsync<EnrolmentDetails>("/api/auth/enrolment");
        var enrolled = await browser.PostApiAsync("/api/auth/enrolment", new { code = Totp.Code(Totp.SecretFrom(enrolment!.AuthenticatorUri)) });
        enrolled.EnsureSuccessStatusCode();
        return browser;
    }

    /// <summary>A browser signed in as a new operator with no tenant membership at all, authenticator enrolled.</summary>
    public async Task<(HttpClient Browser, string Email)> SignedInOperatorAsync()
    {
        var email = $"operator-{Guid.NewGuid():N}@paddockside.test";
        var password = $"test-{Convert.ToHexString(RandomNumberGenerator.GetBytes(8))}";
        await using (var scope = Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Person>>();
            var created = await users.CreateAsync(new Person { UserName = email, Email = email, EmailConfirmed = true, IsOperator = true }, password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        var browser = Browser();
        (await browser.PostApiAsync("/api/auth/password", new { email, password })).EnsureSuccessStatusCode();
        var enrolment = await browser.GetFromJsonAsync<EnrolmentDetails>("/api/auth/enrolment");
        (await browser.PostApiAsync("/api/auth/enrolment", new { code = Totp.Code(Totp.SecretFrom(enrolment!.AuthenticatorUri)) })).EnsureSuccessStatusCode();
        return (browser, email);
    }

    private sealed record EnrolmentDetails(string AuthenticatorUri);

    private sealed class FakeBreachedList : IBreachedPasswordList
    {
        public Task<bool> ContainsAsync(string password, CancellationToken cancellationToken = default) =>
            Task.FromResult(password == BreachedPassword);
    }
}

/// <summary>Accepts every email and keeps it, with a Postmark-like message id.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(OutboundEmail Email, string MessageId)> _sent = new();

    public bool IsConfigured => true;

    public IReadOnlyList<(OutboundEmail Email, string MessageId)> Sent => _sent.ToList();

    /// <summary>Addresses to reject permanently, as Postmark does for an inactive recipient.</summary>
    public HashSet<string> Inactive { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<EmailSendResult> SendAsync(OutboundEmail email, CancellationToken cancellationToken)
    {
        if (Inactive.Contains(email.ToAddress))
            return Task.FromResult(EmailSendResult.Failed("Postmark 422, code 406: inactive recipient", permanent: true));
        var id = Guid.NewGuid().ToString();
        _sent.Enqueue((email, id));
        return Task.FromResult(EmailSendResult.Sent(id));
    }
}

/// <summary>Accepts every text and keeps it.</summary>
public sealed class FakeSmsSender : ISmsSender
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string To, string Body)> _sent = new();

    public bool IsConfigured => true;

    public IReadOnlyList<(string To, string Body)> Sent => _sent.ToList();

    public Task<SmsSendResult> SendAsync(string to, string body, CancellationToken cancellationToken)
    {
        _sent.Enqueue((to, body));
        return Task.FromResult(SmsSendResult.Sent(Guid.NewGuid().ToString()));
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
