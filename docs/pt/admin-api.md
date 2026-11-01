---
layout: default
title: API de administração
locale: pt
---

# API de administração

Os endpoints de administração exigem um token de acesso JWT com o âmbito `authagonal-admin` (configurável através de `AdminApi:Scope`).

Todos os endpoints estão sob `/api/v1/`.

## Obter o primeiro token de administração {#bootstrapping-the-first-admin-token}

Todos os endpoints `/api/v1/*` exigem um bearer token que inclua o âmbito de administração, mas a própria API de administração (e o [registo dinâmico de clientes](client-registration)) **recusa criar ou atualizar qualquer cliente que detenha esse âmbito** (`403 forbidden_scope`), pelo que um cliente criado em tempo de execução nunca consegue escalar para administrador. A única forma de emitir um token de administração é um **cliente inicializado pela configuração**: as entradas da secção de configuração `Clients:` são inseridas ou atualizadas no arranque por `ClientSeedService`, e a configuração é considerada fidedigna; a proteção contra âmbitos proibidos aplica-se apenas às APIs de tempo de execução.

Inicialize um cliente `client_credentials` com o âmbito de administração em `appsettings.json` (ou nas variáveis de ambiente / no armazenamento de segredos equivalentes):

```json
{
  "Clients": [
    {
      "Id": "admin-cli",
      "Name": "Admin CLI",
      "ClientSecret": "a-long-random-secret",
      "GrantTypes": ["client_credentials"],
      "Scopes": ["authagonal-admin"]
    }
  ]
}
```

(O `ClientSecret` é convertido em hash no arranque; forneça antes `SecretHashes` se preferir manter na configuração apenas um valor já em hash. `ClientId`/`ClientName`/`AllowedGrantTypes`/`AllowedScopes` são aceites como alternativas a `Id`/`Name`/`GrantTypes`/`Scopes`.)

Em seguida, troque as credenciais por um token no endpoint de token padrão:

```bash
curl -X POST https://auth.example.com/connect/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=admin-cli" \
  -d "client_secret=a-long-random-secret" \
  -d "scope=authagonal-admin"
```

```json
{ "access_token": "eyJhbGci...", "token_type": "Bearer", "expires_in": 1800, "scope": "authagonal-admin" }
```

A concessão `client_credentials` valida o âmbito pedido contra os `AllowedScopes` do cliente; como o cliente inicializado detém `authagonal-admin`, o token é emitido. Utilize-o como `Authorization: Bearer {access_token}` em todas as chamadas de administração:

```bash
curl https://auth.example.com/api/v1/clients -H "Authorization: Bearer eyJhbGci..."
```

Guarde o segredo do cliente inicializado no armazenamento de segredos da sua implementação; rodá-lo implica uma alteração de configuração + reinício.

## Utilizadores {#users}

### Obter utilizador {#get-user}

```
GET /api/v1/profile/{userId}
```

Devolve o perfil e ainda aquilo de que uma consola de suporte precisa para diagnosticar um problema de início de sessão:
`emailConfirmed`, `isActive`, `lockoutEnd`, `accessFailedCount`, `roles`, os
`externalLogins` associados e `hasPassword` (apenas a presença, nunca o hash). Este último é a diferença
entre "esqueceram-se da palavra-passe" e "nunca tiveram uma, iniciam sessão com SSO",
que levam a conselhos opostos.

Devolve os detalhes do utilizador, incluindo as associações de início de sessão externo.

### O utilizador existe {#user-exists}

```
GET /api/v1/profile/{userId}/exists
```

Devolve `204` se o utilizador existir e `404` caso contrário (uma verificação de existência barata, sem corpo).

### Registar utilizador {#register-user}

```
POST /api/v1/profile/
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Cria um utilizador e envia um email de verificação. Devolve `409 user_exists` se o email já estiver a ser utilizado.

Campos opcionais exclusivos do administrador: `userId` (id fornecido por quem chama; `409 user_id_in_use` em caso de colisão), `emailConfirmed` (cria o utilizador já verificado, sem enviar o email de verificação), `companyName`, `organizationId`, `phone`, `locale` e `customAttributes` (um mapa de cadeias persistido no utilizador e reencaminhado para os destinos de aprovisionamento).

`skipProvisioning: true` cria a identidade sem executar o aprovisionamento. Destina-se a uma aplicação
própria que é ELA MESMA um destino de aprovisionamento e que já está a meio de configurar este utilizador: está
a chamar aqui para emitir a identidade, não para ser contactada de volta acerca de um utilizador que está a meio de
criar. Sem isto, essa aplicação recebe o seu próprio Try para um utilizador construído a meio, que transporta apenas os
atributos que sobreviveram à ida e volta e, se recuperar, acaba por aprovisionar o utilizador duas vezes.

### Atualizar utilizador {#update-user}

```
PUT /api/v1/profile/
Content-Type: application/json

