---
layout: default
title: Migração
locale: pt
---

# Migração a partir do Duende IdentityServer

O pacote `Authagonal.Migration` executa uma migração única do Duende IdentityServer + SQL
Server para os armazenamentos do Authagonal. O mesmo motor está disponível de duas formas:

- **Executor alojado** (recomendado): um serviço em segundo plano dentro do seu anfitrião Authagonal que executa a
  migração uma vez na implementação, condicionado à liderança do cluster, sem bloquear o arranque.
- **CLI**: `tools/Authagonal.Migration.Cli`, para execuções locais/offline contra um destino Table Storage.

O SqlClient existe apenas neste pacote, pelo que os anfitriões que não migram nunca o herdam.

## Executor alojado {#hosted-runner}

Adicione-o depois de `AddAuthagonal` (depende dos armazenamentos, do fornecedor de segredos e da liderança do cluster):

```csharp
builder.Services.AddAuthagonal(builder.Configuration, c => c.UseAzureStorage(blob, table));
builder.Services.AddAuthagonalDuendeMigration(builder.Configuration);

var app = builder.Build();
app.MapAuthagonalEndpoints();
app.MapAuthagonalDuendeMigration();   // GET /admin/migration/status
```

A segunda chamada `Map` é obrigatória e separada: este pacote referencia `Authagonal.Server`, pelo que
`MapAuthagonalEndpoints` não o consegue alcançar. Sem ela, `GET /admin/migration/status` responde 404,
o que não se distingue de uma recusa pela política `IdentityAdmin`, e a execução regista um aviso no arranque
a indicá-lo.

Configure através da secção `Migration`:

```json
{
  "Migration": {
    "Enabled": true,
    "DryRun": false,
    "Version": "1",
    "UsersMode": "CreateOnly",
    "MigrateClients": true,
    "MigrateRefreshTokens": false,
    "LeaseWaitMinutes": 10,
    "StartupDelaySeconds": 30,
    "Source": { "ConnectionString": "Server=...;Database=Identity;..." }
  }
}
```

O executor:

1. Aguarda `StartupDelaySeconds` (os serviços de inicialização de dados terminam primeiro; o arranque nunca é bloqueado).
2. Não faz nada se já existir um marcador `Completed` que não seja `DryRun` para `Version`.
3. Aguarda até `LeaseWaitMinutes` para se tornar líder do cluster (apenas um pod executa a migração).
4. Escreve um marcador `Started`, executa o motor e depois um marcador `Completed`/`Failed` com o relatório.

Perder a liderança a meio da execução cancela o motor; o novo líder volta a executá-lo, o que é seguro porque todas as
passagens são idempotentes. Consulte o progresso em `GET /admin/migration/status` (protegido pela política
`IdentityAdmin`).

## CLI {#cli}

```bash
docker run authagonal-migration \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  --DryRun true --UsersMode CreateOnly
```

(Sem separador `--` depois do nome da imagem.) Ou a partir do código-fonte:

```bash
dotnet run --project tools/Authagonal.Migration.Cli -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  --DryRun true
```

## O que é migrado {#what-gets-migrated}

| Origem (SQL Server) | Destino | Notas |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Utilizadores + índices de email/nome | Os IDs são preservados textualmente. Conversão de claims: `given_name`→FirstName, `family_name`→LastName, `company`→CompanyName, `org_id`→OrganizationId (também as variantes xmlsoap); as claims de email são descartadas; tudo o resto → atributos personalizados. Hashes de palavra-passe nulos (utilizadores apenas com SSO externo) não são problema. Os hashes BCrypt / ASP.NET Identity V3 são verificados sem alterações e atualizados para PBKDF2 nativo no início de sessão seguinte. |
| `AspNetUserLogins` | UserLogins | `409 Conflict` = ignorar (idempotente) |
| `AspNetRoles` + `AspNetUserRoles` | Funções + ligações de funções dos utilizadores | Um mapa de ID de função→nome resolve as atribuições dos utilizadores |
| `ApiScopes` + `IdentityResources` | Âmbitos | Os nomes já existentes (de inicialização) são ignorados; as claims dos âmbitos são copiadas |
| `Clients` do Duende + tabelas filhas | Clientes | Segredos marcados com `SHA256$`/`SHA512$` consoante o comprimento do digest (os restantes são descartados com um aviso); os segredos expirados são ignorados; os clientes definidos na configuração prevalecem (são ignorados) |
| `ApiResources` do Duende | (achatados) | Audiências → clientes criados pela migração; claims de recursos → âmbitos criados pela migração |
| `SamlProviderConfigurations` | SamlProviders + SsoDomains | O CSV `AllowedDomains` é dividido em registos de domínio SSO |
| `OidcProviderConfigurations` | OidcProviders + SsoDomains | A mesma divisão de domínios |
| `AspNetUserTokens` (`AuthenticatorKey`, `RecoveryCodes`) | MfaCredentials | Segredo TOTP base32→protegido (`duende-totp`); códigos de recuperação com hash (`duende-rc-{n}`); o utilizador é ignorado se já tiver MFA |
| `PersistedGrants` do Duende (tokens de atualização) | Concessões | **Impossível com o Duende de origem**, ver abaixo. Exige `MigrateRefreshTokens` *e* `SourceGrantKeysAreUnhashed`; caso contrário, é ignorado com um aviso e os utilizadores voltam a iniciar sessão. |

