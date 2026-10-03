---
layout: default
title: Backend-for-Frontend (BFF)
---

# Backend-for-Frontend (BFF)

A browser SPA that holds an access or refresh token in JavaScript-reachable storage exposes both to XSS. The BFF is a **confidential OIDC client you host on your own backend**. It runs the authorization-code + PKCE flow server-side, keeps the tokens in a server-side session, and gives the browser nothing but an httpOnly session cookie. Calls from the SPA to your APIs go through the BFF's proxy, which attaches the session's access token on the way out.

It ships twice, speaking the same protocol:

| Package | For | Source |
|---|---|---|
| `Authagonal.Bff` (NuGet) | ASP.NET Core hosts | `src/Authagonal.Bff/` |
| `@authagonal/bff` (npm) | Express and Next.js (App Router) | `bff-lib/` |

The BFF is an ordinary confidential client of the auth host: it uses OIDC discovery and the authorize, token, revocation and end-session endpoints, so all it needs from the auth host is a registered client.

## 1. Register a BFF client

The client must be **confidential** (it has a secret), require PKCE, and be allowed `offline_access` if you want server-side refresh. Register:

- redirect URI `https://app.example.com/bff/callback`
- post-logout redirect URI `https://app.example.com/` (and `https://app.example.com/bff/logout-callback` if you use `returnUrl` on logout, see [Logout](#logout))

For subject-wide "log out everywhere" via [back-channel logout](index#key-features), register the client with `BackChannelLogoutSessionRequired = false`. The BFF accepts logout tokens carrying `sid` or only `sub`.

## 2. Wire it up (.NET)

```csharp
builder.Services.AddAuthagonalBff(o =>
{
    o.Authority    = "https://auth.example.com";
    o.ClientId     = builder.Configuration["Bff:ClientId"]!;
    o.ClientSecret = builder.Configuration["Bff:ClientSecret"]!;
    o.Scope        = ["openid", "profile", "email", "offline_access"];
    o.PostLogoutRedirectUri = "https://app.example.com/";
});

var app = builder.Build();
app.UseForwardedHeaders();   // required behind a reverse proxy or ingress
app.MapAuthagonalBff();
app.MapFallbackToFile("index.html");
app.Run();
```

`UseForwardedHeaders` matters: behind a TLS-terminating proxy the BFF sees plain http, so without it the `__Host-` session cookie is written without `Secure` and browsers drop it. See [Installation](installation#production-security-checklist) for declaring the proxy.

### Node (Express)

```ts
import { authagonalBff } from '@authagonal/bff/express';

app.set('trust proxy', 1);
app.use(authagonalBff({
  authority: 'https://auth.example.com',
  clientId: process.env.BFF_CLIENT_ID!,
  clientSecret: process.env.BFF_CLIENT_SECRET!,
  scope: ['openid', 'profile', 'email', 'offline_access'],
  cookieSecret: process.env.BFF_COOKIE_SECRET!,   // encrypts the session cookie
  postLogoutRedirectUri: 'https://app.example.com/',
}));
```

For Next.js use `createBffRoute` from `@authagonal/bff/next` in `app/bff/[...bff]/route.ts`. See `bff-lib/README.md` for both.

## Endpoints

Mounted under `BasePath` (default `/bff`).

| Route | Purpose |
|---|---|
| `GET /bff/login?returnUrl=/` | Starts login: sets a per-login correlation cookie and redirects to `/connect/authorize` with PKCE (`S256`), `state` and `nonce`. |
| `GET /bff/callback` | The OIDC redirect URI (`CallbackPath`). Exchanges the code and creates the session. |
| `GET /bff/user` | `{ isAuthenticated, claims, sessionExpiresAt }`. Needs the anti-forgery header. Always `Cache-Control: no-store`. |
| `GET\|POST /bff/logout` | Ends the session locally and at the auth host. A `POST` needs the anti-forgery header; a `GET` is a plain navigation. |
| `GET /bff/logout-callback` | Landing for the end-session round trip when logout was given a `returnUrl`. |
| `POST /bff/backchannel-logout` | Server-to-server consumer for OIDC back-channel logout. Authenticated by the signed logout token, so it takes no CSRF header. |
| `GET /bff/ws-ticket` | Opt-in (`WsTicketsEnabled`), .NET only. See [WebSocket Auth](websocket-auth). |
| `GET /bff/token?resource=...` | Opt-in (`TokenEndpointEnabled`), .NET only. See [Exchanged tokens for another origin](#exchanged-tokens-for-another-origin). |
| `ANY /bff/api/**` | The token-injecting proxy. Mapped only when `Upstreams` is non-empty. |

`claims` on `/bff/user` is a flat string map of the id_token's claims, minus protocol machinery (`iss`, `aud`, `exp`, `iat`, `nbf`, `nonce`, `at_hash`, `c_hash`, `s_hash`, `azp`, `jti`, `sid`, `auth_time`, `acr`, `amr`, `typ`). Array claims such as `roles` and `groups` are space-joined. The claims are re-read from every refreshed id_token, so a role granted after login reaches the SPA at the next refresh rather than the next login.

## From the browser

Every non-navigation call carries a static header, which defends against CSRF alongside `SameSite=Lax`. Any value is accepted; only its presence is checked.

```js
const me = await fetch('/bff/user', { headers: { 'X-Authagonal-Bff': '1' } }).then(r => r.json());
if (!me.isAuthenticated) location.href = '/bff/login?returnUrl=' + encodeURIComponent(location.pathname);
```

Log in and out by **navigating** (`location.href = '/bff/login'`), not by `fetch`. The header name is `AntiForgeryHeader`.

## The proxy

Configure upstreams and the SPA calls `/bff/api/<prefix>/...`:

```csharp
o.Upstreams.Add(new BffUpstream
{
    Prefix = "/orders",
    TargetBaseUrl = "https://api.internal.example.com",
});
```

The proxy requires the anti-forgery header and a live session, refreshes the access token if it is within `RefreshThresholdSeconds` of expiry, forwards the request with `Authorization: Bearer`, and streams the response back. The session cookie is never forwarded. Inbound `X-Forwarded-*`, `Forwarded` and `X-Real-IP` headers are stripped and re-asserted from the BFF's own state, so a script in the SPA cannot vouch for a client IP or scheme. Redirects from the upstream are relayed to the browser rather than followed.

Per upstream (`BffUpstream`):

| Property | Meaning |
|---|---|
| `Prefix` | Path after `/bff/api` that selects this upstream. |
| `TargetBaseUrl` | Where matched requests are forwarded. |
| `StripPrefix` | Drop the matched prefix before appending to the target. Lets one BFF fan out to several backends sharing a path namespace. |
| `RequiredAuthority` | `"type:action"` pairs. The proxy checks the outgoing token's RFC 9396 `authorization_details` and returns 403 unless every pair is permitted. See [Agentic Auth](agentic-auth). |
| `AuthorityLocation` | The `locations` root this upstream is known by, when it differs from `TargetBaseUrl`. |
| `StrictAuthority` | Refuse the call when a grant carries a constraint the proxy cannot evaluate, instead of passing it through. |

Related options: `AllowAnonymousProxyRequests` forwards a request with no session (or an unrefreshable one) without an `Authorization` header instead of answering 401, for APIs that decide for themselves. A route gated by `RequiredAuthority` is never anonymous. `ExchangeRoutes` binds proxy routes to an [RFC 8693 exchange](agentic-auth), so the upstream receives a downscoped, context-bound token instead of the session's primary one: each route has a `PathPattern` with exactly one placeholder (the only supported constraint is `:guid`), the captured segment is sent as the exchange parameter, and a denied exchange is a 403. An unknown constraint fails at startup rather than silently forwarding the broader token.

## Logout

`/bff/logout` revokes the session's refresh token (best effort), removes the session, clears the cookie, and redirects to the auth host's end-session endpoint with the session's `id_token_hint`. With no session there is nothing to end at the auth host, so it redirects straight to `PostLogoutRedirectUri`. With a `returnUrl`, the auth host redirects back to `/bff/logout-callback`, which re-validates the target against `ReturnUrlAllowlist` and redirects there. Register that callback as a post-logout redirect URI for the client.

Back-channel logout removes sessions server-side: by `sid` when the logout token has one, otherwise every session for the `sub`. Removals are scoped to the tenant whose issuer signed the token, because `sub` is unique only within an issuer. The logout token must carry `iat` and be recent.

## Options reference (.NET)

| Option | Default | Notes |
|---|---|---|
| `Authority`, `ClientId`, `ClientSecret` | required | Not required when `TenantQueryParam` is set. |
| `Scope` | `openid profile offline_access` | `offline_access` enables refresh. |
| `BasePath` | `/bff` | |
| `CallbackPath` | `/bff/callback` | Must equal the registered redirect URI. |
| `CookieName` | `__Host-agbff` | The `__Host-` prefix forces Secure, `Path=/` and no Domain, so it needs https. Override for local http development. |
| `SessionLifetime` | 8 hours | Absolute cap regardless of refreshes. |
| `PersistentCookie` | `false` | When true the cookie gets a `Max-Age` bounded to `SessionLifetime` and survives a browser restart ("stay signed in"). Back-channel logout still ends the session. |
| `CorrelationLifetime` | 30 minutes | How long a login may take between `/bff/login` and the callback. Covers sign-up, verification email, sign-in. |
| `RefreshThresholdSeconds` | 60 | |
| `ReturnUrlAllowlist` | empty | Origins a non-relative `returnUrl` may target. Relative paths are always allowed; anything else becomes `/`. |
| `LoginPassthroughParams` | empty | Query parameter names copied from `/bff/login` to `/connect/authorize` (for example `idp_hint`). The standard parameters always win. |
| `AntiForgeryHeader` | `X-Authagonal-Bff` | |
| `PostLogoutRedirectUri` | none | |
| `WsTicketsEnabled`, `WsTicketLifetime`, `TicketExchangeParams` | off, 30 s, empty | See [WebSocket Auth](websocket-auth). |
| `TokenEndpointEnabled`, `TokenEndpointResources`, `TokenEndpointExchangeParams` | off, empty, empty | Enabling it with no resources fails at startup. |
| `Upstreams`, `ExchangeRoutes`, `AllowAnonymousProxyRequests` | empty, empty, `false` | See [The proxy](#the-proxy). |
| `TenantQueryParam` | none | Multi-tenant mode, below. |

`SessionMode` has only `Store` implemented; `Stateless` is reserved and fails at startup.

The Node package takes the camelCase equivalents of `authority`, `clientId`, `clientSecret`, `scope`, `basePath`, `callbackPath`, `cookieName`, `refreshThresholdSeconds`, `returnUrlAllowlist`, `postLogoutRedirectUri`, `antiForgeryHeader`, `sessionLifetimeSeconds`, `upstreams` and `tenantQueryParam`, plus `cookieSecret`, `sessionStore`, `cookieProtector`, `tenantResolver` and `clientIp`. It does not have the websocket-ticket or token endpoints.

## Sessions and running more than one instance

Sessions are stored through `IBffSessionStore`. The default is `IDistributedCache`, in memory unless you register a real cache (Redis, for example) **before** `AddAuthagonalBff`.

A shared cache is not enough on its own. The refresh single-flight is per process, while the session and its rotating refresh token live in the shared cache. Two replicas can read the same session, both find it needs refreshing, and both redeem the same refresh token. The auth host reads the second redemption as a stolen-token replay and revokes the whole grant family, signing the user out everywhere. Supply a cross-replica lock one of two ways:

- **Register an `ILeaseProvider`** (any backend). The Azure, AWS and SQL providers ship one through `AddAuthagonalClustering`. See [Scaling](scaling).
- **Implement `IBffRefreshLockStore` on your session store** (`TryAcquireRefreshLockAsync(sessionId, ttl)` and `ReleaseRefreshLockAsync`). It is a conditional write with a TTL, for example `SET NX PX` on Redis. The default store cannot offer it because `IDistributedCache` has no set-if-absent. The Node session store carries the equivalent `acquireRefreshLock` / `releaseRefreshLock`.

With neither, the deployment relies on the auth host's `Auth:RefreshTokenReuseGraceSeconds`, which defaults to 0 (strict) in the Server host. The BFF logs a warning at startup when the session store looks shared and no lock is present.

A custom `IBffSessionStore` must honour the `tenantKey` argument on `RemoveBySidAsync` and `RemoveBySubjectAsync`. The other seams are `ICookieProtector` (default: ASP.NET Data Protection) and `ITokenClient`.

## Many tenants from one BFF

Set `TenantQueryParam` (for example `"slug"`) and register an `IBffTenantResolver`. `/bff/login?slug=acme` resolves the tenant's `BffTenantConfig` (authority, client id, secret, scope), the key is stored on the session so later requests re-resolve it, and back-channel logout resolves the tenant from the token's `iss` through `ResolveByIssuerAsync`. With `TenantQueryParam` unset the BFF is single-tenant and the static options are used.

## Exchanged tokens for another origin

The cookie model cannot reach a resource server on a different origin (an iframe app the SPA embeds, say). `TokenEndpointEnabled` adds `GET /bff/token?resource=<audience>`, which returns `{ accessToken, expiresInSeconds }` for an RFC 8693 **exchanged** token: addressed to one resource from `TokenEndpointResources` (anything else is a 400 `resource_not_allowed`), bound to any `TokenEndpointExchangeParams` values on the query, and short-lived. The browser never receives the session's primary token, and should keep the exchanged one in memory only. The tenant client needs the token-exchange grant and must declare the resources as its audiences. A denied exchange is a 403.
