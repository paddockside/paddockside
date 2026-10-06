namespace Paddockside.Web.Services;

/// <summary>The signed-in owner, as far as the browser knows. The Api remains the authority on every request.</summary>
public sealed class OwnerSessionState(PaddocksideApi api)
{
    public OwnerSession? Current { get; private set; }

    public event Action? Changed;

    /// <summary>Asks the Api who is signed in; null when nobody is (or it is a staff session).</summary>
    public async Task<OwnerSession?> RefreshAsync()
    {
        var result = await api.OwnerSessionAsync();
        Set(result.Succeeded ? result.Value : null);
        return Current;
    }

    public async Task SwitchAsync(Guid tenantId)
    {
        if ((await api.SwitchTenantAsync(tenantId)).Succeeded) await RefreshAsync();
    }

    public async Task SignOutAsync()
    {
        await api.SignOutAsync();
        Set(null);
    }

    private void Set(OwnerSession? session)
    {
        Current = session;
        Changed?.Invoke();
    }
}
