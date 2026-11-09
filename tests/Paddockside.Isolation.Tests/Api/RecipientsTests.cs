using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Who a message will go to, shown before it is sent (David, 9 Nov 2026): a nomination and a photo post on a horse
/// reached nobody because both kinds were off by default and one owner had no email, and nothing said so. Shares the
/// sign-in tests' seed: Bel Esprit, owned by Ann (email and mobile) and Bob (email).
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record GoingLine(Guid PartyId, string Name, string How);

    private sealed record LeftOutLine(Guid PartyId, string Name, string Why);

    private sealed record RecipientsPage(string Category, List<GoingLine> Going, List<LeftOutLine> LeftOut, int ToConfirm);

    [Fact]
    public async Task Before_sending_staff_see_who_it_goes_to_and_who_it_wont_reach()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        async Task<RecipientsPage> Preview(object request) =>
            (await (await staff.PostApiAsync($"/api/events/{seed.EventId}/recipients", request)).Content.ReadFromJsonAsync<RecipientsPage>())!;

        // A nomination goes to everyone by default now, and photos too.
        var nomination = new { scope = "Owners", step = "NOM_FACT", channel = "Preferred", namedParties = Array.Empty<Guid>(), photos = false };
        var page = await Preview(nomination);
        Assert.Equal("Nominations and entries", page.Category);
        Assert.Equal(["Ann Owner", "Bob Owner"], page.Going.Select(g => g.Name));
        Assert.All(page.Going, g => Assert.Equal("Email", g.How));
        Assert.Equal(2, (await Preview(new { scope = "Owners", step = (string?)null, channel = "Preferred", namedParties = Array.Empty<Guid>(), photos = true })).Going.Count);

        // Staff only.
        using (var ann = await SignInAsync(seed.AnnEmail))
            Assert.NotEqual(HttpStatusCode.OK, (await ann.PostApiAsync($"/api/events/{seed.EventId}/recipients", nomination)).StatusCode);

        // Bob switches nominations off; Ann's email goes. Both are left out, and why.
        await using (var context = db.ContextFor(seed.TenantId))
        {
            var bob = await context.Parties.SingleAsync(p => p.Id == seed.BobId);
            context.Add(new NotificationPreference(bob, NotificationCategories.Entries, DeliveryChannel.Email, false, DateTimeOffset.UtcNow));
            (await context.Parties.SingleAsync(p => p.Id == seed.AnnId)).ChangeEmail(null);
            await context.SaveChangesAsync();
        }

        page = await Preview(nomination);
        Assert.Empty(page.Going);
        Assert.Equal("Has switched off \"Nominations and entries\"", page.LeftOut.Single(l => l.PartyId == seed.BobId).Why);
        Assert.Equal("No email address (texts can't be sent yet)", page.LeftOut.Single(l => l.PartyId == seed.AnnId).Why);

        // The send does exactly what the preview said: nobody is emailed.
        var sent = (await (await staff.PostApiAsync($"/api/events/{seed.EventId}/items",
            new { audience = "Owners", scope = "Owners", step = "NOM_FACT", channel = "Preferred", namedParties = (Guid[]?)null, body = "Nominated for Saturday." }))
            .Content.ReadFromJsonAsync<Sent>())!;
        Assert.Equal(0, sent.Recipients);

        // Another business's event isn't found.
        using var otherStaff = await Api.SignedInAsync(db.A.TenantId, MemberRole.Coordinator);
        Assert.Equal(HttpStatusCode.NotFound, (await otherStaff.PostApiAsync($"/api/events/{seed.EventId}/recipients", nomination)).StatusCode);
    }
}
