---
layout: default
title: Déconnexion front-channel
locale: fr
---

# Déconnexion front-channel

Authagonal implémente **OpenID Connect Front-Channel Logout 1.0**, un mécanisme de déconnexion piloté par le navigateur qui complète la [déconnexion back-channel](index#key-features). Là où la déconnexion back-channel est un POST de serveur à serveur, la déconnexion front-channel affiche l'URL de déconnexion de chaque partie de confiance dans une iframe masquée, afin que la session navigateur de chaque application (cookies, stockage local) soit nettoyée depuis le navigateur même de l'utilisateur.

## Laquelle utiliser, et quand {#when-to-use-which}

| Enjeu | Back-channel | Front-channel |
|---|---|---|
| Sessions côté serveur | ✅ | ❌ |
| Cookies / stockage local du navigateur | ❌ | ✅ |
| Fonctionne lorsque le navigateur de l'utilisateur est hors ligne | ✅ | ❌ |
| Résiste aux erreurs réseau (nouvelle tentative) | ✅ | ❌ (une seule tentative au mieux) |

La plupart des applications ont intérêt à configurer **les deux**. Le back-channel garantit que le serveur est prévenu ; le front-channel nettoie le navigateur.

## Configuration du client {#client-configuration}

Ajoutez une URI de déconnexion front-channel à l'enregistrement `OAuthClient` :

```json
{
  "clientId": "myapp",
  "frontChannelLogoutUri": "https://myapp.example.com/oidc/frontchannel",
  "frontChannelLogoutSessionRequired": true
}
```

| Champ | Description |
|---|---|
| `FrontChannelLogoutUri` | Le point de terminaison de déconnexion du client, accessible depuis le navigateur |
| `FrontChannelLogoutSessionRequired` | Si `true` (valeur par défaut), l'URL est appelée avec les paramètres de requête `iss` et `sid`, afin que le client puisse rattacher la déconnexion à la session concernée |

## Fonctionnement {#how-it-works}

Lorsque le navigateur visite `/connect/endsession` (GET ou POST) :

1. **Confirmation (protection CSRF).** Si le navigateur possède une session connectée et que la requête ne porte pas d'`id_token_hint` dont le `sub` correspond à cette session, le serveur affiche d'abord une page « Se déconnecter ? » avec un bouton de confirmation au lieu de déconnecter l'utilisateur. Le bouton renvoie un POST accompagné d'un jeton de courte durée (15 minutes) lié à cette session. C'est ce qui empêche une page tierce de mettre fin à la session d'un utilisateur simplement en naviguant vers le point de terminaison (le cookie de session est `SameSite=Lax` ; il accompagne donc un GET de premier niveau intersite). Un `id_token_hint` correspondant tient lieu de confirmation.
2. Le serveur recherche tous les clients avec lesquels l'utilisateur détient actuellement des octrois.
3. Pour chaque client dont la `FrontChannelLogoutUri` passe le contrôle des URL sortantes (le loopback est autorisé, car c'est le navigateur de l'utilisateur lui-même qui effectue la requête, mais les adresses de plages privées et link-local ne le sont pas), le serveur construit une URL en y ajoutant `iss=<issuer>` (et `sid=<session_id>`, lorsque la session en possède un) si `FrontChannelLogoutSessionRequired` vaut `true`.
4. Le serveur révoque les octrois émis pour la session, déconnecte l'utilisateur du cookie du serveur d'autorisation, déclenche en arrière-plan les notifications de déconnexion back-channel et, dès qu'au moins une URL front-channel a été construite, renvoie une page HTML contenant une `<iframe>` masquée pour chacune :
   ```html
   <iframe src="https://myapp.example.com/oidc/frontchannel?iss=https%3A%2F%2Fauth.example.com&sid=abc123" style="display:none"></iframe>
   ```
   La page porte une `Content-Security-Policy` dont le `frame-src` est limité aux origines de ces URL, et ne contient aucun script.
5. La destination après déconnexion est déterminée de la même manière, qu'il y ait des iframes ou non. Le `post_logout_redirect_uri` n'est respecté que lorsque la requête identifie le client (par l'audience de l'`id_token_hint` ou par le paramètre `client_id`) et que l'URI figure parmi les `PostLogoutRedirectUris` enregistrées de ce client (un paramètre `state`, s'il est fourni, est ajouté). Avec des iframes, la page attend 2 secondes (un `meta refresh`) puis redirige, ou affiche un message « déconnecté » en l'absence de destination valide. Sans URL front-channel, le serveur redirige immédiatement (`302`), ou répond `200` avec un `message` JSON en l'absence de destination valide.

