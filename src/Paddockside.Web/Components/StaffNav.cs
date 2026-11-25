using Paddockside.Web.Services;

namespace Paddockside.Web.Components;

/// <param name="Icon">The inside of a 24-unit line icon.</param>
public sealed record StaffLink(string Href, string Label, string Icon);

/// <summary>Where staff can go, kept once for the rail on a wide screen and the menu on a phone.</summary>
public static class StaffNav
{
    /// <summary>The link that carries a count.</summary>
    public const string Pending = "inbound";

    /// <summary>Always showing, on a phone too.</summary>
    public static readonly IReadOnlyList<StaffLink> First =
    [
        new("today", "Today", "<circle cx=\"12\" cy=\"12\" r=\"9\"></circle><path d=\"M12 7v5l3 2\"></path>"),
        new("horses", "Horses", "<path d=\"M4 20V9l8-5 8 5v11\"></path><path d=\"M9 20v-6h6v6\"></path>"),
    ];

    public static readonly IReadOnlyList<StaffLink> Rest =
    [
        new("social", "Social", "<path d=\"M7 3h10l-1 7a4 4 0 0 1-8 0z\"></path><path d=\"M12 14v6\"></path><path d=\"M8 21h8\"></path>"),
        new("calendar", "Calendar", "<path d=\"M4 6h16v14H4z\"></path><path d=\"M4 10h16\"></path><path d=\"M8 3v4\"></path><path d=\"M16 3v4\"></path>"),
        new("clients", "Clients", "<path d=\"M5 4h12a2 2 0 0 1 2 2v14H7a2 2 0 0 1-2-2z\"></path><path d=\"M5 18a2 2 0 0 1 2-2h12\"></path><circle cx=\"12\" cy=\"9\" r=\"2\"></circle>"),
        new("mailouts", "Mailouts", "<path d=\"M21 3L3 10l7 3 3 7z\"></path><path d=\"M10 13l4-4\"></path>"),
        new(Pending, "Pending", "<path d=\"M4 6h16v12H4z\"></path><path d=\"M4 8l8 5 8-5\"></path>"),
        new("reports", "Reports", "<path d=\"M4 20h16\"></path><path d=\"M7 16v-5\"></path><path d=\"M12 16V7\"></path><path d=\"M17 16v-8\"></path>"),
    ];

    private static readonly StaffLink Members = new("members", "Members", "<circle cx=\"9\" cy=\"8\" r=\"3\"></circle><path d=\"M3 20a6 6 0 0 1 12 0\"></path><path d=\"M16 4a3 3 0 0 1 0 6\"></path><path d=\"M18 14a5 5 0 0 1 3 6\"></path>");
    private static readonly StaffLink Audit = new("audit", "Audit log", "<path d=\"M8 6h12\"></path><path d=\"M8 12h12\"></path><path d=\"M8 18h12\"></path><path d=\"M4 6h.01\"></path><path d=\"M4 12h.01\"></path><path d=\"M4 18h.01\"></path>");
    private static readonly StaffLink Ops = new("ops", "Operator console", "<circle cx=\"12\" cy=\"12\" r=\"3\"></circle><path d=\"M19 12a7 7 0 0 0-.1-1.2l2-1.5-2-3.4-2.4.9a7 7 0 0 0-2-1.2L14 3h-4l-.5 2.6a7 7 0 0 0-2 1.2l-2.4-.9-2 3.4 2 1.5A7 7 0 0 0 5 12a7 7 0 0 0 .1 1.2l-2 1.5 2 3.4 2.4-.9a7 7 0 0 0 2 1.2L10 21h4l.5-2.6a7 7 0 0 0 2-1.2l2.4.9 2-3.4-2-1.5A7 7 0 0 0 19 12z\"></path>");

    /// <summary>Administration, for whoever this is: none for most staff.</summary>
    public static IReadOnlyList<StaffLink> Administration(SessionInfo me)
    {
        var links = new List<StaffLink>();
        if (me.Role == "TenantAdmin") links.AddRange([Members, Audit]);
        if (me.IsOperator && me.SupportUntil is null) links.Add(Ops);
        return links;
    }

    public static string RoleLabel(string role) => role == "TenantAdmin" ? "Tenant admin" : role;
}
