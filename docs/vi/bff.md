---
layout: default
title: Backend-for-Frontend (BFF)
locale: vi
---

# Backend-for-Frontend (BFF)

Một SPA trên trình duyệt giữ access token hoặc refresh token trong vùng lưu trữ mà JavaScript truy cập được sẽ để lộ cả hai trước XSS. BFF là một **client OIDC confidential do bạn tự host trên backend của mình**. Nó chạy flow authorization-code + PKCE ở phía server, giữ các token trong một phiên phía server, và không đưa cho trình duyệt thứ gì ngoài một cookie phiên httpOnly. Các lời gọi từ SPA tới API của bạn đi qua proxy của BFF, proxy này gắn access token của phiên vào request khi chuyển đi.

Nó được phát hành ở hai dạng, cùng nói một giao thức:

| Gói | Dành cho | Mã nguồn |
|---|---|---|
| `Authagonal.Bff` (NuGet) | Host ASP.NET Core | `src/Authagonal.Bff/` |
| `@authagonal/bff` (npm) | Express và Next.js (App Router) | `bff-lib/` |

BFF là một client confidential bình thường của auth host: nó dùng OIDC discovery cùng các endpoint authorize, token, revocation và end-session, nên tất cả những gì nó cần từ auth host là một client đã được đăng ký.

## 1. Đăng ký một client BFF {#1-register-a-bff-client}

Client phải là **confidential** (có secret), bắt buộc PKCE, và được phép `offline_access` nếu bạn muốn refresh ở phía server. Hãy đăng ký:

