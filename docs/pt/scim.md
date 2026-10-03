---
layout: default
title: Aprovisionamento SCIM 2.0
nav_order: 13
locale: pt
---

# Aprovisionamento SCIM 2.0

A Authagonal suporta o SCIM 2.0 (System for Cross-domain Identity Management) para o aprovisionamento automatizado de utilizadores a partir de fornecedores de identidade empresariais como o Microsoft Entra ID, o Okta e o OneLogin.

## Descrição geral {#overview}

O SCIM é um protocolo de aprovisionamento de entrada: o seu fornecedor de identidade envia para a Authagonal as alterações de utilizadores e grupos. Complementa o aprovisionamento de saída TCC (Try-Confirm-Cancel) já existente, que envia utilizadores para aplicações a jusante.

**Operações suportadas:**
- CRUD de utilizadores (criar, ler, atualizar; eliminar através de desativação lógica)
- CRUD de grupos com gestão de membros
- Filtragem (operadores `eq` e `co` sobre `userName`, `externalId`, `displayName`)
- Paginação: baseada em cursor (`cursor`/`nextCursor`) tanto em utilizadores como em grupos; `startIndex` continua a ser aceite em grupos para clientes existentes, mas não é anunciado
- PATCH para atualizações parciais (incluindo a desativação com `active=false`)
- Mapeamento de grupos para funções resolvido na emissão de tokens

**Não suportado:** operações em massa, ordenação, ETags, gestão de palavras-passe através de SCIM.

Todos os recursos estão circunscritos ao cliente SCIM que os aprovisionou: um utilizador ou grupo criado pelo cliente de um token SCIM é invisível (404) para todos os outros clientes SCIM.

## Gerar um token SCIM {#generating-a-scim-token}

Os endpoints SCIM são autenticados com Bearer tokens estáticos. Gere tokens através da API de administração:

```http
POST /api/v1/scim/tokens
Authorization: Bearer {admin-token}
Content-Type: application/json

{
  "clientId": "your-client-id",
  "description": "Entra ID SCIM token",
  "expiresInDays": 365,
  "organizationId": "org_acme",
  "allowedEmailDomains": ["acme.example", "acme-eu.example"]
}
```

A resposta inclui o token em bruto **uma única vez**. É guardado como hash SHA-256 e não pode ser recuperado mais tarde, por isso guarde-o em segurança:

```json
{
  "tokenId": "abc123",
  "clientId": "your-client-id",
  "token": "base64-encoded-token",
  "description": "Entra ID SCIM token",
  "createdAt": "2024-01-01T00:00:00Z",
  "expiresAt": "2025-01-01T00:00:00Z",
  "organizationId": "org_acme",
  "allowedEmailDomains": ["acme.example", "acme-eu.example"]
}
```

Omita `expiresInDays` (ou passe `0`) para obter um token sem expiração.

### Etiquetar os utilizadores de um conector com uma organização {#tagging-a-connectors-users-with-an-organization}

`organizationId` é opcional. Quando está definido, todos os utilizadores aprovisionados através desse token são gravados com esse
`OrganizationId`, que é emitido como a claim `org_id` nos respetivos tokens. O SCIM não oferece a um conector nenhuma forma
de indicar qual dos seus clientes está a sincronizar: o SCIM core não define nenhum atributo de organização e a
extensão enterprise não está implementada (consulte *Suporte de esquemas* abaixo). Vinculá-la à credencial
responde a essa pergunta sem precisar de um cliente OAuth por cliente.

Se o omitir, os utilizadores ficam sem etiqueta, que é como todos os tokens se comportavam antes de isto existir. Nada é alguma vez
derivado do id do cliente.

Duas regras:

- **Apenas na criação.** Uma sincronização posterior através de um token com outra etiqueta não volta a etiquetar uma conta ativa.
- **Precede o aprovisionamento.** Uma resposta TCC `/try` só preenche uma organização que ainda esteja vazia
  (consulte [Aprovisionamento](provisioning.md)), pelo que uma vinculação explícita à credencial prevalece, e o payload de `/try`
  transporta o valor vinculado para que uma aplicação a jusante possa ver de que cliente veio a sincronização.

Quando `organizationId` identifica uma [organização](organizations) existente, a criação também grava uma associação `active` a essa organização (sem funções) e audita `scim.organization_member_added`, para que não seja depois recusado um token ao utilizador pela verificação de associação da organização. Um id que não identifique nenhuma organização fica apenas como etiqueta `org_id`, que é o que fazem os tokens emitidos antes de as organizações existirem. Tal como a etiqueta, a associação só é gravada na criação.

