using System.Security.Claims;

namespace Authagonal.Protocol;

/// <summary>
/// The single extension point hosts must implement. Describes how to turn a request
/// at <c>/connect/authorize</c> (or a refresh at <c>/connect/token</c>) into the
/// <see cref="OidcSubject"/> that Authagonal.Protocol will mint tokens for.
/// <para>
/// At authorize, <see cref="ResolveAsync"/> is called with whatever principal the
/// host's authentication scheme produced (cookie login, custom share-link handler,
/// etc.). At refresh, <see cref="ResolveRefreshAsync"/> is called with the subject
/// captured at the original authorize call, so the host can re-validate the session
/// — deactivation, revoked share links, changed roles — without extending the
/// refresh chain indefinitely.
/// </para>
/// </summary>
public interface IOidcSubjectResolver
{
    /// <summary>
    /// Resolve the subject at the authorize endpoint. The principal has already been
    /// authenticated by the host's scheme.
    /// </summary>
    Task<OidcSubjectResult> ResolveAsync(
        ClaimsPrincipal authenticatedPrincipal,
        OidcSubjectResolutionContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Re-resolve the subject when a refresh token is redeemed. Hosts should re-check
    /// that the identity is still valid (user not deactivated, share link not revoked,
    /// etc.) and return a fresh <see cref="OidcSubject"/>. Returning
    /// <see cref="OidcRejection.AccessDenied"/> REFUSES THIS REFRESH; it does not revoke the grant.
    /// </summary>
    /// <remarks>
    /// The distinction matters and used to be stated the other way round here. A rejection fails the
    /// one request: the presented refresh token is left unconsumed and the family is left intact, so
    /// the chain stays refusable for as long as the condition holds and resumes the moment it stops
    /// holding — restoring a revoked membership, or re-enabling an organisation, brings the session
    /// back without a fresh sign-in. The grant still dies on its own absolute lifetime. This is the
    /// same shape as a deactivated user, whose refreshes are refused while <c>IsActive</c> is false.
    /// To actually end a session, revoke the grant (<c>/connect/revocation</c>, or
    /// <c>GrantRevocation</c> on the host side).
    /// </remarks>
    Task<OidcSubjectResult> ResolveRefreshAsync(
        OidcSubject priorSubject,
        OidcSubjectResolutionContext context,
        CancellationToken ct = default);
}

/// <param name="RequestedOrganization">
/// The <c>organization</c> authorize parameter's value — an organisation slug or id — or null when
/// the request named none, which is every request a host that predates organisations sends.
/// Defaulted so existing three-argument CONSTRUCTION keeps compiling; note that the positional arity
/// is now four, so a positional pattern or an explicit <c>Deconstruct</c> into three variables does
/// not.
/// </param>
public sealed record OidcSubjectResolutionContext(
    string ClientId,
    IReadOnlyList<string> RequestedScopes,
    IReadOnlyList<string> RequestedResources,
    string? RequestedOrganization = null);

public abstract record OidcSubjectResult
{
    private OidcSubjectResult() { }

    public static OidcSubjectResult Allow(OidcSubject subject) => new Allowed(subject);

    public static OidcSubjectResult Reject(OidcRejection reason, string? description = null) =>
        new Rejected(reason, description);

    public sealed record Allowed(OidcSubject Subject) : OidcSubjectResult;

    public sealed record Rejected(OidcRejection Reason, string? Description) : OidcSubjectResult;
}

/// <summary>
/// OIDC error codes the subject resolver may surface. Maps to the standard
/// <c>error</c> value in the authorize response.
/// </summary>
public enum OidcRejection
{
    /// <summary>User must re-authenticate. Maps to <c>login_required</c>.</summary>
    LoginRequired,

    /// <summary>User has not consented to the requested scopes. Maps to <c>consent_required</c>.</summary>
    ConsentRequired,

    /// <summary>Multiple accounts are available and the user must pick one. Maps to <c>account_selection_required</c>.</summary>
    AccountSelectionRequired,

    /// <summary>Authenticated user is not permitted for this request. Maps to <c>access_denied</c>.</summary>
    AccessDenied,
}

/// <summary>
/// The resolved identity Authagonal.Protocol mints tokens for. Hosts build this from
/// their own identity model (AuthUser, share-link claim set, whatever) and return it
/// from <see cref="IOidcSubjectResolver"/>. A record so decorating resolvers can overlay
/// fields with a <c>with</c> expression without hand-copying (and silently dropping) the rest.
/// </summary>
public sealed record OidcSubject
{
    /// <summary>Stable identifier for the user. Emitted as the <c>sub</c> claim.</summary>
    public required string SubjectId { get; init; }

    public string? Email { get; init; }
    public bool EmailVerified { get; init; }
    public string? Name { get; init; }
    public string? GivenName { get; init; }
    public string? FamilyName { get; init; }
    public string? Phone { get; init; }

    /// <summary>Preferred language as a BCP-47 tag. Emitted as the standard OIDC <c>locale</c> claim
    /// under the <c>profile</c> scope.</summary>
    public string? Locale { get; init; }

    /// <summary>
    /// Convenience slot for the <c>org_id</c> claim used across Authagonal's product
    /// line. Hosts that don't use it can leave it null — nothing else reads it.
    /// </summary>
    public string? OrganizationId { get; init; }

    /// <summary>
    /// The organisation's tenant-unique slug, emitted as the <c>org_slug</c> claim under the same
    /// scope gate as <see cref="OrganizationId"/>.
    /// </summary>
    /// <remarks>
    /// Null whenever <see cref="OrganizationId"/> is a bare string with no organisation record behind
    /// it — the shape every account had before organisations were a first-class entity. A relying
    /// party that wants a stable, human-readable key reads this; one that only needs identity reads
    /// <see cref="OrganizationId"/>, which is present in both cases.
    /// </remarks>
    public string? OrganizationSlug { get; init; }

