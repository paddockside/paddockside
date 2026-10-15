using System.Net;
using System.Net.Http.Json;

namespace Paddockside.Web.Services;

// Shapes of the Api's JSON. Kept here rather than shared, so the browser app never references server code.
public sealed record NextStep(string Next);

public sealed record EnrolmentDetails(string SharedKey, string AuthenticatorUri, string QrCodePng);

public sealed record RecoveryCodes(IReadOnlyList<string> Codes);

public sealed record SessionInfo(string Email, string TenantName, string Role, bool IsOperator = false, string? SupportUntil = null);

public sealed record HorseSummary(Guid Id, string Name, bool Managed, DateTimeOffset? ManagedSince, int CurrentOwners);

public sealed record HorseDetail(Guid Id, string Name, string? SexAge, string? Pedigree, bool Managed, string? ManagedSince, int CurrentOwners, string? NextKeyDate, string? InboxAddress);

public sealed record StepView(string Label, string State, string? Detail, bool ClientVisible);

public sealed record EventSummary(Guid Id, string Type, string Title, string KeyDate, string Status, List<StepView> Steps, string LastActivity, int Unread = 0);

public sealed record FieldView(string Label, string Value, string? Was);

public sealed record ReplyView(string Author, string Channel, string At, string Body, string? Answer = null);

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
    List<RecipientView> Recipients,
    PromptView? Prompt = null,
    bool CanReplyToSender = false,
    string? Channel = null,
    List<MediaView>? Media = null);

/// <summary>A photo, video or voice note; its file is at api/media/{Id}.</summary>
public sealed record MediaView(Guid Id, string Kind, string ContentType, string FileName, long Length);

public sealed record MediaUploaded(Guid Id, int Files, int Recipients);

public sealed record PromptView(string Kind, string Question, List<string> Answers, string Summary, List<TallyCount> Counts, List<PromptOwnerView> Owners);

public sealed record TallyCount(string Answer, int Count);

public sealed record PromptOwnerView(Guid PartyId, string Name, string? Answer, string? How, string? At);

/// <param name="Kind">Count, YesNo or Choice.</param>
public sealed record PromptRequest(string Kind, string Question, List<string>? Options, int? Max);

public sealed record OwnerOption(Guid Id, string Name);

public sealed record EventPage(EventSummary Event, Guid HorseId, string HorseName, List<ItemView> Items, List<string> StepOptions, List<OwnerOption> Owners,
    Dictionary<string, PromptRequest> StepPrompts, string? Tickets = null, RaceDayView? RaceDay = null);

/// <summary>A race's details for owners on the day: gate, dining, a note.</summary>
public sealed record RaceDayView(string? Gate, string? Dining, string? Note);

public sealed record TicketLine(Guid PartyId, string Name, bool IsHouse, int? Asked, int Given, int? Confirmed);

public sealed record TicketsView(Guid EventId, string EventTitle, Guid HorseId, string HorseName, int? Available, List<TicketLine> Lines,
    int AskedTotal, int GivenTotal, string Summary, string? Question, Guid? QuestionItemId, int ToTell, string? LastConfirmed);

public sealed record TicketsConfirmed(int Told);

public sealed record EventTypeOption(string Code, string Name, string KeyDateLabel, string TitleExample);

public sealed record CreatedEvent(Guid Id);

public sealed record ComposeRequest(string Audience, string Scope, string? Step, string Channel, IReadOnlyList<Guid> NamedParties, string Body, PromptRequest? Prompt = null);

public sealed record Posted(Guid Id, int Recipients);

public sealed record AttachmentView(string Name, string? Dropped);

public sealed record TodayRunner(Guid EventId, Guid HorseId, string Horse, string Type, string Title, string? Time, string? Stage, List<StepView> Steps, List<string> Attention);

public sealed record TodayUndelivered(Guid ItemId, Guid? EventId, Guid HorseId, string Horse, string? Event, string Title, string Sent, string Detail);

public sealed record TodayQueue(int Waiting, string? Oldest);

public sealed record TodayQuiet(Guid Id, string Horse, string Type, string Title, string Detail);

/// <param name="State">"ok", "warn" or "off".</param>
public sealed record TodayHealth(string Label, string State, string Detail);

