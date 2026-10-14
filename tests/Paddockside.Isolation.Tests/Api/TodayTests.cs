using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Paddockside.Domain;

namespace Paddockside.Isolation.Tests.Api;

/// <summary>
/// Today, the staff home (search-reporting.md §3): runners today and tomorrow with their stage, important messages
/// that haven't reached everyone, the queue, dormant events, drafts and integration health. Shares the sign-in tests'
/// seed: Bel Esprit, owned by Ann and Bob, with a Caulfield race in five days.
/// </summary>
public sealed partial class OwnerSignInTests
{
    private sealed record TodayRunner(Guid EventId, string Horse, string Title, string? Time, List<string> Attention);

    private sealed record TodayUndelivered(Guid ItemId, string Detail);

    private sealed record TodayQueue(int Waiting, string? Oldest);

    private sealed record TodayQuiet(Guid Id, string Title, string Detail);

    private sealed record TodayHealth(string Label, string State, string Detail);

    private sealed record TodayView(string Date, List<TodayRunner> Today, List<TodayRunner> Tomorrow, List<TodayUndelivered> Undelivered,
        TodayQueue Queue, List<TodayQuiet> Dormant, List<TodayQuiet> Drafts, List<TodayHealth> Health);

    [Fact]
    public async Task Today_shows_the_runners_what_has_not_arrived_and_what_is_waiting()
    {
        var seed = await SeedAsync();
        var now = DateTimeOffset.UtcNow;
        var sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
        var localToday = TimeZoneInfo.ConvertTime(now, sydney).Date;
        DateTimeOffset At(DateTime wallClock) => new(wallClock, sydney.GetUtcOffset(wallClock));

        Guid raceToday, trialTomorrow, quiet, draft;
        await using (var context = db.ContextFor(seed.TenantId))
        {
            var horse = await context.Horses.SingleAsync(h => h.Id == seed.HorseId);
            var race = new Paddockside.Domain.Event(horse, "RaceStart", "Randwick, Race 6", At(localToday.AddHours(15).AddMinutes(5)));
            race.Open(now.AddDays(-6));
            var trial = new Paddockside.Domain.Event(horse, "BarrierTrial", "Rosehill trial, heat 4", At(localToday.AddDays(1).AddHours(9)));
            trial.Open(now.AddDays(-2));
            var spell = new Paddockside.Domain.Event(horse, "General", "Spelling at Scone");
            spell.Open(now.AddDays(-30));
            var vet = new Paddockside.Domain.Event(horse, "Veterinary", "Scope after trackwork", now.AddDays(3));
            context.AddRange(race, trial, spell, vet);
            await context.SaveChangesAsync();
            (raceToday, trialTomorrow, quiet, draft) = (race.Id, trial.Id, spell.Id, vet.Id);
        }

        // A result goes to both owners; Bob's bounces.
        using var staff = await Api.SignedInAsync(seed.TenantId, MemberRole.Coordinator);
        var result = (await (await staff.PostApiAsync($"/api/events/{seed.EventId}/items",
            new { audience = "Owners", scope = "Owners", step = "Result", channel = "Email", namedParties = (Guid[]?)null, body = "Second, beaten a nose." }))
            .Content.ReadFromJsonAsync<Sent>())!;
        // A stable update isn't important news: it is never listed, even undelivered.
        await staff.PostApiAsync($"/api/events/{seed.EventId}/items",
            new { audience = "Owners", scope = "Owners", step = (string?)null, channel = "Email", namedParties = (Guid[]?)null, body = "Worked well this morning." });
        await using (var context = db.ContextFor(seed.TenantId))
        {
            var bobs = await context.Deliveries.SingleAsync(d => d.StreamItemId == result.Id && d.PartyId == seed.BobId);
            bobs.RecordBounce("Mailbox full", now);
            foreach (var d in await context.Deliveries.Where(d => d.StreamItemId != result.Id).ToListAsync()) d.RecordBounce("Mailbox full", now);
            await context.SaveChangesAsync();
        }

        var today = (await staff.GetFromJsonAsync<TodayView>("/api/today"))!;

        var runner = Assert.Single(today.Today);
        Assert.Equal((raceToday, "Bel Esprit", "3:05 pm"), (runner.EventId, runner.Horse, runner.Time));
        Assert.Equal(trialTomorrow, Assert.Single(today.Tomorrow).EventId);
        Assert.DoesNotContain(today.Today.Concat(today.Tomorrow), r => r.EventId == seed.EventId); // Caulfield is in five days

        var undelivered = Assert.Single(today.Undelivered);
        Assert.Equal(result.Id, undelivered.ItemId);
        Assert.Equal("1 of 2 not delivered: 1 bounced", undelivered.Detail);

        Assert.Equal(quiet, Assert.Single(today.Dormant).Id);      // the seed race had a reply a day ago
        Assert.Equal(draft, Assert.Single(today.Drafts).Id);
        Assert.Equal(0, today.Queue.Waiting);
        Assert.Contains(today.Health, h => h.Label == "Texts");

        // Staff only.
        using var ann = await SignInAsync(seed.AnnEmail);
        Assert.NotEqual(HttpStatusCode.OK, (await ann.GetAsync("/api/today")).StatusCode);
    }
}
