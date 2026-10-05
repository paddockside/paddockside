using Paddockside.Web.Components;

namespace Paddockside.Web.Pages.DesignSample;

/// <summary>
/// Sample data for the /design page: one race start for "Autumn Ridge", following the mockup notes
/// (event-page-mockup-notes.md): Kate on staff, Margaret as an owner, owners tickets asked for by structured
/// prompt, and an acceptance corrected by the racing body. Fictional throughout; the mockup canvas itself is not
/// in the repo, so correct anything here that differs from it.
/// </summary>
public static class AutumnRidge
{
    public const string Tenant = "Laurel Oak Bloodstock";

    public static readonly IReadOnlyList<TenantOption> OtherTenants = [new("hillcrest", "Hillcrest Syndications")];

    public static readonly IReadOnlyList<PartyOption> Owners =
        [new("margaret", "Margaret Hale"), new("tom", "Tom Okafor"), new("priya", "Priya Nair"), new("graham", "Graham Lowe"), new("house", "Laurel Oak house share")];

    public static readonly HorseCardModel Horse = new(
        Name: "Autumn Ridge",
        SexAge: "4yo bay mare",
        Trainer: "J. Hartley",
        NextKeyDate: "Saturday 17 October, Randwick",
        Unread: 3,
        Subtitle: "Sovereign Star x Miss Finland",
        Href: "#autumn-ridge");

    public static readonly HorseCardModel Yearling = new(
        Name: "Lot 231 (unnamed)",
        SexAge: "Yearling colt",
        Trainer: null,
        NextKeyDate: null,
        Unread: 0,
        Subtitle: "Sovereign Star x Northern Lark");

    public static readonly IReadOnlyList<StageStep> RaceStartSteps =
    [
        new("Nominated", StepState.Done, "Sat 3 Oct"),
        new("Trainer's plan", StepState.Done, ClientVisible: false),
        new("Accepted", StepState.Done, "Wed 14 Oct"),
        new("Barrier", StepState.Done, "9"),
        new("Owners tickets", StepState.Current, "Replies by Thursday"),
        new("Trainer's pre-race", StepState.Missing, "Due Friday", ClientVisible: false),
        new("Race day", StepState.Upcoming, "Sat 17 Oct"),
        new("Result", StepState.Upcoming),
        new("Day after", StepState.Upcoming, ClientVisible: false),
    ];

    public static readonly EventCardModel RaceStart = new(
        Type: "Race start",
        Title: "Randwick, Race 6 · 1400 m",
        KeyDate: "Saturday 17 October, Randwick",
        Status: EventStatus.Open,
        Steps: RaceStartSteps,
        Unread: 3,
        LastActivity: "Last update 2 hours ago: Margaret replied about tickets",
        Href: "#race-start");

    public static readonly EventCardModel TrialClosed = new(
        Type: "Barrier trial",
        Title: "Rosehill trial, heat 4",
        KeyDate: "Tuesday 22 September, Rosehill",
        Status: EventStatus.Closed,
        Steps: [new("Entered", StepState.Done), new("Trialled", StepState.Done), new("Trainer's report", StepState.Done)],
        Unread: 0,
        LastActivity: "Closed by Kate on 25 September");

    public static readonly EventCardModel VetCancelled = new(
        Type: "Veterinary",
        Title: "Pre-season check",
        KeyDate: "Monday 28 September",
        Status: EventStatus.Cancelled,
        Steps: [],
        Unread: 0,
        LastActivity: "Cancelled by Kate: done during the trial instead");

    public static readonly FactModel Acceptance = new(
        Label: "Acceptance",
        Source: "Racing NSW",
        ReceivedAt: "Wednesday 14 October, 11:02 am",
        Scope: Scope.Owners,
        Fields:
        [
            new("Race", "Randwick R6, 1400 m, Benchmark 78"),
            new("Barrier", "9", WasValue: "7"),
            new("Weight", "56.5 kg"),
            new("Jockey", "To be confirmed"),
        ],
        Step: "Accepted",
        Correction: "Corrected by Racing NSW on Wednesday 14 October, 4:15 pm (barrier redraw after a scratching)");

    public static readonly FactModel Nomination = new(
        Label: "Nomination",
        Source: "Racing NSW",
        ReceivedAt: "Saturday 3 October, 9:40 am",
        Scope: Scope.Owners,
        Fields: [new("Race", "Randwick R6, 1400 m"), new("Nominated by", "J. Hartley")],
        Step: "Nominated");

    public static readonly MessageModel TicketsMessage = new(
        Author: "Kate (Laurel Oak)",
        At: "Wednesday 14 October, 5:30 pm",
        Scope: Scope.Owners,
        Audience: "Owners (5)",
        Title: "Owners tickets for Saturday",
        Step: "Owners tickets",
        Body: "Autumn Ridge is accepted for Race 6 at Randwick on Saturday, from barrier 9.\n\nThe club has given us 8 owners tickets. Reply with how many you would like by Thursday 5 pm and we will confirm on Friday.",
        Replies:
        [
            new("Margaret Hale", "email", "Wednesday 6:12 pm", "Two please, for me and my sister. Can we see her in the mounting yard?", IsViewer: true),
            new("Tom Okafor", "text message", "Wednesday 7:40 pm", "Just me this time, thanks."),
            new("Priya Nair", "email", "Thursday 8:05 am", "None for us, we will watch from home. Good luck!"),
        ],
        Recipients:
        [
            new("Margaret Hale", "Email and text", DeliveryStatus.Replied),
            new("Tom Okafor", "Text", DeliveryStatus.Replied),
            new("Priya Nair", "Email", DeliveryStatus.Replied),
            new("Graham Lowe", "Email", DeliveryStatus.Opened),
            new("Laurel Oak house share", "Email", DeliveryStatus.Bounced),
        ]);

    public static readonly IReadOnlyList<string> ComposeSteps =
        ["Owners tickets", "Trainer's pre-race", "Staff pre-race update", "Result", "Day after"];
}
