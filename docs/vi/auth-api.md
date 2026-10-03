---
layout: default
title: Auth API
locale: vi
---

# Auth API

Các endpoint này vận hành SPA đăng nhập. Chúng dùng xác thực bằng cookie (`SameSite=Lax`, `HttpOnly`).

Nếu bạn đang xây dựng một giao diện đăng nhập tùy chỉnh, đây là những endpoint bạn cần lập trình theo.

## Endpoint {#endpoints}

### Đăng nhập {#login}

```
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**Thành công (200):** Đặt một auth cookie và trả về:

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe",
  "mfaAvailable": false
}
```

`mfaAvailable` là `true` khi `MfaPolicy` của client là `Enabled` nhưng người dùng chưa đăng ký (giao diện có thể đề nghị thiết lập); khi đó một trường `clientId` cũng được trả về kèm theo.

**Yêu cầu MFA (200):** Nếu người dùng đã đăng ký MFA, họ **luôn** bị yêu cầu xác minh, bất kể `MfaPolicy` của client đang gửi request là gì (MFA là thuộc tính của người dùng/phiên, không phải của client):

```json
{
  "mfaRequired": true,
  "challengeId": "a1b2c3...",
  "methods": ["totp", "webauthn", "recoverycode"],
  "webAuthn": { /* PublicKeyCredentialRequestOptions */ }
}
```

Client nên chuyển hướng tới một trang xác minh MFA và gọi `POST /api/auth/mfa/verify`.

**Yêu cầu thiết lập MFA (200):** Nếu `MfaPolicy` là `Required` và người dùng chưa đăng ký MFA:

```json
{
  "mfaSetupRequired": true,
  "setupToken": "abc123..."
}
```

Client nên chuyển hướng tới một trang thiết lập MFA. Setup token xác thực người dùng với các endpoint thiết lập MFA qua header `X-MFA-Setup-Token`.

**Phản hồi lỗi:**

| `error` | Trạng thái | Mô tả |
|---|---|---|
| `invalid_credentials` | 401 | Sai email hoặc mật khẩu. Cố ý giống hệt nhau với email không tồn tại (chống dò tìm tài khoản). |
| `locked_out` | 423 | Quá nhiều lần thử thất bại. Có kèm `retryAfter` (giây). |
| `account_disabled` | 403 | Tài khoản đã bị vô hiệu hóa (chỉ được báo sau khi nhập đúng mật khẩu) |
| `email_not_confirmed` | 403 | Email chưa được xác minh (chỉ được báo sau khi nhập đúng mật khẩu) |
| `sso_required` | 409 | Tên miền yêu cầu SSO. `redirectUrl` trỏ tới trang đăng nhập SSO. |
| `captcha_failed` | 400 | Xác minh Turnstile thất bại (chỉ khi Turnstile được cấu hình; khi đó request cần có trường `turnstileToken`) |
| `email_required` | 400 | Trường email trống |
| `password_required` | 400 | Trường mật khẩu trống |

### Đăng ký {#register}

```
POST /api/auth/register
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Tạo một tài khoản người dùng mới và gửi email xác minh. Trả về `201 { "success": true, "userId": "..." }`. Các trường tùy chọn: `locale` (thẻ BCP-47 được lưu trên người dùng) và `customAttributes` (một map chuỗi).

Việc đăng ký cố ý được thiết kế **trung lập trước việc dò tìm tài khoản**: nếu email đã được đăng ký, phản hồi vẫn là cùng một `201` trung lập (kèm một `userId` dùng một lần rồi bỏ) và chủ sở hữu thật sẽ nhận một email thông báo đăng nhập/đặt lại mật khẩu thay vào đó. Việc đăng ký cũng bị giới hạn tần suất theo IP, trả `429 rate_limited` khi vượt quá (cửa sổ thời gian và mức trần có thể cấu hình qua `Auth:MaxRegistrationsPerIp` / `Auth:RegistrationWindowMinutes`).

### Xác nhận email {#confirm-email}

```
GET  /api/auth/confirm-email?token={token}
POST /api/auth/confirm-email?token={token}
```

Xác nhận địa chỉ email của người dùng bằng token trong email xác minh. `GET` là liên kết bấm được trong email, nó chuyển hướng tới `/login?email_confirmed=1` (cộng thêm tham số `continue_client` khi việc đăng ký bắt nguồn từ một luồng OAuth). `POST` là đường dành cho chương trình và trả về JSON (token cũng có thể được gửi trong body JSON dưới dạng `{ "token": "..." }`); phản hồi có thể kèm một `appLink` tùy chọn (đích "tiếp tục tới ứng dụng").

### Provider {#providers}

```
GET /api/auth/providers
```

Trả về danh sách các identity provider bên ngoài đã được cấu hình (để hiển thị các nút SSO):

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "type": "oidc", "iconUrl": null, "loginUrl": "/oidc/google/login" }
  ],
  "turnstileSiteKey": null
}
```

