namespace Paddockside.Domain;

public enum SocialKind
{
    Lunch,
    Golf,
    StableVisit,
    Tour,
    Other,
}

public enum SocialStatus
{
    Open,
    Closed,
    Cancelled,
}

/// <summary>
/// A social event (David, 22 Nov 2026): a Christmas lunch, a Sydney golf day, a stable visit, a Melbourne Cup
/// Carnival tour. Not about one horse. It has a date (and an end date for a tour), a venue, and optionally a number of
/// places; people are invited by list, kind of client or by hand (a mailout linked to it), reply with buttons, and are
/// counted like owners tickets. A tour is, for now, just logged with its attendees.
/// </summary>
public sealed class SocialEvent
{
    public const int TitleLength = 200;

    public const int TextLength = 300;

    public const int NotesLength = 4000;

    /// <summary>For EF Core materialisation.</summary>
    private SocialEvent() => Title = null!;

    public SocialEvent(Guid tenantId, string title, SocialKind kind, DateTimeOffset startsAt, DateTimeOffset? endsAt, string? venue, int? places, string? notes,
        DateTimeOffset at)
    {
        TenantId = tenantId;
        Title = null!;
        Change(title, kind, startsAt, endsAt, venue, places, notes);
        CreatedAt = at;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public string Title { get; private set; }

    public SocialKind Kind { get; private set; }

    /// <summary>When it starts; a time of midnight means the day only.</summary>
    public DateTimeOffset StartsAt { get; private set; }

    /// <summary>The last day of a tour or anything over more than a day.</summary>
    public DateTimeOffset? EndsAt { get; private set; }

    public string? Venue { get; private set; }

    /// <summary>How many places there are; null for no limit.</summary>
    public int? Places { get; private set; }

    public string? Notes { get; private set; }

    public SocialStatus Status { get; private set; } = SocialStatus.Open;

    public DateTimeOffset CreatedAt { get; }

    public void Change(string title, SocialKind kind, DateTimeOffset startsAt, DateTimeOffset? endsAt, string? venue, int? places, string? notes)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new DomainException("Give it a name, like \"Christmas Lunch\".");
        if (title.Trim().Length > TitleLength) throw new DomainException($"Keep the name under {TitleLength} characters.");
        if (endsAt is { } end && end < startsAt) throw new DomainException("It can't end before it starts.");
        if (places is < 1) throw new DomainException("Places is how many can come: at least 1, or leave it empty for no limit.");
        if (venue?.Trim().Length > TextLength) throw new DomainException($"Keep the venue under {TextLength} characters.");
        if (notes?.Trim().Length > NotesLength) throw new DomainException("Those notes are too long.");
        (Title, Kind, StartsAt, EndsAt) = (title.Trim(), kind, startsAt, endsAt);
        Venue = string.IsNullOrWhiteSpace(venue) ? null : venue.Trim();
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        Places = places;
    }

    public void Close() => Status = Status == SocialStatus.Cancelled ? Status : SocialStatus.Closed;

    public void Reopen() => Status = SocialStatus.Open;

    public void Cancel() => Status = SocialStatus.Cancelled;

    public static string Label(SocialKind kind) => kind switch
    {
        SocialKind.Lunch => "Lunch or dinner",
        SocialKind.Golf => "Golf",
        SocialKind.StableVisit => "Stable visit",
        SocialKind.Tour => "Tour",
        _ => "Social",
    };
}

public enum AttendeeStatus
{
    Invited,
    Coming,
    Maybe,
    NotComing,

    /// <summary>Said yes when the places had gone: next in line if someone drops out.</summary>
    Waiting,
}

/// <summary>
/// One person on a social event's list, with how many places they want (themselves, a partner, guests: a count and a
/// note, David, 22 Nov 2026). Invited by a mailout, or added by staff; their answer comes from the buttons in the
/// invitation or from staff.
/// </summary>
public sealed class SocialAttendee
{
    public const int NoteLength = 300;

    /// <summary>A sensible ceiling on one booking: a table, not the room.</summary>
    public const int MaxPlaces = 20;