> **Etiquetar não é isolar.** A propriedade é aplicada por **cliente**, não por token. Dois tokens emitidos
> para o mesmo cliente são uma identidade com dois segredos, e cada um pode ler, renomear, desativar e
> eliminar o que o outro criou. Isso não é problema quando uma única parte os detém a todos. Se conectores
> que não confiam uns nos outros detiverem cada um o seu, atribua um cliente a cada um.

### Delimitar as identidades que um conector pode criar {#bounding-which-identities-a-connector-may-create}

`allowedEmailDomains` é o único controlo sobre **quais** utilizadores uma credencial SCIM pode aprovisionar. Defina-o.

Omiti-lo produz um token sem restrições, e sem restrições é mais abrangente do que parece. Um utilizador criado por SCIM é
gravado com `EmailConfirmed = true` (o endereço é tratado como comprovado a partir desse momento), pelo que um
conector sem restrições pode criar `ceo@some-other-company.example` como conta pré-verificada. Quando o verdadeiro
titular iniciar sessão mais tarde através de federação, um registo sem inícios de sessão externos existentes é adotado em vez de
recusado, pelo que o seu início de sessão fica vinculado a essa conta; e como `ScimProvisionedByClientId` continua a identificar o
conector que a criou, esse conector mantém a propriedade total do objeto: pode ler o perfil, renomear
o `userName`, desativá-lo (o que revoga todas as concessões) ou eliminá-lo, o que apaga as chaves de acesso e as
associações a grupos do utilizador e marca a linha com um tombstone, de modo que o conector legítimo desse domínio recebe 404 em todas as
operações.

Um token que omita o campo regista um aviso no momento da emissão, identificando o id do token.

Indique domínios simples (`acme.example`, não `@acme.example` e não um endereço). Um valor que nunca poderia corresponder é
recusado em vez de guardado, porque um limite que não permite nada parece idêntico a um conector mal configurado.

Os operadores também podem definir um limite na configuração:

```json
{
  "Scim": {
    "Clients": {
      "your-client-id": { "AllowedEmailDomains": ["acme.example"] }
    }
  }
}
```

Os dois são **intersetados**, e uma lista vazia de qualquer das origens significa "nenhum limite desta origem". Assim, ambas
vazias significa sem restrições; qualquer uma delas sozinha aplica-se por si; e quando ambas estão definidas, só são
permitidos os domínios presentes em ambas: emitir um token pode restringir o limite configurado por um operador, mas nunca alargá-lo.

É aplicado tanto na criação como em `PUT` e `PATCH`, pelo que uma mudança de nome não consegue mover uma conta para um domínio que a credencial
não tenha permissão para aprovisionar.

### Listar tokens {#listing-tokens}

```http
GET /api/v1/scim/tokens?clientId=your-client-id
Authorization: Bearer {admin-token}
```

### Revogar um token {#revoking-a-token}

```http
DELETE /api/v1/scim/tokens/{tokenId}?clientId=your-client-id
Authorization: Bearer {admin-token}
```

## Configurar o seu fornecedor de identidade {#configuring-your-identity-provider}

### URL do inquilino {#tenant-url}

```
https://your-authagonal-instance/scim/v2
```

### Autenticação {#authentication}

Utilize **OAuth Bearer Token** com o token gerado acima.

### Microsoft Entra ID {#microsoft-entra-id}

1. No portal do Azure, aceda a **Enterprise Applications** > a sua aplicação > **Provisioning**
2. Defina Provisioning Mode como **Automatic**
3. Introduza Tenant URL: `https://your-instance/scim/v2`
4. Introduza Secret Token: o token em bruto do passo de geração
5. Clique em **Test Connection** para verificar
6. Configure os mapeamentos de atributos (ver abaixo)

### Okta {#okta}

1. Na consola de administração do Okta, aceda a **Applications** > a sua aplicação > **Provisioning**
2. Ative **SCIM connector**
3. Defina Base URL: `https://your-instance/scim/v2`
4. Defina Authentication Mode: **HTTP Header**
5. Introduza o Bearer token

### OneLogin {#onelogin}

1. Na administração do OneLogin, aceda a **Applications** > a sua aplicação > **Provisioning**
2. Ative o aprovisionamento
3. Defina SCIM Base URL: `https://your-instance/scim/v2`
4. Defina SCIM Bearer Token

