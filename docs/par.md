---
layout: default
title: Pushed Authorization Requests
---

# Pushed Authorization Requests (PAR)

[RFC 9126](https://www.rfc-editor.org/rfc/rfc9126) lets a client POST its authorize-request parameters directly to the server with standard client authentication and receive a short-lived opaque `request_uri` to hand to the browser. The browser then visits `/connect/authorize?request_uri=...&client_id=...` instead of carrying every parameter on the URL.

Why use it:

- Authorize parameters never appear in browser history, server logs, or `Referer` headers.
- The server authenticates the client at push time, so the parameters are integrity-checked before any redirect happens.
- Long parameter sets (large `claims` requests, multi-resource flows) don't blow URL length limits.

## Endpoint

```
POST /connect/par
Content-Type: application/x-www-form-urlencoded
```

Authentication is the same as `/connect/token`: HTTP Basic with `client_id`/`client_secret`, or form-encoded credentials. Confidential clients must authenticate; public clients post without a secret. Client-authentication failures return `401` (per RFC 9126, unlike the token endpoint, where only `invalid_client` is a 401).

The form body carries the same parameters that would normally go on `/connect/authorize` (`response_type`, `redirect_uri`, `scope`, `state`, `code_challenge`, `code_challenge_method`, `nonce`, `resource`, etc.). `request_uri` itself is rejected, chaining a PAR is forbidden by §2.1 of the spec. If the body carries a `client_id`, it must match the authenticated client. Like the token endpoint, the route refuses a plaintext `http` request unless `AuthagonalProtocolOptions.AllowInsecureHttp` is set.

The request is validated at push time, the same way `/connect/authorize` would validate it (registered `redirect_uri`, allowed scopes, PKCE, `prompt` values and so on). An invalid request is refused immediately with `400 invalid_request` and no `request_uri` is issued, so the mistake surfaces to the client instead of to the end user mid-flow. `authorization_details` is refused with `invalid_authorization_details` (rich authorization requests belong on the token endpoint, not here).

### Limits

- The body is capped at 32 KB, with at most 64 form fields, 256-character names and 8 KB per value. Anything larger is refused with `413 invalid_request`.
- Requests are rate limited at 60 per minute per client and source address, and 300 per minute per client in total, answering `429 temporarily_unavailable`.

### Response

```
HTTP/1.1 201 Created
```
```json
{
  "request_uri": "urn:ietf:params:oauth:request_uri:abc123...",
  "expires_in": 90
}
```

The `request_uri` is single-use. It's removed from the store when the authorization code is issued for it. If it is never redeemed, it expires after 90 seconds.

### Authorization step

```
GET /connect/authorize?client_id=my-rp&request_uri=urn:ietf:params:oauth:request_uri:abc123...
```

When `request_uri` is present, all other parameters are pulled from the pushed payload, anything else on the URL is ignored (other than `client_id`, which must match the client that pushed the payload, and the `error` parameter a failed federation round trip appends). A `request_uri` that is unknown, expired, already consumed or pushed by a different client is refused with `invalid_request`. Only the opaque URNs this server's own PAR endpoint issued are accepted: any other `request_uri` value is refused with `request_uri_not_supported`, and the RFC 9101 `request` parameter with `request_not_supported`.

Pushed `prompt` and `max_age` values are honoured. A PAR request carrying `prompt=login` (or a `max_age` the session has outlived) is satisfied only by a session whose `auth_time` is at or after the moment the request was pushed, so a pre-existing session is signed out and re-authenticated once, and the return trip from login issues a code instead of looping.

## Requiring PAR per client

Set `RequirePushedAuthorizationRequests = true` on a client to refuse plain `/connect/authorize` requests from it. Any non-PAR authorize attempt returns `invalid_request` with the description "This client requires requests to be pushed via /connect/par".

```csharp
new OAuthClient
{
    ClientId = "high-risk-rp",
    RequirePushedAuthorizationRequests = true,
    // ...
}
```

This is the recommended posture for clients that handle sensitive scopes, combined with PKCE, it removes the URL bar as an attack surface.

## Lifetime and storage

The `expires_in` returned from the push is 90 seconds, and that window covers the hop from the push to the first `/connect/authorize` request. Once the record is first picked up, it is extended (once) to an absolute deadline of 15 minutes from the push, so the user can finish login, MFA and consent. The 90 and 15 minute values are constants, not configuration. Pushed payloads are stored via the same `IGrantStore` as auth codes and refresh tokens, so they inherit the host's persistence and replication strategy automatically.

## Discovery

The PAR endpoint advertises itself in `.well-known/openid-configuration` as:

```json
{
  "pushed_authorization_request_endpoint": "https://auth.example.com/connect/par"
}
```
