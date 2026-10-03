---
layout: default
title: Khả năng mở rộng
locale: vi
---

# Khả năng mở rộng

Authagonal có thể được host như một thư viện trong dự án ASP.NET Core của riêng bạn, với toàn quyền kiểm soát các bản cài đặt service.

## Các phương thức mở rộng {#extension-methods}

Ba phương thức ghép Authagonal vào bất kỳ ứng dụng ASP.NET Core nào:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

### Host đa tenant {#multi-tenant-hosting}

Với các bản triển khai đa tenant, hãy dùng `AddAuthagonalCore()` thay thế. Phương thức này đăng ký endpoint, middleware và các service lõi nhưng bỏ qua phần lưu trữ và các service chạy nền; bạn tự cung cấp chúng theo từng tenant. Việc quản lý khóa ký mặc định dùng singleton `ProtocolKeyManager` của `Authagonal.Protocol`, và host nào đăng ký `IKeyManager` của riêng mình trước `AddAuthagonalCore()` sẽ giữ được bản đó:

```csharp
builder.Services.AddScoped<ITenantContext, MyTenantContext>();
builder.Services.AddScoped<IKeyManager, MyPerTenantKeyManager>();
builder.Services.AddAuthagonalCore(builder.Configuration);
```

`IKeyManager` và các interface store (`IClientStore`, `IScimTokenStore`, v.v.) được phân giải từ `HttpContext.RequestServices` lúc xử lý request, nên các đăng ký scoped hoạt động đúng cho việc cô lập theo từng tenant.

### Nhúng riêng `Authagonal.Protocol` {#embedding-authagonalprotocol-alone}

Một host chỉ muốn bề mặt giao thức OIDC (xác thực riêng, pipeline riêng, các endpoint `/connect/*` cắm vào là chạy) sẽ gọi `AddAuthagonalProtocol()` + `MapAuthagonalProtocolEndpoints()` mà không cần bất kỳ phần nào của `Authagonal.Server`.

`/connect/authorize`, `/connect/token`, `/connect/userinfo` và `/connect/par` cũng từ chối http không mã hóa trong cách dùng này, theo RFC 6749 §3.1/§3.2. Vì gói được map vào một pipeline mà nó không sở hữu, yêu cầu này đi kèm các endpoint dưới dạng filter thay vì middleware, nên nó vẫn đứng vững dù bạn ghép pipeline thế nào và dù bạn map toàn bộ bề mặt hay từng endpoint một. Có hai hệ quả nên biết trước khi nâng cấp:

- **Đứng sau một proxy kết thúc TLS, hãy gọi `UseForwardedHeaders` với proxy đã được khai báo.** Filter đọc scheme sau bước định tuyến, nên một `X-Forwarded-Proto: https` được chuyển tiếp sẽ thỏa mãn nó. Không có middleware đó, host của bạn thấy kết nối không mã hóa, điều này cũng có nghĩa là cookie của bạn không được đánh dấu `Secure` và các URL tuyệt đối được sinh ra đều sai, nên đây là điều đáng sửa chứ không nên tìm cách lách. Hãy điền `KnownProxies` / `KnownNetworks` khi đăng ký nó: ASP.NET Core hiểu một tập tin cậy rỗng là "mọi bên gọi đều là proxy được tin cậy", tức là trao quyền quyết định scheme cho bất kỳ ai tới được host của bạn. Nếu body của lần từ chối nhắc tới một `X-Forwarded-Proto` chưa được áp dụng, thì đây chính là middleware nó đang yêu cầu.
- **Host thực sự phục vụ bề mặt giao thức qua http thì bật tùy chọn cho phép**, giống cách máy chủ làm:

```csharp
builder.Services.AddAuthagonalProtocol(o =>
{
    o.AuthenticationScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    o.AllowInsecureHttp = builder.Environment.IsDevelopment();   // never in production
});
```

Discovery và JWKS cố ý không bị chặn: chúng là metadata công khai, và một client không đọc được chúng thì ngay từ đầu cũng không thể biết rằng mình cần https.

Khi bạn dùng `AddAuthagonal()` (máy chủ đầy đủ), bạn không cần đặt riêng tùy chọn này: `Auth:AllowInsecureHttp` được tự động chuyển vào các tùy chọn giao thức, nên một công tắc duy nhất điều khiển toàn bộ bề mặt.

## Ghi đè service {#overriding-services}

