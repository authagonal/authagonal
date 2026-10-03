---
layout: default
title: 安装
locale: zh-Hans
---

# 安装

## Docker（推荐） {#docker-recommended}

拉取并运行预构建的镜像：

```bash
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  drawboardci/authagonal
```

## Docker Compose {#docker-compose}

用于配合 Azurite（Azure Storage 模拟器）进行本地开发：

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

> ⚠️ **`Auth:AllowInsecureHttp` 是开发用设置。**RFC 6749 §3.1/§3.2 要求授权端点和令牌端点使用 TLS，因此除非设置了此项，Authagonal 会拒绝发往 `/connect/*` 的非 https 请求。协议方案是在转发标头处理之后读取的，所以终止 TLS 并转发 `X-Forwarded-Proto: https` 的代理在该设置关闭时即可满足此要求；任何除你之外还有别人能访问的部署都应当这样做。开启该设置后，链路上的观察者能读到授权码、`Authorization: Basic` 标头中的客户端密钥，以及访问令牌和刷新令牌。参见[配置](configuration#authentication)。

## 从源码构建 {#building-from-source}

### 前置条件 {#prerequisites}

- .NET 10 SDK
- Node.js 24+

Authagonal 的目标框架为 `net9.0` 和 `net10.0`，运行时要求使用**已打补丁**的共享框架：**至少 9.0.18 或 10.0.10**。原因参见[生产环境安全检查清单](#production-security-checklist)；如需让启动检查在不满足时拒绝启动，参见 `Auth:RequireMinimumRuntime`。

### 构建 {#build}

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

### Docker 构建 {#docker-build}

```bash
# Server image (multi-stage: builds SPA + .NET in one image)
docker build -t authagonal .

# Migration tool
docker build -f Dockerfile.migration -t authagonal-migration .
```

## 作为库使用（NuGet） {#as-a-library-nuget}

在你自己的 ASP.NET Core 项目中引用 Authagonal 包：

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.AzureProvider" Version="x.y.z" />
```

存储提供程序包是可插拔的：`Authagonal.AzureProvider` 用于 Azure Table Storage（即默认的 `AddAuthagonal()` 接线方式），`Authagonal.SqlProvider` 用于自托管的 PostgreSQL 或 SQLite（参见 [SQL 后端](#sql-backend)），`Authagonal.AwsProvider` 用于 DynamoDB / S3 / Secrets Manager（参见 [AWS 后端](#aws-backend)）。

> **注册顺序很重要。**存储提供程序必须在 `AddAuthagonal()` **之前**注册。正是这个已存在的 `IUserStore` 注册让 `AddAuthagonal()` 跳过其内置的 Azure Table Storage 接线；如果提供程序在之后才注册，那么 `AddAuthagonal()` 已经填充的每一个接口都会被它悄无声息地错过，因为这些注册使用的是 `TryAdd`。
>
> 有三个接口（`IOrganizationStore`、`IOrganizationMembershipStore` 和 `IScimGroupRoleMappingStore`）带有空的、只读的内存兜底实现，这样即使宿主完全没有为它们接入存储，DI 也能解析。`AddAuthagonal()` 会在存储提供程序注册之前把这些兜底实现移开，之后再通过 `TryAdd` 恢复，因此提供程序的持久化存储总是优先，而兜底实现仍能覆盖没有此类存储的宿主。如果你为这三个接口中的任何一个注册了自己的实现，请像其他存储一样在 `AddAuthagonal()` 之前注册。

然后在 `Program.cs` 中组合：

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

所有可覆盖点参见[可扩展性](extensibility)，完整示例参见 [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server)。

### 邮件 {#email}

配置了 `Email:ResendApiKey` 和 `Email:SenderEmail` 后，内置的 [Resend](https://resend.com) 发送器会自动启用，无需注册服务。如果没有任何 `IEmailService`，验证邮件和密码重置邮件会被**静默丢弃**；又因为默认情况下登录要求邮箱已确认，自助注册的用户将永远无法登录（`UseAuthagonal` 会在启动时记录警告）。你可以设置 `Email:*` 键，或在 `AddAuthagonal()` 之前注册自己的 `IEmailService`，或在 `Auth:AutoConfirmEmailDomains` 中列出你的域名以跳过验证（仅限开发/测试）。参见[配置 → 邮件](configuration#email)。

## SQL 后端 {#sql-backend}

要在你自己的数据库上运行而不是使用云服务，请引用 `Authagonal.SqlProvider`，并在 `AddAuthagonal()` **之前**注册它，正是这些注册让 `AddAuthagonal()` 跳过 Azure Table Storage 接线：

```csharp
using Authagonal.SqlProvider;

// PostgreSQL: the production self-hosted backend
builder.Services.AddAuthagonalPostgres(
    "Host=db;Database=authagonal;Username=auth;Password=…;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/db-ca.pem");

// or SQLite: one file, no server. Suits embedded hosts, CI and small single-node deployments
builder.Services.AddAuthagonalSqlite("Data Source=authagonal.db");

builder.Services.AddAuthagonal(builder.Configuration);
```

各表与 Azure 和 DynamoDB 的布局一一对应，并在启动时如不存在则创建（每条语句都是 `IF NOT EXISTS`，因此多个 Pod 并发执行是安全的，对你自行预配的架构则不产生任何操作）。无需任何 `Storage:*` 配置。DataProtection 密钥环也持久化到同一数据库中，因此 Cookie 和防伪令牌在重启后依然有效，并可跨 Pod 使用，无需额外服务。

SQLite 会将写入者串行化，因此它是单节点后端：默认注册的进程内租约和集群事件总线正是与之匹配的组合。多 Pod 的 PostgreSQL 部署则应使用 `clustering.UseSql(dataSource)` 进行领导者选举。

> **排序规则。**在 PostgreSQL 上，键列被固定为 `COLLATE "C"`。键方案处处按字节序比较（前缀边界、环境分区范围、授权过期清理、键集分页），而使用语言排序规则创建的数据库（`en_US.UTF-8` 和 ICU 语言区域是常见默认值）会以不同方式对标点和大小写排序，从而静默返回错误的行。固定排序规则让表布局与数据库的创建方式无关；你无需以任何特定方式创建数据库。

> ⚠️ **密钥材料存放在该数据库中。**在 Azure 上，令牌签名密钥位于 Table Storage，DataProtection 密钥环位于 Blob 容器，两者各自拥有可独立授予的 RBAC；在 AWS 上则分别位于 DynamoDB 和 S3。在 SQL 上，两者都是表，与其他所有数据共用同一个连接字符串，因此应将连接字符串视同签名密钥：否则，一次 `pg_dump`、一个只读副本、一个拥有 `SELECT` 权限的分析角色或一份恢复的备份，就能同时获得为任意主体签发令牌的能力，以及所有身份验证 Cookie 背后的密钥。请在 `AddAuthagonalPostgres()` 之前注册一个 `IFieldCipher`，对 `SigningKeys.keyMaterialJson` 进行静态加密，并设置 `DataProtection:KeyVaultKeyId` 或 `DataProtection:CertificateThumbprint`，使密钥环不以裸露的 `<masterKey>` 形式存储：新部署如果在没有这两者的情况下持久化密钥环，会在启动时被拒绝；已有部署则会在每次启动时以 `Critical` 级别发出警告。这两项设置，以及如何将密钥环指向拥有独立角色的单独架构，参见[包 README](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider#dataprotection-keys)。

表布局、支撑每项一次性使用保证的并发原语，以及如何为其他数据库引擎添加方言，参见[包 README](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider)。

## AWS 后端 {#aws-backend}

要在 AWS 而非 Azure 上运行，请引用 `Authagonal.AwsProvider`，并在 `AddAuthagonal()` **之前**注册 AWS 组件包，正是这些注册让 `AddAuthagonal()` 跳过 Azure Table Storage 接线：

```csharp
using Authagonal.AwsProvider;

builder.Services.AddAuthagonalAwsStorage(
    dynamoDb,                // IAmazonDynamoDB: required
    secretsManager,          // IAmazonSecretsManager: optional; replaces the plaintext ISecretProvider
    s3,                      // IAmazonS3: optional; used for DataProtection keys
    "my-auth-keys-bucket");  // S3 bucket for the DataProtection key ring
builder.Services.AddAuthagonal(builder.Configuration);
```

DynamoDB 表与 Azure 布局一一对应，并在启动时确保存在（幂等；如果已由 Terraform 预配，则不执行任何操作）。凭据通过标准的 AWS 凭据链解析（环境变量 / EC2 实例角色 / IRSA），因此不存在连接字符串与托管标识之分，也无需任何 `Storage:*` 配置。

> ⚠️ **S3 DataProtection 密钥。**如果没有 S3 客户端 + 存储桶，ASP.NET Core Data Protection 密钥环会保存在内存中；这对开发环境中的单节点没有问题，但在生产环境中，Cookie 和防伪令牌会在重启后以及跨节点时失效。生产环境的 AWS 部署务必传入 S3 客户端和存储桶。

## 登录 SPA（npm） {#login-spa-npm}

登录界面以 npm 包的形式发布，便于定制：

```bash
npm install @authagonal/login react react-dom react-router
```

该包附带编译好的 JS 和 CSS，可以直接在你自己的 React 应用中导入组件和样式。完整演练参见[自定义服务器](custom-server)。

`react`、`react-dom` 和 `react-router` 是**对等**依赖：构建时将它们外部化，因此组件使用的是你应用中的副本，而不是自带的副本。正因如此，导出的页面才能在你的 `<BrowserRouter>` 内调用 `useNavigate`，并让它们的钩子运行在渲染它们的那个 React 实例上。请将它们与该包一起安装，不要让包自带一份。

## Backend-for-Frontend (BFF) {#backend-for-frontend-bff}

如果你的 SPA 使用持有者令牌调用 API，请把令牌放在 BFF 中，而不是浏览器里。BFF 以 NuGet 包（`Authagonal.Bff`）和 npm 包（`@authagonal/bff`）的形式发布；两者都不包含在服务器镜像中。参见 [Backend-for-Frontend](bff)。

## 生产环境安全检查清单 {#production-security-checklist}

在让 Authagonal 承接真实流量之前，请确认以下各项。每一项都在[配置](configuration)页面中有详细说明。

- **在已打补丁的 .NET 运行时上运行：至少 9.0.18 或 10.0.10。**GHSA-37gx-xxp4-5rgx 和 GHSA-w3x6-4m5h-cxqf（`System.Security.Cryptography.Xml` 中的一个无限循环漏洞，以及一对 XXE / 资源耗尽漏洞，两者都可从**匿名的** SAML ACS 端点触发）的修复随共享框架发布，而不在 Authagonal 能够引用的任何包中，因此你的依赖关系图中没有任何东西能保证这些修复存在。当运行时低于该下限时，Authagonal 会在启动时记录 `Critical` 日志；设置 `Auth:RequireMinimumRuntime = true` 可让它改为拒绝启动。已发布的容器镜像所用的运行时已达到或高于该下限。
- **在终止 TLS 的代理之后运行，并声明该代理。**Authagonal 必须位于终止 TLS 的反向代理 / 入口之后（或自行终止 TLS）。HSTS 只在 HTTPS 上发出，且 `/connect/*` 拒绝明文请求，因此代理必须转发 `X-Forwarded-Proto: https`；而除非你将 `ForwardedHeaders:KnownNetworks`（或 `KnownProxies`）设置为代理的 CIDR / 地址，否则该标头会被忽略。如果代理没有固定地址，并且除它之外没有任何东西能访问该进程，请使用 `["0.0.0.0/0", "::/0"]`。`ForwardedHeaders:ForwardLimit` 默认为 `1`（只信任最后一跳）。
- **设置 `SecretProvider:VaultUri`。**默认的密钥提供程序是**明文**的：没有 Key Vault 时，上游 OIDC 客户端密钥和 TOTP / MFA 种子会以明文形式存储在 Table Storage 中（以及备份中）。任何生产部署都应配置 Key Vault。
- **锁定管理 API。**`AdminApi:Enabled` 默认为 **true**。管理作用域（`AdminApi:Scope`，默认 `authagonal-admin`）授予完整的管理权限和用户模拟权限。请在网络层面限制 `/api/v1/*` 管理路由，并严格控制谁能获得管理作用域；如果不使用，请设置 `AdminApi:Enabled = false`。
- **保护内部端点。**设置 `Cluster:Secret`，使内部的 `/_internal/backchannel-logout` 端点要求提供 `X-Cluster-Secret` 标头（以恒定时间比较）。没有设置密钥时，该端点**不授权任何人**，并返回 404：源地址不是凭据，而同主机上的反向代理转发的每个请求呈现的都是回环地址。`Cluster:AllowLoopbackWithoutSecret` 会重新允许转发前的回环对端访问，仅限本地开发使用。随附的产品中没有任何组件调用该端点，因此默认拒绝不会破坏任何第一方流程；如果你基于它构建自己的 Pod 间扇出，请设置该密钥。
- **加密备份。**使用明文密钥提供程序时，备份中包含密钥。`SigningKeys` 表默认不纳入备份；如果你通过 `Backup:IncludeSigningKeys` 选择纳入，备份目标必须启用静态加密。参见[备份与恢复](backup-restore)。

## 迁移工具 {#migration-tool}

用于从 Duende IdentityServer + SQL Server 迁移：

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

详情参见[迁移](migration)。
