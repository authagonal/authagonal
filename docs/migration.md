---
layout: default
title: Migration
---

# Migration from Duende IdentityServer

The `Authagonal.Migration` package performs a one-time migration from Duende IdentityServer + SQL
Server into Authagonal's stores. The same engine is available two ways:

- **Hosted runner** (recommended) — a background service inside your Authagonal host that runs the
  migration once on deploy, gated on cluster leadership, without blocking startup.
- **CLI** — `tools/Authagonal.Migration.Cli`, for local/offline runs against a Table Storage target.

SqlClient lives only in this package, so hosts that don't migrate never inherit it.

## Hosted runner

Add it after `AddAuthagonal` (it depends on the stores, the secret provider, and cluster leadership):

```csharp
builder.Services.AddAuthagonal(builder.Configuration, c => c.UseAzureStorage(blob, table));
builder.Services.AddAuthagonalDuendeMigration(builder.Configuration);

var app = builder.Build();
app.MapAuthagonalEndpoints();
app.MapAuthagonalDuendeMigration();   // GET /admin/migration/status
```

The second `Map` call is required and separate: this package references `Authagonal.Server`, so
`MapAuthagonalEndpoints` cannot reach it. Without it `GET /admin/migration/status` answers 404 —
indistinguishable from the `IdentityAdmin` policy refusing you — and the run logs a warning at startup
saying so.

Configure via the `Migration` section:

```json
{
  "Migration": {
    "Enabled": true,
    "DryRun": false,
    "Version": "1",
    "UsersMode": "CreateOnly",
    "MigrateClients": true,
    "MigrateRefreshTokens": false,
    "LeaseWaitMinutes": 10,
    "StartupDelaySeconds": 30,
    "Source": { "ConnectionString": "Server=...;Database=Identity;..." }
  }
}
```

The runner:

1. Waits `StartupDelaySeconds` (seed services finish first; startup is never blocked).
2. Skips if a `Completed`, non-`DryRun` marker already exists for `Version`.
3. Waits up to `LeaseWaitMinutes` to become cluster leader (only one pod runs the migration).
4. Writes a `Started` marker, runs the engine, then a `Completed`/`Failed` marker with the report.

Losing leadership mid-run cancels the engine; the new leader re-runs — safe because every pass is
idempotent. Check progress at `GET /admin/migration/status` (gated by the `IdentityAdmin` policy).

## CLI

```bash
docker run authagonal-migration \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  --DryRun true --UsersMode CreateOnly
```

(No `--` separator after the image name.) Or from source:

```bash
dotnet run --project tools/Authagonal.Migration.Cli -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  --DryRun true
```

## What gets migrated

| Source (SQL Server) | Target | Notes |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Users + email/name indexes | Ids preserved verbatim. Claim folding: `given_name`→FirstName, `family_name`→LastName, `company`→CompanyName, `org_id`→OrganizationId (xmlsoap variants too); email claims dropped; everything else → custom attributes. Null password hashes (external-SSO-only users) are fine. BCrypt / ASP.NET Identity V3 hashes verify unchanged and upgrade to native PBKDF2 on next login. |
| `AspNetUserLogins` | UserLogins | `409 Conflict` = skip (idempotent) |
| `AspNetRoles` + `AspNetUserRoles` | Roles + user role links | Role id→name map resolves user assignments |
| `ApiScopes` + `IdentityResources` | Scopes | Existing (seed) names skipped; scope claims copied |
| Duende `Clients` + child tables | Clients | Secrets tagged `SHA256$`/`SHA512$` by digest length (others dropped with a warning); expired secrets skipped; config-seeded clients win (skipped) |
| Duende `ApiResources` | (flattened) | Audiences → migration-created clients; resource claims → migration-created scopes |
| `SamlProviderConfigurations` | SamlProviders + SsoDomains | `AllowedDomains` CSV split into SSO domain records |
| `OidcProviderConfigurations` | OidcProviders + SsoDomains | Same domain splitting |
| `AspNetUserTokens` (`AuthenticatorKey`, `RecoveryCodes`) | MfaCredentials | TOTP secret base32→protected (`duende-totp`); recovery codes hashed (`duende-rc-{n}`); user skipped if MFA already present |
| Duende `PersistedGrants` (refresh tokens) | Grants | **Not possible against stock Duende** — see below. Requires `MigrateRefreshTokens` *and* `SourceGrantKeysAreUnhashed`; otherwise skipped with a warning and users re-login. |

## Options

