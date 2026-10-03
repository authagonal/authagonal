---
layout: default
title: Instalação
locale: pt
---

# Instalação

## Docker (recomendado) {#docker-recommended}

Obtenha e execute a imagem pré-compilada:

```bash
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  drawboardci/authagonal
```

## Docker Compose {#docker-compose}

Para desenvolvimento local com o Azurite (emulador do Azure Storage):

```yaml
services:
  azurite:
    image: mcr.microsoft.com/azure-storage/azurite
    ports:
      - "10000:10000"
      - "10001:10001"
      - "10002:10002"

  authagonal:
    build: .
    ports:
      - "8080:8080"
    environment:
      - Storage__ConnectionString=DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://azurite:10002/devstoreaccount1;
      - Issuer=http://localhost:8080
      # Local development only: the OAuth endpoints answer plain http. See below.
      - Auth__AllowInsecureHttp=true
    depends_on:
      - azurite
```

```bash
docker compose up
```

> ⚠️ **`Auth:AllowInsecureHttp` é uma definição de desenvolvimento.** As §3.1/§3.2 da RFC 6749 exigem TLS nos endpoints de autorização e de token, pelo que a Authagonal recusa pedidos não https para `/connect/*`, a menos que esta definição esteja ativa. O esquema é lido depois do processamento dos cabeçalhos reencaminhados, pelo que um proxy que termina o TLS e reencaminha `X-Forwarded-Proto: https` cumpre o requisito com a definição desativada, que é o que deve fazer qualquer implementação acessível a alguém além de si. Com a definição ativa, um observador no percurso consegue ler o código de autorização, o segredo do cliente no cabeçalho `Authorization: Basic` e os tokens de acesso e de atualização. Consulte [Configuração](configuration#authentication).

## Compilar a partir do código-fonte {#building-from-source}

### Pré-requisitos {#prerequisites}

- SDK do .NET 10
- Node.js 24+

A Authagonal tem como destino `net9.0` e `net10.0` e requer, em tempo de execução, um shared framework **com as correções aplicadas**: **no mínimo 9.0.18 ou 10.0.10**. Consulte a [lista de verificação de segurança para produção](#production-security-checklist) para saber porquê, e `Auth:RequireMinimumRuntime` para transformar a verificação no arranque numa recusa.

### Compilação {#build}

```bash
# Build everything
dotnet build

# Build the login SPA
cd login-app
npm ci
npm run build

# Run the server
dotnet run --project src/Authagonal.Server
```

### Compilação Docker {#docker-build}

```bash
# Server image (multi-stage: builds SPA + .NET in one image)
docker build -t authagonal .

# Migration tool
docker build -f Dockerfile.migration -t authagonal-migration .
```

## Como biblioteca (NuGet) {#as-a-library-nuget}

Referencie os pacotes da Authagonal no seu próprio projeto ASP.NET Core:

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.AzureProvider" Version="x.y.z" />
```

O pacote do fornecedor de armazenamento é conectável: `Authagonal.AzureProvider` para o Azure Table Storage (a ligação predefinida de `AddAuthagonal()`), `Authagonal.SqlProvider` para PostgreSQL ou SQLite self-hosted (consulte [Backend SQL](#sql-backend)) ou `Authagonal.AwsProvider` para DynamoDB / S3 / Secrets Manager (consulte [Backend AWS](#aws-backend)).

> **A ordem de registo é importante.** Um fornecedor de armazenamento tem de ser registado **antes** de `AddAuthagonal()`. É esse registo de `IUserStore` já existente que faz com que `AddAuthagonal()` ignore a sua ligação integrada ao Azure Table Storage; um fornecedor registado depois perde, sem qualquer aviso, todas as interfaces que `AddAuthagonal()` já preencheu, porque esses registos utilizam `TryAdd`.
>
> Três interfaces (`IOrganizationStore`, `IOrganizationMembershipStore` e `IScimGroupRoleMappingStore`) têm implementações alternativas em memória, vazias e só de leitura, para que a DI consiga resolvê-las num anfitrião que não ligue nenhum armazenamento para elas. `AddAuthagonal()` retira essas alternativas do caminho antes de o fornecedor de armazenamento se registar e repõe-nas com `TryAdd` depois, de modo que o armazenamento durável de um fornecedor prevalece sempre e a alternativa continua a cobrir um anfitrião que não tenha nenhum. Se registar a sua própria implementação de qualquer uma das três, registe-a antes de `AddAuthagonal()`, como qualquer outro armazenamento.

Em seguida, componha-a no seu `Program.cs`:

```csharp
builder.Services.AddSingleton<IAuthHook, MyAuditHook>();   // Custom hook
builder.Services.AddSingleton<IEmailService, MyEmailService>(); // Custom email
builder.Services.AddAuthagonal(builder.Configuration);

var app = builder.Build();
app.UseAuthagonal();
app.MapAuthagonalEndpoints();
app.MapFallbackToFile("index.html");
app.Run();
```

Consulte [Extensibilidade](extensibility) para todos os pontos de substituição e [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server) para um exemplo completo.

### Email {#email}

O remetente [Resend](https://resend.com) integrado é ativado automaticamente quando `Email:ResendApiKey` e `Email:SenderEmail` estão configurados, sem necessidade de registar nenhum serviço. Sem nenhum `IEmailService`, os emails de verificação e de redefinição da palavra-passe são **descartados sem aviso** e, como o início de sessão exige, por predefinição, um email confirmado, os utilizadores que se registaram por si próprios nunca conseguem iniciar sessão (`UseAuthagonal` regista um aviso no arranque). Defina as chaves `Email:*`, registe o seu próprio `IEmailService` antes de `AddAuthagonal()` ou indique os seus domínios em `Auth:AutoConfirmEmailDomains` para saltar a verificação (apenas em desenvolvimento/testes). Consulte [Configuração → Email](configuration#email).

## Backend SQL {#sql-backend}

Para executar na sua própria base de dados em vez de num serviço na cloud, referencie `Authagonal.SqlProvider` e registe-o **antes** de `AddAuthagonal()`: são esses registos que fazem com que `AddAuthagonal()` ignore a sua ligação ao Azure Table Storage:

```csharp
using Authagonal.SqlProvider;

// PostgreSQL: the production self-hosted backend
builder.Services.AddAuthagonalPostgres(
    "Host=db;Database=authagonal;Username=auth;Password=…;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/db-ca.pem");

// or SQLite: one file, no server. Suits embedded hosts, CI and small single-node deployments
builder.Services.AddAuthagonalSqlite("Data Source=authagonal.db");

builder.Services.AddAuthagonal(builder.Configuration);
```

As tabelas espelham uma a uma as estruturas do Azure e do DynamoDB e são criadas no arranque se não existirem (todas as instruções são `IF NOT EXISTS`, pelo que é seguro que vários pods as executem em simultâneo, e não têm qualquer efeito sobre um esquema que tenha aprovisionado por si). Não é necessária nenhuma configuração `Storage:*`. O conjunto de chaves de DataProtection é persistido na mesma base de dados, pelo que os cookies e os tokens antiforgery sobrevivem a reinícios e funcionam entre pods sem nenhum serviço adicional.

O SQLite serializa as escritas, pelo que é um backend de nó único: o lease em processo e o barramento de eventos do cluster registados por predefinição são a combinação correta nesse caso. Uma implementação PostgreSQL com vários pods precisa de `clustering.UseSql(dataSource)` para a eleição do líder.

> **Ordenação (collation).** No PostgreSQL, as colunas de chave estão fixadas em `COLLATE "C"`. O esquema de chaves é ordinal ao byte em todo o lado (limites de prefixo, intervalos de partição por ambiente, a varredura de expiração de concessões, paginação por conjunto de chaves), e uma base de dados criada com uma ordenação linguística (`en_US.UTF-8` e os locales ICU são as predefinições comuns) ordenaria a pontuação e as maiúsculas/minúsculas de outra forma e devolveria, sem aviso, as linhas erradas. A fixação torna a estrutura independente da forma como a base de dados foi criada; não precisa de a criar de nenhuma forma em particular.

> ⚠️ **O material das chaves vive nessa base de dados.** No Azure, a chave de assinatura de tokens está no Table Storage e o conjunto de chaves de DataProtection num contentor de Blob, cada um com RBAC atribuível de forma independente; na AWS, no DynamoDB e no S3. Em SQL, ambos são tabelas atrás da mesma cadeia de ligação que tudo o resto, por isso trate a cadeia de ligação como equivalente à chave de assinatura: caso contrário, um `pg_dump`, uma réplica de leitura, uma função de análise com `SELECT` ou uma cópia de segurança restaurada dão simultaneamente a capacidade de emitir tokens para qualquer titular e as chaves por trás de todos os cookies de autenticação. Registe um `IFieldCipher` antes de `AddAuthagonalPostgres()` para encriptar `SigningKeys.keyMaterialJson` em repouso, e defina `DataProtection:KeyVaultKeyId` ou `DataProtection:CertificateThumbprint` para que o conjunto de chaves não seja guardado com uma `<masterKey>` sem proteção: uma nova implementação que persista o conjunto sem uma é recusada no arranque, e uma existente recebe um aviso de nível `Critical` em cada arranque. Consulte o [README do pacote](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider#dataprotection-keys) para ambos, e para apontar o conjunto de chaves para um esquema separado com a sua própria função.

Consulte o [README do pacote](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider) para a estrutura das tabelas, as primitivas de concorrência por trás de cada garantia de utilização única e como adicionar um dialeto para outro motor.

## Backend AWS {#aws-backend}

Para executar na AWS em vez de no Azure, referencie `Authagonal.AwsProvider` e registe o pacote AWS **antes** de `AddAuthagonal()`: são esses registos que fazem com que `AddAuthagonal()` ignore a sua ligação ao Azure Table Storage:

```csharp
using Authagonal.AwsProvider;

builder.Services.AddAuthagonalAwsStorage(
    dynamoDb,                // IAmazonDynamoDB: required
    secretsManager,          // IAmazonSecretsManager: optional; replaces the plaintext ISecretProvider
    s3,                      // IAmazonS3: optional; used for DataProtection keys
    "my-auth-keys-bucket");  // S3 bucket for the DataProtection key ring
builder.Services.AddAuthagonal(builder.Configuration);
```

As tabelas do DynamoDB espelham uma a uma a estrutura do Azure e são garantidas no arranque (de forma idempotente, sem efeito quando já foram aprovisionadas pelo Terraform). As credenciais são resolvidas através da cadeia padrão da AWS (ambiente / função de instância EC2 / IRSA), pelo que não existe a divisão entre cadeia de ligação e identidade gerida, e não é necessária nenhuma configuração `Storage:*`.

> ⚠️ **Chaves de DataProtection no S3.** Sem um cliente S3 + bucket, o conjunto de chaves de Data Protection do ASP.NET Core é mantido em memória, o que serve para um único nó em desenvolvimento, mas os cookies e os tokens antiforgery deixam de funcionar após um reinício e entre nós em produção. Passe sempre o cliente S3 e o bucket numa implementação AWS de produção.

## SPA de início de sessão (npm) {#login-spa-npm}

A interface de início de sessão é publicada como pacote npm para personalização:

```bash
npm install @authagonal/login react react-dom react-router
```

O pacote inclui JS e CSS compilados; importe os componentes e os estilos diretamente na sua própria aplicação React. Consulte [Servidor personalizado](custom-server) para um guia completo.

`react`, `react-dom` e `react-router` são dependências **peer**: a compilação externaliza-as, pelo que os componentes utilizam as cópias da sua aplicação e não as suas próprias. É isso que permite que as páginas exportadas chamem `useNavigate` dentro do seu `<BrowserRouter>` e executem os seus hooks sobre a instância do React que as renderiza. Instale-as juntamente com o pacote; não deixe que ele traga as suas próprias.

## Backend-for-Frontend (BFF) {#backend-for-frontend-bff}

Se a sua SPA chamar APIs com um bearer token, aloje o token num BFF em vez de no browser. O BFF é publicado como pacote NuGet (`Authagonal.Bff`) e como pacote npm (`@authagonal/bff`); nenhum deles faz parte da imagem do servidor. Consulte [Backend-for-Frontend](bff).

## Lista de verificação de segurança para produção {#production-security-checklist}

Antes de expor a Authagonal a tráfego real, confirme o seguinte. Cada item é detalhado na página de [Configuração](configuration).

- **Execute num runtime .NET com as correções aplicadas: no mínimo 9.0.18 ou 10.0.10.** As correções para GHSA-37gx-xxp4-5rgx e GHSA-w3x6-4m5h-cxqf (um ciclo infinito e um par XXE / esgotamento de recursos em `System.Security.Cryptography.Xml`, ambos alcançáveis a partir do endpoint SAML ACS **anónimo**) são distribuídas no shared framework, e não em nenhum pacote que a Authagonal possa referenciar, pelo que nada no seu grafo de dependências as consegue garantir. A Authagonal regista `Critical` no arranque quando o runtime em execução está abaixo do mínimo; defina `Auth:RequireMinimumRuntime = true` para que, em vez disso, se recuse a arrancar. As imagens de contentor publicadas já utilizam um runtime igual ou superior ao mínimo.
- **Execute atrás de um proxy que termina o TLS e declare-o.** A Authagonal tem de estar atrás de um reverse proxy / ingress que termine o TLS (ou terminar ela própria o TLS). O HSTS só é emitido em HTTPS e `/connect/*` recusa texto simples, pelo que o proxy tem de reencaminhar `X-Forwarded-Proto: https`, e esse cabeçalho é ignorado a menos que defina `ForwardedHeaders:KnownNetworks` (ou `KnownProxies`) com o CIDR / endereço do seu proxy. Utilize `["0.0.0.0/0", "::/0"]` se o proxy não tiver um endereço fixo e nada mais conseguir chegar ao processo. `ForwardedHeaders:ForwardLimit` tem como predefinição `1` (confiar apenas no último salto).
- **Defina `SecretProvider:VaultUri`.** O fornecedor de segredos predefinido é de **texto simples**: sem o Key Vault, os segredos de cliente OIDC a montante e as sementes TOTP / MFA são guardados em claro no Table Storage (e nas cópias de segurança). Configure o Key Vault em qualquer implementação de produção.
- **Restrinja a API de administração.** `AdminApi:Enabled` tem como predefinição **true**. O âmbito de administração (`AdminApi:Scope`, predefinição `authagonal-admin`) concede gestão total e personificação de utilizadores. Restrinja ao nível da rede as rotas de administração `/api/v1/*` e controle rigorosamente a quem é emitido o âmbito de administração, ou defina `AdminApi:Enabled = false` se não for utilizada.
- **Proteja os endpoints internos.** Defina `Cluster:Secret` para que o endpoint interno `/_internal/backchannel-logout` exija o cabeçalho `X-Cluster-Secret` (comparado em tempo constante). Sem segredo, o endpoint não autoriza **ninguém** e responde 404: um endereço de origem não é uma credencial, e loopback é o que um reverse proxy no mesmo anfitrião apresenta em cada pedido que reencaminha. `Cluster:AllowLoopbackWithoutSecret` volta a admitir um par loopback antes do reencaminhamento, apenas para desenvolvimento local. Nada no produto distribuído chama o endpoint, pelo que falhar de forma fechada não quebra nenhum fluxo próprio; defina o segredo se construir a sua própria distribuição pod a pod sobre ele.
- **Encripte as cópias de segurança.** Com o fornecedor de segredos de texto simples, as cópias de segurança contêm segredos. A tabela `SigningKeys` é excluída das cópias de segurança por predefinição; se optar por incluí-la através de `Backup:IncludeSigningKeys`, o destino da cópia de segurança tem de estar encriptado em repouso. Consulte [Cópia de segurança e restauro](backup-restore).

## Ferramenta de migração {#migration-tool}

Para migrar a partir do Duende IdentityServer + SQL Server:

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

Consulte [Migração](migration) para mais detalhes.