Các kết nối có cấu hình `AllowedDomains` bị **loại trừ**: chúng được truy cập theo kiểu nhập email trước qua `/api/auth/sso-check` thay vì qua một nút. `turnstileSiteKey` được đặt khi Cloudflare Turnstile được cấu hình (khi đó giao diện đăng nhập phải gửi `turnstileToken` cùng các request đăng nhập/đăng ký/mật khẩu).

### Đăng xuất {#logout}

```
POST /api/auth/logout
```

Kết thúc phiên của bên gọi theo cùng cách `/connect/endsession` làm: logout token back-channel được gửi tới các relying party có URI đã đăng ký, các grant được phát hành cho phiên đó bị thu hồi, và auth cookie bị xóa. Yêu cầu xác thực bằng cookie và một request cùng origin. Trả về `200`:

```json
{
  "success": true,
  "frontchannel_logout_uris": ["https://myapp.example.com/oidc/frontchannel"]
}
```

`frontchannel_logout_uris` liệt kê các URL đăng xuất front-channel mà bên gọi nên tải (trong iframe ẩn) để hoàn tất việc đăng xuất trong trình duyệt; danh sách rỗng khi không có client nào đăng ký URL như vậy. Xem [Đăng xuất front-channel](front-channel-logout).

### Quên mật khẩu {#forgot-password}

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

Luôn trả về `200` (chống dò tìm tài khoản). Nếu người dùng tồn tại, một email đặt lại mật khẩu sẽ được gửi.

### Đặt lại mật khẩu {#reset-password}

```
POST /api/auth/reset-password
Content-Type: application/json

{
  "token": "base64-encoded-token",
  "newPassword": "NewSecurePass1!"
}
```

| `error` | Mô tả |
|---|---|
| `weak_password` | Không đáp ứng yêu cầu về độ mạnh |
| `invalid_token` | Token sai định dạng |
| `token_expired` | Token đã hết hạn (mặc định có hiệu lực 60 phút, có thể cấu hình qua `Auth:PasswordResetExpiryMinutes`) |

### Phiên {#session}

```
GET /api/auth/session
```

Trả về thông tin phiên hiện tại nếu đã xác thực:

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

Trả về `401` nếu chưa xác thực.

### Ứng dụng {#apps}

```
GET /api/auth/apps
```

Trả về các liên kết ứng dụng của tenant cho trình khởi chạy "quay lại ứng dụng" trên trang tài khoản: các client đang bật có home URI (`initiateLoginUri` được ưu tiên hơn `clientUri`). Mỗi mục là `{ clientId, clientName, homeUri, logoUri, isDefault }`; đúng một ứng dụng được đánh dấu mặc định (client được gắn cờ, hoặc client duy nhất có home URI). Yêu cầu xác thực bằng cookie.

### Hồ sơ (tự phục vụ) {#profile-self-service}

```
GET   /api/auth/profile
PATCH /api/auth/profile
```

Người dùng đã xác thực đọc/cập nhật các trường hồ sơ không nhạy cảm của chính mình: `firstName`, `lastName`, `companyName`, `phone`, `locale`. Các trường null được giữ nguyên; email, mật khẩu, vai trò, trạng thái hoạt động và tổ chức **không** chỉnh sửa được ở đây. Cả hai đều trả về hồ sơ `{ email, emailConfirmed, firstName, lastName, companyName, phone, locale }`.

### Các phiên (tự phục vụ) {#sessions-self-service}

```
GET    /api/auth/sessions
DELETE /api/auth/sessions/{sessionId}
POST   /api/auth/sessions/revoke-others
```