Đăng ký các bản cài đặt tùy chỉnh của bạn **trước khi** gọi `AddAuthagonal()`. Authagonal dùng `TryAdd` bên trong, nên các đăng ký của bạn được ưu tiên:

```csharp
// Custom implementations, registered first so they won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

`IAuthHook` là trường hợp đặc biệt: nó là một pipeline cho phép đăng ký nhiều lần. Hãy đăng ký bao nhiêu hook tùy thích (với bất kỳ lifetime nào, kể cả `AddScoped`) và tất cả đều chạy theo thứ tự đăng ký. `NullAuthHook` không làm gì chỉ được thêm vào khi chưa có hook nào được đăng ký vào lúc `AddAuthagonal()` / `AddAuthagonalCore()` chạy, nên hãy luôn đăng ký hook của bạn trước.

### Các điểm mở rộng {#extensibility-points}

| Interface | Mặc định | Mục đích |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (không làm gì, chỉ được thêm khi chưa có hook nào được đăng ký) | Hook vòng đời cho các sự kiện xác thực: ghi audit log, kiểm tra tùy chỉnh, webhook. Có thể đăng ký nhiều hook; tất cả chạy theo thứ tự |
| `IEmailService` | `NullEmailService` (không làm gì), hoặc trình gửi Resend dựng sẵn khi `Email:ResendApiKey` được cấu hình | Gửi email xác minh, đặt lại mật khẩu, và thông báo tài khoản đã tồn tại |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` (scoped) | Cấp phát người dùng vào các ứng dụng downstream |
| `ISecretProvider` | `PlaintextSecretProvider`, hoặc `KeyVaultSecretProvider` dựng sẵn khi `SecretProvider:VaultUri` được cấu hình | Lưu trữ secret có thể giải mã ngược (Key Vault, AWS Secrets Manager, Vault Transit, v.v.) |
| `ITenantContext` | `DefaultTenantContext` (đọc từ `IConfiguration`) | Phân giải tenant cho các bản triển khai đa tenant |
| `IKeyManager` | `ProtocolKeyManager` (singleton, từ `Authagonal.Protocol`) | Quản lý khóa ký; ghi đè để cô lập khóa theo từng tenant |
| `IProvisioningAppProvider` | `ConfigProvisioningAppProvider` (scoped) | Phân giải các ứng dụng cấp phát hiện có; ghi đè để phân giải ứng dụng động hoặc theo từng tenant |
| `IAuditLogger` | `NullAuditLogger` (không làm gì) | Vết audit cho các thay đổi cấu hình và các sự kiện liên quan tới bảo mật |
| `IClientCredentialsClaimsTransformer` | `NullClientCredentialsClaimsTransformer` (singleton, từ `Authagonal.Protocol`) | Kiểm tra ngữ cảnh do bên gọi cung cấp khi phát hành `client_credentials` và ép claim lên token, hoặc từ chối phát hành |
| `ITokenExchangeSubjectTransformer` | `NullTokenExchangeSubjectTransformer` (singleton, từ `Authagonal.Protocol`) | Ánh xạ chủ thể cho token exchange RFC 8693; xem [Xác thực cho agent](agentic-auth) |
| `ITurnstileKeyProvider` | `OptionsTurnstileKeyProvider` (scoped, đọc `TurnstileOptions`) | Sitekey và secret Turnstile nào áp dụng cho request này |
| `IInteractiveCorsOriginPolicy` | `DenyInteractiveCorsOriginPolicy` (singleton, từ chối mọi origin) | Các origin được phép gọi cross-origin kèm thông tin xác thực tới `/api/auth/*` |

Ba điểm nối khác nằm ở **cấp store** thay vì trong DI: `IFieldCipher`, `IIndexTokenizer`, và `IChangeWriter` (đều nằm trong `Authagonal.Core.Services`). Các storage provider nhận chúng dưới dạng tham số constructor tùy chọn; xem các mục tương ứng bên dưới.

## IAuthHook {#iauthhook}

Interface `IAuthHook` cung cấp các hook vào vòng đời xác thực. Các phương thức nằm trên đường xử lý chính (xác thực, tạo người dùng, phát hành token) có thể ném ngoại lệ để hủy thao tác; các phương thức mới hơn là thông báo sau khi sự việc đã xảy ra. Có thể đăng ký nhiều bản cài đặt `IAuthHook` và tất cả đều chạy theo thứ tự đăng ký.

