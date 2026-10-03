---
layout: default
title: Aprovisionamento
locale: pt
---

# Aprovisionamento TCC

O Authagonal aprovisiona utilizadores em aplicações a jusante através do padrão **Try-Confirm-Cancel (TCC)**. Isto garante que todas as aplicações concordam antes de um utilizador obter acesso, com uma reversão limpa se alguma aplicação o rejeitar.

## Quando é executado o aprovisionamento {#when-provisioning-runs}

O aprovisionamento é executado automaticamente sempre que um utilizador é criado, qualquer que seja a via de criação:

| Endpoint | Acionador |
|---|---|
| `POST /api/v1/profile/` | Criação de utilizador por um administrador |
| `POST /api/auth/register` | Registo self-service |
| SAML ACS (`POST /saml/{id}/acs`) | Primeiro início de sessão SSO (novo utilizador) |
| Callback OIDC (`GET /oidc/callback`) | Primeiro início de sessão SSO (novo utilizador) |
| SCIM (`POST /scim/v2/Users`) | Aprovisionamento pelo fornecedor de identidade |
| `GET /connect/authorize` | Primeira autorização através de um cliente com `ProvisioningApps` |

As combinações aplicação/utilizador já aprovisionadas são ignoradas (o registo é feito na tabela `UserProvisions`).

As vias de criação de utilizadores aprovisionam em **todas as aplicações configuradas**. O endpoint de autorização aprovisiona apenas nas aplicações da lista `ProvisioningApps` do cliente.

**Em caso de rejeição:** se alguma aplicação de aprovisionamento rejeitar o utilizador na fase Try (ou se um callback falhar), o utilizador acabado de criar é eliminado. Isto evita utilizadores criados pela metade. O que o chamador vê depende da via:

| Via | Resposta |
|---|---|
| Criação pelo administrador (`POST /api/v1/profile/`), registo self-service | `422 Unprocessable Entity` com o motivo da rejeição |
| SAML ACS, callback OIDC | `400 Bad Request`, `{ "error": "provisioning_rejected", "message": "..." }` |
| Criação por SCIM | SCIM `400`, `scimType: invalidValue`, com uma mensagem fixa (o texto da aplicação a jusante não é repetido para o fornecedor de identidade) |
| Confirmação da reivindicação de uma conta sem palavra-passe | `400 provisioning_rejected` em JSON, ou um redirecionamento para `/login?error=provisioning_rejected&error_description=...` no caso de um clique no browser (consulte [Promover um utilizador](user-upgrade)) |
| `GET /connect/authorize` | Redirecionamento de volta para o cliente com `error=access_denied` |

O pedido de criação pelo administrador aceita `skipProvisioning: true`, para um chamador próprio que é ele mesmo o destino do aprovisionamento e não quer que o seu próprio callback seja reinvocado enquanto ainda está a meio da configuração do utilizador. Nada é aprovisionado e nenhuma aplicação é chamada para esse utilizador.

## Configuração {#configuration}

### 1. Definir as aplicações de aprovisionamento {#1-define-provisioning-apps}

