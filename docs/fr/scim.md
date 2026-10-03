---
layout: default
title: Provisionnement SCIM 2.0
locale: fr
nav_order: 13
---

# Provisionnement SCIM 2.0

Authagonal prend en charge SCIM 2.0 (System for Cross-domain Identity Management) pour le provisionnement automatisé des utilisateurs depuis des fournisseurs d'identité d'entreprise tels que Microsoft Entra ID, Okta et OneLogin.

## Vue d'ensemble {#overview}

SCIM est un protocole de provisionnement entrant : votre fournisseur d'identité envoie les modifications d'utilisateurs et de groupes à Authagonal. Il complète le provisionnement sortant TCC (Try-Confirm-Cancel) existant, qui envoie les utilisateurs vers les applications en aval.

**Opérations prises en charge :**
- CRUD des utilisateurs (création, lecture, modification, suppression par désactivation logicielle)
- CRUD des groupes avec gestion des membres
- Filtrage (opérateurs `eq` et `co` sur `userName`, `externalId`, `displayName`)
- Pagination : par curseur (`cursor`/`nextCursor`) sur les utilisateurs comme sur les groupes ; `startIndex` reste accepté sur les groupes pour les clients existants, mais n'est pas annoncé
- PATCH pour les modifications partielles (y compris la désactivation par `active=false`)
- Correspondance groupe-rôle résolue à l'émission des jetons

**Non pris en charge :** opérations en masse, tri, ETags, gestion des mots de passe via SCIM.

Toutes les ressources sont rattachées au client SCIM qui les a provisionnées : un utilisateur ou un groupe créé par le client d'un jeton SCIM est invisible (404) pour tout autre client SCIM.

## Générer un jeton SCIM {#generating-a-scim-token}

Les endpoints SCIM sont authentifiés par des Bearer tokens statiques. Générez les jetons via l'API d'administration :

```http
POST /api/v1/scim/tokens
Authorization: Bearer {admin-token}
Content-Type: application/json

{
  "clientId": "your-client-id",
  "description": "Entra ID SCIM token",
  "expiresInDays": 365,
  "organizationId": "org_acme",
  "allowedEmailDomains": ["acme.example", "acme-eu.example"]
}
```

La réponse contient le jeton brut **une seule fois**. Il est stocké sous forme de hachage SHA-256 et ne peut pas être récupéré par la suite : conservez-le donc en lieu sûr.

```json
{
  "tokenId": "abc123",
  "clientId": "your-client-id",
  "token": "base64-encoded-token",
  "description": "Entra ID SCIM token",
  "createdAt": "2024-01-01T00:00:00Z",
  "expiresAt": "2025-01-01T00:00:00Z",
  "organizationId": "org_acme",
  "allowedEmailDomains": ["acme.example", "acme-eu.example"]
}
```

Omettez `expiresInDays` (ou passez `0`) pour un jeton sans expiration.

### Associer une organisation aux utilisateurs d'un connecteur {#tagging-a-connectors-users-with-an-organization}

`organizationId` est optionnel. Lorsqu'il est défini, chaque utilisateur provisionné via ce jeton est enregistré avec cet
`OrganizationId`, qui est émis sous forme de revendication `org_id` dans ses jetons. SCIM ne permet pas à un connecteur
d'indiquer lequel de vos clients finaux il synchronise : le cœur de SCIM ne définit aucun attribut d'organisation, et
l'extension entreprise n'est pas implémentée (voir *Prise en charge des schémas* ci-dessous). Lier cette valeur à l'identifiant
répond à la question sans avoir besoin d'un client OAuth pour chacun de vos clients finaux.

Si vous l'omettez, les utilisateurs ne sont associés à aucune organisation, ce qui était le comportement de tous les jetons avant l'apparition de ce champ. Rien n'est jamais
déduit de l'identifiant du client.

Deux règles :

