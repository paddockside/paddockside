using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace Paddockside.Infrastructure.Identity;

/// <summary>Whether a password appears in a breached-password list.</summary>
public interface IBreachedPasswordList
{
    Task<bool> ContainsAsync(string password, CancellationToken cancellationToken = default);
}

/// <summary>
/// Have I Been Pwned's Pwned Passwords range API. Only the first five hex characters of the password's SHA-1
/// hash leave the server (k-anonymity); the password itself never does. Responses are padded so their size
/// does not leak the match either.
/// </summary>
public sealed class PwnedPasswordsList(HttpClient http) : IBreachedPasswordList
{
    public async Task<bool> ContainsAsync(string password, CancellationToken cancellationToken = default)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        var (prefix, suffix) = (hash[..5], hash[5..]);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"range/{prefix}");
        request.Headers.Add("Add-Padding", "true");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        foreach (var line in body.Split('\n'))
        {
            var parts = line.Trim().Split(':');
            if (parts.Length == 2 && parts[0].Equals(suffix, StringComparison.OrdinalIgnoreCase) && parts[1] != "0")
                return true;
        }

        return false;
    }
}

/// <summary>
/// Rejects breached passwords (identity-access.md §4.2). Fails closed: if the list cannot be reached the
/// password is not accepted, because setting a staff password is rare and can simply be retried.
/// </summary>
public sealed class BreachedPasswordValidator(IBreachedPasswordList list) : IPasswordValidator<Person>
{
    public async Task<IdentityResult> ValidateAsync(UserManager<Person> manager, Person user, string? password)
    {
        if (string.IsNullOrEmpty(password)) return IdentityResult.Success; // length rules report this

        try
        {
            return await list.ContainsAsync(password)
                ? IdentityResult.Failed(new IdentityError { Code = "PasswordBreached", Description = "This password has appeared in a data breach. Choose a different one." })
                : IdentityResult.Success;
        }
        catch (HttpRequestException)
        {
            return IdentityResult.Failed(new IdentityError { Code = "PasswordCheckUnavailable", Description = "We could not check this password right now. Try again in a minute." });
        }
    }
}
