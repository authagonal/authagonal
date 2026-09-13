using Authagonal.Core.Models;
using Authagonal.Core.Stores;
using Authagonal.Protocol;

namespace Authagonal.Server.Services;

/// <summary>
/// Decides which <see cref="Organization"/> an authorization request is for, and whether the user
/// and client are allowed it. One place, because the answer has to be identical at
/// <c>/connect/authorize</c>, on every refresh rotation and on the device grant — a request that
/// switched organisation across a refresh would hand a relying party another customer's data.
/// </summary>
/// <remarks>
/// Precedence, highest first:
/// <list type="number">
///   <item>The <c>organization</c> authorize parameter (or, on a refresh, the organisation the grant
///   was issued for), resolved as a slug and then as an id.</item>
///   <item><see cref="OAuthClient.RestrictedToOrganizationIds"/> when it holds exactly one entry — a
///   per-customer application names its organisation once, in registration, and its relying party
///   never sends a parameter.</item>
///   <item><see cref="AuthUser.OrganizationId"/>, the account's own stored organisation.</item>
/// </list>
/// <para>
/// The third rule is what makes this change invisible to a deployment that has no organisations. A
/// stored organization id that resolves to no record is passed through verbatim as <c>org_id</c> —
/// exactly what the server did before this type existed — with no slug, no name and no membership
/// check. Organisations become load-bearing only once someone creates one.
/// </para>
/// <para>
/// Membership is demanded only for an EXPLICIT selection (rules 1 and 2). When the organisation came
/// from the account's own record, that record is the assertion of belonging, and demanding a second
/// one would lock out every pre-existing user the moment an operator created the matching
/// organisation. <see cref="Organization.Enabled"/> and the client restriction are enforced however
/// the organisation was chosen.
/// </para>
/// </remarks>
public sealed class OrganizationSelector(
    IOrganizationStore organizations,
    IOrganizationMembershipStore memberships)
{
    /// <summary>
    /// Resolve and authorise the organisation for this request.
    /// </summary>
    /// <param name="requestedOrganization">
    /// The <c>organization</c> parameter's value (a slug or an id), or the organisation carried
    /// forward from a prior grant on the refresh path. Null when the request named none.
    /// </param>
    /// <exception cref="OrganizationAccessDeniedException">
    /// The request named an organisation that does not exist, is disabled, is not permitted for this
    /// client, or that the user is not an active member of.
    /// </exception>
    public async Task<OrganizationSelection> SelectAsync(
        AuthUser user,
        OAuthClient? client,
        string? requestedOrganization,
        CancellationToken ct = default)
    {
        var restricted = client?.RestrictedToOrganizationIds ?? [];

        Organization? organization = null;
        string? legacyOrganizationId = null;
        var explicitlySelected = false;

        if (!string.IsNullOrWhiteSpace(requestedOrganization))
        {
            explicitlySelected = true;
            organization = await ResolveAsync(requestedOrganization, ct);
            if (organization is null)
            {
                // Named and absent is a refusal, never a silent fall-through to the account default:
                // a relying party that asked for organisation A must not be handed a token for B.
                throw new OrganizationAccessDeniedException(
                    OidcRejection.AccessDenied,
                    $"No organization matches '{requestedOrganization}'.");
            }
        }
        else if (restricted.Count == 1)
        {
            explicitlySelected = true;
            organization = await organizations.GetAsync(restricted[0], ct);
            if (organization is null)
            {
                throw new OrganizationAccessDeniedException(
                    OidcRejection.AccessDenied,
                    $"Client '{client?.ClientId}' is restricted to organization '{restricted[0]}', which does not exist.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(user.OrganizationId))
        {
            organization = await ResolveAsync(user.OrganizationId, ct);
            // Pre-organizations account: the id is a bare string with no record behind it. Emit it as
            // org_id and gate nothing, which is what this server has always done with the field.
            if (organization is null) legacyOrganizationId = user.OrganizationId;
        }

        if (organization is null && legacyOrganizationId is null)
        {
            // A client that serves several named organisations cannot be told which one by silence.
            // account_selection_required is the OIDC code for exactly this, and it is actionable: the
            // relying party retries with an organization parameter.
            if (restricted.Count > 1)
            {
                throw new OrganizationAccessDeniedException(
                    OidcRejection.AccountSelectionRequired,
                    $"Client '{client?.ClientId}' serves several organizations and the request named none.");
            }

            return OrganizationSelection.None;
        }

        if (legacyOrganizationId is not null)
        {
            RequireClientPermits(client, restricted, legacyOrganizationId, legacyOrganizationId);
            return new OrganizationSelection { OrganizationId = legacyOrganizationId };
        }

        // A named organisation that resolved is only reached below; `explicitlySelected` is already set.

        if (!organization!.Enabled)
        {
            throw new OrganizationAccessDeniedException(
                OidcRejection.AccessDenied,
                $"Organization '{organization.Slug}' is disabled.");
        }

        RequireClientPermits(client, restricted, organization.Id, organization.Slug);

        // Read once, for two purposes. The gate needs it when RequireMembershipForTokens is on; the
        // roles claim needs it whether or not the gate is — fetching it only for the gated case would
        // mean an organisation that chose not to gate could never grant a role, which is not what
        // turning the gate off says.
        //
        // Only for an EXPLICIT selection, the same asymmetry the gate has. An organisation inherited
        // from the account was never proven to be the one this request acts for, so its roles are not
        // this request's authority.
        OrganizationMembership? membership = null;
        if (explicitlySelected)
            membership = await memberships.GetAsync(organization.Id, user.Id, ct);

        var activeMember = membership is not null &&
            string.Equals(membership.Status, MembershipStatus.Active, StringComparison.Ordinal);

        if (explicitlySelected && organization.RequireMembershipForTokens && !activeMember)
        {
            throw new OrganizationAccessDeniedException(
                OidcRejection.AccessDenied,
                $"User is not an active member of organization '{organization.Slug}'.");
        }

        return new OrganizationSelection
        {
            OrganizationId = organization.Id,
            Slug = organization.Slug,
            DisplayName = organization.DisplayName,
            ExplicitlySelected = explicitlySelected,
            // An invited-but-unaccepted or suspended membership grants nothing, exactly as it
            // authorises nothing. The roles come off the row keyed by THIS organisation, so a role
            // held in one organisation cannot reach a token issued for another.
            MembershipRoles = activeMember && membership!.Roles.Count > 0
                ? [.. membership.Roles]
                : null,
        };
    }

    /// <summary>Slug first, then id — the slug is what a relying party sends, so it must not cost a
    /// failed id lookup on every request.</summary>
    private async Task<Organization?> ResolveAsync(string slugOrId, CancellationToken ct) =>
        await organizations.GetBySlugAsync(slugOrId, ct) ?? await organizations.GetAsync(slugOrId, ct);

    private static void RequireClientPermits(
        OAuthClient? client, IReadOnlyList<string> restricted, string organizationId, string label)
    {
        if (restricted.Count == 0) return;
        if (restricted.Contains(organizationId, StringComparer.Ordinal)) return;

        throw new OrganizationAccessDeniedException(
            OidcRejection.AccessDenied,
            $"Client '{client?.ClientId}' is not permitted for organization '{label}'.");
    }
}

/// <summary>
/// The organisation an authorization request resolved to, and the values it puts on a token.
/// <see cref="None"/> when the request selected none — the state every request was in before
/// organisations existed.
/// </summary>
public sealed record OrganizationSelection
{
    /// <summary>The <c>org_id</c> claim. Null when no organisation was selected.</summary>
    public string? OrganizationId { get; init; }

    /// <summary>The <c>org_slug</c> claim. Null for a legacy organization id with no record behind
    /// it — there is no slug to emit, and inventing one would be a claim the server cannot back.</summary>
    public string? Slug { get; init; }

    /// <summary>The <c>org_name</c> claim. Null for the same reason as <see cref="Slug"/>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// True when the request NAMED this organisation rather than inheriting it from the account.
    /// Rides onto <see cref="OidcSubject.OrganizationExplicitlySelected"/> so a refresh rotation can
    /// tell a selection it must preserve and re-check from one it should re-derive.
    /// </summary>
    public bool ExplicitlySelected { get; init; }

    /// <summary>
    /// Roles the user holds WITHIN this organisation, from their active membership row. Null when the
    /// organisation was not explicitly selected, when there is no active membership, or when the
    /// membership grants none.
    /// </summary>
    /// <remarks>
    /// Unioned into the subject's effective roles alongside the directly-assigned and
    /// SCIM-group-granted sets, so they reach the <c>roles</c> claim under the same <c>roles</c> scope
    /// gate as everything else in it. A resource server reading <c>roles</c> therefore does not have to
    /// know whether a role was granted tenant-wide or per organisation — but it does have to read
    /// <c>org_id</c> alongside, because the same role name means "in this organisation" here.
    /// </remarks>
    public IReadOnlyList<string>? MembershipRoles { get; init; }

    /// <summary>No organisation. Emits no org claims at all.</summary>
    public static readonly OrganizationSelection None = new();
}

/// <summary>
/// The request may not be issued a token for the organisation it resolved to. Carries the OIDC error
/// the authorize endpoint should reflect to the relying party.
/// </summary>
/// <remarks>
/// An exception rather than a result type because it is thrown from
/// <c>UserStoreOidcSubjectResolver.BuildSubjectAsync</c>, whose return type is the subject itself and
/// is called directly by the device grant as well as through the two resolver entry points. Every one
/// of those three call sites turns it into a refusal; none of them can proceed without a subject.
/// </remarks>
public sealed class OrganizationAccessDeniedException(OidcRejection rejection, string description)
    : Exception(description)
{
    /// <summary>The OIDC error to reflect — <c>access_denied</c>, or
    /// <c>account_selection_required</c> when the client serves several organisations and the request
    /// named none.</summary>
    public OidcRejection Rejection { get; } = rejection;
}
