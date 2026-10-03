---
layout: default
title: Provisioning
---

# TCC Provisioning

Authagonal provisions users into downstream applications using the **Try-Confirm-Cancel (TCC)** pattern. This ensures that all apps agree before a user gains access, with clean rollback if any app rejects.

## When Provisioning Runs

Provisioning runs automatically whenever a user is created, regardless of the creation path:

| Endpoint | Trigger |
|---|---|
| `POST /api/v1/profile/` | Admin user creation |
| `POST /api/auth/register` | Self-service registration |
| SAML ACS (`POST /saml/{id}/acs`) | First SSO login (new user) |
| OIDC callback (`GET /oidc/callback`) | First SSO login (new user) |
| SCIM (`POST /scim/v2/Users`) | Identity provider provisioning |
| `GET /connect/authorize` | First authorization through a client with `ProvisioningApps` |

Already-provisioned app/user combinations are skipped (tracked in the `UserProvisions` table).

The user-creation paths provision into **every configured app**. The authorize endpoint provisions only into the client's `ProvisioningApps` list.

**On rejection:** If any provisioning app rejects the user in the Try phase (or a callback fails), the newly created user is deleted. This prevents half-created users. What the caller sees depends on the path:

| Path | Response |
|---|---|
| Admin create (`POST /api/v1/profile/`), self-service register | `422 Unprocessable Entity` with the rejection reason |
| SAML ACS, OIDC callback | `400 Bad Request`, `{ "error": "provisioning_rejected", "message": "..." }` |
| SCIM create | SCIM `400`, `scimType: invalidValue`, with a fixed message (the downstream app's text is not echoed to the identity provider) |
| Passwordless account claim confirmation | `400 provisioning_rejected` as JSON, or a redirect to `/login?error=provisioning_rejected&error_description=...` for a browser click (see [User upgrade](user-upgrade)) |
| `GET /connect/authorize` | Redirect back to the client with `error=access_denied` |

The admin create request accepts `skipProvisioning: true`, for a first-party caller that is itself the provisioning target and does not want its own callback re-entered while it is mid-way through setting the user up. Nothing is provisioned and no apps are called for that user.

## Configuration

### 1. Define Provisioning Apps

In `appsettings.json`:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-bearer-token",
      "TryTimeoutSeconds": 60
    }
  }
}
```

`TryTimeoutSeconds` is optional (default 60). Raise it when the downstream app does real work during Try. Confirm, Cancel and Deprovision always use a short fixed timeout (10 seconds) and are not tunable; they should always be cheap.

The `ProvisioningApps` configuration section is only read when no `IProvisioningAppStore` is registered. The Azure Table, AWS and SQL providers each register one, and the library then resolves apps from the store instead (see [Custom App Resolution](#custom-app-resolution)), so with a persistent provider define apps through the admin API rather than in `appsettings.json`.

### 2. Assign Apps to Clients

Each client declares which apps its users must be provisioned into, via the `provisioningApps` field on the client record. Set it through the client admin API (the `Clients` seed configuration does not carry this field). Creating a client binds the whole record, and `PUT /api/v1/clients/{clientId}` merges the fields you send over the stored client, so a request that carries only `provisioningApps` leaves the rest of the client unchanged:

```
PUT /api/v1/clients/web-app
{
  "provisioningApps": ["my-backend"]
}
```

When a user authorizes through `web-app`, they are provisioned into `my-backend` if they haven't been already.

## TCC Protocol

Authagonal makes three types of HTTP calls to your provisioning endpoint. All use `POST` with JSON bodies and `Authorization: Bearer {ApiKey}`.

### Phase 1: Try

**Request:** `POST {CallbackUrl}/try`

```json
{
  "transactionId": "a1b2c3d4...",
  "userId": "user-id",
  "email": "user@example.com",
  "firstName": "Jane",
  "lastName": "Doe",
  "organizationId": "org-id-or-null",
  "customAttributes": { "key": "value" }
}
```

Null fields (including `customAttributes` when the user has none) are omitted from the payload.

**Expected responses:**

| Status | Body | Meaning |
|---|---|---|
| `200` | `{ "approved": true }` | User can be provisioned. App creates a **pending** record. |
| `200` | `{ "approved": false, "reason": "..." }` | User is rejected. No record created. |
| `2xx` | Empty or unparseable body | Treated as approved. |
| Non-2xx | Any | Treated as failure. |

Return an explicit `approved` value. A response whose body cannot be read as JSON is approved, so a misconfigured endpoint that answers `200` with an HTML page approves every user.

The `transactionId` identifies this provisioning attempt. Your app should store it alongside the pending record.

An approved response may also return `organizationId`, `customAttributes` and `emailVerified`. Authagonal merges them onto the user: `organizationId` is applied only if the user doesn't already have one (later apps in the same transaction see the earlier assignment), `customAttributes` entries are merged key by key, and `emailVerified: true` marks the user's email as confirmed (use it when the downstream app has already verified the address; self-service registration then skips the verification email). Both `organizationId` and the attributes flow onto tokens (`org_id` claim; custom attributes via scope `UserClaims` configuration). The merged values are saved to the user once every app has confirmed.

### Phase 2: Confirm

Called only if **all** apps returned `approved: true` in the try phase.

**Request:** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Expected response:** `2xx` (any body). Your app promotes the pending record to confirmed. A non-2xx response or a timeout (10 seconds) counts as a failed confirm.

### Phase 3: Cancel

Called if **any** app's try was rejected or failed, to clean up the apps that did succeed in the try phase.

**Request:** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Expected response:** `200` (any body). Your app deletes the pending record.

Cancel is best-effort: if it fails, Authagonal logs the error and moves on. Your app should **garbage-collect unconfirmed records after a TTL** (e.g., 1 hour) as a safety net.

## Flow Diagram

```
Authorize Endpoint
    │
    ├─ User authenticated ✓
    ├─ Client requires apps: [A, B]
    ├─ User already provisioned into: [A]
    ├─ Need to provision: [B]
    │
    ├─ TRY B ──────────► App B: create pending record
    │   └─ approved: true
    │
    ├─ CONFIRM B ──────► App B: promote to confirmed
    │   └─ 200 OK
    │
    ├─ Store provision record (userId, "B")
    ├─ Issue authorization code
    └─ Redirect to client
