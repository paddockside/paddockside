namespace Paddockside.Web.Services;

/// <summary>Who is signed in, as far as the browser knows. The Api remains the authority on every request.</summary>
public sealed class SessionState(PaddocksideApi api)
{
    public SessionInfo? Current { get; private set; }

    public event Action? Changed;

    /// <summary>Asks the Api who is signed in; null when nobody is.</summary>
    public async Task<SessionInfo?> RefreshAsync()
    {
        var result = await api.MeAsync();
        Set(result.Succeeded ? result.Value : null);
        return Current;
    }

    public async Task SignOutAsync()
    {
        await api.SignOutAsync();
        Set(null);
    }

    private void Set(SessionInfo? session)
    {
        Current = session;
        Changed?.Invoke();
    }
}