{
  "userId": "user-id",
  "firstName": "Jane",
  "lastName": "Smith",
  "organizationId": "new-org-id"
}
```

`userId` é obrigatório; todos os outros campos são opcionais, e só os campos fornecidos são atualizados.

`isActive` desativa ou reativa a conta. `emailConfirmed` (também aceite como `emailVerified`)
marca o endereço como confirmado sem enviar um email de verificação, para quando a posse
tiver sido comprovada de outra forma.

Alterar `organizationId`, ou desativar a conta, desencadeia:
- A rotação do SecurityStamp (invalida todas as sessões por cookie no prazo de 30 minutos)
- A revogação de todos os tokens de atualização

Um bloqueio que só produz efeito no próximo início de sessão não é um bloqueio, e é por isso que a desativação revoga
em vez de esperar pela expiração.

### Pesquisar utilizadores {#search-users}

```
GET /api/v1/profile/search?q=jane&maxResults=20
```

Pesquisa por prefixo sobre os índices de email e de nome. Devolve `{ "users": [ ... ] }`.

### Obter utilizador por email {#get-user-by-email}

```
GET /api/v1/profile/by-email?email=jane@example.com
```

Pesquisa exata, distinta da pesquisa por prefixo, que pode devolver várias pessoas. Quem chama para resolver
"este endereço" em "esta conta" quer uma resposta ou nenhuma. `404` se esse utilizador não existir.

### Listar utilizadores {#list-users}

```
GET /api/v1/profile?organizationId=&count=100&continuationToken=
```

Listagem do diretório paginada por cursor; passe de volta o `continuationToken` devolvido para obter a página seguinte e
pare quando for null. Cursores em vez de deslocamentos porque o armazenamento pagina por token: um deslocamento voltaria
a varrer desde o início em cada página.

### Que utilizadores existem {#which-users-exist}

```
POST /api/v1/profile/exists
Content-Type: application/json

{ "userIds": [ "a", "b", "c" ] }
```

Devolve o subconjunto que existe, mais `truncated: true` quando o pedido excedeu o limite de 500 ids, para que
quem chama seja informado de que o seu lote foi cortado, em vez de receber, sem aviso, uma resposta sobre 500 de 600. Serve para
reconciliar um conjunto de ids com o de outro sistema.

### Estado de MFA de muitos utilizadores {#mfa-status-for-many-users}

```
POST /api/v1/profile/mfa-status
Content-Type: application/json

{ "userIds": [ "a", "b", "c" ] }
```

Devolve `{ "statuses": { "a": true, "b": false }, "truncated": false }`: `true` significa que o utilizador tem pelo menos uma credencial de MFA. Limitado a 500 ids; `truncated: true` indica que o pedido foi cortado. Serve para os indicadores "utiliza MFA" numa vista de diretório.

### Definir uma palavra-passe {#set-a-password}

```
POST /api/v1/profile/{userId}/set-password
Content-Type: application/json

{ "password": "N3w!Password" }
```

O caminho de suporte para alguém que ficou sem acesso a uma conta cujo endereço já não lhe chega. Sujeito
à política de palavras-passe. Revoga todos os tokens de atualização e roda o security stamp: uma alteração de
palavra-passe que deixa as sessões antigas em funcionamento não mudou quem pode agir como essa pessoa.

### Desbloquear um utilizador {#unlock-a-user}

```
POST /api/v1/profile/{userId}/unlock
```

Limpa o bloqueio e a respetiva contagem de tentativas falhadas, deixando a pessoa voltar a entrar já, em vez de quando o
bloqueio acabar por expirar.

### Eliminar utilizador {#delete-user}

```
DELETE /api/v1/profile/{userId}
```

Elimina o utilizador, revoga todas as concessões e desaprovisiona-o de todas as aplicações a jusante (na medida do possível).

### Confirmar email {#confirm-email}

```
POST /api/v1/profile/confirm-email?token={token}
```

### Enviar email de verificação {#send-verification-email}

```
POST /api/v1/profile/{userId}/send-verification-email
```

### Associar identidade externa {#link-external-identity}

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### Desassociar identidade externa {#unlink-external-identity}

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

### Listar as organizações de um usuário {#list-a-users-organizations}

```
GET /api/v1/profile/{userId}/organizations
```

Devolve `{ "items": [ { "organizationId", "slug", "name", "status", "roles" } ] }`: cada organização da qual o usuário tem uma associação, seja qual for o status. Uma associação cuja organização não existe mais é omitida.

### Convidar um usuário {#invite-user}

```
POST /api/v1/profile/invite
Content-Type: application/json

