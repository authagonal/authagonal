---
layout: default
title: Eigener Server
locale: de
---

# Schnellstart: eigener Server

Diese Anleitung zeigt, wie Sie Authagonal als Bibliothek in Ihrem eigenen ASP.NET-Core-Projekt hosten und anschließend die Login-UI mit eigenen React-Komponenten anpassen.

## Teil 1: Einrichtung des Servers {#part-1-server-setup}

### Projekt anlegen {#create-the-project}

```bash
dotnet new web -n MyAuthServer
cd MyAuthServer

# Add Authagonal packages (or project references for source builds)
dotnet add package Authagonal.Server
dotnet add package Authagonal.AzureProvider
```

Ihre `.csproj` sollte Folgendes enthalten:

```xml
<ItemGroup>
  <PackageReference Include="Authagonal.Server" Version="*" />
  <PackageReference Include="Authagonal.AzureProvider" Version="*" />
</ItemGroup>
```

`Authagonal.AzureProvider` liefert die Stores für Azure Table Storage, die `AddAuthagonal` anhand der `Storage:*`-Konfiguration verdrahtet. Um stattdessen auf AWS zu hosten, referenzieren Sie `Authagonal.AwsProvider` und rufen `AddAuthagonalAwsStorage(...)` vor `AddAuthagonal` auf; siehe [Installation → AWS-Backend](installation#aws-backend).

### Program.cs konfigurieren {#configure-programcs}

Die minimale Einrichtung besteht aus drei Aufrufen: `AddAuthagonal`, `UseAuthagonal` und `MapAuthagonalEndpoints`.

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

### appsettings.json konfigurieren {#configure-appsettingsjson}

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

| Schlüssel | Beschreibung |
|---|---|
| `Issuer` | Die öffentliche URL Ihres Auth-Servers. Wird in Tokens und in der OIDC-Discovery verwendet. |
| `Storage:ConnectionString` | Connection String für Azure Table Storage. Alternativ setzen Sie `Storage:TableServiceUri`, um sich mit einer Managed Identity zu authentifizieren; eines von beiden ist erforderlich. |
| `Clients` | Array von OAuth-Clients, die beim Start angelegt werden. |

### Erweiterungspunkte {#extensibility-points}

Registrieren Sie Ihre Implementierungen **vor** dem Aufruf von `AddAuthagonal()`. Authagonal verwendet `TryAdd`, daher haben Ihre Registrierungen Vorrang. `IAuthHook` ist grundsätzlich anders gelagert: Sie können mehrere registrieren, und alle werden ausgeführt; der eingebaute wirkungslose Hook wird nur hinzugefügt, wenn Sie keinen registrieren.

| Schnittstelle | Zweck | Standard |
|---|---|---|
| `IEmailService` | Versand von Verifizierungs-, Passwort-Reset- und „Konto existiert bereits“-E-Mails | Eingebauter Resend-Versand, wenn `Email:ResendApiKey` gesetzt ist; andernfalls wirkungslos (verwirft stillschweigend) |
| `IAuthHook` | Anmelde-, Registrierungs- und Token-Ereignisse zulassen bzw. sperren oder protokollieren | Wirkungslos |
| `IProvisioningOrchestrator` | Benutzer zum Zeitpunkt der Autorisierung in nachgelagerte Anwendungen provisionieren | TCC-Provisionierung |
| `ISecretProvider` | Client-Secrets auflösen | Klartext (oder Key Vault mit `SecretProvider:VaultUri`) |

#### Beispiel: Audit-Hook {#example-audit-hook}

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

Die Schnittstelle hat weitere optionale Member mit wirkungslosen Standardimplementierungen (`OnMfaVerifyFailedAsync`, `OnEmailConfirmedAsync`, `OnMfaEnrolledAsync`, `OnMfaCredentialRemovedAsync`, `OnRecoveryCodesRegeneratedAsync`, `OnPasswordChangedAsync`, `OnTokenIssuingAsync`, `OnDelegationMintedAsync`, `OnApprovalRequestedAsync`, `OnApprovalResolvedAsync`, `OnAgentConsentChangedAsync`, `OnConsentRevokedAsync`, `OnCapabilityTicketRedeemedAsync`); überschreiben Sie sie nur, wenn Sie diese Ereignisse benötigen.

#### Beispiel: E-Mail-Service {#example-email-service}

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

`IEmailService` hat außerdem ein optionales `SendAccountExistsEmailAsync(email, signInUrl, ct)` (standardmäßig wirkungslos), das gesendet wird, wenn sich jemand mit einer Adresse registriert, zu der bereits ein Konto existiert.

> **E-Mail ist die häufigste Falle bei der Integration.** Wenn Sie kein `IEmailService` registrieren und `Email:ResendApiKey` nicht setzen, werden Verifizierungs- und Passwort-Reset-Mails stillschweigend verworfen. Weil die Anmeldesperre für unbestätigte E-Mail-Adressen standardmäßig aktiv ist, können sich selbst registrierte Benutzer dann nie anmelden (`UseAuthagonal` warnt beim Start). Der eingebaute Resend-Versand wird automatisch aktiv, sobald `Email:ResendApiKey` + `Email:SenderEmail` konfiguriert sind; für Entwicklung und Tests überspringt `Auth:AutoConfirmEmailDomains` die Verifizierung für die aufgeführten Domains. Siehe [Konfiguration → E-Mail](configuration#email).

### Eigene Endpunkte hinzufügen {#add-custom-endpoints}

Sie können neben denen von Authagonal eigene Endpunkte hinzufügen:

```csharp
app.MapGet("/custom/health", () => Results.Ok(new { status = "healthy" }));
```

### Admin-API deaktivieren {#disable-admin-api}

Deaktivieren Sie bei öffentlich erreichbaren Deployments die Admin-Endpunkte:

```json
{
  "AdminApi": {
    "Enabled": false
  }
}
```

### Starten {#run-it}

```bash
dotnet run
```

Der Server startet unter der konfigurierten URL und stellt das OIDC-Discovery-Dokument unter `/.well-known/openid-configuration`, die Login-UI unter `/login` sowie alle Auth- und Admin-APIs bereit.

---

## Teil 2: Eigene Login-UI {#part-2-custom-login-ui}

Die Standard-Login-SPA funktioniert ohne weiteres Zutun, Sie können sie aber durch Ihre eigene React-App ersetzen, die Komponenten und API-Clients aus dem npm-Paket `@authagonal/login` importiert.

### Frontend-Gerüst anlegen {#scaffold-the-frontend}

```bash
mkdir login-app && cd login-app
npm init -y
npm install react react-dom react-router @authagonal/login
npm install -D vite @vitejs/plugin-react typescript @types/react @types/react-dom
```

### Was das npm-Paket exportiert {#what-the-npm-package-exports}

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

### Einstiegspunkt (main.tsx) {#entry-point-maintsx}

Laden Sie das Branding vom Server und umschließen Sie Ihre App mit dem Branding-Kontext:

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

### Routing (App.tsx) {#routing-apptsx}

Kombinieren Sie eigene Seiten mit den Seiten des Basispakets. Der Server schickt Benutzer auf Pfade unterhalb von `/login` (der Autorisierungsendpunkt leitet auf `/login?returnUrl=...` weiter, und E-Mails verlinken auf `/login/reset-password`, `/login/consent`, `/login/device`). Der Router muss daher `basename="/login"` verwenden, und die folgenden Routen sind relativ dazu:

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

Wenn Sie der Standard-App nur Seiten hinzufügen möchten, rendern Sie stattdessen `<App extraRoutes={...} />` aus dem Paket. Es stellt bereits jede der obigen Routen bereit (zuzüglich `/account`).

### Eigene Anmeldeseite {#custom-login-page}

Bauen Sie Ihr eigenes Anmeldeformular mit den API-Clients aus dem npm-Paket:

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

### Eigenes Layout {#custom-layout}

Umschließen Sie das Basis-`AuthLayout`, um Ihr eigenes Branding hinzuzufügen:

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

### Branding (wwwroot/branding.json) {#branding-wwwrootbrandingjson}

Konfigurieren Sie das Erscheinungsbild der Login-UI ohne Neubau:

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

Das vollständige Schema, einschließlich lokalisierter Begrüßungstexte, der Liste für die Sprachauswahl und modusabhängiger Überschreibungen von Farben und Logo-Hintergrund für den dunklen und hellen Modus, finden Sie auf der Seite [Branding](branding).

### Vite-Konfiguration {#vite-config}

Leiten Sie API-Aufrufe während der Entwicklung per Proxy an das Backend weiter:

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

### Bauen und bereitstellen {#build-and-serve}

Fügen Sie Ihrer `.csproj` ein Build-Target hinzu, das die SPA automatisch baut und nach `wwwroot` kopiert:

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

Jetzt baut `dotnet build` sowohl den .NET-Server als auch die React-SPA, und `dotnet run` stellt alles aus einem einzigen Prozess bereit.
