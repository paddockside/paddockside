namespace Paddockside.Domain;

/// <summary>
/// One kind of update an owner can be told about (messaging-channels.md §6). Product library items: the tenant
/// will later enable, relabel and set defaults; until then the product's defaults apply everywhere.
/// </summary>
/// <param name="Important">
/// Sent whatever the owner has switched off (at least one way always stays on) and falls back across channels.
/// "Important" categories are the service itself, so they carry no opt-out (§7).
/// </param>
public sealed record NotificationCategory(string Code, string Label, string Description, IReadOnlySet<DeliveryChannel> Defaults, bool Important);

/// <summary>An owner's own choice for one category and channel, kept only where it differs from the default.</summary>
public sealed class NotificationPreference
{
    /// <summary>For EF Core materialisation.</summary>
    private NotificationPreference() => Category = null!;

    public NotificationPreference(Party party, string category, DeliveryChannel channel, bool enabled, DateTimeOffset at)
    {
        if (NotificationCategories.Find(category) is null) throw new DomainException($"No notification category '{category}'.");
        if (channel is not (DeliveryChannel.Email or DeliveryChannel.Sms)) throw new DomainException("Owners choose email or text; the portal always has everything.");
        TenantId = party.TenantId;
        PartyId = party.Id;
        Category = category;
        Channel = channel;
        Enabled = enabled;
        UpdatedAt = at;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid PartyId { get; }

    public string Category { get; }

    public DeliveryChannel Channel { get; }

    public bool Enabled { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public void Set(bool enabled, DateTimeOffset at) => (Enabled, UpdatedAt) = (enabled, at);
}

/// <summary>
/// The product's categories, and how a message finds its category and an owner's channels. The portal is not a
/// channel here: everything an owner may see is always in the portal; these decide what is also pushed to them.
/// </summary>
public static class NotificationCategories
{
    private static readonly IReadOnlySet<DeliveryChannel> None = new HashSet<DeliveryChannel>();
    private static readonly IReadOnlySet<DeliveryChannel> Email = new HashSet<DeliveryChannel> { DeliveryChannel.Email };
    private static readonly IReadOnlySet<DeliveryChannel> Text = new HashSet<DeliveryChannel> { DeliveryChannel.Sms };
    private static readonly IReadOnlySet<DeliveryChannel> Both = new HashSet<DeliveryChannel> { DeliveryChannel.Email, DeliveryChannel.Sms };

    public const string Entries = "entries";
    public const string Acceptances = "acceptances";
    public const string RaceDayChanges = "raceday-changes";
    public const string RaceDayArrangements = "raceday-arrangements";
    public const string Results = "results";
    public const string StableUpdates = "stable-updates";
    public const string Media = "media";
    public const string Ownership = "ownership";
    public const string SalesBreedingCare = "sales-breeding-care";
    public const string News = "news";

    /// <summary>In the order owners see them. Push (§5) joins the defaults when the app has it.</summary>
    public static readonly IReadOnlyList<NotificationCategory> All =
    [
        new(Acceptances, "Acceptances, barriers and weights", "When your horse is accepted for a race, and its barrier and weight.", Both, Important: true),
        new(RaceDayChanges, "Race-day changes", "Scratchings and jockey changes on the day.", Text, Important: true),
        new(RaceDayArrangements, "Race-day arrangements", "Owners tickets, dining and transport.", Email, Important: true),
        new(Results, "Results and stewards' reports", "How your horse ran, as soon as it's official.", Both, Important: true),
        new(Ownership, "Ownership and accounts", "Changes to your share, and administrative notices.", Email, Important: true),
        new(Entries, "Nominations and entries", "When your horse is nominated or entered for a race or trial.", None, Important: false),
        new(StableUpdates, "Trainer and stable updates", "Notes from the trainer and the team between races.", Email, Important: false),
        new(Media, "New photos and videos", "When new photos or videos of your horse are added.", None, Important: false),
        new(SalesBreedingCare, "Sales, breeding and care", "Vet visits, spells, sales and breeding news.", Email, Important: false),
        new(News, "General news", "Newsletters and news that isn't about one horse.", Email, Important: false),
    ];

    public static NotificationCategory? Find(string? code) => All.FirstOrDefault(c => c.Code == code);

    /// <summary>
    /// The category of a message, from the step it belongs to (event-library.md steps). A message on no step is a
    /// stable update; on a care event it is care news.
    /// </summary>
    public static NotificationCategory For(StreamItem message, Event? @event) => Find(message.StepCode switch
    {
        "NOM_FACT" or "TRIAL_ENTRY" => Entries,
        "ACC_FACT" or "BARRIER" => Acceptances,
        "TICKETS" or "STAFF_PRE" => RaceDayArrangements,
        "RESULT_FACT" or "TRIAL_RESULT" => Results,
        "VET_BOOKED" or "VET_REPORT" => SalesBreedingCare,
        _ when @event?.EventType == EventTypes.Veterinary.Code => SalesBreedingCare,
        _ => StableUpdates,
    })!;

    /// <summary>
    /// How one owner hears about a message in <paramref name="category"/>: their choices over the defaults, limited
    /// to the ways we can reach them. An important category never comes back empty while any way exists: if their
    /// choices leave nothing that works, the defaults apply, then whatever does work.
    /// </summary>
    /// <param name="smsAvailable">Whether the tenant can send texts at all (false until a provider is configured).</param>
    public static IReadOnlySet<DeliveryChannel> ChannelsFor(NotificationCategory category, IEnumerable<NotificationPreference> choices,
        bool hasEmail, bool hasMobile, bool smsAvailable)
    {
        bool Reachable(DeliveryChannel c) => c switch
        {
            DeliveryChannel.Email => hasEmail,
            DeliveryChannel.Sms => hasMobile && smsAvailable,
            _ => false,
        };

        var chosen = Chosen(category, choices);
        var result = chosen.Where(Reachable).ToHashSet();
        if (result.Count > 0 || !category.Important) return result;

        result = category.Defaults.Where(Reachable).ToHashSet();
        if (result.Count > 0) return result;
        return new[] { DeliveryChannel.Email, DeliveryChannel.Sms }.Where(Reachable).Take(1).ToHashSet();
    }

    /// <summary>What they have switched on for <paramref name="category"/>, defaults included, before reachability.</summary>
    public static IReadOnlySet<DeliveryChannel> Chosen(NotificationCategory category, IEnumerable<NotificationPreference> choices)
    {
        var mine = choices.Where(p => p.Category == category.Code).ToList();
        return new[] { DeliveryChannel.Email, DeliveryChannel.Sms }
            .Where(c => mine.FirstOrDefault(p => p.Channel == c) is { } p ? p.Enabled : category.Defaults.Contains(c))
            .ToHashSet();
    }
}
