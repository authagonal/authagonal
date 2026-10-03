---
layout: default
title: Xác thực đa yếu tố
locale: vi
---

# Xác thực đa yếu tố (MFA)

Authagonal hỗ trợ xác thực đa yếu tố. Có ba phương thức: TOTP (ứng dụng xác thực), WebAuthn/passkey (khóa phần cứng và sinh trắc học), và mã khôi phục dùng một lần. Passkey cũng có thể được dùng để [đăng nhập không mật khẩu](#passwordless-passkey-login).

Đăng nhập liên kết (SAML/OIDC) cũng được bao phủ: một assertion SAML hoặc OIDC chứng minh yếu tố thứ nhất, không phải yếu tố thứ hai. Người dùng liên kết đã đăng ký MFA sẽ đi qua cùng một thử thách MFA cục bộ như khi đăng nhập bằng mật khẩu, và chính sách `Required` buộc phải đăng ký trước khi bất kỳ phiên nào được cấp. Chỉ khi MFA vừa chưa được đăng ký vừa không bắt buộc thì liên kết mới đứng một mình. Một kết nối có thể từ chối thử thách cục bộ bằng `ChallengeMfaAfterLogin: false` (xem bên dưới).

## Các phương thức được hỗ trợ {#supported-methods}

| Phương thức | Mô tả |
|---|---|
| **TOTP** | Mật khẩu dùng một lần theo thời gian (RFC 6238): 6 chữ số, bước 30 giây, SHA-1, được xác minh với cửa sổ lệch đồng hồ một bước. Hoạt động với mọi ứng dụng xác thực (Google Authenticator, Authy, 1Password, v.v.). Một mã đã được chấp nhận không thể được dùng lại trong khoảng thời gian hiệu lực của nó. |
| **WebAuthn / Passkey** | Khóa bảo mật phần cứng FIDO2, sinh trắc học của nền tảng (Touch ID, Windows Hello), và passkey được đồng bộ. Người dùng có thể đăng ký nhiều passkey, và passkey có thể dùng để đăng nhập không mật khẩu. |
| **Mã khôi phục** | 10 mã dự phòng dùng một lần (10 ký tự từ bảng chữ cái 32 ký tự, hiển thị dạng `XXXXX-XXXXX`) để khôi phục tài khoản khi các phương thức khác không dùng được. Được lưu ở dạng băm và mã hóa khi lưu trữ. |

## Chính sách MFA {#mfa-policy}

Việc thực thi MFA được cấu hình **theo từng client** qua thuộc tính `MfaPolicy` trong `appsettings.json`:

| Giá trị | Hành vi |
|---|---|
| `Disabled` (mặc định) | Không bắt buộc đăng ký; giao diện thiết lập tự phục vụ ẩn MFA khi mọi client đều là `Disabled` |
| `Enabled` | Cho phép đăng ký MFA; không bắt buộc |
| `Required` | Bắt buộc đăng ký với người dùng chưa có MFA |

Người dùng đã đăng ký MFA **luôn nhận thử thách khi đăng nhập, bất kể chính sách của client**. MFA là thuộc tính của người dùng và phiên của họ, không phải của client đang gửi request, nên một request đi qua client `Disabled` không thể được dùng để bỏ qua yếu tố thứ hai của người dùng đã đăng ký.

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "MfaPolicy": "Enabled"
    },
    {
      "ClientId": "admin-portal",
      "MfaPolicy": "Required"
    }
  ]
}
```

Mặc định là `Disabled`, nên các client hiện có không bị ảnh hưởng cho tới khi bạn chủ động bật.

### Ghi đè theo từng người dùng {#per-user-override}

Triển khai `IAuthHook.ResolveMfaPolicyAsync` để ghi đè chính sách của client cho những người dùng cụ thể:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    // Exempt service accounts
    if (email.EndsWith("@service.internal"))
        return Task.FromResult(MfaPolicy.Disabled);

    return Task.FromResult(clientPolicy);
}
```

Chính sách được phân giải quyết định việc đăng ký (được đề xuất hay bị bắt buộc). Nó không miễn thử thách cho người dùng đã đăng ký; người dùng đã đăng ký luôn nhận thử thách.

Xem [Khả năng mở rộng](extensibility) để có tài liệu đầy đủ về hook.

## Flow đăng nhập {#login-flow}

Flow đăng nhập có MFA hoạt động như sau:

1. Người dùng gửi email và mật khẩu tới `POST /api/auth/login`
2. Server xác minh mật khẩu, rồi phân giải chính sách MFA hiệu lực
3. Dựa trên chính sách và trạng thái đăng ký của người dùng:

