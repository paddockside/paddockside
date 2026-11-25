using Paddockside.Application.Messaging;
using Paddockside.Domain;

namespace Paddockside.Infrastructure.Email;

/// <summary>One of the emails the system sends, with made-up content in a real business's look.</summary>
/// <param name="FromProduct">Sent by Paddockside itself (a staff password), not the business.</param>
public sealed record EmailSample(string Key, string Label, string Group, RenderedEmail Email, bool FromProduct = false);

/// <summary>
/// Every kind of email, as a business's owners, clients and staff would get it (David, 25 Nov 2026: "a range of dummy
/// notifications… so I can quickly see how they will all look"). The horse, people and events are made up and never
/// saved; every link opens a page saying it's a preview. A new kind of email belongs in this list too.
/// </summary>
public static class EmailSamples
{
    public const string Updates = "Updates to owners", Questions = "Questions to owners", Access = "Signing in", Mailouts = "Mailouts and invitations", Staff = "Staff";

    /// <param name="baseUrl">The app's own address, for the sample photo and the links.</param>
    public static IReadOnlyList<EmailSample> For(Tenant tenant, OwnerEmailRenderer renderer, string baseUrl, DateTimeOffset now)
    {
        var root = baseUrl.TrimEnd('/');
        var link = $"{root}/email-preview";
        var photo = $"{root}/img/email-sample.jpg";
        const string by = "Alex Sample";

        var horse = new Horse(tenant.Id, "Sample Filly", HorseNameKind.Registered, now.AddYears(-3));
        horse.OpenManagementPeriod(now.AddYears(-2));
        var race = new Event(horse, EventTypes.RaceStart.Code, "Randwick, Race 6, Saturday", now.AddDays(3));
        race.Open(now);
        var owner = new Party(tenant.Id, "Jordan Example");

        RenderedEmail Update(StreamItem item, EmailMedia? media = null) =>
            renderer.Render(tenant, horse, race, item, owner, link, link, media, item.Prompt is null ? null : link);
        StreamItem Message(string? title, string body, MessagePrompt? prompt = null) =>
            StreamItem.Message(horse, race, title, body, StreamItemScope.Owners, now, by, prompt: prompt);
        RenderedEmail Media(string title, string caption, string? url, params MediaKind[] kinds) =>
            Update(StreamItem.Media(horse, race, title, caption, StreamItemScope.Owners, now, by), new EmailMedia(url, kinds));

        var barrier = StreamItem.Fact(horse, race, "Barrier draw", "Racing NSW", [new FactField("Barrier", "7"), new FactField("Weight", "56.5 kg"), new FactField("Runners", "12")], now);
        var redraw = barrier.Correct(horse, race, [new FactField("Barrier", "4"), new FactField("Weight", "56.5 kg"), new FactField("Runners", "11")], now, "redraw after a scratching");
        var acceptance = StreamItem.Fact(horse, race, "Acceptance", "Racing NSW",
            [new FactField("Race", "The Sample Handicap, 1400m"), new FactField("Jockey", "R. Example"), new FactField("Start time", "3:15 pm"), new FactField("Track", "Good 4"), new FactField("Prize money", "$160,000")], now);
        var newsletter = new Mailout(tenant.Id, "Spring news from the stable",
            "It has been a busy few weeks, with six runners and two winners.\n\nThe yearlings have settled in well and three have already been broken in. We will have videos of each of them for you next month.\n\nThank you for your support this season.",
            "All clients", "sample@preview.invalid", now, signature: $"Kind regards,\n{by}\n{tenant.Name}");
        var invitation = new Mailout(tenant.Id, "Christmas Lunch", "Please join us for our annual Christmas lunch. Partners are very welcome.\n\nDrinks from 12 noon, with lunch at 12:30 pm.",
            "Social list", "sample@preview.invalid", now, signature: $"Kind regards,\n{by}");

        // To a mailout or an invitation; made in memory like the rest.
        MailoutAnswer SampleAnswer()
        {
            var sent = new MailoutRecipient(invitation, owner, RoutingToken.New(), now);
            var arrived = new InboundMessage(tenant.Id, "Postmark", "preview", now, "jordan@preview.invalid", owner.DisplayName, "m@preview.invalid", "", "Christmas Lunch", "Is there parking?", null, "[]", "preview");
            return new MailoutAnswer(sent, new MailoutReply(sent, arrived, "Is there parking?"), "Yes, there's parking under the building, and it's free for lunch guests.\n\nLooking forward to seeing you.", $"Kind regards,\n{by}", by, now);
        }

        return
        [
            new("update", "Update from staff", Updates, Update(Message(null, "She worked well this morning and pulled up bright. The stable is happy to push on to Saturday.\n\nWe will let you know the barrier on Wednesday."))),
            new("titled", "Update with a heading", Updates, Update(Message("Trainer's plan", "She will have one more gallop on Thursday and go to the races on Saturday if she pulls up well.\n\nThe 1400 metres should suit her."))),
            new("barrier", "Barrier draw", Updates, Update(barrier)),
            new("correction", "Corrected barrier draw", Updates, Update(redraw)),
            new("acceptance", "Acceptance, with more details", Updates, Update(acceptance)),
            new("photos", "Photos", Updates, Media("New photos", "Out on the track this morning.", photo, MediaKind.Photo, MediaKind.Photo, MediaKind.Photo, MediaKind.Photo)),
            new("video", "Video", Updates, Media("A new video", "Her last gallop before Saturday.", null, MediaKind.Video)),
            new("voice", "Voice note", Updates, Media("A voice note", "The trainer's thoughts after this morning's work.", null, MediaKind.Audio)),
            new("tickets", "Owners tickets question", Questions, Update(Message("Owners tickets for Saturday", "", MessagePrompt.Count("How many owners tickets would you like?")))),
            new("yesno", "Yes or no question", Questions, Update(Message("Presentation", "If she wins, owners are invited to the presentation.", MessagePrompt.YesNo("Will you be there for the presentation?")))),
            new("choice", "Multiple choice question", Questions, Update(Message("Her name", "It's time to choose a name.", MessagePrompt.Choice("Which name do you like best?", ["Sample Filly", "Example Lass", "First Preview"])))),
            new("confirmed", "Tickets confirmed", Questions, Update(Message("Your owners tickets", "You have 2 owners tickets for Saturday. Collect them from the owners' gate from 11 am."))),
            new("reply", "Reply to someone who wrote in", Updates, renderer.RenderDirectReply(tenant, horse, race,
                StreamItem.Message(horse, race, "Re: Saturday", "Thanks for getting in touch. Friday at 10 works for us.", StreamItemScope.NamedParties, now, by, [owner.Id]), owner)),
            new("welcome", "Welcome, a new owner", Access, renderer.RenderInvitation(tenant, horse, owner, link)),
            new("signin", "Sign-in link and code", Access, renderer.RenderSignIn(EmailBrand.Of(tenant), link, "123456", 15)),
            new("mailout", "Mailout", Mailouts, renderer.RenderMailout(tenant, newsletter, owner, link)),
            new("invitation", "Social event invitation", Mailouts, renderer.RenderMailout(tenant, invitation, owner, link,
                new RsvpBlock("Christmas Lunch", "Saturday 12 December, 12:30 pm", "The Sample Club", link, link, link))),
            new("mailoutanswer", "Answer to a reply", Mailouts, renderer.RenderMailoutAnswer(tenant, invitation, null, SampleAnswer(), owner)),
            new("staffinvite", "Staff invitation", Staff, renderer.RenderStaffInvitation(EmailBrand.Of(tenant), by, MemberRoles.Label(MemberRole.Coordinator), MemberRoles.Describe(MemberRole.Coordinator), link)),
            new("password", "New password", Staff, renderer.RenderPasswordReset(link, 30), FromProduct: true),
            new("changed", "Password changed", Staff, renderer.RenderPasswordChanged(link), FromProduct: true),
        ];
    }
}
