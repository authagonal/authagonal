---
layout: default
title: カスタムサーバー
locale: ja
---

# カスタムサーバーのクイックスタート

このガイドでは、Authagonal を自分の ASP.NET Core プロジェクト内でライブラリとしてホストし、ログイン UI を独自の React コンポーネントでカスタマイズする手順を説明します。

## パート 1: サーバーのセットアップ {#part-1-server-setup}

### プロジェクトを作成する {#create-the-project}

```bash
dotnet new web -n MyAuthServer
cd MyAuthServer

# Add Authagonal packages (or project references for source builds)
dotnet add package Authagonal.Server
dotnet add package Authagonal.AzureProvider
```

`.csproj` には次の内容が含まれているはずです。

```xml
<ItemGroup>
  <PackageReference Include="Authagonal.Server" Version="*" />
  <PackageReference Include="Authagonal.AzureProvider" Version="*" />
</ItemGroup>
```

`Authagonal.AzureProvider` は、`AddAuthagonal` が `Storage:*` の構成から組み込む Azure Table Storage のストアを提供します。代わりに AWS でホストするには、`Authagonal.AwsProvider` を参照し、`AddAuthagonal` より前に `AddAuthagonalAwsStorage(...)` を呼び出してください。[インストール → AWS バックエンド](installation#aws-backend) を参照してください。

### Program.cs を構成する {#configure-programcs}

最小限のセットアップは 3 つの呼び出しです。`AddAuthagonal`、`UseAuthagonal`、`MapAuthagonalEndpoints` です。

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

### appsettings.json を構成する {#configure-appsettingsjson}

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

| キー | 説明 |
|---|---|
| `Issuer` | 認証サーバーの公開 URL。トークンと OIDC ディスカバリーで使われます。 |
| `Storage:ConnectionString` | Azure Table Storage の接続文字列。代わりに `Storage:TableServiceUri` を設定すると、マネージド ID で認証できます。どちらか一方が必須です。 |
| `Clients` | 起動時に初期投入される OAuth クライアントの配列。 |

### 拡張点 {#extensibility-points}

独自の実装は `AddAuthagonal()` を呼び出す**前**に登録してください。Authagonal は `TryAdd` を使うため、あなたの登録が優先されます。`IAuthHook` は性質の異なる例外です。複数登録でき、そのすべてが実行されます。組み込みの何もしないフックは、1 つも登録しなかった場合にのみ追加されます。

| インターフェイス | 目的 | 既定 |
|---|---|---|
| `IEmailService` | 確認、パスワードリセット、アカウント既存通知のメールを送信する | `Email:ResendApiKey` が設定されていれば組み込みの Resend 送信機能。それ以外は何もしない (何も通知せずに破棄する) |
| `IAuthHook` | ログイン、登録、トークンのイベントを制御または監査する | 何もしない |
| `IProvisioningOrchestrator` | 認可時に下流アプリへユーザーをプロビジョニングする | TCC プロビジョニング |
| `ISecretProvider` | クライアントシークレットを解決する | 平文 (または `SecretProvider:VaultUri` を指定した場合は Key Vault) |

#### 例: 監査フック {#example-audit-hook}

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

このインターフェイスには、既定で何もしない実装を持つオプションのメンバーがほかにもあります (`OnMfaVerifyFailedAsync`、`OnEmailConfirmedAsync`、`OnMfaEnrolledAsync`、`OnMfaCredentialRemovedAsync`、`OnRecoveryCodesRegeneratedAsync`、`OnPasswordChangedAsync`、`OnTokenIssuingAsync`、`OnDelegationMintedAsync`、`OnApprovalRequestedAsync`、`OnApprovalResolvedAsync`、`OnAgentConsentChangedAsync`、`OnConsentRevokedAsync`、`OnCapabilityTicketRedeemedAsync`)。これらのイベントが必要な場合にだけオーバーライドしてください。

#### 例: メールサービス {#example-email-service}

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

`IEmailService` には、オプションの `SendAccountExistsEmailAsync(email, signInUrl, ct)` (既定では何もしない) もあります。これは、既にアカウントが存在するアドレスで誰かが登録しようとしたときに送信されます。

> **メールは最もよくある統合の落とし穴です。** `IEmailService` を登録せず、`Email:ResendApiKey` も設定しない場合、確認メールとパスワードリセットのメールは何も通知されずに破棄されます。また、確認済みメールアドレスによるログインの制限は既定で有効なため、セルフ登録したユーザーはいつまでもログインできません (`UseAuthagonal` は起動時に警告を出します)。組み込みの Resend 送信機能は、`Email:ResendApiKey` と `Email:SenderEmail` が構成されると自動的に有効になります。開発/テストでは、`Auth:AutoConfirmEmailDomains` に列挙したドメインについて確認を省略できます。[構成 → メール](configuration#email) を参照してください。

### 独自のエンドポイントを追加する {#add-custom-endpoints}

Authagonal のエンドポイントと並べて、独自のエンドポイントを追加できます。

```csharp
app.MapGet("/custom/health", () => Results.Ok(new { status = "healthy" }));
```

### 管理 API を無効にする {#disable-admin-api}

一般公開するデプロイメントでは、管理エンドポイントを無効にしてください。

```json
{
  "AdminApi": {
    "Enabled": false
  }
}
```

### 実行する {#run-it}

```bash
dotnet run
```

サーバーは構成された URL で起動し、`/.well-known/openid-configuration` で OIDC ディスカバリードキュメントを、`/login` でログイン UI を、そしてすべての認証/管理 API を提供します。

---

## パート 2: カスタムログイン UI {#part-2-custom-login-ui}

既定のログイン SPA はそのままで動作しますが、`@authagonal/login` npm パッケージからコンポーネントと API クライアントをインポートする独自の React アプリに置き換えることもできます。

### フロントエンドの雛形を作成する {#scaffold-the-frontend}

```bash
mkdir login-app && cd login-app
npm init -y
npm install react react-dom react-router @authagonal/login
npm install -D vite @vitejs/plugin-react typescript @types/react @types/react-dom
```

### npm パッケージがエクスポートするもの {#what-the-npm-package-exports}

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

### エントリーポイント (main.tsx) {#entry-point-maintsx}

サーバーからブランディングを読み込み、アプリをブランディングのコンテキストでラップします。

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

### ルーティング (App.tsx) {#routing-apptsx}

独自のページとベースパッケージのページを組み合わせます。サーバーはユーザーを `/login` 配下のパスに送ります (認可エンドポイントは `/login?returnUrl=...` にリダイレクトし、メールは `/login/reset-password`、`/login/consent`、`/login/device` にリンクします)。そのため、ルーターは `basename="/login"` を使う必要があり、以下のルートはそこからの相対パスです。

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

標準のアプリにページを追加したいだけなら、代わりにパッケージの `<App extraRoutes={...} />` を描画してください。このコンポーネントは上記のすべてのルート (と `/account`) を既に備えています。

### カスタムログインページ {#custom-login-page}

npm パッケージの API クライアントを使って、独自のログインフォームを構築します。

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

### カスタムレイアウト {#custom-layout}

ベースの `AuthLayout` をラップして、独自のブランディングを追加します。

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

### ブランディング (wwwroot/branding.json) {#branding-wwwrootbrandingjson}

再ビルドせずにログイン UI の外観を構成します。

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

ローカライズされたウェルカムテキスト、言語選択メニューのリスト、ダーク/ライトのモードごとの色とロゴ背景の上書きを含む完全なスキーマは、[ブランディング](branding) ページにあります。

### Vite の構成 {#vite-config}

開発中は API の呼び出しをバックエンドにプロキシします。

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

### ビルドと配信 {#build-and-serve}

`.csproj` にビルドターゲットを追加して、SPA を自動的にビルドし `wwwroot` にコピーするようにします。

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

これで、`dotnet build` は .NET サーバーと React SPA の両方をビルドし、`dotnet run` は単一のプロセスからすべてを配信します。
