---
layout: default
title: Promouvoir un utilisateur
locale: fr
---

# Promouvoir un utilisateur (réclamation d'un compte sans mot de passe)

Certains comptes naissent sans mot de passe :

- un **invité** qui a ouvert un lien de partage et a été créé juste-à-temps par une connexion fédérée,
- un utilisateur **provisionné juste-à-temps (JIT)** par une connexion SSO ou une invitation à une organisation,
- un utilisateur d'annuaire poussé via **SCIM**.

Chacun est un véritable utilisateur Authagonal (identifiant stable, généralement avec un accès en aval déjà
provisionné) qui n'a simplement aucun identifiant de connexion local. Son point d'entrée *était* la fédération, le
lien ou l'invitation.

**Promouvoir** un tel utilisateur lui permet de définir un mot de passe propre et, en général, fait évoluer en même
temps sa relation avec votre produit (invité → membre standard, essai → payant, « créer votre organisation »).
Authagonal fournit ce mécanisme sous la forme d'un flux à part entière, à activer explicitement : la personne se
réinscrit avec la même adresse e-mail, prouve qu'elle contrôle la boîte de réception, et son compte **existant** est
réclamé *sur place* (même identifiant utilisateur, de sorte que tous ses accès antérieurs sont conservés) pendant
que votre application exécute la logique de promotion dont elle a besoin.

> Ce n'est délibérément pas la même chose que « réinitialiser votre mot de passe ». Une réinitialisation suppose un
> compte doté d'identifiants et envoie un lien de réinitialisation par e-mail. Une réclamation transforme un compte
> *sans identifiant* en compte doté d'identifiants et relance le provisionnement, afin que votre système en aval
> puisse réagir à la promotion.

## Quand l'utiliser {#when-to-use-it}

Activez le flux de réclamation lorsqu'un produit en aval considère « une personne qui s'inscrit avec l'adresse
e-mail d'une identité fédérée » comme une voie de promotion légitime, le cas typique étant un invité arrivé par un
lien de partage qui décide de créer un vrai compte. Si votre déploiement ne prévoit pas une telle voie, laissez-le
désactivé (valeur par défaut) : toute adresse e-mail existante est alors traitée comme un doublon, et l'inscription
renvoie la réponse habituelle qui ne permet pas l'énumération des comptes.

## 1. L'activer {#1-enable-it}

La réclamation est commandée par une seule option à activer explicitement dans la section de configuration `Auth`
(liée à `AuthOptions`) :

```json
{
  "Auth": {
    "AllowPasswordlessAccountClaim": true,
    "ClaimAllowedAttributeKeys": ["org_name", "plan"]
  }
}
```

