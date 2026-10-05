namespace Paddockside.Web.Components;

// What the components display. Shaped for the screen, not the database: dates arrive already written in words
// ("Saturday 17 October, Randwick"), because owners read words, not 17/10 (design-system.md §2.0).

/// <summary>Which side is looking. Owners get larger type and never see staff-only detail.</summary>
public enum Side
{
    Staff,
    Owner,
}

/// <summary>Who an item is for (ownership-model.md §3). Always shown as colour plus label.</summary>
public enum Scope
{
    Internal,
    Owners,
    OwnersAtTheTime,
    NamedParties,
    Trainer,
}

/// <summary>What an item is (data-model.md §2). Facts, messages, media and notes must look different.</summary>
public enum Kind
{
    Fact,
    Message,
    Media,
    Note,
}

public sealed record TenantOption(string Id, string Name);

public sealed record HorseCardModel(
    string Name,
    string SexAge,
    string? Trainer,
    string? NextKeyDate,
    int Unread,
    string? PhotoUrl = null,
    string? Subtitle = null,
    string? Href = null);

public enum StepState
{
    Done,
    Current,
    Upcoming,
    Missing,
}

/// <param name="ClientVisible">Whether owners see this step; the client strip shows only those.</param>
public sealed record StageStep(string Label, StepState State, string? Detail = null, bool ClientVisible = true);

public enum EventStatus
{
    Draft,
    Open,
    Closed,
    Cancelled,
}

public sealed record EventCardModel(
    string Type,
    string Title,
    string KeyDate,
    EventStatus Status,
    IReadOnlyList<StageStep> Steps,
    int Unread,
    string LastActivity,
    string? Href = null);

/// <param name="WasValue">The value before a correction, shown struck through.</param>
public sealed record FactField(string Label, string Value, string? WasValue = null);

/// <param name="Correction">When and by whom the fact was corrected, in words; null if never corrected.</param>
public sealed record FactModel(
    string Label,
    string Source,
    string ReceivedAt,
    Scope Scope,
    IReadOnlyList<FactField> Fields,
    string? Step = null,
    string? Correction = null);

public enum DeliveryStatus
{
    Queued,
    Sent,
    Delivered,
    Opened,
    Replied,
    Bounced,
    Suppressed,
}

public sealed record Recipient(string Name, string Channel, DeliveryStatus Status);

/// <param name="IsViewer">This reply was written by the owner looking at it (the only replies an owner sees).</param>
public sealed record Reply(string Author, string Channel, string At, string Body, bool IsViewer = false);

public sealed record MessageModel(
    string Author,
    string At,
    Scope Scope,
    string Audience,
    string Body,
    IReadOnlyList<Reply> Replies,
    IReadOnlyList<Recipient> Recipients,
    string? Step = null,
    string? Title = null);

/// <summary>The one audience class a message goes to (data-model.md D12), or an internal note.</summary>
public enum Audience
{
    Owners,
    Trainer,
    InternalNote,
}

public enum Channel
{
    Preferred,
    Email,
    Sms,
    PortalOnly,
}

/// <summary>A party offered in the compose box (a current owner).</summary>
public sealed record PartyOption(string Id, string Name);

public sealed record ComposeDraft(
    Audience Audience,
    Scope Scope,
    string? Step,
    Channel Channel,
    /// <summary>Ids of the chosen parties (from <see cref="PartyOption.Id"/>).</summary>
    IReadOnlyList<string> NamedParties,
    string Body);

/// <summary>Labels and colour classes, written out in full so Tailwind finds every class it must build.</summary>
public static class Marks
{
    public static string Label(Scope scope) => scope switch
    {
        Scope.Internal => "Internal",
        Scope.Owners => "Owners",
        Scope.OwnersAtTheTime => "Owners at the time",
        Scope.NamedParties => "Named parties",
        Scope.Trainer => "Trainer",
        _ => scope.ToString(),
    };

    public static string Describe(Scope scope) => scope switch
    {
        Scope.Internal => "Staff only",
        Scope.Owners => "Everyone with a current share, now and in future",
        Scope.OwnersAtTheTime => "Only the owners on this date, never later owners",
        Scope.NamedParties => "Only the people named",
        Scope.Trainer => "The trainer and staff, never owners",
        _ => string.Empty,
    };

    public static string Classes(Scope scope) => scope switch
    {
        Scope.Internal => "bg-scope-internal-bg text-scope-internal-fg",
        Scope.Owners => "bg-scope-owners-bg text-scope-owners-fg",
        Scope.OwnersAtTheTime => "bg-scope-owners-at-the-time-bg text-scope-owners-at-the-time-fg",
        Scope.NamedParties => "bg-scope-named-parties-bg text-scope-named-parties-fg",
        Scope.Trainer => "bg-scope-trainer-bg text-scope-trainer-fg",
        _ => string.Empty,
    };

    public static string Label(Kind kind) => kind switch
    {
        Kind.Fact => "Fact",
        Kind.Message => "Message",
        Kind.Media => "Photo or video",
        Kind.Note => "Staff note",
        _ => kind.ToString(),
    };

    public static string Classes(Kind kind) => kind switch
    {
        Kind.Fact => "bg-kind-fact-bg text-kind-fact-fg",
        Kind.Message => "bg-kind-message-bg text-kind-message-fg",
        Kind.Media => "bg-kind-media-bg text-kind-media-fg",
        Kind.Note => "bg-kind-note-bg text-kind-note-fg",
        _ => string.Empty,
    };

    public static string Label(DeliveryStatus status) => status switch
    {
        DeliveryStatus.Queued => "Waiting to send",
        DeliveryStatus.Sent => "Sent",
        DeliveryStatus.Delivered => "Delivered",
        DeliveryStatus.Opened => "Opened",
        DeliveryStatus.Replied => "Replied",
        DeliveryStatus.Bounced => "Bounced",
        DeliveryStatus.Suppressed => "Not sent (unsubscribed)",
        _ => status.ToString(),
    };

    public static bool IsProblem(DeliveryStatus status) => status is DeliveryStatus.Bounced or DeliveryStatus.Suppressed;
}
