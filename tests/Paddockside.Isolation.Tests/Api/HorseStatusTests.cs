using System.Net;
using System.Net.Http.Json;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// A horse's status and what's next (David, 19 Nov 2026): set from the list, kept as history, shown to owners, and
/// optionally sent to them as an update. Shares the sign-in tests' seed: Bel Esprit, owned by Ann and Bob, with a
/// Caulfield race in five days.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record StatusLine(string Code, string Label, string? Location, string? Next, string? ExpectedBack, string? ExpectedOn, string Line);

    private sealed record StatusHistory(StatusLine? Current, List<StatusLine> History);

    private sealed record ListedHorse(Guid Id, string Name, StatusLine? Status, bool Stale, Guid? NextEventId, string? NextEvent);

    private sealed record StatusTold(int Told);

    private sealed record OwnedHorse(Guid Id, string? Status, string? StatusShort);

    [Fact]
    public async Task A_horses_status_is_set_kept_as_history_shown_to_owners_and_sent_when_staff_say()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var status = $"/api/horses/{seed.HorseId}/status";
        await DispatchAsync(); // the owners' portal invitations, from the seed
        var sentBefore = Api.Emails.Sent.Count;

        // Spelling, set quietly: on the list and the history, nothing sent.
        var spell = new { status = "Spelling", location = "Riverbank Farm", next = "Usual 6-week spell", expectedBack = "late December", expectedOn = "2026-12-20", tellOwners = false };
        Assert.Equal(0, (await (await staff.PostApiAsync(status, spell)).Content.ReadFromJsonAsync<StatusTold>())!.Told);
        var listed = (await staff.GetFromJsonAsync<List<ListedHorse>>("/api/horses"))!.Single(h => h.Id == seed.HorseId);
        Assert.Equal(("Spelling", "Riverbank Farm", "late December", "2026-12-20"), (listed.Status!.Label, listed.Status.Location, listed.Status.ExpectedBack, listed.Status.ExpectedOn));
        Assert.Equal("Spelling at Riverbank Farm. Usual 6-week spell. Expected back late December.", listed.Status.Line);
        Assert.False(listed.Stale);
        Assert.Equal(seed.EventId, listed.NextEventId);
        Assert.StartsWith("Caulfield, Saturday · ", listed.NextEvent);
        await DispatchAsync();
        Assert.Equal(sentBefore, Api.Emails.Sent.Count);

        // Back in work, and the owners told: the same check first, then an update to both.
        var back = new { status = "InWork", location = "Sam Stable, Randwick", next = "Looking for a start in the next 2 weeks", expectedBack = (string?)null, expectedOn = (string?)null, tellOwners = true };
        var who = (await (await staff.PostApiAsync($"{status}/recipients", back)).Content.ReadFromJsonAsync<RecipientsPage>())!;
        Assert.Equal(["Ann Owner", "Bob Owner"], who.Going.Select(g => g.Name));
        Assert.Equal(2, (await (await staff.PostApiAsync(status, back)).Content.ReadFromJsonAsync<StatusTold>())!.Told);
        await DispatchAsync();
        Assert.Contains(Api.Emails.Sent, e => e.Email.ToAddress == seed.AnnEmail && e.Email.TextBody.Contains("In work at Sam Stable, Randwick. Looking for a start in the next 2 weeks."));

        // The history, newest first; Ann sees where her horse is at.
        var history = (await staff.GetFromJsonAsync<StatusHistory>(status))!;
        Assert.Equal(["In work", "Spelling"], history.History.Select(h => h.Label));
        using var ann = await SignInAsync(seed.AnnEmail);
        var mine = (await ann.GetFromJsonAsync<List<OwnedHorse>>("/api/my/horses"))!.Single(h => h.Id == seed.HorseId);
        Assert.Equal(("In work at Sam Stable, Randwick", "In work at Sam Stable, Randwick. Looking for a start in the next 2 weeks."), (mine.StatusShort, mine.Status));

        // Only a known status; a viewer can read but not set; another business's horse isn't found.
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync(status, new { status = "Galloping", tellOwners = false })).StatusCode);
        using var viewer = await Api.SignedInAsync(seed.TenantId, MemberRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostApiAsync(status, spell)).StatusCode);
        using var otherStaff = await Api.SignedInAsync(db.A.TenantId, MemberRole.Coordinator);
        Assert.Equal(HttpStatusCode.NotFound, (await otherStaff.PostApiAsync(status, spell)).StatusCode);
    }
}
