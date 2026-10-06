using Microsoft.EntityFrameworkCore;
using Paddockside.Api.Formatting;
using Paddockside.Domain;
using Paddockside.Infrastructure.Inbound;
using Paddockside.Infrastructure.Persistence;
using DomainEvent = Paddockside.Domain.Event;

namespace Paddockside.Api.Development;

/// <summary>
/// Development only: gives the demo tenant's horses their details and a few events, once (when the tenant has
/// no events yet). Dates are relative to today so the race start is always a couple of days away. Fictional.
/// </summary>
public static class DemoStream
{
    private const string Staff = "Kate";

    public static async Task EnsureAsync(DbContextOptions<PaddocksideDbContext> options, Guid tenantId, TimeProvider clock, ILogger logger)
    {
        await using var db = new PaddocksideDbContext(options, new DevelopmentSeeder.FixedTenant(tenantId));
        var tenant = await db.Tenants.SingleAsync();

        // Branding and owner email addresses, for databases seeded before email existed. Addresses use the
        // reserved .test domain, which can never reach a real inbox.
        if (tenant.Slug != "laureloak" || tenant.FooterDetails is null)
            tenant.SetBranding("laureloak", tenant.LogoUrl, "Demo tenant for Paddockside development · not a real business");
        // Mobiles come from 0491 570 006–159, the range ACMA reserves for fiction, so no real phone is ever texted.
        var fictional = 6;
        foreach (var party in (await db.Parties.ToListAsync()).OrderBy(p => p.DisplayName))
        {
            if (party.Kind != PartyKind.Person) continue;
            if (party.PrimaryEmail is null) party.AddEmail($"{party.DisplayName.ToLowerInvariant().Replace(' ', '.')}@owners.paddockside.test");
            if (party.PrimaryMobile is null && fictional <= 159) party.AddMobile($"0491 570 {fictional:D3}");
            fictional++;
        }

        await db.SaveChangesAsync();
        // Each horse's own inbox, e.g. faultless-miss@laureloak.in.paddockside.com.au.
        await HorseInboxes.EnsureAsync(db, clock.GetUtcNow(), CancellationToken.None);

        if (await db.Events.AnyAsync()) return;
        var horses = await db.Horses.Include(h => h.ManagementPeriods).Include(h => h.Interests).AsSplitQuery().ToListAsync();
        var parties = await db.Parties.ToDictionaryAsync(p => p.DisplayName, p => p);
        Horse? Find(string name) => horses.FirstOrDefault(h => h.Name == name);

        // Today in Sydney; the race is the day after tomorrow at 1:30 pm.
        var today = Words.Local(clock.GetUtcNow()).Date;
        DateTimeOffset At(int dayOffset, int hour, int minute) => Words.FromLocal(today.AddDays(dayOffset).AddHours(hour).AddMinutes(minute));
        var raceDay = At(2, 13, 30);

        Find("Faultless Miss")?.SetDetails(HorseSex.Mare, new DateOnly(2022, 9, 14), "Sovereign Star", "Miss Finland");
        Find("Northern Lark")?.SetDetails(HorseSex.Gelding, new DateOnly(2021, 10, 2), "Tamarind Bay", "Lark Song");
        Find("Lot 231 (unnamed)")?.SetDetails(HorseSex.Colt, new DateOnly(2024, 9, 20), "Sovereign Star", "Autumn Belle");
        Find("Ocean Ridge")?.SetDetails(HorseSex.Gelding, new DateOnly(2020, 8, 30), "Harbour Light", "Ocean Song");

        if (Find("Faultless Miss") is not { } mare)
        {
            await db.SaveChangesAsync();
            return;
        }

        // ---- The race start: facts, a correction, owners tickets with replies, a staff note ----------------
        var race = new DomainEvent(mare, EventTypes.RaceStart.Code, "Randwick, Race 6 · 1400 m", raceDay);
        race.Open(At(-9, 9, 0));

        var nomination = StreamItem.Fact(mare, race, "Nomination", "Racing NSW",
            [new("Race", "Randwick R6, 1400 m, Benchmark 78"), new("Nominated by", "J. Hartley")], At(-7, 9, 40), stepCode: "NOM_FACT");
        var acceptance = StreamItem.Fact(mare, race, "Acceptance", "Racing NSW",
            [new("Race", "Randwick R6, 1400 m, Benchmark 78"), new("Weight", "56.5 kg"), new("Jockey", "To be confirmed")], At(-3, 11, 2), stepCode: "ACC_FACT");
        var barrier = StreamItem.Fact(mare, race, "Barrier draw", "Racing NSW", [new("Barrier", "7"), new("Field", "14 runners")], At(-3, 12, 0), stepCode: "BARRIER");
        var barrierCorrected = barrier.Correct(mare, race, [new("Barrier", "9"), new("Field", "13 runners")], At(-3, 16, 15), "barrier redraw after a scratching");
        var note = StreamItem.Note(mare, race, "Rang the stable: the trainer's pre-race report will come Friday after trackwork.", At(-3, 10, 0), Staff);

        var tickets = StreamItem.Message(mare, race, "Owners tickets for race day",
            "She is accepted for Race 6 at Randwick, now from barrier 9.\n\nThe club has given us 8 owners tickets. Reply with how many you would like by 5 pm tomorrow and we will confirm them the day before the race.",
            StreamItemScope.Owners, At(-3, 17, 30), Staff, stepCode: "TICKETS");
        var replies = new List<StreamItem>();
        void ReplyFrom(string name, string channel, string body, DateTimeOffset at)
        {
            if (parties.TryGetValue(name, out var party)) replies.Add(tickets.Reply(mare, race, name, party.Id, channel, body, at));
        }
        ReplyFrom("Margaret Hale", "email", "Two please, for me and my sister. Can we see her in the mounting yard?", At(-3, 18, 12));
        ReplyFrom("Tom Okafor", "text message", "Just me this time, thanks.", At(-3, 19, 40));
        ReplyFrom("Priya Nair", "email", "None for us, we will watch from home. Good luck!", At(-3, 21, 5));

        var deliveries = Delivery.ForOwners(tenant, mare, tickets, DeliveryChannel.Email, At(-3, 17, 30));
        foreach (var delivery in deliveries)
        {
            var name = parties.Values.First(p => p.Id == delivery.PartyId).DisplayName;
            delivery.Record(name switch
            {
                "Margaret Hale" or "Tom Okafor" or "Priya Nair" => DeliveryStatus.Replied,
                "Graham Lowe" => DeliveryStatus.Opened,
                _ => DeliveryStatus.Bounced,
            }, At(-3, 18, 0));
        }

        // ---- An earlier barrier trial, closed; and a cancelled vet visit -----------------------------------
        var trial = new DomainEvent(mare, EventTypes.BarrierTrial.Code, "Rosehill trial, heat 4", At(-26, 10, 0));
        trial.Open(At(-30, 9, 0));
        var trialItems = new[]
        {
            StreamItem.Fact(mare, trial, "Trial entry", "Racing NSW", [new("Heat", "4"), new("Distance", "1050 m")], At(-29, 9, 0), stepCode: "TRIAL_ENTRY"),
            StreamItem.Fact(mare, trial, "Trial result", "Racing NSW", [new("Finished", "2nd of 9"), new("Margin", "0.8 lengths")], At(-26, 10, 20), stepCode: "TRIAL_RESULT"),
            StreamItem.Message(mare, trial, "Trainer's report", "Pleased with that. Ran on strongly and pulled up well; she is ready for a race.",
                StreamItemScope.Owners, At(-25, 8, 0), Staff, stepCode: "TRAINER_REPORT"),
        };
        if (parties.TryGetValue("Laurel Oak house share", out var house)) trial.Close(At(-24, 16, 0), house.Id);

        var vet = new DomainEvent(mare, EventTypes.Veterinary.Code, "Pre-season check", At(-28, 9, 0));
        vet.Open(At(-32, 9, 0));
        vet.Cancel();

        db.AddRange(race, trial, vet);
        db.AddRange(nomination, acceptance, barrier, barrierCorrected, note, tickets);
        db.AddRange(replies);
        db.AddRange(trialItems);
        db.AddRange(deliveries);
        await db.SaveChangesAsync();
        logger.LogInformation("Seeded the demo event stream for {Horse}.", mare.Name);
    }
}
