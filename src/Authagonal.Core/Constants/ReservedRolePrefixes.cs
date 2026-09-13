namespace Authagonal.Core.Constants;

/// <summary>
/// Role-name prefixes that name authority over the deployment itself, and that therefore may never be
/// granted by data a customer controls.
/// </summary>
/// <remarks>
/// Roles are plain strings, so nothing about the type system stops an organisation membership from
/// listing <c>tenant:admin</c>. Membership rows are customer-scoped data — written by a customer's own
/// administrator once delegated administration exists, and by whatever provisioning connector a
/// customer points at the tenant before then — so a membership able to mint a prefixed role would be a
/// privilege escalation from "may manage my own organisation" to "may administer the tenant".
/// <para>
/// These two prefixes are the product's own namespaces: <c>tenant:owner|admin|developer|support</c>
/// govern the management portal, and <c>platform:owner|sre|billing|support|content</c> govern the
/// operator surface across every tenant. Directly-assigned <see cref="Models.AuthUser.Roles"/> and
/// SCIM group→role mappings are unaffected: those are written by an operator through an authenticated
/// admin surface, which is exactly the authority a membership row does not have.
/// </para>
/// </remarks>
public static class ReservedRolePrefixes
{
    /// <summary>The management-portal namespace.</summary>
    public const string Tenant = "tenant:";

    /// <summary>The cross-tenant operator namespace.</summary>
    public const string Platform = "platform:";

    /// <summary>Every reserved prefix, for callers that filter a role set.</summary>
    public static readonly string[] All = [Tenant, Platform];

    /// <summary>
    /// True when <paramref name="role"/> claims one of the reserved namespaces. Ordinal and
    /// case-insensitive: the prefixes are lowercase by convention, and a filter that let
    /// <c>Tenant:Admin</c> through would be no filter at all.
    /// </summary>
    public static bool IsReserved(string? role) =>
        role is not null && All.Any(p => role.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}
