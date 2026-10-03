---
layout: default
title: Custom Server
---

# Custom Server Quick Start

This guide walks through hosting Authagonal as a library in your own ASP.NET Core project, then customizing the login UI with your own React components.

## Part 1: Server Setup

### Create the project

```bash
dotnet new web -n MyAuthServer
cd MyAuthServer

# Add Authagonal packages (or project references for source builds)
dotnet add package Authagonal.Server
dotnet add package Authagonal.AzureProvider
```

Your `.csproj` should contain:

```xml
<ItemGroup>
  <PackageReference Include="Authagonal.Server" Version="*" />
  <PackageReference Include="Authagonal.AzureProvider" Version="*" />
</ItemGroup>
```

`Authagonal.AzureProvider` supplies the Azure Table Storage stores that `AddAuthagonal` wires up from the `Storage:*` configuration. To host on AWS instead, reference `Authagonal.AwsProvider` and call `AddAuthagonalAwsStorage(...)` before `AddAuthagonal`, see [Installation → AWS backend](installation#aws-backend).

### Configure Program.cs

The minimum setup is three calls: `AddAuthagonal`, `UseAuthagonal`, and `MapAuthagonalEndpoints`.

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

### Configure appsettings.json

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

| Key | Description |
|---|---|
| `Issuer` | The public URL of your auth server. Used in tokens and OIDC discovery. |
| `Storage:ConnectionString` | Azure Table Storage connection string. Alternatively set `Storage:TableServiceUri` to authenticate with a managed identity; one of the two is required. |
| `Clients` | Array of OAuth clients seeded on startup. |

### Extensibility points

Register your implementations **before** calling `AddAuthagonal()`, Authagonal uses `TryAdd`, so your registrations win. `IAuthHook` is the exception in kind: you can register several and all of them run, and the built-in no-op hook is added only when you register none.

| Interface | Purpose | Default |
|---|---|---|
| `IEmailService` | Send verification, password reset and account-exists emails | Built-in Resend sender when `Email:ResendApiKey` is set; otherwise no-op (silently discards) |
| `IAuthHook` | Gate or audit login, registration, and token events | No-op |
| `IProvisioningOrchestrator` | Provision users into downstream apps at authorize time | TCC provisioning |
| `ISecretProvider` | Resolve client secrets | Plaintext (or Key Vault with `SecretProvider:VaultUri`) |

#### Example: audit hook

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

The interface has further optional members with no-op default implementations (`OnMfaVerifyFailedAsync`, `OnEmailConfirmedAsync`, `OnMfaEnrolledAsync`, `OnMfaCredentialRemovedAsync`, `OnRecoveryCodesRegeneratedAsync`, `OnPasswordChangedAsync`, `OnTokenIssuingAsync`, `OnDelegationMintedAsync`, `OnApprovalRequestedAsync`, `OnApprovalResolvedAsync`, `OnAgentConsentChangedAsync`, `OnConsentRevokedAsync`, `OnCapabilityTicketRedeemedAsync`), override them only if you need those events.

#### Example: email service

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

`IEmailService` also has an optional `SendAccountExistsEmailAsync(email, signInUrl, ct)` (a no-op by default), sent when someone registers an address that already has an account.

> **Email is the most common integration trap.** If you register no `IEmailService` and don't set `Email:ResendApiKey`, verification and password-reset mails are silently discarded, and because the confirmed-email login gate defaults to on, self-registered users can never log in (`UseAuthagonal` warns at startup). The built-in Resend sender activates automatically when `Email:ResendApiKey` + `Email:SenderEmail` are configured; for dev/test, `Auth:AutoConfirmEmailDomains` skips verification for listed domains. See [Configuration → Email](configuration#email).

### Add custom endpoints

You can add your own endpoints alongside Authagonal's:

```csharp
app.MapGet("/custom/health", () => Results.Ok(new { status = "healthy" }));
```

### Disable admin API

For public-facing deployments, disable the admin endpoints:

```json
{
  "AdminApi": {
    "Enabled": false
  }
}
```

### Run it

```bash
dotnet run
```

The server starts on the configured URL, serving the OIDC discovery document at `/.well-known/openid-configuration`, the login UI at `/login`, and all auth/admin APIs.

---

## Part 2: Custom Login UI

The default login SPA works out of the box, but you can replace it with your own React app that imports components and API clients from the `@authagonal/login` npm package.

### Scaffold the frontend

```bash
mkdir login-app && cd login-app
npm init -y
npm install react react-dom react-router @authagonal/login
npm install -D vite @vitejs/plugin-react typescript @types/react @types/react-dom
```

### What the npm package exports

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

### Entry point (main.tsx)

Load branding from the server and wrap your app in the branding context:

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

### Routing (App.tsx)

Mix custom pages with the base package pages. The server sends users to paths under `/login` (the authorize endpoint redirects to `/login?returnUrl=...`, and emails link to `/login/reset-password`, `/login/consent`, `/login/device`), so the router must use `basename="/login"` and the routes below are relative to it:

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

If you only want to add pages to the stock app, render `<App extraRoutes={...} />` from the package instead. It already provides every route above (plus `/account`).

### Custom login page

Build your own login form using the API clients from the npm package:

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

### Custom layout

Wrap the base `AuthLayout` to add your own branding:

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

### Branding (wwwroot/branding.json)

Configure the login UI appearance without rebuilding:

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

The full schema, including localized welcome text, the language picker list, and per-mode dark/light colour and logo-background overrides, is on the [Branding](branding) page.

### Vite config

Proxy API calls to the backend during development:

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

### Build and serve

Add a build target to your `.csproj` to automatically build the SPA and copy it to `wwwroot`:

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

Now `dotnet build` builds both the .NET server and the React SPA, and `dotnet run` serves everything from a single process.