| Option | Default | Description |
|---|---|---|
| `Enabled` | `false` | Master switch for the hosted runner |
| `DryRun` | `false` | Walk the source and produce the full validation report (id charset/length, duplicate emails, table/column inventory, per-pass counts) without writing |
| `Version` | `"1"` | Run marker. Bump to re-run a delta sweep. Only a `Completed`, non-`DryRun` marker blocks a re-run |
| `UsersMode` | `CreateOnly` | `CreateOnly` skips existing users; `Upsert` overwrites. **Never `Upsert` post-cutover** — it clobbers rehashed passwords and new MFA |
| `MigrateClients` | `true` | Migrate OAuth clients |
| `MigrateRefreshTokens` | `false` | Include active refresh tokens. Requires `SourceGrantKeysAreUnhashed` |
| `SourceGrantKeysAreUnhashed` | `false` | Asserts the source `PersistedGrants.Key` holds handles verbatim. Only true for a fork with a custom grant store |

## Idempotency & delta sweeps

Every pass is idempotent (skip-if-exists, deterministic MFA ids), so the migration is safe to re-run.
Run it days ahead of cutover, then bump `Version` for a final delta sweep close to cutover to pick up
users registered since. Existing records are skipped (or updated under `Upsert`), never duplicated.

## What is NOT migrated

- **Live refresh tokens, against stock Duende.** Duende's `DefaultGrantStore` never persists a
  refresh-token handle: `PersistedGrants.Key` holds `base64(SHA-256(handle + ":" + grantType))`, and
  the presented handle is hashed again on lookup. The handle is therefore not recoverable from the
  source database, and migrated rows would be permanently unredeemable — which is worse than not
  migrating, because the report counts them as created and the breakage only surfaces at the first
  token refresh after cutover. Plan the cutover around one re-login, or run a dual-read shim during
  the transition window. `SourceGrantKeysAreUnhashed` exists only for a fork whose grant store
  persists handles verbatim, and such a fork also owns translating `PersistedGrants.Data` from
  Duende's `RefreshToken` shape into `RefreshTokenData`.
- **SCIM tokens and groups**, **user provisions** — no Duende equivalent; start empty.
- **Signing keys** — not automated. To keep existing tokens valid across cutover, export the RSA
  signing key from Duende and import it into the `SigningKeys` table close to cutover.

## Cutover strategy

1. Deploy dark (`Enabled=false`).
2. `Enabled=true, DryRun=true` → restart → review the report at `/admin/migration/status`.
3. `DryRun=false` → restart → verify the marker is `Completed` + spot-check logins.
4. Bump `Version` for the final delta sweep, then repoint clients/BFFs to Authagonal. **Expect one
   forced re-login** — see below.
5. Monitor; rollback = repoint to the untouched Duende deployment.

## NDJSON user import

A second, independent import source in the same `Authagonal.Migration` package: a flat NDJSON file
(one JSON object per line) instead of a live database connection, and users only — no clients, roles,
scopes or federation config. Built for migrating a legacy app's own user table (a hand-rolled
ASP.NET Identity store, a Rails/Devise table exported to bcrypt, a Node app on scrypt, ...) so people
keep logging in with their old password while it is transparently rehashed to native PBKDF2 on their
next successful login — the same lazy-rehash path the Duende importer above relies on.

### Record schema

One JSON object per line. `email` is the only required field; every other field is optional. **Unknown
top-level fields fail that line** (strict by default) unless `--AllowUnknownFields true` is passed.

| Field | Type | Notes |
|---|---|---|
| `email` | string | Required. Must be a plausible email address. Case-insensitive duplicate key. |
| `username` | string | No dedicated `AuthUser` column — stored in `CustomAttributes["username"]`. |
| `givenName` | string | → `AuthUser.FirstName` |
| `familyName` | string | → `AuthUser.LastName` |
| `displayName` | string | No dedicated column — stored in `CustomAttributes["displayName"]`. |
| `emailVerified` | bool | → `AuthUser.EmailConfirmed`. Defaults to `false` when absent. |
| `passwordHash` | string | → `AuthUser.PasswordHash`, stored **verbatim**. Any format `PasswordHasher` recognises on login (bcrypt `$2a$`/`$2b$`/`$2x$`/`$2y$`, ASP.NET Identity V3, scrypt `$s2$`) verifies unchanged and upgrades to native PBKDF2 from there. Not inspected beyond non-empty — a malformed hash simply fails to verify at login, same as it would outside migration. Omit for SSO-only / passwordless users. |
| `roles` | string[] | → `AuthUser.Roles` |
| `organizationId` | string | → `AuthUser.OrganizationId` |
| `attributes` | object (string→string) | Merged into `AuthUser.CustomAttributes` |
| `phoneNumber` | string | → `AuthUser.Phone` |
| `disabled` | bool | → `AuthUser.IsActive = !disabled`. Defaults to active when absent. |
| `createdAt` | string (ISO 8601) | → `AuthUser.CreatedAt`. Defaults to import time when absent. |
| `externalId` | string | → `AuthUser.ExternalId` — the same field the Duende importer occupies with the source database's user id. |

