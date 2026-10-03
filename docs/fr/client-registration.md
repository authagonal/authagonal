---
layout: default
title: Enregistrement dynamique de clients
locale: fr
---

# Enregistrement dynamique de clients

Authagonal implémente l'**enregistrement dynamique de clients OAuth 2.0** ([RFC 7591](https://datatracker.ietf.org/doc/html/rfc7591)), qui permet aux applications clientes de s'enregistrer elles-mêmes à l'exécution, sans intervention d'un administrateur.

## Activer le point de terminaison {#enabling-the-endpoint}

L'enregistrement dynamique est **désactivé par défaut**. Activez-le via la configuration :

```json
{
  "Auth": {
    "DynamicClientRegistrationEnabled": true
  }
}
```

Ou définissez la variable d'environnement `Auth__DynamicClientRegistrationEnabled=true`. Un hôte multi-locataire peut redéfinir ce paramètre pour chaque locataire via `ITenantContext.DynamicClientRegistrationEnabled` : la réponse propre au locataire l'emporte, et `null` renvoie à l'option définie pour l'ensemble de l'hôte.

Lorsqu'il est activé, le document de découverte annonce le point de terminaison :

```
GET /.well-known/openid-configuration
```
```json
{
  "registration_endpoint": "https://auth.example.com/connect/register"
}
```

## Enregistrer un client {#registering-a-client}

```
POST /connect/register
Content-Type: application/json

{
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "token_endpoint_auth_method": "client_secret_basic",
  "scope": "openid profile email offline_access",
  "audiences": ["https://api.myapp.example.com"],
  "allowed_cors_origins": ["https://myapp.example.com"],
  "backchannel_logout_uri": "https://myapp.example.com/oidc/backchannel",
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

### Réponse {#response}

```
HTTP/1.1 201 Created
Content-Type: application/json

