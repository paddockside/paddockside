using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace Paddockside.Web.Services;

/// <summary>
/// Whether Paddockside has been updated since this tab was opened (David, 26 Nov 2026). Looks at one small file when
/// someone comes back to the tab or moves to another page, at most every few minutes; the file needs no sign-in and no
/// database. Never refreshes by itself: something may be half written.
/// </summary>
public sealed class BuildCheck(HttpClient http, NavigationManager navigation, IJSRuntime js) : IDisposable
{
    private static readonly TimeSpan Gap = TimeSpan.FromMinutes(5);

    private DotNetObjectReference<BuildCheck>? _self;
    private DateTime _last;
    private string? _newer;

    public sealed record Served(string Number, string Commit);

    /// <summary>There's a newer one to refresh for.</summary>
    public bool Updated { get; private set; }

    public event Action? Changed;

    /// <summary>Safe to call from every page.</summary>
    public async Task StartAsync()
    {
        if (_self is not null) return;
        _self = DotNetObjectReference.Create(this);
        _last = DateTime.UtcNow; // just loaded, so it's the newest
        navigation.LocationChanged += Moved;
        await js.InvokeVoidAsync("paddockside.onReturn", _self);
    }

    private void Moved(object? sender, LocationChangedEventArgs e) => _ = LookAsync();

    [JSInvokable]
    public async Task LookAsync()
    {
        if (Updated || DateTime.UtcNow - _last < Gap) return;
        _last = DateTime.UtcNow;
        try
        {
            if (await http.GetFromJsonAsync<Served>("version.json") is not { Commit.Length: > 0 } served || served.Commit == BuildInfo.Commit) return;
            // Already refreshed for this one and still not on it (something between keeps the old): asking again won't help.
            if (await js.InvokeAsync<string?>("paddockside.refreshedFor") == served.Commit) return;
            (_newer, Updated) = (served.Commit, true);
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or NotSupportedException or TaskCanceledException)
        {
            // No signal, or part-way through an update: next time.
        }
    }

    public async Task RefreshAsync() => await js.InvokeVoidAsync("paddockside.refreshFor", _newer);

    /// <summary>Not now: the bar goes until the next time it's looked for.</summary>
    public void Later()
    {
        (Updated, _last) = (false, DateTime.UtcNow + TimeSpan.FromMinutes(25));
        Changed?.Invoke();
    }

    public void Dispose()
    {
        navigation.LocationChanged -= Moved;
        _self?.Dispose();
    }
}
