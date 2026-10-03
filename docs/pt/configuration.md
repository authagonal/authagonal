---
layout: default
title: Configuração
locale: pt
---

# Configuração

A Authagonal é configurada através de `appsettings.json` ou de variáveis de ambiente. As variáveis de ambiente utilizam `__` como separador de secções (por exemplo, `Storage__ConnectionString`).

## Definições obrigatórias {#required-settings}

O armazenamento pode ser configurado de duas formas: indique **ou** `Storage:ConnectionString` **ou** `Storage:TableServiceUri` (o caminho com identidade gerida, preferível em produção).

| Definição | Variável de ambiente | Descrição |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Cadeia de ligação do Azure Table Storage com uma chave de conta. Adequada para desenvolvimento / Azurite. |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | Endpoint do Table Storage com identidade gerida, por exemplo `https://{account}.table.core.windows.net/`. Alternativa a `Storage:ConnectionString` e **preferível em produção**: autentica através de `DefaultAzureCredential`, pelo que nenhuma chave de acesso acaba alguma vez num segredo. O anfitrião tem de conceder à identidade da carga de trabalho a função **Storage Table Data Contributor**. |
| `Issuer` | `Issuer` | O URL base público deste servidor (por exemplo, `https://auth.example.com`) |

## Armazenamento {#storage}

| Definição | Variável de ambiente | Predefinição | Descrição |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *(nenhuma)* | Cadeia de ligação com chave de conta (consulte Definições obrigatórias). |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *(nenhuma)* | URI do Table Storage com identidade gerida (consulte Definições obrigatórias). Tem precedência sobre `Storage:ConnectionString` quando ambos estão definidos. |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | Se devem ser mantidas as tabelas de índice de pesquisa por prefixo `UserFirstNames` / `UserLastNames`, que suportam a pesquisa de administração por prefixo do nome. Defina `false` nos anfitriões que não expõem a pesquisa de administração por nome, para evitar essas escritas. **Nota sobre escalabilidade:** estes índices utilizam uma única partição muito solicitada e limitam o débito a cerca de 2000 operações/s em escala; desative-os se não precisar da pesquisa por nome. |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | URL base para o qual o endpoint `/connect/authorize` redireciona para a SPA de início de sessão (ecrãs de início de sessão, de elevação e de consentimento). Defina-o quando a interface de início de sessão for servida a partir de uma origem diferente da do servidor; a predefinição é o caminho relativo `/login`, servido pela SPA incluída. |

## Autenticação {#authentication}

