---
layout: default
title: Self-Service SSO
---

# Self-Service SSO Onboarding

Once you've [federated a connection](oidc-federation) to a customer's IdP, the next
question is: **what happens when someone who has never logged in before shows up?** Authagonal gives you
three postures for that unknown user, from strictest to most open (reject every unknown user, require invite
context, or auto-provision from an allowed domain), plus the controls to keep an *external* IdP from becoming
a foot-gun. The first two are covered together under Posture 1, the third under Posture 2. This guide is about picking and wiring the posture you want.

All of it is per-connection config: the `OidcProviders` and `SamlProviders` seed sections, the stored
`OidcProviderConfig` / `SamlProviderConfig`, and the admin API. The relevant knobs:

| Knob | Effect | Protocols |
|---|---|---|
| `JitProvisioningEnabled` | May an unknown user be created at all? | OIDC, SAML |
| `ProvisioningAttributeParams` | Require *invite context* on the request before creating one. | OIDC, SAML |
| `AllowUninvitedJit` | Allow self-service creation with **no** invite (tagged with the connection). | OIDC, SAML |
| `IsExternalConnection` | Mark a third-party IdP so the first-party-only flags can't apply. | OIDC only |
| `InteractionPath` | Show a login-app page (name/terms) *before* federating. | OIDC only |

Where each can be set matters, because the admin API does not expose all of them:

- **Config seed (`OidcProviders`, `SamlProviders`):** every knob above that exists for the protocol. Seeded
  connections are re-applied from configuration on every start, so for a seeded connection
  `JitProvisioningEnabled` and `AllowUninvitedJit` come from the seed, not from whatever was last stored.
- **SAML admin API** (`POST` / `PUT /api/v1/saml/connections`): `JitProvisioningEnabled`,
  `ProvisioningAttributeParams` and `AllowUninvitedJit`.
- **OIDC admin API** (`POST /api/v1/oidc/connections`): `JitProvisioningEnabled` and `InteractionPath`
  (which must start with `/`). `ProvisioningAttributeParams`, `AllowUninvitedJit` and `IsExternalConnection`
  are seed-only on OIDC, and there is no update route for an OIDC connection. See
  [Admin API](admin-api) and [OIDC Federation](oidc-federation).

## Posture 1: invite-only (reject the uninvited)

The default. With `JitProvisioningEnabled: false`, an unknown SSO user is rejected outright
(`access_denied`, "contact your administrator"), which is what you want when every user must be pre-created
by an admin or SCIM.

