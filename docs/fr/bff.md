---
layout: default
title: Backend-for-Frontend (BFF)
locale: fr
---

# Backend-for-Frontend (BFF)

Une SPA qui conserve un jeton d'accès ou d'actualisation dans un stockage accessible par JavaScript expose l'un comme l'autre aux attaques XSS. Le BFF est un **client OIDC confidentiel que vous hébergez sur votre propre backend**. Il exécute le flux code d'autorisation + PKCE côté serveur, conserve les jetons dans une session côté serveur et ne donne au navigateur qu'un cookie de session httpOnly. Les appels de la SPA vers vos API passent par le proxy du BFF, qui ajoute au passage le jeton d'accès de la session.

Il est livré en deux versions, qui parlent le même protocole :

| Paquet | Pour | Source |
|---|---|---|
| `Authagonal.Bff` (NuGet) | Hôtes ASP.NET Core | `src/Authagonal.Bff/` |
| `@authagonal/bff` (npm) | Express et Next.js (App Router) | `bff-lib/` |

Le BFF est un client confidentiel ordinaire de l'hôte d'authentification : il utilise la découverte OIDC et les points de terminaison d'autorisation, de jeton, de révocation et de fin de session ; il n'a donc besoin, côté hôte d'authentification, que d'un client enregistré.

## 1. Enregistrer un client BFF {#1-register-a-bff-client}

Le client doit être **confidentiel** (il possède un secret), exiger PKCE et être autorisé à utiliser `offline_access` si vous voulez l'actualisation côté serveur. Enregistrez :

- l'URI de redirection `https://app.example.com/bff/callback`
- l'URI de redirection après déconnexion `https://app.example.com/` (ainsi que `https://app.example.com/bff/logout-callback` si vous utilisez `returnUrl` à la déconnexion ; voir [Déconnexion](#logout))

Pour une « déconnexion partout » à l'échelle du sujet via la [déconnexion back-channel](index#key-features), enregistrez le client avec `BackChannelLogoutSessionRequired = false`. Le BFF accepte les jetons de déconnexion portant `sid` ou seulement `sub`.

## 2. Le brancher (.NET) {#2-wire-it-up-net}

```csharp
builder.Services.AddAuthagonalBff(o =>
{
    o.Authority    = "https://auth.example.com";
    o.ClientId     = builder.Configuration["Bff:ClientId"]!;
    o.ClientSecret = builder.Configuration["Bff:ClientSecret"]!;
    o.Scope        = ["openid", "profile", "email", "offline_access"];
    o.PostLogoutRedirectUri = "https://app.example.com/";
});

var app = builder.Build();
app.UseForwardedHeaders();   // required behind a reverse proxy or ingress
app.MapAuthagonalBff();
app.MapFallbackToFile("index.html");
app.Run();
```

`UseForwardedHeaders` est important : derrière un proxy qui termine TLS, le BFF voit du http simple ; sans cet appel, le cookie de session `__Host-` est écrit sans `Secure` et les navigateurs l'ignorent. Voir [Installation](installation#production-security-checklist) pour déclarer le proxy.

### Node (Express) {#node-express}

```ts
import { authagonalBff } from '@authagonal/bff/express';

app.set('trust proxy', 1);
app.use(authagonalBff({
  authority: 'https://auth.example.com',
  clientId: process.env.BFF_CLIENT_ID!,
  clientSecret: process.env.BFF_CLIENT_SECRET!,
  scope: ['openid', 'profile', 'email', 'offline_access'],
  cookieSecret: process.env.BFF_COOKIE_SECRET!,   // encrypts the session cookie
  postLogoutRedirectUri: 'https://app.example.com/',
}));
```

Pour Next.js, utilisez `createBffRoute` depuis `@authagonal/bff/next` dans `app/bff/[...bff]/route.ts`. Voir `bff-lib/README.md` pour les deux.

## Points de terminaison {#endpoints}

Montés sous `BasePath` (`/bff` par défaut).