Liệt kê và kết thúc các phiên SSO của chính người dùng đã xác thực. Những endpoint này cần phiên phía máy chủ, vốn là tùy chọn bật thêm: gọi `AddAuthagonalServerSideSessions(configuration)` sau `AddAuthagonal` (Azure Table Storage, đọc `Storage:ConnectionString` hoặc `Storage:TableServiceUri`), hoặc đăng ký `ITicketStore` và `IUserSessionRegistry` của riêng bạn. Khi không có registry, `GET` trả về danh sách rỗng, `revoke-others` trả về `{ "revoked": 0 }` và `DELETE` trả về `404 not_supported`. Các route `DELETE` và `POST` yêu cầu một request cùng origin.

`GET` trả về các phiên, hoạt động mới nhất trước:

```json
{
  "sessions": [
    {
      "sessionId": "...",
      "current": true,
      "createdAt": "2026-10-01T02:11:40+00:00",
      "lastSeenAt": "2026-10-04T05:30:12+00:00",
      "expiresAt": "2026-10-08T02:11:40+00:00",
      "ip": "203.0.113.7",
      "userAgent": "Mozilla/5.0 ..."
    }
  ]
}
```

`DELETE` kết thúc một phiên và trả về `{ "revoked": 1 }`, hoặc `404 session_not_found`. `POST /revoke-others` kết thúc mọi phiên trừ phiên của bên gọi và trả về `{ "revoked": <count> }`. Cả hai cũng thông báo cho các relying party của từng phiên bị kết thúc (đăng xuất back-channel và front-channel) và thu hồi các grant gắn với phiên đó, nên refresh token lưu trên thiết bị đó sẽ ngừng hoạt động. Trang tài khoản trong giao diện đăng nhập hiển thị danh sách này khi có một registry được đăng ký.

### Kiểm tra SSO {#sso-check}

```
GET /api/auth/sso-check?email=user@acme.com
```

Kiểm tra xem tên miền email có yêu cầu SSO hay không:

```json
{
  "ssoRequired": true,
  "providerType": "saml",
  "connectionId": "acme-azure",
  "redirectUrl": "/saml/acme-azure/login"
}
```

Nếu không yêu cầu SSO:

```json
{
  "ssoRequired": false
}
```

### Chính sách mật khẩu {#password-policy}

```
GET /api/auth/password-policy
```

Trả về các yêu cầu mật khẩu của máy chủ (cấu hình qua `PasswordPolicy` trong phần thiết lập):

```json
{
  "rules": [
    { "rule": "minLength", "value": 8, "label": "At least 8 characters" },
    { "rule": "uppercase", "value": null, "label": "Uppercase letter" },
    { "rule": "lowercase", "value": null, "label": "Lowercase letter" },
    { "rule": "digit", "value": null, "label": "Number" },
    { "rule": "specialChar", "value": null, "label": "Special character" }
  ]
}
```

Giao diện đăng nhập mặc định gọi endpoint này trên trang đặt lại mật khẩu để hiển thị các yêu cầu một cách động.

## Yêu cầu mật khẩu mặc định {#default-password-requirements}

Với cấu hình mặc định, mật khẩu phải đáp ứng tất cả các điều kiện sau:

- Ít nhất 8 ký tự
- Ít nhất một chữ hoa
- Ít nhất một chữ thường
- Ít nhất một chữ số
- Ít nhất một ký tự không phải chữ hoặc số
- Ít nhất 2 ký tự khác nhau

Có thể tùy chỉnh các yêu cầu này qua phần cấu hình `PasswordPolicy`, xem [Cấu hình](configuration).

## Endpoint MFA {#mfa-endpoints}

### Xác minh MFA {#mfa-verify}

```
POST /api/auth/mfa/verify
Content-Type: application/json

{
  "challengeId": "a1b2c3...",
  "method": "totp",
  "code": "123456"
}
```

Xác minh một thử thách MFA. Khi thành công, đặt auth cookie và trả về thông tin người dùng.

**Phương thức:**

| `method` | Trường bắt buộc | Mô tả |
|---|---|---|
| `totp` | `code` (6 chữ số) | Mật khẩu dùng một lần theo thời gian từ ứng dụng xác thực |
| `webauthn` | `assertion` (chuỗi JSON) | Phản hồi assertion WebAuthn từ `navigator.credentials.get()` |
| `recovery` | `code` (`XXXX-XXXX`) | Mã khôi phục dùng một lần (bị tiêu thụ khi sử dụng) |