| Chính sách | Người dùng có MFA? | Kết quả |
|---|---|---|
| Bất kỳ | Có | Trả về `mfaRequired`: người dùng phải xác minh |
| `Disabled` / `Enabled` | Không | Đặt cookie, đăng nhập hoàn tất |
| `Required` | Không | Trả về `mfaSetupRequired`: người dùng phải đăng ký |

### Thử thách MFA {#mfa-challenge}

Khi `mfaRequired` được trả về, response đăng nhập bao gồm một `challengeId`, các `methods` khả dụng của người dùng, và (khi người dùng có passkey) các tùy chọn assertion `webAuthn`. Client chuyển hướng tới trang thử thách MFA, nơi người dùng xác minh bằng một trong các phương thức đã đăng ký qua `POST /api/auth/mfa/verify`:

```json
{
  "challengeId": "...",
  "method": "totp",
  "code": "123456"
}
```

`method` là `totp`, `recovery`, hoặc `webauthn` (WebAuthn gửi một `assertion` thay vì `code`).

Thử thách hết hạn sau 5 phút (cấu hình qua `Auth:MfaChallengeExpiryMinutes`) và được tiêu thụ khi xác minh thành công.

#### Số lần thử cho phép {#retry-budget}

Một mã sai không làm mất thử thách. Endpoint xác minh kiểm tra mã trước và chỉ tiêu thụ thử thách khi thành công, nên một chữ số TOTP gõ nhầm có thể được thử lại đơn giản với cùng `challengeId`. Các lần thử thất bại trả về `invalid_code` (hoặc `assertion_failed` với WebAuthn) với mã 401 và tăng một bộ đếm có giới hạn trên thử thách; lần thử sai thứ năm sẽ tiêu thụ thử thách và trả về `too_many_attempts`, buộc phải đăng nhập lại từ đầu. Điều này áp dụng cho cả ba phương thức.

Số lần thử theo từng thử thách chỉ là lối xử lý nhanh, không phải giới hạn bảo mật, nên có thêm hai lớp chặn áp dụng cho `POST /api/auth/mfa/verify`:

- **Giới hạn tần suất theo người dùng.** Hơn 10 lần thử xác minh mỗi phút cho một người dùng sẽ trả về `too_many_attempts` với mã 429, bất kể dùng `challengeId` nào.
- **Khóa tài khoản dùng chung.** Mọi mã thất bại cũng được tính vào cùng bộ đếm số lần thất bại như bước mật khẩu (`Auth:MaxFailedAttempts`, `Auth:LockoutDurationMinutes`). Khi ngưỡng bị vượt, thử thách bị tiêu thụ và response là `locked_out` (423). Trong khi tài khoản bị khóa, việc xác minh bị từ chối với `locked_out` trước cả khi mã được kiểm tra.

Chỉ những thông tin xác thực đã được xác nhận mới có thể thỏa mãn một lần xác minh; một lần đăng ký đã bắt đầu nhưng chưa bao giờ hoàn tất không được tính là một yếu tố.

Một thử thách không tồn tại, đã hết hạn, hoặc đã bị tiêu thụ sẽ trả về `invalid_challenge`.

### Đăng nhập liên kết {#federated-logins}

Sau một assertion SAML hoặc OIDC thành công, server phân giải cùng chính sách MFA hiệu lực. Người dùng đã đăng ký MFA được chuyển hướng tới trang thử thách MFA được host sẵn (kèm `challengeId`) thay vì nhận phiên; người dùng chưa có MFA dưới chính sách `Required` được chuyển hướng tới trang thiết lập MFA (kèm `setupToken`). Phiên chỉ được đánh dấu là đã xác thực MFA khi việc xác minh hoàn tất.

Thử thách này áp dụng theo từng kết nối: một kết nối SAML hoặc OIDC đặt `ChallengeMfaAfterLogin` là `false` sẽ bỏ qua thử thách cục bộ đối với người dùng đến qua kết nối đó. Mặc định là `true`.

### Bắt buộc đăng ký {#forced-enrollment}

Khi `mfaSetupRequired` được trả về, response bao gồm một `setupToken`. Token này xác thực người dùng với các endpoint thiết lập MFA (qua header `X-MFA-Setup-Token`) để họ có thể đăng ký một phương thức trước khi nhận phiên cookie. Setup token hết hạn sau 15 phút (cấu hình qua `Auth:MfaSetupTokenExpiryMinutes`).

## Đăng ký MFA {#enrolling-mfa}

Người dùng đăng ký MFA qua các endpoint thiết lập tự phục vụ. Các endpoint này yêu cầu hoặc một phiên cookie đã xác thực, hoặc một setup token.

### Thiết lập TOTP {#totp-setup}

1. Gọi `POST /api/auth/mfa/totp/setup`, trả về mã QR (`data:image/png;base64,...`), một `manualKey` (Base32 để nhập thủ công), và setup token
2. Người dùng quét mã QR bằng ứng dụng xác thực
3. Người dùng nhập mã 6 chữ số để xác nhận: `POST /api/auth/mfa/totp/confirm`