Example file (5 lines):

```ndjson
{"email":"ada.lovelace@legacy.example.com","givenName":"Ada","familyName":"Lovelace","passwordHash":"$2b$12$KIXQ8N6Qe0m6b6b6b6b6bOQe0m6b6b6b6b6b6b6b6b6b6b6b6b6b6","roles":["admin"],"organizationId":"org-legacy-1","externalId":"42"}
{"email":"bob@legacy.example.com","emailVerified":true,"attributes":{"dept":"eng"},"createdAt":"2019-03-04T00:00:00Z"}
{"email":"carol@legacy.example.com","disabled":true,"phoneNumber":"+61400000000"}
{"email":"dave@legacy.example.com","username":"dave1998","displayName":"Dave K."}
{"email":"erin@legacy.example.com"}
```

### CLI

```bash
dotnet run --project tools/Authagonal.Migration.Cli -- import-ndjson-users \
    --Input ./users.ndjson \
    --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
    --DryRun true \
    --OnDuplicate skip \
    --BatchSize 500 \
    --AllowUnknownFields false \
    --ContinueOnError false \
    --AllowPlaintextPii true
```

Same target (Azure Table Storage) and PII-plaintext gate as the Duende CLI above — this source writes
`AuthUser` rows straight to Table Storage with no host-registered `IFieldCipher`/`IIndexTokenizer`, so
it refuses to run unless `--AllowPlaintextPii true` confirms the target has neither configured (or you
wire `NdjsonUserImportEngine` into the host's own DI container instead, where those seams do resolve).
Unlike the Duende CLI there is no `--AllowPlaintextSecrets` gate: this source never writes MFA TOTP
seeds or OAuth client secrets, only user profile fields and a password hash stored verbatim.

### Options

| Option | Default | Description |
|---|---|---|
| `--Input` | *(required)* | Path to the NDJSON file |
| `--Target:ConnectionString` | *(required)* | Azure Table Storage connection string |
| `--DryRun` | `false` | Parse + validate every line, resolve duplicates against the target, and produce the full report — write nothing |
| `--OnDuplicate` | `skip` | How to treat a line whose email (case-insensitive) already matches an existing user: `skip` (leave it untouched, idempotent), `update` (merge the line's present fields onto the existing user), or `fail` (abort the run outright) |
| `--BatchSize` | `500` | How many lines between progress log lines. Not a write-batching mechanism — `IUserStore` has no bulk API, so every import/update is still one store call |
| `--AllowUnknownFields` | `false` | Accept and ignore top-level JSON properties outside the schema above, instead of failing the line |
| `--ContinueOnError` | `false` | Exit 0 even if one or more lines failed to parse/validate. Does not apply to `--OnDuplicate fail`, which always aborts the run regardless of this flag |

### Summary output & exit codes

The report is printed as JSON: `TotalLines`, `Imported`, `Updated`, `Skipped`, `Failed`, and the first
20 `Failures` (`LineNumber` + `Reason`). Blank lines are not counted anywhere. Exit codes:

- `0` — success (or `--ContinueOnError true` with one or more failed lines)
- `1` — one or more lines failed to parse/validate, and `--ContinueOnError` was not set
- `2` — the run aborted: `--OnDuplicate fail` hit an existing email, or a required option was missing

### Idempotency

The default `--OnDuplicate skip` makes re-running with an unchanged file a no-op the second time:
every line whose email already exists is counted as skipped and nothing is written. `update` is safe to
re-run too (it always re-applies the same fields); `fail` is for a one-shot import that should never
silently collide with existing accounts.

### What is NOT imported

- **Roles, scopes, OAuth clients, federation config.** This source is users only — see the Duende
  importer above if you also need those.
- **MFA credentials, external logins.** Not part of the schema; add them through the standard MFA
  setup / SSO flows after import.