- **`AllowPasswordlessAccountClaim`** (`false` par défaut) : active le flux.
- **`ClaimAllowedAttributeKeys`** (vide par défaut = toutes les clés non réservées sont autorisées) : une liste
  blanche des clés d'attributs personnalisés qu'une réclamation peut apporter au compte (voir
  [Transmettre le contexte de promotion](#4-pass-upgrade-context-safely)). Listez les clés qu'attend votre
  provisionneur afin qu'une réclamation ne puisse pas injecter d'attributs arbitraires. Malgré son nom, cette même
  liste filtre aussi les `customAttributes` d'une inscription ordinaire en libre-service.

Lorsque l'option est désactivée, une adresse e-mail existante est un doublon. Lorsqu'elle est activée, un compte
existant **sans identifiant** (pas de `PasswordHash`) peut être réclamé ; un compte qui possède déjà un mot de
passe n'est **jamais** modifié : une réinscription ne peut pas écraser un identifiant réel.

## 2. La réclamation de bout en bout {#2-the-claim-end-to-end}

L'utilisateur appelle le point de terminaison d'inscription ordinaire avec l'adresse e-mail du compte qu'il veut
réclamer :

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

La réponse est un `201` avec `{ "success": true, "userId": "..." }`. Dans le cas d'une réclamation, le `userId` est
une valeur jetable et non l'identifiant réel du compte, de sorte que la réponse ne permet pas de distinguer une
réclamation d'une inscription entièrement nouvelle.

Rien n'est encore actif. Le serveur **met en attente** le mot de passe ainsi que le profil et les attributs, et envoie
par e-mail un nouveau lien de vérification. L'utilisateur l'ouvre :

```
GET https://auth.example.com/api/auth/confirm-email?token=<from the email>
```

Ce `GET` affiche seulement une page de confirmation en un clic (afin que les analyseurs de messagerie et les
préchargeurs de liens qui récupèrent l'URL ne consomment pas le jeton). C'est **l'appui sur le bouton** de cette page
qui envoie `POST /api/auth/confirm-email`, promeut l'identifiant en attente et exécute la promotion. Ce même `POST`
accepte aussi le jeton en paramètre de requête ou dans un corps JSON (`{ "token": "..." }`) ; un appelant JSON reçoit
`{ "message": "Email confirmed successfully.", "appLink": ... }`, tandis que l'envoi du formulaire depuis la page
redirige vers `/login?email_confirmed=1`. Ensuite, l'utilisateur se connecte normalement avec son nouveau mot de passe.

### Ce que fait le serveur {#what-the-server-does}

1. **Inscription** : comme le compte existe et n'a pas de mot de passe, la requête est traitée comme une
   réclamation. Le mot de passe choisi est haché dans `PendingPasswordHash` (inerte : aucune voie d'authentification
   ne le lit), et le prénom et le nom ainsi que les `customAttributes` autorisés par la liste blanche sont mis en
   attente dans `PendingClaimJson`. Le tampon de sécurité du compte est renouvelé au même moment, ce qui invalide tout
   lien de vérification se trouvant déjà dans une boîte de réception. Le compte n'est **pas** modifié par ailleurs. Un
   e-mail de vérification est envoyé même si l'adresse du compte a déjà été confirmée par son flux d'origine : cette
   preuve antérieure appartenait à un *autre* acteur, et la réclamation a besoin de la sienne. Le lien porte un
   condensé `pc=` de l'identifiant mis en attente pour lui.
2. **Confirmation** : la confirmation constitue la preuve de propriété. Le serveur vérifie que le lien est lié à
   l'identifiant *actuellement* en attente, applique le profil et les attributs en attente, exécute
   **`ReprovisionAsync`** (voir la section suivante), puis promeut `PendingPasswordHash` en `PasswordHash` et
   renouvelle de nouveau le tampon de sécurité. Si le provisionnement rejette la promotion, l'identifiant et le profil
   en attente sont abandonnés, et le compte reste sans mot de passe et réclamable : rien de partiel ne subsiste.

Si une seconde réclamation est soumise avant que la première ne soit confirmée, elle remplace l'identifiant en
attente et le premier lien cesse de fonctionner : sa confirmation répond `claim_superseded` (`400` en JSON, ou une
redirection vers `/login?error=claim_superseded` depuis la page de confirmation). Un lien dépourvu de condensé `pc=`,
comme celui produit par une action d'administration « envoyer l'e-mail de vérification », échoue de la même manière
tant qu'un identifiant est en attente. Dans les deux cas, l'utilisateur obtient un nouveau lien en s'inscrivant de
nouveau.

L'identifiant utilisateur ne change jamais ; l'accès aux projets en tant qu'invité, le rattachement SCIM, les
appartenances aux groupes, tout cela survit à la promotion.

## 3. Effectuer la promotion en aval {#3-do-the-upgrade-downstream}

La confirmation d'une réclamation appelle `ReprovisionAsync` qui, contrairement au provisionnement normal, relance le
cycle [TCC Try/Confirm/Cancel](provisioning) **même pour les applications dans lesquelles l'utilisateur est déjà
provisionné**. C'est tout l'intérêt : votre application avait déjà provisionné cet utilisateur en tant qu'*invité* ;
un provisionnement ordinaire l'ignorerait donc ; le reprovisionnement vous donne un second Try, portant désormais le
contexte d'inscription, pour que vous puissiez le promouvoir.

Votre gestionnaire de provisionnement `Try` distingue « premier provisionnement » et « promotion » selon qu'il possède
déjà un enregistrement pour ce `userId`, et réagit au contexte que la réclamation a apporté (ici, `org_name`) :

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

Un `approved: false` renvoyé par n'importe quelle application (ou un rappel en échec) fait échouer la confirmation avec
`400 provisioning_rejected` (un corps JSON pour les appelants d'API, ou une redirection vers
`/login?error=provisioning_rejected&error_description=...` depuis la page de confirmation) et laisse le compte non
promu, toujours sans mot de passe et toujours réclamable. Une inscription hors réclamation qu'une application de
provisionnement rejette donne un `422` ; voir [Provisionnement TCC](provisioning). Un `organizationId` (ou des
`customAttributes` supplémentaires) dans la réponse d'approbation est fusionné dans l'utilisateur et transporté dans
ses jetons.

## 4. Transmettre le contexte de promotion en toute sécurité {#4-pass-upgrade-context-safely}

Les `customAttributes` de l'appel d'inscription sont le moyen par lequel la réclamation transmet le contexte
d'inscription (nom de l'organisation, offre, parrainage) à votre provisionneur. Ils sont **mis en attente**, appliqués
uniquement lors du clic de vérification, et filtrés par `ClaimAllowedAttributeKeys`. Gardez cette liste blanche
restreinte : c'est la frontière qui empêche quelqu'un qui se contente de *connaître* l'adresse e-mail d'un utilisateur
fédéré d'injecter des attributs qui se retrouveraient dans les jetons du véritable propriétaire. Une liste blanche
vide autorise toutes les clés non réservées (pratique pour les flux first-party de confiance) ; une liste renseignée
écarte tout ce qui n'y figure pas.

Quel que soit le contenu de la liste blanche, le filtre applique toujours ces limites, et écarte silencieusement tout
ce qui les enfreint (l'inscription réussit tout de même) :

- 32 attributs au maximum, avec des clés de 64 caractères au plus et des valeurs de 1024 caractères au plus ;
- les clés réservées suivantes ne sont jamais acceptées, même si elles figurent dans `ClaimAllowedAttributeKeys` :
  `federated_connection`, `org_id`, `roles`, `groups`, `sub`, `iss`, `aud`, `scope`, `client_id`, `sid`, `acr`,
  `amr`, `email`, `email_verified`.

Le même filtre s'applique à l'inscription ordinaire en libre-service (hors réclamation).

## Propriétés de sécurité {#security-properties}

- **Connaître l'adresse e-mail ne suffit pas.** La réclamation n'aboutit que lorsque la boîte de réception du compte
  lui-même reçoit et confirme le lien de vérification. Un attaquant qui connaît l'adresse ne reçoit jamais l'e-mail.
- **Un seul identifiant en attente à la fois.** Le lien est lié à l'identifiant mis en attente pour lui ; une
  réclamation ultérieure ne peut donc pas être promue par un lien antérieur (`claim_superseded`).
- **Un identifiant réel n'est jamais écrasé.** Seul un compte dépourvu de `PasswordHash` peut être réclamé ; une
  réclamation visant un compte doté d'identifiants reçoit la réponse de doublon habituelle, qui ne permet pas
  l'énumération.
- **Rien n'est actif avant la confirmation.** Le mot de passe en attente ne permet pas de s'authentifier, et le profil
  et les attributs en attente ne sont pas appliqués, tant que la confirmation n'a pas eu lieu. Une promotion rejetée
  annule tout.
- **L'injection d'attributs est bornée** par `ClaimAllowedAttributeKeys`.

## Voir aussi {#related}

- [Provisionnement TCC](provisioning) : le contrat Try/Confirm/Cancel qu'implémente votre gestionnaire.
- [SSO en libre-service](self-service-sso) : les flux JIT qui créent les comptes sans mot de passe au départ.
