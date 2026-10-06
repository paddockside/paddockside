using Microsoft.AspNetCore.Components;
using Paddockside.Web.Services;

namespace Paddockside.Web.Pages.Owner;

/// <summary>An owner screen: signed in, or sent to sign in and brought straight back afterwards.</summary>
public abstract class OwnerPage : ComponentBase
{
    [Inject] protected OwnerSessionState Session { get; set; } = null!;

    [Inject] protected NavigationManager Navigation { get; set; } = null!;

    [Inject] protected PaddocksideApi Api { get; set; } = null!;

    /// <summary>True when signed in; otherwise navigates to the sign-in page with this page as the return path.</summary>
    protected async Task<bool> EnsureSignedInAsync()
    {
        if (Session.Current is not null || await Session.RefreshAsync() is not null) return true;
        var here = "/" + Navigation.ToBaseRelativePath(Navigation.Uri);
        Navigation.NavigateTo($"my/sign-in?return={Uri.EscapeDataString(here)}", replace: true);
        return false;
    }
}
