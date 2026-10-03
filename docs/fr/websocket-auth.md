---
layout: default
title: Authentification WebSocket (BFF)
locale: fr
---

# Authentifier les WebSockets depuis le BFF

Le [BFF](bff) existe pour qu'une SPA dans le navigateur ne détienne jamais de jeton : elle reçoit un cookie de
session httpOnly, et le BFF injecte l'en-tête `Authorization: Bearer` lorsqu'il relaie les appels d'API. Cela
fonctionne pour HTTP, mais un `WebSocket` de navigateur ne peut pas définir d'en-tête `Authorization`, et vous ne
voulez pas placer le jeton d'accès dans l'URL ni dans un stockage lisible par JavaScript.

Le flux **ws-ticket** résout ce problème. La SPA demande au BFF un ticket à usage unique et de courte durée, ouvre
le socket avec le *ticket* (et non le jeton), et votre hôte d'API échange le ticket contre le véritable jeton d'accès
côté serveur. Le jeton ne passe jamais par le navigateur.

```
Browser ──GET /bff/ws-ticket──► BFF ──(store token under ticket in shared cache)──► returns { ticket }
Browser ──wss://api/…?ticket=── ► API host ──TryRedeemWsTicketAsync──► access token ──► authenticate socket
```

## 1. L'activer sur le BFF {#1-enable-it-on-the-bff}

Les ws-tickets sont à activer explicitement, et le BFF comme votre hôte d'API doivent partager le **même** cache
distribué (Redis en production) afin que l'hôte d'API puisse lire ce que le BFF a écrit :

```csharp
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConnectionString);

builder.Services.AddAuthagonalBff(o =>
{
    // …your usual BFF options (authority, client id/secret, base path)…
    o.WsTicketsEnabled = true;                  // maps GET {BasePath}/ws-ticket
    o.WsTicketLifetime = TimeSpan.FromSeconds(30); // default
});
```

## 2. Émettre le ticket et se connecter (navigateur) {#2-mint--connect-browser}

`GET {BasePath}/ws-ticket` exige le cookie de session et l'en-tête anti-falsification du BFF (comme tout appel au
BFF). Il renvoie un ticket opaque lié au jeton d'accès de la session, fraîchement rafraîchi. Récupérez-le,
connectez-vous, puis abandonnez-le. Ne le conservez jamais :

```javascript
async function openSocket() {
  const res = await fetch('/bff/ws-ticket', {
    headers: { 'X-CSRF': '1' },           // your configured AntiForgeryHeader
  });
  const { ticket } = await res.json();     // { ticket, expiresInSeconds }
  return new WebSocket(`wss://api.example.com/live?ticket=${encodeURIComponent(ticket)}`);
}
```

## 3. L'échanger (hôte d'API) {#3-redeem-it-api-host}

Sur la requête de mise à niveau (upgrade) du socket, extrayez le `ticket` et échangez-le contre le jeton d'accès
auprès du cache partagé. `TryRedeemWsTicketAsync` recherche le jeton puis **supprime** la clé, de sorte qu'un ticket
ne fonctionne qu'une seule et unique fois :

```csharp
using Authagonal.Bff; // BffEndpoints.TryRedeemWsTicketAsync / WsTicketKey

app.Map("/live", async (HttpContext ctx, IDistributedCache cache) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
        return Results.BadRequest();

    var ticket = ctx.Request.Query["ticket"].ToString();
    var accessToken = await BffEndpoints.TryRedeemWsTicketAsync(cache, ticket, ctx.RequestAborted);
    if (accessToken is null)
        return Results.StatusCode(StatusCodes.Status401Unauthorized); // unknown / expired / already used

    // Validate the token the same way you validate Bearer tokens on HTTP, then accept the socket.
    if (!TryValidate(accessToken, out var principal))
        return Results.StatusCode(StatusCodes.Status401Unauthorized);

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await ServeAsync(socket, principal, ctx.RequestAborted);
    return Results.Empty;
});
```

Si votre hôte d'API est un assembly distinct et que vous préférez ne pas référencer `Authagonal.Bff`, sachez que le
format de la clé de cache est public : le jeton est stocké sous `WsTicketKey(ticket)` = `agbff:wst:{ticket}`. Vous
pouvez le lire et le supprimer vous-même, mais préférez l'utilitaire fourni.

## Remarques de sécurité {#security-notes}

- **Aléatoire sur 256 bits, TTL de 30 s.** Le ticket est impossible à deviner et de courte durée ; même s'il fuite
  depuis une URL ou un journal, il est inutilisable une fois la fenêtre écoulée.
- **À usage unique, avec une réserve.** `IDistributedCache` n'offre pas d'opération atomique de lecture et
  suppression ; l'utilitaire lit donc puis supprime : deux requêtes lisant le même ticket dans le très bref
  intervalle précédant la suppression par l'une d'elles pourraient toutes deux réussir. Pour un usage unique strict,
  adossez le cache à un stockage offrant une extraction atomique (Redis `GETDEL`).
- **Le navigateur ne transporte jamais que le ticket opaque.** Le jeton d'accès reste côté serveur, exactement comme
  avec le proxy HTTP.
