---
layout: default
title: WebSocket 認証 (BFF)
locale: ja
---

# BFF から WebSocket を認証する

[BFF](bff) は、ブラウザの SPA がトークンを一切保持しないようにするために存在します。SPA は httpOnly のセッション Cookie を受け取り、BFF は API 呼び出しをプロキシする際に `Authorization: Bearer` ヘッダーを付加します。これは HTTP では機能しますが、ブラウザの `WebSocket` は `Authorization` ヘッダーを設定できません。また、アクセストークンを URL や JavaScript から読み取れるストレージに置くことも避けたいはずです。

これを解決するのが **ws-ticket** フローです。SPA は BFF に短命で 1 回限りのチケットを要求し、(トークンではなく) *チケット*を使ってソケットを開きます。そして API ホストが、サーバー側でチケットを本物のアクセストークンと引き換えます。トークンがブラウザに触れることはありません。

```
Browser ──GET /bff/ws-ticket──► BFF ──(store token under ticket in shared cache)──► returns { ticket }
Browser ──wss://api/…?ticket=── ► API host ──TryRedeemWsTicketAsync──► access token ──► authenticate socket
```

## 1. BFF で有効にする {#1-enable-it-on-the-bff}

ws-ticket はオプトインです。また、BFF が書き込んだものを API ホストが読み取れるよう、BFF と API ホストの両方が**同じ**分散キャッシュ (本番環境では Redis) を共有する必要があります。

```csharp
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConnectionString);

builder.Services.AddAuthagonalBff(o =>
{
    // …your usual BFF options (authority, client id/secret, base path)…
    o.WsTicketsEnabled = true;                  // maps GET {BasePath}/ws-ticket
    o.WsTicketLifetime = TimeSpan.FromSeconds(30); // default
});
```

## 2. 発行して接続する (ブラウザ) {#2-mint--connect-browser}

`GET {BasePath}/ws-ticket` には、セッション Cookie と BFF の偽造防止ヘッダーが必要です (他の BFF 呼び出しと同じです)。このエンドポイントは、セッションの更新したばかりのアクセストークンに束縛された不透明なチケットを返します。取得して接続したら、チケットは破棄してください。決して保存しないでください。

```javascript
async function openSocket() {
  const res = await fetch('/bff/ws-ticket', {
    headers: { 'X-CSRF': '1' },           // your configured AntiForgeryHeader
  });
  const { ticket } = await res.json();     // { ticket, expiresInSeconds }
  return new WebSocket(`wss://api.example.com/live?ticket=${encodeURIComponent(ticket)}`);
}
```

## 3. 引き換える (API ホスト) {#3-redeem-it-api-host}

ソケットのアップグレードリクエストで `ticket` を取り出し、共有キャッシュを使ってアクセストークンと引き換えます。`TryRedeemWsTicketAsync` はトークンを検索してキーを**削除**するため、チケットはちょうど 1 回だけ機能します。

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

API ホストが別のアセンブリで、`Authagonal.Bff` を参照したくない場合は、キャッシュキーの形式が公開されています。トークンは `WsTicketKey(ticket)` = `agbff:wst:{ticket}` の下に保存されます。自分で読み取って削除することもできますが、同梱のヘルパーを使うことをお勧めします。

## セキュリティに関する注意 {#security-notes}

- **256 ビットの乱数、TTL は 30 秒。** チケットは推測不可能で短命です。URL やログから漏れたとしても、有効期間を過ぎれば役に立ちません。
- **1 回限り、ただし注意点あり。** `IDistributedCache` にはアトミックな取得と削除がないため、ヘルパーは取得してから削除します。どちらかが削除する前のごく短い隙間に同じチケットを読み取った 2 つのリクエストは、両方とも成功する可能性があります。厳密に 1 回限りにするには、アトミックな取り出しを提供するストア (Redis の `GETDEL`) をキャッシュの裏側に使ってください。
- **ブラウザが運ぶのは不透明なチケットだけです。** アクセストークンは、HTTP プロキシの場合とまったく同じく、サーバー側にとどまります。
