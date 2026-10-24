using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// The "did everyone get told" reports (search-reporting.md §4): delivery by event, and unreached owners. Shares the
/// sign-in tests' seed: Bel Esprit, owned by Ann and Bob, whose Caulfield race has a tickets question both replied to.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record ReportItem(Guid Id, string Title, string Summary);

    private sealed record ReportCell(string State, string? Detail);

    private sealed record ReportOwner(Guid PartyId, string Name, bool CurrentOwner, List<ReportCell> Cells);

    private sealed record DeliveryReportView(string Horse, string Event, List<ReportItem> Items, List<ReportOwner> Owners);

    private sealed record UnreachedView(Guid PartyId, string Name, string Horses, int Missed, string Why);

    private sealed record UnreachedReportView(int Last, int Days, int Checked, List<UnreachedView> Owners);

    [Fact]
    public async Task Delivery_by_event_and_unreached_owners_show_who_saw_what()
    {
        var seed = await SeedAsync();
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);

        // The result goes to both owners by email: Ann opens it, Bob's bounces.
        var result = (await (await staff.PostApiAsync($"/api/events/{seed.EventId}/items",
            new { audience = "Owners", scope = "Owners", step = "Result", channel = "Email", namedParties = (Guid[]?)null, body = "Second, beaten a nose." }))
            .Content.ReadFromJsonAsync<Sent>())!;
        await using (var context = db.ContextFor(seed.TenantId))
        {
            var now = DateTimeOffset.UtcNow;
            (await context.Deliveries.SingleAsync(d => d.StreamItemId == result.Id && d.PartyId == seed.AnnId)).Record(DeliveryStatus.Opened, now);
            (await context.Deliveries.SingleAsync(d => d.StreamItemId == result.Id && d.PartyId == seed.BobId)).RecordBounce("Mailbox full", now);
            await context.SaveChangesAsync();
        }

        var report = (await staff.GetFromJsonAsync<DeliveryReportView>($"/api/reports/delivery/{seed.EventId}"))!;
        Assert.Equal("Bel Esprit", report.Horse);
        var column = report.Items.FindIndex(i => i.Id == result.Id);
        Assert.True(column >= 0);
        Assert.Equal("1 of 2 opened", report.Items[column].Summary);
        var ann = report.Owners.Single(o => o.PartyId == seed.AnnId);
        var bob = report.Owners.Single(o => o.PartyId == seed.BobId);
        Assert.Equal(new ReportCell("Opened", "Email"), ann.Cells[column]);
        Assert.Equal(new ReportCell("Bounced", "Mailbox full"), bob.Cells[column]);
        // Both replied to the tickets question, so both count as having seen it.
        var tickets = report.Items.FindIndex(i => i.Title == "Tickets");
        Assert.Equal("Replied", bob.Cells[tickets].State);
        // Staff notes and trainer messages are never in it.
        Assert.DoesNotContain(report.Items, i => i.Id == seed.NoteId || i.Id == seed.TrainerMessageId);

        var csv = await staff.GetStringAsync($"/api/reports/delivery/{seed.EventId}/export.csv");
        Assert.Contains("\"Bob Owner\"", csv);
        Assert.Contains("Bounced: Mailbox full", csv);

        // Bob's last important messages: the result bounced and he hasn't looked at the barrier in the portal.
        var unreached = (await staff.GetFromJsonAsync<UnreachedReportView>("/api/reports/unreached?last=2"))!;
        Assert.Equal(2, unreached.Checked);
        var only = Assert.Single(unreached.Owners);
        Assert.Equal((seed.BobId, "Bel Esprit", "Bounced: Mailbox full"), (only.PartyId, only.Horses, only.Why));
        Assert.Contains("Bob Owner", await staff.GetStringAsync("/api/reports/unreached/export.csv?last=2"));

        // Staff only; and another business's event is not found.
        using var owner = await SignInAsync(seed.AnnEmail);
        Assert.NotEqual(HttpStatusCode.OK, (await owner.GetAsync($"/api/reports/delivery/{seed.EventId}")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await owner.GetAsync("/api/reports/unreached")).StatusCode);
        var other = await SeedAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"/api/reports/delivery/{other.EventId}")).StatusCode);
    }
}