## Opções {#options}

| Opção | Predefinição | Descrição |
|---|---|---|
| `Enabled` | `false` | Interruptor geral do executor alojado |
| `DryRun` | `false` | Percorre a origem e produz o relatório de validação completo (conjunto de caracteres/comprimento dos IDs, emails duplicados, inventário de tabelas/colunas, contagens por passagem) sem escrever nada |
| `Version` | `"1"` | Marcador de execução. Incremente-o para voltar a executar uma passagem delta. Só um marcador `Completed` que não seja `DryRun` impede uma nova execução |
| `UsersMode` | `CreateOnly` | `CreateOnly` ignora os utilizadores existentes; `Upsert` substitui-os. **Nunca use `Upsert` depois da transição**: destrói as palavras-passe já convertidas e a MFA nova |
| `MigrateClients` | `true` | Migra os clientes OAuth. Os clientes definidos na configuração prevalecem sempre e os clientes existentes são ignorados |
| `MigrateRefreshTokens` | `false` | Inclui os tokens de atualização ativos. Exige `SourceGrantKeysAreUnhashed` |
| `SourceGrantKeysAreUnhashed` | `false` | Declara que `PersistedGrants.Key` de origem guarda os identificadores textualmente. Só é verdade num fork com um armazenamento de concessões personalizado |
| `Source:ConnectionString` | *(nenhuma)* | Ligação ao SQL Server do Duende de origem |
| `MaxDegreeOfParallelism` | `32` | Concorrência de escrita limitada para as passagens de grande volume (utilizadores, associações de início de sessão externo, MFA, tokens de atualização). Reduza-a para contas pequenas ou propensas a limitação; `1` é totalmente sequencial |
| `LeaseWaitMinutes` | `10` | Executor alojado: desiste de aguardar pela liderança do cluster ao fim deste tempo; um reinício posterior volta a tentar |
| `StartupDelaySeconds` | `30` | Executor alojado: atraso antes de iniciar, para que os serviços de inicialização de dados terminem e o arranque não seja bloqueado |

## Idempotência e passagens delta {#idempotency--delta-sweeps}

Todas as passagens são idempotentes (ignorar se já existir, IDs de MFA determinísticos), pelo que a migração pode ser
executada de novo com segurança. Execute-a dias antes da transição e depois incremente `Version` para uma passagem delta
final perto da transição, de modo a apanhar os utilizadores registados entretanto. Os registos existentes são ignorados
(ou atualizados com `Upsert`), nunca duplicados.

## O que NÃO é migrado {#what-is-not-migrated}

- **Tokens de atualização ativos, com o Duende de origem.** O `DefaultGrantStore` do Duende nunca persiste um
  identificador de token de atualização: `PersistedGrants.Key` guarda `base64(SHA-256(handle + ":" + grantType))`, e
  o identificador apresentado volta a passar por hash na pesquisa. Por isso, o identificador não pode ser recuperado
  da base de dados de origem, e as linhas migradas nunca poderiam ser resgatadas, o que é pior do que não as migrar,
  porque o relatório conta-as como criadas e a avaria só se manifesta na primeira renovação de token depois da
  transição. Planeie a transição contando com um novo início de sessão, ou execute uma camada de leitura dupla durante o período
  de transição. `SourceGrantKeysAreUnhashed` existe apenas para um fork cujo armazenamento de concessões persiste os
  identificadores textualmente, e esse fork também fica responsável por converter `PersistedGrants.Data` da forma
  `RefreshToken` do Duende para `RefreshTokenData`.