{
  "email": "ada@acme.com",
  "firstName": "Ada",
  "lastName": "Lovelace",
  "locale": "en",
  "organizationId": "org_0123456789abcdef0123456789abcdef",
  "organizationRoles": ["Auditor"]
}
```

Somente `email` é obrigatório. Cria a conta (não confirmada, sem senha utilizável) e envia por e-mail um link de uso único, válido por 7 dias, para `/accept-invite`. Com `organizationId` (o id da organização, não o slug), o convite é para essa organização: é gravada uma associação `invited` com `organizationRoles`, que passa a `active` quando o convidado define uma senha. O e-mail é redigido no primeiro `Accept-Language` da sua requisição que a biblioteca inclui, senão em `locale` e, senão, em inglês. Veja [Organizações](organizations#inviting-people).

Devolve `200` com `{ "userId": "...", "email": "ada@acme.com" }` (endereço em minúsculas). Uma falha é `{ "error", "error_description" }`:

| Status | `error` | Quando |
|---|---|---|
| 400 | `email_required` | `email` está ausente ou em branco |
| 400 | `invalid_email` | `email` não é um endereço utilizável |
| 400 | `invalid_role` | um papel da organização começa com `tenant:` ou `platform:` |
| 404 | `organization_not_found` | `organizationId` não designa nenhuma organização |
| 409 | `user_already_exists` | o endereço já tem uma conta |

### Reenviar o convite {#resend-invite}

```
POST /api/v1/profile/{userId}/resend-invite
```

Gera um link novo e o envia. Devolve `200` com `{ "sent": true }`. `404 user_not_found` para um usuário desconhecido, `400 already_accepted` depois que o endereço é confirmado (use uma redefinição de senha) e `502 invite_email_failed` quando o e-mail não pode ser enviado.

## Gestão de MFA {#mfa-management}

### Obter estado de MFA {#get-mfa-status}

```
GET /api/v1/profile/{userId}/mfa
```

Devolve o estado de MFA e os métodos inscritos de um utilizador.

### Repor toda a MFA {#reset-all-mfa}

```
DELETE /api/v1/profile/{userId}/mfa
```

Remove todas as credenciais de MFA e define `MfaEnabled=false`. O utilizador terá de voltar a inscrever-se, se tal for exigido.

### Remover uma credencial de MFA específica {#remove-specific-mfa-credential}

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

Remove uma credencial de MFA específica (por exemplo, um autenticador perdido). Se o último método principal for removido, a MFA é desativada.

## Fornecedores de SSO {#sso-providers}

### Fornecedores SAML {#saml-providers}

```
POST   /api/v1/saml/connections                    # Create
GET    /api/v1/saml/connections/{connectionId}     # Get one
PUT    /api/v1/saml/connections/{connectionId}     # Update (partial: only supplied fields change)
DELETE /api/v1/saml/connections/{connectionId}     # Delete
```

A criação exige `connectionName`, `entityId` e **exatamente um de** `metadataLocation` (um URL de metadados) ou `metadataXml` (metadados do IdP colados, para IdPs sem URL de metadados; são validados por análise e condensados ao guardar). Opcionais: `nameIdFormat` (omita-o para a predefinição emailAddress, `"none"` para omitir a NameIDPolicy, recomendado para ADFS, ou um URN de formato de NameID), `signAuthnRequests`, `iconUrl`, `allowedDomains`, `disableJitProvisioning`, `organizationId`. Cada ligação recebe um par de chaves de SP gerado pelo servidor, que nunca é devolvido pela API. Consulte [SAML](saml) para mais detalhes.

`organizationId` circunscreve a ligação a uma [organização](organizations): só é oferecida quando essa organização está selecionada, os seus `allowedDomains` só são correspondidos dentro dela (e *não* são escritos no índice de domínios de SSO de todo o inquilino), e todas as pessoas que iniciam sessão através dela tornam-se membros da mesma. Omitido ou `null` = uma ligação ao nível do inquilino. Uma organização que não exista devolve `400 unknown_organization`. Na atualização, `null` (o campo ausente) deixa o âmbito inalterado, `""` devolve a ligação ao nível do inquilino, e qualquer das direções reescreve o índice de domínios em conformidade. Consulte [Ligações com âmbito de organização](self-service-sso#organisation-scoped-connections).

### Fornecedores OIDC {#oidc-providers}

```
POST   /api/v1/oidc/connections                    # Create
GET    /api/v1/oidc/connections/{connectionId}     # Get one
DELETE /api/v1/oidc/connections/{connectionId}     # Delete
```

A criação exige `connectionName`, `metadataLocation`, `clientId`, `clientSecret`, `redirectUrl`. Opcionais: `iconUrl`, `allowedDomains`, `passthroughParams`, `organizationId` (com o mesmo significado que numa ligação SAML, acima). O segredo do cliente está protegido em repouso e nunca é devolvido. Consulte [Federação OIDC](oidc-federation).

### Domínios de SSO {#sso-domains}

```
GET    /api/v1/sso/domains                 # List all
```

## Clientes {#clients}

Gira clientes OAuth em tempo de execução. Todas as rotas exigem a política `IdentityAdmin` (o âmbito de administração).

```
GET    /api/v1/clients              # List all clients
GET    /api/v1/clients/{clientId}   # Get one client
POST   /api/v1/clients              # Create a client
PUT    /api/v1/clients/{clientId}   # Update a client
DELETE /api/v1/clients/{clientId}   # Delete a client
```

### Criar / atualizar cliente {#create--update-client}

```
POST /api/v1/clients
Content-Type: application/json