Bước xác nhận bị giới hạn giống như bước xác minh: hơn 10 lần thử mỗi phút cho một người dùng sẽ trả về `too_many_attempts` (429), và khi dùng setup token thì mã sai thứ năm sẽ tiêu thụ thử thách thiết lập. Một lần đăng ký chưa được xác nhận hết hạn sau 30 phút (`setup_expired`).

### Thiết lập WebAuthn / Passkey {#webauthn--passkey-setup}

1. Gọi `POST /api/auth/mfa/webauthn/setup`, trả về một `setupToken` và `PublicKeyCredentialCreationOptions`
2. Client gọi `navigator.credentials.create()` với các tùy chọn đó
3. Gửi response attestation tới `POST /api/auth/mfa/webauthn/confirm`

Việc đăng ký passkey yêu cầu trước đó đã có một thông tin xác thực TOTP được xác nhận (`totp_required_first`). Passkey là một tiện ích theo từng thiết bị đặt trên một yếu tố cơ sở di động được, nên mọi tài khoản đều giữ một yếu tố không phụ thuộc thiết bị và chính sách `Required` không thể được thỏa mãn chỉ bằng một passkey.

Người dùng có thể đăng ký nhiều passkey (mỗi thiết bị một passkey). Một credential ID đã được đăng ký (cho bất kỳ tài khoản nào, kể cả chính tài khoản của người đang đăng ký) bị từ chối với `credential_already_registered` (409). Việc đăng ký lại một authenticator đã được đăng ký sẽ tạo ra dòng thông tin xác thực thứ hai dùng chung một credential ID: bộ đếm chữ ký của nó sẽ bắt đầu lại, làm yếu khả năng phát hiện bản sao, và việc xóa bất kỳ dòng nào cũng sẽ xóa mục tra cứu mà cả hai phụ thuộc vào. Mục tra cứu được chiếm bằng một thao tác ghi chèn-nếu-chưa-có, nên hai lần đăng ký cùng một credential ID không thể cùng thành công. Người dùng có tên miền email được định tuyến tới một IdP bên ngoài qua SSO bắt buộc không thể đăng ký passkey cục bộ (`sso_managed`), vì passkey đó sẽ vượt qua IdP và cơ chế thu hồi quyền truy cập của nó.

### Host của relying party {#relying-party-host}

ID và origin của relying party FIDO2 được phân giải theo từng request từ host, nên mỗi hostname của tenant là một relying party riêng. Hãy đặt `Auth:WebAuthnAllowedHosts` thành các hostname bạn phục vụ, để một host nằm ngoài danh sách đó không thể đóng vai relying party. Danh sách rỗng (mặc định) giữ hành vi trước đây thay vì khóa người dùng passkey hiện có khi nâng cấp, và được ghi log là một lỗ hổng ở lần dùng đầu tiên. Đó không phải trạng thái an toàn để duy trì. Đặt thêm `AllowedHosts` trong `appsettings.json`, để bộ lọc host của ASP.NET Core từ chối các header `Host` không được nhận diện trước khi bất kỳ handler nào chạy, là lớp bảo vệ bên ngoài ít tốn kém hơn.

Độc lập với danh sách đó, mọi thông tin xác thực đều ghi lại relying party mà nó được đăng ký và bị từ chối ở bất kỳ nơi nào khác. Đó là phần mà request không thể tác động: nếu không có nó, cả hai quy trình (ceremony) đều dựng kỳ vọng từ chính header `Host` mà chúng đang xác minh, nên origin và `rpIdHash` bị so sánh với một giá trị do bên gọi cung cấp, và một host đứng giữa đường truyền chuyển tiếp `Host` của chính nó sẽ khiến ràng buộc origin, thuộc tính làm cho passkey chống được phishing, xác minh nó thay vì ngăn chặn nó. Các thông tin xác thực được đăng ký trước khi RP ID được ghi lại thì không mang giá trị này và vẫn tiếp tục hoạt động; chúng sẽ có ràng buộc khi được đăng ký lại.

### Mã khôi phục {#recovery-codes}

Gọi `POST /api/auth/mfa/recovery/generate` để tạo 10 mã dùng một lần. Phải đăng ký trước ít nhất một phương thức chính đã được xác nhận (TOTP hoặc WebAuthn) (`primary_method_required`), và lời gọi cần một phiên đã xác thực thực sự: setup token sẽ nhận `session_required` (403).

Mỗi mã gồm 10 ký tự từ bảng chữ cái 32 ký tự, hiển thị thành hai nhóm năm ký tự (`XXXXX-XXXXX`).