public sealed record TodayPage(string Date, List<TodayRunner> Today, List<TodayRunner> Tomorrow, List<TodayUndelivered> Undelivered, TodayQueue Queue,
    List<TodayQuiet> Dormant, List<TodayQuiet> Drafts, List<TodayHealth> Health);

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
    List<AttachmentView> Attachments,
    string? Waiting = null,
    List<CandidateView>? Candidates = null,
    string Channel = "Email");

public sealed record HorseItem(Guid Id, string Kind, string At, string? Title, string Body, string? Author, string? Channel, bool CanReplyToSender = false,
    List<HorseReply>? Replies = null);

public sealed record HorseReply(string Author, string At, string Body);

public sealed record RepliedToSender(Guid Id, string To);

public sealed record CandidateView(Guid HorseId, string Horse, Guid? EventId, string? Event, int Score, List<string> Reasons);

public sealed record PlaceOption(Guid HorseId, string Horse, List<EventOption> Events);

public sealed record EventOption(Guid Id, string Title, string KeyDate);

public sealed record StaffTenantOption(Guid Id, string Name, string Role, bool Current);

// ---- Support sessions and the audit log ------------------------------------------------------------------------

public sealed record SupportView(Guid Id, Guid TenantId, string Tenant, string Operator, string Reason, string Status,
    string Requested, string? Decided, string? DecidedBy, string? Until, string? EndedBy, bool Emergency = false);

public sealed record AuditView(Guid Id, string At, string Who, string ActorKind, string Action, string Summary, string? From, string? Device, string? Before, string? After);

public sealed record AuditPage(List<AuditView> Entries, string? Older);

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

public sealed record OwnerHorse(Guid Id, string Name, string? SexAge, string? Pedigree, string? Next, string LastUpdate, int Unread);

public sealed record OwnerUpdate(Guid ItemId, Guid HorseId, string Horse, Guid? EventId, string? Event, string Kind, string? Title, string Summary, string At, bool Unread);

public sealed record OwnerField(string Label, string Value, string? Was = null);

public sealed record OwnerReply(string Author, string At, string Body);

public sealed record OwnerItem(Guid Id, string Kind, string At, string? Title, string Body, string? Author, string? Source,
    List<OwnerField> Fields, string? Corrected, List<OwnerReply> Replies, bool CanReply, bool Unread, bool Mine, OwnerPrompt? Prompt = null,
    List<MediaView>? Media = null);

public sealed record OwnerPrompt(string Kind, string Question, List<string> Answers, string? Mine);

public sealed record OwnerTickets(string Line, bool Confirmed, string? Note);

public sealed record OwnerShare(string Line, string Detail);

public sealed record OwnerEarlier(Guid Id, string Title);

public sealed record OwnerEventPage(EventSummary Event, Guid HorseId, string Horse, List<OwnerItem> Items, string? Heading = null, string? GlanceTitle = null,
    List<OwnerField>? Glance = null, OwnerTickets? Tickets = null, OwnerShare? Share = null, List<OwnerEarlier>? Earlier = null, RaceDayView? RaceDay = null);

public sealed record Contacted(Guid Id);

public sealed record NotificationChoice(string Code, string Label, string Description, bool Important, string Email, string Text);

public sealed record NotificationsPage(List<NotificationChoice> Categories, string? Email, string? Mobile, bool TextsAvailable);

public sealed record SignedIn(string Destination);

/// <summary>A sign-in link redeemed: where to go, or why not (and where it was going).</summary>
public sealed record LinkOutcome(string? Destination, string? Error, string? ReturnPath);

