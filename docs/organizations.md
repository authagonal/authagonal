---
layout: default
title: Organizations
nav_order: 14
---

# Organizations

An organization is a customer inside your tenant. One deployment can serve many of them: each has its own identity, its own members, and its own `org_id` on the tokens your applications receive.

## Overview

Before organizations, a user record carried an `OrganizationId` string — written by TCC provisioning or by a SCIM token binding — and that string was emitted as the `org_id` claim. There was nowhere to say what the organization *was*, who belonged to it, or whether it could be authenticated as at all.

An `Organization` gives it a record: an immutable opaque id, an immutable tenant-unique slug, a display name, an enabled flag, a metadata bag, and a branding override. An `OrganizationMembership` records who belongs, and is what actually authorizes issuing a token for that organization.

**What this is for.** An ISV whose product is deployed per customer — one app instance, one database, resolved by hostname — registers one tenant and one organization per customer. Its application reads `org_id` off the access token and refuses anything that is not the instance it is serving. The routing decision the application used to make for itself is now made by the authorization server, and proven by a signed claim.

**The tenant is still the isolation boundary.** One signing key, one issuer, one user store. An organization partitions identity *within* that boundary; it does not create a second one. Two organizations in one tenant share a user directory, and a user may belong to several.

**Not supported yet:**

- **No organization picker.** A user who belongs to several organizations, on a request that names none, gets `account_selection_required` — an error the relying party can act on by retrying with a parameter. There is no hosted screen that asks them to choose.
- **No organization-scoped SSO connections.** SAML and OIDC connections are tenant-wide, and an email domain routes to exactly one connection per tenant. A per-customer identity provider is not expressible yet.
- **No organization-scoped roles.** `OrganizationMembership.Roles` is persisted but nothing reads it. Effective roles are still directly-assigned roles unioned with SCIM-group-granted roles, tenant-wide.
- **No delegated organization administration.** There is no permission that lets a customer's own administrator manage their members.
- **No organization-scoped SCIM.** `ScimToken.OrganizationId` still tags provisioned users rather than granting them membership.
- **No invitations.** A membership is created directly; there is no invitation email flow. The `invited` status exists so one can be added without a schema change.
- **No organization-aware userinfo.** `/connect/userinfo` answers `org_id` from the user record, not from the presented token, so a multi-organization user gets their stored default there rather than the organization the token names. Read `org_id` from the access token.
- **No organization on an exchanged token.** RFC 8693 token exchange rebuilds its subject from the subject token's claims and drops reserved ones, so an exchanged token carries no organization claims.

## Creating an organization

Organizations are stored through `IOrganizationStore`. A durable implementation is required before any can be created — the built-in default is empty and read-only, and refuses writes with a message naming the missing registration. That is deliberate: organization records decide token issuance, and a process-local dictionary would keep minting tokens on every node that had not seen a revocation.

```csharp
await organizationStore.UpsertAsync(new Organization
{
    Id = "org_7f3a",              // opaque, immutable — emitted as org_id
    Slug = "international-sos",   // tenant-unique, immutable — emitted as org_slug
    DisplayName = "International SOS",
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Slug` must be unique within the tenant; a store rejects an upsert whose slug is already held by a different organization, because two records answering one slug would make the `organization` parameter ambiguous.

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

`org_slug` and `org_id` are accepted as aliases — both are in the wild at other providers, and quietly ignoring the one this server did not pick is worse than accepting both. Sending two that name *different* organizations is refused with `invalid_request`: the request means two things, and whichever the server chose, the relying party would have been told the other. Repeating any of the three is refused for the same reason `redirect_uri` is.

The parameter survives the round trip through the login UI, because the whole authorize URL travels as `returnUrl`. It also works through [Pushed Authorization Requests](par.md) with no extra work — the PAR endpoint stores every field it is given, and `/connect/authorize` reads the pushed payload instead of the query.

### Precedence

The organization is resolved in this order:

1. **The `organization` parameter** (or, on a refresh, the organization the grant was issued for).
2. **`OAuthClient.RestrictedToOrganizationIds`, when it holds exactly one entry.** A per-customer application names its organization once, at registration, and its relying party never sends a parameter at all. This is the shape most single-instance-per-customer products want.
3. **`AuthUser.OrganizationId`** — the account's own stored organization.

Rules 1 and 2 are *explicit* selections and must satisfy membership. Rule 3 is not: the account record is itself the assertion of belonging, and demanding a second one would lock out every pre-existing user the moment the matching organization was created.

## Restricting a client to an organization

```csharp
client.RestrictedToOrganizationIds = ["org_7f3a"];
```

Empty — what every existing client has — means unrestricted. A request whose organization is not on the list is refused with `access_denied`. A list of one also selects, per rule 2 above. A list of several restricts but does not select: the request must still name one, or it is refused with `account_selection_required`.

## The claims

Under the `profile` scope, on both the ID token and the access token:

| Claim | Value |
|---|---|
| `org_id` | `Organization.Id` |
| `org_slug` | `Organization.Slug` |
| `org_name` | `Organization.DisplayName` |

All three are reserved: no scope's `UserClaims` list and no custom user attribute can produce or override them. That matters most for `org_slug`, which is the stable key a relying party compares against the customer instance it is serving — a self-asserted one would be that comparison's answer.

An account carrying an organization id that resolves to no record emits `org_id` alone. Absent `org_slug` means "there is no slug", never "withheld".

**Validate `org_id` in your application:**

```csharp
var orgId = User.FindFirst("org_id")?.Value;
if (!string.Equals(orgId, ThisInstanceOrganizationId, StringComparison.Ordinal))
    return Results.Forbid();
```

## Refresh

The organization a grant was issued for is carried across every refresh rotation, and re-checked on each one. Three things therefore take effect on the next rotation rather than waiting out the refresh lifetime:

- revoking or suspending a membership,
- disabling an organization (`Enabled = false`),
- narrowing a client's `RestrictedToOrganizationIds`.

Each ends the refresh chain rather than re-minting.

A grant that merely inherited the account's organization is re-derived on every rotation instead, so re-tagging an account still takes effect.

**Switching organization is a new authorization request**, not a refresh. Send `/connect/authorize` again with a different `organization`; the existing session is reused, so there is no second sign-in, and a new grant begins. Do not expect the refresh endpoint to change organization — it has no user agent and no consent, and the grant records the scopes approved for the organization it was issued for.

## Turning the membership gate off

```csharp
organization.RequireMembershipForTokens = false;
```

On by default. Turn it off for a deployment using organizations for branding and routing rather than for access — anyone who can name the organization is then issued a token for it. An organization whose membership is advisory is not a boundary; make that choice deliberately.

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