```

### On Failure

```
    ├─ TRY A ──────────► App A: create pending record
    │   └─ approved: true
    │
    ├─ TRY B ──────────► App B: rejects
    │   └─ approved: false, reason: "No license available"
    │
    ├─ CANCEL A ───────► App A: delete pending record
    │
    └─ Redirect with error=access_denied
```

### On Partial Confirm Failure

If a confirm fails, Authagonal rolls the whole transaction back:

1. Apps that have not yet been confirmed receive `POST {CallbackUrl}/cancel`.
2. Apps that already confirmed **in this transaction** are compensated with `DELETE {CallbackUrl}/users/{userId}` (the same call as [deprovisioning](#deprovisioning)), and their provision records are removed. Apps the user was provisioned into by an earlier transaction are left alone.
3. A provisioning error is raised, and the calling path deletes the newly created user (or, for the authorize endpoint, answers with an error).

Provision records are only stored after every confirm has succeeded, so a retry attempts all of the apps again. Compensation is best-effort: a failed `DELETE` is logged and the app account may need manual removal.

## Custom App Resolution

The library picks the app source for you:

- When an `IProvisioningAppStore` is registered, which the Azure Table, AWS and SQL providers all do, apps come from the store (`StoreProvisioningAppProvider`) and are managed through the admin API below.
- Otherwise they are read from the `ProvisioningApps` configuration section (`ConfigProvisioningAppProvider`).

Register your own `IProvisioningAppProvider` before `AddAuthagonal` to resolve apps some other way, for example per tenant; the library's default is added only if none is registered:

```csharp
builder.Services.AddSingleton<IProvisioningAppProvider, MyAppProvider>();
builder.Services.AddAuthagonal(builder.Configuration);
```

The provider returns a list of apps and their callback URLs. The `TccProvisioningOrchestrator` calls Try/Confirm/Cancel on each.

> **`CallbackUrl` must be publicly routable by default.** Authagonal validates it when it is written and again on every request it makes, refusing loopback, RFC1918, link-local and `.internal`/`.local` targets (a provisioning callback is a server-fetched URL). A provisioning app that runs inside your own network is a supported deployment: name it in [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard).

### Admin API

Apps held in the store are managed at `/api/v1/provisioning/apps` (policy `IdentityAdmin`; every change is audited):

| Route | Behaviour |
|---|---|
| `GET /` | `{ "apps": [{ "appId", "name", "callbackUrl", "hasApiKey", "tryTimeoutSeconds" }], "limit": n }`. The API key is never returned, only `hasApiKey`. `limit` is the app quota, null when there is none. |
| `POST /` | Create. `name` and `callbackUrl` are required; `apiKey` and `tryTimeoutSeconds` are optional. A 12-character `appId` is generated. Over the quota, `400 provisioning_app_limit`. |
| `PUT /{appId}` | Replace `name`, `callbackUrl` and `tryTimeoutSeconds` (both `name` and `callbackUrl` are required again). `apiKey` omitted or null leaves the key unchanged; an empty string clears it. `404 app_not_found` for an unknown app. |
| `DELETE /{appId}` | `{ "removed": true }`. |
| `POST /{appId}/test` | Posts a Try with a fixed test user (`test-user`, `test@example.com`) to the app, with a 10 second timeout. Returns `{ "success", "statusCode", "body" }` (body truncated to 1000 characters). Connection failures return `success: false, statusCode: 0` rather than an error status. |

`callbackUrl` must be an absolute `http` or `https` URL on an external host, as described above. `tryTimeoutSeconds` is clamped to 5 to 300 seconds. The `appId` is what a client lists in `provisioningApps`.

## Deprovisioning

When a user is deleted via the admin API (`DELETE /api/v1/profile/{userId}`) or deprovisioned via SCIM (`DELETE /scim/v2/Users/{id}`, a soft-delete that deactivates the user), Authagonal calls `DELETE {CallbackUrl}/users/{userId}` on each app the user was provisioned into, with a 10 second timeout, and removes the provision record. This is best-effort: failures are logged but don't block the deletion. An app that is no longer configured is skipped with a warning.

`ReprovisionAsync` on `IProvisioningOrchestrator` re-runs Try and Confirm for every app even where the user is already provisioned. The library uses it when a passwordless account is claimed (see [User upgrade](user-upgrade)); a plain re-login never does.

## Implementing the Upstream Endpoints

### Minimal Example (Node.js/Express)

```javascript
const pending = new Map(); // transactionId → user data

app.post('/provisioning/try', (req, res) => {
  const { transactionId, userId, email } = req.body;

  // Your business logic: can this user be provisioned?
  if (!isAllowed(email)) {
    return res.json({ approved: false, reason: 'Domain not allowed' });
  }

  // Store pending record with TTL
  pending.set(transactionId, { userId, email, createdAt: Date.now() });

  res.json({ approved: true });
});

app.post('/provisioning/confirm', (req, res) => {
  const { transactionId } = req.body;
  const data = pending.get(transactionId);

  if (data) {
    createUser(data); // Promote to real record
    pending.delete(transactionId);
  }

  res.sendStatus(200);
});

app.post('/provisioning/cancel', (req, res) => {
  pending.delete(req.body.transactionId);
  res.sendStatus(200);
});

// Cleanup unconfirmed records older than 1 hour
setInterval(() => {
  const cutoff = Date.now() - 3600000;
  for (const [id, data] of pending) {
    if (data.createdAt < cutoff) pending.delete(id);
  }
}, 600000);
```
