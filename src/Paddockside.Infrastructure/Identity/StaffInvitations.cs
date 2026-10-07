using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Paddockside.Application.Messaging;
using Paddockside.Domain;
using Paddockside.Infrastructure.Email;

namespace Paddockside.Infrastructure.Identity;

public sealed record InvitationSent(Guid Id, bool Sent, string? Problem);

/// <summary>
/// Creates and emails an invitation (identity-access.md §6): to a tenant's staff with a role, or to the product's
/// operators. Inviting the same address again replaces the earlier invitation, so only the newest link works.
/// Shared by the Members page and the operator console.
/// </summary>
public sealed class StaffInvitations(
    PaddocksideIdentityDbContext identity,
    IEmailSender email,
    OwnerEmailRenderer renderer,
    IOptions<EmailOptions> options,
    TimeProvider clock)
{
    public const string OperatorRoleLabel = "Operator";

    public const string OperatorRoleDescription =
        "Runs Paddockside itself: sees each business's account and health, never their horses, owners or messages.";

    /// <summary>Invites <paramref name="address"/> to a tenant's staff, or as an operator when <paramref name="tenantId"/> is null.</summary>
    public async Task<InvitationSent> SendAsync(Guid? tenantId, string tenantName, string address, MemberRole role,
        Guid inviterId, string inviterName, CancellationToken cancellationToken)
    {
        if (options.Value.PortalBaseUrl is not { Length: > 0 } baseUrl)
            throw new InvalidOperationException("Invitations need the portal address configured (Email:PortalBaseUrl).");

        var forOperator = tenantId is null;
        var now = clock.GetUtcNow();
        foreach (var earlier in await identity.StaffInvitations
                     .Where(i => i.TenantId == tenantId && i.ForOperator == forOperator && i.Email == address && i.AcceptedAt == null && i.RevokedAt == null)
                     .ToListAsync(cancellationToken))
            earlier.RevokedAt = now;

        var token = SignInToken.NewLinkToken();
        var invitation = new StaffInvitation
        {
            TenantId = tenantId,
            TenantName = tenantName,
            ForOperator = forOperator,
            Email = address,
            Role = role,
            InvitedByPersonId = inviterId,
            InvitedByName = inviterName,
            TokenHash = SignInToken.Hash(token),
            CreatedAt = now,
            ExpiresAt = now + StaffInvitation.Lifetime,
        };
        identity.StaffInvitations.Add(invitation);
        await identity.SaveChangesAsync(cancellationToken);

        var (label, description) = forOperator ? (OperatorRoleLabel, OperatorRoleDescription) : (MemberRoles.Label(role), MemberRoles.Describe(role));
        var rendered = renderer.RenderStaffInvitation(tenantName, inviterName, label, description, $"{baseUrl.TrimEnd('/')}/join#{token}");
        var result = await email.SendAsync(new OutboundEmail(options.Value.FromAddress, Senders.For(tenantName), address, string.Empty, inviterName,
            rendered.Subject, rendered.Html, rendered.Text, new Dictionary<string, string> { ["purpose"] = forOperator ? "operator-invitation" : "staff-invitation" },
            TrackOpens: false), cancellationToken);

        return new InvitationSent(invitation.Id, result.Accepted, result.Accepted ? null : $"The email was not sent: {result.Error}. Cancel it and try again.");
    }
}