## Endpoints SCIM {#scim-endpoints}

| Método | Caminho | Descrição |
|--------|------|-------------|
| GET | `/scim/v2/Users` | Listar/filtrar utilizadores |
| GET | `/scim/v2/Users/{id}` | Obter um utilizador |
| POST | `/scim/v2/Users` | Criar um utilizador |
| PUT | `/scim/v2/Users/{id}` | Substituir um utilizador |
| PATCH | `/scim/v2/Users/{id}` | Atualização parcial |
| DELETE | `/scim/v2/Users/{id}` | Tombstone (desativa; um GET posterior devolve 404) |
| GET | `/scim/v2/Groups` | Listar/filtrar grupos |
| GET | `/scim/v2/Groups/{id}` | Obter um grupo |
| POST | `/scim/v2/Groups` | Criar um grupo |
| PUT | `/scim/v2/Groups/{id}` | Substituir um grupo |
| PATCH | `/scim/v2/Groups/{id}` | Adicionar/remover membros |
| DELETE | `/scim/v2/Groups/{id}` | Eliminar um grupo |
| GET | `/scim/v2/ServiceProviderConfig` | Capacidades |
| GET | `/scim/v2/Schemas` | Definições de esquemas |
| GET | `/scim/v2/ResourceTypes` | Tipos de recursos |

Todos os endpoints estão também mapeados sem o segmento `/v2` (por exemplo, `/scim/Users`) para os fornecedores de identidade que acrescentam o seu próprio caminho. Os endpoints de descoberta (`ServiceProviderConfig`, `Schemas`, `ResourceTypes` e os URLs base simples `/scim/` e `/scim/v2/`, que devolvem o ServiceProviderConfig) são anónimos; tudo o resto exige um Bearer token SCIM.

Os endpoints de utilizadores e de grupos têm um limite de 200 pedidos por minuto por cliente SCIM; os pedidos excedentes recebem um erro SCIM com o estado `429`.

## Mapeamento de atributos {#attribute-mapping}

### Atributos de utilizador {#user-attributes}

| Atributo SCIM | Campo da Authagonal |
|---------------|------------------|
| `userName` | `Email` |
| `name.givenName` | `FirstName` |
| `name.familyName` | `LastName` |
| `displayName` | `FirstName LastName` |
| `emails[type eq "work"].value` | `Email` |
| `active` | `IsActive` |
| `externalId` | `ExternalId` |
| `preferredLanguage` (recorrendo a `locale` na sua falta) | `Locale` |

### Atributos de grupo {#group-attributes}

| Atributo SCIM | Campo da Authagonal |
|---------------|------------------|
| `displayName` | `DisplayName` |
| `externalId` | `ExternalId` |
| `members` | `MemberUserIds` |

### Suporte de esquemas {#schema-support}

Apenas `User` e `Group` do SCIM 2.0 core (RFC 7643). As tabelas acima são o conjunto suportado completo.

A **extensão enterprise de utilizador não está implementada**, pelo que `employeeNumber`, `costCenter`, `organization`,
`division`, `department` e `manager` são aceites e ignorados em vez de guardados, tanto na criação como na substituição e no
PATCH. O Entra e o Okta mapeiam vários destes atributos nos seus mapeamentos de atributos predefinidos, pelo que um conector de origem
não precisa de os remover. (Antes da 0.27.0, um PATCH que transportasse um deles era rejeitado POR INTEIRO com
`400 invalidPath`, o que fazia falhar todas as sincronizações incrementais enquanto as criações eram bem-sucedidas, e podia deixar um
desaprovisionamento `active: false` bloqueado por causa de um atributo sem relação.)

O relaxamento é restrito: um caminho CORE mal escrito como `name.givenNam` continua a responder `400`, e os
atributos só de leitura (`id`, `meta`, `groups`) mantêm a sua recusa por `mutability`.

Note que o atributo enterprise `organization` **não** se torna o `org_id` do utilizador. Esse valor é
afirmado pelo próprio fornecedor de identidade do cliente, ao passo que a vinculação à credencial descrita acima é definida pelo
operador; utilize antes `organizationId` no token.

## Detalhes de comportamento {#behavior-details}

