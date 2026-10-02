namespace Paddockside.Application.Tenancy;

/// <summary>
/// The tenant the current unit of work acts for. Set from the signed-in session's active tenant — never from a
/// request parameter, route value or header (non-functional.md §2). Every database read is filtered by it.
/// </summary>
public interface ITenantContext
{
    /// <summary>The active tenant, or null when there is none — in which case every query returns nothing.</summary>
    Guid? TenantId { get; }
}
