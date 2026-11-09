using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Closing an event early (David, 10 Nov 2026): an old race left open meant the trainer's mail couldn't pick between it
/// and the new one. Admins only, with a reason while steps are still to come. Shares the sign-in tests' seed: Bel
/// Esprit, owned by Ann and Bob, with a Caulfield race in five days.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record ClosedPage(EventSummary Event, string? Closed, List<ContactItem> Items);

    [Fact]
    public async Task An_admin_closes_an_old_event_early_with_a_reason_and_mail_then_files_on_the_new_one()
    {
        var seed = await SeedAsync();
        Guid newer;
        await using (var context = db.ContextFor(seed.TenantId))
        {
            var horse = await context.Horses.SingleAsync(h => h.Id == seed.HorseId);
            var race = new Paddockside.Domain.Event(horse, "RaceStart", "Randwick, next Saturday", DateTimeOffset.UtcNow.AddDays(12));
            race.Open(DateTimeOffset.UtcNow);
            context.Add(race);
            await context.SaveChangesAsync();
            newer = race.Id;
        }

        using var admin = await Api.SignedInAsync(seed.TenantId, MemberRole.TenantAdmin);
        var inbox = (await admin.GetFromJsonAsync<InboxPage>($"/api/horses/{seed.HorseId}"))!.InboxAddress!;
        var from = $"stable-{Guid.NewGuid():N}@trainers.test";

        // Two open races: the mail can't tell which, so it waits on the horse.
        await InboundEmailAsync(inbox, from, "Which race?", "Going well.");
        Assert.DoesNotContain((await admin.GetFromJsonAsync<ClosedPage>($"/api/events/{newer}"))!.Items, i => i.Title == "Which race?");

        // Only an admin closes, and an unfinished event needs a reason.
        using var manager = await Api.SignedInAsync(seed.TenantId, MemberRole.Manager);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostApiAsync($"/api/events/{seed.EventId}/close", new { reason = "Scratched" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostApiAsync($"/api/events/{seed.EventId}/close", new { reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostApiAsync($"/api/events/{seed.EventId}/close", new { reason = "Scratched; not running this prep" })).StatusCode);

        var closed = (await admin.GetFromJsonAsync<ClosedPage>($"/api/events/{seed.EventId}"))!;
        Assert.Equal("Closed", closed.Event.Status);
        Assert.EndsWith(": Scratched; not running this prep", closed.Closed);

        // Now the next email files itself on the one open race.
        await InboundEmailAsync(inbox, from, "This week", "All set.");
        Assert.Contains((await admin.GetFromJsonAsync<ClosedPage>($"/api/events/{newer}"))!.Items, i => i.Title == "This week");
        await using (var check = db.ContextFor(seed.TenantId))
            Assert.Contains(check.AuditEntries, e => e.Action == "event.closed" && e.Summary.Contains("Scratched; not running this prep"));

        // A week later Ann answers a message from the closed race: kept under it, the race stays closed, Today shows it.
        using var ann = await SignInAsync(seed.AnnEmail);
        Assert.Equal(HttpStatusCode.Created, (await ann.PostApiAsync($"/api/my/items/{seed.MessageId}/replies", new { body = "Sorry I missed this: two tickets." })).StatusCode);
        Assert.Equal("Closed", (await admin.GetFromJsonAsync<ClosedPage>($"/api/events/{seed.EventId}"))!.Event.Status);
        var today = (await admin.GetFromJsonAsync<TodayWithClosed>("/api/today"))!;
        var line = Assert.Single(today.ClosedReplies!);
        Assert.Equal(seed.EventId, line.Id);
        Assert.Contains("the latest from Ann Owner", line.Detail);

        // Mail to the horse still files itself on the one open race.
        await InboundEmailAsync(inbox, from, "After the reply", "Still all set.");
        Assert.Contains((await admin.GetFromJsonAsync<ClosedPage>($"/api/events/{newer}"))!.Items, i => i.Title == "After the reply");
    }

    private sealed record TodayWithClosed(List<TodayQuiet>? ClosedReplies);
}
