---
layout: default
title: Front-Channel Logout
locale: vi
---

# Front-Channel Logout

Authagonal triển khai **OpenID Connect Front-Channel Logout 1.0**, một cơ chế đăng xuất do trình duyệt thực hiện, bổ sung cho [back-channel logout](index#key-features). Trong khi back-channel logout là một lệnh POST server-to-server, front-channel logout hiển thị URL đăng xuất của từng relying party trong một iframe ẩn để phiên trình duyệt của từng ứng dụng (cookie, local storage) được dọn dẹp ngay bên trong trình duyệt của người dùng.

## Khi nào dùng cách nào {#when-to-use-which}

| Mối quan tâm | Back-Channel | Front-Channel |
|---|---|---|
| Phiên phía server | ✅ | ❌ |
| Cookie / local storage của trình duyệt | ❌ | ✅ |
| Hoạt động khi trình duyệt của người dùng offline | ✅ | ❌ |
| Chịu được lỗi mạng (thử lại) | ✅ | ❌ (chỉ một lần thử, cố gắng hết mức) |

Hầu hết ứng dụng đều có lợi khi cấu hình **cả hai**. Back-channel bảo đảm server được thông báo; front-channel dọn sạch trình duyệt.

## Cấu hình client {#client-configuration}

Thêm một front-channel logout URI vào bản ghi `OAuthClient`:

```json
{
  "clientId": "myapp",
  "frontChannelLogoutUri": "https://myapp.example.com/oidc/frontchannel",
  "frontChannelLogoutSessionRequired": true
}
```

| Trường | Mô tả |
|---|---|
| `FrontChannelLogoutUri` | Endpoint đăng xuất của client mà trình duyệt nhìn thấy được |
| `FrontChannelLogoutSessionRequired` | Nếu là `true` (mặc định), URL được gọi kèm các tham số query `iss` và `sid` để client có thể đối chiếu lệnh đăng xuất với đúng phiên cụ thể |

## Cách hoạt động {#how-it-works}

Khi trình duyệt truy cập `/connect/endsession` (GET hoặc POST):

1. **Xác nhận (chống CSRF).** Nếu trình duyệt có một phiên đã đăng nhập và request không mang `id_token_hint` có `sub` khớp với phiên đó, server trước tiên hiển thị một trang "đăng xuất?" có nút xác nhận thay vì đăng xuất người dùng. Nút này POST trở lại kèm một token ngắn hạn (15 phút) gắn với phiên đó. Đây là điều ngăn một trang bên thứ ba kết thúc phiên của người dùng bằng cách điều hướng tới endpoint (cookie phiên là `SameSite=Lax`, nên nó đi kèm một lệnh GET cấp cao nhất từ trang khác site). Một `id_token_hint` khớp sẽ thay thế cho bước xác nhận.
2. Server tìm mọi client mà người dùng hiện có grant.
3. Với mỗi client có `FrontChannelLogoutUri` vượt qua kiểm tra URL đi ra (loopback được phép vì chính trình duyệt của người dùng gửi request, nhưng các địa chỉ thuộc dải riêng và link-local thì không), server dựng một URL, nối thêm `iss=<issuer>` (và `sid=<session_id>`, khi phiên có giá trị này) nếu `FrontChannelLogoutSessionRequired` là `true`.
4. Server thu hồi các grant được mint cho phiên, đăng xuất người dùng khỏi cookie của authorization server, kích hoạt các thông báo back-channel logout ở chế độ nền, và, khi dựng được ít nhất một URL front-channel, trả về một trang HTML chứa một `<iframe>` ẩn cho mỗi URL:
   ```html
   <iframe src="https://myapp.example.com/oidc/frontchannel?iss=https%3A%2F%2Fauth.example.com&sid=abc123" style="display:none"></iframe>
   ```
   Trang này mang một `Content-Security-Policy` có `frame-src` giới hạn trong các origin của những URL đó, và không có script nào.
5. Đích sau khi đăng xuất được xác định theo cùng một cách dù có iframe hay không. `post_logout_redirect_uri` chỉ được chấp nhận khi request xác định được client (qua audience của `id_token_hint`, hoặc tham số `client_id`) và URI nằm trong `PostLogoutRedirectUris` đã đăng ký của client đó (tham số `state`, nếu được cung cấp, sẽ được nối thêm). Khi có iframe, trang chờ 2 giây (một `meta refresh`) rồi chuyển hướng, hoặc hiển thị thông báo "đã đăng xuất" khi không có đích hợp lệ. Khi không có URL front-channel nào, server chuyển hướng ngay (`302`), hoặc trả `200` với một `message` dạng JSON khi không có đích hợp lệ.

`id_token_hint` chỉ được chấp nhận nếu đó là một ID token do chính server này ký (ES256, `typ: JWT`) với một audience duy nhất. Token đã hết hạn vẫn được chấp nhận. Access token, và logout token, bị từ chối khi dùng làm hint. Nếu gửi cả `client_id` và `id_token_hint` mà chúng chỉ tới hai client khác nhau, request thất bại với `400 invalid_request`.

Endpoint JSON `POST /api/auth/logout` (được nút Đăng xuất của ứng dụng đăng nhập sử dụng) chạy cùng các bước thu hồi và thông báo. Nó không hiển thị iframe: nó trả các URL trong `frontchannel_logout_uris` để bên gọi tự tải (xem [Auth API](auth-api#logout)).

## Trình xử lý đăng xuất phía client {#client-side-logout-handler}

Mỗi relying party nên triển khai URL được tham chiếu bởi `FrontChannelLogoutUri`. Một trình xử lý tối giản:

```http
GET /oidc/frontchannel?iss=https://auth.example.com&sid=abc123
```

1. Kiểm tra `iss` khớp với authorization server mong đợi.
2. Nếu có `sid`, xác nhận nó khớp với ID phiên trong cookie phiên.
3. Xóa phiên cục bộ (cookie, phiên phía server, vùng lưu trữ của SPA).
4. Trả về `200 OK` với body rỗng (hoặc một trang rất nhỏ); người dùng không bao giờ nhìn thấy response này.

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

## Tài liệu discovery {#discovery-document}

Front-channel logout được quảng bá trong `/.well-known/openid-configuration`:

```json
{
  "frontchannel_logout_supported": true,
  "frontchannel_logout_session_supported": true
}
```

## Đăng ký client động {#dynamic-client-registration}

Các client được đăng ký qua [Đăng ký client động](client-registration) có thể bao gồm:

```json
{
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

Việc đăng ký từ chối logout URI không phải là địa chỉ bên ngoài (loopback, link-local, dải riêng và các tên `.localhost`/`.local`/`.internal` bị từ chối với `invalid_client_metadata`).

## Hạn chế {#limitations}

- **Cố gắng hết mức**: các iframe chỉ được tải một lần. Nếu lỗi mạng hoặc tiện ích trình duyệt chặn chúng, sẽ không có lần thử lại. Hãy kết hợp với back-channel logout để có độ tin cậy.
- **Cookie bên thứ ba**: một số trình duyệt mặc định chặn cookie trong các iframe khác site. Nếu RP của bạn dựa vào cookie bên thứ nhất, hãy xác nhận trình xử lý đăng xuất không phụ thuộc vào việc cookie được gửi kèm.
- **Thời gian chờ**: trang chờ khoảng 2 giây trước khi chuyển hướng. Các trình xử lý đăng xuất nặng của RP có thể không hoàn tất kịp.

## Liên quan {#related}

- [Đăng ký client động](client-registration), các tham số front-channel trong request đăng ký
- [OAuth Scope](scopes), chấp thuận nhận biết scope bổ trợ cho flow đăng xuất
