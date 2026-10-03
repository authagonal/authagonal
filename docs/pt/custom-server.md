---
layout: default
title: Servidor personalizado
locale: pt
---

# Início rápido do servidor personalizado

Este guia mostra como alojar a Authagonal como biblioteca no seu próprio projeto ASP.NET Core e, em seguida, personalizar a interface de início de sessão com os seus próprios componentes React.

## Parte 1: Preparação do servidor {#part-1-server-setup}

### Criar o projeto {#create-the-project}

```bash
dotnet new web -n MyAuthServer
cd MyAuthServer

# Add Authagonal packages (or project references for source builds)
dotnet add package Authagonal.Server
dotnet add package Authagonal.AzureProvider
```

O seu `.csproj` deve conter:

```xml
<ItemGroup>
  <PackageReference Include="Authagonal.Server" Version="*" />
  <PackageReference Include="Authagonal.AzureProvider" Version="*" />
</ItemGroup>
```

`Authagonal.AzureProvider` fornece os armazenamentos do Azure Table Storage que `AddAuthagonal` liga a partir da configuração `Storage:*`. Para alojar na AWS, referencie antes `Authagonal.AwsProvider` e chame `AddAuthagonalAwsStorage(...)` antes de `AddAuthagonal`; consulte [Instalação → Backend AWS](installation#aws-backend).

### Configurar o Program.cs {#configure-programcs}

A preparação mínima consiste em três chamadas: `AddAuthagonal`, `UseAuthagonal` e `MapAuthagonalEndpoints`.

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

### Configurar o appsettings.json {#configure-appsettingsjson}

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

| Chave | Descrição |
|---|---|
| `Issuer` | O URL público do seu servidor de autenticação. Utilizado nos tokens e na descoberta OIDC. |
| `Storage:ConnectionString` | Cadeia de ligação do Azure Table Storage. Em alternativa, defina `Storage:TableServiceUri` para se autenticar com uma identidade gerida; uma das duas é obrigatória. |
| `Clients` | Matriz de clientes OAuth inicializados no arranque. |

### Pontos de extensibilidade {#extensibility-points}

Registe as suas implementações **antes** de chamar `AddAuthagonal()`: a Authagonal utiliza `TryAdd`, pelo que os seus registos prevalecem. `IAuthHook` é uma exceção de natureza diferente: pode registar vários e todos são executados, e o hook integrado sem efeito só é adicionado quando não registar nenhum.

| Interface | Finalidade | Predefinição |
|---|---|---|
| `IEmailService` | Enviar emails de verificação, de redefinição da palavra-passe e de conta já existente | Remetente Resend integrado quando `Email:ResendApiKey` está definido; caso contrário, sem efeito (descarta sem aviso) |
| `IAuthHook` | Controlar ou auditar eventos de início de sessão, de registo e de tokens | Sem efeito |
| `IProvisioningOrchestrator` | Aprovisionar utilizadores em aplicações a jusante no momento da autorização | Aprovisionamento TCC |
| `ISecretProvider` | Resolver segredos de cliente | Texto simples (ou Key Vault com `SecretProvider:VaultUri`) |

#### Exemplo: hook de auditoria {#example-audit-hook}

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

A interface tem outros membros opcionais com implementações predefinidas sem efeito (`OnMfaVerifyFailedAsync`, `OnEmailConfirmedAsync`, `OnMfaEnrolledAsync`, `OnMfaCredentialRemovedAsync`, `OnRecoveryCodesRegeneratedAsync`, `OnPasswordChangedAsync`, `OnTokenIssuingAsync`, `OnDelegationMintedAsync`, `OnApprovalRequestedAsync`, `OnApprovalResolvedAsync`, `OnAgentConsentChangedAsync`, `OnConsentRevokedAsync`, `OnCapabilityTicketRedeemedAsync`); substitua-os apenas se precisar desses eventos.

#### Exemplo: serviço de email {#example-email-service}

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

`IEmailService` tem também um `SendAccountExistsEmailAsync(email, signInUrl, ct)` opcional (sem efeito por predefinição), enviado quando alguém regista um endereço que já tem conta.

> **O email é a armadilha de integração mais comum.** Se não registar nenhum `IEmailService` nem definir `Email:ResendApiKey`, os emails de verificação e de redefinição da palavra-passe são descartados sem aviso e, como a exigência de email confirmado no início de sessão está ativa por predefinição, os utilizadores que se registaram por si próprios nunca conseguem iniciar sessão (`UseAuthagonal` emite um aviso no arranque). O remetente Resend integrado é ativado automaticamente quando `Email:ResendApiKey` + `Email:SenderEmail` estão configurados; para desenvolvimento/testes, `Auth:AutoConfirmEmailDomains` dispensa a verificação para os domínios indicados. Consulte [Configuração → Email](configuration#email).

### Adicionar endpoints personalizados {#add-custom-endpoints}

Pode adicionar os seus próprios endpoints ao lado dos da Authagonal:

```csharp
app.MapGet("/custom/health", () => Results.Ok(new { status = "healthy" }));
```

### Desativar a API de administração {#disable-admin-api}

Em implementações expostas ao público, desative os endpoints de administração:

```json
{
  "AdminApi": {
    "Enabled": false
  }
}
```

### Executar {#run-it}

```bash
dotnet run
```

O servidor arranca no URL configurado, servindo o documento de descoberta OIDC em `/.well-known/openid-configuration`, a interface de início de sessão em `/login` e todas as APIs de autenticação/administração.

---

## Parte 2: Interface de início de sessão personalizada {#part-2-custom-login-ui}

A SPA de início de sessão predefinida funciona de imediato, mas pode substituí-la pela sua própria aplicação React que importa componentes e clientes de API do pacote npm `@authagonal/login`.

### Criar a estrutura do frontend {#scaffold-the-frontend}

```bash
mkdir login-app && cd login-app
npm init -y
npm install react react-dom react-router @authagonal/login
npm install -D vite @vitejs/plugin-react typescript @types/react @types/react-dom
```

### O que o pacote npm exporta {#what-the-npm-package-exports}

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

### Ponto de entrada (main.tsx) {#entry-point-maintsx}

Carregue a personalização visual a partir do servidor e envolva a sua aplicação no contexto de personalização visual:

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

### Encaminhamento (App.tsx) {#routing-apptsx}

Combine páginas personalizadas com as páginas base do pacote. O servidor envia os utilizadores para caminhos sob `/login` (o endpoint de autorização redireciona para `/login?returnUrl=...`, e os emails têm hiperligações para `/login/reset-password`, `/login/consent`, `/login/device`), pelo que o router tem de utilizar `basename="/login"` e as rotas abaixo são relativas a ele:

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

Se quiser apenas adicionar páginas à aplicação de origem, renderize antes `<App extraRoutes={...} />` a partir do pacote. Esta já fornece todas as rotas acima (e ainda `/account`).

### Página de início de sessão personalizada {#custom-login-page}

Construa o seu próprio formulário de início de sessão com os clientes de API do pacote npm:

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

### Layout personalizado {#custom-layout}

Envolva o `AuthLayout` base para adicionar a sua própria identidade visual:

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

### Personalização visual (wwwroot/branding.json) {#branding-wwwrootbrandingjson}

Configure o aspeto da interface de início de sessão sem recompilar:

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

O esquema completo, incluindo o texto de boas-vindas localizado, a lista do seletor de idioma e as substituições por modo (escuro/claro) de cores e do fundo do logótipo, está na página [Personalização visual](branding).

### Configuração do Vite {#vite-config}

Encaminhe as chamadas à API para o backend durante o desenvolvimento:

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

### Compilar e servir {#build-and-serve}

Adicione um destino de compilação ao seu `.csproj` para compilar automaticamente a SPA e copiá-la para `wwwroot`:

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

Agora, `dotnet build` compila tanto o servidor .NET como a SPA React, e `dotnet run` serve tudo a partir de um único processo.
