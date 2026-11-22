using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
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
        var preview = (await (await staff.PostApiAsync($"/api/social/{id}/invite/preview", new { listIds = Array.Empty<Guid>(), statuses = Array.Empty<string>(), clientIds = new[] { seed.AnnId } }))
            .Content.ReadFromJsonAsync<MailoutPreview>())!;
        Assert.Equal(0, preview.Sending);
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
}