Việc tạo lại mã sẽ thay thế mọi mã khôi phục hiện có. Mỗi mã chỉ dùng được một lần; mã đã dùng được đánh dấu là đã tiêu thụ và không còn được chấp nhận.

Mã không bao giờ được lưu ở dạng văn bản thuần: mỗi mã được băm, và giá trị băm còn được mã hóa khi lưu trữ bằng secret provider của tenant, nên một bản dump storage chỉ cho ra bản mã thay vì một giá trị băm có thể bị dò ngoại tuyến.

## Đăng nhập không mật khẩu bằng passkey {#passwordless-passkey-login}

Passkey không chỉ là yếu tố thứ hai: người dùng đã đăng ký passkey có thể đăng nhập mà không cần mật khẩu.

1. `POST /api/auth/mfa/passwordless/begin` trả về một `challengeId` và `options` assertion cho các discoverable credential, để authenticator đề xuất bất kỳ passkey thường trú nào cho trang web
2. Client gọi `navigator.credentials.get()` với các tùy chọn đó
3. `POST /api/auth/mfa/passwordless/complete` với `{ challengeId, assertion }`: server phân giải người dùng từ chính passkey và đăng nhập cho họ

Trang đăng nhập được host sẵn nối tính năng này vào trường email qua conditional mediation (tự động điền passkey): khi trình duyệt hỗ trợ, một passkey khả dụng được đề xuất như một gợi ý tự động điền mà không cần thêm giao diện nào.

Passkey là xác thực mạnh chống phishing, nên phiên tạo ra mang dấu MFA và không bị thử thách lại. Nếu tên miền email của người dùng được định tuyến tới một IdP bên ngoài qua SSO bắt buộc, đăng nhập không mật khẩu bị từ chối với response 409 `sso_required` kèm URL chuyển hướng SSO, nên passkey cục bộ không thể đi vòng qua IdP.

## Quản lý MFA {#managing-mfa}

### Người dùng tự phục vụ {#user-self-service}

- `GET /api/auth/mfa/status`, xem các phương thức đã đăng ký (cũng cho biết MFA có được client nào đề xuất hay không)
- `DELETE /api/auth/mfa/credentials/{id}`, xóa một thông tin xác thực cụ thể

Việc xóa một thông tin xác thực yêu cầu một phiên đã xác thực thực sự; setup token chỉ cho phép thêm yếu tố đầu tiên và sẽ nhận `session_required` ở đây, nên một setup token bị lộ không thể hạ cấp MFA của người dùng.

Nếu phương thức chính cuối cùng bị xóa, MFA sẽ bị tắt cho người dùng đó.

### Admin API {#admin-api}

Quản trị viên có thể quản lý MFA cho bất kỳ người dùng nào qua [Admin API](admin-api):

- `GET /api/v1/profile/{userId}/mfa`, xem trạng thái MFA của người dùng
- `DELETE /api/v1/profile/{userId}/mfa`, đặt lại toàn bộ MFA (cho người dùng bị khóa ngoài)
- `DELETE /api/v1/profile/{userId}/mfa/{id}`, xóa một thông tin xác thực cụ thể

### Hook audit {#audit-hooks}

Triển khai `IAuthHook.OnMfaVerifiedAsync` để ghi log các sự kiện MFA:

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

Toàn bộ vòng đời MFA đều có hook: `OnMfaVerifyFailedAsync` (một lần thử xác minh thất bại), `OnMfaEnrolledAsync` (một phương thức được xác nhận), `OnMfaCredentialRemovedAsync` (một thông tin xác thực bị xóa, kèm cờ cho biết việc đó có tắt MFA hay không), và `OnRecoveryCodesRegeneratedAsync`.

## Giao diện đăng nhập tùy chỉnh {#custom-login-ui}

Nếu bạn xây dựng giao diện đăng nhập tùy chỉnh, hãy xử lý các response sau từ `POST /api/auth/login`:

1. **Đăng nhập bình thường**: `{ userId, email, name }` kèm cookie được đặt. Chuyển hướng tới `returnUrl`.
2. **Yêu cầu MFA**: `{ mfaRequired: true, challengeId, methods, webAuthn? }`. Hiển thị biểu mẫu thử thách MFA.
3. **Yêu cầu thiết lập MFA**: `{ mfaSetupRequired: true, setupToken }`. Hiển thị flow đăng ký MFA.

Khi xử lý lỗi của `POST /api/auth/mfa/verify`: `invalid_code` và `assertion_failed` có thể thử lại với cùng `challengeId` (trong giới hạn số lần thử); `too_many_attempts` và `invalid_challenge` là lỗi cuối cùng, nên hãy đưa người dùng trở lại biểu mẫu đăng nhập.

Xem [Auth API](auth-api) để có tham chiếu endpoint đầy đủ.
