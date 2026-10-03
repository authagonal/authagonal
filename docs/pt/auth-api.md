---
layout: default
title: API de autenticação
locale: pt
---

# API de autenticação

Estes endpoints suportam a SPA de início de sessão. Utilizam autenticação por cookie (`SameSite=Lax`, `HttpOnly`).

Se estiver a construir uma interface de início de sessão personalizada, são estes os endpoints com que tem de a implementar.

## Endpoints {#endpoints}

### Início de sessão {#login}

```
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**Sucesso (200):** define um cookie de autenticação e devolve:

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe",
  "mfaAvailable": false
}
```

`mfaAvailable` é `true` quando a `MfaPolicy` do cliente é `Enabled` mas o utilizador ainda não se inscreveu (a interface pode oferecer a configuração); nesse caso, é também incluído um campo `clientId`.

**MFA necessária (200):** se o utilizador tiver MFA inscrita, é-lhe **sempre** apresentado um desafio, independentemente da `MfaPolicy` do cliente que faz o pedido (a MFA é uma propriedade do utilizador/da sessão, não do cliente):

```json
{
  "mfaRequired": true,
  "challengeId": "a1b2c3...",
  "methods": ["totp", "webauthn", "recoverycode"],
  "webAuthn": { /* PublicKeyCredentialRequestOptions */ }
}
```

O cliente deve redirecionar para uma página de desafio de MFA e chamar `POST /api/auth/mfa/verify`.

**Configuração de MFA necessária (200):** se a `MfaPolicy` for `Required` e o utilizador não tiver MFA inscrita:

```json
{
  "mfaSetupRequired": true,
  "setupToken": "abc123..."
}
```

O cliente deve redirecionar para uma página de configuração de MFA. O token de configuração autentica o utilizador perante os endpoints de configuração de MFA através do cabeçalho `X-MFA-Setup-Token`.

**Respostas de erro:**

| `error` | Estado | Descrição |
|---|---|---|
| `invalid_credentials` | 401 | Email ou palavra-passe errados. Deliberadamente idêntico para emails desconhecidos (proteção contra enumeração). |
| `locked_out` | 423 | Demasiadas tentativas falhadas. É incluído `retryAfter` (em segundos). |
| `account_disabled` | 403 | A conta está desativada (só é revelado depois de uma palavra-passe correta) |
| `email_not_confirmed` | 403 | O email ainda não foi verificado (só é revelado depois de uma palavra-passe correta) |
| `sso_required` | 409 | O domínio exige SSO. `redirectUrl` aponta para o início de sessão SSO. |
| `captcha_failed` | 400 | A verificação do Turnstile falhou (só quando o Turnstile está configurado; os pedidos precisam então de um campo `turnstileToken`) |
| `email_required` | 400 | O campo de email está vazio |
| `password_required` | 400 | O campo de palavra-passe está vazio |

### Registo {#register}

```
POST /api/auth/register
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Cria uma nova conta de utilizador e envia um email de verificação. Devolve `201 { "success": true, "userId": "..." }`. Campos opcionais: `locale` (etiqueta BCP-47 persistida no utilizador) e `customAttributes` (um mapa de cadeias).

O registo é deliberadamente **neutro face à enumeração**: se o email já estiver registado, a resposta é o mesmo `201` neutro (com um `userId` descartável) e, em vez disso, o verdadeiro titular recebe por email um aviso de início de sessão/reposição. O registo tem também limite de taxa por IP, com `429 rate_limited` quando é excedido (janela e limite configuráveis através de `Auth:MaxRegistrationsPerIp` / `Auth:RegistrationWindowMinutes`).

### Confirmar email {#confirm-email}

```
GET  /api/auth/confirm-email?token={token}
POST /api/auth/confirm-email?token={token}
```

Confirma o endereço de email do utilizador com o token do email de verificação. `GET` é a hiperligação clicável do email e redireciona para `/login?email_confirmed=1` (com um parâmetro `continue_client` quando o registo teve origem num fluxo OAuth). `POST` é o caminho programático e devolve JSON (o token também pode ser enviado num corpo JSON como `{ "token": "..." }`); a resposta inclui um `appLink` opcional (destino "continuar para a aplicação").

### Fornecedores {#providers}

```
GET /api/auth/providers
```

Devolve a lista de fornecedores de identidade externos configurados (para apresentar os botões de SSO):

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "type": "oidc", "iconUrl": null, "loginUrl": "/oidc/google/login" }
  ],
  "turnstileSiteKey": null
}
```