### Criação de utilizadores {#user-creation}
- Os utilizadores aprovisionados por SCIM são criados com `EmailConfirmed = true` (apenas SSO, sem palavra-passe).
- O campo `ScimProvisionedByClientId` regista qual o cliente SCIM que criou o utilizador.
- Se o cliente tiver `ProvisioningApps` configurado, o aprovisionamento TCC é desencadeado automaticamente. Se o aprovisionamento rejeitar o utilizador, a criação SCIM é revertida e a resposta é um `400` SCIM com `scimType: invalidValue` e uma mensagem fixa (o texto da própria aplicação a jusante não é deliberadamente reenviado ao cliente SCIM).
- Criar um utilizador cujo `userName` ou `externalId` já exista devolve um conflito SCIM `409`. As alterações de email através de PUT ou PATCH são verificadas quanto a conflitos da mesma forma.

### Desativação de utilizadores {#user-deactivation}
- `DELETE /scim/v2/Users/{id}` marca o recurso com um **tombstone**: desativa o utilizador, mantém o registo local e preenche `ScimDeletedAt`. Um `GET /scim/v2/Users/{id}` subsequente devolve **404**, como exige a §3.6 da RFC 7644 ("the service provider MUST return a 404 for all operations associated with the previously deleted resource"). Não confirme um desaprovisionamento lendo o recurso de volta e esperando `active: false`. A leitura devolve 404, e isso é sucesso.
- O registo é mantido em vez de apagado para que uma recontratação possa ser recriada: o tombstone liberta o `userName`/`externalId` de que um novo recurso precisa, enquanto a conta local, o seu histórico de auditoria e as suas associações a grupos sobrevivem.
- `PATCH` com `active = false` também desativa o utilizador.
- Os utilizadores desativados não conseguem iniciar sessão através de palavra-passe, SAML ou OIDC.
- Todas as concessões (tokens de atualização, sessões) são revogadas na desativação.
- O desaprovisionamento das aplicações a jusante só é desencadeado por `DELETE`; uma desativação por `PATCH` revoga as concessões, mas deixa as aplicações a jusante intactas.

### Filtragem {#filtering}
A gramática de filtros completa da §3.4.2.2 da RFC 7644 é suportada.

**Operadores:** `eq`, `ne`, `co`, `sw`, `ew`, `gt`, `ge`, `lt`, `le` e `pr` (presença).
**Lógicos:** `and`, `or`, `not (...)`, com agrupamento por parênteses. `and` tem precedência sobre `or`.
**Caminhos:** subatributos (`name.givenName`), atributos multivalor (`emails.value`), caminhos de valor (`emails[type eq "work"].value`) e nomes com prefixo URN (`urn:ietf:params:scim:schemas:core:2.0:User:userName`).

```
userName eq "user@example.com"
userName sw "sales-" and active eq true
emails[type eq "work"].value co "@acme.com"
not (title pr)
meta.lastModified gt "2026-01-01T00:00:00Z"
```

A semântica segue a RFC: a comparação de cadeias não distingue maiúsculas de minúsculas, um atributo multivalor corresponde quando qualquer elemento corresponde, e um atributo ausente torna falsa toda a comparação exceto `ne`. Uma entrada que não seja um filtro SCIM válido é rejeitada com `400` e `scimType: invalidFilter`, indicando o problema.

**Desempenho.** `userName eq` e `externalId eq` (as pesquisas que o Entra e o Okta emitem antes de cada criação ou atualização) são resolvidos através de pesquisas pontuais indexadas em vez de uma varredura da listagem, pelo que se mantêm rápidos com qualquer número de utilizadores. Todos os outros filtros são avaliados durante a paginação pelos utilizadores do cliente, de forma limitada: as PII dos utilizadores estão encriptadas em repouso e só são pesquisáveis através de índices cegos, pelo que predicados mais ricos não podem ser delegados no armazenamento. Com paginação por cursor, `totalResults` é **omitido** enquanto `nextCursor` estiver presente e é o total exato quando `nextCursor` estiver ausente. Consulte Paginação.

### Paginação {#pagination}
As listagens de utilizadores utilizam **paginação por cursor**. Cada página de `GET /scim/v2/Users` devolve uma propriedade `nextCursor` na resposta da lista; passe-a de volta como `?cursor=` para obter a página seguinte. Quando `nextCursor` está ausente, a listagem está completa. O tamanho da página é controlado por `count` (predefinição 100, máximo 200).