- **Tokens e grupos SCIM**, **aprovisionamentos de utilizadores**: não têm equivalente no Duende; começam vazios.
- **Chaves de assinatura**: não automatizadas. Para manter os tokens existentes válidos durante a transição, exporte a
  chave de assinatura RSA do Duende e importe-a na tabela `SigningKeys` perto da transição.

## Estratégia de transição {#cutover-strategy}

1. Implemente sem ativar (`Enabled=false`).
2. `Enabled=true, DryRun=true` → reinicie → reveja o relatório em `/admin/migration/status`.
3. `DryRun=false` → reinicie → verifique se o marcador é `Completed` + teste alguns inícios de sessão por amostragem.
4. Incremente `Version` para a passagem delta final e depois aponte os clientes/BFFs para o Authagonal. **Conte com
   um novo início de sessão obrigatório**, ver acima.
5. Monitorize; a reversão consiste em apontar de novo para a implementação do Duende, que ficou intacta.

## Importação de utilizadores NDJSON {#ndjson-user-import}

Uma segunda origem de importação, independente, no mesmo pacote `Authagonal.Migration`: um ficheiro NDJSON plano
(um objeto JSON por linha) em vez de uma ligação a uma base de dados ativa, e apenas utilizadores, sem clientes,
funções, âmbitos nem configuração de federação. Foi criada para migrar a tabela de utilizadores própria de uma aplicação
legada (um armazenamento ASP.NET Identity feito à medida, uma tabela Rails/Devise exportada para bcrypt, uma aplicação
Node com scrypt, ...), para que as pessoas continuem a iniciar sessão com a palavra-passe antiga enquanto esta é
convertida de forma transparente para PBKDF2 nativo no seu início de sessão seguinte bem-sucedido, o mesmo mecanismo de conversão
diferida em que se apoia o importador do Duende acima.

### Esquema dos registos {#record-schema}

Um objeto JSON por linha. `email` é o único campo obrigatório; todos os outros campos são opcionais. **Campos de nível
superior desconhecidos fazem falhar essa linha** (estrito por predefinição), a menos que seja passado
`--AllowUnknownFields true`.

| Campo | Tipo | Notas |
|---|---|---|
| `email` | string | Obrigatório. Tem de ser um endereço de email plausível. Chave de duplicados sem distinção entre maiúsculas e minúsculas. |
| `username` | string | Sem coluna própria em `AuthUser`; guardado em `CustomAttributes["username"]`. |
| `givenName` | string | → `AuthUser.FirstName` |
| `familyName` | string | → `AuthUser.LastName` |
| `displayName` | string | Sem coluna própria; guardado em `CustomAttributes["displayName"]`. |
| `emailVerified` | bool | → `AuthUser.EmailConfirmed`. Predefinição `false` quando está ausente. |
| `passwordHash` | string | → `AuthUser.PasswordHash`, guardado **textualmente**. Qualquer formato que `PasswordHasher` reconheça no início de sessão (bcrypt `$2a$`/`$2b$`/`$2x$`/`$2y$`, ASP.NET Identity V3, scrypt `$s2$`) é verificado sem alterações e a partir daí atualizado para PBKDF2 nativo. Não é inspecionado além de não estar vazio; um hash malformado simplesmente falha a verificação no início de sessão, tal como aconteceria fora da migração. Omita-o para utilizadores apenas com SSO / sem palavra-passe. |
| `roles` | string[] | → `AuthUser.Roles` |
| `organizationId` | string | → `AuthUser.OrganizationId` |
| `attributes` | objeto (string→string) | Fundido em `AuthUser.CustomAttributes` |
| `phoneNumber` | string | → `AuthUser.Phone` |
| `disabled` | bool | → `AuthUser.IsActive = !disabled`. Ativo por predefinição quando está ausente. |
| `createdAt` | string (ISO 8601) | → `AuthUser.CreatedAt`. Predefinição: a hora da importação, quando está ausente. |
| `externalId` | string | → `AuthUser.ExternalId`, o mesmo campo que o importador do Duende preenche com o ID de utilizador da base de dados de origem. |

Ficheiro de exemplo (5 linhas):

```ndjson
{"email":"ada.lovelace@legacy.example.com","givenName":"Ada","familyName":"Lovelace","passwordHash":"$2b$12$KIXQ8N6Qe0m6b6b6b6b6bOQe0m6b6b6b6b6b6b6b6b6b6b6b6b6b6","roles":["admin"],"organizationId":"org-legacy-1","externalId":"42"}
{"email":"bob@legacy.example.com","emailVerified":true,"attributes":{"dept":"eng"},"createdAt":"2019-03-04T00:00:00Z"}
{"email":"carol@legacy.example.com","disabled":true,"phoneNumber":"+61400000000"}
{"email":"dave@legacy.example.com","username":"dave1998","displayName":"Dave K."}
{"email":"erin@legacy.example.com"}
```

