---
layout: default
title: Scopes OAuth
locale: fr
---

# Scopes OAuth

Authagonal prend en charge à la fois des scopes OAuth/OIDC **intégrés** et des scopes **personnalisés** gérés à l'exécution. Les scopes personnalisés sont persistés, annoncés via le document de découverte et présentés sur l'écran de consentement aux côtés des scopes intégrés.

## Scopes intégrés {#built-in-scopes}

Ces scopes sont toujours disponibles et n'ont pas besoin d'être enregistrés :

| Scope | Rôle |
|---|---|
| `openid` | Nécessaire pour lancer un flux OIDC. Émet un jeton d'identité. |
| `profile` | Revendications de profil standard (name, family_name, given_name, etc.) |
| `email` | Revendications de l'adresse e-mail et `email_verified` |
| `phone` | Revendications `phone_number` et `phone_number_verified` (OIDC Core 5.4) |
| `roles` | La revendication `roles`. Ce n'est pas un scope standard OIDC : l'appartenance à des rôles est une revendication dont l'utilisateur final consent à la divulgation |
| `groups` | La revendication `groups` (appartenance aux groupes SCIM). Ce n'est pas un scope standard OIDC ; il est soumis aux mêmes conditions que `roles` |
| `offline_access` | Émet un jeton d'actualisation en plus du jeton d'accès |

Un client ne peut demander que les scopes figurant dans ses propres `AllowedScopes`. `/connect/authorize` rejette un scope absent de cette liste avec `invalid_scope` au lieu de le filtrer ; ajouter `roles` à la requête d'une application sans l'ajouter au client fait donc échouer toutes les connexions.

## Scopes personnalisés {#custom-scopes}

Les scopes personnalisés se gèrent via l'API d'administration, sur `/api/v1/scopes`. Ils exigent un jeton d'accès JWT portant le scope `authagonal-admin` (configurable via `AdminApi:Scope`).

### Modèle de scope {#scope-model}

```csharp
public sealed class Scope
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }
    public string? Group { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> AllowedRoles { get; set; } = [];
    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

| Champ | Description |
|---|---|
| `Name` | L'identifiant du scope envoyé dans les requêtes de jeton (par exemple `billing.read`) |
| `DisplayName` | Nom lisible affiché sur l'écran de consentement |
| `Description` | Description plus longue affichée sur l'écran de consentement |
| `Emphasize` | Si `true`, l'écran de consentement met ce scope en évidence comme sensible |
| `Group` | Intitulé de l'écran de consentement sous lequel classer ce scope. Purement visuel : n'a jamais d'incidence sur ce qui est accordé |
| `Required` | Si `true`, l'utilisateur ne peut pas désélectionner ce scope lors du consentement |
| `ShowInDiscoveryDocument` | Si `true`, le scope apparaît dans `/.well-known/openid-configuration` sous `scopes_supported` |
| `AllowedRoles` | Rôles qu'un utilisateur doit détenir pour se voir accorder ce scope. Vide (valeur par défaut), le scope n'est soumis à aucune condition ; voir [Scopes conditionnés par rôle](#role-gated-scopes) |
| `UserClaims` | Liste blanche des noms de revendications d'attributs personnalisés inscrites dans les jetons lorsque ce scope est accordé. Les revendications de protocole réservées (comme `org_id`) ne sont jamais publiées par ce biais, de sorte qu'un attribut stocké ne peut pas les falsifier |

### Scopes conditionnés par rôle {#role-gated-scopes}

Les `AllowedScopes` d'un client répondent à la question *cette application peut-elle demander ce scope ?*, tranchée
avant même que quiconque se soit connecté. `AllowedRoles` répond à l'autre moitié : *cette personne peut-elle
l'obtenir ?* Les deux contrôles s'appliquent, et aucun ne remplace l'autre.

```json
{
  "name": "staff-admin",
  "displayName": "Staff administration",
  "allowedRoles": ["staff", "super-admin"]
}
```

Pour un utilisateur ne détenant aucun des rôles listés, le scope est **retiré de l'octroi**, et non refusé : le
client a demandé son ensemble complet et apprend, via le `scope` renvoyé dans la réponse de jeton (RFC 6749 §3.3),
qu'il a obtenu moins. C'est ce qui permet à une même application de servir à la fois le personnel et tous les
autres : l'espace réservé au personnel n'est qu'un scope parmi d'autres, et seules les personnes qui y ont droit le
reçoivent.

Une requête dont *tous* les scopes demandés sont retirés échoue avec `access_denied`, car il ne reste plus rien pour
lequel émettre un jeton.

Le contrôle s'applique partout où un jeton est émis pour un humain :

| Flux | Où il s'exécute |
|---|---|
| Code d'autorisation | Sur `/connect/authorize`, dès que l'utilisateur est connu et **avant** le consentement, de sorte que l'écran ne propose jamais une permission impossible à accorder |
| Device code | Sur `/api/auth/device/approve`, premier point de ce flux où le sujet est connu |
| Actualisation | À chaque rotation, au regard des rôles fraîchement résolus. C'est là que la révocation d'un rôle prend réellement effet, puisque l'octroi enregistre encore ce qui a été approuvé à la connexion |
| Échange de jetons | Pas de contrôle distinct : un échange ne peut que réduire les scopes au sein de ceux du jeton sujet, il ne peut donc jamais atteindre un scope qui n'a pas été accordé au sujet |

Les octrois client-credentials n'ont pas de sujet et ne sont délibérément pas concernées : l'autorité d'un
client machine, c'est son enregistrement.

Définir un scope depuis la configuration permet d'ajouter ou de modifier `AllowedRoles`, mais pas de le vider (comme
pour `UserClaims`, un champ omis préserve la valeur stockée). Pour supprimer un contrôle, faites un `PUT` du scope
avec un tableau vide explicite.

## Initialisation depuis la configuration {#seeding-from-configuration}

Les scopes peuvent être déclarés dans la section de configuration `Scopes`. Ils sont écrits dans le stockage des scopes au démarrage, en même temps que l'[initialisation des clients](configuration#clients).

```json
{
  "Scopes": [
    {
      "Name": "billing.read",
      "DisplayName": "Billing (read-only)",
      "Description": "View invoices and payment history",
      "UserClaims": ["billing_plan"],
      "ShowInDiscoveryDocument": true,
      "Emphasize": false,
      "Group": "Billing",
      "Required": false,
      "AllowedRoles": ["finance"]
    }
  ]
}
```

| Champ | Description |
|---|---|
| `Name` | Obligatoire. Une entrée sans nom est ignorée avec un avertissement |
| `DisplayName`, `Description`, `UserClaims`, `ShowInDiscoveryDocument`, `Emphasize`, `Group`, `Required`, `AllowedRoles` | Comme dans le [modèle de scope](#scope-model) |

L'initialisation est une insertion ou mise à jour (upsert) par `Name`. Un champ que vous définissez l'emporte sur la valeur stockée à chaque démarrage : une modification faite via l'API d'administration sur un champ également initialisé est donc écrasée au démarrage suivant. Un champ que vous omettez conserve la valeur stockée (ou la valeur par défaut du modèle pour un nouveau scope). Comme l'omission signifie « conserver », la configuration peut ajouter ou modifier `UserClaims` et `AllowedRoles` mais ne peut pas les vider : faites-le avec `PUT /api/v1/scopes/{name}` et un tableau vide explicite.

## Points de terminaison d'administration {#admin-endpoints}

### Lister les scopes {#list-scopes}

```
GET /api/v1/scopes
```

Renvoie `{ "scopes": [ ... ] }`.

### Obtenir un scope {#get-scope}

```
GET /api/v1/scopes/{name}
```

Renvoie le scope, ou `404` s'il est introuvable.

### Créer un scope {#create-scope}

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing (read-only)",
  "description": "View invoices and payment history",
  "emphasize": false,
  "required": false,
  "showInDiscoveryDocument": true,
  "userClaims": ["billing_plan"]
}
```