Em `appsettings.json`:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-bearer-token",
      "TryTimeoutSeconds": 60
    }
  }
}
```

`TryTimeoutSeconds` é opcional (predefinição 60). Aumente-o quando a aplicação a jusante faz trabalho real durante o Try. Confirm, Cancel e Deprovision usam sempre um tempo limite curto e fixo (10 segundos) que não é ajustável; devem ser sempre operações leves.

A secção de configuração `ProvisioningApps` só é lida quando não está registado nenhum `IProvisioningAppStore`. Os fornecedores Azure Table, AWS e SQL registam cada um o seu, e a biblioteca passa então a resolver as aplicações a partir do armazenamento (consulte [Resolução personalizada de aplicações](#custom-app-resolution)). Com um fornecedor persistente, defina portanto as aplicações através da API de administração e não em `appsettings.json`.

### 2. Atribuir aplicações aos clientes {#2-assign-apps-to-clients}

Cada cliente declara em que aplicações os seus utilizadores têm de ser aprovisionados, através do campo `provisioningApps` do registo do cliente. Defina-o através da API de administração de clientes (a configuração de inicialização `Clients` não inclui este campo). A criação de um cliente associa o registo completo, e `PUT /api/v1/clients/{clientId}` funde os campos que enviar com o cliente guardado, pelo que um pedido que contenha apenas `provisioningApps` deixa o resto do cliente inalterado:

```
PUT /api/v1/clients/web-app
{
  "provisioningApps": ["my-backend"]
}
```

Quando um utilizador autoriza através de `web-app`, é aprovisionado em `my-backend`, caso ainda não o tenha sido.

## Protocolo TCC {#tcc-protocol}

O Authagonal faz três tipos de chamadas HTTP ao seu endpoint de aprovisionamento. Todas usam `POST` com corpos JSON e `Authorization: Bearer {ApiKey}`.

### Fase 1: Try {#phase-1-try}

**Pedido:** `POST {CallbackUrl}/try`

```json
{
  "transactionId": "a1b2c3d4...",
  "userId": "user-id",
  "email": "user@example.com",
  "firstName": "Jane",
  "lastName": "Doe",
  "organizationId": "org-id-or-null",
  "customAttributes": { "key": "value" }
}
```

Os campos nulos (incluindo `customAttributes` quando o utilizador não tem nenhum) são omitidos do conteúdo.

**Respostas esperadas:**

| Estado | Corpo | Significado |
|---|---|---|
| `200` | `{ "approved": true }` | O utilizador pode ser aprovisionado. A aplicação cria um registo **pendente**. |
| `200` | `{ "approved": false, "reason": "..." }` | O utilizador é rejeitado. Não é criado nenhum registo. |
| `2xx` | Corpo vazio ou impossível de interpretar | Tratado como aprovado. |
| Não 2xx | Qualquer | Tratado como falha. |

Devolva um valor `approved` explícito. Uma resposta cujo corpo não possa ser lido como JSON é aprovada, pelo que um endpoint mal configurado que responda `200` com uma página HTML aprova todos os utilizadores.

O `transactionId` identifica esta tentativa de aprovisionamento. A sua aplicação deve guardá-lo juntamente com o registo pendente.

Uma resposta de aprovação pode também devolver `organizationId`, `customAttributes` e `emailVerified`. O Authagonal funde-os no utilizador: `organizationId` só é aplicado se o utilizador ainda não tiver um (as aplicações seguintes na mesma transação veem a atribuição anterior), as entradas de `customAttributes` são fundidas chave a chave e `emailVerified: true` marca o email do utilizador como confirmado (use-o quando a aplicação a jusante já tiver verificado o endereço; o registo self-service dispensa então o email de verificação). Tanto `organizationId` como os atributos transitam para os tokens (claim `org_id`; atributos personalizados através da configuração `UserClaims` do âmbito). Os valores fundidos são guardados no utilizador depois de todas as aplicações terem confirmado.

### Fase 2: Confirm {#phase-2-confirm}

Chamada apenas se **todas** as aplicações tiverem devolvido `approved: true` na fase Try.

**Pedido:** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Resposta esperada:** `2xx` (qualquer corpo). A sua aplicação promove o registo pendente a confirmado. Uma resposta que não seja 2xx ou um tempo limite esgotado (10 segundos) conta como confirmação falhada.

### Fase 3: Cancel {#phase-3-cancel}

Chamada se o Try de **alguma** aplicação tiver sido rejeitado ou tiver falhado, para limpar as aplicações cujo Try teve êxito.

**Pedido:** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Resposta esperada:** `200` (qualquer corpo). A sua aplicação elimina o registo pendente.

O Cancel é feito na medida do possível: se falhar, o Authagonal regista o erro e prossegue. A sua aplicação deve **eliminar os registos não confirmados ao fim de um TTL** (por exemplo, 1 hora) como rede de segurança.

## Diagrama de fluxo {#flow-diagram}

```
Authorize Endpoint
    │
    ├─ User authenticated ✓
    ├─ Client requires apps: [A, B]
    ├─ User already provisioned into: [A]
    ├─ Need to provision: [B]
    │
    ├─ TRY B ──────────► App B: create pending record
    │   └─ approved: true
    │
    ├─ CONFIRM B ──────► App B: promote to confirmed
    │   └─ 200 OK
    │
    ├─ Store provision record (userId, "B")
    ├─ Issue authorization code
    └─ Redirect to client
```

### Em caso de falha {#on-failure}

```
    ├─ TRY A ──────────► App A: create pending record
    │   └─ approved: true
    │
    ├─ TRY B ──────────► App B: rejects
    │   └─ approved: false, reason: "No license available"
    │
    ├─ CANCEL A ───────► App A: delete pending record
    │
    └─ Redirect with error=access_denied