```csharp
public interface IAuthHook
{
    // Core lifecycle: implement these
    Task OnUserAuthenticatedAsync(string userId, string email, string method,
        string? clientId = null, CancellationToken ct = default);
    Task OnUserCreatedAsync(string userId, string email, string createdVia,
        CancellationToken ct = default);
    Task OnLoginFailedAsync(string email, string reason,
        CancellationToken ct = default);
    Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType,
        CancellationToken ct = default);
    Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email,
        MfaPolicy clientPolicy, string clientId, CancellationToken ct = default);
    Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default);
    Task OnUserUpdatedAsync(string userId, string email, string updatedVia,
        CancellationToken ct = default);
    Task OnUserDeletedAsync(string userId, string email, string deletedVia,
        CancellationToken ct = default);

    // Additive notifications: default no-op implementations, so existing
    // hooks keep compiling as the interface grows
    Task OnMfaVerifyFailedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnEmailConfirmedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaEnrolledAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaCredentialRemovedAsync(string userId, string email, string mfaMethod,
        bool mfaDisabled, CancellationToken ct = default) => Task.CompletedTask;
    Task OnRecoveryCodesRegeneratedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnPasswordChangedAsync(string userId, string email, string changedVia,
        CancellationToken ct = default) => Task.CompletedTask;

    // Token gate and agentic / consent notifications (also default no-ops)
    Task OnTokenIssuingAsync(TokenIssuanceContext context,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnDelegationMintedAsync(DelegationAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnApprovalRequestedAsync(ApprovalAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnApprovalResolvedAsync(ApprovalAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnAgentConsentChangedAsync(string subjectId, string clientId, string change,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnConsentRevokedAsync(string subjectId, string clientId, int grantsRemoved,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnCapabilityTicketRedeemedAsync(string ticketId, string? subjectId, string clientId,
        CancellationToken ct = default) => Task.CompletedTask;
}
```

### Tham số {#parameters}

| Phương thức | Ghi chú và các giá trị `method` / `via` |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"passkey"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnUserUpdatedAsync` | `"admin"`, `"self"` (host có thể truyền giá trị của riêng mình, ví dụ một nguồn SCIM) |
| `OnUserDeletedAsync` | `"admin"`; chỉ là thông báo, bản ghi có thể không còn đọc được nữa |
| `OnLoginFailedAsync` | `"user_not_found"`, `"invalid_password"`, v.v. |
| `OnTokenIssuedAsync` | Các loại grant: `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | Được gọi sau khi xác minh mật khẩu; trả về chính sách MFA hiệu lực cho người dùng. Mặc định: trả về `clientPolicy` không đổi. |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |
| `OnMfaVerifyFailedAsync` | Cùng các phương thức như `OnMfaVerifiedAsync`. Chỉ được gọi sau khi thông tin xác thực của yếu tố thứ nhất hợp lệ, nên một loạt lần gọi dồn dập là tín hiệu mạnh của nỗ lực vượt qua MFA (khác với `OnLoginFailedAsync`, vốn thuộc giai đoạn mật khẩu) |
| `OnEmailConfirmedAsync` | Người dùng đã xác nhận email qua liên kết xác minh; đã được lưu |
| `OnMfaEnrolledAsync` | `"totp"`, `"webauthn"`; thông tin xác thực đã ở trạng thái hoạt động |
| `OnMfaCredentialRemovedAsync` | `"totp"`, `"webauthn"`, `"recoverycode"`; `mfaDisabled` là true khi việc gỡ bỏ không còn để lại yếu tố chính nào |
| `OnRecoveryCodesRegeneratedAsync` | Bộ mã khôi phục trước đó bị vô hiệu |
| `OnPasswordChangedAsync` | ví dụ `"reset"`; thay đổi đã được lưu và các phiên hiện có đã bị vô hiệu |
| `OnTokenIssuingAsync` | Cổng chặn trước khi phát hành, khác với `OnTokenIssuedAsync`. Được gọi với `authorization_code`, `refresh_token` và `device_code`, và với hai lần phát hành cho agent (token exchange được ủy quyền, và `client_credentials` cho một client có agent profile). Ném ngoại lệ để từ chối: một ngoại lệ thông thường trở thành `access_denied` mang thông điệp của nó; ném `ProtocolTokenException` để nêu lỗi OAuth của riêng bạn. Khi refresh, nó chạy trước khi xoay vòng, nên một lần từ chối để refresh token được xuất trình vẫn dùng được. Ngữ cảnh mang `ClientId`, `SubjectId`, `GrantType`, `Scopes`, `RequestedAuthorityJson`, và `OrganizationId` / `OrganizationSlug` khi request đã chọn một tổ chức |
| `OnDelegationMintedAsync` | Một token được ủy quyền (danh tính tổng hợp) đã được phát hành qua token exchange; chỉ là thông báo |
| `OnApprovalRequestedAsync` | Một lần trao đổi được ủy quyền đã dừng lại ở một hành động có chính sách hỏi ý kiến và một yêu cầu phê duyệt đang chờ đã được tạo |
| `OnApprovalResolvedAsync` | Một yêu cầu phê duyệt đang chờ đã được người dùng chấp thuận hoặc từ chối |
| `OnAgentConsentChangedAsync` | `change` là `"granted"` hoặc `"revoked"` (chấp thuận thường trực cho agent) |
| `OnConsentRevokedAsync` | Người dùng đã thu hồi một ứng dụng đã được cấp quyền; sự chấp thuận và các grant gắn với phiên của client đã bị xóa. `grantsRemoved` là số grant đã bị xóa (0 nghĩa là không có grant nào) |
| `OnCapabilityTicketRedeemedAsync` | Một capability ticket đã được đổi lấy token gắn với nó |

