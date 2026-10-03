---
layout: default
title: Đăng ký client động
locale: vi
---

# Đăng ký client động

Authagonal triển khai **OAuth 2.0 Dynamic Client Registration** ([RFC 7591](https://datatracker.ietf.org/doc/html/rfc7591)), cho phép các ứng dụng client tự đăng ký lúc chạy mà không cần quản trị viên can thiệp.

## Bật endpoint {#enabling-the-endpoint}

Đăng ký động **bị tắt theo mặc định**. Bật nó qua cấu hình:

```json
{
  "Auth": {
    "DynamicClientRegistrationEnabled": true
  }
}
```

Hoặc đặt biến môi trường `Auth__DynamicClientRegistrationEnabled=true`. Một host multi-tenant có thể ghi đè thiết lập này theo từng tenant thông qua `ITenantContext.DynamicClientRegistrationEnabled`: câu trả lời riêng của tenant được ưu tiên, còn `null` thì dùng tùy chọn chung của host.

Khi được bật, tài liệu discovery sẽ quảng bá endpoint:

```
GET /.well-known/openid-configuration
```
```json
{
  "registration_endpoint": "https://auth.example.com/connect/register"
}
```

## Đăng ký một client {#registering-a-client}

```
POST /connect/register
Content-Type: application/json

{
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "token_endpoint_auth_method": "client_secret_basic",
  "scope": "openid profile email offline_access",
  "audiences": ["https://api.myapp.example.com"],
  "allowed_cors_origins": ["https://myapp.example.com"],
  "backchannel_logout_uri": "https://myapp.example.com/oidc/backchannel",
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

### Response {#response}

```
HTTP/1.1 201 Created
Content-Type: application/json

{
  "client_id": "a1b2c3d4e5f6...",
  "client_secret": "xkCd2_base64url...",
  "client_id_issued_at": 1745000000,
  "client_secret_expires_at": 0,
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "scope": "openid profile email offline_access",
  "token_endpoint_auth_method": "client_secret_basic"
}
```

`client_secret` chỉ được trả về **một lần** và không thể lấy lại sau đó. Hãy lưu trữ nó an toàn. Response được gửi kèm `Cache-Control: no-store`. `client_id` gồm 32 ký tự hex viết thường, và `client_secret_expires_at` luôn là `0` (secret không hết hạn). Client public (`none`) và client `private_key_jwt` không nhận `client_secret` trong response. Response chỉ trả lại những trường được liệt kê ở trên: `audiences`, `jwks`, `jwks_uri`, `allowed_cors_origins` và các trường logout được lưu nhưng không được trả về.

## Tham số request {#request-parameters}

| Tham số | Bắt buộc | Ghi chú |
|---|---|---|
| `client_name` | không | Mặc định là `client_id` được sinh ra nếu bỏ qua |
| `redirect_uris` | có điều kiện | Bắt buộc khi `grant_types` chứa `authorization_code`. Phải là URI tuyệt đối; các scheme `javascript:`/`data:`/`vbscript:`/`file:` bị từ chối (scheme tùy chỉnh native cho deep link trên di động thì được). URI có fragment bị từ chối (RFC 6749 §3.1.2), và `http` không mã hóa chỉ được chấp nhận cho host loopback (RFC 8252 §7.3). Tối đa 20 mục, mỗi mục tối đa 2048 ký tự. |
| `post_logout_redirect_uris` | không | Các đích chuyển hướng hợp lệ sau khi đăng xuất. Cùng giới hạn 20 mục / 2048 ký tự như `redirect_uris`. |
| `grant_types` | không | Mặc định là `["authorization_code"]`. **Chỉ `authorization_code` và `refresh_token` là đăng ký được**: `client_credentials`, `implicit`, device và mọi grant type khác đều bị từ chối với `invalid_client_metadata`, nên đăng ký mở không bao giờ tạo ra được một client machine-to-machine. `refresh_token` được tự động thêm vào nếu yêu cầu `offline_access`. |
| `token_endpoint_auth_method` | không | `client_secret_basic` (mặc định), `client_secret_post`, `private_key_jwt`, hoặc `none` cho client public. Mọi giá trị khác bị từ chối với `invalid_client_metadata`. |
| `jwks` / `jwks_uri` | khi dùng `private_key_jwt` | Các khóa công khai của client. Bắt buộc có một trong hai với `private_key_jwt` (nếu không sẽ là `invalid_client_metadata`); `jwks_uri` phải vượt qua lớp bảo vệ URL đi ra (phải là một địa chỉ bên ngoài). Client `private_key_jwt` không được cấp secret. |
| `scope` | không | Các scope cách nhau bằng dấu cách. Chỉ năm scope OIDC dựng sẵn (`openid`, `profile`, `email`, `phone`, `offline_access`) cộng với những scope được nêu trong `Auth:DynamicClientRegistrationScopes` là đăng ký được: việc tồn tại trong scope store là **chưa** đủ (xem [Scope](scopes)). Các scope bị giới hạn theo role và scope quản trị (`AdminApi:Scope`, mặc định `authagonal-admin`) không bao giờ đăng ký được. |
| `audiences` | không | Các giá trị JWT `aud` được thêm vào access token. Tối đa 20 mục, mỗi mục tối đa 512 ký tự, mỗi mục là một URI tuyệt đối không có fragment; giá trị sai là `invalid_client_metadata`. |
| `allowed_cors_origins` | không | Mỗi mục phải là một origin hợp lệ (nếu không sẽ là `invalid_client_metadata`), nhưng giá trị **không được lưu như được gửi**: các origin được phép của client được suy ra từ origin của chính các `redirect_uris` dạng `https` của nó, nên bên đăng ký chỉ có thể tới những origin mà nó đã chứng minh có redirect URI. |
| `backchannel_logout_uri` | không | Bật [Back-Channel Logout](index#key-features) |
| `frontchannel_logout_uri` | không | Bật [Front-Channel Logout](front-channel-logout) |
| `frontchannel_logout_session_required` | không | Mặc định là `true`; khi là `true`, URL đăng xuất mang các tham số `iss` và `sid` |

## Mặc định và bất biến {#defaults--invariants}

- **Bắt buộc PKCE**: `RequirePkce` luôn là `true` với các client đăng ký động.
- **Bắt buộc chấp thuận**: `RequireConsent` luôn là `true`, nên người dùng sẽ thấy màn hình chấp thuận cho một client tự đăng ký ngay cả ở những nơi mà một client được cấu hình tĩnh sẽ bỏ qua bước này.
- **Client public**: `token_endpoint_auth_method: "none"` tạo ra một client không có secret. Vẫn bắt buộc PKCE.
- **Truy cập offline**: yêu cầu scope `offline_access` sẽ ngầm thêm `refresh_token` vào `grant_types`.

## Response lỗi {#error-responses}

| HTTP | `error` | Nguyên nhân |
|---|---|---|
| `400` | `invalid_redirect_uri` | Một trong các `redirect_uris` không phải URI tuyệt đối hợp lệ, dùng pseudo-scheme script/data/file, mang fragment, là `http` không mã hóa tới host không phải loopback, hoặc (với cả hai danh sách URI) dài hơn 2048 ký tự |
| `400` | `invalid_client_metadata` | Yêu cầu một grant type không đăng ký được, thiếu `redirect_uris` cho grant type cần nó, `token_endpoint_auth_method` không được hỗ trợ, `private_key_jwt` không có `jwks`/`jwks_uri` (hoặc `jwks_uri` không an toàn), `audiences` không hợp lệ, một mục `allowed_cors_origins` không phải là origin, hoặc một logout URI không phải địa chỉ bên ngoài |
| `400` | `invalid_scope` | Một scope được yêu cầu vừa không phải scope dựng sẵn vừa không được đăng ký |
| `400` | `invalid_client_metadata` | Nhiều hơn 20 `redirect_uris` / `post_logout_redirect_uris` |
| `403` | `invalid_scope` | Một scope được yêu cầu không đăng ký được: không nằm trong `Auth:DynamicClientRegistrationScopes`, hoặc bị giới hạn theo role |
| `403` | `invalid_scope` | Scope quản trị được yêu cầu; scope này không bao giờ được cấp qua đăng ký |
| `403` | `invalid_scope` | Một `IClientScopeGuard` đã đăng ký từ chối một scope được yêu cầu (bên gọi ẩn danh được truyền vào nó) |
| `403` | `not_supported` | Đăng ký client động chưa được bật |
| `429` | `rate_limited` | Quá nhiều lần đăng ký từ IP này (10 lần mỗi giờ) |

## Lưu ý bảo mật {#security-considerations}

Endpoint đăng ký **không yêu cầu xác thực**, nhưng được giới hạn chặt chẽ ngay từ thiết kế:

- **Giới hạn tần suất**: 10 lần đăng ký mỗi địa chỉ nguồn trong một giờ trượt (`429 rate_limited`), nên client store không thể bị làm ngập. Địa chỉ được dùng là địa chỉ mà bên gọi không thể tự chọn (giá trị được chuyển tiếp không được tin một cách mù quáng).
- **Hạn chế grant type**: chỉ `authorization_code` + `refresh_token`; một client đã đăng ký luôn cần một flow có người dùng tham gia và không bao giờ hoạt động được như một client machine-to-machine.
- **Scope theo allowlist, không kế thừa**: bên đăng ký chỉ được khai báo năm scope OIDC dựng sẵn và không gì khác, trừ khi người vận hành liệt kê một scope trong `Auth:DynamicClientRegistrationScopes`. Việc tồn tại trong scope store không phải là sự cho phép: một scope tồn tại vì có client nào đó cần nó, chứ không phải vì mọi bên đăng ký ẩn danh đều được phép nhận nó.
- **Dành riêng scope quản trị**: scope `authagonal-admin` (hoặc bất kỳ giá trị nào `AdminApi:Scope` được đặt) bị từ chối, nên việc đăng ký không bao giờ tạo ra được một client có thể tới [admin API](admin-api).
- **Kiểm tra logout URI**: `backchannel_logout_uri` và `frontchannel_logout_uri` được server truy cập tới, nên chúng phải là các endpoint http(s) bên ngoài: loopback, RFC1918, link-local (bao gồm cả địa chỉ metadata của cloud) và các host `.internal`/`.local` đều bị từ chối.
- **Bản ghi có giới hạn**: tối đa 20 redirect URI, mỗi URI tối đa 2048 ký tự, nên một lần đăng ký không thể bị dùng để làm phình client store.
- **Origin CORS được suy ra, không được tin theo request**: các origin được lưu đến từ chính các redirect URI `https` của client, không bao giờ từ body của request.
- **Luôn bắt buộc PKCE**, và **luôn bắt buộc chấp thuận**, với các client đã đăng ký.

Điều mà endpoint **không** giới hạn, trừ khi bên đăng ký chủ động chọn, là audience. RFC 7591 không có trường nào cho nó, nên một lần đăng ký thông thường hoàn toàn không có `audiences` (một phần mở rộng của Authagonal): client chưa bao giờ được hỏi, danh sách của nó ở trạng thái "chưa đặt", và nó có thể nêu bất kỳ URI tuyệt đối nào làm `resource` tại authorization endpoint và nhận được token mang giá trị đó làm `aud`. Đây là chủ ý (đặc tả ủy quyền của MCP yêu cầu client nêu MCP server làm resource, và một MCP client là một client DCR), và nó khiến resource server phải chịu trách nhiệm ủy quyền dựa trên `scope` thay vì dựa trên `iss` + `aud` + `sub`. **Việc gửi** `audiences`, kể cả dưới dạng danh sách rỗng, là một câu trả lời và ghim client vào đó: danh sách không rỗng là allowlist cho `resource`, còn `[]` tường minh nghĩa là client hoàn toàn không được nêu resource nào. Token exchange là ngoại lệ: ở đó `Audiences` chưa đặt sẽ bị từ chối thẳng, nên một client đã đăng ký không thể hướng một token được exchange tới bất kỳ đâu. Xem [Audience và resource indicator](configuration#audiences-and-resource-indicators-rfc-8707).

Để kiểm soát chặt hơn (initial access token, mTLS, software statement), hãy đặt middleware của riêng bạn hoặc một `IAuthHook` trước endpoint. Hãy cân nhắc tắt hoàn toàn đăng ký động và quản lý client qua admin API trong những môi trường không yêu cầu tự đăng ký.
