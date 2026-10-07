using System.Net;
using System.Net.Http.Json;

namespace Paddockside.Web.Services;

// Shapes of the Api's JSON. Kept here rather than shared, so the browser app never references server code.
public sealed record NextStep(string Next);

public sealed record EnrolmentDetails(string SharedKey, string AuthenticatorUri, string QrCodePng);

public sealed record RecoveryCodes(IReadOnlyList<string> Codes);

public sealed record SessionInfo(string Email, string TenantName, string Role, bool IsOperator = false);

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

// ---- Operator console ----------------------------------------------------------------------------------------

public sealed record OperatorMe(string Email);

public sealed record TenantRow(
    Guid Id, string Name, string Slug, string Created, int Horses, int Staff, int OwnersSignedIn, int InvitationsWaiting,
    int EmailsSent, int EmailsDelivered, int EmailsBounced, int EmailsWaiting, int InboundPending, int InboundHeld);

public sealed record OperatorRow(string Email, bool IsYou);

public sealed record OperatorsPage(List<OperatorRow> Operators, List<string> Invited);

public sealed record TenantCreated(Guid Id, string Slug, bool InvitationSent, string? Problem);

// ---- Members (tenant admins) and joining ------------------------------------------------------------------------

public sealed record MemberView(Guid Id, string Email, string Role, string RoleLabel, string Status, string? Since, bool IsYou);

public sealed record InvitationView(Guid Id, string Email, string Role, string RoleLabel, string InvitedBy, string Sent, string Expires);

public sealed record RoleOption(string Role, string Label, string Description);

public sealed record MembersPage(List<MemberView> Members, List<InvitationView> Invitations, List<RoleOption> Roles);

public sealed record Invited(Guid Id, bool Sent, string? Problem);

public sealed record JoinDetails(string TenantName, string Email, string Role, string RoleDescription, string InvitedBy, string Needs);

// ---- Owner portal --------------------------------------------------------------------------------------------

public sealed record OwnerTenant(Guid Id, string Name, bool Current);

public sealed record OwnerSession(string Name, string? Email, string? Mobile, string TenantName, string? TenantLogoUrl, List<OwnerTenant> Tenants);

public sealed record OwnerHorse(Guid Id, string Name, string? SexAge, string? Pedigree, string? Next, string LastUpdate);

public sealed record OwnerUpdate(Guid ItemId, Guid HorseId, string Horse, Guid? EventId, string? Event, string Kind, string? Title, string Summary, string At);

public sealed record OwnerField(string Label, string Value);

public sealed record OwnerReply(string Author, string At, string Body);

public sealed record OwnerItem(Guid Id, string Kind, string At, string? Title, string Body, string? Author, string? Source,
    List<OwnerField> Fields, string? Corrected, List<OwnerReply> Replies, bool CanReply);

public sealed record OwnerEventPage(EventSummary Event, Guid HorseId, string Horse, List<OwnerItem> Items);

public sealed record SignedIn(string Destination);