| Route | Rôle |
|---|---|
| `GET /bff/login?returnUrl=/` | Lance la connexion : définit un cookie de corrélation propre à cette connexion et redirige vers `/connect/authorize` avec PKCE (`S256`), `state` et `nonce`. |
| `GET /bff/callback` | L'URI de redirection OIDC (`CallbackPath`). Échange le code et crée la session. |
| `GET /bff/user` | `{ isAuthenticated, claims, sessionExpiresAt }`. Exige l'en-tête anti-falsification. Toujours `Cache-Control: no-store`. |
| `GET\|POST /bff/logout` | Met fin à la session localement et auprès de l'hôte d'authentification. Un `POST` exige l'en-tête anti-falsification ; un `GET` est une simple navigation. |
| `GET /bff/logout-callback` | Page d'arrivée de l'aller-retour de fin de session lorsque la déconnexion a reçu un `returnUrl`. |
| `POST /bff/backchannel-logout` | Récepteur de serveur à serveur pour la déconnexion back-channel OIDC. Authentifié par le jeton de déconnexion signé ; il n'exige donc pas d'en-tête CSRF. |
| `GET /bff/ws-ticket` | À activer explicitement (`WsTicketsEnabled`), .NET uniquement. Voir [Authentification WebSocket](websocket-auth). |
| `GET /bff/token?resource=...` | À activer explicitement (`TokenEndpointEnabled`), .NET uniquement. Voir [Jetons échangés pour une autre origine](#exchanged-tokens-for-another-origin). |
| `ANY /bff/api/**` | Le proxy qui injecte le jeton. N'est mappé que lorsque `Upstreams` n'est pas vide. |

`claims` sur `/bff/user` est une table plate de chaînes contenant les revendications de l'id_token, hors mécanique de protocole (`iss`, `aud`, `exp`, `iat`, `nbf`, `nonce`, `at_hash`, `c_hash`, `s_hash`, `azp`, `jti`, `sid`, `auth_time`, `acr`, `amr`, `typ`). Les revendications de type tableau, comme `roles` et `groups`, sont jointes par des espaces. Les revendications sont relues depuis chaque id_token actualisé ; un rôle accordé après la connexion parvient donc à la SPA à l'actualisation suivante, et non à la connexion suivante.

## Depuis le navigateur {#from-the-browser}

Chaque appel autre qu'une navigation porte un en-tête statique, qui protège contre le CSRF en complément de `SameSite=Lax`. Toute valeur est acceptée ; seule sa présence est vérifiée.

```js
const me = await fetch('/bff/user', { headers: { 'X-Authagonal-Bff': '1' } }).then(r => r.json());
if (!me.isAuthenticated) location.href = '/bff/login?returnUrl=' + encodeURIComponent(location.pathname);
```

Connectez et déconnectez l'utilisateur par **navigation** (`location.href = '/bff/login'`), et non avec `fetch`. Le nom de l'en-tête est défini par `AntiForgeryHeader`.

## Le proxy {#the-proxy}

Configurez des services amont, et la SPA appelle `/bff/api/<prefix>/...` :

```csharp
o.Upstreams.Add(new BffUpstream
{
    Prefix = "/orders",
    TargetBaseUrl = "https://api.internal.example.com",
});
```

Le proxy exige l'en-tête anti-falsification et une session active, actualise le jeton d'accès s'il se trouve à moins de `RefreshThresholdSeconds` de son expiration, relaie la requête avec `Authorization: Bearer` et renvoie la réponse en flux continu. Le cookie de session n'est jamais relayé. Les en-têtes entrants `X-Forwarded-*`, `Forwarded` et `X-Real-IP` sont supprimés puis rétablis à partir de l'état propre du BFF, de sorte qu'un script de la SPA ne peut pas se porter garant d'une IP cliente ou d'un schéma. Les redirections de l'amont sont transmises au navigateur au lieu d'être suivies.

Pour chaque amont (`BffUpstream`) :

| Propriété | Signification |
|---|---|
| `Prefix` | Chemin après `/bff/api` qui sélectionne cet amont. |
| `TargetBaseUrl` | Destination vers laquelle les requêtes correspondantes sont relayées. |
| `StripPrefix` | Retire le préfixe correspondant avant de l'ajouter à la cible. Permet à un même BFF de desservir plusieurs backends partageant un espace de chemins. |
| `RequiredAuthority` | Paires `"type:action"`. Le proxy vérifie les `authorization_details` RFC 9396 du jeton sortant et renvoie 403 à moins que chaque paire ne soit autorisée. Voir [Authentification agentique](agentic-auth). |
| `AuthorityLocation` | La racine `locations` sous laquelle cet amont est connu, lorsqu'elle diffère de `TargetBaseUrl`. |
| `StrictAuthority` | Refuse l'appel lorsqu'un octroi porte une contrainte que le proxy ne peut pas évaluer, au lieu de la laisser passer. |

Options associées : `AllowAnonymousProxyRequests` relaie une requête sans session (ou avec une session impossible à actualiser) sans en-tête `Authorization` au lieu de répondre 401, pour les API qui décident par elles-mêmes. Une route soumise à `RequiredAuthority` n'est jamais anonyme. `ExchangeRoutes` associe des routes du proxy à un [échange RFC 8693](agentic-auth), de sorte que l'amont reçoit un jeton aux droits réduits et lié au contexte au lieu du jeton principal de la session : chaque route a un `PathPattern` comportant exactement un espace réservé (la seule contrainte prise en charge est `:guid`), le segment capturé est envoyé comme paramètre de l'échange, et un échange refusé donne un 403. Une contrainte inconnue fait échouer le démarrage plutôt que de relayer silencieusement le jeton aux droits plus larges.

## Déconnexion {#logout}

`/bff/logout` révoque le jeton d'actualisation de la session (au mieux), supprime la session, efface le cookie et redirige vers le point de terminaison de fin de session de l'hôte d'authentification avec l'`id_token_hint` de la session. En l'absence de session, il n'y a rien à terminer auprès de l'hôte d'authentification ; il redirige donc directement vers `PostLogoutRedirectUri`. Avec un `returnUrl`, l'hôte d'authentification redirige vers `/bff/logout-callback`, qui revalide la cible au regard de `ReturnUrlAllowlist` et y redirige. Enregistrez ce rappel comme URI de redirection après déconnexion pour le client.

La déconnexion back-channel supprime les sessions côté serveur : par `sid` lorsque le jeton de déconnexion en porte un, sinon toutes les sessions du `sub`. Les suppressions sont limitées au locataire dont l'émetteur a signé le jeton, car `sub` n'est unique qu'au sein d'un émetteur. Le jeton de déconnexion doit porter `iat` et être récent.

## Référence des options (.NET) {#options-reference-net}

| Option | Valeur par défaut | Remarques |
|---|---|---|
| `Authority`, `ClientId`, `ClientSecret` | obligatoire | Non obligatoires lorsque `TenantQueryParam` est défini. |
| `Scope` | `openid profile offline_access` | `offline_access` active l'actualisation. |
| `BasePath` | `/bff` | |
| `CallbackPath` | `/bff/callback` | Doit être égal à l'URI de redirection enregistrée. |
| `CookieName` | `__Host-agbff` | Le préfixe `__Host-` impose Secure, `Path=/` et l'absence de Domain ; il exige donc https. Redéfinissez-le pour le développement local en http. |
| `SessionLifetime` | 8 heures | Plafond absolu, quelles que soient les actualisations. |
| `PersistentCookie` | `false` | Lorsqu'il vaut true, le cookie reçoit un `Max-Age` borné par `SessionLifetime` et survit au redémarrage du navigateur (« rester connecté »). La déconnexion back-channel met toujours fin à la session. |
| `CorrelationLifetime` | 30 minutes | Durée maximale d'une connexion entre `/bff/login` et le rappel. Couvre l'inscription, l'e-mail de vérification et la connexion. |
| `RefreshThresholdSeconds` | 60 | |
| `ReturnUrlAllowlist` | vide | Origines qu'un `returnUrl` non relatif peut cibler. Les chemins relatifs sont toujours autorisés ; toute autre valeur devient `/`. |
| `LoginPassthroughParams` | vide | Noms de paramètres de requête recopiés de `/bff/login` vers `/connect/authorize` (par exemple `idp_hint`). Les paramètres standard l'emportent toujours. |
| `AntiForgeryHeader` | `X-Authagonal-Bff` | |
| `PostLogoutRedirectUri` | aucune | |
| `WsTicketsEnabled`, `WsTicketLifetime`, `TicketExchangeParams` | désactivé, 30 s, vide | Voir [Authentification WebSocket](websocket-auth). |
| `TokenEndpointEnabled`, `TokenEndpointResources`, `TokenEndpointExchangeParams` | désactivé, vide, vide | L'activer sans ressource fait échouer le démarrage. |
| `Upstreams`, `ExchangeRoutes`, `AllowAnonymousProxyRequests` | vide, vide, `false` | Voir [Le proxy](#the-proxy). |
| `TenantQueryParam` | aucune | Mode multi-locataire, décrit ci-dessous. |

Seul le mode `Store` de `SessionMode` est implémenté ; `Stateless` est réservé et fait échouer le démarrage.

Le paquet Node accepte les équivalents en camelCase de `authority`, `clientId`, `clientSecret`, `scope`, `basePath`, `callbackPath`, `cookieName`, `refreshThresholdSeconds`, `returnUrlAllowlist`, `postLogoutRedirectUri`, `antiForgeryHeader`, `sessionLifetimeSeconds`, `upstreams` et `tenantQueryParam`, ainsi que `cookieSecret`, `sessionStore`, `cookieProtector`, `tenantResolver` et `clientIp`. Il ne dispose ni du point de terminaison ws-ticket ni du point de terminaison de jeton.

## Sessions et exécution de plusieurs instances {#sessions-and-running-more-than-one-instance}

Les sessions sont stockées via `IBffSessionStore`. Par défaut, il s'agit de `IDistributedCache`, en mémoire à moins que vous n'enregistriez un véritable cache (Redis, par exemple) **avant** `AddAuthagonalBff`.

Un cache partagé ne suffit pas à lui seul. L'actualisation à exécution unique (single-flight) est propre à chaque processus, alors que la session et son jeton d'actualisation tournant résident dans le cache partagé. Deux réplicas peuvent lire la même session, constater tous deux qu'elle doit être actualisée et échanger tous deux le même jeton d'actualisation. L'hôte d'authentification interprète le second échange comme le rejeu d'un jeton volé et révoque toute la famille d'octrois, ce qui déconnecte l'utilisateur partout. Fournissez un verrou inter-réplicas de l'une des deux manières suivantes :

- **Enregistrez un `ILeaseProvider`** (quel que soit le backend). Les fournisseurs Azure, AWS et SQL en fournissent un via `AddAuthagonalClustering`. Voir [Mise à l'échelle](scaling).
- **Implémentez `IBffRefreshLockStore` sur votre stockage de sessions** (`TryAcquireRefreshLockAsync(sessionId, ttl)` et `ReleaseRefreshLockAsync`). Il s'agit d'une écriture conditionnelle avec durée de vie, par exemple `SET NX PX` sur Redis. Le stockage par défaut ne peut pas l'offrir, car `IDistributedCache` ne propose pas d'écriture conditionnée à l'absence de la clé. Le stockage de sessions Node comporte l'équivalent, `acquireRefreshLock` / `releaseRefreshLock`.

Sans l'un ni l'autre, le déploiement repose sur `Auth:RefreshTokenReuseGraceSeconds` de l'hôte d'authentification, qui vaut 0 (strict) par défaut dans l'hôte Server. Le BFF journalise un avertissement au démarrage lorsque le stockage de sessions semble partagé et qu'aucun verrou n'est présent.

Un `IBffSessionStore` personnalisé doit respecter l'argument `tenantKey` de `RemoveBySidAsync` et `RemoveBySubjectAsync`. Les autres points d'extension sont `ICookieProtector` (par défaut : ASP.NET Data Protection) et `ITokenClient`.

## Plusieurs locataires depuis un seul BFF {#many-tenants-from-one-bff}

Définissez `TenantQueryParam` (par exemple `"slug"`) et enregistrez un `IBffTenantResolver`. `/bff/login?slug=acme` résout la `BffTenantConfig` du locataire (autorité, identifiant client, secret, scope), la clé est stockée dans la session afin que les requêtes suivantes la résolvent de nouveau, et la déconnexion back-channel résout le locataire à partir de l'`iss` du jeton via `ResolveByIssuerAsync`. Lorsque `TenantQueryParam` n'est pas défini, le BFF est mono-locataire et les options statiques sont utilisées.

## Jetons échangés pour une autre origine {#exchanged-tokens-for-another-origin}

Le modèle à cookie ne permet pas d'atteindre un serveur de ressources situé sur une autre origine (par exemple une application en iframe que la SPA intègre). `TokenEndpointEnabled` ajoute `GET /bff/token?resource=<audience>`, qui renvoie `{ accessToken, expiresInSeconds }` pour un jeton **échangé** selon la RFC 8693 : destiné à une seule ressource de `TokenEndpointResources` (toute autre valeur donne un 400 `resource_not_allowed`), lié aux éventuelles valeurs `TokenEndpointExchangeParams` présentes dans la requête, et de courte durée. Le navigateur ne reçoit jamais le jeton principal de la session et ne doit conserver le jeton échangé qu'en mémoire. Le client du locataire a besoin de l'octroi d'échange de jetons et doit déclarer les ressources comme ses audiences. Un échange refusé donne un 403.
