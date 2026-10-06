using System.Net;
using System.Net.Http.Json;

namespace Paddockside.Web.Services;

// Shapes of the Api's JSON. Kept here rather than shared, so the browser app never references server code.
public sealed record NextStep(string Next);

public sealed record EnrolmentDetails(string SharedKey, string AuthenticatorUri, string QrCodePng);

public sealed record RecoveryCodes(IReadOnlyList<string> Codes);

public sealed record SessionInfo(string Email, string TenantName, string Role);

public sealed record HorseSummary(Guid Id, string Name, bool Managed, DateTimeOffset? ManagedSince, int CurrentOwners);

public sealed record HorseDetail(Guid Id, string Name, string? SexAge, string? Pedigree, bool Managed, string? ManagedSince, int CurrentOwners, string? NextKeyDate, string? InboxAddress);

public sealed record StepView(string Label, string State, string? Detail, bool ClientVisible);

public sealed record EventSummary(Guid Id, string Type, string Title, string KeyDate, string Status, List<StepView> Steps, string LastActivity);

public sealed record FieldView(string Label, string Value, string? Was);

public sealed record ReplyView(string Author, string Channel, string At, string Body);

public sealed record RecipientView(string Name, string Channel, string Status, string? Note);

public sealed record ItemView(
    Guid Id,
    string Kind,
    string Scope,
    string At,
    string? Title,
    string Body,
    string? Author,
    string? Step,
    string? Source,
    List<FieldView> Fields,
    string? Correction,
    string? Audience,
    List<ReplyView> Replies,
    List<RecipientView> Recipients);

public sealed record OwnerOption(Guid Id, string Name);

public sealed record EventPage(EventSummary Event, Guid HorseId, string HorseName, List<ItemView> Items, List<string> StepOptions, List<OwnerOption> Owners);

public sealed record ComposeRequest(string Audience, string Scope, string? Step, string Channel, IReadOnlyList<Guid> NamedParties, string Body);

public sealed record Posted(Guid Id, int Recipients);

public sealed record AttachmentView(string Name, string? Dropped);

public sealed record InboundView(
    Guid Id,
    string State,
    string Received,
    string From,
    string To,
    string? Subject,
    string? Body,
    string? Reason,
    int? Tier,
    Guid? HorseId,
    string? Horse,
    Guid? EventId,
    string? Event,
    List<AttachmentView> Attachments);

/// <summary>Held is null unless the signed-in person is a tenant admin.</summary>
public sealed record InboundCounts(int Pending, int Placed, int Ignored, int? Held);

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

    public Task<ApiResult<HorseDetail>> HorseAsync(Guid id) => GetAsync<HorseDetail>($"api/horses/{id}");

    public Task<ApiResult<List<EventSummary>>> HorseEventsAsync(Guid id) => GetAsync<List<EventSummary>>($"api/horses/{id}/events");

    public Task<ApiResult<EventPage>> EventAsync(Guid id) => GetAsync<EventPage>($"api/events/{id}");

    public Task<ApiResult<Posted>> PostToEventAsync(Guid id, ComposeRequest request) => PostAsync<Posted>($"api/events/{id}/items", request);

    public Task<ApiResult<List<InboundView>>> InboundAsync(string state) => GetAsync<List<InboundView>>($"api/inbound?state={Uri.EscapeDataString(state)}");

    public Task<ApiResult<InboundCounts>> InboundCountsAsync() => GetAsync<InboundCounts>("api/inbound/counts");

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
            HttpStatusCode.NotFound => "That could not be found. It may have been removed, or the link is wrong.",
            _ => "Something went wrong. Try again in a moment.",
        };
        return new ApiResult<T>(default, message, response.StatusCode);
    }

    private static ApiResult<T> Unreachable<T>() =>
        new(default, "We could not reach Paddockside. Check your connection and try again.", HttpStatusCode.ServiceUnavailable);

    private sealed record Problem(string? Title);
}
