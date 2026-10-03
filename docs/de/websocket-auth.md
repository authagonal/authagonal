---
layout: default
title: WebSocket-Authentifizierung (BFF)
locale: de
---

# WebSockets über das BFF authentifizieren

Das [BFF](bff) gibt es, damit eine Browser-SPA nie ein Token hält: Sie erhält ein httpOnly-Sitzungscookie, und
das BFF setzt den Header `Authorization: Bearer`, wenn es API-Aufrufe weiterleitet. Für HTTP funktioniert das,
aber ein Browser-`WebSocket` kann keinen `Authorization`-Header setzen, und das Access Token wollen Sie weder in
der URL noch in einem für JavaScript lesbaren Speicher ablegen.

Der **ws-ticket**-Ablauf löst dieses Problem. Die SPA fordert beim BFF ein kurzlebiges Einmal-Ticket an, öffnet den
Socket mit dem *Ticket* (nicht mit dem Token), und Ihr API-Host löst das Ticket serverseitig gegen das echte Access
Token ein. Das Token gelangt nie in den Browser.

```
Browser ──GET /bff/ws-ticket──► BFF ──(store token under ticket in shared cache)──► returns { ticket }
Browser ──wss://api/…?ticket=── ► API host ──TryRedeemWsTicketAsync──► access token ──► authenticate socket
```

## 1. Auf dem BFF aktivieren {#1-enable-it-on-the-bff}

ws-Tickets sind optional (Opt-in), und das BFF und Ihr API-Host müssen sich **denselben** verteilten Cache teilen
(in Produktion Redis), damit der API-Host lesen kann, was das BFF geschrieben hat:

```csharp
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConnectionString);

builder.Services.AddAuthagonalBff(o =>
{
    // …your usual BFF options (authority, client id/secret, base path)…
    o.WsTicketsEnabled = true;                  // maps GET {BasePath}/ws-ticket
    o.WsTicketLifetime = TimeSpan.FromSeconds(30); // default
});
```

## 2. Ausstellen und verbinden (Browser) {#2-mint--connect-browser}

`GET {BasePath}/ws-ticket` erfordert das Sitzungscookie und den Anti-Forgery-Header des BFF (wie jeder BFF-Aufruf).
Der Endpunkt gibt ein opakes Ticket zurück, das an das frisch erneuerte Access Token der Sitzung gebunden ist. Holen
Sie es ab, verbinden Sie sich und verwerfen Sie es. Speichern Sie es niemals dauerhaft:

```javascript
async function openSocket() {
  const res = await fetch('/bff/ws-ticket', {
    headers: { 'X-CSRF': '1' },           // your configured AntiForgeryHeader
  });
  const { ticket } = await res.json();     // { ticket, expiresInSeconds }
  return new WebSocket(`wss://api.example.com/live?ticket=${encodeURIComponent(ticket)}`);
}
```

## 3. Einlösen (API-Host) {#3-redeem-it-api-host}

Lesen Sie beim Upgrade-Request des Sockets das `ticket` aus und tauschen Sie es über den gemeinsamen Cache gegen das
Access Token. `TryRedeemWsTicketAsync` schlägt das Token nach und **löscht** den Schlüssel, sodass ein Ticket genau
einmal funktioniert:

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

Wenn Ihr API-Host eine separate Assembly ist und Sie `Authagonal.Bff` lieber nicht referenzieren möchten: Das Format
des Cache-Schlüssels ist öffentlich. Das Token liegt unter `WsTicketKey(ticket)` = `agbff:wst:{ticket}`. Sie können
es selbst lesen und löschen, sollten aber den mitgelieferten Helper bevorzugen.

## Sicherheitshinweise {#security-notes}

- **256 Bit Zufall, 30 s TTL.** Das Ticket ist nicht zu erraten und kurzlebig; selbst wenn es über eine URL oder ein
  Log nach außen gelangt, ist es nach Ablauf des Zeitfensters wertlos.
- **Einmalig verwendbar, mit einer Einschränkung.** `IDistributedCache` bietet kein atomares Lesen-und-Löschen, daher
  liest der Helper zuerst und löscht danach: Zwei Requests, die dasselbe Ticket in der winzigen Lücke lesen, bevor
  einer von beiden löscht, könnten beide erfolgreich sein. Für strikte Einmalverwendung hinterlegen Sie den Cache mit
  einem Speicher, der ein atomares Entnehmen bietet (Redis `GETDEL`).
- **Der Browser trägt immer nur das opake Ticket.** Das Access Token bleibt serverseitig, genau wie beim HTTP-Proxy.