### Ví dụ: Audit logger {#example-audit-logger}

```csharp
public sealed class AuditAuthHook(ILogger<AuditAuthHook> logger) : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Login: {Email} via {Method}", email, method);
        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email,
        string createdVia, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] User created: {Email} via {Via}", email, createdVia);
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct)
    {
        logger.LogWarning("[AUDIT] Login failed: {Email} ({Reason})", email, reason);
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId,
        string grantType, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Token issued: {ClientId} ({GrantType})",
            clientId, grantType);
        return Task.CompletedTask;
    }

    // ... remaining required methods return Task.CompletedTask
}
```

### Ví dụ: Giới hạn theo tên miền {#example-domain-restriction}

```csharp
public sealed class DomainRestrictionHook : IAuthHook
{
    private static readonly HashSet<string> BlockedDomains = ["competitor.com"];

    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        var domain = email.Split('@').Last();
        if (BlockedDomains.Contains(domain))
            throw new InvalidOperationException($"Domain {domain} is not allowed");

        return Task.CompletedTask;
    }

    // ... other methods return Task.CompletedTask
}
```

## IClientCredentialsClaimsTransformer {#iclientcredentialsclaimstransformer}

Một token `client_credentials` không có chủ thể, nên điểm nối token exchange không chạm tới được nó. Điểm nối này dành cho một bên gọi là service của chính bạn mà token của nó phải nêu ngữ cảnh nó đang hành động trong đó (một tổ chức, một tenant) khi không có người dùng. Nó chạy sau khi client, các scope của client và mọi resource RFC 8707 đã được kiểm tra, và trước khi token được phát hành.

```csharp
public interface IClientCredentialsClaimsTransformer
{
    Task<ClientCredentialsClaimsResult> TransformAsync(
        OAuthClient client,
        IReadOnlyList<string> grantedScopes,
        IReadOnlyDictionary<string, string> extraParameters,
        CancellationToken ct = default);
}
```

- `extraParameters` chứa các tham số form không thuộc giao thức của token request (mỗi tham số một giá trị, giá trị đầu tiên được dùng), ví dụ một `organization_id` mà bên gọi đã gửi.
- Trả về `ClientCredentialsClaimsResult.Allow(claims)` để ép `claims` lên token (null hoặc rỗng thì token giữ nguyên), hoặc `ClientCredentialsClaimsResult.Reject(error, description)` để từ chối phát hành với lỗi OAuth đó.
- Các tên claim giao thức dành riêng vẫn bị chặn khi phát hành.
- Hãy kiểm tra ràng buộc do bên gọi cung cấp với nguồn thẩm quyền của chính bạn; đừng sao chép nó lên token mà không kiểm tra.
- `NullClientCredentialsClaimsTransformer` mặc định được đăng ký bằng `TryAddSingleton`, nên hãy đăng ký bản của bạn trước để thay thế nó.

## ITurnstileKeyProvider {#iturnstilekeyprovider}

