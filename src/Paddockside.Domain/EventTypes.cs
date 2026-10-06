namespace Paddockside.Domain;

/// <summary>
/// One step an event type expects (event-library.md §3): its code (what stream items carry as their step), a
/// label, when it is due relative to the key date, and whether owners see it on their stage strip.
/// </summary>
public sealed record ExpectedStep(string Code, string Label, int DaysFromKeyDate, bool ClientVisible = true);

/// <summary>
/// When traffic about an event is expected, in days around the key date (event-library.md §4 "Window"). An event
/// type without one is open-ended.
/// </summary>
public sealed record EventWindow(int DaysBefore, int DaysAfter);

/// <summary>An event type, the steps it expects in order, and its expected window.</summary>
public sealed record EventType(string Code, string Name, IReadOnlyList<ExpectedStep> Steps, EventWindow? Window = null);

public enum StageState
{
    Done,
    Current,
    Upcoming,
    Missing,
}

public sealed record Stage(ExpectedStep Step, StageState State);

/// <summary>
/// The built-in event types. A subset of event-library.md §4 for now; tenant-edited templates come later
/// (event-library.md §7). Codes and offsets follow the catalogue.
/// </summary>
public static class EventTypes
{
    public static readonly EventType RaceStart = new("RaceStart", "Race start",
    [
        new("NOM_FACT", "Nominated", -7),
        new("TRAINER_PLAN", "Trainer's plan", -14, ClientVisible: false),
        new("ACC_FACT", "Accepted", -3),
        new("BARRIER", "Barrier", -2),
        new("TICKETS", "Owners tickets", -2),
        new("TRAINER_PRE", "Trainer's pre-race", -1, ClientVisible: false),
        new("STAFF_PRE", "Pre-race update", -1),
        new("RESULT_FACT", "Result", 0),
        new("DAY_AFTER", "Day after", 1, ClientVisible: false),
    ], new EventWindow(DaysBefore: 28, DaysAfter: 21));

    public static readonly EventType BarrierTrial = new("BarrierTrial", "Barrier trial",
    [
        new("TRIAL_ENTRY", "Entered", -3),
        new("TRIAL_RESULT", "Trialled", 0),
        new("TRAINER_REPORT", "Trainer's report", 1),
    ], new EventWindow(DaysBefore: 7, DaysAfter: 7));

    public static readonly EventType Veterinary = new("Veterinary", "Veterinary",
    [
        new("VET_BOOKED", "Booked", -2),
        new("VET_REPORT", "Vet's report", 0),
    ]);

    public static readonly EventType General = new("General", "General", []);

    public static readonly IReadOnlyList<EventType> All = [RaceStart, BarrierTrial, Veterinary, General];

    public static EventType Find(string code) => All.FirstOrDefault(t => t.Code == code) ?? General;

    /// <summary>
    /// Where an event is up to. A step is done when any item carries its code. An undone step is missing once its
    /// whole due day has passed (a step due Thursday is not late until Friday). The first remaining step is current;
    /// the rest are upcoming. A closed or cancelled event has no current step.
    /// </summary>
    public static IReadOnlyList<Stage> Stages(EventType type, DateTimeOffset? keyDate, IEnumerable<string?> stepCodesSeen, DateTimeOffset now, bool finished = false)
    {
        var seen = stepCodesSeen.Where(c => c is not null).ToHashSet();
        var currentAssigned = finished;
        var stages = new List<Stage>();
        foreach (var step in type.Steps)
        {
            StageState state;
            if (seen.Contains(step.Code)) state = StageState.Done;
            else if (keyDate is { } key && key.Date.AddDays(step.DaysFromKeyDate + 1) <= now.ToOffset(key.Offset).DateTime) state = StageState.Missing;
            else if (!currentAssigned) { state = StageState.Current; currentAssigned = true; }
            else state = StageState.Upcoming;
            stages.Add(new Stage(step, state));
        }

        return stages;
    }
}
