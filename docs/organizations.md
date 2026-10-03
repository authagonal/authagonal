---
layout: default
title: Organizations
nav_order: 14
---

# Organizations

An organization is a customer inside your tenant. One deployment can serve many of them: each has its own identity, its own members, and its own `org_id` on the tokens your applications receive.

## Overview

Before organizations, a user record carried an `OrganizationId` string (written by TCC provisioning or by a SCIM token binding) and that string was emitted as the `org_id` claim. There was nowhere to say what the organization *was*, who belonged to it, or whether it could be authenticated as at all.

An `Organization` gives it a record: an immutable opaque id, an immutable tenant-unique slug, a display name, an enabled flag, a metadata bag, and a branding override. An `OrganizationMembership` records who belongs, and is what actually authorizes issuing a token for that organization.

**What this is for.** An ISV whose product is deployed per customer (one app instance, one database, resolved by hostname) registers one tenant and one organization per customer. Its application reads `org_id` off the access token and refuses anything that is not the instance it is serving. The routing decision the application used to make for itself is now made by the authorization server, and proven by a signed claim.

**The tenant is still the isolation boundary.** One signing key, one issuer, one user store. An organization partitions identity *within* that boundary; it does not create a second one. Two organizations in one tenant share a user directory, and a user may belong to several.

**Not supported yet:**