{
  "client_id": "a1b2c3d4e5f6...",
  "client_secret": "xkCd2_base64url...",
  "client_id_issued_at": 1745000000,
  "client_secret_expires_at": 0,
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "scope": "openid profile email offline_access",
  "token_endpoint_auth_method": "client_secret_basic"
}
```

Le `client_secret` est renvoyé **une seule fois** et ne peut pas être récupéré ultérieurement. Conservez-le en lieu sûr. La réponse est envoyée avec `Cache-Control: no-store`. `client_id` compte 32 caractères hexadécimaux en minuscules, et `client_secret_expires_at` vaut toujours `0` (les secrets n'expirent pas). Les clients publics (`none`) et les clients `private_key_jwt` ne reçoivent pas de `client_secret` dans la réponse. La réponse ne renvoie que les champs indiqués : `audiences`, `jwks`, `jwks_uri`, `allowed_cors_origins` et les champs de déconnexion sont stockés mais pas renvoyés.

## Paramètres de la requête {#request-parameters}

| Paramètre | Obligatoire | Remarques |
|---|---|---|
| `client_name` | non | Vaut par défaut le `client_id` généré s'il est omis |
| `redirect_uris` | selon le cas | Obligatoire lorsque `grant_types` contient `authorization_code`. Doivent être des URI absolues ; les schémas `javascript:`/`data:`/`vbscript:`/`file:` sont rejetés (les schémas personnalisés natifs pour les liens profonds mobiles sont acceptés). Un fragment est rejeté (RFC 6749 §3.1.2), et le `http` en clair n'est accepté que pour les hôtes loopback (RFC 8252 §7.3). 20 entrées au maximum, de 2048 caractères au plus chacune. |
| `post_logout_redirect_uris` | non | Cibles de redirection valides après la déconnexion. Mêmes limites de 20 entrées / 2048 caractères que `redirect_uris`. |
| `grant_types` | non | Vaut par défaut `["authorization_code"]`. **Seuls `authorization_code` et `refresh_token` peuvent être enregistrés** : `client_credentials`, `implicit`, device et tout autre type d'octroi sont rejetés avec `invalid_client_metadata`, de sorte que l'enregistrement ouvert ne peut jamais créer de client machine à machine. `refresh_token` est ajouté automatiquement si `offline_access` est demandé. |
| `token_endpoint_auth_method` | non | `client_secret_basic` (par défaut), `client_secret_post`, `private_key_jwt`, ou `none` pour les clients publics. Toute autre valeur est refusée avec `invalid_client_metadata`. |
| `jwks` / `jwks_uri` | avec `private_key_jwt` | Les clés publiques du client. L'un des deux est obligatoire pour `private_key_jwt` (sinon `invalid_client_metadata`) ; `jwks_uri` doit passer le contrôle des URL sortantes (une adresse externe). Aucun secret n'est délivré à un client `private_key_jwt`. |
| `scope` | non | Scopes séparés par des espaces. Seuls les cinq scopes intégrés d'OIDC (`openid`, `profile`, `email`, `phone`, `offline_access`), plus ceux que nomme `Auth:DynamicClientRegistrationScopes`, peuvent être enregistrés : l'existence dans le stockage des scopes ne suffit **pas** (voir [Scopes](scopes)). Les scopes conditionnés par rôle et le scope d'administration (`AdminApi:Scope`, par défaut `authagonal-admin`) ne peuvent jamais être enregistrés. |
| `audiences` | non | Valeurs `aud` de JWT ajoutées aux jetons d'accès. 20 entrées au maximum, de 512 caractères au plus, chacune étant une URI absolue sans fragment ; une valeur incorrecte donne `invalid_client_metadata`. |
| `allowed_cors_origins` | non | Chaque entrée doit être une origine valide (sinon `invalid_client_metadata`), mais la valeur n'est **pas stockée telle qu'envoyée** : les origines autorisées du client sont dérivées des origines de ses propres `redirect_uris` en `https`, de sorte qu'un déclarant ne peut atteindre que des origines pour lesquelles il a déjà prouvé une URI de redirection. |
| `backchannel_logout_uri` | non | Active la [déconnexion back-channel](index#key-features) |
| `frontchannel_logout_uri` | non | Active la [déconnexion front-channel](front-channel-logout) |
| `frontchannel_logout_session_required` | non | Vaut `true` par défaut ; lorsque `true`, l'URL de déconnexion porte les paramètres `iss` et `sid` |

## Valeurs par défaut et invariants {#defaults--invariants}

- **PKCE obligatoire** : `RequirePkce` vaut toujours `true` pour les clients enregistrés dynamiquement.
- **Consentement obligatoire** : `RequireConsent` vaut toujours `true` ; un utilisateur voit donc l'écran de consentement pour un client auto-enregistré, même là où un client défini statiquement dans la configuration le sauterait.
- **Clients publics** : `token_endpoint_auth_method: "none"` produit un client sans secret. PKCE reste obligatoire.
- **Accès hors ligne** : demander le scope `offline_access` ajoute implicitement `refresh_token` à `grant_types`.

## Réponses d'erreur {#error-responses}

| HTTP | `error` | Cause |
|---|---|---|
| `400` | `invalid_redirect_uri` | L'une des `redirect_uris` n'est pas une URI absolue valide, utilise un pseudo-schéma script/data/file, porte un fragment, est en `http` en clair vers un hôte autre que loopback, ou (pour l'une ou l'autre liste d'URI) dépasse 2048 caractères |
| `400` | `invalid_client_metadata` | Un type d'octroi non enregistrable a été demandé, `redirect_uris` est absent pour un type d'octroi qui l'exige, `token_endpoint_auth_method` n'est pas pris en charge, `private_key_jwt` n'a ni `jwks` ni `jwks_uri` (ou a un `jwks_uri` dangereux), `audiences` est invalide, une entrée de `allowed_cors_origins` n'est pas une origine, ou une URI de déconnexion n'est pas une adresse externe |
| `400` | `invalid_scope` | Un scope demandé n'est ni intégré ni enregistré |
| `400` | `invalid_client_metadata` | Plus de 20 `redirect_uris` / `post_logout_redirect_uris` |
| `403` | `invalid_scope` | Un scope demandé n'est pas enregistrable : absent de `Auth:DynamicClientRegistrationScopes`, ou conditionné par rôle |
| `403` | `invalid_scope` | Le scope d'administration a été demandé ; il ne peut jamais être accordé par l'enregistrement |
| `403` | `invalid_scope` | Un `IClientScopeGuard` enregistré a refusé un scope demandé (l'appelant anonyme lui est transmis) |
| `403` | `not_supported` | L'enregistrement dynamique de clients n'est pas activé |
| `429` | `rate_limited` | Trop d'enregistrements depuis cette adresse IP (10 par heure) |

## Considérations de sécurité {#security-considerations}

Le point de terminaison d'enregistrement n'est **pas authentifié**, mais il est encadré par conception :

- **Débit limité** : 10 enregistrements par adresse source sur une heure glissante (`429 rate_limited`), afin que le stockage des clients ne puisse pas être inondé. L'adresse prise en compte est celle que l'appelant ne peut pas choisir (la valeur transmise par les en-têtes de transfert n'est pas acceptée aveuglément).
- **Types d'octroi restreints** : uniquement `authorization_code` + `refresh_token` ; un client enregistré exige toujours un flux passant par un utilisateur et ne peut jamais agir comme client machine à machine.
- **Scopes sur liste d'autorisation, non hérités** : un déclarant peut déclarer les cinq scopes intégrés d'OIDC et rien d'autre, sauf si un opérateur liste un scope dans `Auth:DynamicClientRegistrationScopes`. L'existence dans le stockage des scopes ne vaut pas permission : un scope existe parce qu'un client en a besoin, non parce que n'importe quel déclarant anonyme pourrait le revendiquer.
- **Scope d'administration réservé** : le scope `authagonal-admin` (ou la valeur de `AdminApi:Scope`) est refusé, de sorte que l'enregistrement ne peut jamais produire un client qui atteigne l'[API d'administration](admin-api).
- **URI de déconnexion validées** : `backchannel_logout_uri` et `frontchannel_logout_uri` sont appelées par le serveur ; elles doivent donc être des points de terminaison http(s) externes : le loopback, la RFC1918, le link-local (y compris l'adresse de métadonnées du cloud) et les hôtes `.internal`/`.local` sont refusés.
- **Enregistrements bornés** : 20 URI de redirection au maximum, de 2048 caractères au plus chacune, afin qu'un seul enregistrement ne puisse pas servir à gonfler le stockage des clients.
- **Origines CORS dérivées, et non accordées sur parole** : les origines stockées proviennent des propres URI de redirection `https` du client, jamais du corps de la requête.
- **PKCE toujours obligatoire**, et **consentement toujours obligatoire**, pour les clients enregistrés.

Ce qu'il n'encadre **pas**, à moins que le déclarant ne le choisisse, c'est l'audience. La RFC 7591 ne prévoit aucun champ pour elle ; un enregistrement standard omet donc entièrement `audiences` (une extension Authagonal) : la question n'a jamais été posée au client, sa liste est « non définie », et il peut désigner n'importe quelle URI absolue comme `resource` sur le point de terminaison d'autorisation et recevoir un jeton portant cette valeur comme `aud`. C'est délibéré (la spécification d'autorisation MCP impose aux clients de désigner le serveur MCP comme ressource, et un client MCP est un client DCR), et cela rend le serveur de ressources responsable d'autoriser sur la base de `scope` plutôt que sur `iss` + `aud` + `sub`. **Envoyer** `audiences`, même sous forme de liste vide, constitue une réponse et y lie le client : une liste non vide est la liste d'autorisation pour `resource`, et un `[]` explicite signifie que le client ne peut désigner aucune ressource. L'échange de jetons fait exception : là, des `Audiences` non définies entraînent un refus pur et simple, de sorte qu'un client enregistré ne peut diriger un jeton échangé nulle part. Voir [Audiences et indicateurs de ressource](configuration#audiences-and-resource-indicators-rfc-8707).

Pour un contrôle plus strict (jetons d'accès initiaux, mTLS, déclarations logicielles), placez devant le point de terminaison votre propre middleware ou un `IAuthHook`. Envisagez de désactiver complètement l'enregistrement dynamique et de gérer les clients via l'API d'administration dans les environnements où l'enregistrement en libre-service n'est pas une exigence.