Cả hai khóa Turnstile đều đến từ một đối tượng để widget mà trình duyệt hiển thị và secret mà máy chủ dùng để xác minh không bao giờ lệch nhau. `OptionsTurnstileKeyProvider` mặc định đọc `SiteKey` và `SecretKey` từ `TurnstileOptions`, phù hợp cho một host phục vụ một tên miền. Một host phục vụ các tên miền do khách hàng cung cấp, trong khi Cloudflare giới hạn số hostname của một widget, sẽ đăng ký bản cài đặt scoped của riêng mình trả về cặp khóa của widget được phân bổ cho host đang gửi request.

```csharp
public interface ITurnstileKeyProvider
{
    string? SiteKey { get; }     // null when disabled
    string? SecretKey { get; }   // null or empty disables enforcement
}
```

Được đăng ký bằng `TryAddScoped`, nên một đăng ký thực hiện trước `AddAuthagonal` sẽ được ưu tiên.

## IInteractiveCorsOriginPolicy {#iinteractivecorsoriginpolicy}

API xác thực tương tác (`/api/auth/*`) mặc định từ chối các lời gọi cross-origin kèm thông tin xác thực, vì nó được điều khiển bởi ứng dụng đăng nhập phục vụ từ cùng origin. Một host cho phép tenant xây dựng màn hình đăng nhập riêng trên một origin khác sẽ cài đặt interface này để bảo đảm cho các origin cụ thể.

```csharp
public interface IInteractiveCorsOriginPolicy
{
    ValueTask<bool> IsAllowedAsync(HttpContext context, string origin, string path);
}
```

- Được tham vấn theo từng request và từng origin; việc phân giải tenant đã chạy xong khi nó được gọi.
- Trả về true cho phép origin đó đọc các phản hồi đã xác thực từ các endpoint tài khoản, phiên, hồ sơ và thiết lập MFA của bất kỳ ai đang đăng nhập. Chỉ trả lời cho các origin mà host kiểm soát hoặc đã xác minh, không bao giờ cho một origin lấy từ chính request.
- Mặc định (`DenyInteractiveCorsOriginPolicy`, `TryAddSingleton`) trả về false cho mọi origin.

## ISecretProvider {#isecretprovider}

`ISecretProvider` (trong `Authagonal.Core.Services`) là điểm nối mã hóa có thể giải ngược cho các secret được lưu trữ như client secret của SSO, mật khẩu SMTP và seed TOTP. `ProtectAsync` biến một bản rõ thành một tham chiếu mà store lưu lại; `ResolveAsync` biến tham chiếu trở lại thành bản rõ. `PlaintextSecretProvider` mặc định lưu giá trị nguyên trạng (tham chiếu CHÍNH LÀ giá trị).

```csharp
public interface ISecretProvider
{
    Task<string> ResolveAsync(string secretReference, CancellationToken ct = default);
    Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default);
}
```

Đặt `SecretProvider:VaultUri` sẽ tự động nối `KeyVaultSecretProvider` dựng sẵn (Azure Key Vault qua `DefaultAzureCredential`). Với mọi thứ khác, hãy đăng ký bản cài đặt của riêng bạn trước `AddAuthagonal()`.

## Mã hóa trường PII: IFieldCipher {#pii-field-encryption-ifieldcipher}

`IFieldCipher` mã hóa từng giá trị trường PII của người dùng (điện thoại, công ty, thuộc tính tùy chỉnh, email và tên trên bản ghi hồ sơ) khi lưu trữ. Đây là một điểm nối ở cấp store: các storage provider nhận nó dưới dạng tham số constructor tùy chọn (ví dụ `TableUserStore`), và khi vắng mặt thì `NullFieldCipher` chuyển thẳng được áp dụng, nên mã hóa hoàn toàn là tùy chọn bật thêm và các host chưa cấu hình tiếp tục lưu bản rõ.

```csharp
public interface IFieldCipher
{
    Task<string> ProtectAsync(string plaintext, CancellationToken ct = default);
    Task<string> ResolveAsync(string stored, CancellationToken ct = default);

    // Batch variants have default loop implementations; override for backends
    // with a one-round-trip batch primitive (e.g. Vault Transit)
    Task<IReadOnlyList<string>> ProtectManyAsync(IReadOnlyList<string> plaintexts,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResolveManyAsync(IReadOnlyList<string> stored,
        CancellationToken ct = default);
}
```

