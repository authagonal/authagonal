---
layout: default
title: Pushed Authorization Requests
locale: fr
---

# Pushed Authorization Requests (PAR)

[RFC 9126](https://www.rfc-editor.org/rfc/rfc9126) permet à un client d'envoyer par POST les paramètres de sa requête d'autorisation directement au serveur, avec l'authentification client standard, et de recevoir en retour un `request_uri` opaque de courte durée à transmettre au navigateur. Le navigateur visite alors `/connect/authorize?request_uri=...&client_id=...` au lieu de transporter chaque paramètre dans l'URL.

Pourquoi l'utiliser :

- Les paramètres d'autorisation n'apparaissent jamais dans l'historique du navigateur, les journaux du serveur ni les en-têtes `Referer`.
- Le serveur authentifie le client au moment de l'envoi, de sorte que l'intégrité des paramètres est vérifiée avant toute redirection.
- Les ensembles de paramètres volumineux (requêtes `claims` importantes, flux multi-ressources) ne font pas exploser les limites de longueur d'URL.

## Point de terminaison {#endpoint}

```
POST /connect/par
Content-Type: application/x-www-form-urlencoded
```

L'authentification est la même que pour `/connect/token` : HTTP Basic avec `client_id`/`client_secret`, ou identifiants encodés dans le formulaire. Les clients confidentiels doivent s'authentifier ; les clients publics envoient la requête sans secret. Les échecs d'authentification du client renvoient `401` (conformément à la RFC 9126, contrairement au point de terminaison de jeton, où seul `invalid_client` donne un 401).

Le corps du formulaire transporte les mêmes paramètres que ceux qui figureraient normalement sur `/connect/authorize` (`response_type`, `redirect_uri`, `scope`, `state`, `code_challenge`, `code_challenge_method`, `nonce`, `resource`, etc.). `request_uri` lui-même est refusé : le chaînage de PAR est interdit par le §2.1 de la spécification. Si le corps contient un `client_id`, celui-ci doit correspondre au client authentifié. Comme le point de terminaison de jeton, la route refuse une requête `http` en clair, sauf si `AuthagonalProtocolOptions.AllowInsecureHttp` est défini.

La requête est validée au moment de l'envoi, de la même manière que `/connect/authorize` la validerait (`redirect_uri` enregistré, scopes autorisés, PKCE, valeurs de `prompt`, etc.). Une requête invalide est immédiatement refusée avec `400 invalid_request` et aucun `request_uri` n'est émis : l'erreur remonte ainsi au client plutôt qu'à l'utilisateur final en plein milieu du flux. `authorization_details` est refusé avec `invalid_authorization_details` (les requêtes d'autorisation enrichies relèvent du point de terminaison de jeton, pas de celui-ci).

### Limites {#limits}

- Le corps est plafonné à 32 Ko, avec au plus 64 champs de formulaire, des noms de 256 caractères et 8 Ko par valeur. Tout ce qui dépasse est refusé avec `413 invalid_request`.
- Les requêtes sont limitées à 60 par minute par client et par adresse source, et à 300 par minute par client au total, avec la réponse `429 temporarily_unavailable`.

### Réponse {#response}

```
HTTP/1.1 201 Created
```
```json
{
  "request_uri": "urn:ietf:params:oauth:request_uri:abc123...",
  "expires_in": 90
}
```

Le `request_uri` est à usage unique. Il est retiré du stockage lorsque le code d'autorisation est émis pour lui. S'il n'est jamais utilisé, il expire au bout de 90 secondes.

### Étape d'autorisation {#authorization-step}

```
GET /connect/authorize?client_id=my-rp&request_uri=urn:ietf:params:oauth:request_uri:abc123...
```

Lorsque `request_uri` est présent, tous les autres paramètres sont tirés de la charge utile envoyée, et tout autre élément de l'URL est ignoré (hormis `client_id`, qui doit correspondre au client ayant envoyé la charge utile, et le paramètre `error` qu'ajoute un aller-retour de fédération échoué). Un `request_uri` inconnu, expiré, déjà consommé ou envoyé par un autre client est refusé avec `invalid_request`. Seules les URN opaques émises par le propre point de terminaison PAR de ce serveur sont acceptées : toute autre valeur de `request_uri` est refusée avec `request_uri_not_supported`, et le paramètre `request` de la RFC 9101 avec `request_not_supported`.

Les valeurs `prompt` et `max_age` envoyées sont respectées. Une requête PAR portant `prompt=login` (ou un `max_age` que la session a dépassé) n'est satisfaite que par une session dont l'`auth_time` est égal ou postérieur au moment où la requête a été envoyée : une session préexistante est donc déconnectée puis réauthentifiée une fois, et le retour depuis la connexion émet un code au lieu de boucler.

## Exiger PAR pour un client {#requiring-par-per-client}

Définissez `RequirePushedAuthorizationRequests = true` sur un client pour refuser ses requêtes `/connect/authorize` ordinaires. Toute tentative d'autorisation hors PAR renvoie `invalid_request` avec la description « This client requires requests to be pushed via /connect/par ».

```csharp
new OAuthClient
{
    ClientId = "high-risk-rp",
    RequirePushedAuthorizationRequests = true,
    // ...
}
```

C'est la configuration recommandée pour les clients qui manipulent des scopes sensibles : combinée à PKCE, elle supprime la barre d'adresse en tant que surface d'attaque.

## Durée de vie et stockage {#lifetime-and-storage}

La valeur `expires_in` renvoyée par l'envoi est de 90 secondes, et cette fenêtre couvre le trajet entre l'envoi et la première requête `/connect/authorize`. Dès que l'enregistrement est pris en charge pour la première fois, il est prolongé (une seule fois) jusqu'à une échéance absolue de 15 minutes après l'envoi, afin que l'utilisateur puisse terminer la connexion, la MFA et le consentement. Les valeurs de 90 secondes et de 15 minutes sont des constantes, pas des paramètres de configuration. Les charges utiles envoyées sont stockées via le même `IGrantStore` que les codes d'autorisation et les jetons d'actualisation, et héritent donc automatiquement de la stratégie de persistance et de réplication de l'hôte.

## Découverte {#discovery}

Le point de terminaison PAR s'annonce dans `.well-known/openid-configuration` ainsi :

```json
{
  "pushed_authorization_request_endpoint": "https://auth.example.com/connect/par"
}
```
