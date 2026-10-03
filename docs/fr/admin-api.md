---
layout: default
title: API d'administration
locale: fr
---

# API d'administration

Les endpoints d'administration exigent un jeton d'accès JWT portant le scope `authagonal-admin` (configurable via `AdminApi:Scope`).

Tous les endpoints se trouvent sous `/api/v1/`.

## Obtenir le premier jeton d'administration {#bootstrapping-the-first-admin-token}

Chaque endpoint `/api/v1/*` exige un bearer token portant le scope d'administration, mais l'API d'administration elle-même (ainsi que l'[enregistrement dynamique de clients](client-registration)) **refuse de créer ou de modifier tout client détenant ce scope** (`403 forbidden_scope`) : un client créé à l'exécution ne peut donc jamais s'élever au rang d'administrateur. Le seul moyen d'émettre un jeton d'administration est un **client initialisé par la configuration** : les entrées de la section de configuration `Clients:` sont créées ou mises à jour au démarrage par `ClientSeedService`, et la configuration est de confiance ; la protection contre les scopes interdits ne s'applique qu'aux API d'exécution.

Déclarez dans `appsettings.json` (ou dans les variables d'environnement / le magasin de secrets équivalents) un client `client_credentials` doté du scope d'administration :

```json
{
  "Clients": [
    {
      "Id": "admin-cli",
      "Name": "Admin CLI",
      "ClientSecret": "a-long-random-secret",
      "GrantTypes": ["client_credentials"],
      "Scopes": ["authagonal-admin"]
    }
  ]
}
```

(`ClientSecret` est haché au démarrage ; fournissez plutôt `SecretHashes` si vous préférez ne conserver qu'une valeur déjà hachée dans la configuration. `ClientId`/`ClientName`/`AllowedGrantTypes`/`AllowedScopes` sont acceptés comme alias de `Id`/`Name`/`GrantTypes`/`Scopes`.)

Échangez ensuite les identifiants contre un jeton auprès de l'endpoint de jeton standard :

```bash
curl -X POST https://auth.example.com/connect/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=admin-cli" \
  -d "client_secret=a-long-random-secret" \
  -d "scope=authagonal-admin"
```

```json
{ "access_token": "eyJhbGci...", "token_type": "Bearer", "expires_in": 1800, "scope": "authagonal-admin" }
```

L'octroi `client_credentials` valide le scope demandé par rapport aux `AllowedScopes` du client ; comme le client initialisé détient `authagonal-admin`, le jeton est émis. Utilisez-le sous la forme `Authorization: Bearer {access_token}` dans chaque appel d'administration :

```bash
curl https://auth.example.com/api/v1/clients -H "Authorization: Bearer eyJhbGci..."
```

Conservez le secret du client initialisé dans le magasin de secrets de votre déploiement ; sa rotation consiste en un changement de configuration suivi d'un redémarrage.

## Utilisateurs {#users}

### Obtenir un utilisateur {#get-user}

```
GET /api/v1/profile/{userId}
```

Renvoie le profil ainsi que ce dont une console de support a besoin pour diagnostiquer un problème de connexion :
`emailConfirmed`, `isActive`, `lockoutEnd`, `accessFailedCount`, `roles`, les
`externalLogins` liés et `hasPassword` (présence uniquement, jamais le hachage). Ce dernier fait la différence
entre « la personne a oublié son mot de passe » et « elle n'en a jamais eu, elle se connecte par SSO »,
deux situations qui appellent des conseils opposés.

Renvoie les détails de l'utilisateur, y compris les liens vers ses identités externes.

### Existence d'un utilisateur {#user-exists}

```
GET /api/v1/profile/{userId}/exists
```

Renvoie `204` si l'utilisateur existe, `404` sinon (une vérification d'existence peu coûteuse, sans corps).

### Inscrire un utilisateur {#register-user}

```
POST /api/v1/profile/
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Crée un utilisateur et envoie un e-mail de vérification. Renvoie `409 user_exists` si l'adresse e-mail est déjà utilisée.

Champs optionnels réservés à l'administration : `userId` (identifiant fourni par l'appelant, `409 user_id_in_use` en cas de collision), `emailConfirmed` (crée l'utilisateur déjà vérifié, sans envoyer l'e-mail de vérification), `companyName`, `organizationId`, `phone`, `locale` et `customAttributes` (un dictionnaire de chaînes conservé sur l'utilisateur et transmis aux cibles de provisionnement).

`skipProvisioning: true` crée l'identité sans exécuter le provisionnement. Il est destiné à une application
first-party qui est ELLE-MÊME une cible de provisionnement et qui est déjà en train de configurer cet utilisateur : elle
appelle cet endpoint pour créer l'identité, et non pour être rappelée au sujet d'un utilisateur qu'elle est en train de
créer. Sans ce champ, cette application reçoit son propre Try pour un utilisateur à moitié construit, ne portant que les
attributs qui ont survécu à l'aller-retour, et, si elle s'en remet, finit par provisionner l'utilisateur deux fois.

### Modifier un utilisateur {#update-user}

```
PUT /api/v1/profile/
Content-Type: application/json

{
  "userId": "user-id",
  "firstName": "Jane",
  "lastName": "Smith",
  "organizationId": "new-org-id"
}
```

`userId` est obligatoire ; tous les autres champs sont optionnels, et seuls les champs fournis sont modifiés.

`isActive` désactive ou réactive le compte. `emailConfirmed` (également accepté sous la forme `emailVerified`)
marque l'adresse comme confirmée sans envoyer d'e-mail de vérification, lorsque la possession de l'adresse a été
établie d'une autre manière.

Modifier `organizationId`, ou désactiver le compte, déclenche :
- la rotation du SecurityStamp (invalide toutes les sessions par cookie dans un délai de 30 minutes)
- la révocation de tous les jetons de rafraîchissement

Un blocage qui ne prend effet qu'à la prochaine connexion n'est pas un blocage : c'est pourquoi la désactivation révoque
au lieu d'attendre l'expiration.

### Rechercher des utilisateurs {#search-users}

```
GET /api/v1/profile/search?q=jane&maxResults=20
```

Recherche par préfixe dans les index d'e-mail et de nom. Renvoie `{ "users": [ ... ] }`.

### Obtenir un utilisateur par e-mail {#get-user-by-email}

```
GET /api/v1/profile/by-email?email=jane@example.com
```

Recherche exacte, distincte de la recherche ci-dessus, qui procède par préfixe et peut renvoyer plusieurs personnes. Un appelant
qui fait correspondre « cette adresse » à « ce compte » attend une seule réponse ou aucune. `404` si cet utilisateur n'existe pas.

### Lister les utilisateurs {#list-users}

```
GET /api/v1/profile?organizationId=&count=100&continuationToken=
```

Liste de l'annuaire paginée par curseur ; renvoyez le `continuationToken` reçu pour obtenir la page suivante, et
arrêtez-vous lorsqu'il est nul. Des curseurs plutôt que des décalages, car le store pagine par jeton : un décalage
reparcourrait tout depuis le début à chaque page.

### Utilisateurs existants parmi une liste {#which-users-exist}

```
POST /api/v1/profile/exists
Content-Type: application/json

{ "userIds": [ "a", "b", "c" ] }
```

Renvoie le sous-ensemble des utilisateurs qui existent, plus `truncated: true` lorsque la requête dépassait la limite de 500 identifiants : l'appelant
est ainsi informé que son lot a été tronqué, au lieu de recevoir sans avertissement une réponse portant sur 500 identifiants sur 600. Sert à
rapprocher un ensemble d'identifiants de celui d'un autre système.

### Statut MFA de plusieurs utilisateurs {#mfa-status-for-many-users}

```
POST /api/v1/profile/mfa-status
Content-Type: application/json

{ "userIds": [ "a", "b", "c" ] }
```

Renvoie `{ "statuses": { "a": true, "b": false }, "truncated": false }` : `true` signifie que l'utilisateur possède au moins un identifiant MFA. Limité à 500 identifiants ; `truncated: true` indique que la requête a été tronquée. Sert aux badges « utilise la MFA » d'une vue d'annuaire.

### Définir un mot de passe {#set-a-password}

```
POST /api/v1/profile/{userId}/set-password
Content-Type: application/json

{ "password": "N3w!Password" }
```

La procédure de support pour une personne qui n'a plus accès à son compte parce que l'adresse associée ne lui parvient plus. Soumis
à la politique de mot de passe. Révoque tous les jetons de rafraîchissement et fait tourner le security stamp : un changement de
mot de passe qui laisse les anciennes sessions actives n'a pas changé qui peut agir au nom de cette personne.

### Déverrouiller un utilisateur {#unlock-a-user}

```
POST /api/v1/profile/{userId}/unlock
```

Lève le verrouillage et remet à zéro son compteur de tentatives échouées, pour laisser la personne revenir tout de suite plutôt qu'au moment où
le verrouillage finit par expirer.

### Supprimer un utilisateur {#delete-user}

```
DELETE /api/v1/profile/{userId}
```

Supprime l'utilisateur, révoque tous ses octrois et le déprovisionne de toutes les applications en aval (au mieux).

### Confirmer l'e-mail {#confirm-email}

```
POST /api/v1/profile/confirm-email?token={token}
```

### Envoyer l'e-mail de vérification {#send-verification-email}

```
POST /api/v1/profile/{userId}/send-verification-email
```

### Lier une identité externe {#link-external-identity}

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### Délier une identité externe {#unlink-external-identity}

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

## Gestion de la MFA {#mfa-management}

### Obtenir le statut MFA {#get-mfa-status}

```
GET /api/v1/profile/{userId}/mfa
```

Renvoie le statut MFA et les méthodes enrôlées d'un utilisateur.

### Réinitialiser toute la MFA {#reset-all-mfa}

```
DELETE /api/v1/profile/{userId}/mfa
```

Supprime tous les identifiants MFA et définit `MfaEnabled=false`. L'utilisateur devra procéder à un nouvel enrôlement si la MFA est exigée.

### Supprimer un identifiant MFA précis {#remove-specific-mfa-credential}

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

Supprime un identifiant MFA précis (par exemple, un authentificateur perdu). Si la dernière méthode principale est supprimée, la MFA est désactivée.

## Fournisseurs SSO {#sso-providers}

### Fournisseurs SAML {#saml-providers}

```
POST   /api/v1/saml/connections                    # Create
GET    /api/v1/saml/connections/{connectionId}     # Get one
PUT    /api/v1/saml/connections/{connectionId}     # Update (partial: only supplied fields change)
DELETE /api/v1/saml/connections/{connectionId}     # Delete
```

La création exige `connectionName`, `entityId` et **exactement un** des deux champs `metadataLocation` (une URL de métadonnées) ou `metadataXml` (métadonnées de l'IdP collées telles quelles, pour les IdP sans URL de métadonnées ; elles sont analysées pour validation et condensées à l'enregistrement). Optionnels : `nameIdFormat` (à omettre pour la valeur par défaut emailAddress, `"none"` pour omettre NameIDPolicy, ce qui est recommandé pour ADFS, ou une URN de format NameID), `signAuthnRequests`, `iconUrl`, `allowedDomains`, `disableJitProvisioning`, `organizationId`. Chaque connexion reçoit une paire de clés SP générée par le serveur ; elle n'est jamais renvoyée par l'API. Voir [SAML](saml) pour plus de détails.

`organizationId` limite la connexion à une seule [organisation](organizations) : elle n'est proposée que lorsque cette organisation est sélectionnée, ses `allowedDomains` ne sont comparés qu'au sein de celle-ci (et ne sont *pas* écrits dans l'index des domaines SSO du locataire), et toute personne qui se connecte par son intermédiaire en devient membre. Omis ou `null` = une connexion au niveau du locataire. Une organisation inexistante donne `400 unknown_organization`. À la modification, `null` (champ absent) laisse le périmètre inchangé, `""` ramène la connexion au niveau du locataire, et dans les deux sens l'index des domaines est réécrit en conséquence. Voir [Connexions limitées à une organisation](self-service-sso#organisation-scoped-connections).

### Fournisseurs OIDC {#oidc-providers}

```
POST   /api/v1/oidc/connections                    # Create
GET    /api/v1/oidc/connections/{connectionId}     # Get one
DELETE /api/v1/oidc/connections/{connectionId}     # Delete
```

La création exige `connectionName`, `metadataLocation`, `clientId`, `clientSecret`, `redirectUrl`. Optionnels : `iconUrl`, `allowedDomains`, `passthroughParams`, `organizationId` (même signification que pour une connexion SAML, ci-dessus). Le secret client est protégé au repos et n'est jamais renvoyé. Voir [Fédération OIDC](oidc-federation).

### Domaines SSO {#sso-domains}

```
GET    /api/v1/sso/domains                 # List all
```

## Clients {#clients}

Gérez les clients OAuth à l'exécution. Toutes les routes exigent la politique `IdentityAdmin` (le scope d'administration).

```
GET    /api/v1/clients              # List all clients
GET    /api/v1/clients/{clientId}   # Get one client
POST   /api/v1/clients              # Create a client
PUT    /api/v1/clients/{clientId}   # Update a client
DELETE /api/v1/clients/{clientId}   # Delete a client
```

### Créer / modifier un client {#create--update-client}

```
POST /api/v1/clients
Content-Type: application/json

{
  "clientId": "my-app",
  "clientName": "My Application",
  "allowedGrantTypes": ["authorization_code"],
  "redirectUris": ["https://app.example.com/callback"],
  "allowedScopes": ["openid", "profile", "email"]
}
```

`POST` renvoie `409` si le client existe déjà. `PUT` modifie un client existant (`404` s'il est introuvable) ; à la modification, seuls les scopes nouvellement ajoutés sont contrôlés contre l'élévation de privilèges.

Remarques :

- **Les hachages de secret ne sont jamais renvoyés.** `clientSecretHashes` est retiré de chaque réponse (liste, lecture, création, modification). À la modification, omettre `clientSecretHashes` conserve le secret enregistré ; fournir de nouveaux hachages le remplace.
- **Le scope d'administration ne peut pas être accordé à un client.** Demander `AdminApi:Scope` (par défaut `authagonal-admin`) dans `allowedScopes` renvoie `403 forbidden_scope` : aucun client ne peut détenir le scope d'administration, faute de quoi un client `client_credentials` pourrait émettre des jetons d'administration indéfiniment.
- Ajouter des scopes que l'appelant n'est pas autorisé à accorder renvoie `403`.

## Scopes {#scopes}

Gérez les scopes OAuth personnalisés à l'exécution. Voir [Scopes OAuth](scopes) pour le modèle complet des scopes.

```
GET    /api/v1/scopes           # List all scopes
GET    /api/v1/scopes/{name}    # Get one scope
POST   /api/v1/scopes           # Create a scope
PUT    /api/v1/scopes/{name}    # Update a scope (only supplied fields change)
DELETE /api/v1/scopes/{name}    # Delete a scope
```

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing, read-only",
  "description": "View invoices and payment history",
  "userClaims": ["billing_plan"]
}
```

Renvoie `201` à la création (`409` si le scope existe déjà), le JSON du scope à la lecture et à la modification, et `204` à la suppression.

## Applications de provisionnement {#provisioning-apps}

Gérez à l'exécution les cibles de provisionnement en aval. Toutes les routes exigent la politique `IdentityAdmin`.

```
GET    /api/v1/provisioning/apps               # List apps (also returns the configured limit)
POST   /api/v1/provisioning/apps               # Create an app
PUT    /api/v1/provisioning/apps/{appId}       # Update an app
DELETE /api/v1/provisioning/apps/{appId}       # Delete an app
POST   /api/v1/provisioning/apps/{appId}/test  # Send a test /try call to the app's callback
```

### Créer / modifier une application de provisionnement {#create--update-provisioning-app}

```
POST /api/v1/provisioning/apps
Content-Type: application/json

{
  "name": "Backend",
  "callbackUrl": "https://api.example.com/provisioning",
  "apiKey": "secret-api-key",
  "tryTimeoutSeconds": 30
}
```

- `name` et `callbackUrl` sont obligatoires ; `callbackUrl` doit être une URL `http(s)` absolue.
- `tryTimeoutSeconds` est ramené dans la plage 5 à 300.
- **La clé d'API n'est jamais renvoyée.** Les réponses exposent `hasApiKey` (un booléen) au lieu de la clé elle-même. À la modification, omettre `apiKey` la laisse inchangée, une chaîne vide l'efface, et une valeur la remplace.
- La création est soumise à un quota configurable par déploiement (`IProvisioningAppQuota`) ; le dépasser renvoie `400 provisioning_app_limit`. La réponse de liste inclut la `limit` en vigueur.

### Tester une application de provisionnement {#test-a-provisioning-app}

```
POST /api/v1/provisioning/apps/{appId}/test
```

Envoie un `POST {callbackUrl}/try` synthétique avec un exemple de contenu (et la clé d'API de l'application comme bearer token si elle est définie) et renvoie `{ success, statusCode, body }`, afin que vous puissiez vérifier la connectivité depuis l'interface d'administration.

## Rôles {#roles}

### Lister les rôles {#list-roles}

```
GET /api/v1/roles
```

### Obtenir un rôle {#get-role}

```
GET /api/v1/roles/{roleId}
```

### Créer un rôle {#create-role}

```
POST /api/v1/roles
Content-Type: application/json

{
  "name": "admin",
  "description": "Administrator role"
}
```

### Modifier un rôle {#update-role}

```
PUT /api/v1/roles/{roleId}
Content-Type: application/json

{
  "name": "admin",
  "description": "Updated description"
}
```

### Supprimer un rôle {#delete-role}

```
DELETE /api/v1/roles/{roleId}
```

### Attribuer un rôle à un utilisateur {#assign-role-to-user}

```
POST /api/v1/roles/assign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

L'attribution se fait par **nom de rôle**, et non par identifiant de rôle. Renvoie la liste des rôles mise à jour de l'utilisateur.

### Retirer un rôle à un utilisateur {#unassign-role-from-user}

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

### Obtenir les rôles d'un utilisateur {#get-users-roles}

```
GET /api/v1/roles/user/{userId}
```

### Utilisateurs ayant un rôle {#users-in-a-role}

```
GET /api/v1/roles/{roleName}/users?maxResults=200
```

L'inverse de l'opération précédente (qui détient ce rôle), obtenu à partir d'un index d'appartenance aux rôles plutôt qu'en
lisant chaque utilisateur. Renvoie `{ "roleName": "...", "members": [ { "userId", "email", "firstName",
"lastName", "roles" } ] }` ; chaque membre porte l'ensemble de ses rôles, car une console qui liste un
rôle veut presque toujours montrer ce que ses membres ont d'autre.

`404 role_not_found` pour un rôle qui n'existe pas, plutôt qu'une liste vide : « personne ne le détient »
et « vous avez mal orthographié le rôle » sont deux problèmes différents. `501 not_supported` si le store configuré
n'indexe pas l'appartenance aux rôles, pour la même raison : une liste de membres vide se lirait comme
« personne n'administre ceci ».

Les comptes écrits avant l'existence de l'index y restent invisibles jusqu'à leur réindexation
(`IUserStore.ReindexUserAsync`, qui crée ou met à jour les appartenances d'un utilisateur sans en retirer aucune).

## Jetons SCIM {#scim-tokens}

### Générer un jeton {#generate-token}

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id",
  "description": "Entra provisioning",
  "expiresInDays": 365
}
```

`description` et `expiresInDays` sont optionnels (omettez `expiresInDays` pour un jeton sans expiration). Renvoie le jeton brut une seule fois. Conservez-le en lieu sûr : il ne peut plus être récupéré.

### Lister les jetons {#list-tokens}

```
GET /api/v1/scim/tokens?clientId=client-id
```

Renvoie les métadonnées des jetons (identifiant, date de création) sans la valeur brute du jeton.

### Révoquer un jeton {#revoke-token}

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## Jetons {#tokens}

### Emprunter l'identité d'un utilisateur {#impersonate-user}

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid%20profile
```

Émet des jetons (d'accès, de rafraîchissement et, lorsque `openid` est demandé, un id token) pour le compte d'un utilisateur sans exiger ses identifiants. Utile pour les tests et le support. Les paramètres sont passés dans la chaîne de requête.

| Paramètre de requête | Obligatoire | Description |
|---|---|---|
| `clientId` | Oui | Le client pour lequel les jetons sont émis. Les durées de vie des jetons proviennent de la configuration de ce client. |
| `userId` | Oui | L'utilisateur dont l'identité est empruntée. |
| `scopes` | Non | Liste de scopes **séparés par des espaces** (encodez les espaces dans l'URL). Prend par défaut les `AllowedScopes` du client lorsqu'il est omis. |

Restrictions :

- Les scopes sont limités aux `AllowedScopes` du client : demander un scope que le client ne pourrait pas demander lui-même renvoie `400 invalid_scope`.
- Le scope d'administration (`AdminApi:Scope`, par défaut `authagonal-admin`) **ne peut pas** être émis par cet endpoint ; le demander renvoie `403 forbidden_scope`. Cela empêche un jeton d'administration (éventuellement limité dans le temps) d'émettre un jeton d'accès ou de rafraîchissement d'administration de longue durée.

La réponse est une réponse de jeton standard contenant `access_token`, `refresh_token`, un `id_token` optionnel, `expires_in` et le `scope` accordé (séparé par des espaces).