- redirect URI `https://app.example.com/bff/callback`
- post-logout redirect URI `https://app.example.com/` (và `https://app.example.com/bff/logout-callback` nếu bạn dùng `returnUrl` khi đăng xuất, xem [Đăng xuất](#logout))

Để có "đăng xuất ở mọi nơi" trên phạm vi toàn subject thông qua [back-channel logout](index#key-features), hãy đăng ký client với `BackChannelLogoutSessionRequired = false`. BFF chấp nhận logout token mang `sid` hoặc chỉ mang `sub`.

## 2. Kết nối (.NET) {#2-wire-it-up-net}

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

`UseForwardedHeaders` rất quan trọng: đứng sau một proxy kết thúc TLS, BFF chỉ thấy http thường, nên thiếu nó thì cookie phiên `__Host-` được ghi mà không có `Secure` và trình duyệt sẽ bỏ cookie đó. Xem [Cài đặt](installation#production-security-checklist) để biết cách khai báo proxy.

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

Với Next.js, hãy dùng `createBffRoute` từ `@authagonal/bff/next` trong `app/bff/[...bff]/route.ts`. Xem `bff-lib/README.md` cho cả hai.

## Các endpoint {#endpoints}

Được gắn dưới `BasePath` (mặc định `/bff`).

| Route | Mục đích |
|---|---|
| `GET /bff/login?returnUrl=/` | Bắt đầu đăng nhập: đặt một cookie correlation riêng cho lần đăng nhập và chuyển hướng tới `/connect/authorize` với PKCE (`S256`), `state` và `nonce`. |
| `GET /bff/callback` | Redirect URI của OIDC (`CallbackPath`). Đổi code lấy token và tạo phiên. |
| `GET /bff/user` | `{ isAuthenticated, claims, sessionExpiresAt }`. Cần header chống giả mạo. Luôn có `Cache-Control: no-store`. |
| `GET\|POST /bff/logout` | Kết thúc phiên tại chỗ và tại auth host. `POST` cần header chống giả mạo; `GET` là một lần điều hướng thông thường. |
| `GET /bff/logout-callback` | Điểm đến của vòng end-session khi lệnh đăng xuất được truyền `returnUrl`. |
| `POST /bff/backchannel-logout` | Bên tiếp nhận server-to-server cho OIDC back-channel logout. Được xác thực bằng logout token đã ký, nên không cần header CSRF. |
| `GET /bff/ws-ticket` | Tùy chọn bật (`WsTicketsEnabled`), chỉ có trên .NET. Xem [Xác thực WebSocket](websocket-auth). |
| `GET /bff/token?resource=...` | Tùy chọn bật (`TokenEndpointEnabled`), chỉ có trên .NET. Xem [Token được exchange cho origin khác](#exchanged-tokens-for-another-origin). |
| `ANY /bff/api/**` | Proxy gắn token. Chỉ được map khi `Upstreams` không rỗng. |

`claims` trên `/bff/user` là một map string phẳng chứa các claim của id_token, trừ đi các claim thuộc cơ chế giao thức (`iss`, `aud`, `exp`, `iat`, `nbf`, `nonce`, `at_hash`, `c_hash`, `s_hash`, `azp`, `jti`, `sid`, `auth_time`, `acr`, `amr`, `typ`). Các claim dạng mảng như `roles` và `groups` được nối bằng dấu cách. Các claim được đọc lại từ mỗi id_token sau khi refresh, nên một role được cấp sau khi đăng nhập sẽ tới SPA ở lần refresh kế tiếp thay vì lần đăng nhập kế tiếp.

## Từ trình duyệt {#from-the-browser}

Mọi lời gọi không phải điều hướng đều mang một header tĩnh, header này chống CSRF cùng với `SameSite=Lax`. Mọi giá trị đều được chấp nhận; chỉ sự hiện diện của header được kiểm tra.

```js
const me = await fetch('/bff/user', { headers: { 'X-Authagonal-Bff': '1' } }).then(r => r.json());
if (!me.isAuthenticated) location.href = '/bff/login?returnUrl=' + encodeURIComponent(location.pathname);
```

Đăng nhập và đăng xuất bằng cách **điều hướng** (`location.href = '/bff/login'`), không phải bằng `fetch`. Tên header là `AntiForgeryHeader`.

## Proxy {#the-proxy}

Cấu hình các upstream và SPA sẽ gọi `/bff/api/<prefix>/...`:

```csharp
o.Upstreams.Add(new BffUpstream
{
    Prefix = "/orders",
    TargetBaseUrl = "https://api.internal.example.com",
});
```

Proxy yêu cầu header chống giả mạo và một phiên còn hiệu lực, refresh access token nếu token chỉ còn trong khoảng `RefreshThresholdSeconds` trước khi hết hạn, chuyển tiếp request với `Authorization: Bearer`, và stream response trở lại. Cookie phiên không bao giờ được chuyển tiếp. Các header `X-Forwarded-*`, `Forwarded` và `X-Real-IP` gửi đến bị loại bỏ và được khẳng định lại từ trạng thái riêng của BFF, nên một script trong SPA không thể bảo chứng cho IP hay scheme của client. Các chuyển hướng từ upstream được chuyển tiếp về trình duyệt thay vì được proxy tự đi theo.

Cho mỗi upstream (`BffUpstream`):

| Thuộc tính | Ý nghĩa |
|---|---|
| `Prefix` | Đường dẫn sau `/bff/api` dùng để chọn upstream này. |
| `TargetBaseUrl` | Nơi các request khớp được chuyển tiếp tới. |
| `StripPrefix` | Bỏ phần prefix đã khớp trước khi nối vào đích. Cho phép một BFF phân phối tới nhiều backend dùng chung một không gian đường dẫn. |
| `RequiredAuthority` | Các cặp `"type:action"`. Proxy kiểm tra `authorization_details` theo RFC 9396 của token gửi đi và trả 403 trừ khi mọi cặp đều được cho phép. Xem [Xác thực cho agent](agentic-auth). |
| `AuthorityLocation` | Gốc `locations` mà upstream này được biết đến, khi nó khác `TargetBaseUrl`. |
| `StrictAuthority` | Từ chối lời gọi khi một grant mang ràng buộc mà proxy không đánh giá được, thay vì cho đi qua. |

Các tùy chọn liên quan: `AllowAnonymousProxyRequests` chuyển tiếp một request không có phiên (hoặc có phiên không refresh được) mà không kèm header `Authorization` thay vì trả 401, dành cho các API tự quyết định. Một route bị chặn bởi `RequiredAuthority` không bao giờ là ẩn danh. `ExchangeRoutes` gắn các route của proxy với một [exchange theo RFC 8693](agentic-auth), để upstream nhận được một token đã thu hẹp phạm vi và gắn với ngữ cảnh thay vì token chính của phiên: mỗi route có một `PathPattern` với đúng một placeholder (ràng buộc duy nhất được hỗ trợ là `:guid`), đoạn đường dẫn bắt được sẽ được gửi làm tham số exchange, và một exchange bị từ chối sẽ trả 403. Một ràng buộc không xác định sẽ gây lỗi lúc khởi động thay vì âm thầm chuyển tiếp token có phạm vi rộng hơn.

## Đăng xuất {#logout}

`/bff/logout` thu hồi refresh token của phiên (cố gắng hết mức), xóa phiên, xóa cookie, và chuyển hướng tới endpoint end-session của auth host kèm `id_token_hint` của phiên. Khi không có phiên thì không có gì để kết thúc tại auth host, nên nó chuyển hướng thẳng tới `PostLogoutRedirectUri`. Khi có `returnUrl`, auth host chuyển hướng về `/bff/logout-callback`, nơi đích được kiểm tra lại với `ReturnUrlAllowlist` rồi mới chuyển hướng tới đó. Hãy đăng ký callback này làm post-logout redirect URI cho client.

Back-channel logout xóa các phiên ở phía server: theo `sid` khi logout token có trường này, nếu không thì xóa mọi phiên của `sub`. Việc xóa được giới hạn trong tenant có issuer đã ký token, vì `sub` chỉ là duy nhất trong phạm vi một issuer. Logout token phải mang `iat` và phải còn mới.

## Tham chiếu tùy chọn (.NET) {#options-reference-net}

| Tùy chọn | Mặc định | Ghi chú |
|---|---|---|
| `Authority`, `ClientId`, `ClientSecret` | bắt buộc | Không bắt buộc khi đã đặt `TenantQueryParam`. |
| `Scope` | `openid profile offline_access` | `offline_access` bật refresh. |
| `BasePath` | `/bff` | |
| `CallbackPath` | `/bff/callback` | Phải trùng với redirect URI đã đăng ký. |
| `CookieName` | `__Host-agbff` | Tiền tố `__Host-` buộc Secure, `Path=/` và không có Domain, nên cần https. Ghi đè khi phát triển cục bộ bằng http. |
| `SessionLifetime` | 8 giờ | Giới hạn tuyệt đối bất kể số lần refresh. |
| `PersistentCookie` | `false` | Khi bật, cookie có `Max-Age` bị giới hạn bởi `SessionLifetime` và tồn tại qua lần khởi động lại trình duyệt ("duy trì đăng nhập"). Back-channel logout vẫn kết thúc phiên. |
| `CorrelationLifetime` | 30 phút | Thời gian một lần đăng nhập được phép kéo dài giữa `/bff/login` và callback. Bao gồm đăng ký, email xác minh, đăng nhập. |
| `RefreshThresholdSeconds` | 60 | |
| `ReturnUrlAllowlist` | rỗng | Các origin mà một `returnUrl` không tương đối được phép trỏ tới. Đường dẫn tương đối luôn được cho phép; mọi giá trị khác trở thành `/`. |
| `LoginPassthroughParams` | rỗng | Tên các tham số query được sao chép từ `/bff/login` sang `/connect/authorize` (ví dụ `idp_hint`). Các tham số chuẩn luôn được ưu tiên. |
| `AntiForgeryHeader` | `X-Authagonal-Bff` | |
| `PostLogoutRedirectUri` | không có | |
| `WsTicketsEnabled`, `WsTicketLifetime`, `TicketExchangeParams` | tắt, 30 s, rỗng | Xem [Xác thực WebSocket](websocket-auth). |
| `TokenEndpointEnabled`, `TokenEndpointResources`, `TokenEndpointExchangeParams` | tắt, rỗng, rỗng | Bật nó mà không có resource nào sẽ gây lỗi lúc khởi động. |
| `Upstreams`, `ExchangeRoutes`, `AllowAnonymousProxyRequests` | rỗng, rỗng, `false` | Xem [Proxy](#the-proxy). |
| `TenantQueryParam` | không có | Chế độ multi-tenant, xem bên dưới. |

`SessionMode` chỉ có `Store` được triển khai; `Stateless` được dành sẵn và gây lỗi lúc khởi động.

Gói Node nhận các tên camelCase tương đương của `authority`, `clientId`, `clientSecret`, `scope`, `basePath`, `callbackPath`, `cookieName`, `refreshThresholdSeconds`, `returnUrlAllowlist`, `postLogoutRedirectUri`, `antiForgeryHeader`, `sessionLifetimeSeconds`, `upstreams` và `tenantQueryParam`, cùng với `cookieSecret`, `sessionStore`, `cookieProtector`, `tenantResolver` và `clientIp`. Gói này không có endpoint websocket-ticket hay endpoint token.

## Phiên và việc chạy nhiều hơn một instance {#sessions-and-running-more-than-one-instance}

Các phiên được lưu qua `IBffSessionStore`. Mặc định là `IDistributedCache`, nằm trong bộ nhớ trừ khi bạn đăng ký một cache thật (ví dụ Redis) **trước** `AddAuthagonalBff`.

Riêng một cache dùng chung là chưa đủ. Cơ chế single-flight cho refresh hoạt động theo từng process, trong khi phiên và refresh token xoay vòng của nó nằm trong cache dùng chung. Hai replica có thể đọc cùng một phiên, cùng thấy phiên cần refresh, và cùng đổi một refresh token. Auth host coi lần đổi thứ hai là replay của một token bị đánh cắp và thu hồi toàn bộ họ grant, khiến người dùng bị đăng xuất ở mọi nơi. Hãy cung cấp một khóa liên replica theo một trong hai cách:

- **Đăng ký một `ILeaseProvider`** (backend bất kỳ). Các provider Azure, AWS và SQL cung cấp sẵn một cái thông qua `AddAuthagonalClustering`. Xem [Mở rộng quy mô](scaling).
- **Triển khai `IBffRefreshLockStore` trên store phiên của bạn** (`TryAcquireRefreshLockAsync(sessionId, ttl)` và `ReleaseRefreshLockAsync`). Đây là một thao tác ghi có điều kiện kèm TTL, ví dụ `SET NX PX` trên Redis. Store mặc định không cung cấp được nó vì `IDistributedCache` không có thao tác ghi-nếu-chưa-có. Store phiên của Node mang các hàm tương đương `acquireRefreshLock` / `releaseRefreshLock`.

Nếu không có cả hai, triển khai sẽ dựa vào `Auth:RefreshTokenReuseGraceSeconds` của auth host, giá trị mặc định là 0 (nghiêm ngặt) trong Server host. BFF ghi một cảnh báo lúc khởi động khi store phiên có vẻ được dùng chung mà không có khóa nào.

Một `IBffSessionStore` tùy chỉnh phải tôn trọng tham số `tenantKey` trên `RemoveBySidAsync` và `RemoveBySubjectAsync`. Các điểm mở rộng còn lại là `ICookieProtector` (mặc định: ASP.NET Data Protection) và `ITokenClient`.

## Nhiều tenant từ một BFF {#many-tenants-from-one-bff}

Đặt `TenantQueryParam` (ví dụ `"slug"`) và đăng ký một `IBffTenantResolver`. `/bff/login?slug=acme` phân giải `BffTenantConfig` của tenant (authority, client id, secret, scope), khóa tenant được lưu trên phiên để các request sau phân giải lại, và back-channel logout phân giải tenant từ `iss` của token thông qua `ResolveByIssuerAsync`. Khi không đặt `TenantQueryParam`, BFF hoạt động ở chế độ single-tenant và dùng các tùy chọn tĩnh.

## Token được exchange cho origin khác {#exchanged-tokens-for-another-origin}

Mô hình cookie không thể tới một resource server ở origin khác (chẳng hạn một ứng dụng iframe mà SPA nhúng vào). `TokenEndpointEnabled` thêm `GET /bff/token?resource=<audience>`, trả về `{ accessToken, expiresInSeconds }` cho một token **được exchange** theo RFC 8693: nhắm tới một resource trong `TokenEndpointResources` (mọi giá trị khác trả 400 `resource_not_allowed`), gắn với các giá trị `TokenEndpointExchangeParams` có trên query, và có thời hạn ngắn. Trình duyệt không bao giờ nhận token chính của phiên, và chỉ nên giữ token được exchange trong bộ nhớ. Client của tenant cần grant token-exchange và phải khai báo các resource đó làm audience của mình. Một exchange bị từ chối sẽ trả 403.
