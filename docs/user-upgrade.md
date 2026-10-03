---
layout: default
title: Upgrading a User
---

# Upgrading a User (Passwordless Account Claim)

Some accounts start life without a password:

- a **guest** who opened a share link and was created just-in-time by a federated login,
- a user **JIT-provisioned** by an SSO sign-in or an org invite,
- a directory user pushed in over **SCIM**.

Each is a real Authagonal user (stable id, usually with downstream access already provisioned) that
simply has no local credential. The way in *was* the federation, the link, or the invite.

**Upgrading** such a user lets them set a first-party password and, usually, promotes their relationship
with your product at the same time (guest → standard member, trial → paid, "create your organization").
Authagonal ships this as a first-class, opt-in flow: the person re-registers with the same email, proves
they control the inbox, and their **existing** account is claimed *in place* (same user id, so all their
prior access survives) while your app runs whatever upgrade logic it needs.

> This is deliberately not the same as "reset your password." A password reset assumes a credentialed
> account and emails a reset link. A claim turns a *credential-less* account into a credentialed one and
> re-runs provisioning, so your downstream can react to the promotion.

## When to use it

Enable the claim flow when a downstream product treats "someone registering with a federated identity's
email" as a legitimate upgrade path, the classic case being a share-link guest who decides to create a
real account. If your deployment has no such path, leave it off (the default): every existing email is
then treated as a duplicate, and registration returns the normal enumeration-neutral response.

## 1. Enable it

The claim is gated by a single opt-in option in the `Auth` configuration section (bound to `AuthOptions`):

```json
{
  "Auth": {
    "AllowPasswordlessAccountClaim": true,
    "ClaimAllowedAttributeKeys": ["org_name", "plan"]
  }
}
```