As ligações com `AllowedDomains` configurado são **excluídas**: essas são alcançadas a partir do email, através de `/api/auth/sso-check`, em vez de um botão. `turnstileSiteKey` é definido quando o Cloudflare Turnstile está configurado (a interface de início de sessão tem então de enviar um `turnstileToken` nos pedidos de início de sessão, registo e palavra-passe).

### Terminar sessão {#logout}

```
POST /api/auth/logout
```

Termina a sessão de quem chama da mesma forma que `/connect/endsession`: são enviados tokens de back-channel logout às relying parties que tenham um URI registado, as concessões emitidas para essa sessão são revogadas e o cookie de autenticação é limpo. Exige autenticação por cookie e um pedido da mesma origem. Devolve `200`:

```json
{
  "success": true,
  "frontchannel_logout_uris": ["https://myapp.example.com/oidc/frontchannel"]
}
```

`frontchannel_logout_uris` lista os URLs de front-channel logout que quem chama deve carregar (iframes ocultos) para concluir o fim da sessão no browser; está vazio quando nenhum cliente registou nenhum. Consulte [Front-Channel Logout](front-channel-logout).

### Palavra-passe esquecida {#forgot-password}

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

Devolve sempre `200` (proteção contra enumeração). Se o utilizador existir, envia um email de reposição.

### Repor palavra-passe {#reset-password}

```
POST /api/auth/reset-password
Content-Type: application/json

{
  "token": "base64-encoded-token",
  "newPassword": "NewSecurePass1!"
}
```

| `error` | Descrição |
|---|---|
| `weak_password` | Não cumpre os requisitos de robustez |
| `invalid_token` | O token está malformado |
| `token_expired` | O token expirou (validade predefinida de 60 minutos, configurável através de `Auth:PasswordResetExpiryMinutes`) |

### Sessão {#session}

```
GET /api/auth/session
```

Devolve as informações da sessão atual, se estiver autenticado:

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

Devolve `401` se não estiver autenticado.

### Aplicações {#apps}

```
GET /api/auth/apps
```

Devolve as hiperligações das aplicações do inquilino para o iniciador "voltar à aplicação" da página de conta: clientes ativados que tenham um URI inicial (`initiateLoginUri` tem preferência sobre `clientUri`). Cada entrada é `{ clientId, clientName, homeUri, logoUri, isDefault }`; exatamente uma aplicação é marcada como predefinida (o cliente assinalado, ou o único cliente com URI inicial). Exige autenticação por cookie.

### Perfil (self-service) {#profile-self-service}

```
GET   /api/auth/profile
PATCH /api/auth/profile
```

O utilizador autenticado lê/atualiza os seus próprios campos de perfil não sensíveis: `firstName`, `lastName`, `companyName`, `phone`, `locale`. Os campos null ficam inalterados; o email, a palavra-passe, as funções, o estado de ativação e a organização **não** são editáveis aqui. Ambos devolvem o perfil `{ email, emailConfirmed, firstName, lastName, companyName, phone, locale }`.

### Sessões (self-service) {#sessions-self-service}

```
GET    /api/auth/sessions
DELETE /api/auth/sessions/{sessionId}
POST   /api/auth/sessions/revoke-others
```

