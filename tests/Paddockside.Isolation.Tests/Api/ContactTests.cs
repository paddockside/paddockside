using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// A horse's trainer, vet, authority and clubs (David, 9 Nov 2026): the trainer's first email reaches the horse from an
/// address nobody knows; staff say "this is the trainer"; from then on the trainer's email lands under the step due,
/// ready to send on to owners. Steps can be set afterwards without sending anything. Shares the sign-in tests' seed:
/// Bel Esprit, owned by Ann and Bob, with a Caulfield race in five days.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record InboxPage(string? InboxAddress);

    private sealed record ContactLine(Guid Id, Guid PartyId, string Name, string? Email, string Role);

    private sealed record ContactItem(Guid Id, string? Step, string? FromRole, bool CanTagSender, string? SendOnStep, string? Title);

    private sealed record ContactEventPage(List<ContactItem> Items);

    private sealed record ClientLine(Guid Id, string Name);

    [Fact]
    public async Task The_trainer_is_tagged_from_their_email_and_from_then_on_their_updates_land_on_the_step()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var inbox = (await staff.GetFromJsonAsync<InboxPage>($"/api/horses/{seed.HorseId}"))!.InboxAddress!;
        var trainer = $"stable-{Guid.NewGuid():N}@trainers.test";
        async Task<ContactItem> Find(string subject) =>
            (await staff.GetFromJsonAsync<ContactEventPage>($"/api/events/{seed.EventId}"))!.Items.Single(i => i.Title == subject);

        // Race week: the trainer writes to the horse for the first time. Nobody knows the address, so staff are asked.
        await InboundEmailAsync(inbox, trainer, "Bel Esprit this week", "Worked well, all set for Saturday.");
        var first = await Find("Bel Esprit this week");
        Assert.True(first.CanTagSender);
        Assert.Null(first.Step);

        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostApiAsync($"/api/items/{first.Id}/sender", new { role = "Trainer" })).StatusCode);
        first = await Find("Bel Esprit this week");
        Assert.Equal(("Trainer", "Trainer's pre-race", "Pre-race update", false), (first.FromRole, first.Step, first.SendOnStep, first.CanTagSender));
        var contact = Assert.Single((await staff.GetFromJsonAsync<List<ContactLine>>($"/api/horses/{seed.HorseId}/contacts"))!);
        Assert.Equal(("Trainer", trainer), (contact.Role, contact.Email));
        // A contact, not a client.
        Assert.DoesNotContain((await staff.GetFromJsonAsync<List<ClientLine>>("/api/clients"))!, c => c.Id == contact.PartyId);

        // From now on: the trainer's next email files itself under the step, staff only, nothing sent.
        await DispatchAsync(); // the owners' portal invitations, from the seed
        var sentBefore = Api.Emails.Sent.Count;
        await InboundEmailAsync(inbox, trainer, "Gear change", "Blinkers on for Saturday.");
        var second = await Find("Gear change");
        Assert.Equal(("Trainer", "Trainer's pre-race"), (second.FromRole, second.Step));

        // Staff file it under another step afterwards: nothing is sent again.
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostApiAsync($"/api/items/{second.Id}/step", new { step = "Trainer's plan" })).StatusCode);
        Assert.Equal("Trainer's plan", (await Find("Gear change")).Step);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PostApiAsync($"/api/items/{second.Id}/step", new { step = "Foaling" })).StatusCode);
        await DispatchAsync();
        Assert.Equal(sentBefore, Api.Emails.Sent.Count);

        // A new trainer takes over: one trainer at a time.
        var next = $"new-{Guid.NewGuid():N}@trainers.test";
        await InboundEmailAsync(inbox, next, "Taking over", "I have her from Monday.");
        Assert.Equal(HttpStatusCode.NoContent, (await staff.PostApiAsync($"/api/items/{(await Find("Taking over")).Id}/sender", new { role = "Trainer" })).StatusCode);
        Assert.Equal(next, Assert.Single((await staff.GetFromJsonAsync<List<ContactLine>>($"/api/horses/{seed.HorseId}/contacts"))!).Email);

        // A viewer can't say who's who.
        using var viewer = await Api.SignedInAsync(seed.TenantId, MemberRole.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostApiAsync($"/api/items/{first.Id}/sender", new { role = "Vet" })).StatusCode);
        await using var context = db.ContextFor(seed.TenantId);
        Assert.Contains(context.AuditEntries, e => e.Action == "contact.tagged");
    }
}