Renvoie `201 Created` avec le scope. Renvoie `400` (`invalid_request`) si `name` est absent ou contient des espaces, et `409` (`scope_exists`) si un scope du même nom existe déjà.

### Mettre à jour un scope {#update-scope}

```
PUT /api/v1/scopes/{name}
Content-Type: application/json

{
  "displayName": "Billing (read)",
  "description": "View invoices",
  "emphasize": true
}
```

Seuls les champs fournis sont mis à jour ; les champs omis conservent leur valeur actuelle.

### Supprimer un scope {#delete-scope}

```
DELETE /api/v1/scopes/{name}
```

Renvoie `204 No Content` (`404` si le scope n'existe pas). Les jetons déjà émis qui incluent ce scope restent valides jusqu'à leur expiration ; révoquez-les explicitement via `/connect/revocation` si nécessaire.

## Document de découverte {#discovery-document}

Les scopes pour lesquels `ShowInDiscoveryDocument = true` apparaissent sous `scopes_supported` dans `/.well-known/openid-configuration`. Les sept scopes intégrés sont toujours annoncés.

```json
{
  "scopes_supported": ["openid", "profile", "email", "phone", "roles", "groups", "offline_access", "billing.read"]
}
```

## Écran de consentement {#consent-screen}

Lorsqu'un client demande un scope qui ne figure pas dans sa liste d'exemption de consentement, la page de consentement liste chaque scope demandé par son `DisplayName` (ou, à défaut, son `Name`), avec la `Description` en dessous. Les scopes pour lesquels `Emphasize = true` bénéficient d'un traitement visuel distinct. Les scopes `Required` ne peuvent pas être désélectionnés.

Voir [Écran de consentement OAuth](index#key-features) pour le parcours côté utilisateur.

## Enregistrement dynamique de clients {#dynamic-client-registration}

Les clients enregistrés via l'[enregistrement dynamique de clients](client-registration) ne peuvent déclarer que les scopes intégrés d'OIDC (`openid`, `profile`, `email`, `phone`, `offline_access`), ainsi que tout scope nommé dans `Auth:DynamicClientRegistrationScopes`. La simple existence d'un scope dans le stockage n'autorise pas un client auto-enregistré à le déclarer, et les scopes conditionnés par rôle (ceux qui ont des `AllowedRoles`) ne sont jamais enregistrables. Tout le reste est rejeté avec `invalid_scope`.