Có hai điểm hợp đồng quan trọng. `ProtectAsync` phải trả về một token bản mã tự mô tả (ví dụ `vault:v{n}:...` của Vault Transit), và `ResolveAsync` phải chuyển nguyên vẹn một giá trị mà nó không nhận ra là bản mã của mình. Quy tắc chuyển thẳng này là thứ cho phép mã hóa được triển khai dần dần trên các bản ghi hiện có: đọc một bản ghi chưa được chuyển đổi sẽ trả về bản rõ cũ, và lần ghi tiếp theo sẽ mã hóa lại nó.

## Tìm kiếm blind-index: IIndexTokenizer {#blind-index-search-iindextokenizer}

`IIndexTokenizer` giữ cho các trường đã mã hóa vẫn tìm kiếm được. Nó biến một giá trị bản rõ đã chuẩn hóa thành một token blind-index tất định, an toàn khi dùng làm khóa bảng, thường là một HMAC có khóa mà khóa đó nằm ngoài cơ sở dữ liệu. Tính tất định nghĩa là phép tra cứu bằng nhau vẫn hoạt động ("email = x" trở thành "token = HMAC(x)"), trong khi một bản dump cơ sở dữ liệu không thể tính lại cũng không thể đảo ngược một token. Tìm kiếm theo tiền tố được xây thêm phía trên bằng cách token hóa riêng từng tiền tố của một giá trị, vì một HMAC có khóa phá hủy thứ tự và các phép quét theo khoảng.

> **Những gì một bản dump vẫn để lộ.** "Không thể tính lại cũng không thể đảo ngược" đúng với một token đơn lẻ, không
> đúng với chỉ mục nói chung. Ba phần còn sót lại vẫn tồn tại, và đáng biết chúng trước khi bạn dựa vào cơ chế này:
>
>   *(Đã sửa.)* ~~**Cấu trúc.** Chỉ mục tiền tố ghi một dòng cho mỗi tiền tố, nên số dòng của một bản ghi
>   bằng độ dài của trường được đánh chỉ mục.~~ Mọi giá trị được đánh chỉ mục giờ ghi một số dòng cố định,
>   được độn thêm các dòng mồi nhử mà không truy vấn nào tạo ra được và một bản dump không thể phân biệt với tiền tố thật.
> - **Bằng nhau và tần suất.** Token về bản chất là tất định, đó là điều làm phép tra cứu hoạt động,
>   nên một bản dump cho thấy những bản ghi nào có chung giá trị và mỗi giá trị phổ biến tới mức nào. Chỉ mục tên miền
>   chia nhóm người dùng của bạn theo nơi làm việc, điều này thường đủ để nhận diện người mà không cần khôi phục địa chỉ.
> - **Bản rõ do kẻ tấn công chọn.** Một kẻ tấn công vừa đọc được store *vừa* khiến được giá trị bị đánh chỉ mục
>   (đăng ký một tài khoản, được cấp phát qua SCIM) có thể gửi một giá trị ứng viên rồi tìm token của nó.
>   Cách đó khôi phục được mọi giá trị đoán được (tên miền phổ biến, tên phổ biến) bất kể khóa
>   nằm ở đâu, vì thứ đóng vai trò oracle là đường ghi chứ không phải thuật toán mã hóa.
>
> Token hóa bảo vệ trước đúng tình huống nó được xây dựng cho: một người chỉ nắm trong tay một bản dump,
> đang cố đọc địa chỉ. Hai phần còn sót lại chính là những gì một oracle đăng ký vốn đã để lộ.
> Nếu chúng không thể chấp nhận được, hãy để các bảng chỉ mục tiền tố và tên miền ở trạng thái không cấu hình
> (tra cứu khớp chính xác không để lộ cả hai) thay vì cho rằng HMAC đã che chắn chúng.

```csharp
public interface IIndexTokenizer
{
    Task<string> TokenizeAsync(string value, CancellationToken ct = default);
    Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values,
        CancellationToken ct = default);
}
```

Giống `IFieldCipher`, đây là một tham số constructor store tùy chọn với mặc định chuyển thẳng (`NullIndexTokenizer`), nên các dòng chỉ mục vẫn dùng khóa là bản rõ cho tới khi bạn bật tùy chọn. Các token trả về phải an toàn khi dùng làm giá trị PartitionKey/RowKey của Azure Table (không chứa `/ \ # ?` hay ký tự điều khiển).

