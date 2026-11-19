using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// The calendar (David, 19 Nov 2026): every event on its date, drafts marked, cancelled ones left off, and horses due
/// back from a spell. Shares the sign-in tests' seed: Bel Esprit, owned by Ann and Bob, with a Caulfield race in five days.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record CalendarLine(string Date, string Kind, string KindLabel, Guid? HorseId, string Title, string Short, string? Detail, string Href,
        bool Draft, bool Done);

    [Fact]
    public async Task The_calendar_shows_events_drafts_and_horses_due_back_but_not_cancelled_events()
    {
        var seed = await SeedAsync();
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, sydney).Date);
        DateTimeOffset On(int days) { var day = today.AddDays(days).ToDateTime(new TimeOnly(12, 0)); return new(day, sydney.GetUtcOffset(day)); }
        await using (var context = db.ContextFor(seed.TenantId))
        {
            var horse = await context.Horses.SingleAsync(h => h.Id == seed.HorseId);
            var draft = new Paddockside.Domain.Event(horse, "Veterinary", "Scope after trackwork", On(8));
            var cancelled = new Paddockside.Domain.Event(horse, "BarrierTrial", "Rosehill trial", On(9));
            cancelled.Cancel();
            context.AddRange(draft, cancelled,
                new HorseStatusEntry(horse, HorseStatus.Spelling, "Riverbank Farm", "Usual spell", "in a fortnight", today.AddDays(14), "Staff", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Viewer);
        string Day(int days) => today.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var entries = (await staff.GetFromJsonAsync<List<CalendarLine>>($"/api/calendar?from={Day(0)}&to={Day(20)}"))!;

        var race = entries.Single(e => e.Kind == "RaceStart");
        Assert.Equal(("Race start", "Bel Esprit · Caulfield, Saturday", $"events/{seed.EventId}", false), (race.KindLabel, race.Short, race.Href, race.Draft));
        Assert.StartsWith("next: ", race.Detail);
        Assert.True(entries.Single(e => e.Kind == "Veterinary").Draft);
        Assert.DoesNotContain(entries, e => e.Kind == "BarrierTrial");
        var back = entries.Single(e => e.Kind == "DueBack");
        Assert.Equal((Day(14), "Spelling at Riverbank Farm", $"horses/{seed.HorseId}"), (back.Date, back.Detail, back.Href));

        // A month and its edges at most; and another business's calendar has none of this.
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.GetAsync($"/api/calendar?from={Day(0)}&to={Day(200)}")).StatusCode);
        using var otherStaff = await Api.SignedInAsync(db.A.TenantId, MemberRole.Coordinator);
        Assert.DoesNotContain((await otherStaff.GetFromJsonAsync<List<CalendarLine>>($"/api/calendar?from={Day(0)}&to={Day(20)}"))!, e => e.HorseId == seed.HorseId);
    }
}