{
  "clientId": "my-app",
  "clientName": "My Application",
  "allowedGrantTypes": ["authorization_code"],
  "redirectUris": ["https://app.example.com/callback"],
  "allowedScopes": ["openid", "profile", "email"]
}
```

`POST` devolve `409` se o cliente já existir. `PUT` atualiza um cliente existente (`404` se não for encontrado); na atualização, só os âmbitos recém-adicionados são verificados quanto a escalada.

Notas:

- **Os hashes dos segredos nunca são devolvidos.** `clientSecretHashes` é removido de todas as respostas (listar, obter, criar, atualizar). Na atualização, omitir `clientSecretHashes` preserva o segredo guardado; fornecer novos hashes roda-o.
- **O âmbito de administração não pode ser concedido a um cliente.** Pedir `AdminApi:Scope` (predefinição `authagonal-admin`) em `allowedScopes` devolve `403 forbidden_scope`; nenhum cliente pode deter o âmbito de administração, caso contrário um cliente `client_credentials` poderia emitir tokens de administração indefinidamente.
- Adicionar âmbitos que quem chama não tem permissão para conceder devolve `403`.

## Âmbitos {#scopes}

Gira âmbitos OAuth personalizados em tempo de execução. Consulte [Âmbitos OAuth](scopes) para o modelo completo de âmbitos.

```
GET    /api/v1/scopes           # List all scopes
GET    /api/v1/scopes/{name}    # Get one scope
POST   /api/v1/scopes           # Create a scope
PUT    /api/v1/scopes/{name}    # Update a scope (only supplied fields change)
DELETE /api/v1/scopes/{name}    # Delete a scope
```

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing, read-only",
  "description": "View invoices and payment history",
  "userClaims": ["billing_plan"]
}
```

Devolve `201` na criação (`409` se o âmbito já existir), o JSON do âmbito em obter/atualizar e `204` na eliminação.

## Aplicações de aprovisionamento {#provisioning-apps}

Gira os destinos de aprovisionamento a jusante em tempo de execução. Todas as rotas exigem a política `IdentityAdmin`.

