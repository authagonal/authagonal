---
layout: default
title: Servidor personalizado
locale: es
---

# Inicio rápido de un servidor personalizado

Esta guía explica cómo alojar Authagonal como biblioteca en su propio proyecto de ASP.NET Core y, después, personalizar la interfaz de inicio de sesión con sus propios componentes de React.

## Parte 1: Configuración inicial del servidor {#part-1-server-setup}

### Crear el proyecto {#create-the-project}

```bash
dotnet new web -n MyAuthServer
cd MyAuthServer

# Add Authagonal packages (or project references for source builds)
dotnet add package Authagonal.Server
dotnet add package Authagonal.AzureProvider
```

Su `.csproj` debe contener:

```xml
<ItemGroup>
  <PackageReference Include="Authagonal.Server" Version="*" />
  <PackageReference Include="Authagonal.AzureProvider" Version="*" />
</ItemGroup>
```

`Authagonal.AzureProvider` aporta los almacenes de Azure Table Storage que `AddAuthagonal` configura a partir de la configuración `Storage:*`. Para alojarlo en AWS en su lugar, haga referencia a `Authagonal.AwsProvider` y llame a `AddAuthagonalAwsStorage(...)` antes de `AddAuthagonal`; consulte [Instalación → Backend de AWS](installation#aws-backend).

### Configurar Program.cs {#configure-programcs}

La configuración mínima consta de tres llamadas: `AddAuthagonal`, `UseAuthagonal` y `MapAuthagonalEndpoints`.

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

### Configurar appsettings.json {#configure-appsettingsjson}

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

| Clave | Descripción |
|---|---|
| `Issuer` | La URL pública de su servidor de autenticación. Se usa en los tokens y en el descubrimiento de OIDC. |
| `Storage:ConnectionString` | Cadena de conexión de Azure Table Storage. Como alternativa, establezca `Storage:TableServiceUri` para autenticarse con una identidad administrada; una de las dos es obligatoria. |
| `Clients` | Matriz de clientes OAuth que se cargan al iniciar. |

### Puntos de extensión {#extensibility-points}

Registre sus implementaciones **antes** de llamar a `AddAuthagonal()`; Authagonal usa `TryAdd`, así que sus registros prevalecen. `IAuthHook` es una excepción de otra naturaleza: puede registrar varios y todos se ejecutan, y el hook integrado sin efecto solo se añade cuando no registra ninguno.

| Interfaz | Propósito | Predeterminado |
|---|---|---|
| `IEmailService` | Enviar correos de verificación, de restablecimiento de contraseña y de cuenta ya existente | Remitente integrado de Resend cuando `Email:ResendApiKey` está establecido; en caso contrario, sin efecto (descarta sin aviso) |
| `IAuthHook` | Controlar o auditar los eventos de inicio de sesión, registro y tokens | Sin efecto |
| `IProvisioningOrchestrator` | Aprovisionar usuarios en aplicaciones descendentes en el momento de la autorización | Aprovisionamiento TCC |
| `ISecretProvider` | Resolver los secretos de cliente | Texto plano (o Key Vault con `SecretProvider:VaultUri`) |

#### Ejemplo: hook de auditoría {#example-audit-hook}

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

La interfaz tiene más miembros opcionales con implementaciones predeterminadas sin efecto (`OnMfaVerifyFailedAsync`, `OnEmailConfirmedAsync`, `OnMfaEnrolledAsync`, `OnMfaCredentialRemovedAsync`, `OnRecoveryCodesRegeneratedAsync`, `OnPasswordChangedAsync`, `OnTokenIssuingAsync`, `OnDelegationMintedAsync`, `OnApprovalRequestedAsync`, `OnApprovalResolvedAsync`, `OnAgentConsentChangedAsync`, `OnConsentRevokedAsync`, `OnCapabilityTicketRedeemedAsync`); sobrescríbalos solo si necesita esos eventos.

#### Ejemplo: servicio de correo electrónico {#example-email-service}

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

`IEmailService` también tiene un método opcional `SendAccountExistsEmailAsync(email, signInUrl, ct)` (sin efecto por defecto), que se envía cuando alguien registra una dirección que ya tiene una cuenta.

> **El correo electrónico es la trampa de integración más habitual.** Si no registra ningún `IEmailService` ni establece `Email:ResendApiKey`, los correos de verificación y de restablecimiento de contraseña se descartan sin aviso y, como el requisito de correo confirmado para iniciar sesión está activado por defecto, los usuarios autorregistrados nunca podrán iniciar sesión (`UseAuthagonal` lo advierte al iniciar). El remitente integrado de Resend se activa automáticamente cuando se configuran `Email:ResendApiKey` + `Email:SenderEmail`; para desarrollo y pruebas, `Auth:AutoConfirmEmailDomains` omite la verificación para los dominios indicados. Consulte [Configuración → Correo electrónico](configuration#email).

### Añadir endpoints personalizados {#add-custom-endpoints}

Puede añadir sus propios endpoints junto a los de Authagonal:

```csharp
app.MapGet("/custom/health", () => Results.Ok(new { status = "healthy" }));
```

### Desactivar la API de administración {#disable-admin-api}

En los despliegues expuestos al público, desactive los endpoints de administración:

```json
{
  "AdminApi": {
    "Enabled": false
  }
}
```

### Ejecutarlo {#run-it}

```bash
dotnet run
```

El servidor se inicia en la URL configurada y sirve el documento de descubrimiento de OIDC en `/.well-known/openid-configuration`, la interfaz de inicio de sesión en `/login` y todas las API de autenticación y administración.

---

## Parte 2: Interfaz de inicio de sesión personalizada {#part-2-custom-login-ui}

La SPA de inicio de sesión predeterminada funciona de serie, pero puede sustituirla por su propia aplicación React que importe componentes y clientes de API del paquete npm `@authagonal/login`.

### Generar la estructura del frontend {#scaffold-the-frontend}

```bash
mkdir login-app && cd login-app
npm init -y
npm install react react-dom react-router @authagonal/login
npm install -D vite @vitejs/plugin-react typescript @types/react @types/react-dom
```

### Qué exporta el paquete npm {#what-the-npm-package-exports}

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

### Punto de entrada (main.tsx) {#entry-point-maintsx}

Cargue la personalización de marca desde el servidor y envuelva su aplicación en el contexto de marca:

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

### Enrutamiento (App.tsx) {#routing-apptsx}

Combine páginas personalizadas con las páginas del paquete base. El servidor envía a los usuarios a rutas bajo `/login` (el endpoint de autorización redirige a `/login?returnUrl=...`, y los correos enlazan a `/login/reset-password`, `/login/consent`, `/login/device`), por lo que el enrutador debe usar `basename="/login"` y las rutas siguientes son relativas a él:

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

Si solo quiere añadir páginas a la aplicación estándar, renderice en su lugar `<App extraRoutes={...} />` desde el paquete. Ya proporciona todas las rutas anteriores (además de `/account`).

### Página de inicio de sesión personalizada {#custom-login-page}

Construya su propio formulario de inicio de sesión con los clientes de API del paquete npm:

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

### Diseño personalizado {#custom-layout}

Envuelva el `AuthLayout` base para añadir su propia marca:

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

### Personalización de marca (wwwroot/branding.json) {#branding-wwwrootbrandingjson}

Configure la apariencia de la interfaz de inicio de sesión sin recompilar:

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

El esquema completo, que incluye el texto de bienvenida localizado, la lista del selector de idioma y las sustituciones de color y de fondo del logotipo para los modos oscuro y claro, está en la página de [Personalización de marca](branding).

### Configuración de Vite {#vite-config}

Redirija las llamadas a la API al backend mediante un proxy durante el desarrollo:

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

### Compilar y servir {#build-and-serve}

Añada un destino de compilación a su `.csproj` para compilar automáticamente la SPA y copiarla en `wwwroot`:

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

Ahora `dotnet build` compila tanto el servidor .NET como la SPA de React, y `dotnet run` lo sirve todo desde un único proceso.
