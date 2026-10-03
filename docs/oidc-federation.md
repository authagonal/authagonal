---
layout: default
title: OIDC Federation
---

# OIDC Federation

Authagonal can federate authentication to external OIDC identity providers (Google, Apple, Azure AD, etc.). This allows "Login with Google"-style flows while Authagonal remains the central auth server.

## How It Works

There are two entry paths into federation:

**Domain-based (interactive login):**

1. User enters their email on the login page
2. SPA calls `/api/auth/sso-check`, if the email domain is linked to an OIDC provider, SSO is required
3. User clicks "Continue with SSO" and is redirected to the external IdP (when the email is the `login_hint` of an authorize request and its domain is routed to a connection, the user goes straight to the IdP with `login_hint` forwarded)
4. After authenticating, the IdP redirects back to `/oidc/callback`
5. Authagonal validates the id_token, links the user (or creates one if the connection allows JIT provisioning), and sets a session cookie

**RP-hinted (`idp_hint`):**

The downstream relying party can route directly to a specific upstream IdP without going through the email/SSO-domain step. Append `idp_hint={connectionId}` to `/connect/authorize`:

```
/connect/authorize?client_id=my-rp&scope=openid+email&...&idp_hint=google
```

When the request is unauthenticated, Authagonal redirects to `/oidc/{connectionId}/login` with the original `/authorize` URL preserved as `returnUrl`. After federation completes, the user lands back at `/authorize` with a session cookie and the flow proceeds normally. If the connection sets `InteractionPath`, the user is first sent to that login-app page (see [Collect something before federating](self-service-sso#collect-something-before-federating)). A connection with `ShowOnLogin: false` is never offered as a login button and is reachable only this way.

## Setup

### 1. Create an OIDC Provider

**Option A, Configuration (recommended for static setups):**

Add to `appsettings.json`:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

Providers are seeded on startup. `ConnectionId`, `MetadataLocation`, `ClientId` and `ClientSecret` are required (startup fails without them). `RedirectUrl` is accepted for compatibility and ignored: the redirect URI is derived per request as `{Issuer}/oidc/callback`, since it has to be on the origin the browser is on, and that is the URI to register with the IdP (a different seeded value is logged as ignored). The `ClientSecret` is protected via `ISecretProvider` (Key Vault when configured, plaintext otherwise). SSO domain mappings are registered automatically from `AllowedDomains`, except for an organisation-scoped connection, whose domains are matched only within its organisation.

The seed can also set every behavioural flag in the table below. **A seeded entry replaces the stored connection on every start**: a flag you leave out reverts to its default, so state in config every flag you want kept (`ConnectionName`, `IconUrl` and `OrganizationId` are the only values that survive an omission, and `CreatedAt` is preserved).

| Field | Default | Effect |
|---|---|---|
| `JitProvisioningEnabled` | `false` | Create an unknown federated user on first login. Off means an unknown user is rejected with `access_denied` |
| `AllowUninvitedJit` | `false` | With `ProvisioningAttributeParams` declared, also provision a user who arrives without that context. See [Self-Service SSO](self-service-sso) |
| `ProvisioningAttributeParams` | none | Authorize-request query keys captured onto a JIT-provisioned user as provisioning attributes (the inward mirror of `PassthroughParams`) |
| `PassthroughParams` | none | Query keys forwarded onto the upstream authorize URL, see [Passthrough query parameters](#passthrough-query-parameters) |
| `SessionExpClaim` | none | See [Session lifetime cap](#session-lifetime-cap) |
| `ShowOnLogin` | `true` | `false` hides the "Continue with" button; the connection is reached only through `idp_hint` |
| `ChallengeMfaAfterLogin` | `true` | `false` trusts the upstream's own MFA and skips the local challenge |
| `IsExternalConnection` | `false` | Marks a customer-owned third-party IdP. It neutralises `UseUpstreamSubjectAsUserId` and `AutoLinkExistingByEmail` even if set |
| `UseUpstreamSubjectAsUserId` | `false` | A JIT user's local id is the upstream `sub` instead of a new GUID. First-party connections only |
| `AutoLinkExistingByEmail` | `false` | Link to an existing local account by email even when `AllowedDomains` does not cover the domain. First-party connections only |
| `RevalidateOnRefresh` | `false` | See [Federated Sessions](federated-sessions) |
| `InteractionPath` | none | Login-app path shown before federating an `idp_hint` request (must start with `/`) |
| `OrganizationId` | none | Scope the connection to one organisation, see [Self-Service SSO](self-service-sso#organisation-scoped-connections) |

> **An IdP on your own private network.** `MetadataLocation` must be https and, by default, must resolve to a publicly routable address: Authagonal refuses internal targets on every URL it fetches, at the URL and again at the socket. To federate with an on-premises IdP, name it in [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard). That covers the whole exchange, including the `token_endpoint`, `userinfo_endpoint` and `jwks_uri` the discovery document names. https is still required: this document supplies the keys every upstream `id_token` is validated against, and a private network is not a secure channel.

**Option B, Admin API (for runtime management):**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"],
    "jitProvisioningEnabled": true
  }'
```

The create body accepts `connectionName`, `metadataLocation`, `clientId` and `clientSecret` (all required), plus `iconUrl`, `redirectUrl` (ignored, optional), `organizationId`, `allowedDomains`, `passthroughParams`, `jitProvisioningEnabled` (default `false`), `challengeMfaAfterLogin` (default `true`) and `interactionPath`. The connection id is generated by the server and returned in the `201` body (the client secret is never returned). `metadataLocation` must be https and is checked against the outbound-fetch guard at create time. The other flags in the table above (`SessionExpClaim`, `ShowOnLogin`, `IsExternalConnection`, `RevalidateOnRefresh` and the rest) cannot be set through the create route: seed them from configuration or write them through `IOidcProviderStore` from hosting code. There is no update route for an OIDC connection; to change one, delete it and create it again (or edit the seeded config). `GET /api/v1/oidc/connections/{connectionId}` and `DELETE` complete the set.

### 2. SSO Domain Routing

When `AllowedDomains` is specified (in config or via the create API), SSO domain mappings are registered automatically. Without domain routing, users can still be directed to the OIDC login via `/oidc/{connectionId}/login`.

## Endpoints

| Endpoint | Description |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...&loginHint=...` | Initiates OIDC login. Generates PKCE + state + nonce, derives the upstream scope and passthrough params from `returnUrl`, redirects to the IdP's authorization endpoint (`loginHint`, when present, is sent upstream as `login_hint`). `404` for an unknown connection. |
| `GET /oidc/callback` | Handles the IdP callback. Exchanges the code for tokens, validates the id_token, captures every non-protocol claim onto the cookie as `federated:*`, creates/signs in the user. |

## Scope and claim flow-through

The scope set requested by the downstream RP at `/connect/authorize` is forwarded to the upstream IdP, **filtered to the standard OIDC set**: `openid`, `profile`, `email`, `address`, `phone`, with `openid` always included. Anything else the RP requested (custom API scopes, `offline_access`, …) is dropped before the upstream call (the one exception is a connection with `RevalidateOnRefresh`, which adds `offline_access` back so it can obtain an upstream refresh token): a strict IdP like Google returns `invalid_scope` on unknown values, and the upstream only needs to identify the user, the RP's own scopes are honored on Authagonal-issued tokens, not upstream ones. Whatever claims the upstream IdP scope-gates onto the id_token come back to Authagonal, get stashed on the cookie ticket as `federated:<name>` claims, and ride through into `OidcSubject.FederationClaims` at the next `/connect/authorize` traversal. From there `ProtocolTokenService` re-emits them on Authagonal-issued tokens, gated by the same `Scope.UserClaims` whitelist that gates `CustomAttributes`. On key collision the value in Authagonal's own user store wins: these claims arrive verbatim from the upstream IdP, so letting them overwrite would let a customer-controlled IdP restate any scope-released claim about their own user and beat this server's record of it. An upstream claim with no stored counterpart still flows through.

Net effect: no per-connection allowlist of claims to preserve. Every non-protocol claim the upstream puts on the id_token is captured; which of them reach downstream tokens is controlled by the downstream scope's `UserClaims`, declare the claim there and the value flows through.

`FederationClaims` survives refresh rotations distinct from `CustomAttributes`, so per-session federation context (e.g. a share-link token captured at the original authorize) stays intact while per-user attributes still re-read fresh from the user store.

## Passthrough query parameters

`OidcProviderConfig.PassthroughParams` is a per-connection whitelist of query keys that flow through from the original `/authorize` request onto the upstream IdP's authorize URL. The standard set (`scope`, `state`, `nonce`, PKCE) is always forwarded; this is for additional, RP-specified values like a one-shot credential the upstream needs to authenticate (e.g. `link_token` for share-link IdPs).

When a key is whitelisted, Authagonal pulls its value from the original `/authorize` query (carried via `returnUrl`) and appends it to the upstream URL. Anything not on the whitelist is dropped silently.

## Session lifetime cap

`OidcProviderConfig.SessionExpClaim` is the optional name of an id_token claim (Unix seconds) whose value caps the local session lifetime. When present, the upstream value rides through as `session_max_exp` on the cookie ticket and into the issued auth code; access / id / refresh tokens are clamped so no token, including those minted from rotations, outlives the upstream session. Useful when the upstream IdP enforces shorter session bounds than Authagonal would by default.

## Security Features

- **PKCE**: code_challenge with S256 on every authorization request
- **Nonce validation**: nonce stored with the state, must be present in the id_token and match
- **State validation**: single-use (consumed atomically via `IOidcStateStore`, persisted with expiry) **and browser-bound**: a `SameSite=Lax` cookie scoped to `/oidc` is set at login and must match the `state` on the callback, so an attacker can't complete a federation flow they started and deliver the callback URL to a victim (login CSRF)
- **id_token signature validation**: keys fetched from the IdP's JWKS endpoint; issuer, audience and lifetime validated
- **Userinfo fallback**: if the id_token doesn't contain an email, the userinfo endpoint is tried. The userinfo `sub` must match the id_token `sub` (OIDC Core 5.3.2), otherwise the response is ignored
- **Stable identity linking**: a returning user is resolved by provider + `sub`, never by email alone. Attaching a federated identity to a **pre-existing** local account by email requires the connection's `AllowedDomains` to cover that email's domain (the admin's explicit vouch that the IdP owns it) or `AutoLinkExistingByEmail` on a first-party connection, and is refused when the domain is routed to a different connection. An account already bound to another connection's federated identity is only adopted when this connection is the authority for the domain, in which case the old binding is removed. An upstream-asserted `email_verified` is *not* sufficient to seize an existing account
- **Domain enforcement**: when `AllowedDomains` is set, the connection may only assert identities within those domains (`access_denied` otherwise)
- **JIT is opt-in**: unless the connection sets `JitProvisioningEnabled`, an unknown user is rejected with `access_denied`. When JIT does apply, an upstream that does not assert `email_verified` cannot create an account, and neither can a connection whose email domain is routed to a different connection
- **Open-redirect guard**: `returnUrl` must be a same-site relative path; protocol-relative (`//`) and backslash forms are rejected
- **Local MFA still applies by default**: federation proves the first factor only. A user who is MFA-enrolled (or whose client policy requires MFA) is routed through the local MFA challenge/setup pages after the callback instead of being signed straight in; only then does the session carry the MFA marker. A connection with `ChallengeMfaAfterLogin: false` skips this and signs the user in as MFA-authenticated on the federation alone
- **Metadata is trusted narrowly**: the discovery document must be https and its URL bound to the issuer it names, and upstream id_tokens are accepted only with asymmetric signature algorithms (RS/PS/ES 256, 384, 512)
- **Organisation binding**: a user who signs in through an organisation-scoped connection is made a member of that organisation and the session carries its `org_id`

## Azure AD Specifics

Azure AD sometimes returns emails as a JSON array in the `emails` claim (especially for B2C). Authagonal handles this by checking both the `email` claim and the `emails` array (a JSON array or a single string).

## Supported Providers

Any OIDC-compliant provider that supports:
- Authorization Code flow
- PKCE (S256)
- Discovery document (`.well-known/openid-configuration`)

Tested with:
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C

## Related Guides

- [Self-Service SSO](self-service-sso): JIT provisioning postures (invite-only vs. self-service), the connection trust tier, and pre-federation interstitials.
- [Federated Sessions](federated-sessions): propagate upstream revocation to the local session with `RevalidateOnRefresh`.
- [Upgrading a User](user-upgrade): let a federated / guest account claim a first-party password.
