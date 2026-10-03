---
layout: default
title: Máy chủ tùy chỉnh
locale: vi
---

# Bắt đầu nhanh với máy chủ tùy chỉnh

Hướng dẫn này đi qua cách chạy Authagonal dưới dạng thư viện trong dự án ASP.NET Core của riêng bạn, rồi tùy biến giao diện đăng nhập bằng các React component của riêng bạn.

## Phần 1: Thiết lập máy chủ {#part-1-server-setup}

### Tạo dự án {#create-the-project}

```bash
dotnet new web -n MyAuthServer
cd MyAuthServer

# Add Authagonal packages (or project references for source builds)
dotnet add package Authagonal.Server
dotnet add package Authagonal.AzureProvider
```

Tệp `.csproj` của bạn cần chứa:

```xml
<ItemGroup>
  <PackageReference Include="Authagonal.Server" Version="*" />
  <PackageReference Include="Authagonal.AzureProvider" Version="*" />
</ItemGroup>
```

`Authagonal.AzureProvider` cung cấp các store Azure Table Storage mà `AddAuthagonal` kết nối dựa trên cấu hình `Storage:*`. Để chạy trên AWS thay vào đó, hãy tham chiếu `Authagonal.AwsProvider` và gọi `AddAuthagonalAwsStorage(...)` trước `AddAuthagonal`, xem [Cài đặt → Backend AWS](installation#aws-backend).

### Cấu hình Program.cs {#configure-programcs}

Thiết lập tối thiểu gồm ba lời gọi: `AddAuthagonal`, `UseAuthagonal` và `MapAuthagonalEndpoints`.

```csharp
var builder = WebApplication.CreateBuilder(args);

// 1. Register custom services BEFORE AddAuthagonal (yours take precedence)
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, ConsoleEmailService>();

// 2. Register Authagonal
builder.Services.AddAuthagonal(builder.Configuration);

var app = builder.Build();

// 3. Middleware + endpoints
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// 4. Serve the login SPA from wwwroot
app.MapFallbackToFile("index.html");

app.Run();
```

### Cấu hình appsettings.json {#configure-appsettingsjson}

```json
{
  "Issuer": "https://auth.example.com",
  "Storage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=..."
  },
  "Clients": [
    {
      "Id": "my-app",
      "Name": "My Application",
      "GrantTypes": ["authorization_code", "refresh_token"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "Scopes": ["openid", "profile", "email", "offline_access"],
      "CorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireSecret": false,
      "AllowOfflineAccess": true
    }
  ]
}
```

| Khóa | Mô tả |
|---|---|
| `Issuer` | URL công khai của máy chủ xác thực. Được dùng trong token và OIDC discovery. |
| `Storage:ConnectionString` | Connection string của Azure Table Storage. Hoặc đặt `Storage:TableServiceUri` để xác thực bằng managed identity; bắt buộc phải có một trong hai. |
| `Clients` | Mảng các OAuth client được nạp sẵn lúc khởi động. |

### Các điểm mở rộng {#extensibility-points}

Đăng ký các hiện thực của bạn **trước** khi gọi `AddAuthagonal()`, Authagonal dùng `TryAdd`, nên đăng ký của bạn được ưu tiên. `IAuthHook` là ngoại lệ về bản chất: bạn có thể đăng ký nhiều hook và tất cả đều chạy, còn hook không làm gì dựng sẵn chỉ được thêm khi bạn không đăng ký hook nào.

| Interface | Mục đích | Mặc định |
|---|---|---|
| `IEmailService` | Gửi email xác minh, đặt lại mật khẩu và thông báo tài khoản đã tồn tại | Bên gửi Resend dựng sẵn khi `Email:ResendApiKey` được đặt; nếu không thì không làm gì (âm thầm loại bỏ) |
| `IAuthHook` | Chặn hoặc kiểm toán các sự kiện đăng nhập, đăng ký và token | Không làm gì |
| `IProvisioningOrchestrator` | Cấp phát người dùng vào các ứng dụng downstream tại thời điểm authorize | Cấp phát TCC |
| `ISecretProvider` | Phân giải client secret | Plaintext (hoặc Key Vault với `SecretProvider:VaultUri`) |

#### Ví dụ: hook kiểm toán {#example-audit-hook}

```csharp
using Authagonal.Core.Models;
using Authagonal.Core.Services;

public class AuditAuthHook(ILogger<AuditAuthHook> logger) : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId = null, CancellationToken ct = default)
    {
        logger.LogInformation("Login: {Email} via {Method}", email, method);
        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email,
        string createdVia, CancellationToken ct = default)
    {
        logger.LogInformation("New user: {Email} via {Via}", email, createdVia);
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason,
        CancellationToken ct = default)
    {
        logger.LogWarning("Failed login: {Email}: {Reason}", email, reason);
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId,
        string grantType, CancellationToken ct = default)
    {
        logger.LogInformation("Token issued: {ClientId} ({GrantType})", clientId, grantType);
        return Task.CompletedTask;
    }

    public Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email,
        MfaPolicy clientPolicy, string clientId, CancellationToken ct = default)
        => Task.FromResult(clientPolicy);

    public Task OnMfaVerifiedAsync(string userId, string email,
        string mfaMethod, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task OnUserUpdatedAsync(string userId, string email,
        string updatedVia, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task OnUserDeletedAsync(string userId, string email,
        string deletedVia, CancellationToken ct = default)
        => Task.CompletedTask;
}
```

Interface này còn có thêm các thành viên tùy chọn với hiện thực mặc định không làm gì (`OnMfaVerifyFailedAsync`, `OnEmailConfirmedAsync`, `OnMfaEnrolledAsync`, `OnMfaCredentialRemovedAsync`, `OnRecoveryCodesRegeneratedAsync`, `OnPasswordChangedAsync`, `OnTokenIssuingAsync`, `OnDelegationMintedAsync`, `OnApprovalRequestedAsync`, `OnApprovalResolvedAsync`, `OnAgentConsentChangedAsync`, `OnConsentRevokedAsync`, `OnCapabilityTicketRedeemedAsync`), chỉ ghi đè chúng nếu bạn cần các sự kiện đó.

#### Ví dụ: dịch vụ email {#example-email-service}

```csharp
using Authagonal.Core.Services;

public class ConsoleEmailService(ILogger<ConsoleEmailService> logger) : IEmailService
{
    public Task SendVerificationEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        logger.LogInformation("Verify email: {Url}", callbackUrl);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        logger.LogInformation("Reset password: {Url}", callbackUrl);
        return Task.CompletedTask;
    }
}
```

`IEmailService` cũng có một phương thức tùy chọn `SendAccountExistsEmailAsync(email, signInUrl, ct)` (mặc định không làm gì), được gửi khi ai đó đăng ký một địa chỉ đã có tài khoản.

> **Email là cái bẫy tích hợp phổ biến nhất.** Nếu bạn không đăng ký `IEmailService` nào và không đặt `Email:ResendApiKey`, thư xác minh và thư đặt lại mật khẩu bị âm thầm loại bỏ, và vì điều kiện đăng nhập yêu cầu email đã xác nhận được bật theo mặc định, người dùng tự đăng ký sẽ không bao giờ đăng nhập được (`UseAuthagonal` cảnh báo lúc khởi động). Bên gửi Resend dựng sẵn tự động kích hoạt khi `Email:ResendApiKey` + `Email:SenderEmail` được cấu hình; cho dev/test, `Auth:AutoConfirmEmailDomains` bỏ qua bước xác minh với các tên miền được liệt kê. Xem [Cấu hình → Email](configuration#email).

### Thêm endpoint tùy chỉnh {#add-custom-endpoints}

Bạn có thể thêm endpoint của riêng mình bên cạnh các endpoint của Authagonal:

```csharp
app.MapGet("/custom/health", () => Results.Ok(new { status = "healthy" }));
```

### Tắt admin API {#disable-admin-api}

Với các bản triển khai hướng ra công chúng, hãy tắt các endpoint quản trị:

```json
{
  "AdminApi": {
    "Enabled": false
  }
}
```

### Chạy thử {#run-it}

```bash
dotnet run
```

Máy chủ khởi động tại URL đã cấu hình, phục vụ tài liệu OIDC discovery tại `/.well-known/openid-configuration`, giao diện đăng nhập tại `/login`, cùng mọi API xác thực/quản trị.

---

## Phần 2: Giao diện đăng nhập tùy chỉnh {#part-2-custom-login-ui}

SPA đăng nhập mặc định dùng được ngay, nhưng bạn có thể thay nó bằng ứng dụng React của riêng mình, import các component và API client từ gói npm `@authagonal/login`.

### Dựng khung frontend {#scaffold-the-frontend}

```bash
mkdir login-app && cd login-app
npm init -y
npm install react react-dom react-router @authagonal/login
npm install -D vite @vitejs/plugin-react typescript @types/react @types/react-dom
```

### Những gì gói npm export {#what-the-npm-package-exports}

```typescript
// Components: use as-is or as reference
import {
  AuthLayout,
  LoginPage,
  ForgotPasswordPage,
  ResetPasswordPage,
  MfaChallengePage,
  MfaSetupPage,
  RegisterPage,
  ConsentPage,
  AgentConsentPage,
  GrantsPage,
  DevicePage,
  App,              // Standalone SPA with full routing (accepts an extraRoutes prop)
} from '@authagonal/login';

// UI primitives
import {
  Button, Input, Label, Card, CardHeader, CardTitle, CardDescription, CardContent, CardFooter,
  Alert, Separator, Turnstile, cn,
} from '@authagonal/login';

// API clients: call from your custom pages
import {
  login, register, logout, ssoCheck, forgotPassword, resetPassword,
  getSession, getProviders, getPasswordPolicy,
  mfaVerify, mfaStatus, mfaTotpSetup, mfaTotpConfirm,
  mfaWebAuthnSetup, mfaWebAuthnConfirm, mfaRecoveryGenerate,
  mfaDeleteCredential,
  ApiRequestError,
} from '@authagonal/login';

// Branding
import {
  loadBranding, useBranding, BrandingContext, brandingDefaults, resolveLocalized,
  getBoot, getOrganization,
} from '@authagonal/login';

// Redirect helpers for the post-login hop
import { resolveRedirect, isSameOriginPath } from '@authagonal/login';

// i18n: always import from this package, not react-i18next directly
import { useTranslation, i18n } from '@authagonal/login';

// Styles
import '@authagonal/login/styles.css';

// Types
import type {
  BrandingConfig, LocalizedString, LoginResponse,
  SessionResponse, ExternalProvider, PasswordPolicyResponse,
  MfaStatusResponse, MfaTotpSetupResponse,
} from '@authagonal/login';
```

### Điểm vào (main.tsx) {#entry-point-maintsx}

Tải cấu hình thương hiệu từ máy chủ và bọc ứng dụng trong branding context:

```tsx
import { createRoot } from 'react-dom/client';
import { loadBranding, BrandingContext } from '@authagonal/login';
import '@authagonal/login/styles.css';
import App from './App';

loadBranding().then((config) => {
  document.title = `Sign In | ${config.appName}`;
  createRoot(document.getElementById('root')!).render(
    <BrandingContext.Provider value={config}>
      <App />
    </BrandingContext.Provider>
  );
});
```

### Định tuyến (App.tsx) {#routing-apptsx}

Kết hợp các trang tùy chỉnh với các trang có sẵn trong gói. Máy chủ đưa người dùng tới các đường dẫn dưới `/login` (authorize endpoint chuyển hướng tới `/login?returnUrl=...`, còn email liên kết tới `/login/reset-password`, `/login/consent`, `/login/device`), nên router phải dùng `basename="/login"` và các route dưới đây là tương đối so với nó:

```tsx
import { BrowserRouter, Routes, Route, Navigate } from 'react-router';
import {
  RegisterPage, ForgotPasswordPage, ResetPasswordPage, MfaChallengePage, MfaSetupPage,
  ConsentPage, AgentConsentPage, DevicePage, GrantsPage,
} from '@authagonal/login';
import MyLoginPage from './MyLoginPage';
import MyLayout from './MyLayout';

export default function App() {
  return (
    <BrowserRouter basename="/login">
      <MyLayout>
        <Routes>
          <Route path="/" element={<MyLoginPage />} />
          <Route path="/register" element={<RegisterPage />} />
          <Route path="/forgot-password" element={<ForgotPasswordPage />} />
          <Route path="/reset-password" element={<ResetPasswordPage />} />
          <Route path="/mfa-challenge" element={<MfaChallengePage />} />
          <Route path="/mfa-setup" element={<MfaSetupPage />} />
          <Route path="/consent" element={<ConsentPage />} />
          <Route path="/consent/agents/:clientId" element={<AgentConsentPage />} />
          <Route path="/device" element={<DevicePage />} />
          <Route path="/grants" element={<GrantsPage />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </MyLayout>
    </BrowserRouter>
  );
}
```

Nếu bạn chỉ muốn thêm trang vào ứng dụng có sẵn, hãy render `<App extraRoutes={...} />` từ gói thay vào đó. Nó đã cung cấp sẵn mọi route ở trên (cùng với `/account`).

### Trang đăng nhập tùy chỉnh {#custom-login-page}

Xây dựng form đăng nhập của riêng bạn bằng các API client từ gói npm:

```tsx
import { useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { login, resolveRedirect, ApiRequestError, useBranding } from '@authagonal/login';

export default function MyLoginPage() {
  const branding = useBranding();
  const navigate = useNavigate();
  const [searchParams] = useSearchParams();
  const returnUrl = searchParams.get('returnUrl') || '';
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    try {
      const result = await login(email, password, returnUrl || undefined);

      // A second factor is still owed: hand over to the package's MFA pages.
      if (result.mfaRequired && result.challengeId) {
        const params = new URLSearchParams({
          challengeId: result.challengeId,
          ...(returnUrl ? { returnUrl } : {}),
          ...(result.methods ? { methods: result.methods.join(',') } : {}),
          ...(result.webAuthn ? { webAuthn: JSON.stringify(result.webAuthn) } : {}),
        });
        navigate(`/mfa-challenge?${params.toString()}`);
        return;
      }
      if (result.mfaSetupRequired) {
        navigate(`/mfa-setup${returnUrl ? `?returnUrl=${encodeURIComponent(returnUrl)}` : ''}`, {
          state: { setupToken: result.setupToken },
        });
        return;
      }

      // Login sets a cookie. resolveRedirect only returns a same-origin path or the origin of a
      // registered client's home URI; anything else falls back to the default.
      window.location.href = await resolveRedirect(returnUrl, () => '/login/account');
    } catch (err) {
      if (err instanceof ApiRequestError) {
        setError(err.message || 'Login failed');
      }
    }
  };

  return (
    <form onSubmit={handleSubmit}>
      <h1>Sign in to {branding.appName}</h1>
      {error && <p className="error">{error}</p>}
      <input
        type="email"
        value={email}
        onChange={(e) => setEmail(e.target.value)}
        placeholder="Email"
        required
      />
      <input
        type="password"
        value={password}
        onChange={(e) => setPassword(e.target.value)}
        placeholder="Password"
        required
      />
      <button type="submit">Sign in</button>
    </form>
  );
}
```

### Bố cục tùy chỉnh {#custom-layout}

Bọc `AuthLayout` có sẵn để thêm nhận diện thương hiệu của riêng bạn:

```tsx
import { AuthLayout } from '@authagonal/login';

export default function MyLayout({ children }: { children: React.ReactNode }) {
  return (
    <>
      <AuthLayout>{children}</AuthLayout>
      <footer>
        &copy; {new Date().getFullYear()} My Company |
        <a href="/terms">Terms</a> | <a href="/privacy">Privacy</a>
      </footer>
    </>
  );
}
```

### Thương hiệu (wwwroot/branding.json) {#branding-wwwrootbrandingjson}

Cấu hình giao diện đăng nhập mà không cần build lại:

```json
{
  "appName": "My App",
  "logoUrl": "/logo.svg",
  "primaryColor": "#059669",
  "supportEmail": "support@example.com",
  "showForgotPassword": true,
  "showRegistration": false,
  "darkMode": "auto",
  "customCssUrl": "/custom.css"
}
```

Schema đầy đủ, bao gồm văn bản chào mừng đã bản địa hóa, danh sách bộ chọn ngôn ngữ, và các giá trị ghi đè màu sắc cùng nền logo riêng cho từng chế độ tối/sáng, có trên trang [Thương hiệu](branding).

### Cấu hình Vite {#vite-config}

Chuyển tiếp (proxy) các lời gọi API tới backend trong lúc phát triển:

```typescript
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  resolve: {
    dedupe: ['react', 'react-dom'],
  },
  server: {
    proxy: {
      '/api': { target: 'http://localhost:5000', changeOrigin: true },
      '/connect': { target: 'http://localhost:5000', changeOrigin: true },
      '/saml': { target: 'http://localhost:5000', changeOrigin: true },
      '/oidc': { target: 'http://localhost:5000', changeOrigin: true },
    },
  },
});
```

### Build và phục vụ {#build-and-serve}

Thêm một build target vào `.csproj` để tự động build SPA và sao chép nó vào `wwwroot`:

```xml
<Target Name="BuildLoginApp" BeforeTargets="Build" Condition="!Exists('wwwroot/index.html')">
  <Exec Command="npm ci" WorkingDirectory="login-app" />
  <Exec Command="npm run build" WorkingDirectory="login-app" />
  <ItemGroup>
    <LoginAppFiles Include="login-app/dist/**/*" />
  </ItemGroup>
  <Copy SourceFiles="@(LoginAppFiles)" DestinationFolder="wwwroot/%(RecursiveDir)" />
</Target>
```

Giờ đây `dotnet build` build cả máy chủ .NET lẫn React SPA, và `dotnet run` phục vụ mọi thứ từ một tiến trình duy nhất.