- **No organization picker.** A user who belongs to several organizations, on a request that names none, gets `account_selection_required`, an error the relying party can act on by retrying with a parameter. There is no hosted screen that asks them to choose.
- **No delegated organization administration.** There is no permission that lets a customer's own administrator manage their members.
- **No organization-scoped SCIM isolation.** A SCIM token bound to an organization (`ScimToken.OrganizationId`) tags the users it creates and, when the id names a real organization, makes them active members (see [Membership from a SCIM token](#membership-from-a-scim-token)). Ownership checks still key on the OAuth client, not the organization, so the binding decides tagging, not access.
- **No invitation flow in this library.** There is no invite endpoint and no invitation email. A host writes an `invited` membership itself; the ways it becomes `active` are in [Invitations](#invitations).
- **No organization-scoped groups.** The `groups` claim and SCIM group membership stay tenant-wide; only roles are organization-scoped.
- **No organization webhook events, and no organization-scoped audit.** `IAuthHook` has no organization lifecycle events (created, membership granted or revoked), existing hook payloads carry no `organizationId`, and there is no organization column or index on the audit log.
- **Role-gated scopes are filtered against tenant roles at authorize.** `Scope.AllowedRoles` is applied on `/connect/authorize` against the account's directly-assigned roles, before the organization is resolved, so a scope whose `AllowedRoles` is satisfied only by an organization-scoped role is dropped at authorize, or refused with `access_denied` if no requested scope survives. On refresh the same gate runs against the resolved subject's roles, which do include the organization's. Until the two agree, gate scopes on tenant roles.
- **No organization branding in this library.** `Organization.BrandingJson` is stored for the host to merge over the tenant's branding; nothing in this library reads it. The login app shows the organization's name ("Signing in to {name}") when the host's boot payload carries an `organization` (`{ id, slug, name }`); the library itself resolves no organization before authentication except through the `organization` parameter, a single-entry client restriction, an organization-scoped connection, or a host's `ITenantContext.OrganizationId`.
- **No organization admin REST API.** `IOrganizationStore` and `IOrganizationMembershipStore` are the surface; a host that wants endpoints builds them. A host that lists organizations or members should use `ListPageAsync` / `ListByOrganizationPageAsync` (below).

## Creating an organization

Organizations are stored through `IOrganizationStore`. A durable implementation is required before any can be created. The built-in default is empty and read-only, and refuses writes with a message naming the missing registration. That is deliberate: organization records decide token issuance, and a process-local dictionary would keep minting tokens on every node that had not seen a revocation.

```csharp
await organizationStore.UpsertAsync(new Organization
{
    Id = "org_7f3a",              // opaque, immutable, emitted as org_id
    Slug = "international-sos",   // tenant-unique, immutable, emitted as org_slug
    DisplayName = "International SOS",
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Slug` must match `^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$`: 1 to 64 characters of lowercase letters, digits and interior hyphens, with no leading or trailing hyphen. Lowercase because the `organization` parameter is lowercased before the slug lookup, so an uppercase slug would be a value no request could ever resolve.

`Slug` must be unique within the tenant, and **ids and slugs share one namespace**: a store rejects an upsert whose slug is already held by another organization, and equally one whose slug equals another organization's id, or whose id equals another's slug. Two records answering one value would make the `organization` parameter mean one organization while every stored id means another.

`Id` must match `^[A-Za-z0-9._~-]{1,200}$`: the same shape as the `organization` parameter, so every id can always be sent as one. An id outside that shape is an id no request can select, and a client restricted to one would refuse every request.

`Id` **should** also contain at least one character a slug may not: an uppercase letter, `.`, `_` or `~`. Ids and slugs share one lookup namespace, and an all-lowercase value is resolved slug-first, so an id that is itself slug-shaped is one that could later be refused at creation because someone took that slug, while an id carrying a non-slug character never can. The recommended shape for new ids is an `org_`-prefixed opaque value (`org_7f3a9c`): `_` is not slug-legal, so the prefix alone guarantees it. No convention is enforced, and the values already in the field are arbitrary: they come from a downstream app's TCC `/try` response (`TccProvisioningOrchestrator`) or an operator's SCIM token binding (`ScimToken.OrganizationId`, stamped on new users by `ScimUserEndpoints`).

Both `Id` and `Slug` are immutable in practice. Relying parties compare them against the instance they are serving and will hard-code them, so changing either is an outage with no error message. `DisplayName` is freely mutable and is what a screen renders.

## Granting membership

```csharp
await membershipStore.UpsertAsync(new OrganizationMembership
{
    OrganizationId = "org_7f3a",
    UserId = user.Id,
    Status = MembershipStatus.Active,
    JoinedAt = DateTimeOffset.UtcNow,
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Status` is `invited`, `active` or `suspended`. **Only `active` authorizes token issuance.** Suspending rather than deleting keeps the record of who invited whom.

### Invitations

An `invited` row carries `InvitedByUserId`, `InvitedAt`, and any roles the invitee was offered. The library never sends the invitation; it promotes the row to `active` (keeping roles, inviter and invitation time, and stamping `JoinedAt`) in two places:

- **Signing in through an organization-scoped SAML or OIDC connection.** The organization's own IdP vouching for the person accepts the invitation (`FederatedOrganizationBinding`, 0.30.2). Without it an invitee who only ever signs in through SSO would be refused a token by the membership gate.
- **Automatic membership** (below), when the user qualifies.

A `suspended` row is never promoted by either route and never modified by a sign-in.

### Verified domains and automatic membership

`Organization.Domains` holds the email domains the organization has claimed, each an `OrganizationDomain { Domain, VerificationToken, CreatedAt, VerifiedAt }`. `Domain` is stored lowercase, trimmed, with no trailing dot. The library stores the claim and reads `VerifiedAt`; proving control (typically a DNS TXT record carrying `VerificationToken`) is the host's job, and the host stamps `VerifiedAt` when it succeeds.

`Organization.AllowAutoMembership` (off by default) lets a user join without an invitation. When the organization is **explicitly** selected (a carried refresh grant, the `organization` parameter, an organization-scoped connection, or a client restricted to exactly that organization) and the user has no active membership, they are made a member if **all** of these hold:

- the organization is enabled and `AllowAutoMembership` is on,
- `AuthUser.EmailConfirmed` is true,
- the part of the email after the last `@`, lowercased, **exactly** equals a domain with `VerifiedAt` set. A verified `acme.com` does not admit `user@eu.acme.com`.

A missing row is created `active` with no roles; an `invited` row is promoted; a `suspended` row is never touched. It applies at authorize and on every refresh, so while the flag is on, deleting a qualifying member's row does not keep them out (they rejoin at their next token); suspend them instead. An organization inherited only from `AuthUser.OrganizationId` never auto-joins. Each auto-join is logged at Information.

### Membership from a SCIM token

A SCIM token minted with `organizationId` stamps that value as `org_id` on every user it creates. When the id names an existing organization, the create also writes an `active` membership with no roles and audits `scim.organization_member_added`. An id that names no organization stays a bare tag. Creation only: a later sync does not re-tag or add members. See [SCIM](scim#tagging-a-connectors-users-with-an-organization).

### Listing and deleting

`IOrganizationStore.ListPageAsync(cursor, limit)` and `IOrganizationMembershipStore.ListByOrganizationPageAsync(organizationId, cursor, limit)` return a page (`Items` and an opaque `NextCursor`, null on the last page). `limit` is clamped to 1..200 and a malformed cursor throws `ArgumentException`. The cursor is keyset-based, so a row added or removed between reads never shifts or repeats a page. Both have default implementations over the unpaged listings, so a custom store keeps compiling; the Azure Table stores override them with a server-side range query.

Deleting a user through the admin `DELETE /api/v1/profile/{userId}`, SCIM `DELETE /scim/v2/Users/{id}` or the SCIM reclaim path also deletes every membership they hold (`AccountArtefactPurge.PurgeAsync` with an `IOrganizationMembershipStore`; the three-store overload purges none). A host with its own delete path must pass the membership store too, or organizations keep listing the deleted member.

## Organization-scoped roles

`OrganizationMembership.Roles` holds the roles a user has **within** that organization. The names come from the tenant's existing role catalogue: an ISV declares "Auditor" once, and every customer grants it to their own people.

```csharp
membership.Roles = ["Auditor", "Site Manager"];
```

They are unioned into the `roles` claim alongside the user's directly-assigned roles and any granted by SCIM group membership, under the same `roles` scope gate. A resource server does not have to know whether a role was granted tenant-wide or per organization, but it **does** have to read `org_id` alongside `roles`, because the same role name now means "in this organization".

Four rules bound it:

- **Only an explicitly selected organization contributes roles**: one named by the `organization` parameter or by a single-entry client restriction. An organization inherited from `AuthUser.OrganizationId` contributes none, the same asymmetry the membership gate has.
- **Only an `active` membership contributes.** An invited-but-unaccepted or suspended member grants nothing, exactly as they authorize nothing.
- **Roles never cross organizations.** They are read from the membership row keyed by the selected organization, so a role held in one cannot reach a token issued for another.
- **Reserved prefixes are stripped.** A role beginning `tenant:` or `platform:` is dropped at the union and logged at Warning. A membership row is customer-scoped data, so a membership able to grant `tenant:admin` would turn "may manage my own organization" into "may administer the tenant". Directly-assigned roles and SCIM group→role mappings are unaffected: those are written by an operator through an authenticated admin surface, which is the authority a membership row does not have.

Roles are re-read from the membership row on every refresh rotation, so changing them reaches a live session at its next refresh.

Tenant-wide roles are **unioned with**, not replaced by, the organization's: `tenant:admin` is portal authority and survives selecting an organization.

## Selecting an organization on an authorization request

Send `organization` with an organization slug or id:

```http
GET /connect/authorize
  ?client_id=mobiom-web
  &response_type=code
  &redirect_uri=https://audit.example.com/callback
  &scope=openid%20profile
  &organization=international-sos
  &code_challenge=...&code_challenge_method=S256
```

The value must match `^[A-Za-z0-9._~-]{1,200}$` (the RFC 3986 unreserved set); anything else is `invalid_request`. How it resolves depends on its case:

- **Any uppercase character → resolved as an id only, exactly.** Slugs are lowercase-only, so such a value cannot be one. Lowercasing it and asking the slug index anyway would ask "is some organization's slug the lowercased form of this id?", and if one were, a caller naming an id would be handed a different customer.
- **All lowercase → slug first, then id.** It could be either, and a slug is what a relying party usually sends. Unambiguous because a store refuses to let an id and a slug share a value.

`org_slug` and `org_id` are accepted as aliases: both are in the wild at other providers, and quietly ignoring the one this server did not pick is worse than accepting both. Sending two that name *different* organizations is refused with `invalid_request`: the request means two things, and whichever the server chose, the relying party would have been told the other. Repeating any of the three is refused for the same reason `redirect_uri` is.

The parameter survives the round trip through the login UI, because the whole authorize URL travels as `returnUrl`. It also works through [Pushed Authorization Requests](par) with no extra work: the PAR endpoint stores every field it is given, and `/connect/authorize` reads the pushed payload instead of the query.

### Precedence

The organization is resolved in this order:

1. **On a refresh, the organization the grant was issued for.**
2. **The organization an [organisation-scoped SSO connection](self-service-sso#organisation-scoped-connections) authenticated this session for.** The only source here that was *proven* rather than asserted by a caller: the user signed in at an IdP that belongs to exactly one organization. A request naming a different one is refused with `access_denied` rather than quietly issued the other.
3. **The `organization` parameter.**
4. **`OAuthClient.RestrictedToOrganizationIds`, when it holds exactly one entry.** A per-customer application names its organization once, at registration, and its relying party never sends a parameter at all. This is the shape most single-instance-per-customer products want.
5. **`AuthUser.OrganizationId`**: the account's own stored organization.

Rules 1-4 are *explicit* selections and must satisfy membership. Rule 5 is not: the account record is itself the assertion of belonging, and demanding a second one would lock out every pre-existing user the moment the matching organization was created.

Before anyone has authenticated (home-realm discovery, the login page's provider list, `/sso-check`), there is no user and no grant, so rules 3, 4 and then `ITenantContext.OrganizationId` are resolved on their own. See [Organisation-scoped connections](self-service-sso#organisation-scoped-connections).

## Restricting a client to an organization

```csharp
client.RestrictedToOrganizationIds = ["org_7f3a"];
```

Each entry must match the organization id shape `^[A-Za-z0-9._~-]{1,200}$`; the admin API answers `400 invalid_request` for an empty or malformed one, because a restriction listing an id no `organization` parameter can send matches nothing, and a restriction that matches nothing refuses every request. A `null` list is normalized to empty.

Empty (what every existing client has) means unrestricted. A request whose organization is not on the list is refused with `access_denied`. A list of one also selects, per rule 4 above. A list of several restricts but does not select: the request must still name one, or it is refused with `account_selection_required`.

## The claims

On both the ID token and the access token:

| Claim | Value | Scope |
|---|---|---|
| `org_id` | `Organization.Id` | none; always present when the subject has an organization |
| `org_slug` | `Organization.Slug` | none; always present when the organization is a real record |
| `org_name` | `Organization.DisplayName` | `profile` |

**`org_id` and `org_slug` are deliberately not scope-gated.** They are authorization context, not profile data: they say which customer the token may act for, which is the first thing a multi-customer resource server checks, before it has decided whether it cares about a name, and often on a token that requested no profile at all. Under a `profile` gate an API-only client asking for `openid` alone received a token with no organization on it, which reads as "belongs to nobody": the resource server either refuses a legitimate caller, or treats the token as unscoped and serves every customer's data from it. The second failure is silent, and it is the one that matters.

Releasing them ungated discloses nothing the client had not already established: it chose the organization, or it is restricted to one. `org_name` keeps the `profile` gate because it is presentation, and nothing should authorize on it.

An account with no organization emits none of the three, so a token that carried no organization claims before carries none now.

All three are reserved: no scope's `UserClaims` list and no custom user attribute can produce or override them. That matters most for `org_slug`, which is the stable key a relying party compares against the customer instance it is serving. A self-asserted one would be that comparison's answer.

An account carrying an organization id that resolves to no record emits `org_id` alone. Absent `org_slug` means "there is no slug", never "withheld".

**Validate `org_id` in your application:**

```csharp
var orgId = User.FindFirst("org_id")?.Value;
if (!string.Equals(orgId, ThisInstanceOrganizationId, StringComparison.Ordinal))
    return Results.Forbid();
```

## Userinfo, introspection and token exchange

**`/connect/userinfo`** answers `org_id`, `org_slug`, `org_name` and `roles` from the **presented token**, not from the user record. `org_id` and `org_slug` are returned whenever the token carries them, with no scope gate, for the same reason they are ungated on the token itself; `org_name` needs `profile`. That is the only source that can be right once a user may belong to several organizations: the account carries one default, while the token names the organization the grant was actually issued for. Profile fields (`email`, `name`, `phone_number`) stay live: those are the subject's current details, which is what userinfo is for.

So re-tagging an account does not change what userinfo says about a token already issued, and a user signed in to organization B is never told `org_id` A by the same server that put B in their ID token.

**`/connect/introspect`** includes `org_id` and `org_slug` when the token carries them. A resource server that validates the JWT itself reads them off the token; one that introspects instead now gets the same answer.

**RFC 8693 token exchange** carries `org_id`, `org_slug` and `org_name` from the subject token onto the exchanged one, and enforces the **exchanging** client's `RestrictedToOrganizationIds` against them: a client registered to serve one customer cannot exchange another customer's token, and cannot exchange a token that carries no organization at all. Because `org_id` is ungated, that check also works for a resource-server token minted without the `profile` scope: under the old gating such a token looked unattributed, and a restricted client was refused its own traffic. A refusal is `invalid_target`, matching the other target-policy refusals on that path. An exchange is a projection of an existing session, and a projection that dropped the organization it was acting for would be unattributed rather than narrower. A host's `ITokenExchangeSubjectTransformer` may still re-bind the exchange to another organization deliberately (that is what context-bound exchanges are for), but it has to say so.

## Refresh

The organization a grant was issued for is carried across every refresh rotation, and re-checked on each one. Three things therefore take effect at the next rotation rather than waiting out the refresh lifetime:

- revoking or suspending a membership,
- disabling an organization (`Enabled = false`),
- narrowing a client's `RestrictedToOrganizationIds`.

**Each one refuses the refresh; none of them revokes the grant.** The presented refresh token is left unconsumed and the family is left intact, so the chain stays refusable for as long as the condition holds and resumes the moment it stops holding: restoring a membership, or re-enabling an organization, brings the session back with no fresh sign-in. The grant still expires on its own absolute lifetime. This is the same shape as a deactivated user, whose refreshes are refused while `IsActive` is false.

To actually end a session, revoke the grant: `POST /connect/revocation` with the refresh token, or `GrantRevocation` on the host side. Disabling an organization is a gate, not a revocation.

A grant that merely inherited the account's organization is re-derived on every rotation instead, so re-tagging an account still takes effect.

**Switching organization is a new authorization request**, not a refresh. Send `/connect/authorize` again with a different `organization`; the existing session is reused, so there is no second sign-in, and a new grant begins. Do not expect the refresh endpoint to change organization: it has no user agent and no consent, and the grant records the scopes approved for the organization it was issued for.

## Turning the membership gate off

```csharp
organization.RequireMembershipForTokens = false;
```

On by default. Turn it off for a deployment using organizations for branding and routing rather than for access: anyone who can name the organization is then issued a token for it. An organization whose membership is advisory is not a boundary; make that choice deliberately.

## Refusing an issuance from a host hook

`IAuthHook.OnTokenIssuingAsync` fires immediately before the `authorization_code`, `refresh_token` and `device_code` grants mint anything, with the resolved subject:

```csharp
public Task OnTokenIssuingAsync(TokenIssuanceContext context, CancellationToken ct = default)
{
    if (IsOffboarded(context.SubjectId, context.ClientId))
        throw new InvalidOperationException("This account is being offboarded.");
    return Task.CompletedTask;
}
```

Throwing refuses the issuance with `access_denied` and the exception's message as `error_description`; throwing a `ProtocolTokenException` instead names your own OAuth error. On the refresh path the gate runs **before** the rotation, so a refusal leaves the presented refresh token unconsumed and the family intact: "not right now" is not "end this session".

It is a default interface member, so an existing `IAuthHook` that does not override it is unaffected. The two agentic mints (`client_credentials` and token exchange, each with an agent profile) fire it exactly as they did before.

## Refusals

| Condition | Error |
|---|---|
| Two selectors naming different organizations | `invalid_request` |
| Any selector repeated | `invalid_request` (delivered directly, not reflected to `redirect_uri`) |
| Named organization does not exist | `access_denied` |
| Organization is disabled | `access_denied` |
| Client not permitted for this organization | `access_denied` |
| User is not an active member (explicit selection) | `access_denied` |
| Client serves several organizations, request named none | `account_selection_required` |

## Device flow

The device grant has no authorization request to carry a parameter, so it falls through to the client restriction and then the account default. A device client that must be pinned to one organization should be registered with a single-entry `RestrictedToOrganizationIds`.

## Upgrading an existing deployment

Nothing changes until an organization exists. With no records:

- no request can select an organization,
- no membership gate engages,
- an account carrying a legacy `OrganizationId` keeps emitting `org_id` from the user record, exactly as before,
- a token for a user with no organization carries none of the three claims.

Once you create organizations, grant memberships **before** pointing a client or a relying party at one: an explicit selection demands an active membership, and a customer whose users have records but no memberships will be refused.
