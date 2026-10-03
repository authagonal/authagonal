---
layout: default
title: 扩展
locale: zh-Hans
---

# 扩展

Authagonal 的设计使其无需任何特殊配置即可进行纵向和横向扩展。

## 设计上无状态 {#stateless-by-design}

所有持久状态都存储在后端存储中（Azure Table Storage、AWS 后端上的 DynamoDB，或自托管 SQL 后端上的 PostgreSQL）。不存在需要粘性会话或实例间协调的进程内状态：

- **签名密钥**：从 Table Storage 加载，每小时刷新
- **授权码和刷新令牌**：存储在 Table Storage 中，并强制一次性使用
- **SAML 重放防护**：请求 ID 记录在 Table Storage 中，使用原子删除
- **OIDC state 和 PKCE 验证器**：存储在 Table Storage 中
- **客户端和提供方配置**：每次请求时从 Table Storage 获取

## Cookie 加密（数据保护） {#cookie-encryption-data-protection}

ASP.NET Core 的数据保护密钥环负责保护认证 Cookie，因此所有实例都必须共享同一个密钥环。它会按以下顺序自动持久化：

1. `DataProtection:BlobUri`（如果已设置；一个显式指定的 blob，使用 `DefaultAzureCredential` 进行认证）。
2. `Storage:ConnectionString` 所指账户中的 `dataprotection` 容器，除非该账户是 Azurite。
3. 在托管标识路径上（`Storage:TableServiceUri`），使用同一账户中相邻的 blob 端点 `https://{account}.blob.…/dataprotection/keys.xml`。该标识需要在此账户上拥有 Storage Blob Data Contributor 角色。

只有无法识别的表端点（Azurite、路径风格的模拟器）才会回退到按机器存储的文件存储，它是临时的，并且每个 Pod 各自独立：重启会让所有人退出登录，各副本之间也无法读取彼此的 Cookie。发生这种情况时，启动检查会记录一条 `Critical` 日志。

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