- **À la création seulement.** Une synchronisation ultérieure via un jeton associé à une autre organisation ne réassocie pas un compte existant.
- **Elle précède le provisionnement.** Une réponse TCC `/try` ne renseigne qu'une organisation encore vide
  (voir [Provisionnement](provisioning.md)) : une liaison explicite à l'identifiant l'emporte donc, et le contenu de `/try`
  transporte la valeur liée afin qu'une application en aval puisse voir de quel client final provient la synchronisation.

Lorsque `organizationId` désigne une [organisation](organizations) existante, la création écrit aussi une appartenance `active` à celle-ci (sans rôle) et journalise `scim.organization_member_added` dans l'audit : l'utilisateur ne se voit donc pas ensuite refuser un jeton par le contrôle d'appartenance de l'organisation. Un identifiant qui ne désigne aucune organisation reste une simple étiquette `org_id`, ce que produisent les jetons émis avant l'existence des organisations. Comme l'étiquette, l'appartenance n'est écrite qu'à la création.

> **Étiqueter n'est pas isoler.** La propriété est appliquée par **client**, et non par jeton. Deux jetons émis
> pour le même client constituent une seule identité dotée de deux secrets, et chacun peut lire, renommer, désactiver et
> supprimer ce que l'autre a créé. Cela ne pose pas de problème lorsqu'une seule partie les détient tous. Si des
> connecteurs qui ne se font pas mutuellement confiance détiennent chacun le leur, attribuez un client à chacun.

### Limiter les identités qu'un connecteur peut créer {#bounding-which-identities-a-connector-may-create}

`allowedEmailDomains` est le seul moyen de contrôler **quels** utilisateurs un identifiant SCIM peut provisionner. Définissez-le.

