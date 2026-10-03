---
layout: default
title: Serveur personnalisé
locale: fr
---

# Démarrage rapide d'un serveur personnalisé

Ce guide explique comment héberger Authagonal comme bibliothèque dans votre propre projet ASP.NET Core, puis comment personnaliser l'interface de connexion avec vos propres composants React.

## Partie 1 : mise en place du serveur {#part-1-server-setup}

### Créer le projet {#create-the-project}

```bash
dotnet new web -n MyAuthServer
cd MyAuthServer

# Add Authagonal packages (or project references for source builds)
dotnet add package Authagonal.Server
dotnet add package Authagonal.AzureProvider
```

Votre `.csproj` doit contenir :

```xml
<ItemGroup>
  <PackageReference Include="Authagonal.Server" Version="*" />
  <PackageReference Include="Authagonal.AzureProvider" Version="*" />
</ItemGroup>
```

`Authagonal.AzureProvider` fournit les stores Azure Table Storage que `AddAuthagonal` branche à partir de la configuration `Storage:*`. Pour héberger sur AWS à la place, référencez `Authagonal.AwsProvider` et appelez `AddAuthagonalAwsStorage(...)` avant `AddAuthagonal` : voir [Installation → Backend AWS](installation#aws-backend).

### Configurer Program.cs {#configure-programcs}

La mise en place minimale tient en trois appels : `AddAuthagonal`, `UseAuthagonal` et `MapAuthagonalEndpoints`.

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

### Configurer appsettings.json {#configure-appsettingsjson}

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

| Clé | Description |
|---|---|
| `Issuer` | L'URL publique de votre serveur d'authentification. Utilisée dans les jetons et dans la découverte OIDC. |
| `Storage:ConnectionString` | Chaîne de connexion Azure Table Storage. Vous pouvez aussi définir `Storage:TableServiceUri` pour vous authentifier avec une identité managée ; l'un des deux est obligatoire. |
| `Clients` | Tableau des clients OAuth initialisés au démarrage. |

### Points d'extension {#extensibility-points}

Enregistrez vos implémentations **avant** d'appeler `AddAuthagonal()` : Authagonal utilise `TryAdd`, donc vos enregistrements l'emportent. `IAuthHook` fait exception par nature : vous pouvez en enregistrer plusieurs et tous s'exécutent, et le hook intégré qui ne fait rien n'est ajouté que si vous n'en enregistrez aucun.

| Interface | Rôle | Par défaut |
|---|---|---|
| `IEmailService` | Envoyer les e-mails de vérification, de réinitialisation du mot de passe et d'avis de compte existant | Expéditeur Resend intégré lorsque `Email:ResendApiKey` est défini ; sinon aucune action (les messages sont ignorés sans avertissement) |
| `IAuthHook` | Contrôler ou auditer les événements de connexion, d'inscription et de jeton | Aucune action |
| `IProvisioningOrchestrator` | Provisionner les utilisateurs dans les applications en aval au moment de l'autorisation | Provisionnement TCC |
| `ISecretProvider` | Résoudre les secrets des clients | Texte en clair (ou Key Vault avec `SecretProvider:VaultUri`) |

#### Exemple : hook d'audit {#example-audit-hook}

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

L'interface comporte d'autres membres optionnels dont l'implémentation par défaut ne fait rien (`OnMfaVerifyFailedAsync`, `OnEmailConfirmedAsync`, `OnMfaEnrolledAsync`, `OnMfaCredentialRemovedAsync`, `OnRecoveryCodesRegeneratedAsync`, `OnPasswordChangedAsync`, `OnTokenIssuingAsync`, `OnDelegationMintedAsync`, `OnApprovalRequestedAsync`, `OnApprovalResolvedAsync`, `OnAgentConsentChangedAsync`, `OnConsentRevokedAsync`, `OnCapabilityTicketRedeemedAsync`) ; ne les surchargez que si vous avez besoin de ces événements.

#### Exemple : service d'e-mail {#example-email-service}

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

`IEmailService` dispose aussi d'une méthode optionnelle `SendAccountExistsEmailAsync(email, signInUrl, ct)` (qui ne fait rien par défaut), appelée lorsque quelqu'un s'inscrit avec une adresse qui possède déjà un compte.

> **L'e-mail est le piège d'intégration le plus courant.** Si vous n'enregistrez aucun `IEmailService` et ne définissez pas `Email:ResendApiKey`, les e-mails de vérification et de réinitialisation du mot de passe sont ignorés sans avertissement ; et comme l'exigence d'un e-mail confirmé pour se connecter est activée par défaut, les utilisateurs inscrits par eux-mêmes ne peuvent jamais se connecter (`UseAuthagonal` émet un avertissement au démarrage). L'expéditeur Resend intégré s'active automatiquement lorsque `Email:ResendApiKey` + `Email:SenderEmail` sont configurés ; en développement et en test, `Auth:AutoConfirmEmailDomains` saute la vérification pour les domaines listés. Voir [Configuration → E-mail](configuration#email).

### Ajouter des endpoints personnalisés {#add-custom-endpoints}

Vous pouvez ajouter vos propres endpoints à côté de ceux d'Authagonal :

```csharp
app.MapGet("/custom/health", () => Results.Ok(new { status = "healthy" }));
```

### Désactiver l'API d'administration {#disable-admin-api}

Pour les déploiements exposés au public, désactivez les endpoints d'administration :

```json
{
  "AdminApi": {
    "Enabled": false
  }
}
```

### L'exécuter {#run-it}

```bash
dotnet run
```

Le serveur démarre sur l'URL configurée et sert le document de découverte OIDC sur `/.well-known/openid-configuration`, l'interface de connexion sur `/login`, ainsi que toutes les API d'authentification et d'administration.

---

## Partie 2 : interface de connexion personnalisée {#part-2-custom-login-ui}

La SPA de connexion par défaut fonctionne telle quelle, mais vous pouvez la remplacer par votre propre application React, qui importe des composants et des clients d'API depuis le paquet npm `@authagonal/login`.

### Créer la structure du frontend {#scaffold-the-frontend}

```bash
mkdir login-app && cd login-app
npm init -y
npm install react react-dom react-router @authagonal/login
npm install -D vite @vitejs/plugin-react typescript @types/react @types/react-dom
```

### Ce qu'exporte le paquet npm {#what-the-npm-package-exports}

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

### Point d'entrée (main.tsx) {#entry-point-maintsx}

Chargez la personnalisation depuis le serveur et enveloppez votre application dans le contexte de personnalisation :

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

### Routage (App.tsx) {#routing-apptsx}

Combinez des pages personnalisées avec les pages de base du paquet. Le serveur envoie les utilisateurs vers des chemins sous `/login` (l'endpoint d'autorisation redirige vers `/login?returnUrl=...`, et les e-mails pointent vers `/login/reset-password`, `/login/consent`, `/login/device`) ; le routeur doit donc utiliser `basename="/login"`, et les routes ci-dessous sont relatives à ce chemin :

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

Si vous voulez seulement ajouter des pages à l'application standard, rendez plutôt `<App extraRoutes={...} />` depuis le paquet. Elle fournit déjà toutes les routes ci-dessus (plus `/account`).

### Page de connexion personnalisée {#custom-login-page}

Construisez votre propre formulaire de connexion avec les clients d'API du paquet npm :

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

### Mise en page personnalisée {#custom-layout}

Enveloppez le `AuthLayout` de base pour ajouter votre propre identité visuelle :

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

### Personnalisation (wwwroot/branding.json) {#branding-wwwrootbrandingjson}

Configurez l'apparence de l'interface de connexion sans recompiler :

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

Le schéma complet, y compris le texte d'accueil traduit, la liste du sélecteur de langue et les surcharges de couleurs et de fond de logo propres aux modes sombre et clair, est décrit sur la page [Personnalisation de l'interface](branding).

### Configuration de Vite {#vite-config}

Redirigez les appels d'API vers le backend pendant le développement :

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

### Compiler et servir {#build-and-serve}

Ajoutez une cible de build à votre `.csproj` pour compiler automatiquement la SPA et la copier dans `wwwroot` :

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

Désormais, `dotnet build` compile à la fois le serveur .NET et la SPA React, et `dotnet run` sert l'ensemble depuis un seul processus.