```

### Em caso de falha parcial da confirmação {#on-partial-confirm-failure}

Se uma confirmação falhar, o Authagonal reverte toda a transação:

1. As aplicações ainda não confirmadas recebem `POST {CallbackUrl}/cancel`.
2. As aplicações que já confirmaram **nesta transação** são compensadas com `DELETE {CallbackUrl}/users/{userId}` (a mesma chamada do [desaprovisionamento](#deprovisioning)), e os respetivos registos de aprovisionamento são removidos. As aplicações em que o utilizador foi aprovisionado por uma transação anterior não são afetadas.
3. É gerado um erro de aprovisionamento e a via chamadora elimina o utilizador acabado de criar (ou, no caso do endpoint de autorização, responde com um erro).

Os registos de aprovisionamento só são guardados depois de todas as confirmações terem tido êxito, pelo que uma nova tentativa volta a tentar todas as aplicações. A compensação é feita na medida do possível: um `DELETE` falhado é registado e a conta na aplicação pode ter de ser removida manualmente.

## Resolução personalizada de aplicações {#custom-app-resolution}

A biblioteca escolhe por si a origem das aplicações:

- Quando está registado um `IProvisioningAppStore`, o que acontece com todos os fornecedores Azure Table, AWS e SQL, as aplicações vêm do armazenamento (`StoreProvisioningAppProvider`) e são geridas através da API de administração abaixo.
- Caso contrário, são lidas da secção de configuração `ProvisioningApps` (`ConfigProvisioningAppProvider`).

Registe o seu próprio `IProvisioningAppProvider` antes de `AddAuthagonal` para resolver as aplicações de outra forma, por exemplo por inquilino; a predefinição da biblioteca só é adicionada se não estiver registado nenhum:

```csharp
builder.Services.AddSingleton<IProvisioningAppProvider, MyAppProvider>();
builder.Services.AddAuthagonal(builder.Configuration);
```

O fornecedor devolve uma lista de aplicações e os respetivos URLs de callback. O `TccProvisioningOrchestrator` chama Try/Confirm/Cancel em cada uma.

> **Por predefinição, `CallbackUrl` tem de ser publicamente encaminhável.** O Authagonal valida-o quando é escrito e de novo em cada pedido que faz, recusando destinos de loopback, RFC1918, link-local e `.internal`/`.local` (um callback de aprovisionamento é um URL obtido pelo servidor). Uma aplicação de aprovisionamento que corra dentro da sua própria rede é uma implementação suportada: indique-a em [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard).

### API de administração {#admin-api}

As aplicações guardadas no armazenamento são geridas em `/api/v1/provisioning/apps` (política `IdentityAdmin`; todas as alterações são auditadas):

| Rota | Comportamento |
|---|---|
| `GET /` | `{ "apps": [{ "appId", "name", "callbackUrl", "hasApiKey", "tryTimeoutSeconds" }], "limit": n }`. A chave de API nunca é devolvida, apenas `hasApiKey`. `limit` é a quota de aplicações, nula quando não existe. |
| `POST /` | Criar. `name` e `callbackUrl` são obrigatórios; `apiKey` e `tryTimeoutSeconds` são opcionais. É gerado um `appId` de 12 caracteres. Acima da quota, `400 provisioning_app_limit`. |
| `PUT /{appId}` | Substitui `name`, `callbackUrl` e `tryTimeoutSeconds` (`name` e `callbackUrl` voltam a ser obrigatórios). `apiKey` omitido ou nulo deixa a chave inalterada; uma string vazia apaga-a. `404 app_not_found` para uma aplicação desconhecida. |
| `DELETE /{appId}` | `{ "removed": true }`. |
| `POST /{appId}/test` | Envia à aplicação um Try com um utilizador de teste fixo (`test-user`, `test@example.com`), com um tempo limite de 10 segundos. Devolve `{ "success", "statusCode", "body" }` (o corpo é truncado a 1000 caracteres). As falhas de ligação devolvem `success: false, statusCode: 0` em vez de um estado de erro. |

`callbackUrl` tem de ser um URL `http` ou `https` absoluto num anfitrião externo, conforme descrito acima. `tryTimeoutSeconds` é limitado ao intervalo de 5 a 300 segundos. O `appId` é o que um cliente indica em `provisioningApps`.

## Desaprovisionamento {#deprovisioning}

Quando um utilizador é eliminado através da API de administração (`DELETE /api/v1/profile/{userId}`) ou desaprovisionado através de SCIM (`DELETE /scim/v2/Users/{id}`, uma eliminação lógica que desativa o utilizador), o Authagonal chama `DELETE {CallbackUrl}/users/{userId}` em cada aplicação em que o utilizador foi aprovisionado, com um tempo limite de 10 segundos, e remove o registo de aprovisionamento. Isto é feito na medida do possível: as falhas são registadas mas não bloqueiam a eliminação. Uma aplicação que já não esteja configurada é ignorada com um aviso.

`ReprovisionAsync` em `IProvisioningOrchestrator` volta a executar Try e Confirm para todas as aplicações, mesmo quando o utilizador já está aprovisionado. A biblioteca usa-o quando uma conta sem palavra-passe é reivindicada (consulte [Promover um utilizador](user-upgrade)); um simples novo início de sessão nunca o faz.

## Implementar os endpoints a montante {#implementing-the-upstream-endpoints}

### Exemplo mínimo (Node.js/Express) {#minimal-example-nodejsexpress}

```javascript
const pending = new Map(); // transactionId → user data

app.post('/provisioning/try', (req, res) => {
  const { transactionId, userId, email } = req.body;

  // Your business logic: can this user be provisioned?
  if (!isAllowed(email)) {
    return res.json({ approved: false, reason: 'Domain not allowed' });
  }

  // Store pending record with TTL
  pending.set(transactionId, { userId, email, createdAt: Date.now() });

  res.json({ approved: true });
});

app.post('/provisioning/confirm', (req, res) => {
  const { transactionId } = req.body;
  const data = pending.get(transactionId);

  if (data) {
    createUser(data); // Promote to real record
    pending.delete(transactionId);
  }

  res.sendStatus(200);
});

app.post('/provisioning/cancel', (req, res) => {
  pending.delete(req.body.transactionId);
  res.sendStatus(200);
});

// Cleanup unconfirmed records older than 1 hour
setInterval(() => {
  const cutoff = Date.now() - 3600000;
  for (const [id, data] of pending) {
    if (data.createdAt < cutoff) pending.delete(id);
  }
}, 600000);
```