```
GET    /api/v1/provisioning/apps               # List apps (also returns the configured limit)
POST   /api/v1/provisioning/apps               # Create an app
PUT    /api/v1/provisioning/apps/{appId}       # Update an app
DELETE /api/v1/provisioning/apps/{appId}       # Delete an app
POST   /api/v1/provisioning/apps/{appId}/test  # Send a test /try call to the app's callback
```

### Criar / atualizar aplicação de aprovisionamento {#create--update-provisioning-app}

```
POST /api/v1/provisioning/apps
Content-Type: application/json

{
  "name": "Backend",
  "callbackUrl": "https://api.example.com/provisioning",
  "apiKey": "secret-api-key",
  "tryTimeoutSeconds": 30
}
```

- `name` e `callbackUrl` são obrigatórios; `callbackUrl` tem de ser um URL `http(s)` absoluto.
- `tryTimeoutSeconds` é ajustado ao intervalo 5–300.
- **A chave de API nunca é devolvida.** As respostas expõem `hasApiKey` (um booleano) em vez da própria chave. Na atualização, omitir `apiKey` deixa-a inalterada, uma cadeia vazia limpa-a e um valor substitui-a.
- A criação está sujeita a uma quota configurável por implementação (`IProvisioningAppQuota`); excedê-la devolve `400 provisioning_app_limit`. A resposta da listagem inclui o `limit` atual.

### Testar uma aplicação de aprovisionamento {#test-a-provisioning-app}

```
POST /api/v1/provisioning/apps/{appId}/test
```

Envia um `POST {callbackUrl}/try` sintético com um payload de exemplo (e a chave de API da aplicação como bearer token, se estiver definida) e devolve `{ success, statusCode, body }` para que possa verificar a conectividade a partir da interface de administração.

## Funções {#roles}

### Listar funções {#list-roles}

```
GET /api/v1/roles
```

### Obter função {#get-role}

```
GET /api/v1/roles/{roleId}
```

### Criar função {#create-role}

```
POST /api/v1/roles
Content-Type: application/json

{
  "name": "admin",
  "description": "Administrator role"
}
```

### Atualizar função {#update-role}

```
PUT /api/v1/roles/{roleId}
Content-Type: application/json

{
  "name": "admin",
  "description": "Updated description"
}
```

### Eliminar função {#delete-role}

```
DELETE /api/v1/roles/{roleId}
```

### Atribuir função a um utilizador {#assign-role-to-user}

```
POST /api/v1/roles/assign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

A atribuição é feita pelo **nome da função**, não pelo id da função. Devolve a lista de funções atualizada do utilizador.

### Retirar função a um utilizador {#unassign-role-from-user}

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

### Obter as funções de um utilizador {#get-users-roles}

```
GET /api/v1/roles/user/{userId}
```

### Utilizadores com uma função {#users-in-a-role}

```
GET /api/v1/roles/{roleName}/users?maxResults=200
```

O inverso do anterior (quem detém esta função), respondido a partir de um índice de membros de funções em vez de
ler todos os utilizadores. Devolve `{ "roleName": "...", "members": [ { "userId", "email", "firstName",
"lastName", "roles" } ] }`; cada membro transporta o seu conjunto completo de funções, porque uma consola que lista uma
função quase sempre quer mostrar o que mais os seus membros têm.

`404 role_not_found` para uma função que não existe, em vez de uma lista vazia: "ninguém detém isto"
e "escreveu mal o nome da função" são problemas diferentes. `501 not_supported` se o armazenamento
configurado não indexar os membros das funções, pela mesma razão: uma lista de membros vazia seria lida como
"ninguém administra isto".

As contas escritas antes de o índice existir são invisíveis para ele até serem reindexadas
(`IUserStore.ReindexUserAsync`, que insere ou atualiza as associações de um utilizador a funções sem remover nenhuma).

## Organizações {#organizations}

As organizações, seus membros e seus domínios de e-mail (veja [Organizações](organizations) para o que significam). As falhas são `{ "error": "<code>", "error_description": "..." }` com status `400` (entrada inválida), `404` (não encontrado) ou `409` (conflito).

As listagens são paginadas com `?cursor={cursor}&limit={limit}`: `limit` é limitado a 1..200 (padrão 50) e a resposta é `{ "items": [...], "nextCursor": "..." }`, com `nextCursor` nulo na última página. Devolva `nextCursor` sem modificação; um cursor que a listagem não emitiu é `400 invalid_cursor`.

Uma organização é `{ "id", "slug", "name", "brandingJson", "metadata", "enabled", "requireMembershipForTokens", "allowAutoMembership", "createdAt", "updatedAt", "domains" }`.

### Listar organizações {#list-organizations}

```
GET /api/v1/organizations?cursor={cursor}&limit={limit}
```

`200`, ordenadas por id. `400 invalid_cursor`.

### Criar uma organização {#create-organization}

```
POST /api/v1/organizations
Content-Type: application/json

