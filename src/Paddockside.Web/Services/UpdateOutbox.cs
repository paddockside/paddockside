using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Paddockside.Web.Services;

/// <summary>One file on an update: where it's up to, and where to play it from on this phone.</summary>
/// <param name="State">waiting, uploading, done or failed.</param>
public sealed record OutboxFile(string Key, string Name, string Kind, long Size, int? Seconds, string State, string? Error, int Percent, string? Url);

/// <param name="State">draft, sending, sent or failed.</param>
/// <param name="Options">Whatever the page wants back when it's opened again, as JSON.</param>
public sealed record OutboxUpdate(string Id, Guid HorseId, string Horse, string State, string? Body, string? EventId, string? Options, string? Error, int Recipients, Guid? SentEventId,
    bool Unkept, List<OutboxFile> Files)
{
    /// <summary>Anything on it yet, so it's worth saying there's one.</summary>
    public bool HasContent => Files.Count > 0 || !string.IsNullOrWhiteSpace(Body);

    /// <summary>How far its files have got, 0 to 100.</summary>
    public int Percent => Files.Count == 0 ? 100 : (int)(Files.Sum(f => f.Percent / 100d * f.Size) / Math.Max(1, Files.Sum(f => f.Size)) * 100);
}

public sealed record OutboxState(bool Online, bool NeedsSignIn, List<OutboxUpdate> Updates);

/// <summary>What the send says, ready for the server; the outbox adds the files and its key.</summary>
public sealed record UpdatePayload(Guid? EventId, string Scope, string? Step, string? Channel, IReadOnlyList<Guid> NamedParties, string Body, PromptRequest? Prompt);

/// <summary>
/// The updates on their way from this phone (wwwroot/js/updates.js does the work): one for the whole app, so what's being
/// sent carries on from any page.
/// </summary>
public sealed class UpdateOutbox(IJSRuntime js, SessionState session) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private DotNetObjectReference<UpdateOutbox>? _self;
    private string? _who;

    public OutboxState State { get; private set; } = new(true, false, []);

    public event Action? Changed;

    /// <summary>Something went wrong with the microphone mid-recording (a call came in): what was said so far is kept.</summary>
    public event Action? RecordingEnded;

    /// <summary>For whoever is signed in. Safe to call from every page.</summary>
    public async Task StartAsync()
    {
        if (session.Current is not { } me) return;
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/updates.js");
        _self ??= DotNetObjectReference.Create(this);
        var who = $"{me.Email}|{me.TenantName}";
        if (_who == who && !State.NeedsSignIn) return;
        _who = who;
        UpdatesChanged(await _module.InvokeAsync<OutboxState>("start", _self, who));
    }

    /// <summary>Nobody's: signing out leaves them on the phone for whoever wrote them.</summary>
    public void SignedOut() => _who = null;

    private async Task<IJSObjectReference> ModuleAsync()
    {
        await StartAsync();
        return _module ?? throw new InvalidOperationException("The outbox is for whoever is signed in.");
    }

    [JSInvokable]
    public void UpdatesChanged(OutboxState state)
    {
        State = state;
        Changed?.Invoke();
    }

    [JSInvokable]
    public void VoiceEnded() => RecordingEnded?.Invoke();

    public OutboxUpdate? Find(string id) => State.Updates.FirstOrDefault(u => u.Id == id);

    private sealed record Draft(string Id, OutboxState State);

    /// <summary>The update being written for this horse, or a new one.</summary>
    public async Task<string> DraftAsync(Guid horseId, string horse)
    {
        var draft = await (await ModuleAsync()).InvokeAsync<Draft>("draft", horseId, horse);
        UpdatesChanged(draft.State);
        return draft.Id;
    }

    public async Task SaveAsync(string id, string body, Guid? eventId, object options) =>
        await (await ModuleAsync()).InvokeVoidAsync("save", id, body, eventId, JsonSerializer.Serialize(options));

    public async Task BindAsync(ElementReference input, string id) => await (await ModuleAsync()).InvokeVoidAsync("bind", input, id);

    public async Task RemoveAsync(string id, string key) => await (await ModuleAsync()).InvokeVoidAsync("remove", id, key);

    public async Task DiscardAsync(string id) => await (await ModuleAsync()).InvokeVoidAsync("discard", id);

    public async Task SendAsync(string id, UpdatePayload payload) => await (await ModuleAsync()).InvokeVoidAsync("send", id, payload);

    public async Task<bool> EditAsync(string id) => await (await ModuleAsync()).InvokeAsync<bool>("edit", id);

    public async Task SeenAsync(string id) => await (await ModuleAsync()).InvokeVoidAsync("seen", id);

    public async Task RetryAsync() => await (await ModuleAsync()).InvokeVoidAsync("retry");

    /// <summary>Null when it's recording; otherwise why not.</summary>
    public async Task<string?> StartVoiceAsync(string id, ElementReference canvas, ElementReference clock) =>
        await (await ModuleAsync()).InvokeAsync<string?>("startVoice", id, canvas, clock, _self);

    public async Task PauseVoiceAsync() => await (await ModuleAsync()).InvokeVoidAsync("pauseVoice");

    public async Task ResumeVoiceAsync() => await (await ModuleAsync()).InvokeVoidAsync("resumeVoice");

    public async Task StopVoiceAsync() => await (await ModuleAsync()).InvokeVoidAsync("stopVoice");

    public async Task CancelVoiceAsync() => await (await ModuleAsync()).InvokeVoidAsync("cancelVoice");

    public async ValueTask DisposeAsync()
    {
        _self?.Dispose();
        if (_module is not null) await _module.DisposeAsync();
    }
}