## Ghi nhận change-log: IChangeWriter {#change-log-capture-ichangewriter}

`IChangeWriter` (đổi tên từ `ITombstoneWriter` trong 0.6.0) ghi khóa của mọi dòng bị thay đổi vào một bảng change-log riêng, để các bản sao lưu tăng dần tìm được những gì đã thay đổi mà không phải quét cột `Timestamp` không có chỉ mục của các bảng đang dùng. Thao tác xóa được ghi nhận cho mọi bảng (phép quét dòng đang tồn tại không thể thấy một dòng đã mất); thao tác upsert được ghi nhận cho các bảng mà bản sao lưu đọc từ log thay vì quét. Các bản cài đặt dựng sẵn: `TableChangeWriter` (Azure Table Storage), `DynamoChangeWriter` (DynamoDB), và `SqlChangeWriter` (PostgreSQL / SQLite).

```csharp
public interface IChangeWriter
{
    // Deletes
    Task WriteAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);

    // Upserts
    Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteUpsertBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);
}
```

Hợp đồng về thứ tự cho bên cài đặt và bên gọi: ghi tombstone của thao tác xóa TRƯỚC khi xóa dòng dữ liệu. Một sự cố sập giữa chừng theo thứ tự ngược lại sẽ làm mất thao tác xóa khỏi mọi bản sao lưu về sau, vì xóa là loại thay đổi duy nhất mà một lần quét lại không tự khắc phục được. Sự cố theo chiều ngược lại thì an toàn: một lần ghi sau đó vào khóa sẽ đóng dấu một timestamp mới hơn, và merge/restore giữ lại các dòng được ghi sau tombstone.

## Endpoint tùy chỉnh {#custom-endpoints}

Thêm endpoint của riêng bạn bên cạnh các endpoint của Authagonal:

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## Tích hợp HashiCorp Vault Transit {#hashicorp-vault-transit-integration}

> **Việc ký JWT không được giao cho Vault.** Mục này trước đây có một đoạn DI trông như
> bật được tính năng đó. Đăng ký `VaultTransitCryptoProvider` **không có tác dụng gì tới việc ký token**:
> `ProtocolKeyManager` gọi `ProtocolSigningKeyOps.BuildSigningCredentials`, phương thức này dựng một
> `ECDsaSecurityKey` từ vật liệu khóa trong `ISigningKeyStore`, và không có gì thay nó bằng một
> `VaultTransitSecurityKey`. Một host làm theo đoạn cũ thấy token ES256 xác minh được với JWKS
> và hợp lý mà kết luận rằng Vault đang ký chúng, trong khi khóa riêng thực ra được sinh cục bộ ở lần khởi động đầu tiên
> và được lưu vào kho dữ liệu chính, ở dạng bản rõ trừ khi tình cờ có một `IFieldCipher` được đăng ký.
> Quyền đọc kho đó đồng nghĩa với khả năng mạo danh hoàn toàn issuer. Nếu bạn có yêu cầu tuân thủ rằng
> khóa ký không bao giờ rời khỏi HSM, cơ chế này không đáp ứng yêu cầu đó.
>
> Máy chủ giờ ghi log lỗi lúc khởi động nếu phát hiện `VaultTransitCryptoProvider` được đăng ký, để
> sự hiểu lầm này không thể âm thầm tồn tại.
>
> Để biến nó thành thật cần nhiều hơn một lần đăng ký DI: `ISigningKeyStore` phải biểu diễn được một khóa không có
> vật liệu cục bộ (một *tên* khóa Transit thay vì một số vô hướng bí mật), `BuildSigningCredentials` cần một
> điểm nối để trả về một `VaultTransitSecurityKey`, `BuildJwksAsync` phải công bố khóa công khai đọc lại
> từ Vault, và việc xoay vòng cùng công bố trước phải tạo và nâng cấp các phiên bản khóa Transit thay vì
> sinh cục bộ. `VaultTransitClient`, `VaultTransitSecurityKey`, `VaultTransitSignatureProvider` và
> `VaultTransitCryptoProvider` được giữ lại vì chúng là những phần hoạt động được; thứ còn thiếu là phần nối dây.