    /// <summary>
    /// The organisation's display name, emitted as the <c>org_name</c> claim under the same scope
    /// gate as <see cref="OrganizationId"/>. Null for the same reason as
    /// <see cref="OrganizationSlug"/>, and never anything an authorization decision should rest on —
    /// it is mutable presentation, unlike the id and the slug.
    /// </summary>
    public string? OrganizationName { get; init; }

    /// <summary>
    /// True when the organisation was NAMED by the request — the <c>organization</c> parameter, or a
    /// client registered against exactly one — rather than derived from the account's own stored
    /// organisation. Never emitted as a claim.
    /// </summary>
    /// <remarks>
    /// This is what has to survive a refresh rotation, and it is not derivable afterwards. A grant
    /// that named its organisation must keep that organisation across every rotation and be re-checked
    /// against membership on each one, while a grant that merely inherited the account's must be
    /// re-derived so an operator re-tagging the account still takes effect. Comparing the carried
    /// organisation against the account's CURRENT value cannot tell the two apart — after a re-tag they
    /// differ in both cases — so the fact travels with the grant instead of being reconstructed.
    /// </remarks>
    public bool OrganizationExplicitlySelected { get; init; }

    /// <summary>Roles to emit as <c>roles</c> claims on access and id tokens.</summary>
    public IReadOnlyList<string>? Roles { get; init; }

    /// <summary>Group display names to emit as <c>groups</c> claims. Typically sourced from SCIM.</summary>
    public IReadOnlyList<string>? Groups { get; init; }

    /// <summary>
    /// Custom attributes carried by the subject. Each entry is emitted as a claim only
    /// if a requested scope's <c>UserClaims</c> whitelist releases that claim name.
    /// Reserved OAuth/OIDC protocol claim names (<c>iss</c>, <c>sub</c>, <c>aud</c>,
    /// <c>exp</c>, <c>iat</c>, <c>scope</c>, <c>client_id</c>, <c>roles</c>, <c>groups</c>,
    /// etc.) cannot be shadowed even if a scope lists them.
    /// </summary>
    public IReadOnlyDictionary<string, string>? CustomAttributes { get; init; }

    /// <summary>
    /// Per-session claims sourced from an upstream OIDC IdP during federation. Same
    /// scope-gated emission rules as <see cref="CustomAttributes"/>, but distinct so
    /// they survive across refresh rotations without bleeding into the per-user record.
    /// On refresh, the host carries this set forward unchanged from the prior subject;
    /// <see cref="CustomAttributes"/> is re-read fresh from the user store. On key collision the
    /// STORE wins: these arrive verbatim from an upstream id_token, so letting them overwrite would
    /// let a customer-controlled IdP restate any scope-released claim about their own user and beat
    /// this server's record of it.
    /// </summary>
    public IReadOnlyDictionary<string, string>? FederationClaims { get; init; }

    /// <summary>
    /// Additional claims to force onto the access token regardless of scope gating.
    /// Used by hosts with a short-lived, bounded-scope use case (e.g. share-link
    /// tokens carrying <c>link_share_token</c>) where the claim is the whole point of
    /// the token. Reserved protocol claim names are still blocked.
    /// </summary>
    public IReadOnlyDictionary<string, string>? AdditionalClaims { get; init; }

    /// <summary>
    /// Optional session cap. When set, access / id / refresh token lifetimes issued from
    /// this subject are clamped so no token — including those minted from rotations —
    /// outlives this moment. Typically sourced from an upstream IdP's <c>exp</c>-style
    /// claim, or from the absolute expiry of a share link.
    /// </summary>
    public DateTimeOffset? SessionMaxExpiresAt { get; init; }

    /// <summary>
    /// When the end-user last actively authenticated. Emitted as the OIDC <c>auth_time</c> claim, and
    /// the value <c>max_age</c> is measured against.
    /// </summary>
    /// <remarks>
    /// <c>auth_time</c> was advertised in <c>claims_supported</c> but no ID token could ever carry it:
    /// the 0.11.0 work recorded the authentication time on the session cookie only, so it never
    /// reached the subject and never reached a token. Without it, <c>max_age</c> has nothing to
    /// compare against and an RP's re-authentication demand cannot be honoured or evidenced.
    /// </remarks>
    public DateTimeOffset? AuthTime { get; init; }

    /// <summary>
    /// Stable per-authentication-session identifier. Emitted as the <c>sid</c> claim on ID
    /// tokens and propagated into back-channel logout tokens when the relying party has
    /// <c>BackChannelLogoutSessionRequired</c> set. Generated at sign-in by the host and
    /// preserved across refresh rotations so RPs can correlate logout events back to the
    /// original login.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Opaque refresh token issued by an upstream IdP during federation, for a connection that
    /// revalidates the local session on refresh. NEVER emitted into any issued token — it is redeemed
    /// server-to-server against the upstream on each local refresh (so upstream revocation/expiry
    /// propagates), then rotated. Persisted in the refresh grant alongside the subject and carried across
    /// rotations. Null for connections that don't revalidate on refresh.
    /// </summary>
    public string? UpstreamRefreshToken { get; init; }

    /// <summary>Connection id whose token endpoint <see cref="UpstreamRefreshToken"/> is redeemed
    /// against. Set together with <see cref="UpstreamRefreshToken"/>.</summary>
    public string? UpstreamConnectionId { get; init; }
}
