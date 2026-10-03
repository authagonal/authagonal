---
layout: default
title: Trang chủ
locale: vi
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Logo Authagonal">
</p>

# Authagonal

Máy chủ xác thực OAuth 2.0 / OpenID Connect / SAML 2.0 dành cho .NET, chạy trên lớp lưu trữ có thể thay thế: PostgreSQL hoặc SQLite của riêng bạn, Azure Table Storage, hoặc AWS (DynamoDB / S3 / Secrets Manager).

Một bản triển khai duy nhất, khép kín. Máy chủ và giao diện đăng nhập được đóng gói chung trong một Docker image, SPA được phục vụ từ cùng origin với API, nên xác thực bằng cookie, chuyển hướng và CSP đều hoạt động mà không phải vướng vào sự phức tạp của cross-origin.

> **Muốn dùng dịch vụ được quản lý?** [Authagonal Cloud](https://authagonal.io) vận hành toàn bộ những thứ này cho bạn, đa tenant, mọi tính năng có trong mọi gói, không tính phí SSO theo từng kết nối. → [authagonal.io](https://authagonal.io)

## Tính năng chính {#key-features}

- **OIDC Provider**: các grant authorization_code + PKCE, client_credentials, refresh_token, device_code với cơ chế xoay vòng dùng một lần
- **SAML 2.0 SP**: bản triển khai tự xây dựng, hỗ trợ đầy đủ Azure AD (ký response, ký assertion, hoặc cả hai), một cặp khóa SP riêng cho từng kết nối để ký AuthnRequest + giải mã `EncryptedAssertion`, và Single Logout (khởi tạo từ SP lẫn từ IdP)
- **Liên kết OIDC động**: kết nối tới Google, Apple, Azure AD, hoặc bất kỳ IdP nào tuân thủ OIDC
- **Xác thực đa yếu tố**: TOTP, WebAuthn/passkey, mã khôi phục; chính sách theo từng client (`Disabled` / `Enabled` / `Required`) có thể ghi đè theo từng người dùng qua `IAuthHook`, được áp dụng cả cho đăng nhập liên kết
- **Cấp phát SCIM 2.0**: nhận cấp phát người dùng/nhóm từ Entra ID, Okta, OneLogin; liệt kê phân trang bằng con trỏ và bộ lọc `eq` dựa trên blind index
- **Màn hình chấp thuận OAuth**: chấp thuận theo từng client, hỏi lại khi scope thay đổi, và quản lý grant
- **Device Authorization Grant**: luồng RFC 8628 cho các thiết bị hạn chế nhập liệu (smart TV, CLI, IoT)
- **Token Introspection**: RFC 7662 để resource server kiểm tra tính hợp lệ của token
- **Ký token**: chỉ ES256. Access token mang `typ: at+jwt` theo RFC 9068 để resource server
  phân biệt được chúng với id_token và logout token, nhưng **không tuyên bố tuân thủ RFC 9068**: §2.1
  yêu cầu RS256 nằm trong số các thuật toán được hỗ trợ, còn máy chủ này không phát hành cũng không chấp nhận nó. Chỉ dùng
  một thuật toán là lập trường có chủ đích: mỗi thuật toán được chấp nhận thêm là thêm một cách để
  bên kiểm tra token bị dụ dùng nhầm thuật toán.
- **Back-Channel Logout**: gửi thông báo OIDC Back-Channel Logout 1.0 tới các relying party
- **Phiên phía máy chủ** *(tùy chọn bật)*: `AddAuthagonalServerSideSessions` lưu ticket SSO trong kho lưu trữ để cookie xác thực chỉ mang một id không trong suốt, đồng thời bật chức năng tự phục vụ `GET /api/auth/sessions` để liệt kê phiên và thu hồi theo từng thiết bị ([Auth API](auth-api#sessions-self-service))
- **Backend-for-Frontend**: `Authagonal.Bff` (.NET) và `@authagonal/bff` (Node), một BFF dạng confidential client để SPA không bao giờ nắm giữ token ([BFF](bff))
- **GDPR tự phục vụ** *(Authagonal Cloud)*: xuất dữ liệu và lên lịch xóa tài khoản từ trang tài khoản
  do Cloud lưu trữ. Ứng dụng đăng nhập có sẵn giao diện cho chức năng này, nhưng các endpoint mà nó gọi
  (`GET /api/v1/account/export`, `POST /api/v1/account/erasure`) do auth host của Cloud phục vụ và
  **không** thuộc bề mặt của thư viện này. Bản tự triển khai phải tự hiện thực chúng, hoặc bỏ hai
  nút đó khỏi trang tài khoản của mình: `MapFallbackToFile` trả lời một route chưa được hiện thực bằng 200 kèm HTML
  của chính SPA, nên một chức năng xuất chưa được hiện thực phải được nhận diện ra thay vì bị tải xuống như một tệp.
- **Cấp phát TCC**: cấp phát theo mô hình Try-Confirm-Cancel vào các ứng dụng downstream ngay tại thời điểm authorize
- **Giao diện đăng nhập tùy biến thương hiệu**: cấu hình lúc chạy qua một tệp JSON, logo, màu sắc, CSS custom property, không cần build lại; đã bản địa hóa sang 11 ngôn ngữ
- **Auth Hook**: khả năng mở rộng qua `IAuthHook` cho ghi nhật ký kiểm toán, kiểm tra hợp lệ tùy chỉnh, webhook
- **Điểm mở rộng mã hóa PII**: các điểm mở rộng `IFieldCipher` / `IIndexTokenizer` để mã hóa dữ liệu lưu trữ ở cấp trường, kèm tìm kiếm bằng blind index có khóa (HMAC); mã khôi phục được mã hóa qua `ISecretProvider`
- **Client HashiCorp Vault Transit**: ký/xác minh chữ ký, mã hóa/giải mã và HMAC có khóa trên engine Transit của Vault, dùng để xây dựng một `IFieldCipher` hoặc `IIndexTokenizer`. Ký JWT từ xa chưa được kết nối: khóa ký token luôn là khóa nằm trong `ISigningKeyStore`.
- **Thư viện có thể kết hợp**: `AddAuthagonal()` / `UseAuthagonal()` để chạy trong dự án của riêng bạn với các dịch vụ ghi đè tùy chỉnh
- **Sẵn sàng cho Native AOT**: IL trimming và tuần tự hóa JSON sinh bằng source generator để khởi động nhanh
- **Lưu trữ có thể thay thế**: PostgreSQL hoặc SQLite tự vận hành (không cần tài khoản cloud), hoặc Azure Table Storage / AWS (DynamoDB / S3 / Secrets Manager) cho backend chi phí thấp, thân thiện với serverless
- **Sao lưu & khôi phục**: sao lưu tăng dần (dựa trên change-log, có lượt quét toàn bộ làm lưới an toàn), kiểm tra tính toàn vẹn, theo dõi thao tác xóa bằng tombstone
- **Admin API**: CRUD người dùng, quản lý provider SAML/OIDC, định tuyến SSO theo tên miền, mạo danh bằng token

## Các tích hợp phổ biến {#common-integrations}

Hướng dẫn theo tác vụ cho những luồng mà các nhóm hay xây dựng nhất:

- **[Nâng cấp người dùng](user-upgrade)**: biến một tài khoản khách / SSO / được mời thành tài khoản có thông tin đăng nhập thông qua cơ chế nhận lại tài khoản không mật khẩu, và chạy bước nâng khách → thành viên chuẩn của bạn khi xác nhận.
- **[SSO tự phục vụ](self-service-sso)**: cấp phát JIT cho các kết nối doanh nghiệp: onboarding chỉ qua lời mời so với tự phục vụ, ngăn IdP bên ngoài trở thành cái bẫy tự gây hại, và các trang trung gian trước khi chuyển sang IdP liên kết.
- **[Phiên liên kết](federated-sessions)**: thu hồi phiên cục bộ khi IdP upstream thu hồi (`RevalidateOnRefresh`).
- **[Backend-for-Frontend (BFF)](bff)**: giữ token bên ngoài trình duyệt: một OIDC confidential client trên backend của bạn với cookie phiên httpOnly và một proxy API tự chèn token, bằng .NET hoặc Node.
- **[Xác thực WebSocket](websocket-auth)**: xác thực WebSocket của trình duyệt thông qua BFF mà không để lộ token.
- **[Xác thực cho agent](agentic-auth)**: ủy quyền thẩm quyền của người dùng cho AI agent: agent đã đăng ký, thẩm quyền chi tiết theo RFC 9396, token ủy quyền tổng hợp (RFC 8693 `act`), chấp thuận thường trực, phê duyệt đúng lúc, capability ticket.
- **[Tổ chức](organizations)**: phục vụ nhiều khách hàng từ một tenant: bản ghi `Organization` và bản ghi thành viên, tham số authorize `organization`, `org_id` / `org_slug` / `org_name` trên token, vai trò theo phạm vi tổ chức, và từ chối người không phải thành viên.

## Kiến trúc {#architecture}

```
Client App                    Authagonal                         IdP (Azure AD, etc.)
    │                             │                                    │
    ├─ GET /connect/authorize ──► │                                    │
    │                             ├─ 302 → /login (SPA)                │
    │                             │   ├─ SSO check                     │
    │                             │   └─ SAML/OIDC redirect ─────────► │
    │                             │                                    │
    │                             │ ◄── SAML Response / OIDC callback ─┤
    │                             │   └─ Create user + cookie          │
    │                             │                                    │
    │                             ├─ TCC provisioning (try/confirm)    │
    │                             ├─ Issue authorization code          │
    │ ◄─ 302 ?code=...&state=... ┤                                    │
    │                             │                                    │
    ├─ POST /connect/token ─────► │                                    │
    │ ◄─ { access_token, ... } ──┤                                    │
```

Hãy bắt đầu với hướng dẫn [Cài đặt](installation) hoặc chuyển thẳng tới [Bắt đầu nhanh](quickstart). Để chạy Authagonal bên trong dự án của riêng bạn, xem [Khả năng mở rộng](extensibility). Về quản lý dữ liệu, xem [Sao lưu & khôi phục](backup-restore). Để xem toàn bộ lịch sử thay đổi, xem [Changelog](https://github.com/authagonal/authagonal/blob/master/CHANGELOG.md).