/// <summary>Held is null unless the signed-in person is a tenant admin.</summary>
public sealed record InboundCounts(int Pending, int Placed, int Ignored, int? Held, int Waiting = 0, int Suggested = 0, string? Ages = null, string? MedianToPlace = null);

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

    public Task<ApiResult<List<EventTypeOption>>> EventTypesAsync() => GetAsync<List<EventTypeOption>>("api/event-types");

    public Task<ApiResult<CreatedEvent>> CreateEventAsync(Guid horseId, string type, string title, string? keyDate, bool open) =>
        PostAsync<CreatedEvent>($"api/horses/{horseId}/events", new { type, title, keyDate, open });

    /// <summary>Photos, video or voice notes onto an event: "Owners" to share them, "Internal" for staff only.</summary>
    public async Task<ApiResult<MediaUploaded>> UploadMediaAsync(Guid eventId, string scope, string caption,
        IEnumerable<Microsoft.AspNetCore.Components.Forms.IBrowserFile> files, long maxBytes)
    {
        using var form = new MultipartFormDataContent { { new StringContent(scope), "scope" }, { new StringContent(caption), "caption" } };
        foreach (var file in files)
        {
            var content = new StreamContent(file.OpenReadStream(maxBytes));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType);
            form.Add(content, "files", file.Name);
        }

        return await SendContentAsync<MediaUploaded>(HttpMethod.Post, $"api/events/{eventId}/media", form);
    }

    public Task<ApiResult<object>> SetRaceDayAsync(Guid eventId, RaceDayView details) =>
        PutAsync<object>($"api/events/{eventId}/race-day", new { gate = details.Gate, dining = details.Dining, note = details.Note });

    public Task<ApiResult<object>> OpenEventAsync(Guid eventId) => PostAsync<object>($"api/events/{eventId}/open", new { });

    public Task<ApiResult<TicketsView>> TicketsAsync(Guid eventId) => GetAsync<TicketsView>($"api/events/{eventId}/tickets");

    public Task<ApiResult<TicketsView>> SaveTicketsAsync(Guid eventId, int? available, IEnumerable<(Guid PartyId, int Given)> lines) =>
        PutAsync<TicketsView>($"api/events/{eventId}/tickets", new { available, lines = lines.Select(l => new { partyId = l.PartyId, given = l.Given }) });

    public Task<ApiResult<TicketsConfirmed>> ConfirmTicketsAsync(Guid eventId, string? note) =>
        PostAsync<TicketsConfirmed>($"api/events/{eventId}/tickets/confirm", new { note });

    public Task<ApiResult<object>> RecordAnswerAsync(Guid eventId, Guid itemId, Guid partyId, string value) =>
        PostAsync<object>($"api/events/{eventId}/items/{itemId}/answers", new { partyId, value });

    public Task<ApiResult<List<InboundView>>> InboundAsync(string state) => GetAsync<List<InboundView>>($"api/inbound?state={Uri.EscapeDataString(state)}");

    /// <summary>Mail the matcher suggested for this event, waiting for someone to confirm it.</summary>
    public Task<ApiResult<List<InboundView>>> SuggestedForEventAsync(Guid eventId) => GetAsync<List<InboundView>>($"api/inbound?state=Waiting&eventId={eventId}");

    /// <summary>"View as an owner": the event exactly as that owner sees it in the portal.</summary>
    public Task<ApiResult<OwnerEventPage>> EventAsOwnerAsync(Guid eventId, Guid partyId) => GetAsync<OwnerEventPage>($"api/events/{eventId}/as-owner/{partyId}");

    public Task<ApiResult<InboundCounts>> InboundCountsAsync() => GetAsync<InboundCounts>("api/inbound/counts");

    public Task<ApiResult<TodayPage>> TodayAsync() => GetAsync<TodayPage>("api/today");

    public Task<ApiResult<RepliedToSender>> ReplyToSenderAsync(Guid itemId, string body) =>
        PostAsync<RepliedToSender>($"api/items/{itemId}/reply-to-sender", new { body });

    public Task<ApiResult<List<HorseItem>>> OnTheHorseAsync(Guid horseId) => GetAsync<List<HorseItem>>($"api/horses/{horseId}/unplaced");

    public Task<ApiResult<List<PlaceOption>>> InboundPlacesAsync() => GetAsync<List<PlaceOption>>("api/inbound/places");

    public Task<ApiResult<object>> PlaceInboundAsync(Guid id, Guid horseId, Guid? eventId) =>
        PostAsync<object>($"api/inbound/{id}/place", new { horseId, eventId });

    public Task<ApiResult<object>> DismissInboundAsync(Guid id, bool spam) => PostAsync<object>($"api/inbound/{id}/{(spam ? "spam" : "ignore")}", new { });

    public Task<ApiResult<List<StaffTenantOption>>> StaffTenantsAsync() => GetAsync<List<StaffTenantOption>>("api/auth/tenants");

    public Task<ApiResult<object>> SwitchStaffTenantAsync(Guid tenantId) => PostAsync<object>("api/auth/switch", new { tenantId });

    // ---- Support sessions and the audit log ----------------------------------------------------------------------

    public Task<ApiResult<List<SupportView>>> TenantSupportAsync() => GetAsync<List<SupportView>>("api/support");

    public Task<ApiResult<object>> AnswerSupportAsync(Guid id, string answer) => PostAsync<object>($"api/support/{id}/{answer}", new { });

    public Task<ApiResult<List<SupportView>>> MySupportSessionsAsync() => GetAsync<List<SupportView>>("api/ops/support");

    public Task<ApiResult<SupportView>> AskForSupportAsync(Guid tenantId, string reason, int hours, bool emergency = false) =>
        PostAsync<SupportView>($"api/ops/tenants/{tenantId}/support", new { reason, hours, emergency });

    public Task<ApiResult<List<SupportView>>> PendingEmergenciesAsync() => GetAsync<List<SupportView>>("api/ops/support/emergency");

    public Task<ApiResult<object>> SecondEmergencyAsync(Guid tenantId, Guid id) => PostAsync<object>($"api/ops/support/{tenantId}/{id}/second", new { });

    public Task<ApiResult<object>> EnterSupportAsync(Guid tenantId, Guid id) => PostAsync<object>($"api/ops/support/{tenantId}/{id}/enter", new { });

    public Task<ApiResult<object>> EndSupportAsync(Guid tenantId, Guid id) => PostAsync<object>($"api/ops/support/{tenantId}/{id}/end", new { });

    public Task<ApiResult<object>> LeaveSupportAsync() => PostAsync<object>("api/ops/support/exit", new { });

    public Task<ApiResult<AuditPage>> AuditAsync(string? q, string? action, string? before) =>
        GetAsync<AuditPage>($"api/audit?q={Uri.EscapeDataString(q ?? "")}&action={Uri.EscapeDataString(action ?? "")}{(before is null ? "" : $"&before={Uri.EscapeDataString(before)}")}");

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

    public Task<ApiResult<object>> ForgotPasswordAsync(string email) => PostAsync<object>("api/auth/forgot-password", new { email });

    public Task<ApiResult<object>> CheckResetLinkAsync(string token) => PostAsync<object>("api/auth/reset-password/check", new { token });

    public Task<ApiResult<object>> ResetPasswordAsync(string token, string password) => PostAsync<object>("api/auth/reset-password", new { token, password });

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

    public Task<ApiResult<OwnerPrompt>> AnswerAsync(Guid itemId, string value) => PostAsync<OwnerPrompt>($"api/my/items/{itemId}/answer", new { value });

    public Task<ApiResult<object>> MarkEventSeenAsync(Guid eventId) => PostAsync<object>($"api/my/events/{eventId}/seen", new { });

    public Task<ApiResult<Contacted>> ContactAboutEventAsync(Guid eventId, string body) => PostAsync<Contacted>($"api/my/events/{eventId}/contact", new { body });

    public Task<ApiResult<NotificationsPage>> MyNotificationsAsync() => GetAsync<NotificationsPage>("api/my/notifications");

    public Task<ApiResult<object>> SetNotificationAsync(string category, string channel, bool enabled) =>
        PutAsync<object>("api/my/notifications", new { category, channel, enabled });

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

    private Task<ApiResult<T>> PostAsync<T>(string path, object body) => SendAsync<T>(HttpMethod.Post, path, body);

    private Task<ApiResult<T>> PutAsync<T>(string path, object body) => SendAsync<T>(HttpMethod.Put, path, body);

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string path, object body)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
            request.Headers.Add(RequestHeader, "1");
            return await ReadAsync<T>(await http.SendAsync(request));
        }
        catch (HttpRequestException)
        {
            return Unreachable<T>();
        }
    }

    /// <summary>As SendAsync, with a body that isn't JSON: an upload form.</summary>
    private async Task<ApiResult<T>> SendContentAsync<T>(HttpMethod method, string path, HttpContent content)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
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
