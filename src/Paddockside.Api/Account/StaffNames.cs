using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Paddockside.Infrastructure.Persistence;

namespace Paddockside.Api.Account;

/// <summary>
/// Who a message says it's from: the name that person set on their Account page (David, 25 Nov 2026), or their email
/// address, which emails leave out and sign as the business instead.
/// </summary>
public static class StaffNames
{
    public static async Task<string> AuthorAsync(ClaimsPrincipal user, PaddocksideDbContext db, CancellationToken cancellationToken)
    {
        var name = Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var person)
            ? await db.StaffSignatures.AsNoTracking().Where(s => s.PersonId == person).Select(s => s.Name).SingleOrDefaultAsync(cancellationToken)
            : null;
        return name ?? user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name ?? "Staff";
    }
}