- **`AllowPasswordlessAccountClaim`** (default `false`): turns the flow on.
- **`ClaimAllowedAttributeKeys`** (default empty = allow all non-reserved keys): a whitelist of the
  custom-attribute keys a claim may carry onto the account (see
  [Passing upgrade context](#4-pass-upgrade-context-safely)). List the keys your provisioner expects so a
  claim can't inject arbitrary attributes. Despite its name, the same list also filters the
  `customAttributes` of an ordinary self-service registration.

With the flag off, an existing email is a duplicate. With it on, an existing **credential-less** account
(no `PasswordHash`) is claimable; an account that already has a password is **never** touched: a
re-register can't overwrite a real credential.

## 2. The claim, end to end

The user calls the ordinary registration endpoint with the email of the account they want to claim:

```bash
# 1. The user re-registers with the SAME email as their guest/SSO/invite account.
curl -X POST https://auth.example.com/api/auth/register \
  -H "Content-Type: application/json" \
  -d '{
    "email": "grace@acme.com",
    "password": "a-strong-passphrase",
    "firstName": "Grace",
    "lastName": "Hopper",
    "customAttributes": { "org_name": "Acme Inc" }
  }'
# → 201 Created (enumeration-neutral: the same response a brand-new signup returns)
```

The response is `201` with `{ "success": true, "userId": "..." }`. On the claim path the `userId` is a
throwaway value, not the real account id, so the response cannot be used to tell a claim from a brand-new
signup.

Nothing is live yet. The server **stages** the password and the profile/attributes and emails a fresh
verification link. The user opens it:

```
GET https://auth.example.com/api/auth/confirm-email?token=<from the email>
```

That `GET` only renders a one-click confirmation page (so mail scanners and link prefetchers that fetch
the URL do not consume the token). **Pressing the button** on that page submits
`POST /api/auth/confirm-email` and is what promotes the staged credential and runs the upgrade. The same
`POST` also accepts the token as a query parameter or a JSON body (`{ "token": "..." }`); a JSON caller
gets `{ "message": "Email confirmed successfully.", "appLink": ... }`, while the form post from the page
redirects to `/login?email_confirmed=1`. After it, the user signs in normally with their new password.

### What the server does

1. **Register**: because the account exists and has no password, the request is treated as a claim. The
   chosen password is hashed into `PendingPasswordHash` (inert, no auth path reads it), and the first and
   last name plus whitelisted `customAttributes` are staged in `PendingClaimJson`. The account's security
   stamp is rotated at the same moment, which invalidates any verification link already sitting in an
   inbox. The account is **not** modified otherwise. A verification email is sent even though the
   account's email was already confirmed by its original flow: that prior proof belonged to a *different*
   actor, and the claim needs its own. The link carries a `pc=` digest of the credential staged for it.
2. **Confirm**: the confirmation is the ownership proof. The server checks the link is bound to the
   *currently* staged credential, applies the staged profile/attributes, runs **`ReprovisionAsync`** (see
   next section), then promotes `PendingPasswordHash` to `PasswordHash` and rotates the security stamp
   again. If provisioning rejects the upgrade, the staged credential and profile are discarded and the
   account stays passwordless and re-claimable, so nothing partial persists.

If a second claim is submitted before the first is confirmed, it replaces the staged credential and the
first link stops working: confirming it answers `claim_superseded` (JSON `400`, or a redirect to
`/login?error=claim_superseded` from the confirmation page). A link with no `pc=` digest, such as one from
an admin "send verification email" action, also fails this way while a credential is staged. In both cases
the user asks for a fresh link by registering again.

The user id never changes, so guest project access, SCIM linkage, group memberships, all of it, survive
the upgrade.

## 3. Do the upgrade downstream

A claim confirm calls `ReprovisionAsync`, which, unlike normal provisioning, re-runs the
[TCC Try/Confirm/Cancel](provisioning) cycle **even for apps the user is already
provisioned into**. That is the whole point: your app was already provisioned this user as a *guest*, so a
plain provision would skip them; reprovision gives you a second Try, now carrying the signup context, so
you can promote them.

Your provisioning `Try` handler distinguishes "first provision" from "upgrade" by whether it already has a
record for that `userId`, and reacts to the context the claim carried (here, `org_name`):

```javascript
// POST {CallbackUrl}/try
app.post('/provisioning/try', async (req, res) => {
  const { transactionId, userId, email, customAttributes } = req.body;
  const existing = await db.members.findByAuthId(userId);

  if (!existing) {
    // First time we've seen this user: a plain new signup.
    stagePending(transactionId, { userId, email, role: 'member' });
    return res.json({ approved: true });
  }

  if (existing.kind === 'guest') {
    // UPGRADE: the guest is claiming a real account. Create their org from the signup context,
    // and stage the promotion (applied in /confirm). Reject to abort the whole claim if it can't proceed.
    const orgName = customAttributes?.org_name;
    if (!orgName) return res.json({ approved: false, reason: 'Organization name is required' });

    stagePending(transactionId, { userId, upgradeTo: 'standard', orgName });
    // Return org_id so Authagonal stamps it on the user's tokens (org_id claim).
    const orgId = deterministicOrgId(userId);
    return res.json({ approved: true, organizationId: orgId });
  }

  // Already a full member: nothing to do, but approve so the claim completes.
  res.json({ approved: true });
});

// POST {CallbackUrl}/confirm: all apps approved; commit the promotion.
app.post('/provisioning/confirm', async (req, res) => {
  const p = takePending(req.body.transactionId);
  if (p?.upgradeTo === 'standard') {
    await db.orgs.create({ id: deterministicOrgId(p.userId), name: p.orgName, ownerAuthId: p.userId });
    await db.members.promote(p.userId, { kind: 'standard' });
  }
  res.sendStatus(200);
});

// POST {CallbackUrl}/cancel: the claim failed elsewhere; drop the staged promotion.
app.post('/provisioning/cancel', (req, res) => { takePending(req.body.transactionId); res.sendStatus(200); });
```

An `approved: false` from any app (or a failed callback) makes the confirm fail with `400 provisioning_rejected`
(a JSON body for API callers, or a redirect to `/login?error=provisioning_rejected&error_description=...` from
the confirmation page) and leaves the account un-upgraded, still passwordless and still claimable. A
non-claim registration that a provisioning app rejects is a `422`; see [TCC Provisioning](provisioning).
An `organizationId` (or extra `customAttributes`) in the approved response is merged onto the user and
rides their tokens.

## 4. Pass upgrade context safely

The `customAttributes` on the register call are how the claim carries signup context (org name, plan,
referral) to your provisioner. They are **staged**, applied only on the verification click, and filtered by
`ClaimAllowedAttributeKeys`. Keep that whitelist tight: it is the boundary that stops someone who merely
*knows* a federated user's email from injecting attributes that would ride the real owner's tokens. An
empty whitelist allows every non-reserved key (convenient for trusted first-party flows); a populated one
drops anything not listed.

Whatever the whitelist says, the filter always applies these limits, and silently drops anything that
breaks them (the registration still succeeds):

- at most 32 attributes, with keys up to 64 characters and values up to 1024 characters;
- these reserved keys are never accepted, even if listed in `ClaimAllowedAttributeKeys`: `federated_connection`,
  `org_id`, `roles`, `groups`, `sub`, `iss`, `aud`, `scope`, `client_id`, `sid`, `acr`, `amr`, `email`,
  `email_verified`.

The same filter runs on ordinary (non-claim) self-service registration.

## Security properties

- **Knowing the email is not enough.** The claim only completes when the account's own inbox receives and
  confirms the verification link. An attacker who knows the address never gets the email.
- **One staged credential at a time.** The link is bound to the credential staged for it, so a later claim
  cannot be promoted by an earlier link (`claim_superseded`).
- **A real credential is never overwritten.** Only a `PasswordHash`-less account is claimable; a claim
  against a credentialed account is the normal enumeration-neutral duplicate response.
- **Nothing is live until confirmed.** The staged password can't authenticate, and the staged
  profile/attributes aren't applied, until the confirmation. A rejected upgrade rolls everything back.
- **Attribute injection is bounded** by `ClaimAllowedAttributeKeys`.

## Related

- [TCC Provisioning](provisioning): the Try/Confirm/Cancel contract your handler implements.
- [Self-service SSO](self-service-sso): the JIT flows that create the passwordless accounts in the first place.
