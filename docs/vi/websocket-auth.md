---
layout: default
title: Xác thực WebSocket (BFF)
locale: vi
---

# Xác thực WebSocket từ BFF

[BFF](bff) tồn tại để một SPA trên trình duyệt không bao giờ giữ token: nó nhận một cookie phiên httpOnly,
và BFF chèn header `Authorization: Bearer` khi chuyển tiếp các lời gọi API. Cách đó hoạt động
với HTTP, nhưng một `WebSocket` trên trình duyệt không thể đặt header `Authorization`, và bạn không muốn đặt
access token vào URL hay vào bộ nhớ mà JS đọc được.

Flow **ws-ticket** giải quyết điều này. SPA xin BFF một ticket ngắn hạn, dùng một lần, mở
socket bằng *ticket* (không phải token), và API host của bạn đổi ticket lấy access token thật
ở phía máy chủ. Token không bao giờ chạm tới trình duyệt.

```
Browser ──GET /bff/ws-ticket──► BFF ──(store token under ticket in shared cache)──► returns { ticket }
Browser ──wss://api/…?ticket=── ► API host ──TryRedeemWsTicketAsync──► access token ──► authenticate socket
```

## 1. Bật trên BFF {#1-enable-it-on-the-bff}

Ws-ticket cần bật tường minh, và cả BFF lẫn API host của bạn phải dùng chung **cùng một** distributed cache (Redis
trong production) để API host có thể đọc những gì BFF đã ghi:

```csharp
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConnectionString);

builder.Services.AddAuthagonalBff(o =>
{
    // …your usual BFF options (authority, client id/secret, base path)…
    o.WsTicketsEnabled = true;                  // maps GET {BasePath}/ws-ticket
    o.WsTicketLifetime = TimeSpan.FromSeconds(30); // default
});
```

## 2. Mint và kết nối (trình duyệt) {#2-mint--connect-browser}

`GET {BasePath}/ws-ticket` yêu cầu cookie phiên và header chống giả mạo của BFF (giống như mọi lời gọi BFF
khác). Nó trả về một ticket mờ được gắn với access token vừa được làm mới của phiên. Lấy ticket, kết nối,
rồi bỏ nó đi. Không bao giờ lưu trữ nó:

```javascript
async function openSocket() {
  const res = await fetch('/bff/ws-ticket', {
    headers: { 'X-CSRF': '1' },           // your configured AntiForgeryHeader
  });
  const { ticket } = await res.json();     // { ticket, expiresInSeconds }
  return new WebSocket(`wss://api.example.com/live?ticket=${encodeURIComponent(ticket)}`);
}
```

## 3. Đổi ticket (API host) {#3-redeem-it-api-host}

Trên request nâng cấp kết nối của socket, lấy `ticket` ra và đổi nó lấy access token qua
cache dùng chung. `TryRedeemWsTicketAsync` tra cứu token và **xóa** khóa, nên một ticket chỉ dùng được đúng một lần:

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

Nếu API host của bạn là một assembly riêng và bạn không muốn tham chiếu `Authagonal.Bff`, định dạng khóa cache
là công khai: token được lưu dưới `WsTicketKey(ticket)` = `agbff:wst:{ticket}`. Bạn có thể tự đọc và xóa
nó, nhưng nên ưu tiên helper có sẵn.

## Ghi chú bảo mật {#security-notes}

- **Ngẫu nhiên 256 bit, TTL 30 giây.** Ticket không thể đoán được và có thời gian sống ngắn; kể cả khi bị lộ qua URL/log,
  nó trở nên vô dụng sau khoảng thời gian đó.
- **Dùng một lần, kèm một lưu ý.** `IDistributedCache` không có thao tác get-and-delete nguyên tử, nên helper thực hiện
  get rồi mới delete: hai request đọc cùng một ticket trong khoảng hở cực ngắn trước khi một trong hai xóa nó có thể cùng
  thành công. Để bảo đảm dùng một lần một cách nghiêm ngặt, hãy dùng cho cache một store có thao tác pop nguyên tử (Redis `GETDEL`).
- **Trình duyệt chỉ bao giờ mang ticket mờ.** Access token ở lại phía máy chủ, đúng như với
  proxy HTTP.
