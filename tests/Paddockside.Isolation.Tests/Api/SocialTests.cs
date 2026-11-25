using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Social events (David, 22 Nov 2026): invited by list, answered with the invitation's buttons, counted against the
/// places with a waiting list, a later round skipping anyone already invited, and on the calendar. Shares the
/// sign-in tests' seed: Ann and Bob, owners of Bel Esprit.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record SocialMade(Guid Id);

    private sealed record Attendee(Guid PartyId, string Name, string Status, int Places, string? Note, string? AnsweredBy);

    private sealed record SocialHead(string Title, int? Places, int Coming, int Invited, int Waiting);

    private sealed record SocialPage(SocialHead Event, List<Attendee> Attendees);

    [Fact]
    public async Task A_social_event_is_invited_by_list_answered_with_buttons_and_counted_against_its_places()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var day = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var id = (await (await staff.PostApiAsync("/api/social", new { title = "Christmas Lunch", kind = "Lunch", date = day, time = "12:30", endDate = (string?)null,
            venue = "The Sydney Club", places = 3, notes = (string?)null })).Content.ReadFromJsonAsync<SocialMade>())!.Id;

        var list = (await (await staff.PostApiAsync("/api/client-lists", new { name = "Sydney Social" })).Content.ReadFromJsonAsync<ListCreated>())!.Id;
        await staff.PostApiAsync($"/api/client-lists/{list}/add", new { clientIds = new[] { seed.AnnId, seed.BobId } });
        var invitation = new { subject = "Christmas Lunch", body = "Join us for lunch.", listIds = new[] { list }, statuses = Array.Empty<string>(),
            clientIds = Array.Empty<Guid>(), horseId = (Guid?)null, signature = (string?)null, attachmentIds = Array.Empty<Guid>() };
        Assert.Equal(HttpStatusCode.Created, (await staff.PostApiAsync($"/api/social/{id}/invite", invitation)).StatusCode);
        var page = (await staff.GetFromJsonAsync<SocialPage>($"/api/social/{id}"))!;
        Assert.Equal(2, page.Event.Invited);

        // Ann's invitation carries her own buttons; opening one asks, and changes nothing until she sends.
        await DispatchAsync();
        var email = Api.Emails.Sent.Last(e => e.Email.ToAddress == seed.AnnEmail && e.Email.Subject == "Christmas Lunch").Email;
        Assert.Contains("I&#39;ll come", email.HtmlBody);
        var link = new Uri(Regex.Match(email.TextBody, @"I'll come: (\S+)").Groups[1].Value);
        using var anyone = Api.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        Assert.Contains("How many places", await anyone.GetStringAsync(link.PathAndQuery));
        Assert.Equal("Invited", (await staff.GetFromJsonAsync<SocialPage>($"/api/social/{id}"))!.Attendees.Single(a => a.PartyId == seed.AnnId).Status);

        async Task<string> Reply(Uri to, string answer, int places, string note) =>
            await (await anyone.PostAsync(to.AbsolutePath, new FormUrlEncodedContent(new Dictionary<string, string> { ["answer"] = answer, ["places"] = $"{places}", ["note"] = note })))
                .Content.ReadAsStringAsync();
        Assert.Contains("down for 2 places", await Reply(link, "Coming", 2, "Vegetarian"));

        // Bob wants two as well, but only one is left: the waiting list.
        var bobs = Api.Emails.Sent.Last(e => e.Email.ToName == "Bob Owner" && e.Email.Subject == "Christmas Lunch").Email;
        Assert.Contains("waiting list", await Reply(new Uri(Regex.Match(bobs.TextBody, @"I'll come: (\S+)").Groups[1].Value), "Coming", 2, ""));
        page = (await staff.GetFromJsonAsync<SocialPage>($"/api/social/{id}"))!;
        Assert.Equal((2, 2), (page.Event.Coming, page.Event.Waiting));
        var ann = page.Attendees.Single(a => a.PartyId == seed.AnnId);
        Assert.Equal(("Coming", 2, "Vegetarian", "them"), (ann.Status, ann.Places, ann.Note, ann.AnsweredBy));

        // A second round to people picked: Ann is already on the list, so she's left out of it.
        var second = new { invitation.subject, invitation.body, listIds = Array.Empty<Guid>(), statuses = Array.Empty<string>(), clientIds = new[] { seed.AnnId },
            horseId = (Guid?)null, signature = (string?)null, attachmentIds = Array.Empty<Guid>() };
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync($"/api/social/{id}/invite", second)).StatusCode);

        // Staff take Bob off the waiting list; the calendar shows the lunch with its count.
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PutApiAsync($"/api/social/{id}/attendees/{seed.BobId}", new { status = "Coming", places = 1, note = "" })).StatusCode);
        var calendar = (await staff.GetFromJsonAsync<List<CalendarLine>>($"/api/calendar?from={day}&to={day}"))!;
        var lunch = Assert.Single(calendar, e => e.Kind == "Social");
        Assert.Equal(("Christmas Lunch", "The Sydney Club · 3 of 3 places"), (lunch.Title, lunch.Detail));

        // A viewer reads but can't make one; another business doesn't see it.
        using var viewer = await Api.SignedInAsync(seed.TenantId, MemberRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostApiAsync("/api/social", new { title = "Golf", kind = "Golf", date = day })).StatusCode);
        using var otherStaff = await Api.SignedInAsync(db.A.TenantId, MemberRole.Coordinator);
        Assert.Equal(HttpStatusCode.NotFound, (await otherStaff.GetAsync($"/api/social/{id}")).StatusCode);
    }

    private sealed record UnsentHead(int Invited, int Unsent);

    private sealed record UnsentLine(Guid PartyId, string Status, string StatusLabel, bool Unsent);

    private sealed record UnsentPage(UnsentHead Event, List<UnsentLine> Attendees);

    [Fact]
    public async Task People_added_by_hand_are_not_invited_until_an_invitation_goes_and_then_only_once()
    {
        // David, 25 Nov 2026: people were put on with Add people, shown as invited though nothing had been sent, and
        // then couldn't be invited because they counted as already on the list.
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var day = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var id = (await (await staff.PostApiAsync("/api/social", new { title = "Golf Day", kind = "Golf", date = day })).Content.ReadFromJsonAsync<SocialMade>())!.Id;
        Assert.Equal(HttpStatusCode.OK, (await staff.PostApiAsync($"/api/social/{id}/attendees", new { clientIds = new[] { seed.AnnId, seed.BobId } })).StatusCode);

        var page = (await staff.GetFromJsonAsync<UnsentPage>($"/api/social/{id}"))!;
        Assert.Equal((0, 2), (page.Event.Invited, page.Event.Unsent));
        Assert.All(page.Attendees, a => Assert.Equal(("Not invited yet", true), (a.StatusLabel, a.Unsent)));

        // Bob rings to say yes: he has answered, so an invitation to both goes to Ann only.
        await staff.PutApiAsync($"/api/social/{id}/attendees/{seed.BobId}", new { status = "Coming", places = 1, note = "" });
        var both = new { listIds = Array.Empty<Guid>(), statuses = Array.Empty<string>(), clientIds = new[] { seed.AnnId, seed.BobId } };
        Assert.Equal([seed.AnnId], (await staff.GetFromJsonAsync<InviteDraft>($"/api/social/{id}/invitation"))!.Going.Select(g => g.PartyId));
        var invitation = new { subject = "Golf Day", body = "Join us.", both.listIds, both.statuses, both.clientIds };
        Assert.Equal(HttpStatusCode.Created, (await staff.PostApiAsync($"/api/social/{id}/invite", invitation)).StatusCode);
        await DispatchAsync();
        Assert.Single(Api.Emails.Sent, e => e.Email.Subject == "Golf Day" && e.Email.ToAddress == seed.AnnEmail);
        Assert.DoesNotContain(Api.Emails.Sent, e => e.Email.Subject == "Golf Day" && e.Email.ToName == "Bob Owner");

        // Still one line each, Ann's now truly invited; a second round has nobody left.
        page = (await staff.GetFromJsonAsync<UnsentPage>($"/api/social/{id}"))!;
        Assert.Equal(2, page.Attendees.Count);
        Assert.Equal((1, 0), (page.Event.Invited, page.Event.Unsent));
        Assert.Equal("Invited", page.Attendees.Single(a => a.PartyId == seed.AnnId).StatusLabel);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync($"/api/social/{id}/invite", invitation)).StatusCode);

        // Adding someone already on the list still doesn't put them on twice.
        await staff.PostApiAsync($"/api/social/{id}/attendees", new { clientIds = new[] { seed.AnnId } });
        Assert.Equal(2, (await staff.GetFromJsonAsync<UnsentPage>($"/api/social/{id}"))!.Attendees.Count);
    }

    private sealed record GuestLine(Guid PartyId, string Name, string Email);

    private sealed record NotReached(string Name, string Why);

    private sealed record InviteDraft(string Subject, string Body, string Signature, List<GuestLine> Going, List<NotReached> LeftOut, string? Problem);

    private sealed record AddedPeople(int Count, int Already);

    private sealed record Line(Guid PartyId, string Status, bool Unsent, string? CantEmail, string? Delivery);

    private sealed record ListPage(List<Line> Attendees);

    private sealed record InviteHtml(string Html);

    [Fact]
    public async Task A_list_is_added_checked_sent_and_topped_up_without_anyone_being_invited_twice()
    {
        // David, 25 Nov 2026: add clients or a client list, then Send invitations shows what's going and sends it; later,
        // add more, and the same list again doesn't send anyone a second one.
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var day = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var title = $"Stable Visit {Guid.NewGuid():N}"[..24];
        var id = (await (await staff.PostApiAsync("/api/social", new { title, kind = "StableVisit", date = day, time = "10:00", venue = "Randwick" })).Content.ReadFromJsonAsync<SocialMade>())!.Id;
        var list = (await (await staff.PostApiAsync("/api/client-lists", new { name = $"Visit {title}" })).Content.ReadFromJsonAsync<ListCreated>())!.Id;
        await staff.PostApiAsync($"/api/client-lists/{list}/add", new { clientIds = new[] { seed.AnnId } });

        async Task<AddedPeople> Add(object who) => (await (await staff.PostApiAsync($"/api/social/{id}/attendees", who)).Content.ReadFromJsonAsync<AddedPeople>())!;
        async Task<InviteDraft> Draft() => (await staff.GetFromJsonAsync<InviteDraft>($"/api/social/{id}/invitation"))!;
        async Task<List<Line>> Lines() => (await staff.GetFromJsonAsync<ListPage>($"/api/social/{id}"))!.Attendees;
        Task<HttpResponseMessage> Send(InviteDraft d) => staff.PostApiAsync($"/api/social/{id}/invite", new { subject = d.Subject, body = d.Body, signature = d.Signature, clientIds = d.Going.Select(g => g.PartyId) });
        int Emails(string to) => Api.Emails.Sent.Count(e => e.Email.Subject.Contains(title) && e.Email.ToAddress == to);

        // The list goes on; nobody is emailed by that.
        Assert.Equal(new AddedPeople(1, 0), await Add(new { listIds = new[] { list } }));
        await DispatchAsync();
        Assert.Equal(0, Emails(seed.AnnEmail));

        // What Send invitations shows: who, the words made from the event, and the email itself.
        var draft = await Draft();
        Assert.Null(draft.Problem);
        Assert.Equal([seed.AnnEmail], draft.Going.Select(g => g.Email));
        Assert.Equal($"Invitation: {title}", draft.Subject);
        Assert.Contains(title, draft.Body);
        Assert.Contains(seed.TenantName, draft.Signature);
        var html = (await (await staff.PostApiAsync($"/api/social/{id}/invitation/sample", new { subject = draft.Subject, body = "Come and see her <now>.", signature = "" })).Content.ReadFromJsonAsync<InviteHtml>())!.Html;
        Assert.Contains("Hi Ann,", html);
        Assert.Contains("Come and see her &lt;now&gt;.", html);
        Assert.Contains("I&#39;ll come", html);
        Assert.Contains("/email-preview", html);

        Assert.Equal(HttpStatusCode.Created, (await Send(draft)).StatusCode);
        await DispatchAsync();
        Assert.Equal(1, Emails(seed.AnnEmail));
        var ann = Assert.Single(await Lines());
        Assert.Equal((false, "Sent"), (ann.Unsent, ann.Delivery));

        // The same list, now with Bob on it: only Bob is new, and only Bob is sent it, with the same words ready.
        await staff.PostApiAsync($"/api/client-lists/{list}/add", new { clientIds = new[] { seed.BobId } });
        Assert.Equal(new AddedPeople(1, 1), await Add(new { listIds = new[] { list }, clientIds = new[] { seed.AnnId } }));
        Assert.DoesNotContain((await Draft()).Going, g => g.PartyId == seed.AnnId);
        Assert.Equal(HttpStatusCode.Created, (await Send(await Draft())).StatusCode);
        await DispatchAsync();
        Assert.Equal(1, Emails(seed.AnnEmail));
        Assert.Single(Api.Emails.Sent, e => e.Email.Subject.Contains(title) && e.Email.ToName == "Bob Owner");
        Assert.Empty((await Draft()).Going);

        // Nothing to add, not their business's list, or a viewer.
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync($"/api/social/{id}/attendees", new { listIds = Array.Empty<Guid>() })).StatusCode);
        using var otherStaff = await Api.SignedInAsync(db.A.TenantId, MemberRole.Coordinator);
        Assert.Equal(HttpStatusCode.NotFound, (await otherStaff.GetAsync($"/api/social/{id}/invitation")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherStaff.PostApiAsync($"/api/social/{id}/attendees", new { listIds = new[] { list } })).StatusCode);
        using var viewer = await Api.SignedInAsync(seed.TenantId, MemberRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/social/{id}/invitation")).StatusCode);
    }

    [Fact]
    public async Task Someone_an_invitation_could_not_go_to_is_shown_as_not_invited_and_why()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var day = DateTime.Today.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var id = (await (await staff.PostApiAsync("/api/social", new { title = "Tour", kind = "Tour", date = day })).Content.ReadFromJsonAsync<SocialMade>())!.Id;
        await staff.PostApiAsync($"/api/social/{id}/attendees", new { clientIds = new[] { seed.AnnId, seed.BobId } });

        // Bob unsubscribes between the invitation being sent and it going out: he's not invited after all.
        await staff.PostApiAsync($"/api/social/{id}/invite", new { subject = "Tour", body = "Join us.", clientIds = new[] { seed.AnnId, seed.BobId } });
        await using (var context = db.ContextFor(seed.TenantId))
        {
            (await context.Parties.SingleAsync(p => p.Id == seed.BobId)).Unsubscribe(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }

        await DispatchAsync();
        var lines = (await staff.GetFromJsonAsync<ListPage>($"/api/social/{id}"))!.Attendees;
        var bob = lines.Single(a => a.PartyId == seed.BobId);
        Assert.Equal((true, "Unsubscribed", null), (bob.Unsent, bob.CantEmail, bob.Delivery));
        Assert.Equal("Sent", lines.Single(a => a.PartyId == seed.AnnId).Delivery);

        // Send invitations says so, rather than leaving him out quietly.
        var draft = (await staff.GetFromJsonAsync<InviteDraft>($"/api/social/{id}/invitation"))!;
        Assert.Empty(draft.Going);
        Assert.Equal(["Unsubscribed"], draft.LeftOut.Select(l => l.Why));
    }
}
