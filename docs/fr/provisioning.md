---
layout: default
title: Provisionnement
locale: fr
---

# Provisionnement TCC

Authagonal provisionne les utilisateurs dans les applications en aval selon le modèle **Try-Confirm-Cancel (TCC)**. Celui-ci garantit que toutes les applications donnent leur accord avant qu'un utilisateur obtienne l'accès, avec une annulation propre si l'une d'elles refuse.

## Quand le provisionnement s'exécute {#when-provisioning-runs}

Le provisionnement s'exécute automatiquement à chaque création d'utilisateur, quelle que soit la voie de création :

| Point de terminaison | Déclencheur |
|---|---|
| `POST /api/v1/profile/` | Création d'un utilisateur par un administrateur |
| `POST /api/auth/register` | Inscription en libre-service |
| SAML ACS (`POST /saml/{id}/acs`) | Première connexion SSO (nouvel utilisateur) |
| Callback OIDC (`GET /oidc/callback`) | Première connexion SSO (nouvel utilisateur) |
| SCIM (`POST /scim/v2/Users`) | Provisionnement par le fournisseur d'identité |
| `GET /connect/authorize` | Première autorisation via un client doté de `ProvisioningApps` |

Les combinaisons application/utilisateur déjà provisionnées sont ignorées (suivi dans la table `UserProvisions`).

Les voies de création d'utilisateur provisionnent dans **toutes les applications configurées**. Le point de terminaison d'autorisation ne provisionne que dans la liste `ProvisioningApps` du client.

**En cas de refus :** si une application de provisionnement refuse l'utilisateur lors de la phase Try (ou si un rappel échoue), l'utilisateur nouvellement créé est supprimé. Cela évite les utilisateurs à moitié créés. Ce que voit l'appelant dépend de la voie :