    /// <summary>For EF Core materialisation.</summary>
    private SocialAttendee() { }

    public SocialAttendee(SocialEvent socialEvent, Party party, DateTimeOffset at, Guid? mailoutId = null)
    {
        if (party.TenantId != socialEvent.TenantId) throw new DomainException("That person belongs to another business.");
        TenantId = socialEvent.TenantId;
        SocialEventId = socialEvent.Id;
        PartyId = party.Id;
        MailoutId = mailoutId;
        UpdatedAt = at;
    }

    public Guid Id { get; } = Guid.CreateVersion7();

    public Guid TenantId { get; }

    public Guid SocialEventId { get; }

    public Guid PartyId { get; }

    /// <summary>The invitation they were sent, if they were (its recipient token is their reply link).</summary>
    public Guid? MailoutId { get; private set; }

    public AttendeeStatus Status { get; private set; } = AttendeeStatus.Invited;

    /// <summary>Places wanted: 1 for themselves, more with a partner or guests. Counts only when coming (or waiting).</summary>
    public int Places { get; private set; } = 1;

    /// <summary>"Vegetarian", "+1 is Sarah".</summary>
    public string? Note { get; private set; }

    /// <summary>Who answered last: "them" (from the invitation) or the member of staff.</summary>
    public string? AnsweredBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool Counts => Status == AttendeeStatus.Coming;

    /// <summary>Put on the list by hand and neither sent an invitation nor answered: an invitation can still go to them.</summary>
    public bool NotInvitedYet => MailoutId is null && Status == AttendeeStatus.Invited;

    /// <summary>An invitation went to someone already on the list; only the first counts.</summary>
    public void InvitedBy(Guid mailoutId) => MailoutId ??= mailoutId;

    /// <summary>
    /// Their invitation never left (they unsubscribed, or their address was refused, before it went): they're not invited
    /// after all, and can be.
    /// </summary>
    public void InvitationNotSent(Guid mailoutId)
    {
        if (MailoutId == mailoutId) MailoutId = null;
    }

    /// <summary>
    /// An answer, from the invitation's buttons or from staff. A yes that needs more places than are left goes on the
    /// waiting list (<paramref name="placesLeft"/> excludes this person's own places), but someone already coming keeps the
    /// places they have when they answer again, even if staff fitted them in over the number.
    /// </summary>
    public void Answer(AttendeeStatus answer, int places, string? note, int? placesLeft, string by, DateTimeOffset at)
    {
        if (answer == AttendeeStatus.Invited) throw new DomainException("Choose coming, maybe or not coming.");
        if (places is < 1 or > MaxPlaces) throw new DomainException($"Places is between 1 and {MaxPlaces}.");
        if (note?.Trim().Length > NoteLength) throw new DomainException($"Keep the note under {NoteLength} characters.");
        var keeps = Status == AttendeeStatus.Coming && places <= Places;
        Status = answer == AttendeeStatus.Coming && !keeps && placesLeft is { } left && places > left ? AttendeeStatus.Waiting : answer;
        Places = places;
        if (note is not null) Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        (AnsweredBy, UpdatedAt) = (by, at);
    }

    /// <summary>Staff move someone on the list exactly where they say (off the waiting list, say), whatever the count.</summary>
    public void Set(AttendeeStatus status, int places, string? note, string by, DateTimeOffset at)
    {
        if (places is < 1 or > MaxPlaces) throw new DomainException($"Places is between 1 and {MaxPlaces}.");
        if (note?.Trim().Length > NoteLength) throw new DomainException($"Keep the note under {NoteLength} characters.");
        (Status, Places, Note, AnsweredBy, UpdatedAt) = (status, places, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), by, at);
    }

    public static string Label(AttendeeStatus status) => status switch
    {
        AttendeeStatus.Invited => "Invited",
        AttendeeStatus.Coming => "Coming",
        AttendeeStatus.Maybe => "Maybe",
        AttendeeStatus.NotComing => "Not coming",
        _ => "Waiting list",
    };
}
