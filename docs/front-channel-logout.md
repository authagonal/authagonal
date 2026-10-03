---
layout: default
title: Front-Channel Logout
---

# Front-Channel Logout

Authagonal implements **OpenID Connect Front-Channel Logout 1.0**, a browser-driven logout mechanism that complements [back-channel logout](index#key-features). Where back-channel logout is a server-to-server POST, front-channel logout renders the logout URL of each relying party in a hidden iframe so that each app's browser session (cookies, local storage) is cleaned up from inside the user's browser.

## When to Use Which

| Concern | Back-Channel | Front-Channel |
|---|---|---|
| Server-side sessions | ✅ | ❌ |
| Browser cookies / local storage | ❌ | ✅ |
| Works when the user's browser is offline | ✅ | ❌ |
| Survives network errors (retry) | ✅ | ❌ (single best-effort attempt) |

Most apps benefit from configuring **both**. Back-channel guarantees the server is told; front-channel clears the browser.

## Client Configuration

Add a front-channel logout URI to the `OAuthClient` record:

```json
{
  "clientId": "myapp",
  "frontChannelLogoutUri": "https://myapp.example.com/oidc/frontchannel",
  "frontChannelLogoutSessionRequired": true
}
```

| Field | Description |
|---|---|
| `FrontChannelLogoutUri` | The client's browser-visible logout endpoint |
| `FrontChannelLogoutSessionRequired` | If `true` (default), the URL is called with `iss` and `sid` query parameters so the client can correlate the logout with the specific session |

## How It Works

When the browser visits `/connect/endsession` (GET or POST):

1. **Confirmation (CSRF guard).** If the browser has a signed-in session and the request does not carry an `id_token_hint` whose `sub` matches that session, the server first renders a "sign out?" page with a confirm button instead of logging the user out. The button POSTs back with a short-lived (15 minutes) token bound to that session. This is what stops a third-party page from ending a user's session by navigating to the endpoint (the session cookie is `SameSite=Lax`, so it accompanies a cross-site top-level GET). A matching `id_token_hint` stands in for the confirmation.
2. The server finds all clients the user currently has grants with.
3. For each client with a `FrontChannelLogoutUri` that passes the outbound-URL check (loopback is allowed because the user's own browser makes the request, but private-range and link-local addresses are not), the server builds a URL, appending `iss=<issuer>` (and `sid=<session_id>`, when the session has one) if `FrontChannelLogoutSessionRequired` is `true`.
4. The server revokes the grants minted for the session, signs the user out of the authorization-server cookie, triggers back-channel logout notifications in the background, and, when at least one front-channel URL was built, returns an HTML page containing a hidden `<iframe>` for each:
   ```html
   <iframe src="https://myapp.example.com/oidc/frontchannel?iss=https%3A%2F%2Fauth.example.com&sid=abc123" style="display:none"></iframe>
   ```
   The page carries a `Content-Security-Policy` whose `frame-src` is limited to the origins of those URLs, and no scripts.
5. The post-logout destination is resolved the same way whether or not iframes are involved. The `post_logout_redirect_uri` is honored only when the request identifies the client (through the `id_token_hint` audience, or the `client_id` parameter) and the URI is in that client's registered `PostLogoutRedirectUris` (a `state` parameter, if supplied, is appended). With iframes, the page waits 2 seconds (a `meta refresh`) and then redirects, or shows a "signed out" message when there is no valid destination. With no front-channel URLs, the server redirects immediately (`302`), or answers `200` with a JSON `message` when there is no valid destination.

`id_token_hint` is accepted only if it is an ID token this server signed (ES256, `typ: JWT`) with a single audience. Expired tokens are accepted. Access tokens, and logout tokens, are rejected as hints. If both `client_id` and `id_token_hint` are sent and name different clients, the request fails with `400 invalid_request`.

The JSON endpoint `POST /api/auth/logout` (used by the login app's Sign out button) runs the same revocation and notification steps. It does not render iframes: it returns the URLs in `frontchannel_logout_uris` for the caller to load (see the [Auth API](auth-api#logout)).

## Client-Side Logout Handler

Each relying party should implement the URL referenced by `FrontChannelLogoutUri`. A minimal handler:

```http
GET /oidc/frontchannel?iss=https://auth.example.com&sid=abc123
```

1. Verify `iss` matches the expected authorization server.
2. If `sid` is provided, confirm it matches the session cookie's session ID.
3. Clear the local session (cookies, server-side session, SPA storage).
4. Respond with `200 OK` and an empty body (or a tiny page), the response is never visible to the user.

```csharp
app.MapGet("/oidc/frontchannel", (HttpContext ctx) =>
{
    var iss = ctx.Request.Query["iss"].ToString();
    var sid = ctx.Request.Query["sid"].ToString();
    // Validate iss/sid, then clear local session
    ctx.SignOutAsync();
    return Results.Ok();
});
```

## Discovery Document

Front-channel logout is advertised in `/.well-known/openid-configuration`:

```json
{
  "frontchannel_logout_supported": true,
  "frontchannel_logout_session_supported": true
}
```

## Dynamic Client Registration

Clients registered via [Dynamic Client Registration](client-registration) may include:

```json
{
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

Registration refuses a logout URI that is not an external address (loopback, link-local, private-range and `.localhost`/`.local`/`.internal` names are rejected with `invalid_client_metadata`).

## Limitations

- **Best effort**: iframes are loaded once. If a network error or browser extension blocks them, there is no retry. Pair with back-channel logout for reliability.
- **Third-party cookies**: some browsers block cookies in cross-site iframes by default. If your RP relies on first-party cookies, confirm the logout handler does not depend on cookies being sent.
- **Timeout**: the page waits ~2 seconds before redirecting. Heavy RP logout handlers may not complete in time.

## Related

- [Dynamic Client Registration](client-registration), front-channel parameters in the registration request
- [OAuth Scopes](scopes), scope-aware consent complements the logout flow