Pedir um `startIndex` superior a 1 no endpoint Users devolve um erro `400` que o encaminha para a paginação por cursor; não é oferecida paginação por deslocamento para além da primeira página. `totalResults` é **totalmente omitido** enquanto `nextCursor` estiver presente e só indica o total exato na última página. Deliberadamente, não indica o tamanho da página devolvida: um cliente de sincronização que lia `totalResults`, verificava que era igual ao número de recursos que acabara de receber e concluía que tinha o diretório inteiro lia o inquilino de forma incompleta sem dar por isso. Conduza o ciclo a partir de `nextCursor`, nunca de `totalResults`, e trate um `totalResults` ausente como "ainda não conhecido", não como zero.

**As listagens de grupos também são paginadas por cursor.** `GET /scim/v2/Groups` devolve um `nextCursor` tanto na forma filtrada
como na não filtrada; siga-o da mesma forma. `startIndex` continua a ser aceite em Groups para os clientes que já o
utilizam, mas **não é anunciado** em `ServiceProviderConfig` e não se deve confiar nele: `pagination.index` é uma
afirmação sobre o fornecedor, não sobre uma coleção, e `/Users` não o suporta, pelo que o único valor que é
verdadeiro em todo o lado é `false`. Utilize cursores, que funcionam em ambos.

Uma listagem de grupos filtrada percorre janelas limitadas em vez de materializar o inquilino inteiro, pelo que pode devolver
uma página vazia quando ainda existem correspondências mais à frente. Quando isso acontece, devolve um `nextCursor` e **omite**
`totalResults`: uma página vazia com cursor significa "continue", e uma página vazia sem cursor significa que o
conjunto filtrado está realmente vazio. Não trate a primeira página vazia como o fim da coleção.

`count=0` devolve `totalResults` sem recursos (§3.4.2.4 da RFC 7644) em ambas as coleções, e um `count`
negativo é recusado com um `400` em vez de ajustado ao limite.

### Associação a grupos através de PATCH {#group-membership-via-patch}
`PATCH /scim/v2/Groups/{id}` aceita os formatos de associação que os principais fornecedores de identidade efetivamente enviam:

- **Adicionar membros:** `op: "add"` com `path: "members"` e uma matriz de valores com objetos `{ "value": "user-id" }`. Os duplicados são ignorados.
- **Substituir membros:** `op: "replace"` com `path: "members"` substitui toda a associação pela matriz fornecida.
- **Remover um membro específico (matriz de valores):** `op: "remove"` com `path: "members"` e uma matriz de valores com os ids dos membros a remover (o formato que o Entra ID envia).
- **Remover um membro específico (filtro no caminho):** `op: "remove"` com `path: 'members[value eq "user-id"]'`, com o id transportado no filtro do caminho e sem valor (o formato que o Okta envia no desaprovisionamento).
- **Remover todos os membros:** `op: "remove"` com `path: "members"` e sem valor esvazia o grupo.

### Mapeamento de grupos para funções {#group-to-role-mapping}
A associação a um grupo SCIM pode conceder funções da aplicação. Os mapeamentos são uma linha por par (grupo, função), e um grupo pode conceder várias funções. São resolvidos na **emissão de tokens**: as funções efetivas de um utilizador são as funções que lhe foram atribuídas diretamente mais as funções de todos os grupos mapeados a que pertence, pelo que adicionar ou remover um membro de um grupo produz efeito no token seguinte sem alterar o registo do utilizador. Um armazenamento de mapeamentos vazio não tem qualquer efeito.

Os mapeamentos são persistidos através de `IScimGroupRoleMappingStore` (implementado pelos fornecedores de armazenamento Azure e AWS; caso contrário, é registada uma implementação predefinida em memória) e são geridos pela superfície de administração da aplicação anfitriã, não através da própria API SCIM.

Opcionalmente, um cliente com `IncludeGroupsInTokens` ativado também recebe os nomes de apresentação dos grupos SCIM do utilizador como claim `groups` nos tokens emitidos.

## Limitações conhecidas {#known-limitations}

- **Sem operações em massa:** os utilizadores e os grupos têm de ser aprovisionados individualmente.
- **Sem ordenação:** as listagens de utilizadores devolvem a ordem do armazenamento com paginação por cursor; as listagens de grupos são ordenadas por data de criação.
- **Sem gestão de palavras-passe:** os utilizadores aprovisionados por SCIM autenticam-se apenas através de SSO.
- **Tombstone, não apagamento:** `DELETE` desativa o recurso e marca-o com um tombstone (um `GET` posterior devolve 404, conforme a §3.6 da RFC 7644) em vez de remover permanentemente o registo local do utilizador. Para o apagamento, utilize a API de administração.
