---
layout: default
title: Início rápido
locale: pt
---

# Início rápido

Ponha a Authagonal a funcionar localmente em 5 minutos.

## 1. Iniciar o servidor {#1-start-the-server}

```bash
docker compose up
```

Isto inicia a Authagonal em `http://localhost:8080`, com o Azurite como armazenamento.

> O ficheiro compose define `Auth__AllowInsecureHttp=true`, porque as §3.1/§3.2 da RFC 6749 exigem TLS nos endpoints de autorização e de token, e de outro modo a Authagonal recusa pedidos em texto simples para `/connect/*`. Esse interruptor destina-se a um portátil. Tudo aquilo a que outra pessoa consiga aceder fica atrás de um proxy que termina o TLS e reencaminha `X-Forwarded-Proto: https`, com o interruptor removido: consulte [Instalação](installation).

## 2. Verificar que está a funcionar {#2-verify-its-running}

```bash
# Health check
curl http://localhost:8080/health

# OIDC discovery
curl http://localhost:8080/.well-known/openid-configuration

# Login page (returns the SPA)
curl http://localhost:8080/login
```

## 3. Registar um cliente {#3-register-a-client}

Adicione um cliente ao seu `appsettings.json` (ou passe-o através de variáveis de ambiente):

```json
{
  "Clients": [
    {
      "ClientId": "my-web-app",
      "ClientName": "My Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["http://localhost:3000/callback"],
      "PostLogoutRedirectUris": ["http://localhost:3000"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["http://localhost:3000"],
      "RequirePkce": true,
      "RequireClientSecret": false
    }
  ]
}
```

Os clientes são inicializados no arranque, uma operação segura de executar em cada implementação.

## 4. Iniciar o início de sessão {#4-initiate-a-login}

Redirecione os seus utilizadores para:

```
http://localhost:8080/connect/authorize
  ?client_id=my-web-app
  &redirect_uri=http://localhost:3000/callback
  &response_type=code
  &scope=openid profile email
  &state=random-state
  &code_challenge=...
  &code_challenge_method=S256
```

O utilizador vê a página de início de sessão, autentica-se e é redirecionado de volta com um código de autorização.

> **Primeiro utilizador:** registe um em `http://localhost:8080/login/register` ou crie um através da [API de administração](admin-api). O registo autónomo envia um email de verificação e, sem nenhum remetente de email configurado (a predefinição local), esse email é descartado; por isso, para testes locais, defina `Auth__AutoConfirmEmailDomains__0=example.dev` (qualquer domínio com que se registe) para saltar a verificação, ou configure `Email:ResendApiKey` + `Email:SenderEmail`. Consulte [Configuração → Email](configuration#email).

## 5. Trocar o código {#5-exchange-the-code}

```bash
curl -X POST http://localhost:8080/connect/token \
  -d grant_type=authorization_code \
  -d code=THE_CODE \
  -d redirect_uri=http://localhost:3000/callback \
  -d client_id=my-web-app \
  -d code_verifier=THE_VERIFIER
```

Resposta:

```json
{
  "access_token": "eyJ...",
  "id_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 1800,
  "scope": "openid profile email"
}
```

`expires_in` é o `AccessTokenLifetimeSeconds` do cliente (1800 para um cliente inicializado, a menos que o defina). Não aparece aqui nenhum `refresh_token`: um cliente só recebe um quando define `AllowOfflineAccess` e o pedido solicita o âmbito `offline_access`.

## Demonstração funcional {#working-demo}

O diretório `demos/sample-app/` contém uma SPA React + API completa que implementa todo o fluxo OIDC acima. Consulte o [README das demonstrações](https://github.com/authagonal/authagonal/tree/master/demos) para obter instruções.

## Próximos passos {#next-steps}

- [Configuração](configuration): referência completa de todas as definições
- [Extensibilidade](extensibility): alojar como biblioteca, adicionar hooks personalizados
- [Personalização visual](branding): personalizar a interface de início de sessão
- [SAML](saml): adicionar fornecedores de SSO SAML
- [Aprovisionamento](provisioning): aprovisionar utilizadores em aplicações a jusante