{
  "slug": "acme",
  "name": "Acme Corp",
  "brandingJson": "{\"appName\":\"Acme\",\"primaryColor\":\"#1a56db\"}",
  "enabled": true,
  "requireMembershipForTokens": true,
  "allowAutoMembership": false,
  "metadata": { "plan": "enterprise" }
}
```

`slug` e `name` são obrigatórios; o restante é opcional e mantém o padrão da organização se omitido. `brandingJson` é uma cadeia que contém um objeto JSON. Devolve `201` com um cabeçalho `Location` e a organização; seu `id` é `org_` seguido de 32 dígitos hexadecimais.

| Status | `error` | Quando |
|---|---|---|
| 400 | `slug_required` | `slug` está ausente ou em branco |
| 400 | `slug_invalid` | `slug` não tem de 1 a 64 letras minúsculas, dígitos e hífens internos |
| 400 | `name_required` | `name` está ausente ou em branco |
| 400 | `invalid_branding_json` | `brandingJson` não é um objeto JSON |
| 409 | `slug_taken` | outra organização tem o valor como slug ou como id, inclusive numa criação concorrente |

### Obter uma organização {#get-organization}

```
GET /api/v1/organizations/{idOrSlug}
```

`200`, buscada primeiro por id e depois por slug. `404 organization_not_found`.

### Atualizar uma organização {#update-organization}

```
PUT /api/v1/organizations/{id}
Content-Type: application/json

{ "name": "Acme Corporation", "enabled": true }
```

Todos os campos são opcionais e alteram apenas a si mesmos: `name`, `brandingJson`, `enabled`, `requireMembershipForTokens`, `allowAutoMembership` e `metadata` (substituído por inteiro; `{}` o esvazia). `slug` é aceito apenas para ser recusado quando difere. Devolve `200` com a organização. `404 organization_not_found`, `400 slug_immutable`, `400 invalid_branding_json`.

### Excluir uma organização {#delete-organization}

```
DELETE /api/v1/organizations/{id}
```

Exclui todas as associações e depois a organização. `204`; `404 organization_not_found`.

### Listar membros {#list-members}

```
GET /api/v1/organizations/{id}/members?cursor={cursor}&limit={limit}
```

Uma página, ordenada por id de usuário, de `{ "organizationId", "userId", "email", "name", "status", "roles", "createdAt", "updatedAt" }`; `email` e `name` vêm do registro do usuário e são nulos quando ele não existe mais. `404 organization_not_found`, `400 invalid_cursor`.

### Adicionar um membro {#add-member}

```
POST /api/v1/organizations/{id}/members
Content-Type: application/json

{ "userId": "user-id", "status": "active", "roles": ["Auditor"] }
```

Designa um usuário existente por `userId` (verificado primeiro) ou por `email`. `status` é `invited`, `active` (padrão) ou `suspended`; `roles` é opcional. Devolve `201` com um cabeçalho `Location` e a associação.

| Status | `error` | Quando |
|---|---|---|
| 404 | `organization_not_found` | essa organização não existe |
| 404 | `user_not_found` | nem `userId` nem `email` designam um usuário |
| 409 | `membership_exists` | o usuário já é membro |
| 400 | `invalid_role` | um papel começa com `tenant:` ou `platform:` |
| 400 | `invalid_status` | `status` não é nenhum dos três |

### Atualizar um membro {#update-member}

```
PUT /api/v1/organizations/{id}/members/{userId}
Content-Type: application/json

{ "status": "suspended", "roles": [] }
```

Ambos os campos são opcionais. Devolve `200` com a associação. `404 membership_not_found`, `400 invalid_status`, `400 invalid_role`.

### Remover um membro {#remove-member}

```
DELETE /api/v1/organizations/{id}/members/{userId}
```

`204`; `404 membership_not_found`.

### Adicionar um domínio {#add-domain}

```
POST /api/v1/organizations/{id}/domains
Content-Type: application/json

