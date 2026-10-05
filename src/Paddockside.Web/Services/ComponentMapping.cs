using Paddockside.Web.Components;

namespace Paddockside.Web.Services;

/// <summary>Turns API responses into the components' display models.</summary>
public static class ComponentMapping
{
    public static HorseCardModel ToCard(this HorseDetail h) =>
        new(h.Name, h.SexAge ?? "", Trainer: null, h.NextKeyDate, Unread: 0, Subtitle: h.Pedigree);

    public static EventCardModel ToCard(this EventSummary e, string? href = null) => new(
        e.Type,
        e.Title,
        e.KeyDate,
        Enum.TryParse<EventStatus>(e.Status, out var status) ? status : EventStatus.Open,
        e.Steps.Select(s => new StageStep(s.Label, ToStepState(s.State), s.Detail, s.ClientVisible)).ToList(),
        Unread: 0,
        e.LastActivity,
        href);

    public static FactModel ToFact(this ItemView i) => new(
        i.Title ?? "Fact",
        i.Source ?? "Unknown source",
        i.At,
        ToScope(i.Scope),
        i.Fields.Select(f => new Components.FactField(f.Label, f.Value, f.Was)).ToList(),
        i.Step,
        i.Correction);

    public static MessageModel ToMessage(this ItemView i) => new(
        i.Author ?? "Unknown sender",
        i.At,
        ToScope(i.Scope),
        i.Audience ?? "",
        i.Body,
        i.Replies.Select(r => new Reply(r.Author, r.Channel, r.At, r.Body)).ToList(),
        i.Recipients.Select(r => new Recipient(r.Name, r.Channel,
            Enum.TryParse<DeliveryStatus>(r.Status, out var s) ? s : DeliveryStatus.Queued)).ToList(),
        i.Step,
        i.Title);

    public static Scope ToScope(string scope) => Enum.TryParse<Scope>(scope, out var s) ? s : Scope.Internal;

    private static StepState ToStepState(string state) => state switch
    {
        "Done" => StepState.Done,
        "Current" => StepState.Current,
        "Missing" => StepState.Missing,
        _ => StepState.Upcoming,
    };
}