| Definição | Predefinição | Descrição |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Tempo de vida da sessão por cookie (deslizante) |
| `Authentication:AllowInsecureCookie` | `false` | Permite que o cookie de sessão seja enviado por http sem encriptação (`SameAsRequest` em vez de `Always`). **Apenas para desenvolvimento.** O cookie É a sessão, e `SameAsRequest` só parece equivalente atrás de um proxy que termina o TLS: depende de `X-Forwarded-Proto` chegar e ser considerado de confiança, pelo que um ingress mal configurado, uma sonda de estado em HTTP sem encriptação ou um proxy que descarte o cabeçalho produzem um cookie sem Secure que depois acompanha qualquer pedido sem encriptação para o mesmo anfitrião. A falha é silenciosa. |
| `Authentication:CookieDomain` | *(não definido)* | Limita o cookie de sessão a um domínio principal, para que seja enviado aos subdomínios irmãos (`app.example.com` além de `auth.example.com`). **Isto tem um custo na vinculação à origem:** o cookie deixa de poder ter o prefixo `__Host-`, que é o que faz o browser recusá-lo se não for Secure, `Path=/` e sem `Domain`, pelo que qualquer subdomínio que possa definir cookies no domínio principal, e qualquer coisa que consiga apoderar-se de um deles, fica abrangido. Deixe-o por definir, a menos que uma origem irmã precise efetivamente da sessão. |
| `Auth:AllowInsecureHttp` | `false` | Permite que os endpoints OAuth (`/connect/*`) respondam a pedidos http sem encriptação. **Apenas para desenvolvimento.** A RFC 6749 §3.1/§3.2 exige TLS nos endpoints de autorização e de token, pelo que, por predefinição, um pedido não https a qualquer um deles é recusado com `invalid_request`. O esquema é avaliado *depois* do processamento dos cabeçalhos reencaminhados, pelo que um proxy que termina o TLS e reencaminha `X-Forwarded-Proto: https` passa a barreira com esta opção desativada, desde que esse proxy esteja declarado em [`ForwardedHeaders:KnownNetworks` / `KnownProxies`](#the-two-headers-are-not-trusted-on-the-same-terms); sem isso, o cabeçalho é ignorado. Só uma implementação efetivamente sem encriptação (o `docker-compose.yml` incluído, a demonstração de servidor personalizado) precisa dela, e o servidor regista um aviso no arranque sempre que está ativa. É transmitida para `AuthagonalProtocolOptions.AllowInsecureHttp`, pelo que também rege os endpoints que pertencem a `Authagonal.Protocol` (consulte [Extensibilidade](extensibility#embedding-authagonalprotocol-alone)). |
| `Auth:RequireMinimumRuntime` | `false` | Recusa arrancar quando o shared framework do .NET é anterior ao mínimo de segurança que a Authagonal exige (**9.0.18 / 10.0.10**). Esse mínimo existe porque as correções de GHSA-37gx-xxp4-5rgx e GHSA-w3x6-4m5h-cxqf (um ciclo infinito e um par XXE / esgotamento de recursos em `System.Security.Cryptography.Xml`, ambos alcançáveis a partir do endpoint SAML ACS **anónimo**) são distribuídas no runtime e não num pacote que esta biblioteca possa fixar, pelo que nenhuma das suas dependências as pode garantir. Com `false`, um runtime antigo gera um registo `Critical` e o servidor arranca: recusar por predefinição transformaria uma atualização de versão da Authagonal numa interrupção de serviço numa frota cujo runtime esteja um patch atrás. Defina-a como `true` onde não arrancar for preferível a servir XML não autenticado num runtime sem correções. |
| `Auth:MaxFailedAttempts` | `5` | Tentativas de início de sessão falhadas antes do bloqueio da conta |
| `Auth:LockoutDurationMinutes` | `10` | Duração do bloqueio da conta depois do máximo de tentativas falhadas |
| `Auth:MaxLoginAttemptsPerIp` | `30` | Tentativas de palavra-passe permitidas por endereço de origem em cada `Auth:LoginWindowMinutes` e, separadamente, por email submetido na mesma janela. O bloqueio por conta não consegue travar um ataque de pulverização (uma tentativa contra cada uma de milhares de contas), e cada tentativa não autenticada paga um PBKDF2 completo, pelo que isto limita ambos. Excedê-lo responde `429 too_many_attempts` (`AuthEndpoints.cs:107-119`). |
| `Auth:LoginWindowMinutes` | `5` | Janela de `Auth:MaxLoginAttemptsPerIp` |
| `Auth:MaxRegistrationsPerIp` | `5` | Número máximo de registos por endereço IP dentro da janela |
| `Auth:RegistrationWindowMinutes` | `60` | Janela do limite de taxa de registos |
| `Auth:MaxPasswordResetsPerEmail` | `3` | Número máximo de emails de reposição da palavra-passe por endereço de destino dentro da janela (indexado pelo email e não pelo IP de quem chama, para que um endereço não possa ser bombardeado com emails) |
| `Auth:MaxPasswordResetsPerIp` | `15` | Número máximo de pedidos de palavra-passe esquecida por IP de origem dentro da janela. O limite por email restringe o correio enviado a uma vítima; este restringe quem chama a percorrer uma lista de endereços, o que de outra forma seria correio anónimo ilimitado a partir do seu domínio de envio verificado, mais uma leitura do armazenamento por endereço. |
| `Auth:PasswordResetWindowMinutes` | `60` | Janela do limite de taxa de reposição da palavra-passe |
| `Auth:DurableRateLimiting` | `false` | Mantém os contadores de limite de taxa no armazenamento configurado, para que todas as réplicas partilhem um único orçamento, em vez de cada nó manter o seu. Custa uma ida e volta ao armazenamento por verificação; uma implementação com um único nó não ganha nada. Exige um fornecedor que disponibilize `IRateLimitCounterStore` (Azure, SQL, AWS). Caso contrário, o anfitrião recusa arrancar, em vez de voltar silenciosamente a limites por nó. Consulte [Limites para todo o cluster](#cluster-wide-limits-authdurableratelimiting). |
| `Auth:AutoConfirmEmailDomains` | *(vazio)* | Domínios de email (matriz de cadeias) cujos registos self-service são confirmados automaticamente, sem o email de verificação. Vazio (a predefinição) significa que todos os registos têm de ser verificados. Destina-se apenas a desenvolvimento/testes; nunca indique um domínio que possa receber correio real. |
| `Auth:AllowPasswordlessAccountClaim` | `false` | Registar um email que pertence a uma conta existente **sem credencial local** (federada ou aprovisionada por JIT) prepara uma palavra-passe nessa conta, em vez de devolver a resposta de duplicado neutra face à enumeração. A credencial preparada e quaisquer atributos ficam inativos até o requerente clicar num novo email de verificação, pelo que conhecer o email de uma conta federada não basta para se apoderar dela. Uma conta que já tenha palavra-passe nunca é afetada. Consulte [Atualizar um utilizador](user-upgrade). |
| `Auth:ClaimAllowedAttributeKeys` | *(vazio)* | Chaves de atributos personalizados que uma reivindicação sem palavra-passe pode transportar do pedido de registo para a conta reivindicada. Vazio permite todas as chaves (compatibilidade com versões anteriores); indique chaves para restringir o que uma reivindicação pode injetar no aprovisionamento a jusante e nos tokens. |
| `Auth:EmailVerificationExpiryHours` | `24` | Tempo de vida da hiperligação de verificação de email |
| `Auth:PasswordResetExpiryMinutes` | `60` | Tempo de vida da hiperligação de reposição da palavra-passe |
| `Auth:MfaChallengeExpiryMinutes` | `5` | Tempo de vida do token de desafio de MFA |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | Tempo de vida do token de configuração de MFA (para inscrição obrigatória) |
| `Auth:WebAuthnAllowedHosts` | *(vazio)* | Anfitriões autorizados a agir como relying party WebAuthn. Vazio aceita qualquer anfitrião (as implementações existentes continuam a funcionar) e é uma lacuna: caso contrário, o RP ID e a origem esperada são derivados do próprio pedido que está a ser validado. Numa implementação multi-inquilino, indique todos os anfitriões dos inquilinos. Consulte [MFA](mfa). |
| `Auth:Pbkdf2Iterations` | `100000` | Número de iterações PBKDF2 para o hash das palavras-passe |
| `Auth:FailedLoginMinimumMilliseconds` | `250` | Duração mínima, em tempo real, a que um início de sessão falhado é sujeito antes de ser devolvido `invalid_credentials`, medida a partir do início do pedido. Fecha o oráculo temporal de enumeração de utilizadores: uma conta inexistente é verificada contra um hash fictício no formato PBKDF2 nativo, mas uma conta real pode ainda ter um hash bcrypt, Scrypt.NET ou ASP.NET Identity V3 importado com um custo diferente, pelo que é impossível fazer trabalho igual e o que se impõe é um tempo decorrido igual. Aumente-o acima do hash mais lento que a implementação contém, por exemplo se importou bcrypt com custo acima de 11, um hash Scrypt.NET `$s2$` com um `N` elevado, ou se aumentou `Pbkdf2Iterations` muito acima da predefinição. É registado um único aviso da primeira vez que um início de sessão falhado o ultrapassa. `0` desativa o preenchimento e volta a abrir o oráculo. |
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | Janela de tolerância opcional (em segundos) para a reutilização concorrente de tokens de atualização. `0` (predefinição) mantém a postura estrita: qualquer reutilização de um token de atualização já consumido revoga todos os tokens desse utilizador+cliente. Defina `> 0` para tratar uma reutilização dentro da janela como uma nova tentativa idempotente (volta a entregar os tokens sucessores), útil para clientes móveis com quebras de ligação. |
| `Auth:DynamicClientRegistrationEnabled` | `false` | Ativa o endpoint de registo dinâmico de clientes `POST /connect/register` (RFC 7591). Desativado por predefinição porque o registo aberto pode ser abusado em implementações multi-inquilino. Consulte [Registo dinâmico de clientes](client-registration). |
| `Auth:DynamicClientRegistrationScopes` | *(vazio)* | Âmbitos que um registante anónimo pode atribuir a si próprio, além dos âmbitos OIDC integrados, que são sempre registáveis (`openid`, `profile`, `email`, `phone`, `offline_access`). Vazio significa os integrados e mais nada: um âmbito existir no armazenamento não é autorização para que um cliente autorregistado o declare. Os âmbitos condicionados por funções nunca são registáveis, em caso algum. Consulte [Registo dinâmico de clientes](client-registration). |
| `Auth:SigningKeyLifetimeDays` | `90` | Tempo de vida da chave de assinatura antes da rotação automática (as chaves são ES256 / P-256) |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Frequência com que as chaves de assinatura são recarregadas a partir do armazenamento |
| `Auth:KeyRotationEnabled` | `false` | Ativa a rotação automática da chave de assinatura |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | Frequência com que se verifica se a chave ativa precisa de rotação |
| `Auth:KeyRotationLeadTimeDays` | `14` | Roda a chave quando a chave ativa expira dentro deste número de dias |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Intervalo entre verificações do security stamp do cookie |
| `Auth:AllowedInternalTargets` | *(vazio)* | Destinos internos a partir dos quais a Authagonal pode obter dados nos caminhos em que foi **o operador** a fornecer o URL: metadados SAML a montante, descoberta OIDC a montante, callbacks de aprovisionamento. Vazio significa que todos os endereços internos são recusados. Consulte [Pedidos de saída](#outbound-fetches-ssrf-guard). |
| `Auth:AllowOutboundProxy` | `false` | Envia esses mesmos pedidos configurados pelo operador através do proxy HTTP do ambiente, aceitando que a verificação de endereços não consegue ver através dele. Nunca se aplica a um `jwks_uri` nem a um URI de back-channel logout registados por um cliente. Consulte [Pedidos de saída](#outbound-fetches-ssrf-guard). |
| `Auth:AtRestBackfillEnabled` | `false` | Executa uma vez, no arranque, o preenchimento retroativo dos dados em repouso, no líder do cluster. Reescreve todas as linhas de utilizador existentes e as linhas de índice derivadas do perfil segundo o esquema atual de dados em repouso, que é o caminho de migração para ativar `IFieldCipher` / `IIndexTokenizer` numa implementação que já tem dados (consulte [Extensibilidade](extensibility#pii-field-encryption-ifieldcipher)). Registar apenas uma cifra só encripta as linhas escritas a partir daí. Gera um volume real de escritas, é idempotente e é executado uma vez por processo, pelo que deve desativá-lo depois de o registo indicar uma execução completa. |
| `Auth:MaxScimGroupsPerClient` | `5000` | Número máximo de grupos SCIM que um cliente de aprovisionamento pode deter; acima disso, a criação é recusada. O armazenamento de grupos não está indexado, pelo que uma tabela sem limite faria cada emissão de token pagar por ela. |
| `Auth:MaxScimGroupMembers` | `10000` | Número máximo de membros que um grupo SCIM pode ter; acima disso, a criação, a substituição e o patch são recusados. |

## Proteção de dados {#data-protection}

As chaves do ASP.NET Core Data Protection (que encriptam o cookie de sessão) têm de ser partilhadas entre instâncias; consulte [Escalabilidade](scaling#cookie-encryption-data-protection). Opções de persistência, por ordem de precedência:

| Definição | Predefinição | Descrição |
|---|---|---|
| `DataProtection:BlobUri` | *(nenhuma)* | URI explícito do Azure Blob para o anel de chaves (por exemplo, `https://{account}.blob.core.windows.net/dataprotection/keys.xml`). Autentica através de `DefaultAzureCredential`, o caminho preferível em produção, a par de `Storage:TableServiceUri`. |
| *(alternativa)* | *(nenhuma)* | Quando `DataProtection:BlobUri` não está definido, o anel de chaves é persistido automaticamente: num contentor `dataprotection` da conta indicada por `Storage:ConnectionString` (a menos que seja o Azurite) ou, no caminho com identidade gerida, no endpoint de blobs derivado de `Storage:TableServiceUri` (`https://{account}.table.…` → `https://{account}.blob.…/dataprotection/keys.xml`), o que exige Storage Blob Data Contributor na mesma conta. Só um endpoint de tabelas não reconhecido (Azurite, emuladores com estilo de caminho) recorre ao armazenamento em ficheiro por máquina, que é efémero e por pod; `KeyRingStartupCheck` regista ao nível Critical quando isso acontece. |

No backend AWS, passe um cliente S3 + um bucket a `AddAuthagonalAwsStorage` para persistir o anel de chaves no S3; consulte [Instalação → backend AWS](installation#aws-backend). No backend SQL, o anel de chaves é persistido por `AddAuthagonalPostgres` / `AddAuthagonalSqlite`; consulte [Instalação → backend SQL](installation#sql-backend).

Persistir não é encriptar. Seja qual for o backend que guarda o anel, este é escrito como XML em texto simples (incluindo a chave mestra), a menos que uma destas opções esteja definida. Esse anel protege o cookie de autenticação, pelo que ler o armazenamento equivale a poder forjar uma sessão para qualquer utilizador:

| Definição | Predefinição | Descrição |
|---|---|---|
| `DataProtection:KeyVaultKeyId` | *(nenhuma)* | URI da chave do Azure Key Vault utilizada para envolver o anel de chaves. Autentica através de `DefaultAzureCredential`. |
| `DataProtection:CertificateThumbprint` | *(nenhuma)* | Thumbprint de um certificado no armazenamento da máquina utilizado para envolver o anel de chaves. |
| `DataProtection:AllowUnencryptedKeyRing` | `false` | Aceita deliberadamente um anel de chaves em texto simples. É repetido ao nível `Critical` em cada arranque, para que apareça numa auditoria e não apenas num ficheiro de configuração. |

O arranque impõe isto a partir das opções *resolvidas* do anel de chaves, pelo que se aplica de forma idêntica ao repositório Azure, AWS, SQL e a qualquer repositório registado pelo anfitrião. Uma implementação que persista o anel sem encriptação e **ainda sem chaves** é recusada, para que o estado inseguro nunca chegue a ser criado; uma cujo anel **já tenha chaves** arranca e regista ao nível `Critical`, porque recusar nesse caso deitaria abaixo uma implementação em funcionamento por causa de uma atualização de versão. Em desenvolvimento, nunca há recusa.

## Cache e tempos limite {#cache-and-timeouts}

| Definição | Predefinição | Descrição |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | Durante quanto tempo as origens CORS autorizadas ficam em cache |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | Duração da cache do documento de descoberta OIDC |
| `Cache:SamlMetadataCacheMinutes` | `60` | Duração da cache dos metadados do IdP SAML |
| `Cache:OidcStateLifetimeMinutes` | `10` | Tempo de vida do parâmetro state da autorização OIDC |
| `Cache:SamlReplayLifetimeMinutes` | `10` | Tempo de vida do ID do AuthnRequest SAML (prevenção de repetição) |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Tempo limite da verificação de estado do Table Storage |
| `Cache:HealthCheckCacheSeconds` | `5` | Durante quanto tempo a resposta de `/health` é reutilizada antes de o armazenamento voltar a ser consultado (corresponde ao `Cache-Control: max-age` que o endpoint anuncia). `0` faz a sonda em cada pedido, o que volta a abrir a amplificação anónima que a cache fecha. |

## Serviços em segundo plano {#background-services}

| Definição | Predefinição | Descrição |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | Atraso inicial antes da primeira limpeza de tokens expirados |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | Intervalo de limpeza de tokens expirados |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | Atraso inicial antes da primeira reconciliação de concessões |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | Intervalo de reconciliação de concessões |

### Varreduras de expiração (Azure Table) {#expiry-sweeps-azure-table}

O Azure Table Storage não tem TTL, pelo que, no backend Azure, o servidor executa um `TableExpirySweepService` por tabela (a cada 15 minutos, apenas no líder do cluster) sobre `MfaChallenges`, `RevokedTokens` e `UpstreamRefreshTokens`, eliminando as linhas cuja expiração já passou. Serve apenas a retenção: cada uma dessas linhas já é recusada na leitura pela sua própria verificação de expiração. Uma linha sem expiração indicada (possível em `UpstreamRefreshTokens`) deliberadamente nunca é varrida. O DynamoDB e o SQL tratam nativamente das mesmas três tabelas. Não há nada a configurar.

## Proteção contra bots (Cloudflare Turnstile) {#bot-protection-cloudflare-turnstile}

Opcional. Quando está definida uma chave secreta, o início de sessão, o registo, a palavra-passe esquecida e a reposição da palavra-passe verificam um `turnstileToken` junto da Cloudflare antes de fazerem qualquer trabalho; sem chave secreta, nada muda e não é apresentado nenhum widget.

| Definição | Predefinição | Descrição |
|---|---|---|
| `Turnstile:SiteKey` | *(não definido)* | Sitekey pública, disponibilizada à interface de início de sessão (`turnstileSiteKey` em `GET /api/auth/providers`) para que esta possa apresentar o widget |
| `Turnstile:SecretKey` | *(não definido)* | Segredo para a verificação do lado do servidor. Não definido ou vazio desativa completamente o Turnstile |

Um anfitrião que sirva domínios fornecidos pelos clientes não pode utilizar um único par de chaves (a Cloudflare limita o número de nomes de anfitrião de um widget); substitui [`ITurnstileKeyProvider`](extensibility#iturnstilekeyprovider). Consulte [API de autenticação](auth-api#providers) para o erro `captcha_failed`.

## Funções {#roles}

As funções são definidas na matriz `Roles` e inicializadas no arranque, juntamente com os clientes, os âmbitos e os
fornecedores. Inicializá-las é sobretudo importante quando um âmbito está condicionado com
[`AllowedRoles`](scopes#role-gated-scopes): um âmbito condicionado a uma função que nada cria está vedado
a todos, incluindo o operador que o configurou, e falha silenciosamente: o âmbito simplesmente
nunca é concedido.

```json
{
  "Roles": [
    {
      "Name": "staff-admin",
      "Description": "Internal staff console",
      "Members": [ "ada@example.com", "grace@example.com" ]
    }
  ]
}
```

| Campo | Descrição |
|---|---|
| `Name` | O nome da função, tal como é utilizado em `Scope.AllowedRoles` e na claim `roles` do token |
| `Description` | Legível por pessoas; atualizada nos arranques seguintes quando a inicialização indica uma |
| `Members` | Emails colocados na função em cada arranque. Um endereço que ainda não tenha utilizador é ignorado com um aviso e tentado novamente no arranque seguinte, pelo que o arranque nunca depende de uma conta que alguém ainda não criou |

A inicialização é **aditiva e idempotente**. Nunca remove uma função nem revoga uma associação: a configuração
não é a fonte de verdade sobre quem detém o quê, pelo que uma função concedida através da API de administração sobrevive ao
reinício seguinte.

## Clientes {#clients}

Os clientes são definidos na matriz `Clients` e inicializados no arranque. Cada cliente pode ter:

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "ClientName": "My Application",
      "SecretHashes": ["pbkdf2-hash-here"],
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email", "custom-scope"],
      "Audiences": ["https://api.example.com"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "AlwaysIncludeUserClaimsInIdToken": false,
      "AccessTokenLifetimeSeconds": 1800,
      "IdentityTokenLifetimeSeconds": 300,
      "AuthorizationCodeLifetimeSeconds": 300,
      "AbsoluteRefreshTokenLifetimeSeconds": 2592000,
      "SlidingRefreshTokenLifetimeSeconds": 1296000,
      "RefreshTokenUsage": "OneTime",
      "MfaPolicy": "Enabled",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "RestrictedToOrganizationIds": [],
      "InitiateLoginUri": "https://app.example.com/login",
      "ClientUri": "https://app.example.com",
      "IsDefaultApplication": false
    }
  ]
}
```

A inicialização segue o padrão **ler-fundir-escrever**: um campo que a inicialização não indica mantém o valor guardado, pelo que um reinício nunca anula uma alteração feita através da API de administração (um cliente desativado continua desativado, um segredo rodado sobrevive, `Audiences` e o JWKS do cliente são preservados). Um campo que a inicialização indica é substituído em cada arranque.

Notas sobre os campos (de `ClientSeedService.ClientSeedConfig`):

- **Nomes alternativos.** `ClientId`/`Id`, `ClientName`/`Name`, `AllowedGrantTypes`/`GrantTypes`, `AllowedScopes`/`Scopes`, `AllowedCorsOrigins`/`CorsOrigins` e `RequireClientSecret`/`RequireSecret` são intercambiáveis. Um único objeto `SeedClient` é também lido como mais uma entrada.
- **Segredos.** Indique `SecretHashes` (já com hash) ou `ClientSecret` (texto simples, com hash calculado no arranque, utilizado apenas quando não são indicados hashes). A inicialização só é aplicada quando fornece um, pelo que um segredo rodado através da API de administração sobrevive ao reinício seguinte. Não existe nenhuma chave `ClientSecretHashes` no formato de inicialização.
- **`BackChannelLogoutUri`**: para onde são enviados por POST os tokens de back-channel logout; consulte [Back-Channel Logout](#back-channel-logout).
- **`RestrictedToOrganizationIds`**: ids das organizações com que o cliente pode ser utilizado. Vazio significa sem restrições; uma única entrada também seleciona essa organização num pedido que não indique nenhuma (consulte [Organizações](organizations)).
- **`InitiateLoginUri`, `ClientUri`, `IsDefaultApplication`**: alimentam a lista `/api/auth/apps` e o botão de continuar para a aplicação do ecrã de início de sessão.
- **Não inicializáveis.** `RequireConsent`, `ProvisioningApps`, `RequirePushedAuthorizationRequests`, o JWKS do cliente e os campos de front-channel logout não têm chave no formato de inicialização, pelo que a configuração não os consegue definir. As chaves desconhecidas são ignoradas sem aviso.
- Uma inicialização cujos âmbitos ou audiências violem as regras de âmbitos reservados ou de audiências é recusada com um registo de erro e ignorada.

### Audiências e indicadores de recurso (RFC 8707) {#audiences-and-resource-indicators-rfc-8707}

`Audiences` é a lista de valores permitidos do cliente para o parâmetro `resource` (RFC 8707) e para o parâmetro `audience` de uma troca de tokens (RFC 8693). O que sobreviver a essa verificação torna-se a claim `aud` do token de acesso emitido; sem `resource` no pedido, `aud` recorre a `Audiences` e, sem nenhum dos dois, é o `client_id`.

Uma lista `Audiences` vazia significa **"nenhuma"** para qualquer cliente que tenha efetivamente respondido à pergunta: um cujo pedido de criação continha o campo `audiences`, seja por registo dinâmico (onde o campo é uma extensão da Authagonal à RFC 7591), pela API de administração ou pela configuração de inicialização. Um cliente assim não pode indicar nenhum `resource`, em nenhum caminho: a autorização, `client_credentials` e a troca de tokens coincidem.

Um registo dinâmico que **omite** `audiences` (todos os clientes RFC 7591 padrão, ou seja, todos os clientes MCP) nunca foi questionado. A sua lista está "por definir" e pode indicar qualquer URI absoluto como `resource`; a especificação de autorização do MCP depende disto. A mesma leitura aplica-se aos clientes guardados antes de existir `AudiencesDeclared`, porque restringir todos os clientes guardados na atualização partiria fluxos que hoje funcionam.

| Cliente | `Audiences` vazio significa |
|---|---|
| O pedido de criação continha `audiences` (campo de extensão do DCR, API de administração, inicialização) | **recusar**: não pode ser indicado nenhum `resource` |
| Registo DCR que omitiu `audiences` | **"por definir"**: é aceite qualquer URI absoluto como `resource` |
| Guardado antes de existir `AudiencesDeclared` | **"por definir"**: é aceite qualquer URI absoluto como `resource` |

**Adaptar um cliente antigo** é um `PUT` à API de administração de clientes com `audiencesDeclared: true` (e as `audiences` a que deve ficar fixado). O indicador só restringe: uma atualização pode defini-lo e não o pode limpar, pelo que uma edição não relacionada nunca devolve silenciosamente um cliente à leitura permissiva.

A consequência para as linhas antigas merece ser dita com clareza, em vez de ficar escondida:

> Um cliente pré-existente sem `Audiences` configurado pode indicar **qualquer** URI absoluto como `resource` no endpoint de autorização ou em `client_credentials`, e receber um token de acesso cujo `aud` é esse valor, assinado pela chave deste inquilino, contendo o `sub` do utilizador que fez o pedido e quaisquer âmbitos permitidos ao cliente.

Uma lista `audiences` declarada é validada no momento em que é escrita: no máximo 20 entradas com no máximo 512 caracteres, cada uma um URI absoluto com esquema explícito e sem fragmento. Os valores de `resource` estão sujeitos ao mesmo formato; note que um caminho isolado como `/admin` **não** é aceite, mesmo que o analisador `Uri` do .NET o considere um URI `file:` absoluto em Linux.

Indicar um recurso não dá acesso a ele. Mas significa que o servidor de autorização não pode ser a única coisa entre um cliente e uma API que nunca deveria chamar, por isso:

- **Os servidores de recursos TÊM DE autorizar com base em `scope`** (ou no seu próprio modelo), e não apenas em `iss` + `aud` + `sub`. Um token que indica a sua API em `aud` prova que o cliente pediu a sua API. Não prova que o cliente tem permissão para a chamar, e este servidor não o consegue obrigar a prová-lo.
- **Os servidores de recursos TÊM DE validar `aud` contra o seu próprio identificador**, e não apenas verificar "que existe algum valor".
- **Defina `Audiences` em todos os clientes que devam ficar fixados a um conjunto fixo de APIs.** Com ele configurado, um `resource` não listado é recusado com `invalid_target` no endpoint de autorização e em `client_credentials`. Este é o único local onde a restrição pode ser imposta.
- **Adapte com `audiencesDeclared: true` os clientes criados antes de este existir**, para que a sua lista de audiências vazia signifique "nenhuma" e não "qualquer coisa".
- **Um cliente autorregistado pode declarar `audiences`** no registo e fica sujeito ao que declara, incluindo a uma lista vazia. `Auth:DynamicClientRegistrationEnabled` continua desativado por predefinição; consulte [Registo dinâmico de clientes](client-registration).

### Tipos de concessão {#grant-types}

| Tipo de concessão | Caso de utilização |
|---|---|
| `authorization_code` | Início de sessão interativo do utilizador (aplicações web, SPAs, aplicações móveis) |
| `client_credentials` | Comunicação entre serviços |
| `refresh_token` | Renovação de tokens (exige `AllowOfflineAccess: true`) |
| `urn:ietf:params:oauth:grant-type:device_code` | Concessão de autorização de dispositivos (RFC 8628) para dispositivos com entrada limitada |

### Utilização de tokens de atualização {#refresh-token-usage}

| Valor | Comportamento |
|---|---|
| `OneTime` (predefinição) | Cada atualização emite um novo token de atualização e invalida o anterior. Por predefinição (`Auth:RefreshTokenReuseGraceSeconds = 0`), qualquer reutilização de um token já consumido revoga imediatamente todos os tokens desse utilizador+cliente; **não** há nenhuma janela de tolerância ativa por predefinição. Defina `Auth:RefreshTokenReuseGraceSeconds` com um valor positivo para optar por uma janela de tolerância a novas tentativas. |
| `ReUse` | O mesmo token de atualização é reutilizado até expirar. |

### Aplicações de aprovisionamento {#provisioning-apps}

A matriz `ProvisioningApps` de um cliente (lida no momento da autorização, `AuthorizeEndpoint.cs:578`; o inicializador de configuração não a associa e as rotas de clientes da API de administração não a transportam, pelo que é o anfitrião que a define no registo guardado do cliente) referencia IDs de aplicações definidos na secção de configuração `ProvisioningApps`. Quando um utilizador autoriza através deste cliente, é aprovisionado nessas aplicações através de TCC. Consulte [Aprovisionamento](provisioning) para mais detalhes.

## Âmbitos {#scopes}

Os [âmbitos OAuth](scopes) personalizados podem ser inicializados a partir da matriz `Scopes`. Cada entrada é inserida ou atualizada por `Name` no arranque (uma entrada sem `Name` é ignorada com um aviso):

```json
{
  "Scopes": [
    {
      "Name": "billing.read",
      "DisplayName": "Billing (read-only)",
      "Description": "View invoices and payment history",
      "UserClaims": ["billing_plan"],
      "ShowInDiscoveryDocument": true,
      "Emphasize": false,
      "Group": "Billing",
      "Required": false,
      "AllowedRoles": ["finance"]
    }
  ]
}
```

Um campo que defina prevalece sobre o valor guardado em cada arranque; um campo que omita preserva o valor guardado. Assim, a configuração pode adicionar ou alterar `UserClaims` e `AllowedRoles`, mas não os pode esvaziar (utilize `PUT /api/v1/scopes/{name}` para isso). O significado dos campos está em [Modelo de âmbitos](scopes#scope-model).

## Aplicações de aprovisionamento {#provisioning-apps-1}

Defina as aplicações a jusante em que os utilizadores devem ser aprovisionados:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-api-key"
    },
    "analytics": {
      "CallbackUrl": "https://analytics.example.com/provisioning",
      "ApiKey": "another-key"
    }
  }
}
```

Consulte [Aprovisionamento](provisioning) para a especificação completa do protocolo TCC.

## Política de MFA {#mfa-policy}

A autenticação multifator é imposta por cliente através da propriedade `MfaPolicy`:

| Valor | Comportamento |
|---|---|
| `Disabled` (predefinição) | Sem desafio de MFA, mesmo que o utilizador tenha MFA inscrita |
| `Enabled` | Apresenta o desafio aos utilizadores que têm MFA inscrita; não obriga à inscrição |
| `Required` | Apresenta o desafio aos utilizadores inscritos; obriga à inscrição os utilizadores sem MFA |

```json
{
  "Clients": [
    {
      "ClientId": "secure-app",
      "MfaPolicy": "Required"
    }
  ]
}
```

Quando a `MfaPolicy` é `Required` e o utilizador não inscreveu MFA, o início de sessão devolve `{ mfaSetupRequired: true, setupToken: "..." }`. O token de configuração autentica o utilizador perante os endpoints de configuração de MFA (através do cabeçalho `X-MFA-Setup-Token`) para que se possa inscrever antes de obter uma sessão por cookie.

Os inícios de sessão federados (SAML/OIDC) também respeitam a política de MFA: um utilizador com MFA inscrita passa pelo desafio de MFA depois de o IdP externo o autenticar, e `Required` obriga à inscrição os utilizadores federados sem MFA.

### Substituição através de IAuthHook {#iauthhook-override}

O método `IAuthHook.ResolveMfaPolicyAsync` pode substituir a política do cliente por utilizador:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    return Task.FromResult(clientPolicy);
}
```

## Política de palavras-passe {#password-policy}

Personalize os requisitos de robustez das palavras-passe:

```json
{
  "PasswordPolicy": {
    "MinLength": 10,
    "MinUniqueChars": 3,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": false
  }
}
```

| Propriedade | Predefinição | Descrição |
|---|---|---|
| `MinLength` | `8` | Comprimento mínimo da palavra-passe |
| `MinUniqueChars` | `2` | Número mínimo de caracteres distintos |
| `RequireUppercase` | `true` | Exige pelo menos uma letra maiúscula |
| `RequireLowercase` | `true` | Exige pelo menos uma letra minúscula |
| `RequireDigit` | `true` | Exige pelo menos um algarismo |
| `RequireSpecialChar` | `true` | Exige pelo menos um carácter não alfanumérico |

A política é imposta na reposição da palavra-passe e no registo de utilizadores pela administração. A interface de início de sessão obtém a política ativa a partir de `GET /api/auth/password-policy` para apresentar os requisitos de forma dinâmica.

## Fornecedores SAML {#saml-providers}

Defina fornecedores de identidade SAML na configuração. São inicializados no arranque:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com", "example.org"]
    }
  ]
}
```

| Propriedade | Obrigatória | Descrição |
|---|---|---|
| `ConnectionId` | Sim | Identificador estável (utilizado em URLs como `/saml/{connectionId}/login`) |
| `ConnectionName` | Não | Nome de apresentação (a predefinição é o ConnectionId) |
| `EntityId` | Sim | O entity ID de SP **deste servidor**, o identificador que regista no IdP, e não o entity ID do próprio IdP |
| `MetadataLocation` | Sim | URL do XML de metadados SAML do IdP. Tem de ser https e encaminhável publicamente, a menos que o anfitrião esteja indicado em [`Auth:AllowedInternalTargets`](#outbound-fetches-ssrf-guard): este documento contém os certificados contra os quais todas as asserções são validadas. Se o seu IdP não publicar nenhum endpoint de metadados https, defina antes `metadataXml` através da [API de administração](admin-api); a inicialização por configuração não tem nenhuma chave para isso. |
| `AllowedDomains` | Não | Domínios de email encaminhados para este fornecedor através de SSO |
| `OrganizationId` | Não | Limita esta ligação a uma [organização](organizations). Null (a predefinição) torna-a uma ligação ao nível do inquilino; só as ligações ao nível do inquilino registam os seus `AllowedDomains` como rotas de domínio SSO |
| `JitProvisioningEnabled` | Não | Cria um utilizador no primeiro início de sessão. Predefinição `false` |
| `AllowUninvitedJit` | Não | Permite que o JIT crie um utilizador numa organização para a qual não foi convidado. Predefinição `false` |
| `ChallengeMfaAfterLogin` | Não | Apresenta o desafio da política de MFA da aplicação depois do início de sessão no IdP. Predefinição `true` |
| `ProvisioningAttributeParams` | Não | Atributos da asserção passados ao aprovisionamento a jusante |
| `AllowUnsolicitedResponses` | Não | Aceita respostas iniciadas pelo IdP (não solicitadas) nesta ligação. Predefinição `false` |

Os booleanos são escritos a partir da inicialização em cada arranque, incluindo a predefinição, pelo que, numa ligação inicializada que um operador alterou através da API de administração, são revertidos no reinício seguinte. Os campos para os quais a inicialização não tem chave (`SpCertificate`, `SignAuthnRequests`, `NameIdFormat`, `MetadataXml`, `IconUrl`) são preservados.

## Fornecedores OIDC {#oidc-providers}

Defina fornecedores de identidade OIDC na configuração. São inicializados no arranque:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

| Propriedade | Obrigatória | Descrição |
|---|---|---|
| `ConnectionId` | Sim | Identificador estável (utilizado em URLs como `/oidc/{connectionId}/login`) |
| `ConnectionName` | Não | Nome de apresentação (a predefinição é o ConnectionId) |
| `MetadataLocation` | Sim | URL do documento de descoberta OpenID Connect do IdP |
| `ClientId` | Sim | Client ID OAuth2 registado no IdP |
| `ClientSecret` | Sim | Segredo de cliente OAuth2 (protegido através de `ISecretProvider` no arranque) |
| `RedirectUrl` | Não | **Ignorado.** O URI de redirecionamento é derivado em cada pedido como `{Issuer}/oidc/callback`: registe *esse* no IdP. Um valor aqui não tem efeito e é registado como ignorado. |
| `AllowedDomains` | Não | Domínios de email encaminhados para este fornecedor através de SSO |
| `OrganizationId` | Não | Limita esta ligação a uma [organização](organizations); null significa ao nível do inquilino |
| `JitProvisioningEnabled` | Não | Cria um utilizador no primeiro início de sessão. Predefinição `false` |
| `AllowUninvitedJit` | Não | Permite que o JIT crie um utilizador numa organização para a qual não foi convidado. Predefinição `false` |
| `UseUpstreamSubjectAsUserId` | Não | Utiliza o `sub` a montante como id do utilizador local. Predefinição `false` |
| `ShowOnLogin` | Não | Mostra um botão para esta ligação no ecrã de início de sessão. Predefinição `true`; as ligações encaminhadas por domínio são alcançadas a partir do email em qualquer caso |
| `ChallengeMfaAfterLogin` | Não | Apresenta o desafio da política de MFA da aplicação depois do início de sessão no IdP. Predefinição `true` |
| `AutoLinkExistingByEmail` | Não | Liga um primeiro início de sessão a uma conta local existente com o mesmo email. Predefinição `false` |
| `PassthroughParams`, `ProvisioningAttributeParams` | Não | Parâmetros passados ao IdP / ao aprovisionamento a jusante |
| `RevalidateOnRefresh` | Não | Volta a verificar a sessão a montante quando um token de atualização é utilizado. Predefinição `false` |
| `IsExternalConnection`, `SessionExpClaim` | Não | Definições de sessão federada; consulte [Sessões federadas](federated-sessions) |
| `InteractionPath` | Não | Caminho da aplicação de início de sessão (por exemplo `/guest`) apresentado antes de um pedido `idp_hint` não autenticado ser federado através desta ligação. Vazio federa diretamente |

A inicialização OIDC substitui mais coisas do que a SAML, em cada arranque. Os booleanos são escritos a partir da inicialização, incluindo a predefinição, pelo que, numa ligação inicializada que um operador alterou através da API de administração, são revertidos no reinício seguinte. O mesmo se aplica a `AllowedDomains`, `PassthroughParams`, `ProvisioningAttributeParams`, `SessionExpClaim` e `InteractionPath`: uma chave que a inicialização omita volta a vazio ou à predefinição, em vez de manter o valor guardado. Só `IconUrl` e `CreatedAt` são sempre preservados, e `ConnectionName` e `OrganizationId` são preservados quando a inicialização os omite.

> **Nota:** os fornecedores também podem ser geridos em tempo de execução através da [API de administração](admin-api). Os fornecedores inicializados pela configuração são inseridos ou atualizados em cada arranque, pelo que as alterações de configuração produzem efeito no reinício.

## Fornecedor de segredos {#secret-provider}

Os segredos de cliente OIDC a montante e as sementes TOTP / MFA podem ser guardados no Azure Key Vault em vez de em texto simples:

| Definição | Descrição |
|---|---|
| `SecretProvider:VaultUri` | URI do Key Vault (por exemplo, `https://my-vault.vault.azure.net/`). Se não estiver definido, é utilizado o fornecedor de **texto simples** e os segredos são guardados tal como estão no Table Storage. |
| `SecretProvider:RequireVaultReferences` | `false` por predefinição. Quando é `true`, uma referência guardada sem prefixo de cofre (`kv:` para o Key Vault, `sm:` para o AWS Secrets Manager) é um **erro**, em vez de ser aceite como valor em texto simples. Defina-o quando uma migração para o cofre tiver terminado. |

Quando está configurado, os valores de segredos que parecem referências do Key Vault são resolvidos em tempo de execução. Utiliza `DefaultAzureCredential` para a autenticação.

### Migrar para um cofre e fechar a porta depois {#migrating-into-a-vault-and-closing-the-door-afterwards}

Ambos os fornecedores suportados por cofre devolvem tal como está uma referência sem prefixo, tratando-a como um valor em texto simples escrito antes de a implementação ter um cofre. É isso que permite migrar um sistema em funcionamento um segredo de cada vez, em vez de tudo de uma só vez, mas, se ficar aberto, é um caminho permanente de despromoção: qualquer coisa que consiga escrever uma coluna de configuração (uma migração a meio, um caminho de administração que guarda um valor em bruto onde deveria estar uma referência, um atacante com acesso ao armazenamento mas sem acesso ao cofre) substitui um segredo protegido pelo cofre por um valor à sua escolha, e a verificação passa perfeitamente, porque, numa referência sem prefixo, a referência *é* o valor.

Defina `SecretProvider:RequireVaultReferences` quando a migração estiver concluída. Resolver uma referência sem prefixo passa então a lançar uma exceção, em vez de devolver discretamente texto em claro. Defini-lo enquanto o fornecedor resolvido for o de texto simples é recusado no arranque, uma vez que essa combinação não tem nenhum estado funcional: todas as referências que o fornecedor de texto simples escreve não têm prefixo.

O servidor regista também um aviso no arranque sempre que um anfitrião que não seja de desenvolvimento acaba com o fornecedor de texto simples.

> ⚠️ **Produção: defina `SecretProvider:VaultUri`.** O fornecedor de segredos predefinido é de **texto simples**. Quando `SecretProvider:VaultUri` não está definido, os segredos de cliente OIDC a montante e as sementes TOTP / MFA são escritos no Azure Table Storage em claro e, por isso, aparecem em claro em qualquer [cópia de segurança](backup-restore). Em qualquer implementação de produção, configure `SecretProvider:VaultUri` para que estes segredos sejam guardados no Key Vault.

## API de administração {#admin-api}

| Definição | Predefinição | Descrição |
|---|---|---|
| `AdminApi:Enabled` | `true` | **Ativada por predefinição.** Defina-a como `false` para desativar todos os endpoints de administração (não serão registados). |
| `AdminApi:Scope` | `authagonal-admin` | Âmbito JWT necessário para aceder aos endpoints de administração. Altere-o para corresponder ao nome de âmbito que já utiliza (por exemplo, `projects-identity-admin` em migrações a partir do IdentityServer). |

> ⚠️ **A API de administração está ativada por predefinição e é altamente privilegiada.** O âmbito de administração concede gestão total e personificação de utilizadores: qualquer pessoa que detenha um token com `AdminApi:Scope` pode emitir tokens para qualquer utilizador, gerir clientes e ler/escrever toda a configuração. Restrinja na rede o acesso aos endpoints de administração (as rotas de administração `/api/v1/*`) e controle rigorosamente a quem pode ser emitido o âmbito de administração. Como medida de defesa em profundidade, o âmbito é *reservado*: nunca pode ser concedido a um cliente OAuth (consulte [API de administração](admin-api)) e não pode ser emitido através do endpoint de personificação. Defina `AdminApi:Enabled = false` se a API de administração não for utilizada.

## Consentimento {#consent}

O consentimento por cliente pode ser ativado com a propriedade `RequireConsent`:

| Valor | Comportamento |
|---|---|
| `false` (predefinição) | A autorização prossegue imediatamente após a autenticação |
| `true` | É apresentado ao utilizador um ecrã de consentimento que lista os âmbitos pedidos. O consentimento é persistido durante 5 anos e só volta a ser pedido quando são pedidos novos âmbitos. |

Os utilizadores podem ver e revogar as suas concessões de consentimento em `GET /consent/grants` e `DELETE /consent/grants/{clientId}`.

## Back-Channel Logout {#back-channel-logout}

Registe um `BackChannelLogoutUri` num cliente para receber notificações OIDC Back-Channel Logout 1.0. Quando um utilizador termina sessão, a Authagonal envia um token de logout assinado (JWT) para o URI registado de cada cliente.

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback"
    }
  ]
}
```

## Email {#email}

O remetente de email integrado utiliza o [Resend](https://resend.com) e **é ativado automaticamente** quando `Email:ResendApiKey` está configurado, sem necessidade de registar nenhum serviço. Para utilizar outro fornecedor, registe a sua própria implementação de `IEmailService` antes de chamar `AddAuthagonal()` (tem precedência independentemente das chaves `Email:*`).

| Definição | Descrição |
|---|---|
| `Email:ResendApiKey` | Chave de API do Resend. Quando está definida, é utilizado o remetente Resend integrado. |
| `Email:SenderEmail` | Endereço de email do remetente |
| `Email:SenderName` | Nome de apresentação do remetente (a predefinição é `"Authagonal"`) |

> ⚠️ **Sem nenhum remetente de email, o autorregisto não funciona.** Quando `Email:ResendApiKey` não está definido e nenhum `IEmailService` personalizado está registado, um serviço que não faz nada descarta silenciosamente todo o correio, os emails de verificação e de reposição da palavra-passe nunca chegam e, como o início de sessão exige por predefinição um email confirmado, os utilizadores que se registem por si próprios nunca conseguem iniciar sessão. `UseAuthagonal` regista um aviso no arranque nesse estado. Saída de emergência para desenvolvimento/testes: `Auth:AutoConfirmEmailDomains` confirma automaticamente os registos dos domínios indicados.

Os emails para endereços `@example.com` são ignorados silenciosamente (útil para testes).

## Cluster {#cluster}

A camada de clustering disponibiliza **eleição de líder** (para que as tarefas condicionadas ao líder, como a rotação da chave de assinatura, sejam executadas em exatamente um nó) e um **barramento de eventos entre nós**, com backends conectáveis. A predefinição é em processo: um único nó é sempre o seu próprio líder, a definição certa para um único nó e para desenvolvimento local, sem qualquer configuração.

| Definição | Variável de ambiente | Predefinição | Descrição |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Interruptor principal. Quando é `false`, o nó é executado de forma autónoma (sempre líder, barramento de eventos em processo). |
| `Cluster:Secret` | `Cluster__Secret` | *(nenhum)* | Segredo partilhado exigido no endpoint exclusivamente interno `/_internal/backchannel-logout`. Quando está definido, quem chama tem de o apresentar no cabeçalho `X-Cluster-Secret` (comparado em tempo constante). Quando **não está definido, o endpoint não autoriza ninguém** e responde 404: um endereço de origem não é uma credencial, e loopback é exatamente o que um reverse proxy no mesmo anfitrião apresenta em cada pedido que reencaminha, incluindo os que têm origem na internet. |
| `Cluster:AllowLoopbackWithoutSecret` | `Cluster__AllowLoopbackWithoutSecret` | `false` | Opção para desenvolvimento: sem `Cluster:Secret`, aceita quem chama cujo **endereço de par antes do reencaminhamento** seja loopback. Os intervalos privados continuam a ser recusados: numa rede de cluster partilhada, isso significaria confiar em todas as cargas de trabalho vizinhas. Não a defina num anfitrião atrás de um reverse proxy. |
| `Cluster:RunLeaderElection` | `Cluster__RunLeaderElection` | `true` | Se este nó executa o ciclo de renovação do lease e pode tornar-se líder. Com `false`, o nó continua a aderir ao cluster e a consumir o barramento de eventos; simplesmente nunca disputa o lease. Adequa-se a um nó que tem de receber eventos do cluster mas nunca pode deter a liderança. |
| `Cluster:LeaseTtlSeconds` | `Cluster__LeaseTtlSeconds` | `30` | Duração do lease de liderança. É renovado aproximadamente a meio deste intervalo. |
| `Cluster:PollIntervalSeconds` | `Cluster__PollIntervalSeconds` | `3` | Frequência com que o backend do barramento de eventos consulta as mensagens publicadas por outros nós. |

**As implementações com vários nós** substituem-no por um backend real através do callback `configureClustering` em `AddAuthagonal` / `AddAuthagonalCore`:

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS equivalent (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// Self-hosted PostgreSQL (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` registam apenas o barramento de eventos, mantendo o lease em processo, para nós que têm de receber eventos do cluster mas nunca podem disputar a liderança.

Consulte [Escalabilidade](scaling) para saber como a liderança e o barramento de eventos se comportam entre instâncias.

## Cabeçalhos reencaminhados (proxy de confiança) {#forwarded-headers-trusted-proxy}

A Authagonal indexa o limite de taxa e o bloqueio de contas pelo IP do cliente, e só emite HSTS em pedidos HTTPS. Atrás de um reverse proxy / ingress, o IP real do cliente e o esquema chegam nos cabeçalhos `X-Forwarded-For` / `X-Forwarded-Proto`. Estas definições controlam **que saltos de proxy são de confiança** para definir esses valores, para que quem chama não possa falsificar `X-Forwarded-For` para forjar o IP do cliente.

| Definição | Variável de ambiente | Predefinição | Descrição |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | Número de saltos de proxy a respeitar a partir da direita da cadeia `X-Forwarded-For`. A predefinição `1` confia apenas no único salto que o seu ingress acrescenta e ignora tudo o que esteja mais à esquerda na cadeia. |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0` (matriz) | *(vazio)* | Intervalos CIDR (matriz de cadeias, por exemplo `"10.0.0.0/8"`) autorizados a definir cabeçalhos reencaminhados. Defina-o com o CIDR do seu proxy / ingress / pods. Declará-lo é o que permite que `X-Forwarded-Proto` seja sequer respeitado; ver abaixo. |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies__0` (matriz) | *(vazio)* | Endereços IP individuais de proxies (matriz de cadeias) autorizados a definir cabeçalhos reencaminhados. Utilize-o juntamente com `KnownNetworks` ou em vez dele. |

```json
{
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"],
    "KnownProxies": []
  }
}
```

### Os dois cabeçalhos não merecem a mesma confiança {#the-two-headers-are-not-trusted-on-the-same-terms}

`X-Forwarded-For` ajusta o **IP do cliente**, a chave de que dependem o limite de taxa, o bloqueio e a proteção de `/_internal`. Sem nada declarado, a Authagonal respeita-o a partir dos intervalos de loopback e RFC1918 e regista um aviso. É uma predefinição de melhor esforço, e é melhor do que o comportamento da framework com um conjunto de confiança vazio, que é respeitar o cabeçalho vindo de *qualquer* autor de chamada.

`X-Forwarded-Proto` altera o **esquema**, e o esquema decide se `/connect/*` responde de todo (RFC 6749 §3.1/§3.2), se os cookies são marcados como `Secure` e se os URLs absolutos gerados são https. Só é respeitado vindo de um proxy que tenha declarado em `KnownNetworks` / `KnownProxies`. Um endereço privado não é uma declaração: a Authagonal é distribuída como biblioteca e não consegue ver a rede em que foi implementada, pelo que "o par tem um endereço privado" é uma suposição sobre a topologia. Numa LAN plana, numa VPC partilhada ou numa bridge de contentores partilhada, todas as cargas de trabalho vizinhas estão dentro desses intervalos e poderiam afirmar `https` num pedido que chegou sem encriptação.

**Se o seu proxy não tiver um endereço fixo** (um ingress do Kubernetes, um balanceador de carga rotativo, uma plataforma que não lhe indica o CIDR do salto), declare todos os pares como proxies:

```json
{
  "ForwardedHeaders": {
    "KnownNetworks": ["0.0.0.0/0", "::/0"]
  }
}
```

Isso é seguro precisamente quando nada além do proxy consegue chegar ao processo, que é o pressuposto em que uma implementação assim já assenta. Escrevê-lo coloca-o num sítio onde pode ser revisto, em vez de deixar a biblioteca inferi-lo. Se outras cargas de trabalho *conseguirem* chegar diretamente ao Kestrel, podem falsificar o esquema e o IP do cliente com esta definição, pelo que deve fixar antes o CIDR real.

### Proxy não declarado: todas as quotas por origem são partilhadas {#undeclared-proxy-every-per-source-quota-is-shared}

Os limites de taxa indexados pelo endereço de quem chama (início de sessão, registo, palavra-passe esquecida, registo dinâmico de clientes, o SAML ACS) precisam de saber que cliente fez o pedido. Atrás de um reverse proxy, esse é o IP do cliente reencaminhado, e o IP do cliente reencaminhado só é prova se tiver declarado o proxy que o escreveu. Sem nada declarado, a Authagonal indexa essas quotas pelo par que efetivamente observa, que, atrás de um proxy, é o proxy: **todos os clientes partilham um único orçamento, e qualquer autor de chamada isolado pode esgotá-lo para todos** (a predefinição do início de sessão é de 30 tentativas por 5 minutos).

Isto é deliberado e não um defeito, e não pode ser corrigido no servidor. A alternativa, indexar mesmo assim pelo valor reencaminhado, dá a quem chama um orçamento novo por pedido bastando variar um cabeçalho, porque, atrás de um balanceador de carga L4, o salto reencaminhado mais à direita *é* o cabeçalho do próprio autor da chamada. Em qual das duas situações se encontra é exatamente o que a declaração diz ao servidor, e nada mais o pode dizer. Declare o proxy e as quotas passam a ser por cliente.

> ⚠️ **É necessário um proxy que termine o TLS, e tem de estar declarado.** A Authagonal tem de ser executada atrás de um reverse proxy que termine o TLS (ou terminar ela própria o TLS). O HSTS (`Strict-Transport-Security`) só é emitido em pedidos HTTPS, e os endpoints OAuth recusam liminarmente pedidos sem encriptação, a menos que `Auth:AllowInsecureHttp` esteja definido, pelo que o proxy tem de reencaminhar `X-Forwarded-Proto: https` **e** estar indicado em `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` para que o HSTS seja enviado e `/connect/*` sequer responda. Não declarar nada é a falha mais comum numa atualização: o cabeçalho chega, nada tem legitimidade para agir sobre ele, e todos os pedidos `/connect/*` respondem 400 numa implementação que está efetivamente em TLS. O registo de arranque indica-o, tal como o corpo da recusa.

## Pedidos de saída (proteção contra SSRF) {#outbound-fetches-ssrf-guard}

A Authagonal faz pedidos HTTP iniciados pelo servidor para URLs que não escolheu: os metadados SAML ou o documento de descoberta OIDC de um IdP a montante, o `jwks_uri` de um cliente durante a autenticação `private_key_jwt`, um URI de back-channel logout, um callback de aprovisionamento. Alguns desses URLs são fornecidos por quem registou um cliente, e um URL que indique `169.254.169.254` ou um anfitrião dentro do seu cluster passa então a ser um pedido que a Authagonal faz em nome de um atacante.

Cada um desses pedidos é protegido duas vezes. A **verificação do URL** recusa esquemas que não sejam http(s), endereços internos literais e nomes `localhost` / `.local` / `.internal`, no momento em que o URL é aceite (uma escrita de administração, um registo dinâmico de cliente), onde o erro pode ser atribuído a quem o escreveu. A **verificação do endereço** é executada no socket: resolve o anfitrião, recusa todos os endereços devolvidos que sejam internos e liga-se a um endereço que efetivamente verificou, em vez de devolver o nome ao sistema operativo. É a segunda que faz o que uma verificação de texto não consegue, porque um nome de anfitrião não é um texto sobre o qual o atacante tenha de ser honesto: `logout.attacker.test` passa todas as regras de sufixo e de literais e depois responde com o endereço de metadados da cloud. Como um redirecionamento é uma nova ligação, a verificação do endereço é repetida em cada salto.

Ambas estão ativas por predefinição e a maioria das implementações nunca dá por elas. Há duas situações que as tornam visíveis.

### Chegar intencionalmente a um destino interno {#reaching-an-internal-destination-on-purpose}

Federar com um IdP que só é alcançável pela sua rede privada, ou aprovisionar uma aplicação que é executada no mesmo cluster, é recusado exatamente pela mesma regra que trava o ataque. Indique esses destinos:

```json
{
  "Auth": {
    "AllowedInternalTargets": ["idp.corp.internal", "*.svc.corp.internal", "10.4.0.0/16"]
  }
}
```

| Forma da entrada | Permite |
|---|---|
| `idp.corp.internal` | Esse anfitrião exato, e todos os endereços para que é resolvido |
| `*.corp.internal` | Qualquer anfitrião sob o sufixo, e todos os endereços para que esses são resolvidos |
| `10.4.0.0/16`, `fd00:1234::/48` | Essa rede, com qualquer nome |
| `10.4.1.7` | Esse único endereço, com qualquer nome |

A forma como variável de ambiente é `Auth__AllowedInternalTargets__0`, `__1` e assim sucessivamente. Uma entrada CIDR malformada falha no arranque, em vez de não permitir nada sem aviso.

**Esta lista só abrange os URLs que o operador forneceu.** O pedido de metadados SAML a montante, a descoberta OIDC a montante (incluindo o `token_endpoint`, o `userinfo_endpoint` e o `jwks_uri` que esse documento indica) e os callbacks de aprovisionamento. Deliberadamente **não** abrange um `jwks_uri` ou um URI de back-channel logout registados por um cliente, em que um anfitrião interno nunca corresponde a uma implementação legítima, pelo que abrir um destino de federação não pode também abrir o serviço de metadados a um pedido `/connect/token` anónimo. Não existe nenhum "desligar" global.

Note que o https continua a ser exigido em ambos os URLs de metadados de federação, independentemente desta lista. Esse documento contém as chaves e os certificados contra os quais todas as asserções a montante são validadas, e uma rede privada não é um canal seguro.

> ⚠️ **Anfitriões multi-inquilino: verifique quem escreve o URL de metadados antes de indicar qualquer coisa.** Esta lista está limitada aos destinos que *o operador* configurou e, numa implementação com um único inquilino, o administrador das ligações é o próprio operador. Se executar a Authagonal para outras pessoas (um SaaS em que os administradores dos inquilinos configuram as suas próprias ligações SAML/OIDC através do portal ou da API de administração), então `MetadataLocation` é fornecido pelo **cliente**, e cada entrada que adicionar aqui fica alcançável por qualquer inquilino que aponte uma ligação para ela. Deixe-a vazia num anfitrião assim (a predefinição) e, se um inquilino precisar mesmo de um IdP local, dê-lhe um caminho de saída que termine fora da sua rede, em vez de abrir um a partir de dentro dela.

### Se a sua saída exigir um proxy HTTP {#if-your-egress-requires-an-http-proxy}

A verificação do endereço está associada a `SocketsHttpHandler.ConnectCallback` e, com um proxy em vigor, o .NET invoca esse callback com o endpoint do **proxy** e nunca com o do destino, pelo que a verificação inspecionaria o proxy, consideraria-o perfeitamente encaminhável e permitiria tudo. Falharia de forma aberta precisamente nas redes com maior probabilidade de ter um proxy. Por isso, os clientes protegidos definem `UseProxy = false` e, numa rede só com proxy, os seus pedidos falham.

`Auth:AllowOutboundProxy` volta a enviar através do proxy os pedidos configurados pelo operador (metadados SAML, descoberta OIDC, callbacks de aprovisionamento). Mantém a verificação do URL e perde a verificação do endereço para esses pedidos: um nome de anfitrião que seja resolvido para um endereço interno deixa de ser detetado. **Não** abrange o pedido ao `jwks_uri` do cliente nem a entrega de back-channel logout: esses destinos são escolhidos por quem regista o cliente e alcançáveis a partir de pedidos anónimos, pelo que não existe nenhum interruptor para eles. Uma rede que tenha de os encaminhar por proxy precisa de um gateway de saída com filtragem de SSRF à frente deles.

`UseAuthagonal()` regista um aviso no arranque quando encontra `HTTPS_PROXY`, `HTTP_PROXY` ou `ALL_PROXY` definidos, indicando que clientes o contornam; caso contrário, o sintoma é "o SSO deixou de funcionar", sem nada que aponte para a causa.

### O que não está protegido {#what-is-not-guarded}

Os clientes de saída do BFF e o envio de emails. `AuthagonalBffOptions.Upstreams[].TargetBaseUrl` é configuração sua, cujo exemplo documentado é um endereço interno, o cliente de tokens do BFF comunica com a autoridade que configurou, e o proxy já recusa qualquer destino composto que tenha saído da autoridade a montante configurada, pelo que quem chama não consegue desviar esses pedidos. O `Resend` faz POST para uma constante definida em tempo de compilação. Os três utilizam normalmente o proxy do ambiente.

## Limite de taxa {#rate-limiting}

Os limites de taxa integrados protegem os endpoints mais sujeitos a abuso:

| Endpoint | Limite | Janela | Indexado por |
|---|---|---|---|
| `POST /api/auth/login` | 30 (`Auth:MaxLoginAttemptsPerIp`) | 5 minutos (`Auth:LoginWindowMinutes`) | Endereço de origem e, separadamente, o email submetido |
| `POST /api/auth/register` | 5 (`Auth:MaxRegistrationsPerIp`) | 1 hora (`Auth:RegistrationWindowMinutes`) | IP do cliente |
| `POST /api/auth/forgot-password` | 3 (`Auth:MaxPasswordResetsPerEmail`) | 1 hora (`Auth:PasswordResetWindowMinutes`) | Email de destino |
| `POST /api/auth/forgot-password` | 15 (`Auth:MaxPasswordResetsPerIp`) | 1 hora (`Auth:PasswordResetWindowMinutes`) | IP do cliente |
| `POST /connect/register` (quando ativado) | 10 | 1 hora | IP do cliente |
| Endpoints SCIM | 200 | 1 minuto | Cliente SCIM |

Por predefinição, os limites são impostos **em processo, por nó** (através do ponto de extensão `IRateLimiter`), pelo que, com N instâncias, o limite efetivo é N vezes o valor configurado. Trate-os como uma salvaguarda e imponha o limite global de referência na periferia da rede (WAF / ingress / CDN). Consulte [Escalabilidade](scaling#rate-limiting).

### Limites para todo o cluster (`Auth:DurableRateLimiting`) {#cluster-wide-limits-authdurableratelimiting}

Defina `Auth:DurableRateLimiting` como `true` para mover os contadores para o armazenamento que a implementação já
utiliza, para que todas as réplicas partilhem um único orçamento e o limite deixe de se multiplicar pelo número de instâncias.

| | em processo (predefinição) | durável |
|---|---|---|
| Limite com N réplicas | N vezes o valor configurado | o valor configurado |
| Custo por verificação | nenhum | uma ida e volta ao armazenamento |
| Sobrevive ao reinício de um pod | não | sim |
| Backends | qualquer um | Azure Table, SQL, DynamoDB |

Vale a pena ativá-lo quando um orçamento protege algo que se pode adivinhar, sobretudo o `user_code` do fluxo de dispositivo, em que
o limite de tentativas é a única coisa entre um atacante e um código que concede uma sessão ativa, e um
orçamento que cresce com o número de réplicas tem a forma errada. É menos útil para os limites de volume, em que
a periferia da rede é, de qualquer forma, o limite de referência.

Detalhes que importam em produção:

- **Não é gratuito.** Cada verificação de limite de taxa passa a ser uma ida e volta ao armazenamento, incluindo nos caminhos de início de sessão, de token
  e de SCIM. Uma implementação com um único nó não ganha nada (nesse caso, por nó *é* para todo o cluster) e deve
  mantê-lo desativado.
- **Janelas fixas, pelo que as rajadas podem atravessar uma fronteira.** Um orçamento de N é "N por janela, e até 2N
  à volta de uma fronteira", e os orçamentos incluídos têm essa margem. É isto que permite que o contador seja um único
  incremento atómico em todos os backends, que é a propriedade em que a correção assenta.
- **Falha de forma aberta.** Se o armazenamento estiver inacessível, o pedido é permitido e é registado um erro: o
  limitador protege o caminho de início de sessão e não se pode tornar uma forma de o deitar abaixo. Mantenha a regra na periferia da rede.
- **O anfitrião não arranca** se definir isto sem um fornecedor que disponibilize `IRateLimitCounterStore`.
  Recusa, em vez de voltar discretamente ao limite por nó que acabou de desativar.
- **As linhas dos contadores são recolhidas automaticamente**: no DynamoDB por TTL nativo, no SQL por `SqlExpiryReaper`, no Azure
  Table por uma varredura feita apenas pelo líder (o Table Storage não tem nem TTL nem aritmética do lado do servidor, pelo que é também
  o backend em que um incremento custa uma leitura mais uma escrita condicional).

## CORS {#cors}

O CORS é configurado dinamicamente e **delimitado por caminho**: a descrição anterior, de uma linha ("as origens de todos
os clientes registados são permitidas automaticamente"), descrevia bastante mais do que o fornecedor faz.

- **As origens registadas pelos clientes** (`AllowedCorsOrigins` num cliente) só são respeitadas em `/connect/` e
  `/.well-known/`. **Não** abrem `/api/auth/`, `/api/v1/` nem `/scim/`. Um cliente desativado não contribui com
  nada, e uma origem malformada é descartada.
- **As credenciais nunca são permitidas** em `/api/auth/`, `/api/v1/`, `/scim/`, `/consent` ou `/approvals`, para
  nenhuma origem, seja configurada pelo operador ou registada por um cliente. Um cliente de browser que chame esses caminhos com
  `credentials: 'include'` a partir de outra origem falha, independentemente da configuração; utilize um
  backend-for-frontend (consulte o pacote `@authagonal/bff`) em vez de chamadas entre origens com credenciais.
- As políticas resolvidas ficam em cache durante 60 minutos.

Assim, uma origem adicionada aos `AllowedCorsOrigins` de um cliente faz `/connect/*` funcionar e não faz `/api/v1/*`
funcionar. Isso é deliberado: esses caminhos transportam o cookie de sessão e a superfície de administração.

## HashiCorp Vault Transit {#hashicorp-vault-transit}

O `VaultTransitClient` comunica com o motor de segredos Transit do Vault: assinatura, verificação, encriptação, desencriptação e HMAC com chave.
É a peça de base para um `IFieldCipher` ou um `IIndexTokenizer` suportados pelo Vault, que o próprio operador regista.

**A assinatura de JWT não é delegada no Vault.** O `ProtocolKeyManager` assina sempre com a chave em
`ISigningKeyStore`, e não existe nenhum ponto de extensão que a substitua por uma chave do Vault. Consulte
[Extensibilidade](extensibility) para saber o que isso exigiria.

Isto é configurado programaticamente quando a Authagonal é alojada como biblioteca.

## Exemplo completo {#full-example}

```json
{
  "Storage": {
    "TableServiceUri": "https://myaccount.table.core.windows.net/",
    "NameIndexesEnabled": true
  },
  "Issuer": "https://auth.example.com",
  "LoginAppUrl": "/login",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "RefreshTokenReuseGraceSeconds": 0,
    "DynamicClientRegistrationEnabled": false,
    "SigningKeyLifetimeDays": 90
  },
  "SecretProvider": {
    "VaultUri": "https://my-vault.vault.azure.net/"
  },
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"]
  },
  "Cluster": {
    "Enabled": true,
    "Secret": "shared-secret-here"
  },
  "AdminApi": {
    "Enabled": true,
    "Scope": "authagonal-admin"
  },
  "Authentication": {
    "CookieLifetimeHours": 48
  },
  "PasswordPolicy": {
    "MinLength": 8,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": true
  },
  "Email": {
    "ResendApiKey": "re_xxx",
    "SenderEmail": "noreply@example.com",
    "SenderName": "Example Auth"
  },
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com"]
    }
  ],
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "...",
      "ClientSecret": "...",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["gmail.com"]
    }
  ],
  "ProvisioningApps": {
    "backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret"
    }
  },
  "Clients": [
    {
      "ClientId": "web",
      "ClientName": "Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "MfaPolicy": "Enabled",
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
