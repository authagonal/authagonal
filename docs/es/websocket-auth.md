---
layout: default
title: Autenticación de WebSocket (BFF)
locale: es
---

# Autenticar WebSockets desde el BFF

El [BFF](bff) existe para que una SPA de navegador nunca tenga un token: recibe una cookie de sesión httpOnly
y el BFF inyecta la cabecera `Authorization: Bearer` cuando hace de proxy de las llamadas a la API. Eso funciona
para HTTP, pero un `WebSocket` de navegador no puede establecer una cabecera `Authorization`, y usted no quiere
poner el token de acceso en la URL ni en un almacenamiento legible desde JS.

El flujo **ws-ticket** resuelve esto. La SPA pide al BFF un ticket de vida corta y un solo uso, abre el
socket con el *ticket* (no con el token) y su host de API canjea el ticket por el token de acceso real en
el servidor. El token nunca llega al navegador.

```
Browser ──GET /bff/ws-ticket──► BFF ──(store token under ticket in shared cache)──► returns { ticket }
Browser ──wss://api/…?ticket=── ► API host ──TryRedeemWsTicketAsync──► access token ──► authenticate socket
```

## 1. Activarlo en el BFF {#1-enable-it-on-the-bff}

Los ws-tickets son opcionales, y tanto el BFF como su host de API deben compartir la **misma** caché distribuida (Redis
en producción) para que el host de API pueda leer lo que escribió el BFF:

```csharp
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConnectionString);

builder.Services.AddAuthagonalBff(o =>
{
    // …your usual BFF options (authority, client id/secret, base path)…
    o.WsTicketsEnabled = true;                  // maps GET {BasePath}/ws-ticket
    o.WsTicketLifetime = TimeSpan.FromSeconds(30); // default
});
```

## 2. Emitir y conectar (navegador) {#2-mint--connect-browser}

`GET {BasePath}/ws-ticket` exige la cookie de sesión y la cabecera antifalsificación del BFF (igual que cualquier
llamada al BFF). Devuelve un ticket opaco ligado al token de acceso recién renovado de la sesión. Obténgalo, conéctese
y descártelo. Nunca lo persista:

```javascript
async function openSocket() {
  const res = await fetch('/bff/ws-ticket', {
    headers: { 'X-CSRF': '1' },           // your configured AntiForgeryHeader
  });
  const { ticket } = await res.json();     // { ticket, expiresInSeconds }
  return new WebSocket(`wss://api.example.com/live?ticket=${encodeURIComponent(ticket)}`);
}
```

## 3. Canjearlo (host de API) {#3-redeem-it-api-host}

En la solicitud de upgrade del socket, extraiga el `ticket` y cámbielo por el token de acceso en la caché
compartida. `TryRedeemWsTicketAsync` busca el token y **elimina** la clave, de modo que un ticket funciona exactamente una vez:

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

Si su host de API es un ensamblado independiente y prefiere no referenciar `Authagonal.Bff`, el formato de la clave
de caché es público: el token se guarda en `WsTicketKey(ticket)` = `agbff:wst:{ticket}`. Puede leerlo y eliminarlo
usted mismo, pero es preferible el helper incluido.

## Notas de seguridad {#security-notes}

- **256 bits aleatorios, TTL de 30 s.** El ticket es imposible de adivinar y de vida corta; aunque se filtre desde una URL o un log,
  no sirve de nada una vez pasada la ventana.
- **De un solo uso, con un matiz.** `IDistributedCache` no tiene una operación atómica de obtener y eliminar, así que el helper
  obtiene y luego elimina: dos solicitudes que lean el mismo ticket en el brevísimo intervalo antes de que cualquiera de ellas lo elimine
  podrían tener éxito ambas. Para un uso único estricto, respalde la caché con un almacén que ofrezca una extracción atómica (Redis `GETDEL`).
- **El navegador solo transporta el ticket opaco.** El token de acceso se queda en el servidor, exactamente igual que con
  el proxy HTTP.