| Voie | Réponse |
|---|---|
| Création par un administrateur (`POST /api/v1/profile/`), inscription en libre-service | `422 Unprocessable Entity` avec le motif du refus |
| SAML ACS, callback OIDC | `400 Bad Request`, `{ "error": "provisioning_rejected", "message": "..." }` |
| Création SCIM | `400` SCIM, `scimType: invalidValue`, avec un message fixe (le texte de l'application en aval n'est pas renvoyé au fournisseur d'identité) |
| Confirmation de la réclamation d'un compte sans mot de passe | `400 provisioning_rejected` en JSON, ou une redirection vers `/login?error=provisioning_rejected&error_description=...` pour un clic dans le navigateur (voir [Promouvoir un utilisateur](user-upgrade)) |
| `GET /connect/authorize` | Redirection vers le client avec `error=access_denied` |

La requête de création par un administrateur accepte `skipProvisioning: true`, destiné à un appelant first-party qui est lui-même la cible du provisionnement et ne souhaite pas que son propre rappel soit invoqué alors qu'il est en train de configurer l'utilisateur. Rien n'est alors provisionné et aucune application n'est appelée pour cet utilisateur.

## Configuration {#configuration}

### 1. Définir les applications de provisionnement {#1-define-provisioning-apps}

Dans `appsettings.json` :

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

`TryTimeoutSeconds` est facultatif (60 par défaut). Augmentez-le lorsque l'application en aval effectue un vrai travail pendant le Try. Confirm, Cancel et Deprovision utilisent toujours un délai d'expiration court et fixe (10 secondes), non réglable ; ces appels doivent toujours rester peu coûteux.

La section de configuration `ProvisioningApps` n'est lue que si aucun `IProvisioningAppStore` n'est enregistré. Les fournisseurs Azure Table, AWS et SQL en enregistrent chacun un, et la bibliothèque résout alors les applications depuis ce stockage (voir [Résolution personnalisée des applications](#custom-app-resolution)) ; avec un fournisseur persistant, définissez donc les applications via l'API d'administration plutôt que dans `appsettings.json`.

### 2. Affecter des applications aux clients {#2-assign-apps-to-clients}

Chaque client déclare dans quelles applications ses utilisateurs doivent être provisionnés, via le champ `provisioningApps` de l'enregistrement du client. Définissez-le via l'API d'administration des clients (la configuration d'initialisation `Clients` ne porte pas ce champ). La création d'un client lie l'enregistrement complet, et `PUT /api/v1/clients/{clientId}` fusionne les champs envoyés avec le client stocké ; une requête qui ne porte que `provisioningApps` laisse donc le reste du client inchangé :

```
PUT /api/v1/clients/web-app
{
  "provisioningApps": ["my-backend"]
}
```

Lorsqu'un utilisateur donne son autorisation via `web-app`, il est provisionné dans `my-backend` s'il ne l'a pas déjà été.

## Protocole TCC {#tcc-protocol}

Authagonal effectue trois types d'appels HTTP vers votre point de terminaison de provisionnement. Tous utilisent `POST` avec un corps JSON et `Authorization: Bearer {ApiKey}`.

### Phase 1 : Try {#phase-1-try}

**Requête :** `POST {CallbackUrl}/try`

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

Les champs nuls (y compris `customAttributes` lorsque l'utilisateur n'en a pas) sont omis de la charge utile.

**Réponses attendues :**

| Statut | Corps | Signification |
|---|---|---|
| `200` | `{ "approved": true }` | L'utilisateur peut être provisionné. L'application crée un enregistrement **en attente**. |
| `200` | `{ "approved": false, "reason": "..." }` | L'utilisateur est refusé. Aucun enregistrement n'est créé. |
| `2xx` | Corps vide ou illisible | Traité comme une approbation. |
| Hors 2xx | Quelconque | Traité comme un échec. |

Renvoyez une valeur `approved` explicite. Une réponse dont le corps ne peut pas être lu comme du JSON vaut approbation ; un point de terminaison mal configuré qui répond `200` avec une page HTML approuve donc tous les utilisateurs.

Le `transactionId` identifie cette tentative de provisionnement. Votre application doit le conserver avec l'enregistrement en attente.

Une réponse d'approbation peut aussi renvoyer `organizationId`, `customAttributes` et `emailVerified`. Authagonal les fusionne dans l'utilisateur : `organizationId` n'est appliqué que si l'utilisateur n'en a pas déjà un (les applications suivantes dans la même transaction voient l'affectation précédente), les entrées de `customAttributes` sont fusionnées clé par clé, et `emailVerified: true` marque l'adresse e-mail de l'utilisateur comme confirmée (utilisez-le lorsque l'application en aval a déjà vérifié l'adresse ; l'inscription en libre-service omet alors l'e-mail de vérification). `organizationId` comme les attributs se retrouvent dans les jetons (revendication `org_id` ; attributs personnalisés via la configuration `UserClaims` des scopes). Les valeurs fusionnées sont enregistrées dans l'utilisateur une fois que toutes les applications ont confirmé.

### Phase 2 : Confirm {#phase-2-confirm}

Appelée uniquement si **toutes** les applications ont renvoyé `approved: true` lors de la phase Try.

**Requête :** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Réponse attendue :** `2xx` (corps quelconque). Votre application fait passer l'enregistrement en attente à l'état confirmé. Une réponse hors 2xx ou un délai dépassé (10 secondes) compte comme un échec de confirmation.

### Phase 3 : Cancel {#phase-3-cancel}

Appelée si le Try d'**une quelconque** application a été refusé ou a échoué, afin de nettoyer les applications dont le Try a réussi.

**Requête :** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Réponse attendue :** `200` (corps quelconque). Votre application supprime l'enregistrement en attente.

Cancel est exécuté au mieux : s'il échoue, Authagonal journalise l'erreur et poursuit. Par sécurité, votre application doit **purger les enregistrements non confirmés au-delà d'une durée de vie** (par exemple 1 heure).

## Diagramme de flux {#flow-diagram}

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

### En cas d'échec {#on-failure}

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

### En cas d'échec partiel de la confirmation {#on-partial-confirm-failure}

Si une confirmation échoue, Authagonal annule l'ensemble de la transaction :

1. Les applications qui n'ont pas encore été confirmées reçoivent `POST {CallbackUrl}/cancel`.
2. Les applications déjà confirmées **dans cette transaction** font l'objet d'une compensation avec `DELETE {CallbackUrl}/users/{userId}` (le même appel que pour le [déprovisionnement](#deprovisioning)), et leurs enregistrements de provisionnement sont supprimés. Les applications dans lesquelles l'utilisateur avait été provisionné par une transaction antérieure ne sont pas touchées.
3. Une erreur de provisionnement est levée, et la voie appelante supprime l'utilisateur nouvellement créé (ou, pour le point de terminaison d'autorisation, répond par une erreur).

Les enregistrements de provisionnement ne sont stockés qu'une fois toutes les confirmations réussies ; une nouvelle tentative reprend donc toutes les applications. La compensation est exécutée au mieux : un `DELETE` en échec est journalisé, et le compte dans l'application peut nécessiter une suppression manuelle.

## Résolution personnalisée des applications {#custom-app-resolution}

La bibliothèque choisit pour vous la source des applications :

- Lorsqu'un `IProvisioningAppStore` est enregistré, ce que font tous les fournisseurs Azure Table, AWS et SQL, les applications proviennent du stockage (`StoreProvisioningAppProvider`) et sont gérées via l'API d'administration décrite ci-dessous.
- Sinon, elles sont lues depuis la section de configuration `ProvisioningApps` (`ConfigProvisioningAppProvider`).

Enregistrez votre propre `IProvisioningAppProvider` avant `AddAuthagonal` pour résoudre les applications autrement, par exemple par locataire ; l'implémentation par défaut de la bibliothèque n'est ajoutée que si aucune n'est enregistrée :

```csharp
builder.Services.AddSingleton<IProvisioningAppProvider, MyAppProvider>();
builder.Services.AddAuthagonal(builder.Configuration);
```

Le fournisseur renvoie une liste d'applications et leurs URL de rappel. Le `TccProvisioningOrchestrator` appelle Try/Confirm/Cancel sur chacune.

> **Par défaut, `CallbackUrl` doit être routable publiquement.** Authagonal la valide lors de son écriture, puis à nouveau à chaque requête qu'il émet, en refusant les cibles loopback, RFC1918, link-local et `.internal`/`.local` (un rappel de provisionnement est une URL que le serveur va chercher). Une application de provisionnement exécutée au sein de votre propre réseau est un déploiement pris en charge : nommez-la dans [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard).

### API d'administration {#admin-api}

Les applications conservées dans le stockage se gèrent sur `/api/v1/provisioning/apps` (politique `IdentityAdmin` ; chaque modification est auditée) :

| Route | Comportement |
|---|---|
| `GET /` | `{ "apps": [{ "appId", "name", "callbackUrl", "hasApiKey", "tryTimeoutSeconds" }], "limit": n }`. La clé d'API n'est jamais renvoyée, seulement `hasApiKey`. `limit` est le quota d'applications, nul en l'absence de quota. |
| `POST /` | Création. `name` et `callbackUrl` sont obligatoires ; `apiKey` et `tryTimeoutSeconds` sont facultatifs. Un `appId` de 12 caractères est généré. Au-delà du quota, `400 provisioning_app_limit`. |
| `PUT /{appId}` | Remplace `name`, `callbackUrl` et `tryTimeoutSeconds` (`name` et `callbackUrl` sont de nouveau obligatoires). Un `apiKey` omis ou nul laisse la clé inchangée ; une chaîne vide l'efface. `404 app_not_found` pour une application inconnue. |
| `DELETE /{appId}` | `{ "removed": true }`. |
| `POST /{appId}/test` | Envoie à l'application un Try avec un utilisateur de test fixe (`test-user`, `test@example.com`), avec un délai d'expiration de 10 secondes. Renvoie `{ "success", "statusCode", "body" }` (corps tronqué à 1000 caractères). Les échecs de connexion renvoient `success: false, statusCode: 0` plutôt qu'un statut d'erreur. |

`callbackUrl` doit être une URL `http` ou `https` absolue pointant vers un hôte externe, comme décrit ci-dessus. `tryTimeoutSeconds` est borné entre 5 et 300 secondes. L'`appId` est la valeur qu'un client liste dans `provisioningApps`.

## Déprovisionnement {#deprovisioning}

Lorsqu'un utilisateur est supprimé via l'API d'administration (`DELETE /api/v1/profile/{userId}`) ou déprovisionné via SCIM (`DELETE /scim/v2/Users/{id}`, une suppression logique qui désactive l'utilisateur), Authagonal appelle `DELETE {CallbackUrl}/users/{userId}` sur chaque application dans laquelle l'utilisateur avait été provisionné, avec un délai d'expiration de 10 secondes, et supprime l'enregistrement de provisionnement. Cette opération est exécutée au mieux : les échecs sont journalisés mais ne bloquent pas la suppression. Une application qui n'est plus configurée est ignorée avec un avertissement.

`ReprovisionAsync` sur `IProvisioningOrchestrator` relance Try et Confirm pour chaque application, même lorsque l'utilisateur y est déjà provisionné. La bibliothèque l'utilise lorsqu'un compte sans mot de passe est réclamé (voir [Promouvoir un utilisateur](user-upgrade)) ; une simple reconnexion ne le fait jamais.

## Implémenter les points de terminaison en amont {#implementing-the-upstream-endpoints}

### Exemple minimal (Node.js/Express) {#minimal-example-nodejsexpress}

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