Lista e termina as sessões SSO do próprio utilizador autenticado. Exigem sessões do lado do servidor, que são opcionais: chame `AddAuthagonalServerSideSessions(configuration)` depois de `AddAuthagonal` (Azure Table Storage, lendo `Storage:ConnectionString` ou `Storage:TableServiceUri`), ou registe o seu próprio `ITicketStore` e `IUserSessionRegistry`. Sem registo de sessões, `GET` devolve uma lista vazia, `revoke-others` devolve `{ "revoked": 0 }` e `DELETE` devolve `404 not_supported`. As rotas `DELETE` e `POST` exigem um pedido da mesma origem.

`GET` devolve as sessões, com a atividade mais recente primeiro:

```json
{
  "sessions": [
    {
      "sessionId": "...",
      "current": true,
      "createdAt": "2026-10-01T02:11:40+00:00",
      "lastSeenAt": "2026-10-04T05:30:12+00:00",
      "expiresAt": "2026-10-08T02:11:40+00:00",
      "ip": "203.0.113.7",
      "userAgent": "Mozilla/5.0 ..."
    }
  ]
}
```

`DELETE` termina uma sessão e devolve `{ "revoked": 1 }`, ou `404 session_not_found`. `POST /revoke-others` termina todas as sessões exceto a de quem chama e devolve `{ "revoked": <count> }`. Ambos notificam também as relying parties de cada sessão terminada (back-channel e front-channel logout) e revogam as concessões vinculadas a ela, pelo que os tokens de atualização guardados nesse dispositivo deixam de funcionar. A página de conta da interface de início de sessão mostra esta lista quando está registado um registo de sessões.

### Verificação de SSO {#sso-check}

```
GET /api/auth/sso-check?email=user@acme.com
```

Verifica se o domínio do email exige SSO:

```json
{
  "ssoRequired": true,
  "providerType": "saml",
  "connectionId": "acme-azure",
  "redirectUrl": "/saml/acme-azure/login"
}
```

Se o SSO não for exigido:

```json
{
  "ssoRequired": false
}
```

### Política de palavras-passe {#password-policy}

```
GET /api/auth/password-policy
```

Devolve os requisitos de palavra-passe do servidor (configurados através de `PasswordPolicy` nas definições):

```json
{
  "rules": [
    { "rule": "minLength", "value": 8, "label": "At least 8 characters" },
    { "rule": "uppercase", "value": null, "label": "Uppercase letter" },
    { "rule": "lowercase", "value": null, "label": "Lowercase letter" },
    { "rule": "digit", "value": null, "label": "Number" },
    { "rule": "specialChar", "value": null, "label": "Special character" }
  ]
}
```

A interface de início de sessão predefinida obtém este endpoint na página de reposição da palavra-passe para apresentar os requisitos de forma dinâmica.

## Requisitos de palavra-passe predefinidos {#default-password-requirements}

Com a configuração predefinida, as palavras-passe têm de cumprir todos estes requisitos:

- Pelo menos 8 caracteres
- Pelo menos uma letra maiúscula
- Pelo menos uma letra minúscula
- Pelo menos um algarismo
- Pelo menos um carácter não alfanumérico
- Pelo menos 2 caracteres distintos

Podem ser personalizados através da secção de configuração `PasswordPolicy`; consulte [Configuração](configuration).

## Endpoints de MFA {#mfa-endpoints}

### Verificação de MFA {#mfa-verify}

```
POST /api/auth/mfa/verify
Content-Type: application/json

{
  "challengeId": "a1b2c3...",
  "method": "totp",
  "code": "123456"
}
```

Verifica um desafio de MFA. Em caso de sucesso, define o cookie de autenticação e devolve as informações do utilizador.

**Métodos:**

| `method` | Campos obrigatórios | Descrição |
|---|---|---|
| `totp` | `code` (6 algarismos) | Palavra-passe de utilização única baseada no tempo, gerada pela aplicação de autenticação |
| `webauthn` | `assertion` (cadeia JSON) | Resposta de asserção WebAuthn de `navigator.credentials.get()` |
| `recovery` | `code` (`XXXX-XXXX`) | Código de recuperação de utilização única (consumido quando é utilizado) |