Un `id_token_hint` n'est accepté que s'il s'agit d'un jeton d'identité signé par ce serveur (ES256, `typ: JWT`) avec une audience unique. Les jetons expirés sont acceptés. Les jetons d'accès, ainsi que les jetons de déconnexion, sont refusés comme indications. Si `client_id` et `id_token_hint` sont tous deux envoyés et désignent des clients différents, la requête échoue avec `400 invalid_request`.

Le point de terminaison JSON `POST /api/auth/logout` (utilisé par le bouton de déconnexion de l'application de connexion) exécute les mêmes étapes de révocation et de notification. Il n'affiche pas d'iframes : il renvoie les URL dans `frontchannel_logout_uris` pour que l'appelant les charge (voir l'[API Auth](auth-api#logout)).

## Gestionnaire de déconnexion côté client {#client-side-logout-handler}

Chaque partie de confiance doit implémenter l'URL référencée par `FrontChannelLogoutUri`. Un gestionnaire minimal :

```http
GET /oidc/frontchannel?iss=https://auth.example.com&sid=abc123
```

1. Vérifiez que `iss` correspond au serveur d'autorisation attendu.
2. Si `sid` est fourni, confirmez qu'il correspond à l'identifiant de session du cookie de session.
3. Effacez la session locale (cookies, session côté serveur, stockage de la SPA).
4. Répondez `200 OK` avec un corps vide (ou une page minuscule) ; la réponse n'est jamais visible par l'utilisateur.

```csharp
app.MapGet("/oidc/frontchannel", (HttpContext ctx) =>
{
    var iss = ctx.Request.Query["iss"].ToString();
    var sid = ctx.Request.Query["sid"].ToString();
    // Validate iss/sid, then clear local session
    ctx.SignOutAsync();
    return Results.Ok();
});
```

## Document de découverte {#discovery-document}

La déconnexion front-channel est annoncée dans `/.well-known/openid-configuration` :

```json
{
  "frontchannel_logout_supported": true,
  "frontchannel_logout_session_supported": true
}
```

## Enregistrement dynamique de clients {#dynamic-client-registration}

Les clients enregistrés via l'[enregistrement dynamique de clients](client-registration) peuvent inclure :

```json
{
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

L'enregistrement refuse une URI de déconnexion qui n'est pas une adresse externe (le loopback, le link-local, les plages privées et les noms en `.localhost`/`.local`/`.internal` sont rejetés avec `invalid_client_metadata`).

## Limites {#limitations}

- **Au mieux** : les iframes sont chargées une seule fois. Si une erreur réseau ou une extension de navigateur les bloque, il n'y a pas de nouvelle tentative. Associez-la à la déconnexion back-channel pour plus de fiabilité.
- **Cookies tiers** : certains navigateurs bloquent par défaut les cookies dans les iframes intersites. Si votre RP s'appuie sur des cookies first-party, vérifiez que le gestionnaire de déconnexion ne dépend pas de l'envoi de cookies.
- **Délai** : la page attend environ 2 secondes avant de rediriger. Les gestionnaires de déconnexion de RP trop lourds risquent de ne pas aboutir à temps.

## Voir aussi {#related}

- [Enregistrement dynamique de clients](client-registration) : les paramètres front-channel dans la requête d'enregistrement
- [Scopes OAuth](scopes) : le consentement tenant compte des scopes complète le flux de déconnexion