在 AWS 后端上，向 `AddAuthagonalAwsStorage` 传入一个 S3 客户端和存储桶，即可将密钥环持久化到 S3；否则密钥环只存在于内存中，Cookie 在重启后以及跨节点时都会失效。参见[安装 → AWS 后端](installation#aws-backend)。在 SQL 后端上，密钥环由 `AddAuthagonalPostgres` / `AddAuthagonalSqlite` 持久化。

持久化并不等于加密：除非设置了 `DataProtection:KeyVaultKeyId` 或 `DataProtection:CertificateThumbprint`，否则密钥环是明文 XML。启动时，未加密且尚无密钥的密钥环会被拒绝；已经有密钥的密钥环则会在启动时记录一条 `Critical` 日志（`DataProtection:AllowUnencryptedKeyRing=true` 表示有意接受这种情况）。完整的 `DataProtection:*` 表格请参见[配置](configuration)。

## 每实例缓存 {#per-instance-caches}

少量读取频繁、变化缓慢的值会在每个实例的内存中缓存，以减少与 Table Storage 的往返：

| 数据 | 缓存时长 | 过期数据的影响 |
|---|---|---|
| OIDC 发现文档 | 60 分钟（可配置） | 延迟感知 IdP 的密钥轮换 |
| SAML IdP 元数据 | 60 分钟（可配置） | 同上 |
| CORS 允许的来源 | 60 分钟（可配置） | 新来源最多需要一小时才能生效 |

这些缓存可以用于生产环境。所有时长都可以通过 `Cache` 配置节进行配置，参见[配置](configuration)。如果需要立即生效，请重启受影响的实例。

## 速率限制 {#rate-limiting}

容易被滥用的端点（按 IP 限制的注册、按目标电子邮件限制的密码重置、按客户端限制的 SCIM、按 IP 限制的动态客户端注册，参见[配置 → 速率限制](configuration#rate-limiting)）受到内置速率限制器的保护。

默认情况下，限制在 `IRateLimiter` 接入点之后**在每个节点的进程内**执行，因此在 N 个实例的情况下，实际上限是所配置值的 N 倍。这是有意为之：限制器是防止单个节点遭受失控滥用的兜底手段，而权威的全局限制应该放在边缘（WAF / Ingress / CDN），因为边缘在负载均衡之前就能看到所有流量。

这种权衡对容量限制是正确的，但在一种情况下是错误的：保护**可猜测秘密**的预算。设备流程中的 `user_code` 是一个由小字母表组成的短字符串，而尝试次数限制是攻击者与一个能授予有效会话的代码之间唯一的屏障。在这种情况下，随副本数量成倍增长的上限并不合适，而且它会让真正的上限取决于你的 Ingress 配置，而不是服务器本身。

设置 **`Auth:DurableRateLimiting=true`** 可以把计数器移到你已经在运行的存储中，让所有副本共享同一份预算。代价是每次速率限制检查都需要一次存储往返；它使用固定窗口（预算为 N 时，跨越窗口边界最多可允许 2N 次）；存储不可达时会失效放行，因此它是叠加在边缘规则之上，而不是取而代之。三种后端都会自动回收计数器行。参见[配置 → 集群范围的限制](configuration#cluster-wide-limits-authdurableratelimiting)。

## 集群 {#clustering}

多个实例通过**领导者选举**和**跨节点事件总线**进行协调，两者都基于可插拔的后端：

- **领导者选举**：基于租约的选举（`Cluster:LeaseTtlSeconds`，默认 30 秒，大约每隔该值的一半续约一次）。恰好有一个节点持有租约；领导者宕机时，领导权会自动转移。需要领导者执行的工作只在领导者上运行：到期时*停用*签名密钥（当 `Auth:KeyRotationEnabled` 开启时）、授权协调清理（仅 Azure 后端）、静态数据回填（当 `Auth:AtRestBackfillEnabled` 开启时；非领导者会短暂等待领导权，然后跳过），以及速率限制计数器清理（启用了 `Auth:DurableRateLimiting` 的 Azure 后端）。当 `Cluster:Enabled=false` 时，单个节点就是永久的领导者，因此独立部署仍然会运行所有这些工作。
- **事件总线**：跨节点通知（例如多租户宿主中的缓存失效），每隔 `Cluster:PollIntervalSeconds`（默认 3 秒）轮询一次。

每个实例在启动时都会生成一个随机的 12 位十六进制节点 ID 来标识自己；该 ID 不会被持久化。

### 后端 {#backends}

**默认是进程内实现**：单个节点始终是自己的领导者，事件只在本地传递，对于单个实例而言无需任何配置即可正确运行。多节点部署通过 `AddAuthagonal` 上的 `configureClustering` 回调换入真正的后端：

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS: leadership + event bus via DynamoDB (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// PostgreSQL: leadership via a conditional-upsert lease row, event bus via an
// append-only log in the same database (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` 只注册事件总线，保留进程内（始终为领导者）的租约；对于必须接收集群事件、但绝不能竞争领导权的节点，请使用它们。

> **注意：**在多个节点上使用进程内默认实现时，*每个*节点都会认为自己是领导者。这对大多数工作负载无害，但在多个实例上开启 `Auth:KeyRotationEnabled` 之前，请先启用真正的租约后端。

签名密钥的**生成**与上述需要领导者执行的停用是分开的，也不由它驱动：每个节点在启动时以及每次 `Auth:SigningKeyCacheRefreshMinutes` 刷新时都会调用 `EnsureActiveKeyAsync`，因此在 `KeyRotationEnabled` 关闭（默认）时，90 天到期时的轮换完全由这条路径驱动。生成会获取自己的短期集群租约，因此只要配置了真正的租约后端，它就只有单一写入者。在多个节点上使用进程内默认实现时，不存在这种协调，两个同时遇到过期密钥的节点可能各自生成一个；两个密钥最终都会出现在 JWKS 中，由任一密钥签名的令牌都能通过验证，但被报告为活动状态的密钥可能来回变化。这是多节点部署应配置真正租约后端的又一个理由。

所有集群设置请参见[配置](configuration#cluster)页面。

### 多租户部署 {#multi-tenant-deployments}

在多租户模式（`AddAuthagonalCore()`）下，`TokenCleanupService`、`GrantReconciliationService`、`SigningKeyRotationService` 以及配置预置服务（客户端、提供方、作用域、角色）都不会被注册：它们属于单租户的 `AddAuthagonal()` 组合，这些工作由宿主按租户进行管理。

## 名称索引热分区 {#name-index-hot-partition}

管理端的名称前缀搜索由 `UserFirstNames` / `UserLastNames` 索引表支撑，这两张表使用**单个热分区**。在大规模场景下，这会把索引写入吞吐量限制在大约每秒 2,000 次操作，在高负载下可能成为用户创建/更新的瓶颈。如果你不对外提供管理端名称搜索，请设置 `Storage:NameIndexesEnabled = false`，完全跳过这些写入。参见[配置](configuration)。

## 受信任代理与内部端点 {#trusted-proxy-and-internal-endpoints}

在负载均衡器后面运行多个实例时：

- **转发请求头**：速率限制和账户锁定都以客户端 IP 为键，该 IP 从 `X-Forwarded-For` 解析而来。请将 `ForwardedHeaders:KnownNetworks` 设置为你的 Ingress / Pod CIDR，以免客户端 IP 在实例之间被伪造。`ForwardedHeaders:ForwardLimit` 默认为 `1`。参见[配置](configuration#forwarded-headers-trusted-proxy)。
- **内部端点**：`/_internal/backchannel-logout` 要求在 `X-Cluster-Secret` 请求头中提供 `Cluster:Secret`（以恒定时间比较）。没有它时，该端点不授权任何人并返回 404；来源 IP 不被视为凭据，因为同主机上的反向代理为每个转发请求呈现的都是回环地址，而在共享的集群网络中，私有地址段涵盖的是每一个相邻的工作负载。`Cluster:AllowLoopbackWithoutSecret` 是仅用于开发环境的选项，会重新接纳转发之前的回环对端。随附的产品从不调用此路由（会话的扇出通过 `SessionTermination` 在进程内完成），因此它只与你自己构建的扇出有关。

## 扩展建议 {#scaling-recommendations}

**纵向扩展**：增加单个实例的 CPU 和内存。适用于让每个实例处理更多并发请求。

**横向扩展**：在负载均衡器后面运行多个实例。不需要粘性会话或共享缓存。每个实例都完全独立。

**缩容至零**：Authagonal 支持缩容至零的部署（例如 `minReplicas: 0` 的 Azure Container Apps）。空闲之后的第一个请求会有几秒钟的冷启动，期间 .NET 运行时进行初始化，签名密钥从存储中加载。