**Semântica das novas tentativas:** um código errado **não** consome o desafio; o código é validado primeiro e o desafio só é consumido em caso de sucesso, pelo que o utilizador pode tentar novamente com o mesmo `challengeId` depois de se enganar num algarismo (`401 invalid_code` / `assertion_failed`). Cada desafio tolera **5 tentativas falhadas**; a 5.ª falha consome-o e devolve `401 too_many_attempts`, obrigando a um novo início de sessão (o que limita a força bruta sobre o TOTP a 5 tentativas por desafio). Os desafios também expiram (predefinição de 5 minutos, `Auth:MfaChallengeExpiryMinutes`); um `challengeId` expirado, desconhecido ou já consumido devolve `invalid_challenge`. Os códigos TOTP estão ainda protegidos contra reutilização: um código de um intervalo de tempo já utilizado é rejeitado.

### Estado da MFA {#mfa-status}

```
GET /api/auth/mfa/status
```

Devolve os métodos de MFA em que o utilizador está inscrito. Exige autenticação por cookie ou o cabeçalho `X-MFA-Setup-Token`.

```json
{
  "enabled": true,
  "offered": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

`offered` é `false` quando a `MfaPolicy` de todos os clientes é `Disabled`, ou seja, o inquilino tem a MFA desativada, para que a interface de configuração se possa ocultar. As entradas de códigos de recuperação contêm ainda `isConsumed`.

### Configuração de TOTP {#totp-setup}

```
POST /api/auth/mfa/totp/setup
→ { "setupToken": "...", "qrCodeDataUri": "data:image/png;base64,...", "manualKey": "BASE32..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
→ { "success": true }
```

### Configuração de WebAuthn / chave de acesso {#webauthn--passkey-setup}

```
POST /api/auth/mfa/webauthn/setup
→ { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
→ { "success": true, "credentialId": "..." }
```

A inscrição de uma chave de acesso exige **primeiro uma credencial TOTP confirmada** (`400 totp_required_first`): as chaves de acesso são uma comodidade por dispositivo, assente num fator de base portátil, pelo que uma conta nunca pode ficar apenas com chaves de acesso e presa a um dispositivo. Os utilizadores cujo domínio de email é encaminhado para SSO não podem inscrever uma chave de acesso local (`400 sso_managed`), porque contornaria o IdP do inquilino. Um ID de credencial já registado em **qualquer** conta, incluindo a do próprio utilizador que se está a inscrever, é rejeitado com `409 credential_already_registered`, porque um duplicado reiniciaria o contador de assinaturas dessa credencial e faria duas linhas partilhar uma única entrada de pesquisa.

### Códigos de recuperação {#recovery-codes}

```
POST /api/auth/mfa/recovery/generate
→ { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

Gera 10 códigos de recuperação de utilização única. Exige que esteja inscrito pelo menos um método principal (TOTP ou WebAuthn). Gerar de novo substitui todos os códigos de recuperação existentes.

### Remover credencial de MFA {#remove-mfa-credential}

```
DELETE /api/auth/mfa/credentials/{credentialId}
→ { "success": true }
```

Remove uma credencial de MFA específica. Se o último método principal for removido, a MFA é desativada para o utilizador. Exige uma sessão real por cookie; um token de configuração é rejeitado com `403 session_required` (os tokens de configuração existem apenas para adicionar um primeiro fator, nunca para enfraquecer a MFA).

### Início de sessão com chave de acesso, sem palavra-passe {#passwordless-passkey-login}

```
POST /api/auth/mfa/passwordless/begin
→ { "challengeId": "...", "options": { /* PublicKeyCredentialRequestOptions */ } }

POST /api/auth/mfa/passwordless/complete
{ "challengeId": "...", "assertion": "..." }
→ { "userId": "...", "email": "...", "name": "..." }
```

Início de sessão com credencial detetável (chave de acesso residente) sem contexto de utilizador prévio: `begin` emite um desafio de asserção com uma lista `allowCredentials` vazia, e `complete` resolve o utilizador **a partir da** chave de acesso escolhida, verifica a asserção e inicia a sessão (a sessão contém o marcador de MFA, já que uma chave de acesso é uma autenticação forte resistente a phishing). Como nenhum utilizador foi identificado antes da cerimónia, o passo 6 da WebAuthn §7.2 torna aqui obrigatório o user handle do autenticador: uma asserção sem ele é recusada com `401 user_handle_required`, e uma que indique uma conta diferente da do titular da credencial com `401 credential_not_found`. Se o domínio de email do utilizador resolvido for encaminhado para SSO, o início de sessão é recusado com `409 sso_required` + `redirectUrl`, para que uma chave de acesso local não possa contornar um IdP obrigatório.

## Autorização de dispositivos (RFC 8628) {#device-authorization-rfc-8628}

### Pedir um código de dispositivo {#request-device-code}

```
POST /connect/deviceauthorization
Content-Type: application/x-www-form-urlencoded

client_id=my-cli&scope=openid+profile
```

Devolve um código de dispositivo, um código de utilizador e um URI de verificação:

```json
{
  "device_code": "abc123...",
  "user_code": "ABCD-EFGH",
  "verification_uri": "https://auth.example.com/device",
  "verification_uri_complete": "https://auth.example.com/device?user_code=ABCD-EFGH",
  "expires_in": 300,
  "interval": 5
}
```

`expires_in` provém do `DeviceCodeLifetimeSeconds` do cliente (predefinição 300). O dispositivo apresenta ao utilizador o `verification_uri` e o `user_code` e depois consulta periodicamente o endpoint de token com o `device_code`, com um intervalo nunca inferior a `interval` segundos, caso contrário o endpoint de token responde `slow_down` (RFC 8628 §3.5). Enquanto o utilizador ainda não tiver aprovado, o endpoint de token devolve `authorization_pending`. O utilizador visita o URI de verificação, inicia sessão e introduz o código de utilizador para aprovar.

### Mostrar o pedido antes de aprovar {#show-the-request-before-approving}

```
GET /api/auth/device/info?user_code=ABCD-EFGH
```

Exige autenticação por cookie. Descreve o que o código concederia, para que o ecrã de aprovação possa mostrar ao utilizador que aplicação está a pedir antes de este aprovar (um fluxo de dispositivo iniciado por um atacante e aprovado num pedido opaco é o padrão de consentimento ilícito contra o qual a RFC 8628 §5.4 alerta):

```json
{
  "clientId": "my-cli",
  "clientName": "My CLI",
  "clientUri": "https://example.com",
  "logoUri": null,
  "scopes": ["openid", "profile"]
}
```

`scopes` é o que seria efetivamente concedido, depois da verificação de funções por utilizador nos âmbitos restritos por função, e não o pedido em bruto. Erros: `401 not_authenticated`, `400 user_code_required`, `400 invalid_user_code` (desconhecido, consumido ou expirado), `400 expired`. Partilha o contador de limite de taxa da aprovação (abaixo).

### Aprovar dispositivo {#approve-device}

```
POST /api/auth/device/approve
Content-Type: application/x-www-form-urlencoded

user_code=ABCD-EFGH&scopes=openid+profile
```

Exige autenticação por cookie e um pedido da mesma origem. `scopes` é opcional (separado por espaços): só pode restringir aquilo a que o utilizador tem direito, nunca alargá-lo, e omiti-lo concede tudo aquilo a que tem direito. Aprova o código de dispositivo para o utilizador atual e devolve `200 { "approved": true }`. O dispositivo pode então trocar o código de dispositivo por tokens através do endpoint de token, com o tipo de concessão `urn:ietf:params:oauth:grant-type:device_code`.

O código submetido é normalizado conforme a RFC 8628 §6.1 antes da pesquisa: é convertido em maiúsculas e todos os caracteres fora do alfabeto de 31 caracteres do código são descartados. `ABCD-EFGH`, `abcd-efgh`, `ABCDEFGH`, `ABCD EFGH` e um copiar-colar que transformou o hífen num travessão são todos o mesmo código. O hífen existe apenas para facilitar a leitura do código em voz alta.

| Estado | `error` | Significado |
|---|---|---|
| 400 | `user_code_required`, `invalid_user_code`, `expired` | Como em `info` |
| 400 | `invalid_scope` | Foi indicado `scopes`, mas nenhum deles é um âmbito a que o utilizador tenha direito |
| 403 | `access_denied` | O utilizador não tem direito a nenhum dos âmbitos pedidos (`Scope.AllowedRoles`) |
| 403 | `mfa_enrolment_required` | A política de MFA efetiva do cliente é `Required` e o utilizador não tem segundo fator; inscreva-se e volte a aprovar |

A introdução tem um limite de dez tentativas por minuto por titular (RFC 8628 §5.1), partilhado entre `info`, `approve` e `deny`; a décima primeira devolve `429`. Esse contador é por nó com o limitador de taxa em processo predefinido, pelo que uma implementação com várias réplicas deve também impor o limite na periferia da rede.

### Recusar dispositivo {#deny-device}

```
POST /api/auth/device/deny
Content-Type: application/x-www-form-urlencoded

user_code=ABCD-EFGH
```

Exige autenticação por cookie e um pedido da mesma origem. Regista a recusa do utilizador e devolve `200 { "success": true }`. A consulta seguinte do dispositivo ao endpoint de token recebe `access_denied` (RFC 8628 §3.5) em vez de `authorization_pending` até o código expirar. Os mesmos erros e o mesmo contador de limite de taxa que `info`.

## Introspeção de tokens (RFC 7662) {#token-introspection-rfc-7662}

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

token=eyJhbGci...
```

Ou com credenciais codificadas como formulário:

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded

token=eyJhbGci...&client_id=my-app&client_secret=secret
```

Devolve os metadados do token:

```json
{
  "active": true,
  "sub": "user-id",
  "client_id": "my-app",
  "scope": "openid profile",
  "iss": "https://auth.example.com",
  "exp": 1234567890,
  "iat": 1234567890,
  "token_type": "Bearer"
}
```

Os tokens inativos ou inválidos devolvem `{ "active": false }`. Suporta tanto tokens de acesso JWT como tokens de atualização opacos.

## Endpoints de consentimento {#consent-endpoints}

### Informações de consentimento {#consent-info}

```
GET /consent/info?client_id=my-app
```

Exige autenticação por cookie. Devolve os detalhes do cliente e os âmbitos pedidos para a página de consentimento. Os âmbitos não são retirados da query string: são a oferta que o endpoint de autorização registou para este utilizador e este cliente (depois da filtragem pelas funções a que tem direito), pelo que uma ligação forjada não consegue colocar o nome de um cliente de confiança por cima de uma lista de permissões escolhida por quem chama.

```json
{
  "clientId": "my-app",
  "clientName": "My Application",
  "description": null,
  "clientUri": null,
  "logoUri": null,
  "scopes": ["openid", "profile", "email"],
  "scopeDetails": [
    { "name": "openid", "displayName": null, "description": null, "emphasize": false, "required": false, "group": null },
    { "name": "profile", "displayName": null, "description": null, "emphasize": false, "required": false, "group": null },
    { "name": "email", "displayName": null, "description": null, "emphasize": false, "required": false, "group": null }
  ]
}
```

`scopeDetails` acompanha `scopes` em paralelo (mesma ordem, uma entrada por âmbito), pelo que uma aplicação de início de sessão que só leia `scopes` continua a funcionar. Cada entrada contém a apresentação registada para esse âmbito:

| Campo | Significado |
|---|---|
| `name` | O nome do âmbito, tal como em `scopes`. |
| `displayName` | O nome de apresentação registado, ou `null` quando o âmbito não está registado. |
| `description` | A descrição registada, ou `null`. |
| `emphasize` | `true` quando o âmbito está registado como tendo consequências, para que o ecrã possa chamar a atenção para ele. A predefinição é `false`. |
| `required` | `true` quando o âmbito está registado como não recusável: o ecrã mostra-o assinalado e bloqueado. A predefinição é `false`. |
| `group` | O título sob o qual o âmbito é arrumado, ou `null` para o mostrar isoladamente. |

Um âmbito não registado produz `null` em `displayName`, `description` e `group` e `false` nos dois indicadores, e a aplicação de início de sessão recorre ao seu próprio texto. Consulte [Âmbitos](scopes) para registar o texto.

Erros:

| Estado | Corpo | Quando |
|---|---|---|
| `401` | nenhum | Nenhum utilizador com sessão iniciada. |
| `404` | `{ "error": "client_not_found" }` | `client_id` desconhecido. |
| `400` | `{ "error": "no_pending_consent_request" }` | Não existe nenhuma oferta de consentimento válida para este utilizador e este cliente (nenhuma foi registada, ou expirou). |

### Submeter consentimento {#submit-consent}

```
POST /consent
Content-Type: application/json

{
  "clientId": "my-app",
  "decision": "allow",
  "scopes": ["openid", "profile", "email"],
  "returnUrl": "/connect/authorize?..."
}
```

Regista a decisão de consentimento do utilizador (exige autenticação por cookie) e devolve `{ "redirect": "..." }` para a SPA navegar até lá. Ao permitir, os âmbitos concedidos são persistidos (filtrados pelos `AllowedScopes` do cliente, para que um corpo adulterado não possa registar âmbitos que o cliente não poderia pedir) e o redirecionamento aponta de volta para o fluxo de autorização. Com `"decision": "deny"`, o redirecionamento aponta para o `redirect_uri` do cliente com um erro `access_denied`.

### Listar concessões {#list-grants}

```
GET /consent/grants
```

Devolve todas as aplicações que o utilizador autorizou:

```json
[
  {
    "clientId": "my-app",
    "clientName": "My Application",
    "scopes": ["openid", "profile", "email"],
    "consentedAt": "2026-04-09T12:00:00Z"
  }
]
```

### Revogar concessão {#revoke-grant}

```
DELETE /consent/grants/{clientId}
```

Revoga o consentimento de uma aplicação específica. Será pedido ao utilizador que volte a consentir no seu próximo início de sessão.

## Descoberta e chaves de assinatura (JWKS) {#discovery-and-signing-keys-jwks}

Ambos são públicos e anónimos. São o que um servidor de recursos utiliza para validar os tokens que este servidor emite.

```
GET /.well-known/openid-configuration
GET /.well-known/oauth-authorization-server
GET /.well-known/openid-configuration/jwks
```

- Os dois caminhos de metadados devolvem o mesmo documento de descoberta; o seu `jwks_uri` é `{issuer}/.well-known/openid-configuration/jwks`.
- O JWKS lista todas as chaves de assinatura não expiradas (`kty`, `use`, `kid`, `alg` e `crv`/`x`/`y` para as chaves EC). A rotação publica a chave seguinte com dias de antecedência, pelo que uma cópia em cache nunca deixa de ter a chave com que um token foi assinado.
- As respostas incluem `Cache-Control: public, max-age=3600`.
- A assinatura é apenas ES256; a descoberta anuncia `id_token_signing_alg_values_supported: ["ES256"]`.
- O emissor provém de `ITenantContext` e as chaves de `IKeyManager`, pelo que um anfitrião multi-inquilino com um gestor de chaves por inquilino serve chaves por inquilino.

## Comportamento do endpoint de autorização {#authorization-endpoint-behaviour}

`GET /connect/authorize` é o ponto de entrada do fluxo de código de autorização. Há dois comportamentos que importam a quem construa um cliente ou uma interface de início de sessão sobre ele.

### Emissor na resposta (RFC 9207) {#issuer-in-the-response-rfc-9207}

Todos os redirecionamentos de volta para o `redirect_uri` do cliente contêm um parâmetro de query `iss` com o emissor, tanto em caso de sucesso (juntamente com `code` e `state`) como de erro (juntamente com `error`, `error_description` e `state`). O mesmo se aplica ao redirecionamento de erro quando um utilizador recusa o consentimento em `/consent`. O documento de descoberta anuncia-o com `authorization_response_iss_parameter_supported: true`. Um cliente que comunique com vários servidores de autorização deve comparar `iss` com o emissor com que iniciou o fluxo, que é o que neutraliza o ataque de mix-up; os clientes que ignoram o parâmetro não são afetados. Os erros gerados antes de se conhecer um `redirect_uri` de confiança (`client_id` desconhecido, um URI de redirecionamento não registado) são devolvidos como um corpo de erro JSON e não como um redirecionamento, pelo que não têm `iss`.

### `prompt` e `max_age` {#prompt-and-max_age}

| Pedido | Comportamento |
|---|---|
| `prompt=login` | Uma sessão existente é terminada e o utilizador é enviado para `/login` para se autenticar novamente. O `prompt` é retirado do `returnUrl` para que o novo início de sessão não seja obrigado a voltar a autenticar-se num ciclo. Num [pedido enviado previamente](par), o prompt acompanha o payload guardado, e o ciclo é quebrado exigindo que o `auth_time` da sessão seja igual ou posterior ao momento em que o pedido foi enviado |
| `prompt=select_account` | Tratado como `prompt=login`: o servidor mantém uma sessão por browser, pelo que a escolha de conta é o ecrã de início de sessão |
| `prompt=create` | Um utilizador não autenticado é enviado para `/login/register` em vez do formulário de início de sessão. Uma sessão existente simplesmente prossegue |
| `prompt=consent` | O ecrã de consentimento é apresentado mesmo que uma concessão guardada satisfaça o pedido, uma vez por pedido (o marcador de satisfeito é de utilização única) |
| `prompt=none` | Nunca é apresentada nenhuma interface. O servidor responde com um redirecionamento que contém `login_required` (sem sessão), `interaction_required` (é necessária elevação ou inscrição de MFA) ou `consent_required` (é necessário consentimento) |
| `max_age=N` | Se o `auth_time` da sessão tiver mais de `N` segundos, ou estiver ausente, o utilizador volta a autenticar-se exatamente como com `prompt=login`. `max_age=0` volta sempre a autenticar |

`prompt=none` combinado com qualquer outro valor é rejeitado com `invalid_request`, tal como qualquer valor fora de `none`, `login`, `consent`, `select_account` e `create`. O anfitrião incorporável `Authagonal.Protocol` respeita `prompt=login`, `select_account`, `none` e `max_age` da mesma forma, mas não tem interface de consentimento, pelo que responde a `prompt=consent` com `consent_required`.

## Construir uma interface de início de sessão personalizada {#building-a-custom-login-ui}

A SPA predefinida (`login-app/`) é uma implementação desta API. Para construir a sua própria:

1. Sirva a sua interface nos caminhos `/login`, `/forgot-password`, `/reset-password`, `/consent`, `/device`
2. O endpoint de autorização redireciona os utilizadores não autenticados para `/login?returnUrl={encoded-authorize-url}`
3. Depois de um início de sessão bem-sucedido (cookie definido), redirecione o utilizador para o `returnUrl`
4. As ligações de reposição da palavra-passe utilizam `{Issuer}/login/reset-password?p={token}` (a SPA de início de sessão está montada em `/login`)

A sua interface tem de ser servida a partir da **mesma origem** que a API, porque:
- A autenticação por cookie utiliza `SameSite=Lax` + `HttpOnly`
- O endpoint de autorização redireciona para `/login` (relativo)
- As ligações de reposição utilizam `{Issuer}/login/reset-password`
