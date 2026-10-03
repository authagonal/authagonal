---
layout: default
title: WebSocket 认证（BFF）
locale: zh-Hans
---

# 通过 BFF 对 WebSocket 进行认证

[BFF](bff) 的存在是为了让浏览器 SPA 永远不持有令牌：SPA 拿到的是一个 httpOnly 会话 Cookie，而 BFF 在代理 API 调用时注入 `Authorization: Bearer` 请求头。这对 HTTP 有效，但浏览器的 `WebSocket` 无法设置 `Authorization` 请求头，而你也不希望把访问令牌放在 URL 中或 JS 可读取的存储中。

**ws-ticket** 流程解决了这个问题。SPA 向 BFF 请求一个短期、一次性的票据，使用该*票据*（而不是令牌）打开套接字，你的 API 宿主则在服务器端用票据兑换真正的访问令牌。令牌从不经过浏览器。

```
Browser ──GET /bff/ws-ticket──► BFF ──(store token under ticket in shared cache)──► returns { ticket }
Browser ──wss://api/…?ticket=── ► API host ──TryRedeemWsTicketAsync──► access token ──► authenticate socket
```

## 1. 在 BFF 上启用 {#1-enable-it-on-the-bff}

ws-ticket 需要显式启用，并且 BFF 和你的 API 宿主必须共享**同一个**分布式缓存（生产环境中为 Redis），这样 API 宿主才能读取 BFF 写入的内容：

```csharp
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConnectionString);

builder.Services.AddAuthagonalBff(o =>
{
    // …your usual BFF options (authority, client id/secret, base path)…
    o.WsTicketsEnabled = true;                  // maps GET {BasePath}/ws-ticket
    o.WsTicketLifetime = TimeSpan.FromSeconds(30); // default
});
```

## 2. 签发并连接（浏览器） {#2-mint--connect-browser}

`GET {BasePath}/ws-ticket` 需要会话 Cookie 和 BFF 的防伪请求头（与任何 BFF 调用相同）。它返回一个不透明票据，该票据绑定到会话中刚刚刷新过的访问令牌。获取票据、建立连接，然后丢弃它。永远不要持久化保存它：

```javascript
async function openSocket() {
  const res = await fetch('/bff/ws-ticket', {
    headers: { 'X-CSRF': '1' },           // your configured AntiForgeryHeader
  });
  const { ticket } = await res.json();     // { ticket, expiresInSeconds }
  return new WebSocket(`wss://api.example.com/live?ticket=${encodeURIComponent(ticket)}`);
}
```

## 3. 兑换票据（API 宿主） {#3-redeem-it-api-host}

在套接字的升级请求中取出 `ticket`，并通过共享缓存将其兑换为访问令牌。`TryRedeemWsTicketAsync` 会查找令牌并**删除**该键，因此一个票据恰好只能使用一次：

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

如果你的 API 宿主是一个独立的程序集，而你不想引用 `Authagonal.Bff`，缓存键的格式是公开的：令牌存储在 `WsTicketKey(ticket)` = `agbff:wst:{ticket}` 下。你可以自己读取并删除它，但最好还是使用随附的辅助方法。

## 安全说明 {#security-notes}

- **256 位随机值，30 秒 TTL。** 票据无法被猜测且生命周期很短；即使它从 URL 或日志中泄露，过了时间窗口也毫无用处。
- **一次性使用，但有一个前提。** `IDistributedCache` 没有原子的“读取并删除”操作，因此辅助方法采用先读取再删除的方式：如果两个请求在任一方删除之前的极短间隙内读取了同一个票据，两者可能都会成功。如需严格的一次性使用，请让缓存使用提供原子弹出操作的存储（Redis `GETDEL`）。
- **浏览器只会携带不透明票据。** 访问令牌始终留在服务器端，与 HTTP 代理完全一样。