L'omettre produit un jeton sans restriction, et « sans restriction » va plus loin qu'il n'y paraît. Un utilisateur créé par SCIM est
enregistré avec `EmailConfirmed = true` (l'adresse est considérée comme prouvée dès cet instant) : un connecteur
sans restriction peut donc créer `ceo@some-other-company.example` sous forme de compte pré-vérifié. Lorsque le véritable
propriétaire se connecte plus tard par fédération, un enregistrement sans connexion externe existante est adopté au lieu
d'être refusé, si bien que sa connexion se lie à ce compte ; et comme `ScimProvisionedByClientId` désigne toujours le
connecteur qui l'a créé, ce connecteur conserve l'entière propriété de l'objet : il peut lire le profil, renommer
le `userName`, le désactiver (ce qui révoque tous les octrois), ou le supprimer, ce qui purge les passkeys et les
appartenances aux groupes de l'utilisateur et marque la ligne comme supprimée (tombstone), de sorte que le connecteur légitime pour ce domaine reçoit un 404 sur chaque
opération.

Un jeton qui omet ce champ journalise un avertissement à sa création, en indiquant l'identifiant du jeton.

Fournissez des domaines nus (`acme.example`, et non `@acme.example`, ni une adresse). Une valeur qui ne pourrait jamais correspondre est
refusée plutôt qu'enregistrée, car une limite qui n'autorise rien est indiscernable d'un connecteur mal configuré.

Les opérateurs peuvent aussi définir une limite dans la configuration :

```json
{
  "Scim": {
    "Clients": {
      "your-client-id": { "AllowedEmailDomains": ["acme.example"] }
    }
  }
}
```

Les deux sont **intersectées**, et une liste vide, quelle qu'en soit la source, signifie « aucune limite de cette source ». Ainsi, deux
listes vides donnent un jeton sans restriction ; l'une ou l'autre seule s'applique telle quelle ; et lorsque les deux sont définies, seuls les domaines présents dans les deux sont
autorisés : la création d'un jeton peut restreindre la limite configurée par un opérateur, mais jamais l'élargir.

La limite est appliquée à la création comme lors d'un `PUT` ou d'un `PATCH`, si bien qu'un renommage ne peut pas faire passer un compte dans un domaine que l'identifiant
n'est pas autorisé à provisionner.

### Lister les jetons {#listing-tokens}

```http
GET /api/v1/scim/tokens?clientId=your-client-id
Authorization: Bearer {admin-token}
```

### Révoquer un jeton {#revoking-a-token}

```http
DELETE /api/v1/scim/tokens/{tokenId}?clientId=your-client-id
Authorization: Bearer {admin-token}
```

## Configurer votre fournisseur d'identité {#configuring-your-identity-provider}

### URL du locataire {#tenant-url}

```
https://your-authagonal-instance/scim/v2
```

### Authentification {#authentication}

Utilisez **OAuth Bearer Token** avec le jeton généré ci-dessus.

### Microsoft Entra ID {#microsoft-entra-id}

1. Dans le portail Azure, allez dans **Enterprise Applications** > votre application > **Provisioning**
2. Réglez Provisioning Mode sur **Automatic**
3. Saisissez le Tenant URL : `https://your-instance/scim/v2`
4. Saisissez le Secret Token : le jeton brut obtenu à l'étape de génération
5. Cliquez sur **Test Connection** pour vérifier
6. Configurez les correspondances d'attributs (voir ci-dessous)

### Okta {#okta}

1. Dans la console d'administration Okta, allez dans **Applications** > votre application > **Provisioning**
2. Activez **SCIM connector**
3. Définissez la Base URL : `https://your-instance/scim/v2`
4. Réglez Authentication Mode sur **HTTP Header**
5. Saisissez le Bearer token

### OneLogin {#onelogin}

1. Dans l'administration OneLogin, allez dans **Applications** > votre application > **Provisioning**
2. Activez le provisionnement
3. Définissez la SCIM Base URL : `https://your-instance/scim/v2`
4. Définissez le SCIM Bearer Token

## Endpoints SCIM {#scim-endpoints}

| Méthode | Chemin | Description |
|--------|------|-------------|
| GET | `/scim/v2/Users` | Lister / filtrer les utilisateurs |
| GET | `/scim/v2/Users/{id}` | Obtenir un utilisateur |
| POST | `/scim/v2/Users` | Créer un utilisateur |
| PUT | `/scim/v2/Users/{id}` | Remplacer un utilisateur |
| PATCH | `/scim/v2/Users/{id}` | Modification partielle |
| DELETE | `/scim/v2/Users/{id}` | Marquer comme supprimé (désactive ; un GET ultérieur renvoie 404) |
| GET | `/scim/v2/Groups` | Lister / filtrer les groupes |
| GET | `/scim/v2/Groups/{id}` | Obtenir un groupe |
| POST | `/scim/v2/Groups` | Créer un groupe |
| PUT | `/scim/v2/Groups/{id}` | Remplacer un groupe |
| PATCH | `/scim/v2/Groups/{id}` | Ajouter / retirer des membres |
| DELETE | `/scim/v2/Groups/{id}` | Supprimer un groupe |
| GET | `/scim/v2/ServiceProviderConfig` | Capacités |
| GET | `/scim/v2/Schemas` | Définitions des schémas |
| GET | `/scim/v2/ResourceTypes` | Types de ressources |

Chaque endpoint est aussi exposé sans le segment `/v2` (par exemple `/scim/Users`) pour les fournisseurs d'identité qui ajoutent leur propre chemin. Les endpoints de découverte (`ServiceProviderConfig`, `Schemas`, `ResourceTypes`, ainsi que les URL de base nues `/scim/` et `/scim/v2/`, qui renvoient le ServiceProviderConfig) sont anonymes ; tout le reste exige un Bearer token SCIM.

Les endpoints d'utilisateurs et de groupes sont limités à 200 requêtes par minute et par client SCIM ; les requêtes excédentaires reçoivent une erreur SCIM de statut `429`.

## Correspondance des attributs {#attribute-mapping}

### Attributs d'utilisateur {#user-attributes}

| Attribut SCIM | Champ Authagonal |
|---------------|------------------|
| `userName` | `Email` |
| `name.givenName` | `FirstName` |
| `name.familyName` | `LastName` |
| `displayName` | `FirstName LastName` |
| `emails[type eq "work"].value` | `Email` |
| `active` | `IsActive` |
| `externalId` | `ExternalId` |
| `preferredLanguage` (à défaut, `locale`) | `Locale` |

### Attributs de groupe {#group-attributes}

| Attribut SCIM | Champ Authagonal |
|---------------|------------------|
| `displayName` | `DisplayName` |
| `externalId` | `ExternalId` |
| `members` | `MemberUserIds` |

### Prise en charge des schémas {#schema-support}

Uniquement les ressources `User` et `Group` du cœur de SCIM 2.0 (RFC 7643). Les tableaux ci-dessus constituent l'ensemble complet pris en charge.

L'**extension entreprise pour les utilisateurs n'est pas implémentée** : `employeeNumber`, `costCenter`, `organization`,
`division`, `department` et `manager` sont donc acceptés puis ignorés plutôt qu'enregistrés, à la création, au remplacement comme au
PATCH. Entra et Okta en mappent plusieurs dans leurs correspondances d'attributs par défaut : un connecteur standard
n'a donc pas besoin qu'on les retire. (Avant la version 0.27.0, un PATCH qui en contenait un était rejeté EN BLOC avec
`400 invalidPath`, ce qui faisait échouer chaque synchronisation incrémentale alors que les créations réussissaient, et pouvait bloquer un
déprovisionnement `active: false` à cause d'un attribut sans rapport.)

Cet assouplissement est étroit : un chemin du CŒUR mal orthographié, comme `name.givenNam`, répond toujours `400`, et
les attributs en lecture seule (`id`, `meta`, `groups`) conservent leur refus au titre de `mutability`.

Notez que l'attribut entreprise `organization` ne devient **pas** l'`org_id` de l'utilisateur. Cette valeur est
affirmée par le propre fournisseur d'identité du client, alors que la liaison à l'identifiant décrite ci-dessus est définie par
l'opérateur ; utilisez plutôt `organizationId` sur le jeton.

## Détails du comportement {#behavior-details}

### Création d'utilisateur {#user-creation}
- Les utilisateurs provisionnés par SCIM sont créés avec `EmailConfirmed = true` (SSO uniquement, sans mot de passe).
- Le champ `ScimProvisionedByClientId` enregistre quel client SCIM a créé l'utilisateur.
- Si le client a des `ProvisioningApps` configurées, le provisionnement TCC est déclenché automatiquement. Si le provisionnement rejette l'utilisateur, la création SCIM est annulée et la réponse est un `400` SCIM avec `scimType: invalidValue` et un message fixe (le texte propre à l'application en aval n'est délibérément pas renvoyé au client SCIM).
- Créer un utilisateur dont le `userName` ou l'`externalId` existe déjà renvoie un conflit SCIM `409`. Les changements d'adresse e-mail via PUT ou PATCH font l'objet du même contrôle de conflit.

### Désactivation d'utilisateur {#user-deactivation}
- `DELETE /scim/v2/Users/{id}` **marque la ressource comme supprimée** (tombstone) : il désactive l'utilisateur, conserve l'enregistrement local et renseigne `ScimDeletedAt`. Un `GET /scim/v2/Users/{id}` ultérieur renvoie **404**, comme l'exige le §3.6 de la RFC 7644 (« the service provider MUST return a 404 for all operations associated with the previously deleted resource »). Ne confirmez pas un déprovisionnement en relisant la ressource et en vous attendant à `active: false`. La lecture renvoie un 404, et c'est le signe d'un succès.
- L'enregistrement est conservé plutôt qu'effacé afin qu'une personne réembauchée puisse être recréée : le marqueur de suppression libère le `userName`/`externalId` dont une nouvelle ressource a besoin, tandis que le compte local, son historique d'audit et ses appartenances aux groupes subsistent.
- Un `PATCH` avec `active = false` désactive aussi l'utilisateur.
- Les utilisateurs désactivés ne peuvent pas se connecter par mot de passe, SAML ou OIDC.
- Tous les octrois (jetons de rafraîchissement, sessions) sont révoqués lors de la désactivation.
- Le déprovisionnement des applications en aval n'est déclenché que par `DELETE` ; une désactivation par `PATCH` révoque les octrois mais laisse les applications en aval intactes.

### Filtrage {#filtering}
La grammaire complète des filtres du §3.4.2.2 de la RFC 7644 est prise en charge.

**Opérateurs :** `eq`, `ne`, `co`, `sw`, `ew`, `gt`, `ge`, `lt`, `le` et `pr` (présence).
**Opérateurs logiques :** `and`, `or`, `not (...)`, avec regroupement par parenthèses. `and` est prioritaire sur `or`.
**Chemins :** sous-attributs (`name.givenName`), attributs multivalués (`emails.value`), chemins de valeur (`emails[type eq "work"].value`) et noms préfixés par une URN (`urn:ietf:params:scim:schemas:core:2.0:User:userName`).

```
userName eq "user@example.com"
userName sw "sales-" and active eq true
emails[type eq "work"].value co "@acme.com"
not (title pr)
meta.lastModified gt "2026-01-01T00:00:00Z"
```

La sémantique suit la RFC : la comparaison de chaînes ne tient pas compte de la casse, un attribut multivalué correspond lorsque l'un quelconque de ses éléments correspond, et un attribut absent rend toute comparaison fausse, sauf `ne`. Une entrée qui n'est pas un filtre SCIM valide est rejetée avec `400` et `scimType: invalidFilter`, avec un message qui nomme le problème.

**Performances.** `userName eq` et `externalId eq` (les recherches qu'Entra et Okta effectuent avant chaque création ou modification) sont résolus par des lectures ponctuelles indexées plutôt que par un parcours de liste : ils restent donc rapides quel que soit le nombre d'utilisateurs. Tout autre filtre est évalué au fil de la pagination des utilisateurs du client, de façon bornée : les données personnelles des utilisateurs sont chiffrées au repos et ne sont interrogeables que via des index aveugles, si bien que des prédicats plus riches ne peuvent pas être délégués au stockage. En pagination par curseur, `totalResults` est **omis** tant que `nextCursor` est présent, et donne le total exact une fois `nextCursor` absent. Voir Pagination.

### Pagination {#pagination}
Les listes d'utilisateurs utilisent la **pagination par curseur**. Chaque page de `GET /scim/v2/Users` renvoie une propriété `nextCursor` dans la réponse de liste ; renvoyez-la sous la forme `?cursor=` pour obtenir la page suivante. Lorsque `nextCursor` est absent, la liste est complète. La taille de page est contrôlée par `count` (100 par défaut, 200 au maximum).

Demander un `startIndex` supérieur à 1 sur l'endpoint Users renvoie une erreur `400` qui vous oriente vers la pagination par curseur ; la pagination par décalage au-delà de la première page n'est pas proposée. `totalResults` est **entièrement omis** tant que `nextCursor` est présent, et ne porte le total exact que sur la dernière page. Il n'indique délibérément pas la taille de la page renvoyée : un client de synchronisation qui lisait `totalResults`, constatait qu'il était égal au nombre de ressources qu'il venait de recevoir et en concluait qu'il détenait tout l'annuaire, lisait en réalité le locataire de façon incomplète sans s'en apercevoir. Pilotez la boucle avec `nextCursor`, jamais avec `totalResults`, et considérez un `totalResults` absent comme « pas encore connu », et non comme zéro.

**Les listes de groupes sont aussi paginées par curseur.** `GET /scim/v2/Groups` renvoie un `nextCursor` dans sa forme filtrée
comme non filtrée ; suivez-le de la même façon. `startIndex` reste accepté sur Groups pour les clients qui l'utilisent
déjà, mais il n'est **pas annoncé** dans `ServiceProviderConfig` et il ne faut pas s'y fier : `pagination.index` est une
affirmation sur le fournisseur, et non sur une collection, et `/Users` ne le prend pas en charge ; la seule valeur
vraie partout est donc `false`. Utilisez les curseurs, qui fonctionnent sur les deux.

Une liste de groupes filtrée parcourt le contenu par fenêtres bornées plutôt que de charger tout le locataire : elle peut donc renvoyer
une page vide alors que des correspondances existent plus loin. Dans ce cas, elle renvoie un `nextCursor` et **omet**
`totalResults` : une page vide avec un curseur signifie « continuez », et une page vide sans curseur signifie que l'ensemble
filtré est réellement vide. Ne traitez pas la première page vide comme la fin de la collection.

`count=0` renvoie `totalResults` sans aucune ressource (§3.4.2.4 de la RFC 7644) sur les deux collections, et un `count`
négatif est refusé avec un `400` plutôt que ramené à une valeur admise.

### Appartenance aux groupes via PATCH {#group-membership-via-patch}
`PATCH /scim/v2/Groups/{id}` accepte les formes d'appartenance qu'envoient réellement les principaux fournisseurs d'identité :

- **Ajouter des membres :** `op: "add"` avec `path: "members"` et un tableau de valeurs composé d'objets `{ "value": "user-id" }`. Les doublons sont ignorés.
- **Remplacer les membres :** `op: "replace"` avec `path: "members"` remplace l'ensemble des membres par le tableau fourni.
- **Retirer un membre précis (tableau de valeurs) :** `op: "remove"` avec `path: "members"` et un tableau de valeurs contenant les identifiants des membres à retirer (la forme qu'envoie Entra ID).
- **Retirer un membre précis (filtre de chemin) :** `op: "remove"` avec `path: 'members[value eq "user-id"]'`, l'identifiant étant porté par le filtre de chemin, sans valeur (la forme qu'envoie Okta pour le déprovisionnement).
- **Retirer tous les membres :** `op: "remove"` avec `path: "members"` et sans valeur vide le groupe.

### Correspondance groupe-rôle {#group-to-role-mapping}
L'appartenance à un groupe SCIM peut accorder des rôles applicatifs. Les correspondances sont stockées à raison d'une ligne par paire (groupe, rôle), et un groupe peut accorder plusieurs rôles. Elles sont résolues à l'**émission des jetons** : les rôles effectifs d'un utilisateur sont ses rôles attribués directement plus les rôles de chaque groupe mappé auquel il appartient, si bien qu'ajouter ou retirer un membre d'un groupe prend effet au jeton suivant sans toucher à l'enregistrement de l'utilisateur. Un store de correspondances vide n'a aucun effet.

Les correspondances sont conservées via `IScimGroupRoleMappingStore` (implémenté par les fournisseurs de stockage Azure et AWS ; une implémentation en mémoire est enregistrée par défaut dans les autres cas) et sont gérées par l'interface d'administration de l'application hôte, et non via l'API SCIM elle-même.

En option, un client pour lequel `IncludeGroupsInTokens` est activé reçoit aussi les noms d'affichage des groupes SCIM de l'utilisateur sous forme de revendication `groups` dans les jetons émis.

## Limitations connues {#known-limitations}

- **Pas d'opérations en masse :** les utilisateurs et les groupes doivent être provisionnés individuellement.
- **Pas de tri :** les listes d'utilisateurs suivent l'ordre du stockage en pagination par curseur ; les listes de groupes sont triées par date de création.
- **Pas de gestion des mots de passe :** les utilisateurs provisionnés par SCIM s'authentifient uniquement par SSO.
- **Marquage, pas effacement :** `DELETE` désactive la ressource et la marque comme supprimée (un `GET` ultérieur renvoie 404, conformément au §3.6 de la RFC 7644) au lieu de supprimer définitivement l'enregistrement local de l'utilisateur. Pour un effacement, utilisez l'API d'administration.