### CLI {#cli-1}

```bash
dotnet run --project tools/Authagonal.Migration.Cli -- import-ndjson-users \
    --Input ./users.ndjson \
    --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
    --DryRun true \
    --OnDuplicate skip \
    --BatchSize 500 \
    --AllowUnknownFields false \
    --ContinueOnError false \
    --AllowPlaintextPii true
```

O mesmo destino (Azure Table Storage) e a mesma barreira de PII em texto simples que a CLI do Duende acima: esta origem
escreve linhas `AuthUser` diretamente no Table Storage sem nenhum `IFieldCipher`/`IIndexTokenizer` registado pelo
anfitrião, pelo que se recusa a executar a menos que `--AllowPlaintextPii true` confirme que o destino não tem nenhum dos
dois configurado (ou, em alternativa, ligue `NdjsonUserImportEngine` ao contentor de DI do próprio anfitrião, onde esses
pontos de extensão são resolvidos). Ao contrário da CLI do Duende, não existe a barreira `--AllowPlaintextSecrets`: esta
origem nunca escreve sementes TOTP de MFA nem segredos de clientes OAuth, apenas campos do perfil do utilizador e um hash
de palavra-passe guardado textualmente.

### Opções {#options-1}

| Opção | Predefinição | Descrição |
|---|---|---|
| `--Input` | *(obrigatório)* | Caminho para o ficheiro NDJSON |
| `--Target:ConnectionString` | *(obrigatório)* | Cadeia de ligação do Azure Table Storage |
| `--DryRun` | `false` | Analisa e valida todas as linhas, resolve os duplicados face ao destino e produz o relatório completo, sem escrever nada |
| `--OnDuplicate` | `skip` | Como tratar uma linha cujo email (sem distinção entre maiúsculas e minúsculas) já corresponde a um utilizador existente: `skip` (deixa-o intacto, idempotente), `update` (funde os campos presentes na linha com o utilizador existente) ou `fail` (aborta a execução de imediato) |
| `--BatchSize` | `500` | Quantas linhas entre linhas de registo de progresso. Não é um mecanismo de escrita em lote; `IUserStore` não tem API em massa, pelo que cada importação/atualização continua a ser uma chamada ao armazenamento |
| `--AllowUnknownFields` | `false` | Aceita e ignora propriedades JSON de nível superior fora do esquema acima, em vez de fazer falhar a linha |
| `--ContinueOnError` | `false` | Termina com 0 mesmo que uma ou mais linhas não tenham passado a análise/validação. Não se aplica a `--OnDuplicate fail`, que aborta sempre a execução independentemente deste indicador |

### Resumo e códigos de saída {#summary-output--exit-codes}

O relatório é impresso em JSON: `TotalLines`, `Imported`, `Updated`, `Skipped`, `Failed` e as primeiras
20 `Failures` (`LineNumber` + `Reason`). As linhas em branco não são contadas em lado nenhum. Códigos de saída:

- `0`: sucesso (ou `--ContinueOnError true` com uma ou mais linhas falhadas)
- `1`: uma ou mais linhas não passaram a análise/validação, e `--ContinueOnError` não foi definido
- `2`: a execução foi abortada: `--OnDuplicate fail` encontrou um email existente, ou faltava uma opção obrigatória

### Idempotência {#idempotency}

A predefinição `--OnDuplicate skip` faz com que voltar a executar com um ficheiro inalterado não tenha qualquer efeito à
segunda vez: todas as linhas cujo email já existe são contadas como ignoradas e nada é escrito. `update` também pode ser
executado de novo com segurança (volta sempre a aplicar os mesmos campos); `fail` destina-se a uma importação única que
nunca deve colidir silenciosamente com contas existentes.

### O que NÃO é importado {#what-is-not-imported}

- **Funções, âmbitos, clientes OAuth, configuração de federação.** Esta origem trata apenas de utilizadores: consulte o
  importador do Duende acima se também precisar destes elementos.
- **Credenciais de MFA, associações de início de sessão externo.** Não fazem parte do esquema; adicione-os através dos fluxos padrão de
  configuração de MFA / SSO depois da importação.