If you want JIT but *only* when an invite is present, enable JIT **and** declare
`ProvisioningAttributeParams`. Those name the whitelisted `/authorize` query params that carry the invite
context (e.g. `acceptKind`, `acceptToken`). An unknown user is provisioned only when at least one of those
params actually arrived with a value; a bare SSO login with no invite is rejected with `access_denied`
("This login requires an invitation"), so a stray login can't silently self-provision a new account/org.
The params are read from the query of the `/authorize` URL the user is returning to (the `RelayState` for
SAML); OIDC also falls back to the callback request's own query.

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "ConnectionName": "Acme (Entra)",
      "MetadataLocation": "https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration",
      "ClientId": "…", "ClientSecret": "…",
      "AllowedDomains": ["acme.com"],
      "JitProvisioningEnabled": true,
      "ProvisioningAttributeParams": ["acceptKind", "acceptToken"]
    }
  ]
}
```

There is no `RedirectUrl` to set: the callback `redirect_uri` is derived per request as
`{issuer}/oidc/callback`, so register that URI with the upstream IdP. A seeded `RedirectUrl` is ignored.

The captured params land on the JIT user's `CustomAttributes` and reach your
[provisioning `Try` handler](provisioning), which is the real gate on the *values* (e.g.
"does this invite token match this email?"). Authagonal captures whitelisted keys; your provisioner decides
if they're valid. If `Try` answers `approved: false`, the just-created user is deleted and the browser gets
`400 provisioning_rejected`.

## Posture 2: self-service (auto-provision an allowed-domain user)

For "any employee of a customer can just log in and get an account," set `AllowUninvitedJit: true`. Now an
unknown user from an **allowed domain** is provisioned even without invite context, and Authagonal tags them
with the connection they came through so your provisioner can place them in the right tenant instead of
spinning up a new one. The domain check applies only when `AllowedDomains` is non-empty: a connection that
lists no domains accepts whatever domain its IdP asserts, so list them on every self-service connection.

```json
{
  "ConnectionId": "acme-entra",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true,
  "ProvisioningAttributeParams": ["acceptKind", "acceptToken"],
  "AllowUninvitedJit": true
}
```

The tag arrives as a `federated_connection` custom attribute. Its value is the connection's `ConnectionName`
(not its `ConnectionId`), and it is written only when the user was created with no invite context, so an
invited user carries the captured params instead. Your `Try` handler branches on it:

```javascript
app.post('/provisioning/try', async (req, res) => {
  const { userId, email, customAttributes } = req.body;

  if (customAttributes?.acceptToken) {
    // Invited: validate the invite and add them to that org.
    const org = await validateInvite(customAttributes.acceptToken, email);
    if (!org) return res.json({ approved: false, reason: 'Invalid invite' });
    stage(userId, { orgId: org.id, role: customAttributes.acceptKind ?? 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  if (customAttributes?.federated_connection) {
    // Self-service: no invite, but they came through a known enterprise connection.
    const org = await orgForConnection(customAttributes.federated_connection);
    stage(userId, { orgId: org.id, role: 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  return res.json({ approved: false, reason: 'No invite and no known connection' });
});
```

`AllowUninvitedJit` is opt-in per connection: a connection that declares `ProvisioningAttributeParams` but
does **not** set it stays invite-only.

Two more checks run before any unknown user is created, whichever posture you chose:

- **The domain must not belong to another connection.** If the SSO domain index routes the user's email
  domain to a different connection, the login is refused with `access_denied` ("This email domain is
  managed by a different identity provider").
- **OIDC only: the upstream must have verified the email.** When the upstream does not report `email_verified` as true (read from the id_token, or from the
  userinfo response when the email came from there), the login is
  refused with `access_denied`. A SAML assertion has no such flag, so SAML relies on `AllowedDomains`
  instead.

`federated_connection` is a reserved attribute name. It is never emitted on a token, an OIDC upstream's
id_token claim of that name is dropped, and anonymous self-service registration cannot set it, so only the SSO
callbacks can assert which connection an account came through.

## Keep external IdPs from becoming foot-guns

A few OIDC connection flags are safe on a connection **you** control but dangerous on an arbitrary third-party
IdP:

- **`UseUpstreamSubjectAsUserId`**: the upstream chooses the local user id. On your own share-link
  provider that keeps ids aligned; on a customer's IdP it lets *them* pick your user ids.
- **`AutoLinkExistingByEmail`**: attach a federated login to a pre-existing local account by email,
  skipping the domain-ownership check. Inbox-verified and first-party, fine; on an external IdP it's an
  account-takeover lever.

Mark third-party connections **external** and those flags are neutralised even if set:

```json
{
  "ConnectionId": "acme-entra",
  "IsExternalConnection": true,
  "UseUpstreamSubjectAsUserId": false,
  "AutoLinkExistingByEmail": false
}
```

`IsExternalConnection` defaults to `false` (first-party) so existing connections are unaffected. Set it on
every OIDC connection that points at someone else's IdP; a later misconfiguration then can't hand that IdP
control over local identities. SAML connections have none of these flags, so there is nothing to neutralise
there. (Attaching a federated identity to a pre-existing account still additionally requires the
connection's `AllowedDomains` to vouch for the email's domain: see
[OIDC Federation: Security](oidc-federation).)

## Collect something before federating

Sometimes you need to show the user a page **before** bouncing them to the IdP: a guest's display name, a
terms checkbox, a plan picker. `InteractionPath` (OIDC connections only) names a login-app route to render
first:

```json
{ "ConnectionId": "guest-link", "InteractionPath": "/guest" }
```

When an unauthenticated `idp_hint={ConnectionId}` request hits `/connect/authorize`, Authagonal redirects to
`{LoginAppUrl}{InteractionPath}?returnUrl=<authorize url>&connection={id}` instead of straight to the IdP
(`LoginAppUrl` defaults to `/login`, and the path must start with `/`). The same redirect happens when
an [organisation's](#organisation-scoped-connections) sole or domain-matched connection is auto-challenged,
and when `prompt=login` forces a re-authentication through an `idp_hint`. Your page collects what it needs,
appends the values to the `returnUrl`'s query (where `PassthroughParams` /
`ProvisioningAttributeParams` read them from), and continues to `/oidc/{id}/login` itself. A page that
decides no interaction is needed can continue immediately.

## Organisation-scoped connections

Everything above describes a **tenant-level** connection: one the whole tenant shares, whose
`AllowedDomains` claim an email domain for every login screen the tenant serves. That is the right
shape when you federate to one customer per tenant. It is the wrong shape when one tenant serves many
customer [organizations](organizations) and each brings its own IdP: two customers cannot both claim
`contoso.com`, and one customer's "Continue with Contoso Entra" button has no business on another's
login screen.

Set `OrganizationId` on a connection and it belongs to that organization instead (on create and, for SAML,
update, through the admin API; an organization that does not exist is `400 unknown_organization`):

```json
{
  "ConnectionId": "acme-entra",
  "OrganizationId": "org_7f3a9c",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true
}
```

Three things change, and nothing else does.

**It is offered only when that organization is selected.** An org-scoped connection never appears on
the tenant's own login screen and is never reached by a request that resolved to no organization,
even one whose `login_hint` matches its domains exactly.

**Its domains are matched only within that organization.** An org-scoped connection is deliberately
*not* written to the tenant-wide SSO domain index, so a domain may be claimed once at tenant level and
once per organization. A second claim inside one organization is still refused with `domain_claimed`,
across both protocols, so one address cannot route to two of an organization's IdPs. Moving a connection
into an organization removes its index rows; moving it back (send `"organizationId": ""` to the SAML update
endpoint) re-registers them. OIDC connections have no update route, so their scope is set at create time or
in the `OidcProviders` seed.

**Everyone who signs in through it is a member of it.** The SAML ACS and the OIDC callback stamp the
connection's organization as `org_id`, overriding the account's own `AuthUser.OrganizationId` (which
is a downstream provisioning artefact rather than an assertion about this sign-in), and create an
active membership if the user holds none. An **invited** membership is accepted: it becomes `active` and
keeps its roles, inviter and invitation time, because the organization's own IdP has now vouched for the
person. Any other existing membership is left alone: a `suspended` row stays suspended, so signing in
again cannot restore access an administrator revoked.

### Which organization a request is for, before anyone signs in

Home-realm discovery has to answer this before there is a user, so it resolves separately from (but in
the same order as) the [post-authentication selector](organizations#precedence):

1. The **`organization` parameter** on the request (a slug or an id).
2. **`OAuthClient.RestrictedToOrganizationIds`**, when it holds exactly one entry. Two or more is not a
   selection: the client serves several and the request named none.
3. **`ITenantContext.OrganizationId`**: a host that pins one per request, e.g. a per-organization
   custom domain. `null` in every single-tenant deployment.

The organization must exist and be enabled, and the client's restriction must permit it. Anything else
resolves to *no organization* and the request continues on the tenant-wide path exactly as before. In
particular, a parameter naming an organization the client is restricted away from is not refused here:
the refusal already exists after authentication (`access_denied`), and moving it in front of the login
screen would change which requests an unauthenticated caller can tell apart.

### What `/connect/authorize` does with it

With an organization resolved, and before any tenant-wide rule:

- `idp_hint` naming one of **its** connections goes straight to that connection. That includes SAML, which
  the tenant-wide hint path (OIDC only) cannot reach. A hint naming anything else falls through.
- **Exactly one connection and no contradicting `login_hint`** → straight to it. A connection listing no
  domains claims the whole organization; one that lists domains is still auto-challenged unless the
  hinted address's domain is not among them.
- **Several connections** → the hinted email domain picks between them.
- **No match** → the tenant-wide `login_hint` and login-card behaviour, unchanged.

A federation that failed and bounced back with `error=` on the query returns that error to the relying
party rather than federating again, so an auto-challenge cannot loop.

### What the login app sees

`/api/auth/providers` and `/api/auth/sso-check` both take an `organization` query parameter (and fall
back to `ITenantContext.OrganizationId`), resolved by the same rules. With an organization:

- `providers` lists **that organization's** button connections first, then the tenant's own. A connection
  is a button only when it lists no `AllowedDomains` (and, for OIDC, has `ShowOnLogin` on); domain-routed
  ones are reached email-first through `sso-check`. Org-scoped connections are excluded from the list
  entirely when no organization is resolved, and other organizations' connections are never listed.
- `providers` gains **`autoChallenge`** when the organization has exactly one connection: a full
  provider record (`connectionId`, `name`, `type`, `loginUrl`, `iconUrl`) for the connection the app
  should go straight to, skipping the card. It carries the whole record rather than a bare id because
  that connection may be domain-routed or hidden and therefore absent from `providers`. The field is
  omitted otherwise, and it is **advisory**: `/connect/authorize` performs the same auto-challenge itself,
  so an app that ignores it still reaches the same IdP.
- `sso-check` matches the organization's connections' domains **before** the tenant-wide index, and
  falls through to it when the organization claims nothing for that address. A sole connection that lists
  no domains claims every address.

### At token issuance

A session established through an org-scoped connection carries its organization as the highest-priority
source after a refresh's carried value (above the `organization` parameter and above the client's
restriction) because it is the only one that was *proven*: the user authenticated at an IdP belonging
to exactly that organization. A request naming a different one is refused with `access_denied` rather
than quietly issued the other. The organization's `RequireMembershipForTokens` still applies, which is
why the callback creates the membership.

## Related

- [Organizations](organizations): the records, memberships, claims and selection rules.
- [OIDC Federation](oidc-federation): setting up the connection and the security model.
- [TCC Provisioning](provisioning): the `Try` handler these flows call.
- [Keeping federated sessions in sync](federated-sessions): revoking local sessions when the upstream does.