{ "domain": "acme.com" }
```

Reivindica o domínio, não verificado. O domínio é primeiro convertido para minúsculas e tem os espaços aparados, e deve ser um nome simples como `acme.com`. Devolve `201` com um cabeçalho `Location` e `{ "domain", "verified", "verifiedAt", "createdAt", "recordName", "recordValue" }`; publique um registro TXT em `recordName` com o valor `recordValue` (veja [Verificação de domínios](organizations#domain-verification)).

| Status | `error` | Quando |
|---|---|---|
| 404 | `organization_not_found` | essa organização não existe |
| 400 | `domain_invalid` | não é um nome de domínio simples |
| 409 | `domain_exists` | esta organização já o reivindica |
| 409 | `domain_taken` | outra organização o reivindica, verificado ou não |

### Verificar um domínio {#verify-domain}

```
POST /api/v1/organizations/{id}/domains/{domain}/verify
```

Consulta o registro TXT. Devolve `200` com o domínio (`verified: true`), inclusive quando já estava verificado. `404 organization_not_found`, `404 domain_not_found`, `409 domain_taken` e `409 verification_failed` quando o registro está ausente ou incorreto; seu `error_description` dá o nome e o valor a publicar e o que foi encontrado.

### Remover um domínio {#remove-domain}

```
DELETE /api/v1/organizations/{id}/domains/{domain}
```

`204`. Os membros não são tocados. `404 organization_not_found`, `404 domain_not_found`.

### Criar organizações por backfill {#backfill-organizations}

```
POST /api/v1/organizations/backfill
Content-Type: application/json

{ "dryRun": true }
```

Cria organizações e associações a partir do `OrganizationId` legado dos usuários. `dryRun` é true por padrão, com ou sem corpo, e nesse caso nada é gravado. Devolve `200` com `{ "dryRun", "organizationsCreated", "membershipsCreated", "usersSkipped", "organizations": [ { "legacyValue", "slug", "users" } ], "truncated" }`. Veja [Backfill](organizations#backfill).

## Tokens SCIM {#scim-tokens}

### Gerar token {#generate-token}

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id",
  "description": "Entra provisioning",
  "expiresInDays": 365
}
```

`description` e `expiresInDays` são opcionais (omita `expiresInDays` para obter um token sem expiração). Devolve o token em bruto uma única vez. Guarde-o em segurança, pois não pode ser obtido novamente.

`organizationId` é opcional e etiqueta cada usuário criado pelo token (veja [Organizações](organizations#membership-from-a-scim-token)). Deve designar uma organização existente: caso contrário o resultado é `400 unknown_organization`, e um valor malformado dá `400 invalid_request`.

### Listar tokens {#list-tokens}

```
GET /api/v1/scim/tokens?clientId=client-id
```

Devolve os metadados do token (ID, data de criação) sem o valor do token em bruto.

### Revogar token {#revoke-token}

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## Tokens {#tokens}

### Personificar utilizador {#impersonate-user}

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid%20profile
```

Emite tokens (de acesso, de atualização e, quando `openid` é pedido, o id token) em nome de um utilizador, sem exigir as suas credenciais. Útil para testes e suporte. Os parâmetros são passados como query strings.

| Parâmetro de consulta | Obrigatório | Descrição |
|---|---|---|
| `clientId` | Sim | O cliente para o qual os tokens são emitidos. Os tempos de vida dos tokens provêm da configuração deste cliente. |
| `userId` | Sim | O utilizador a personificar. |
| `scopes` | Não | Lista de âmbitos **separados por espaços** (codifique os espaços no URL). Predefine-se para os `AllowedScopes` do cliente quando omitido. |

Restrições:

- Os âmbitos estão limitados aos `AllowedScopes` do cliente; pedir qualquer âmbito que o próprio cliente não pudesse pedir devolve `400 invalid_scope`.
- O âmbito de administração (`AdminApi:Scope`, predefinição `authagonal-admin`) **não pode** ser emitido através deste endpoint; pedi-lo devolve `403 forbidden_scope`. Isto impede que um token de administração (possivelmente com tempo limitado) emita um token de acesso/atualização de administração de longa duração.

A resposta é uma resposta de token padrão com `access_token`, `refresh_token`, `id_token` opcional, `expires_in` e o `scope` concedido (separado por espaços).
