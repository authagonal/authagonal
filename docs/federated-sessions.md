---
layout: default
title: Federated Sessions
---

# Keeping Federated Sessions in Sync With the Upstream

When a user signs in through an [external IdP](oidc-federation), Authagonal issues its
*own* session and tokens. By default that local session then lives its own life: if the customer disables
the user in their directory, or the guest's share link is revoked upstream, the local Authagonal session
keeps working until its cookie expires.

For offboarding and revocation to propagate quickly, turn on **`RevalidateOnRefresh`**. Then, on every local
token refresh, Authagonal redeems the upstream refresh token against the IdP, and if the upstream says the
credential is gone, the local refresh is refused and the session stops getting new tokens within one
access-token lifetime.

`RevalidateOnRefresh` is a setting of **OIDC connections** only. SAML has no refresh token to redeem, so a SAML
connection cannot revalidate; bound those sessions with the assertion's own session expiry instead (see
[SAML](saml)).

## Enable it

Per connection (seeded from configuration as below, or set when creating the connection through the
[Admin API](admin-api)), and the upstream must actually issue a refresh token:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "MetadataLocation": "https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration",
      "ClientId": "...", "ClientSecret": "...",
      "AllowedDomains": ["acme.com"],
      "RevalidateOnRefresh": true
    }
  ]
}
```

That's the whole setup. When the flag is on, Authagonal adds `offline_access` to the scope it requests from the
upstream (if the downstream request did not already carry it), stashes the refresh token the upstream returns,
and redeems it server-to-server on each local refresh. The upstream refresh token is **never** emitted to a
client. It lives encrypted in a durable per-session store, seeded at login with a flat seven-day expiry (the
absolute session cap), and is used only for revalidation.

The upstream has to cooperate: if its app registration never receives `offline_access` (for example, consent
was not granted), no refresh token comes back and there is nothing to redeem. Authagonal logs a warning
(`RevalidateOnRefresh is enabled for connection ... but no upstream refresh token is held`) on each refresh in
that state, and the upstream is **not** re-checked.

## What happens on refresh

1. The RP refreshes an Authagonal token as usual (`grant_type=refresh_token` at `/connect/token`).
2. Authagonal redeems the upstream refresh token at the IdP's token endpoint:
   - **Success** → the local refresh proceeds; if the upstream rotated its token, the new one is stored and
     shared by every RP grant in the session.
   - **`invalid_grant`** → the federated credential is gone (user disabled, session revoked, token expired).
     The local refresh is **refused**: the RP gets `invalid_grant` from `/connect/token`, and the stored
     upstream token is deleted. The refusal fails that request only; it does not revoke the Authagonal grant,
     so the RP's refresh token is left unconsumed and keeps being refused while the upstream stays revoked.
   - **Any other 4xx** (e.g. `invalid_client` from a rotated/misconfigured secret, or a 429), a 5xx, an
     unparseable error body, a transport failure, or a connection that cannot be loaded (deleted, discovery
     or secret failure) → treated as **transient**: the session survives so an operator fault doesn't
     mass-log-out every federated user. Fix the config; nothing is lost. The session is still bounded by the
     absolute session cap.

Because there is **one** upstream token per browser session (keyed by user + connection + session), a
second RP that the user opens reads and rotates the *same* token: so one app's refresh can't leave another
app holding a dead copy.

## Nothing to implement

There is no interface to write here. The durable store
(`IUpstreamRefreshTokenStore`) is registered automatically by the Azure, AWS and SQL storage providers, and the
redemption is internal. You only flip `RevalidateOnRefresh` on the connections whose upstream owns a
revocable credential.

The stored token is removed when the session ends by any route that reaches it: sign-out, revoking a session
from the account page, "sign out everywhere", and the expiry sweep. If a host registers no store, the copy
carried on the session cookie is the fallback.

Every OIDC-federated session also records which connection owns it (the `upstream_connection_id` claim),
whether or not that connection revalidates. That is bookkeeping only: nothing is redeemed for a connection
without the flag.

> **Reach:** this feature is active wherever the store is registered (the Azure Table, DynamoDB and SQL
> providers). Enable it on connections to a **trusted** upstream, in particular one that one-time-rotates
> its refresh tokens (Entra, Auth0), and combine it with `IsExternalConnection` for third-party IdPs (see
> [Self-service SSO](self-service-sso)).

## Complementary: a hard session cap

`RevalidateOnRefresh` keeps a session honest against upstream revocation. If instead (or additionally) you
want the local session to never *outlive* the upstream's asserted session, set `SessionExpClaim` to the name
of an id_token claim carrying an expiry (Unix seconds). Authagonal clamps the local session and all tokens
minted from it (including refresh rotations, and including a device-code grant approved through that session)
to that bound. See [OIDC Federation → Session lifetime cap](oidc-federation).

The device flow is worth naming because it was the exception until recently: the device-code record had nowhere
to carry the approving session's bound, so a device approved through a federated session kept minting tokens for
the client's full absolute refresh lifetime after that session had ended, and `RevalidateOnRefresh` never
re-asked the upstream for it either. Both now follow the approving session. A device approved through a
*non-federated* session has no bound to inherit, which is the same result the authorize flow gives.