/// <summary>A sign-in link redeemed: where to go, or why not (and where it was going).</summary>
public sealed record LinkOutcome(string? Destination, string? Error, string? ReturnPath);

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

    // ---- Operator console ------------------------------------------------------------------------------------------

    public Task<ApiResult<OperatorMe>> OperatorMeAsync() => GetAsync<OperatorMe>("api/ops/me");

    public Task<ApiResult<List<TenantRow>>> OperatorTenantsAsync() => GetAsync<List<TenantRow>>("api/ops/tenants");

    public Task<ApiResult<TenantCreated>> CreateTenantAsync(string name, string? slug, string adminEmail) =>
        PostAsync<TenantCreated>("api/ops/tenants", new { name, slug, adminEmail });

    public Task<ApiResult<OperatorsPage>> OperatorsAsync() => GetAsync<OperatorsPage>("api/ops/operators");

    public Task<ApiResult<TenantCreated>> InviteOperatorAsync(string email) => PostAsync<TenantCreated>("api/ops/operators/invitations", new { email });

    // ---- Members and joining -----------------------------------------------------------------------------------

    public Task<ApiResult<MembersPage>> MembersAsync() => GetAsync<MembersPage>("api/members");

    public Task<ApiResult<Invited>> InviteAsync(string email, string role) => PostAsync<Invited>("api/members/invitations", new { email, role });

    public Task<ApiResult<object>> RevokeInvitationAsync(Guid id) => PostAsync<object>($"api/members/invitations/{id}/revoke", new { });

    public Task<ApiResult<object>> ChangeRoleAsync(Guid membershipId, string role) => PostAsync<object>($"api/members/{membershipId}/role", new { role });

    public Task<ApiResult<object>> SuspendAsync(Guid membershipId) => PostAsync<object>($"api/members/{membershipId}/suspend", new { });

    public Task<ApiResult<object>> ReactivateAsync(Guid membershipId) => PostAsync<object>($"api/members/{membershipId}/reactivate", new { });

    public Task<ApiResult<JoinDetails>> InspectInvitationAsync(string token) => PostAsync<JoinDetails>("api/join/inspect", new { token });

    public Task<ApiResult<NextStep>> AcceptInvitationAsync(string token, string password) => PostAsync<NextStep>("api/join/accept", new { token, password });

    // ---- Owner sign-in and portal --------------------------------------------------------------------------------

    public Task<ApiResult<object>> RequestEmailLinkAsync(string email, string? returnPath) =>
        PostAsync<object>("api/client-auth/email", new { email, returnPath });

    public Task<ApiResult<object>> RequestSmsCodeAsync(string mobile, string? returnPath) =>
        PostAsync<object>("api/client-auth/sms", new { mobile, returnPath });

    public Task<ApiResult<SignedIn>> SubmitClientCodeAsync(string code) => PostAsync<SignedIn>("api/client-auth/code", new { code });

    /// <summary>Redeems a link; a used or expired one says why and where it was going.</summary>
    public async Task<LinkOutcome> RedeemLinkAsync(string token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/client-auth/link") { Content = JsonContent.Create(new { token }) };
            request.Headers.Add(RequestHeader, "1");
            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode) return new LinkOutcome((await response.Content.ReadFromJsonAsync<SignedIn>())!.Destination, null, null);
            var problem = await response.Content.ReadFromJsonAsync<LinkProblem>();
            return new LinkOutcome(null, problem?.Title ?? "That link didn't work. Ask for a new one below.", problem?.ReturnPath);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            return new LinkOutcome(null, "We could not reach Paddockside. Check your connection and try again.", null);
        }
    }

    public Task<ApiResult<OwnerSession>> OwnerSessionAsync() => GetAsync<OwnerSession>("api/my/session");

    public Task<ApiResult<object>> SwitchTenantAsync(Guid tenantId) => PostAsync<object>("api/my/tenant", new { tenantId });

    public Task<ApiResult<List<OwnerHorse>>> MyHorsesAsync() => GetAsync<List<OwnerHorse>>("api/my/horses");

    public Task<ApiResult<List<EventSummary>>> MyHorseEventsAsync(Guid horseId) => GetAsync<List<EventSummary>>($"api/my/horses/{horseId}/events");

    public Task<ApiResult<List<OwnerUpdate>>> MyUpdatesAsync() => GetAsync<List<OwnerUpdate>>("api/my/updates");

    public Task<ApiResult<OwnerEventPage>> MyEventAsync(Guid id) => GetAsync<OwnerEventPage>($"api/my/events/{id}");

    public Task<ApiResult<OwnerReply>> ReplyAsync(Guid itemId, string body) => PostAsync<OwnerReply>($"api/my/items/{itemId}/replies", new { body });

    private sealed record LinkProblem(string? Title, string? ReturnPath);

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
            // 202 and 204 carry no body.
            var empty = response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Accepted || response.Content.Headers.ContentLength == 0;
            var value = empty ? default : await response.Content.ReadFromJsonAsync<T>();
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
