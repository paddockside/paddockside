using System.Net;
using System.Net.Http.Json;

namespace Paddockside.Web.Services;

// Shapes of the Api's JSON. Kept here rather than shared, so the browser app never references server code.
public sealed record NextStep(string Next);

public sealed record EnrolmentDetails(string SharedKey, string AuthenticatorUri, string QrCodePng);

public sealed record RecoveryCodes(IReadOnlyList<string> Codes);

public sealed record SessionInfo(string Email, string TenantName, string Role);

public sealed record HorseSummary(Guid Id, string Name, bool Managed, DateTimeOffset? ManagedSince, int CurrentOwners);

/// <summary>The outcome of a call: a value, or a message that can be shown to the person as it is.</summary>
public sealed record ApiResult<T>(T? Value, string? Error, HttpStatusCode Status)
{
    public bool Succeeded => Error is null;
}

/// <summary>Calls the Api on the same origin, so the session cookie travels with every request.</summary>
public sealed class PaddocksideApi(HttpClient http)
{
    /// <summary>Required on every state-changing call; see RequireApiRequestHeader in the Api.</summary>
    public const string RequestHeader = "X-Paddockside-Request";

    public Task<ApiResult<NextStep>> SubmitPasswordAsync(string email, string password) =>
        PostAsync<NextStep>("api/auth/password", new { email, password });

    public Task<ApiResult<object>> SubmitAuthenticatorCodeAsync(string code) => PostAsync<object>("api/auth/totp", new { code });

    public Task<ApiResult<object>> SubmitRecoveryCodeAsync(string code) => PostAsync<object>("api/auth/recovery-code", new { code });

    public Task<ApiResult<EnrolmentDetails>> StartEnrolmentAsync() => GetAsync<EnrolmentDetails>("api/auth/enrolment");

    public Task<ApiResult<RecoveryCodes>> CompleteEnrolmentAsync(string code) => PostAsync<RecoveryCodes>("api/auth/enrolment", new { code });

    public Task<ApiResult<object>> SignOutAsync() => PostAsync<object>("api/auth/sign-out", new { });

    public Task<ApiResult<SessionInfo>> MeAsync() => GetAsync<SessionInfo>("api/auth/me");

    public Task<ApiResult<List<HorseSummary>>> HorsesAsync() => GetAsync<List<HorseSummary>>("api/horses");

    private async Task<ApiResult<T>> GetAsync<T>(string path)
    {
        try
        {
            return await ReadAsync<T>(await http.GetAsync(path));
        }
        catch (HttpRequestException)
        {
            return Unreachable<T>();
        }
    }

    private async Task<ApiResult<T>> PostAsync<T>(string path, object body)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            request.Headers.Add(RequestHeader, "1");
            return await ReadAsync<T>(await http.SendAsync(request));
        }
        catch (HttpRequestException)
        {
            return Unreachable<T>();
        }
    }

    private static async Task<ApiResult<T>> ReadAsync<T>(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            var value = response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<T>();
            return new ApiResult<T>(value, null, response.StatusCode);
        }

        var problem = response.Content.Headers.ContentType?.MediaType == "application/problem+json"
            ? await response.Content.ReadFromJsonAsync<Problem>()
            : null;
        var message = problem?.Title ?? response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => "Too many attempts. Wait a minute, then try again.",
            HttpStatusCode.Unauthorized => "Please sign in.",
            HttpStatusCode.Forbidden => "Your account does not have access to this.",
            _ => "Something went wrong. Try again in a moment.",
        };
        return new ApiResult<T>(default, message, response.StatusCode);
    }

    private static ApiResult<T> Unreachable<T>() =>
        new(default, "We could not reach Paddockside. Check your connection and try again.", HttpStatusCode.ServiceUnavailable);

    private sealed record Problem(string? Title);
}