**Cơ chế thử lại:** một mã sai **không** làm mất thử thách, mã được kiểm tra trước và thử thách chỉ bị tiêu thụ khi thành công, nên người dùng có thể thử lại cùng `challengeId` sau khi gõ nhầm một chữ số (`401 invalid_code` / `assertion_failed`). Mỗi thử thách chịu được **5 lần thử thất bại**; lần thất bại thứ 5 tiêu thụ thử thách và trả về `401 too_many_attempts`, buộc phải đăng nhập lại từ đầu (điều này giới hạn việc dò TOTP ở 5 lần đoán mỗi thử thách). Thử thách cũng hết hạn (mặc định 5 phút, `Auth:MfaChallengeExpiryMinutes`); một `challengeId` đã hết hạn, không tồn tại, hoặc đã bị tiêu thụ sẽ trả về `invalid_challenge`. Mã TOTP còn được bảo vệ chống phát lại, một mã thuộc bước thời gian đã được dùng sẽ bị từ chối.

### Trạng thái MFA {#mfa-status}

```
GET /api/auth/mfa/status
```

Trả về các phương thức MFA mà người dùng đã đăng ký. Yêu cầu xác thực bằng cookie hoặc header `X-MFA-Setup-Token`.

```json
{
  "enabled": true,
  "offered": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

`offered` là `false` khi `MfaPolicy` của mọi client đều là `Disabled`, tức là tenant đã tắt MFA, để giao diện thiết lập có thể tự ẩn. Các mục mã khôi phục còn mang thêm `isConsumed`.

### Thiết lập TOTP {#totp-setup}

```
POST /api/auth/mfa/totp/setup
→ { "setupToken": "...", "qrCodeDataUri": "data:image/png;base64,...", "manualKey": "BASE32..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
→ { "success": true }
```

### Thiết lập WebAuthn / passkey {#webauthn--passkey-setup}

```
POST /api/auth/mfa/webauthn/setup
→ { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
→ { "success": true, "credentialId": "..." }
```

Việc đăng ký passkey yêu cầu **đã có một thông tin xác thực TOTP được xác nhận trước** (`400 totp_required_first`), passkey là một tiện ích theo từng thiết bị được xây trên một yếu tố nền tảng có thể mang theo, nên một tài khoản không bao giờ rơi vào tình trạng chỉ có passkey và bị khóa vào một thiết bị. Người dùng có tên miền email được định tuyến qua SSO không thể đăng ký passkey cục bộ (`400 sso_managed`), vì như vậy sẽ vượt qua IdP của tenant. Một credential ID đã được đăng ký cho **bất kỳ** tài khoản nào, kể cả chính tài khoản đang đăng ký, sẽ bị từ chối với `409 credential_already_registered`, vì một bản trùng lặp sẽ khởi động lại bộ đếm chữ ký của thông tin xác thực đó và khiến hai bản ghi dùng chung một mục tra cứu.

### Mã khôi phục {#recovery-codes}

```
POST /api/auth/mfa/recovery/generate
→ { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

Sinh 10 mã khôi phục dùng một lần. Yêu cầu đã đăng ký ít nhất một phương thức chính (TOTP hoặc WebAuthn). Sinh lại sẽ thay thế toàn bộ mã khôi phục hiện có.

### Gỡ thông tin xác thực MFA {#remove-mfa-credential}

```
DELETE /api/auth/mfa/credentials/{credentialId}
→ { "success": true }
```

Gỡ một thông tin xác thực MFA cụ thể. Nếu phương thức chính cuối cùng bị gỡ, MFA sẽ bị tắt cho người dùng. Yêu cầu một phiên cookie thật, setup token sẽ bị từ chối với `403 session_required` (setup token chỉ tồn tại để thêm yếu tố đầu tiên, không bao giờ để hạ cấp MFA).

### Đăng nhập bằng passkey không mật khẩu {#passwordless-passkey-login}

```
POST /api/auth/mfa/passwordless/begin
→ { "challengeId": "...", "options": { /* PublicKeyCredentialRequestOptions */ } }

POST /api/auth/mfa/passwordless/complete
{ "challengeId": "...", "assertion": "..." }
→ { "userId": "...", "email": "...", "name": "..." }
```

Đăng nhập bằng thông tin xác thực có thể khám phá (passkey lưu trên thiết bị) mà không cần ngữ cảnh người dùng từ trước: `begin` phát hành một thử thách assertion với danh sách `allowCredentials` rỗng, và `complete` phân giải người dùng **từ** passkey được chọn, xác minh assertion, rồi đăng nhập cho họ (phiên mang dấu MFA, vì passkey là xác thực mạnh chống phishing). Vì chưa có người dùng nào được xác định trước nghi thức này, bước 6 của WebAuthn §7.2 khiến user handle của authenticator trở thành bắt buộc ở đây: một assertion không có user handle bị từ chối với `401 user_handle_required`, và một assertion nêu một tài khoản khác với chủ sở hữu của thông tin xác thực bị từ chối với `401 credential_not_found`. Nếu tên miền email của người dùng được phân giải định tuyến qua SSO, việc đăng nhập bị từ chối với `409 sso_required` + `redirectUrl` để một passkey cục bộ không thể lách qua một IdP bắt buộc.

## Ủy quyền thiết bị (RFC 8628) {#device-authorization-rfc-8628}

### Yêu cầu device code {#request-device-code}

```
POST /connect/deviceauthorization
Content-Type: application/x-www-form-urlencoded

client_id=my-cli&scope=openid+profile
```

Trả về một device code, một user code, và một verification URI:

```json
{
  "device_code": "abc123...",
  "user_code": "ABCD-EFGH",
  "verification_uri": "https://auth.example.com/device",
  "verification_uri_complete": "https://auth.example.com/device?user_code=ABCD-EFGH",
  "expires_in": 300,
  "interval": 5
}
```

`expires_in` lấy từ `DeviceCodeLifetimeSeconds` của client (mặc định 300). Thiết bị hiển thị `verification_uri` và `user_code` cho người dùng, rồi thăm dò token endpoint với `device_code`, không nhanh hơn mỗi `interval` giây một lần, nếu không token endpoint sẽ trả lời `slow_down` (RFC 8628 §3.5). Khi người dùng chưa chấp thuận, token endpoint trả về `authorization_pending`. Người dùng truy cập verification URI, đăng nhập, và nhập user code để chấp thuận.

### Hiển thị request trước khi chấp thuận {#show-the-request-before-approving}

```
GET /api/auth/device/info?user_code=ABCD-EFGH
```

Yêu cầu xác thực bằng cookie. Mô tả những gì mã sẽ cấp, để màn hình chấp thuận có thể cho người dùng thấy ứng dụng nào đang yêu cầu trước khi họ chấp thuận (một device flow do kẻ tấn công khởi tạo và được chấp thuận qua một lời nhắc mơ hồ chính là kiểu chấp thuận bất hợp pháp mà RFC 8628 §5.4 cảnh báo):

```json
{
  "clientId": "my-cli",
  "clientName": "My CLI",
  "clientUri": "https://example.com",
  "logoUri": null,
  "scopes": ["openid", "profile"]
}
```

`scopes` là những gì thực sự sẽ được cấp, sau phép kiểm tra vai trò theo từng người dùng trên các scope giới hạn theo vai trò, không phải request thô. Lỗi: `401 not_authenticated`, `400 user_code_required`, `400 invalid_user_code` (không tồn tại, đã bị tiêu thụ hoặc đã hết hạn), `400 expired`. Nó dùng chung bộ đếm giới hạn tần suất với việc chấp thuận (bên dưới).

### Chấp thuận thiết bị {#approve-device}

```
POST /api/auth/device/approve
Content-Type: application/x-www-form-urlencoded

user_code=ABCD-EFGH&scopes=openid+profile
```

Yêu cầu xác thực bằng cookie và một request cùng origin. `scopes` là tùy chọn (phân cách bằng dấu cách): nó chỉ có thể thu hẹp những gì người dùng được hưởng, không bao giờ mở rộng, và bỏ trống nó sẽ cấp mọi thứ được hưởng. Chấp thuận device code cho người dùng hiện tại và trả về `200 { "approved": true }`. Sau đó thiết bị có thể đổi device code lấy token qua token endpoint bằng loại grant `urn:ietf:params:oauth:grant-type:device_code`.

Mã được gửi lên được chuẩn hóa theo RFC 8628 §6.1 trước khi tra cứu: nó được chuyển thành chữ hoa và mọi ký tự nằm ngoài bảng chữ cái 31 ký tự của mã đều bị loại bỏ. `ABCD-EFGH`, `abcd-efgh`, `ABCDEFGH`, `ABCD EFGH` và một bản sao chép-dán đã biến dấu gạch nối thành dấu gạch ngang dài đều là cùng một mã. Dấu gạch nối tồn tại chỉ để mã dễ đọc to hơn.

| Trạng thái | `error` | Ý nghĩa |
|---|---|---|
| 400 | `user_code_required`, `invalid_user_code`, `expired` | Như với `info` |
| 400 | `invalid_scope` | `scopes` được cung cấp nhưng không có scope nào trong đó là scope người dùng được hưởng |
| 403 | `access_denied` | Người dùng không được hưởng scope nào trong số được yêu cầu (`Scope.AllowedRoles`) |
| 403 | `mfa_enrolment_required` | Chính sách MFA hiệu lực của client là `Required` và người dùng chưa có yếu tố thứ hai; hãy đăng ký, rồi chấp thuận lại |

Việc nhập mã bị giới hạn ở mười lần thử mỗi phút cho mỗi chủ thể (RFC 8628 §5.1), dùng chung giữa `info`, `approve` và `deny`; lần thứ mười một trả về `429`. Bộ đếm đó tính theo từng node với bộ giới hạn tần suất trong tiến trình mặc định, nên một bản triển khai nhiều replica cũng nên áp giới hạn ở tầng biên.

### Từ chối thiết bị {#deny-device}

```
POST /api/auth/device/deny
Content-Type: application/x-www-form-urlencoded

user_code=ABCD-EFGH
```

Yêu cầu xác thực bằng cookie và một request cùng origin. Ghi nhận việc người dùng từ chối và trả về `200 { "success": true }`. Lần thăm dò token endpoint tiếp theo của thiết bị nhận `access_denied` (RFC 8628 §3.5) thay vì `authorization_pending` cho tới khi mã hết hạn. Cùng các lỗi và bộ đếm giới hạn tần suất như `info`.

## Token introspection (RFC 7662) {#token-introspection-rfc-7662}

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

token=eyJhbGci...
```

Hoặc với thông tin xác thực mã hóa trong form:

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded

token=eyJhbGci...&client_id=my-app&client_secret=secret
```

Trả về metadata của token:

```json
{
  "active": true,
  "sub": "user-id",
  "client_id": "my-app",
  "scope": "openid profile",
  "iss": "https://auth.example.com",
  "exp": 1234567890,
  "iat": 1234567890,
  "token_type": "Bearer"
}
```

Token không còn hiệu lực hoặc không hợp lệ trả về `{ "active": false }`. Hỗ trợ cả access token JWT lẫn refresh token mờ.

## Endpoint chấp thuận {#consent-endpoints}

### Thông tin chấp thuận {#consent-info}

```
GET /consent/info?client_id=my-app
```

Yêu cầu xác thực bằng cookie. Trả về thông tin chi tiết của client và các scope được yêu cầu cho trang chấp thuận. Các scope không được lấy từ query string: chúng là đề nghị mà authorize endpoint đã ghi lại cho người dùng và client này (sau khi lọc theo quyền hưởng của vai trò), nên một liên kết được chế tác không thể đặt tên của một client đáng tin cậy lên trên một danh sách quyền do bên gọi tự chọn.

```json
{
  "clientId": "my-app",
  "clientName": "My Application",
  "description": null,
  "clientUri": null,
  "logoUri": null,
  "scopes": ["openid", "profile", "email"],
  "scopeDetails": [
    { "name": "openid", "displayName": null, "description": null, "emphasize": false, "required": false, "group": null },
    { "name": "profile", "displayName": null, "description": null, "emphasize": false, "required": false, "group": null },
    { "name": "email", "displayName": null, "description": null, "emphasize": false, "required": false, "group": null }
  ]
}
```

`scopeDetails` song song với `scopes` (cùng thứ tự, mỗi scope một mục), nên một ứng dụng đăng nhập chỉ đọc `scopes` vẫn hoạt động bình thường. Mỗi mục mang phần trình bày đã đăng ký cho scope đó:

| Trường | Ý nghĩa |
|---|---|
| `name` | Tên scope, như trong `scopes`. |
| `displayName` | Tên hiển thị đã đăng ký, hoặc `null` khi scope chưa được đăng ký. |
| `description` | Mô tả đã đăng ký, hoặc `null`. |
| `emphasize` | `true` khi scope được đăng ký là có hệ quả lớn, để màn hình có thể làm nổi bật nó. Mặc định là `false`. |
| `required` | `true` khi scope được đăng ký là không thể từ chối: màn hình hiển thị nó ở trạng thái đã chọn và bị khóa. Mặc định là `false`. |
| `group` | Tiêu đề nhóm để xếp scope vào, hoặc `null` để hiển thị riêng. |

Một scope chưa được đăng ký cho ra `null` với `displayName`, `description` và `group` và `false` với hai cờ, và ứng dụng đăng nhập dùng cách diễn đạt của chính nó làm dự phòng. Xem [Scope](scopes) để biết cách đăng ký cách diễn đạt.

Lỗi:

| Trạng thái | Body | Khi nào |
|---|---|---|
| `401` | không có | Không có người dùng nào đang đăng nhập. |
| `404` | `{ "error": "client_not_found" }` | `client_id` không tồn tại. |
| `400` | `{ "error": "no_pending_consent_request" }` | Không có đề nghị chấp thuận nào còn hiệu lực cho người dùng và client này (chưa từng được ghi lại, hoặc đã hết hạn). |

### Gửi quyết định chấp thuận {#submit-consent}

```
POST /consent
Content-Type: application/json

{
  "clientId": "my-app",
  "decision": "allow",
  "scopes": ["openid", "profile", "email"],
  "returnUrl": "/connect/authorize?..."
}
```

Ghi nhận quyết định chấp thuận của người dùng (yêu cầu xác thực bằng cookie) và trả về `{ "redirect": "..." }` để SPA điều hướng tới. Khi cho phép, các scope được cấp sẽ được lưu (lọc theo `AllowedScopes` của client, một body bị sửa đổi không thể ghi lại các scope mà client không được phép yêu cầu) và redirect trỏ về luồng authorize. Với `"decision": "deny"`, redirect trỏ tới `redirect_uri` của client kèm lỗi `access_denied`.

### Liệt kê grant {#list-grants}

```
GET /consent/grants
```

Trả về mọi ứng dụng mà người dùng đã cấp quyền:

```json
[
  {
    "clientId": "my-app",
    "clientName": "My Application",
    "scopes": ["openid", "profile", "email"],
    "consentedAt": "2026-04-09T12:00:00Z"
  }
]
```

### Thu hồi grant {#revoke-grant}

```
DELETE /consent/grants/{clientId}
```

Thu hồi sự chấp thuận cho một ứng dụng cụ thể. Người dùng sẽ được yêu cầu chấp thuận lại ở lần đăng nhập tiếp theo.

## Discovery và khóa ký (JWKS) {#discovery-and-signing-keys-jwks}

Cả hai đều công khai và không cần xác thực. Chúng là thứ resource server dùng để kiểm tra các token mà máy chủ này phát hành.

```
GET /.well-known/openid-configuration
GET /.well-known/oauth-authorization-server
GET /.well-known/openid-configuration/jwks
```

- Hai đường dẫn metadata trả về cùng một tài liệu discovery; `jwks_uri` của nó là `{issuer}/.well-known/openid-configuration/jwks`.
- JWKS liệt kê mọi khóa ký chưa hết hạn (`kty`, `use`, `kid`, `alg`, và `crv`/`x`/`y` với các khóa EC). Việc xoay vòng công bố khóa tiếp theo trước nhiều ngày, nên một bản lưu đệm không bao giờ thiếu khóa đã dùng để ký một token.
- Các phản hồi mang `Cache-Control: public, max-age=3600`.
- Chỉ ký bằng ES256; discovery công bố `id_token_signing_alg_values_supported: ["ES256"]`.
- Issuer lấy từ `ITenantContext`, và các khóa lấy từ `IKeyManager`, nên một host đa tenant có key manager theo từng tenant sẽ phục vụ khóa theo từng tenant.

## Hành vi của authorization endpoint {#authorization-endpoint-behaviour}

`GET /connect/authorize` là điểm vào của luồng authorization code. Có hai hành vi quan trọng với bất kỳ ai xây dựng client hoặc giao diện đăng nhập dựa trên nó.

### Issuer trong phản hồi (RFC 9207) {#issuer-in-the-response-rfc-9207}

Mọi lần chuyển hướng về `redirect_uri` của client đều mang tham số truy vấn `iss` chứa issuer, khi thành công (cùng với `code` và `state`) và khi lỗi (cùng với `error`, `error_description` và `state`). Điều tương tự áp dụng cho lần chuyển hướng lỗi khi người dùng từ chối chấp thuận tại `/consent`. Tài liệu discovery công bố điều này bằng `authorization_response_iss_parameter_supported: true`. Một client làm việc với nhiều authorization server nên so sánh `iss` với issuer mà nó đã bắt đầu luồng, đó là cách chặn tấn công mix-up; các client bỏ qua tham số này không bị ảnh hưởng. Các lỗi phát sinh trước khi biết được một `redirect_uri` đáng tin cậy (`client_id` không tồn tại, một redirect URI chưa đăng ký) được trả về dưới dạng body lỗi JSON, không phải chuyển hướng, nên các lỗi đó không có `iss`.

### `prompt` và `max_age` {#prompt-and-max_age}

| Request | Hành vi |
|---|---|
| `prompt=login` | Phiên hiện có bị đăng xuất và người dùng được chuyển tới `/login` để xác thực lại. `prompt` bị loại khỏi `returnUrl` để lần đăng nhập mới không bị buộc xác thực lại thành vòng lặp. Với một [request được đẩy](par), prompt đi theo payload đã lưu, và vòng lặp được phá bằng cách yêu cầu `auth_time` của phiên phải bằng hoặc sau thời điểm request được đẩy |
| `prompt=select_account` | Được xử lý như `prompt=login`: máy chủ giữ một phiên cho mỗi trình duyệt, nên việc chọn tài khoản chính là màn hình đăng nhập |
| `prompt=create` | Người dùng chưa xác thực được chuyển tới `/login/register` thay vì biểu mẫu đăng nhập. Một phiên hiện có thì cứ tiếp tục |
| `prompt=consent` | Màn hình chấp thuận được hiển thị ngay cả khi một grant đã lưu đủ đáp ứng request, một lần cho mỗi request (dấu đã đáp ứng chỉ dùng được một lần) |
| `prompt=none` | Không bao giờ hiển thị giao diện nào. Máy chủ trả lời bằng một lần chuyển hướng mang `login_required` (không có phiên), `interaction_required` (cần nâng cấp MFA hoặc đăng ký MFA) hoặc `consent_required` (cần chấp thuận) |
| `max_age=N` | Nếu `auth_time` của phiên cũ hơn `N` giây, hoặc không có, người dùng được xác thực lại đúng như với `prompt=login`. `max_age=0` luôn xác thực lại |

`prompt=none` kết hợp với bất kỳ giá trị nào khác bị từ chối với `invalid_request`, cũng như mọi giá trị nằm ngoài `none`, `login`, `consent`, `select_account` và `create`. Host `Authagonal.Protocol` nhúng được xử lý `prompt=login`, `select_account`, `none` và `max_age` theo cùng cách, nhưng không có giao diện chấp thuận, nên nó trả lời `prompt=consent` bằng `consent_required`.

## Xây dựng giao diện đăng nhập tùy chỉnh {#building-a-custom-login-ui}

SPA mặc định (`login-app/`) là một bản cài đặt của API này. Để xây dựng giao diện của riêng bạn:

1. Phục vụ giao diện của bạn tại các đường dẫn `/login`, `/forgot-password`, `/reset-password`, `/consent`, `/device`
2. Authorize endpoint chuyển hướng người dùng chưa xác thực tới `/login?returnUrl={encoded-authorize-url}`
3. Sau khi đăng nhập thành công (cookie đã được đặt), chuyển hướng người dùng tới `returnUrl`
4. Liên kết đặt lại mật khẩu dùng `{Issuer}/login/reset-password?p={token}` (SPA đăng nhập được gắn dưới `/login`)

Giao diện của bạn phải được phục vụ từ **cùng origin** với API vì:
- Xác thực bằng cookie dùng `SameSite=Lax` + `HttpOnly`
- Authorize endpoint chuyển hướng tới `/login` (đường dẫn tương đối)
- Liên kết đặt lại mật khẩu dùng `{Issuer}/login/reset-password`
