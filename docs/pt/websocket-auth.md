---
layout: default
title: Autenticação WebSocket (BFF)
locale: pt
---

# Autenticar WebSockets a partir do BFF

O [BFF](bff) existe para que uma SPA no browser nunca detenha um token: recebe um cookie de sessão httpOnly
e o BFF injeta o cabeçalho `Authorization: Bearer` quando encaminha as chamadas à API. Isto funciona
para HTTP, mas um `WebSocket` no browser não consegue definir um cabeçalho `Authorization`, e não convém colocar o
token de acesso no URL nem num armazenamento legível por JS.

O fluxo de **ws-ticket** resolve isto. A SPA pede ao BFF um ticket de curta duração e de utilização única, abre o
socket com o *ticket* (não com o token) e o anfitrião da sua API resgata o ticket pelo token de acesso real
do lado do servidor. O token nunca toca no browser.

```
Browser ──GET /bff/ws-ticket──► BFF ──(store token under ticket in shared cache)──► returns { ticket }
Browser ──wss://api/…?ticket=── ► API host ──TryRedeemWsTicketAsync──► access token ──► authenticate socket
```

## 1. Ativar no BFF {#1-enable-it-on-the-bff}

Os ws-tickets são opcionais, e tanto o BFF como o anfitrião da sua API têm de partilhar a **mesma** cache distribuída (Redis
em produção), para que o anfitrião da API consiga ler o que o BFF escreveu:

```csharp
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConnectionString);

builder.Services.AddAuthagonalBff(o =>
{
    // …your usual BFF options (authority, client id/secret, base path)…
    o.WsTicketsEnabled = true;                  // maps GET {BasePath}/ws-ticket
    o.WsTicketLifetime = TimeSpan.FromSeconds(30); // default
});
```

## 2. Emitir e ligar (browser) {#2-mint--connect-browser}

`GET {BasePath}/ws-ticket` exige o cookie de sessão e o cabeçalho anti-falsificação do BFF (como qualquer chamada
ao BFF). Devolve um ticket opaco vinculado ao token de acesso acabado de renovar da sessão. Obtenha-o, estabeleça a ligação
e descarte-o. Nunca o persista:

```javascript
async function openSocket() {
  const res = await fetch('/bff/ws-ticket', {
    headers: { 'X-CSRF': '1' },           // your configured AntiForgeryHeader
  });
  const { ticket } = await res.json();     // { ticket, expiresInSeconds }
  return new WebSocket(`wss://api.example.com/live?ticket=${encodeURIComponent(ticket)}`);
}
```

## 3. Resgatar (anfitrião da API) {#3-redeem-it-api-host}

No pedido de upgrade do socket, extraia o `ticket` e troque-o pelo token de acesso na cache
partilhada. `TryRedeemWsTicketAsync` procura o token e **elimina** a chave, pelo que um ticket funciona exatamente uma vez:

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

Se o anfitrião da sua API for um assembly separado e preferir não referenciar `Authagonal.Bff`, o formato da chave
da cache é público: o token é guardado em `WsTicketKey(ticket)` = `agbff:wst:{ticket}`. Pode lê-lo e eliminá-lo
por si, mas dê preferência ao helper fornecido.

## Notas de segurança {#security-notes}

- **256 bits aleatórios, TTL de 30 s.** O ticket é impossível de adivinhar e de curta duração; mesmo que seja exposto num URL ou num log,
  é inútil depois da janela.
- **Utilização única, com uma ressalva.** `IDistributedCache` não tem uma operação atómica de obter e eliminar, pelo que o helper
  obtém e depois elimina: dois pedidos que leiam o mesmo ticket no breve intervalo antes de qualquer um deles o eliminar podem
  ambos ter êxito. Para uma utilização única estrita, suporte a cache num armazenamento que ofereça uma extração atómica (o `GETDEL` do Redis).
- **O browser só transporta o ticket opaco.** O token de acesso permanece do lado do servidor, exatamente como no
  proxy HTTP.
