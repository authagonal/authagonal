---
layout: default
title: 自定义服务器
locale: zh-Hans
---

# 自定义服务器快速入门

本指南演示如何在你自己的 ASP.NET Core 项目中将 Authagonal 作为库托管，然后用你自己的 React 组件定制登录界面。

## 第 1 部分：服务器搭建 {#part-1-server-setup}

### 创建项目 {#create-the-project}

```bash
dotnet new web -n MyAuthServer
cd MyAuthServer

# Add Authagonal packages (or project references for source builds)
dotnet add package Authagonal.Server
dotnet add package Authagonal.AzureProvider
```

你的 `.csproj` 应包含：

```xml
<ItemGroup>
  <PackageReference Include="Authagonal.Server" Version="*" />
  <PackageReference Include="Authagonal.AzureProvider" Version="*" />
</ItemGroup>
```

`Authagonal.AzureProvider` 提供 Azure Table Storage 存储，由 `AddAuthagonal` 根据 `Storage:*` 配置接入。如果要改为托管在 AWS 上，请引用 `Authagonal.AwsProvider`，并在 `AddAuthagonal` 之前调用 `AddAuthagonalAwsStorage(...)`，参见[安装 → AWS 后端](installation#aws-backend)。

### 配置 Program.cs {#configure-programcs}

最小配置只需三个调用：`AddAuthagonal`、`UseAuthagonal` 和 `MapAuthagonalEndpoints`。

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

### 配置 appsettings.json {#configure-appsettingsjson}

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

| 键 | 说明 |
|---|---|
| `Issuer` | 身份验证服务器的公开 URL。用于令牌和 OIDC 发现。 |
| `Storage:ConnectionString` | Azure Table Storage 连接字符串。也可以改为设置 `Storage:TableServiceUri`，以托管标识进行身份验证；两者必须设置其一。 |
| `Clients` | 启动时预置的 OAuth 客户端数组。 |

### 扩展点 {#extensibility-points}

请在调用 `AddAuthagonal()` **之前**注册你的实现。Authagonal 使用 `TryAdd`，因此你的注册优先。`IAuthHook` 在性质上是个例外：你可以注册多个，它们全部都会运行；只有在你一个都没注册时，才会添加内置的空操作钩子。

| 接口 | 用途 | 默认值 |
|---|---|---|
| `IEmailService` | 发送验证邮件、密码重置邮件和账户已存在邮件 | 设置了 `Email:ResendApiKey` 时使用内置的 Resend 发送器；否则为空操作（静默丢弃） |
| `IAuthHook` | 对登录、注册和令牌事件进行拦截或审计 | 空操作 |
| `IProvisioningOrchestrator` | 在授权时将用户预配到下游应用 | TCC 预配 |
| `ISecretProvider` | 解析客户端密钥 | 明文（或在设置 `SecretProvider:VaultUri` 时使用 Key Vault） |

#### 示例：审计钩子 {#example-audit-hook}

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

该接口还有其他可选成员，它们带有空操作的默认实现（`OnMfaVerifyFailedAsync`、`OnEmailConfirmedAsync`、`OnMfaEnrolledAsync`、`OnMfaCredentialRemovedAsync`、`OnRecoveryCodesRegeneratedAsync`、`OnPasswordChangedAsync`、`OnTokenIssuingAsync`、`OnDelegationMintedAsync`、`OnApprovalRequestedAsync`、`OnApprovalResolvedAsync`、`OnAgentConsentChangedAsync`、`OnConsentRevokedAsync`、`OnCapabilityTicketRedeemedAsync`），只有在需要这些事件时才需重写。

#### 示例：邮件服务 {#example-email-service}

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

`IEmailService` 还有一个可选的 `SendAccountExistsEmailAsync(email, signInUrl, ct)`（默认为空操作），当有人用已有账户的地址注册时发送。

> **邮件是最常见的集成陷阱。**如果你既没有注册 `IEmailService`，也没有设置 `Email:ResendApiKey`，验证邮件和密码重置邮件会被静默丢弃；又因为“邮箱已确认才可登录”的限制默认开启，自助注册的用户将永远无法登录（`UseAuthagonal` 会在启动时发出警告）。配置了 `Email:ResendApiKey` + `Email:SenderEmail` 后，内置的 Resend 发送器会自动启用；在开发/测试环境中，`Auth:AutoConfirmEmailDomains` 可为列出的域名跳过验证。参见[配置 → 邮件](configuration#email)。

### 添加自定义端点 {#add-custom-endpoints}

你可以在 Authagonal 的端点之外添加自己的端点：

```csharp
app.MapGet("/custom/health", () => Results.Ok(new { status = "healthy" }));
```

### 禁用管理 API {#disable-admin-api}

对于面向公众的部署，请禁用管理端点：

```json
{
  "AdminApi": {
    "Enabled": false
  }
}
```

### 运行 {#run-it}

```bash
dotnet run
```

服务器会在配置的 URL 上启动，在 `/.well-known/openid-configuration` 提供 OIDC 发现文档，在 `/login` 提供登录界面，并提供所有身份验证/管理 API。

---

## 第 2 部分：自定义登录界面 {#part-2-custom-login-ui}

默认的登录 SPA 开箱即用，但你也可以用自己的 React 应用替换它，并从 `@authagonal/login` npm 包中导入组件和 API 客户端。

### 搭建前端脚手架 {#scaffold-the-frontend}

```bash
mkdir login-app && cd login-app
npm init -y
npm install react react-dom react-router @authagonal/login
npm install -D vite @vitejs/plugin-react typescript @types/react @types/react-dom
```

### npm 包导出的内容 {#what-the-npm-package-exports}

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

### 入口文件（main.tsx） {#entry-point-maintsx}

从服务器加载品牌配置，并用品牌上下文包裹你的应用：

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

### 路由（App.tsx） {#routing-apptsx}

将自定义页面与基础包中的页面混合使用。服务器会把用户引导到 `/login` 下的路径（授权端点重定向到 `/login?returnUrl=...`，邮件中的链接指向 `/login/reset-password`、`/login/consent`、`/login/device`），因此路由器必须使用 `basename="/login"`，下面的路由都相对于它：

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

如果你只想在现成应用的基础上添加页面，请改为渲染包中的 `<App extraRoutes={...} />`。它已经提供了上面的所有路由（外加 `/account`）。

### 自定义登录页面 {#custom-login-page}

使用 npm 包中的 API 客户端构建你自己的登录表单：

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

### 自定义布局 {#custom-layout}

包裹基础的 `AuthLayout`，添加你自己的品牌元素：

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

### 品牌定制（wwwroot/branding.json） {#branding-wwwrootbrandingjson}

无需重新构建即可配置登录界面的外观：

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

完整的架构说明，包括本地化的欢迎文本、语言选择器列表，以及按深色/浅色模式分别覆盖的颜色和徽标背景，参见[品牌定制](branding)页面。

### Vite 配置 {#vite-config}

在开发期间将 API 调用代理到后端：

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

### 构建与托管 {#build-and-serve}

在 `.csproj` 中添加一个构建目标，自动构建 SPA 并将其复制到 `wwwroot`：

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

现在 `dotnet build` 会同时构建 .NET 服务器和 React SPA，而 `dotnet run` 会从单个进程提供所有内容。
