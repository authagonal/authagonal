---
layout: default
title: OAuth Scopes
---

# OAuth Scopes

Authagonal supports both **built-in** OAuth/OIDC scopes and **custom** scopes managed at runtime. Custom scopes are persisted, advertised via the discovery document, and surfaced on the consent screen alongside built-ins.

## Built-in Scopes

These scopes are always available and do not need to be registered:

| Scope | Purpose |
|---|---|
| `openid` | Required to initiate an OIDC flow. Issues an ID token. |
| `profile` | Standard profile claims (name, family_name, given_name, etc.) |
| `email` | Email address and `email_verified` claims |
| `phone` | `phone_number` and `phone_number_verified` claims (OIDC Core 5.4) |
| `roles` | The `roles` claim. Not an OIDC standard scope: role membership is a claim the end user consents to disclose |
| `groups` | The `groups` claim (SCIM group membership). Not an OIDC standard scope, gated like `roles` |
| `offline_access` | Issues a refresh token alongside the access token |

A client may request only the scopes listed in its own `AllowedScopes`. `/connect/authorize` rejects a scope absent from that list with `invalid_scope` rather than filtering it out, so adding `roles` to an application's request without adding it to the client breaks every login.

## Custom Scopes

Custom scopes are managed through the admin API at `/api/v1/scopes`. They require a JWT access token with the `authagonal-admin` scope (configurable via `AdminApi:Scope`).

### Scope Model

```csharp
public sealed class Scope
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }
    public string? Group { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> AllowedRoles { get; set; } = [];
    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

| Field | Description |
|---|---|
| `Name` | The scope identifier sent in token requests (e.g., `billing.read`) |
| `DisplayName` | Human-readable name shown on the consent screen |
| `Description` | Longer description shown on the consent screen |
| `Emphasize` | If `true`, the consent screen highlights this scope as sensitive |
| `Group` | Consent-screen heading to file this scope under. Presentation only: it never affects what is granted |
| `Required` | If `true`, the user cannot deselect this scope when consenting |
| `ShowInDiscoveryDocument` | If `true`, the scope appears in `/.well-known/openid-configuration` under `scopes_supported` |
| `AllowedRoles` | Roles a user must hold to be granted this scope. Empty (the default) leaves it ungated, see [Role-gated scopes](#role-gated-scopes) |
| `UserClaims` | Whitelist of custom-attribute claim names released onto tokens when this scope is granted. Reserved protocol claims (such as `org_id`) are never released this way, so a stored attribute cannot forge them |

### Role-gated scopes

A client's `AllowedScopes` answers *may this application ask for this scope*, a question settled
before anyone has logged in. `AllowedRoles` answers the other half: *may this person have it*. Both
gates apply, and neither substitutes for the other.

```json
{
  "name": "staff-admin",
  "displayName": "Staff administration",
  "allowedRoles": ["staff", "super-admin"]
}
```

A user holding none of the listed roles has the scope **dropped from the grant**, not refused: the
client asked for its full set and is told, via the `scope` echoed in the token response (RFC 6749
§3.3), that it got less. This is what lets one application serve both staff and everyone else: the
staff surface is one scope among several, and only the people entitled to it receive it.

A request in which *every* requested scope is dropped fails with `access_denied`, because there is
nothing left to issue a token for.

The gate applies everywhere a token is minted for a human:

| Flow | Where it runs |
|---|---|
| Authorization code | At `/connect/authorize`, once the user is known and **before** consent, so the screen never offers a permission that cannot be granted |
| Device code | At `/api/auth/device/approve`, the first point in that flow at which the subject is known |
| Refresh | On every rotation, against freshly resolved roles. This is where revoking a role actually takes effect, since the grant still records what was approved at login |
| Token exchange | Not separately gated: an exchange may only downscope within the subject token's own scopes, so it can never reach one the subject was not granted |

Client-credentials grants have no subject and are deliberately untouched: a machine client's
authority is its registration.

Seeding a scope from configuration can add or change `AllowedRoles` but cannot clear it (as with
`UserClaims`, an omitted field preserves the stored value). To remove a gate, `PUT` the scope with
an explicit empty array.

## Seeding from Configuration

Scopes can be declared in the `Scopes` configuration section. They are written to the scope store at startup, alongside [client seeding](configuration#clients).

```json
{
  "Scopes": [
    {
      "Name": "billing.read",
      "DisplayName": "Billing (read-only)",
      "Description": "View invoices and payment history",
      "UserClaims": ["billing_plan"],
      "ShowInDiscoveryDocument": true,
      "Emphasize": false,
      "Group": "Billing",
      "Required": false,
      "AllowedRoles": ["finance"]
    }
  ]
}
```

| Field | Description |
|---|---|
| `Name` | Required. An entry with no name is skipped with a warning |
| `DisplayName`, `Description`, `UserClaims`, `ShowInDiscoveryDocument`, `Emphasize`, `Group`, `Required`, `AllowedRoles` | As in the [scope model](#scope-model) |

Seeding is an upsert by `Name`. A field you set wins over the stored value on every start, so an edit made through the admin API to a field that is also seeded is overwritten at the next boot. A field you omit keeps whatever is stored (or the model default for a new scope). Because omission means "keep", config can add or change `UserClaims` and `AllowedRoles` but cannot empty them: do that with `PUT /api/v1/scopes/{name}` and an explicit empty array.

## Admin Endpoints

### List Scopes

```
GET /api/v1/scopes
```

Returns `{ "scopes": [ ... ] }`.

### Get Scope

```
GET /api/v1/scopes/{name}
```

Returns the scope or `404` if not found.

### Create Scope

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing (read-only)",
  "description": "View invoices and payment history",
  "emphasize": false,
  "required": false,
  "showInDiscoveryDocument": true,
  "userClaims": ["billing_plan"]
}
```

Returns `201 Created` with the scope. Returns `400` (`invalid_request`) if `name` is missing or contains whitespace, and `409` (`scope_exists`) if a scope with the same name already exists.

### Update Scope

```
PUT /api/v1/scopes/{name}
Content-Type: application/json

{
  "displayName": "Billing (read)",
  "description": "View invoices",
  "emphasize": true
}
```

Only supplied fields are updated; omitted fields retain their current values.

### Delete Scope

```
DELETE /api/v1/scopes/{name}
```

Returns `204 No Content` (`404` if the scope doesn't exist). Tokens already issued that include this scope remain valid until they expire, revoke them explicitly via `/connect/revocation` if needed.

## Discovery Document

Scopes with `ShowInDiscoveryDocument = true` appear under `scopes_supported` in `/.well-known/openid-configuration`. The seven built-in scopes are always advertised.

```json
{
  "scopes_supported": ["openid", "profile", "email", "phone", "roles", "groups", "offline_access", "billing.read"]
}
```

## Consent Screen

When a client requests a scope that is not in its consent-skip list, the consent page lists each requested scope by `DisplayName` (falling back to `Name`) with the `Description` underneath. Scopes with `Emphasize = true` receive a distinct visual treatment. `Required` scopes cannot be deselected.

See [OAuth Consent Screen](index#key-features) for the user-facing flow.

## Dynamic Client Registration

Clients registered via [Dynamic Client Registration](client-registration) may only declare the OIDC built-ins (`openid`, `profile`, `email`, `phone`, `offline_access`) plus any scope named in `Auth:DynamicClientRegistrationScopes`. A scope merely existing in the store is not permission for a self-registered client to declare it, and role-gated scopes (those with `AllowedRoles`) are never registrable. Anything else is rejected with `invalid_scope`.