Điều mà `VaultTransitClient` **thực sự** hữu ích hiện nay là các điểm nối mã hóa và HMAC: một
`IFieldCipher` dựa trên Vault cho PII khi lưu trữ, hoặc một `IIndexTokenizer` cho blind index có khóa:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("Vault", client =>
{
    client.BaseAddress = new Uri("https://vault.example.com");
    client.DefaultRequestHeaders.Add("X-Vault-Token", "hvs.xxx");
});

builder.Services.AddSingleton<VaultTransitClient>();

// Your own adapters over the client. These are the seams Authagonal actually consumes.
builder.Services.AddSingleton<IFieldCipher, MyVaultFieldCipher>();
builder.Services.AddSingleton<IIndexTokenizer, MyVaultIndexTokenizer>();

builder.Services.AddAuthagonal(builder.Configuration);
```

Đăng ký một `IFieldCipher` cũng là cách tắt `PlaintextSigningKeyWarning`, vì các store khóa ký
chuyển vật liệu khóa của chúng qua chính điểm nối đó, và đây là điều gần nhất với lời khẳng định ban đầu
có thể làm được hiện nay: khóa riêng vẫn tồn tại cục bộ, nhưng không ở dạng bản rõ.

`VaultTransitClient` cung cấp các thao tác sau:

| Phương thức | Mô tả |
|---|---|
| `SignAsync(keyName, data)` | Ký dữ liệu bằng một khóa Vault Transit |
| `VerifyAsync(keyName, data, signature)` | Xác minh một chữ ký định dạng JWS qua endpoint verify của Transit |
| `EncryptAsync` / `DecryptAsync` (+ `EncryptBatchAsync` / `DecryptBatchAsync`) | Mã hóa đối xứng dưới một khóa `aes256-gcm96`; trả về token `vault:v{n}:...` để lưu nguyên văn |
| `HmacAsync` / `HmacBatchAsync` | HMAC có khóa dưới một khóa `hmac` (token blind-index) |
| `CreateKeyAsync(keyName, type)` | Tạo một khóa Transit mới (mặc định: `ecdsa-p256`) |
| `EnsureKeyTypeAsync(keyName, type)` | Bảo đảm một cách lũy đẳng rằng khóa tồn tại với loại mong muốn (tạo lại khi sai loại; không thể đổi loại khóa Transit tại chỗ) |
| `RotateKeyAsync(keyName)` | Xoay vòng một khóa sang phiên bản mới |
| `DeleteKeyAsync(keyName)` | Xóa một khóa (bật `deletion_allowed` trước) |
| `ReadKeyAsync(keyName)` | Đọc metadata, các phiên bản và khóa công khai của khóa |
| `KeyExistsAsync(keyName)` | Kiểm tra một khóa có tồn tại hay không |

`VaultTransitCryptoProvider` tích hợp với `JsonWebTokenHandler` của .NET để việc ký JWT dùng Vault một cách trong suốt. `VaultTransitSecurityKey` và `VaultTransitSignatureProvider` xử lý phần tích hợp ở mức thấp.

## Email {#email}

Trình gửi Resend dựng sẵn tự động kích hoạt khi `Email:ResendApiKey` được cấu hình (hãy đặt cả `Email:SenderEmail`). Khi không có `IEmailService` nào, thư bị bỏ qua qua `NullEmailService`, và vì cổng chặn đăng nhập yêu cầu email đã xác nhận mặc định được bật, người dùng tự đăng ký sẽ không bao giờ đăng nhập được; `UseAuthagonal()` ghi một cảnh báo rõ ràng lúc khởi động trong trạng thái đó.

Để dùng một nhà cung cấp khác, hãy đăng ký `IEmailService` của riêng bạn trước `AddAuthagonal()`:

```csharp
public sealed class SmtpEmailService(SmtpClient smtp) : IEmailService
{
    public async Task SendVerificationEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Verify your email", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }

    public async Task SendPasswordResetEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Reset your password", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }
}
```

`IEmailService` cũng khai báo `SendAccountExistsEmailAsync` (được gửi khi ai đó cố đăng ký một email đã được đăng ký, giữ cho phản hồi đăng ký trung lập trước việc dò tìm tài khoản). Phương thức này có bản cài đặt mặc định không làm gì, nên các bản cài đặt hiện có vẫn tiếp tục biên dịch được.

## Xem thêm {#see-also}

- [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server): ví dụ hoàn chỉnh chạy được
- [demos/sample-app/](https://github.com/authagonal/authagonal/tree/master/demos/sample-app): ví dụ ứng dụng client
