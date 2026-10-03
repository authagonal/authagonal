---
layout: default
title: 配置
locale: zh-Hans
---

# 配置

Authagonal 通过 `appsettings.json` 或环境变量进行配置。环境变量使用 `__` 作为配置节分隔符（例如 `Storage__ConnectionString`）。

## 必需设置 {#required-settings}

存储可以通过两种方式之一进行配置：提供 `Storage:ConnectionString` **或** `Storage:TableServiceUri`（托管标识路径，生产环境首选）**其中之一**。

| 设置 | 环境变量 | 说明 |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | 带账户密钥的 Azure Table Storage 连接字符串。适用于开发环境 / Azurite。 |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | 使用托管标识的 Table Storage 端点，例如 `https://{account}.table.core.windows.net/`。可替代 `Storage:ConnectionString`，且**在生产环境中首选**：它通过 `DefaultAzureCredential` 进行身份验证，因此任何访问密钥都不会出现在机密中。宿主必须为工作负载标识授予 **Storage Table Data Contributor** 角色。 |
| `Issuer` | `Issuer` | 本服务器的公开基础 URL（例如 `https://auth.example.com`） |

## 存储 {#storage}

| 设置 | 环境变量 | 默认值 | 说明 |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *（无）* | 带账户密钥的连接字符串（参见必需设置）。 |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *（无）* | 使用托管标识的 Table Storage URI（参见必需设置）。两者都设置时，它优先于 `Storage:ConnectionString`。 |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | 是否维护 `UserFirstNames` / `UserLastNames` 前缀搜索索引表，它们为管理端的姓名前缀搜索提供支持。在不提供管理端姓名搜索的宿主上设置为 `false`，即可跳过这些写入。**扩展说明：**这些索引使用单个热点分区，在大规模下吞吐量上限约为每秒 2,000 次操作，如果你不需要姓名搜索，请禁用它们。 |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | `/connect/authorize` 端点为登录 SPA（登录、升级验证和同意界面）重定向到的基础 URL。当登录界面与服务器不在同一来源时设置此项；默认为由内置 SPA 提供的相对路径 `/login`。 |

## 身份验证 {#authentication}

| 设置 | 默认值 | 说明 |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Cookie 会话生命周期（滑动过期） |
| `Authentication:AllowInsecureCookie` | `false` | 允许会话 Cookie 通过明文 http 发送（使用 `SameAsRequest` 而不是 `Always`）。**仅限开发环境。**Cookie 本身就是会话，而 `SameAsRequest` 只有在终止 TLS 的代理之后才看起来与之等效：它依赖于 `X-Forwarded-Proto` 能够到达并被信任，因此配置错误的入口、明文 HTTP 上的健康探测，或丢弃该请求头的代理，都会产生一个非 Secure 的 Cookie，该 Cookie 随后会附在发往同一主机的任何明文请求上。这种失败是悄无声息的。 |
| `Authentication:CookieDomain` | *（未设置）* | 将会话 Cookie 的作用范围设为父域，使其被发送到同级子域（`app.example.com` 以及 `auth.example.com`）。**这会牺牲来源绑定：**Cookie 将无法再携带 `__Host-` 前缀，而正是这个前缀让浏览器拒绝不满足 Secure、`Path=/` 且不带 `Domain` 条件的 Cookie，因此任何能在父域上设置 Cookie 的子域，以及任何能接管其中某个子域的人，都会落入影响范围。除非某个同级来源确实需要该会话，否则请不要设置。 |
| `Auth:AllowInsecureHttp` | `false` | 允许 OAuth 端点（`/connect/*`）响应明文 http 请求。**仅限开发环境。**RFC 6749 §3.1/§3.2 要求授权端点和令牌端点使用 TLS，因此默认情况下，对其中任何一个发出的非 https 请求都会以 `invalid_request` 拒绝。协议方案在转发请求头处理*之后*才进行判定，因此终止 TLS 并转发 `X-Forwarded-Proto: https` 的代理在此项关闭时也能通过检查，前提是该代理已在 [`ForwardedHeaders:KnownNetworks` / `KnownProxies`](#the-two-headers-are-not-trusted-on-the-same-terms) 中声明，否则该请求头会被忽略。只有真正使用明文的部署（随附的 `docker-compose.yml`、自定义服务器演示）才需要它，并且只要开启，服务器就会在启动时记录一条警告。它会传递到 `AuthagonalProtocolOptions.AllowInsecureHttp`，因此也控制由 `Authagonal.Protocol` 提供的端点（参见[可扩展性](extensibility#embedding-authagonalprotocol-alone)）。 |
| `Auth:RequireMinimumRuntime` | `false` | 当 .NET 共享框架早于 Authagonal 要求的安全基线（**9.0.18 / 10.0.10**）时拒绝启动。之所以存在这条基线，是因为 GHSA-37gx-xxp4-5rgx 和 GHSA-w3x6-4m5h-cxqf 的修复（`System.Security.Cryptography.Xml` 中的一个无限循环，以及一对 XXE / 资源耗尽问题，两者都可以从**匿名**的 SAML ACS 端点触发）随运行时发布，而不在本库可以固定版本的包中，因此你的任何依赖都无法保证包含它们。保持 `false` 时，旧的运行时会产生一条 `Critical` 日志，服务器照常启动：如果默认拒绝启动，那么对于运行时只落后一个补丁的服务器群，升级 Authagonal 版本就会变成一次服务中断。如果在未打补丁的运行时上不启动比提供未经身份验证的 XML 处理更可取，请将其设置为 `true`。 |
| `Auth:MaxFailedAttempts` | `5` | 账户被锁定前允许的失败登录次数 |
| `Auth:LockoutDurationMinutes` | `10` | 达到最大失败次数后的账户锁定时长 |
| `Auth:MaxLoginAttemptsPerIp` | `30` | 每个来源地址在每个 `Auth:LoginWindowMinutes` 内允许的密码尝试次数，并且（另行计算）同一时间窗口内每个所提交邮箱允许的次数。按账户锁定无法限制撒网式攻击（对数千个账户各尝试一次），而每次未经身份验证的尝试都要完整执行一次 PBKDF2，因此这项设置对两者都加以限制。超出时返回 `429 too_many_attempts`（`AuthEndpoints.cs:107-119`）。 |
| `Auth:LoginWindowMinutes` | `5` | `Auth:MaxLoginAttemptsPerIp` 的时间窗口 |
| `Auth:MaxRegistrationsPerIp` | `5` | 时间窗口内每个 IP 地址的最大注册次数 |
| `Auth:RegistrationWindowMinutes` | `60` | 注册限流的时间窗口 |
| `Auth:MaxPasswordResetsPerEmail` | `3` | 时间窗口内每个目标地址的最大密码重置邮件数（以邮箱而不是调用方 IP 为键，因此单个地址不会被邮件轰炸） |
| `Auth:MaxPasswordResetsPerIp` | `15` | 时间窗口内每个来源 IP 的最大忘记密码请求数。按邮箱的上限约束的是发给单个受害者的邮件；这项设置约束的是逐个遍历地址列表的调用方，否则这将是从你已验证的发信域名发出的、不受限制的匿名邮件，外加每个地址一次存储读取。 |
| `Auth:PasswordResetWindowMinutes` | `60` | 密码重置限流的时间窗口 |
| `Auth:DurableRateLimiting` | `false` | 将限流计数器保存在已配置的存储中，使所有副本共享同一个额度，而不是每个节点各自计数。每次检查都要付出一次存储往返；单节点部署不会因此获益。需要一个提供 `IRateLimitCounterStore` 的提供程序（Azure、SQL、AWS）。否则宿主会拒绝启动，而不是悄悄退回按节点限流。参见[集群范围的限流](#cluster-wide-limits-authdurableratelimiting)。 |
| `Auth:AutoConfirmEmailDomains` | *（空）* | 一组邮箱域名（字符串数组），这些域名的自助注册会被自动确认，跳过验证邮件。为空（默认）表示每次注册都必须验证。仅供开发/测试使用；绝不要列出能接收真实邮件的域名。 |
| `Auth:AllowPasswordlessAccountClaim` | `false` | 用属于某个**没有本地凭据**的现有账户（联合登录或 JIT 预配）的邮箱注册时，会在该账户上暂存一个密码，而不是返回不暴露枚举信息的重复响应。暂存的凭据和任何属性在认领者点击一封新的验证邮件之前都不会生效，因此仅知道某个联合账户的邮箱不足以接管它。已经有密码的账户永远不受影响。参见[用户升级](user-upgrade)。 |
| `Auth:ClaimAllowedAttributeKeys` | *（空）* | 无密码认领可以从注册请求带到被认领账户上的自定义属性键。为空则允许所有键（向后兼容）；列出键以限制认领能注入下游预配和令牌中的内容。 |
| `Auth:EmailVerificationExpiryHours` | `24` | 邮箱验证链接的有效期 |
| `Auth:PasswordResetExpiryMinutes` | `60` | 密码重置链接的有效期 |
| `Auth:MfaChallengeExpiryMinutes` | `5` | MFA 质询令牌的有效期 |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | MFA 设置令牌的有效期（用于强制注册） |
| `Auth:WebAuthnAllowedHosts` | *（空）* | 允许作为 WebAuthn 依赖方的主机。为空则接受任何主机（现有部署可继续工作），这是一个缺口：否则 RP ID 和预期来源会从正在被验证的请求中推导出来。在多租户部署中，请列出每一个租户主机。参见 [MFA](mfa)。 |
| `Auth:Pbkdf2Iterations` | `100000` | 密码哈希使用的 PBKDF2 迭代次数 |
| `Auth:FailedLoginMinimumMilliseconds` | `250` | 失败登录在返回 `invalid_credentials` 之前被保持的最短实际耗时，从请求开始时计算。它消除了用户枚举的计时预言机：不存在的账户会用原生 PBKDF2 格式的虚拟哈希进行验证，但真实账户可能仍持有以不同成本导入的 bcrypt、Scrypt.NET 或 ASP.NET Identity V3 哈希，因此不可能做到工作量相等，所强制的是耗时相等。请将其提高到部署中最慢的哈希之上，例如你导入了成本高于 11 的 bcrypt、`N` 较高的 Scrypt.NET `$s2$` 哈希，或将 `Pbkdf2Iterations` 提高到远超默认值。失败登录第一次超出此值时会记录一条警告。`0` 会禁用填充，重新打开该预言机。 |
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | 针对刷新令牌并发重用的可选宽限窗口（秒）。`0`（默认）保持严格的姿态：对已被消耗的刷新令牌的任何重用，都会撤销该用户+客户端的所有令牌。设置为 `> 0` 时，窗口内的重用会被视为幂等重试（重新下发后继令牌），这对连接不稳定的移动客户端很有用。 |
| `Auth:DynamicClientRegistrationEnabled` | `false` | 启用 `POST /connect/register` 动态客户端注册端点（RFC 7591）。默认关闭，因为开放注册在多租户部署中可能被滥用。参见[动态客户端注册](client-registration)。 |
| `Auth:DynamicClientRegistrationScopes` | *（空）* | 匿名注册者可以为自己分配的作用域，在始终可注册的 OIDC 内置作用域（`openid`、`profile`、`email`、`phone`、`offline_access`）之外额外允许。为空表示只有内置作用域：某个作用域存在于存储中，并不意味着自助注册的客户端可以声明它。受角色限制的作用域无论如何都不可注册。参见[动态客户端注册](client-registration)。 |
| `Auth:SigningKeyLifetimeDays` | `90` | 签名密钥在自动轮换前的生命周期（密钥为 ES256 / P-256） |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | 从存储重新加载签名密钥的频率 |
| `Auth:KeyRotationEnabled` | `false` | 启用签名密钥自动轮换 |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | 检查活动密钥是否需要轮换的频率 |
| `Auth:KeyRotationLeadTimeDays` | `14` | 当活动密钥在这么多天内即将过期时进行轮换 |
| `Auth:SecurityStampRevalidationMinutes` | `30` | 两次 Cookie 安全戳检查之间的间隔 |
| `Auth:AllowedInternalTargets` | *（空）* | 在由**你**提供 URL 的路径上，Authagonal 可以从中获取内容的内部目标：上游 SAML 元数据、上游 OIDC 发现文档、预配回调。为空表示拒绝所有内部地址。参见[出站请求](#outbound-fetches-ssrf-guard)。 |
| `Auth:AllowOutboundProxy` | `false` | 让这些由运维人员配置的请求经由环境中的 HTTP 代理发送，并接受地址检查无法穿透代理这一事实。永远不适用于客户端注册的 `jwks_uri` 或后端通道注销 URI。参见[出站请求](#outbound-fetches-ssrf-guard)。 |
| `Auth:AtRestBackfillEnabled` | `false` | 在启动时于集群领导者上运行一次静态数据回填。它会把每个现有的用户行及其由个人资料派生的索引行重写为当前的静态存储方案，这是在已有数据的部署上启用 `IFieldCipher` / `IIndexTokenizer` 的迁移路径（参见[可扩展性](extensibility#pii-field-encryption-ifieldcipher)）。只注册加密器只会加密之后写入的行。它会产生实际的写入量，是幂等的，并且每个进程运行一次，因此在日志报告完整运行一次后请将其关闭。 |
| `Auth:MaxScimGroupsPerClient` | `5000` | 一个预配客户端最多可拥有的 SCIM 组数量；超过后创建会被拒绝。组存储没有索引，因此一个不受限制的表会让每次令牌签发都为它付出代价。 |
| `Auth:MaxScimGroupMembers` | `10000` | 一个 SCIM 组最多可携带的成员数量；超过后创建、替换和修补都会被拒绝。 |

## 数据保护 {#data-protection}

ASP.NET Core 数据保护密钥（用于加密会话 Cookie）必须在各实例之间共享，参见[扩展](scaling#cookie-encryption-data-protection)。持久化选项按优先级排列：

| 设置 | 默认值 | 说明 |
|---|---|---|
| `DataProtection:BlobUri` | *（无）* | 密钥环的显式 Azure Blob URI（例如 `https://{account}.blob.core.windows.net/dataprotection/keys.xml`）。通过 `DefaultAzureCredential` 进行身份验证，是与 `Storage:TableServiceUri` 搭配的首选生产路径。 |
| *（兜底）* | *（无）* | 未设置 `DataProtection:BlobUri` 时，密钥环会被自动持久化：保存到 `Storage:ConnectionString` 所指账户中的 `dataprotection` 容器（除非那是 Azurite），或者在托管标识路径上，保存到由 `Storage:TableServiceUri` 推导出的 Blob 端点（`https://{account}.table.…` → `https://{account}.blob.…/dataprotection/keys.xml`），这需要在同一账户上拥有 Storage Blob Data Contributor 角色。只有无法识别的表端点（Azurite、路径风格的模拟器）才会退回到按机器的文件存储，它是临时的且按 Pod 隔离；发生这种情况时，`KeyRingStartupCheck` 会记录 Critical 日志。 |

在 AWS 后端上，向 `AddAuthagonalAwsStorage` 传入 S3 客户端和存储桶即可将密钥环持久化到 S3，参见[安装 → AWS 后端](installation#aws-backend)。在 SQL 后端上，密钥环由 `AddAuthagonalPostgres` / `AddAuthagonalSqlite` 持久化，参见[安装 → SQL 后端](installation#sql-backend)。

持久化不等于加密。无论由哪个后端保存密钥环，除非设置了以下其中一项，否则它都会以明文 XML（包括主密钥）写入。该密钥环保护着身份验证 Cookie，因此对存储的读取权限就等于能够为任何用户伪造会话：

| 设置 | 默认值 | 说明 |
|---|---|---|
| `DataProtection:KeyVaultKeyId` | *（无）* | 用于包装密钥环的 Azure Key Vault 密钥 URI。通过 `DefaultAzureCredential` 进行身份验证。 |
| `DataProtection:CertificateThumbprint` | *（无）* | 机器存储中用于包装密钥环的证书指纹。 |
| `DataProtection:AllowUnencryptedKeyRing` | `false` | 有意接受明文密钥环。每次启动时都会以 `Critical` 级别重新声明，使其出现在审计中，而不只是出现在配置文件里。 |

启动时依据*解析后*的密钥环选项强制执行这一点，因此它同样适用于 Azure、AWS、SQL 以及任何由宿主注册的存储库。以不加密的方式持久化密钥环且**尚无密钥**的部署会被拒绝启动，因此这种不安全的状态永远不会被创建；而密钥环**已经有密钥**的部署会启动并以 `Critical` 级别记录日志，因为在那里拒绝启动会让一个正在运行的部署因版本升级而宕机。开发环境永远不会拒绝。

## 缓存与超时 {#cache-and-timeouts}

| 设置 | 默认值 | 说明 |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | CORS 允许来源的缓存时长 |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | OIDC 发现文档的缓存时长 |
| `Cache:SamlMetadataCacheMinutes` | `60` | SAML IdP 元数据的缓存时长 |
| `Cache:OidcStateLifetimeMinutes` | `10` | OIDC 授权 state 参数的生命周期 |
| `Cache:SamlReplayLifetimeMinutes` | `10` | SAML AuthnRequest ID 的生命周期（防止重放） |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Table Storage 健康检查超时 |
| `Cache:HealthCheckCacheSeconds` | `5` | `/health` 的应答在再次查询存储之前被复用的时长（与该端点声明的 `Cache-Control: max-age` 一致）。`0` 表示每个请求都进行探测，这会重新打开该缓存所消除的匿名放大问题。 |

## 后台服务 {#background-services}

| 设置 | 默认值 | 说明 |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | 首次清理过期令牌之前的初始延迟 |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | 过期令牌清理间隔 |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | 首次授权对账之前的初始延迟 |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | 授权对账间隔 |

### 过期清扫（Azure Table） {#expiry-sweeps-azure-table}

Azure Table Storage 没有 TTL，因此在 Azure 后端上，服务器会针对 `MfaChallenges`、`RevokedTokens` 和 `UpstreamRefreshTokens` 每个表各运行一个 `TableExpirySweepService`（每 15 分钟一次，仅在集群领导者上），删除已过期的行。它只负责保留期管理：这些行在读取时本就会被各自的过期检查拒绝。没有声明过期时间的行（在 `UpstreamRefreshTokens` 上可能出现）有意永远不会被清扫。DynamoDB 和 SQL 会以原生方式回收这三个表。无需任何配置。

## 机器人防护（Cloudflare Turnstile） {#bot-protection-cloudflare-turnstile}

需要显式启用。设置了密钥后，登录、注册、忘记密码和重置密码在执行任何操作之前，都会先向 Cloudflare 验证 `turnstileToken`；没有密钥时，一切不变，也不会渲染小组件。

| 设置 | 默认值 | 说明 |
|---|---|---|
| `Turnstile:SiteKey` | *（未设置）* | 公开的站点密钥，提供给登录界面（`GET /api/auth/providers` 上的 `turnstileSiteKey`），以便其渲染小组件 |
| `Turnstile:SecretKey` | *（未设置）* | 用于服务器端验证的密钥。未设置或为空时完全禁用 Turnstile |

服务客户自有域名的宿主无法只使用一个密钥对（Cloudflare 会限制一个小组件可用的主机名数量）；它应替换 [`ITurnstileKeyProvider`](extensibility#iturnstilekeyprovider)。关于 `captcha_failed` 错误，参见[身份验证 API](auth-api#providers)。

## 角色 {#roles}

角色在 `Roles` 数组中定义，并在启动时与客户端、作用域和提供方一起预置。
当某个作用域通过 [`AllowedRoles`](scopes#role-gated-scopes) 进行限制时，预置角色最为重要：
一个限定于从未被创建的角色的作用域，对所有人都是关闭的，包括配置它的运维人员，
而且它会悄无声息地失败：该作用域只是永远不会被授予。

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

| 字段 | 说明 |
|---|---|
| `Name` | 角色名称，与 `Scope.AllowedRoles` 中以及 `roles` 令牌声明上所用的名称相同 |
| `Description` | 供人阅读；当预置配置给出描述时，会在之后的启动中更新 |
| `Members` | 每次启动时被放入该角色的邮箱。尚无对应用户的地址会被跳过并记录警告，在下次启动时重试，因此启动永远不依赖于某个尚未有人创建的账户 |

预置是**增量且幂等的**。它从不删除角色，也从不撤销成员资格：配置并不是“谁拥有什么”的权威记录，因此通过管理 API 授予的角色在下次重启后依然存在。

## 客户端 {#clients}

客户端在 `Clients` 数组中定义，并在启动时预置。每个客户端可以包含：

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

预置采用**读取-合并-写入**的方式：预置配置未给出的字段会保留已存储的值，因此重启永远不会撤销通过管理 API 所做的更改（已禁用的客户端保持禁用，已轮换的密钥依然保留，`Audiences` 和客户端 JWKS 被保留）。预置配置给出的字段则会在每次启动时被覆盖。

关于各字段的说明（来自 `ClientSeedService.ClientSeedConfig`）：

- **别名。**`ClientId`/`Id`、`ClientName`/`Name`、`AllowedGrantTypes`/`GrantTypes`、`AllowedScopes`/`Scopes`、`AllowedCorsOrigins`/`CorsOrigins` 以及 `RequireClientSecret`/`RequireSecret` 可以互换使用。单个 `SeedClient` 对象也会被读作额外的一个条目。
- **密钥。**提供 `SecretHashes`（已哈希）或 `ClientSecret`（明文，在启动时哈希，仅在未提供哈希时使用）其中之一。只有当预置配置提供了密钥时才会应用它，因此通过管理 API 轮换的密钥在下次重启后依然保留。预置配置的结构中没有 `ClientSecretHashes` 键。
- **`BackChannelLogoutUri`**：后端通道注销令牌被 POST 到的地址；参见[后端通道注销](#back-channel-logout)。
- **`RestrictedToOrganizationIds`**：该客户端可以搭配使用的组织 ID。为空表示不受限制；只含一个条目时，还会为未指定任何组织的请求选中该组织（参见[组织](organizations)）。
- **`InitiateLoginUri`、`ClientUri`、`IsDefaultApplication`**：为 `/api/auth/apps` 列表以及登录界面上的“继续前往应用”按钮提供数据。
- **不可预置。**`RequireConsent`、`ProvisioningApps`、`RequirePushedAuthorizationRequests`、客户端 JWKS 以及前端通道注销相关字段在预置配置的结构中没有对应的键，因此无法通过配置设置。未知的键会被忽略，且不会产生警告。
- 如果预置配置的作用域或受众违反了保留作用域规则或受众规则，它会被拒绝，记录错误日志并被跳过。

### 受众与资源指示符（RFC 8707） {#audiences-and-resource-indicators-rfc-8707}

`Audiences` 是客户端针对 `resource` 参数（RFC 8707）以及令牌交换的 `audience` 参数（RFC 8693）的允许列表。通过该检查的值会成为所签发访问令牌的 `aud` 声明；请求中没有 `resource` 时，`aud` 退回到 `Audiences`，两者都没有时则为 `client_id`。

对于任何确实回答过这个问题的客户端，空的 `Audiences` 列表表示**“无”**：即其创建请求携带了 `audiences` 字段的客户端，无论是通过动态注册（该字段在那里是 Authagonal 对 RFC 7591 的扩展）、管理 API 还是预置配置。这样的客户端根本不能指定任何 `resource`，在任何路径上都是如此：授权、`client_credentials` 和令牌交换的行为一致。

**省略了** `audiences` 的动态注册（所有标准的 RFC 7591 客户端，也就是所有 MCP 客户端）从未被问过这个问题。它的列表处于“未设置”状态，可以将任何绝对 URI 指定为 `resource`；MCP 授权规范依赖于此。同样的解读也适用于在 `AudiencesDeclared` 出现之前存储的客户端，因为在升级时收紧每一个已存储的客户端会破坏如今能正常工作的流程。

| 客户端 | 空的 `Audiences` 表示 |
|---|---|
| 创建请求携带了 `audiences`（DCR 扩展字段、管理 API、预置配置） | **拒绝**：不能指定任何 `resource` |
| 省略了 `audiences` 的 DCR 注册 | **“未设置”**：接受任何绝对 URI 作为 `resource` |
| 在 `AudiencesDeclared` 出现之前存储 | **“未设置”**：接受任何绝对 URI 作为 `resource` |

**改造旧客户端**的方法是向管理端客户端 API 发送 `PUT`，带上 `audiencesDeclared: true`（以及它应当被固定到的 `audiences`）。该标志只会收紧：更新可以设置它，但不能清除它，因此一次无关的编辑永远不会悄悄地让客户端回到宽松的解读。

对于旧行的后果，值得直白地说明，而不是将其掩盖：

> 一个没有配置 `Audiences` 的既有客户端，可以在授权端点或在 `client_credentials` 下将**任何**绝对 URI 指定为 `resource`，并获得一个 `aud` 为该值、由本租户密钥签名、携带发起请求的用户的 `sub` 以及该客户端被允许的任何作用域的访问令牌。

已声明的 `audiences` 列表会在写入时进行校验：最多 20 个条目，每个最多 512 个字符，且每个都必须是带有显式协议方案、不含片段的绝对 URI。`resource` 值也遵循同样的形式；注意，像 `/admin` 这样的单纯路径**不会**被接受，尽管 .NET 的 `Uri` 解析器在 Linux 上会把它当作一个绝对的 `file:` URI。

指定某个资源并不等于能访问它。但这确实意味着，授权服务器不能成为横在客户端与一个它本不该调用的 API 之间的唯一屏障，因此：

- **资源服务器必须依据 `scope`**（或其自身的模型）进行授权，而不能仅依据 `iss` + `aud` + `sub`。一个在 `aud` 中指定了你的 API 的令牌，证明的是客户端请求了你的 API。它并不能证明该客户端被允许调用它，而本服务器也无法让它证明这一点。
- **资源服务器必须依据自身的标识符验证 `aud`**，而不只是验证“存在某个值”。
- **在每个应当被固定到一组固定 API 的客户端上设置 `Audiences`。**配置之后，未列出的 `resource` 会在授权端点和 `client_credentials` 上以 `invalid_target` 拒绝。这是唯一能够强制执行该限制的地方。
- **为在该标志出现之前创建的客户端补上 `audiencesDeclared: true`**，使其空的受众列表表示“无”，而不是“任意”。
- **自助注册的客户端可以在注册时声明 `audiences`**，并受其所声明内容的约束，包括空列表。`Auth:DynamicClientRegistrationEnabled` 默认仍是关闭的；参见[动态客户端注册](client-registration)。

### 授权类型 {#grant-types}

| 授权类型 | 使用场景 |
|---|---|
| `authorization_code` | 交互式用户登录（Web 应用、SPA、移动应用） |
| `client_credentials` | 服务之间的通信 |
| `refresh_token` | 令牌续期（需要 `AllowOfflineAccess: true`） |
| `urn:ietf:params:oauth:grant-type:device_code` | 面向输入受限设备的设备授权（RFC 8628） |

### 刷新令牌用法 {#refresh-token-usage}

| 值 | 行为 |
|---|---|
| `OneTime`（默认） | 每次刷新都签发一个新的刷新令牌并使旧令牌失效。默认情况下（`Auth:RefreshTokenReuseGraceSeconds = 0`），对已被消耗的令牌的任何重用都会立即撤销该用户+客户端的所有令牌，默认**没有**宽限窗口。将 `Auth:RefreshTokenReuseGraceSeconds` 设置为正值，即可启用重试容忍窗口。 |
| `ReUse` | 在过期之前重复使用同一个刷新令牌。 |

### 预配应用 {#provisioning-apps}

客户端的 `ProvisioningApps` 数组（在授权时读取，`AuthorizeEndpoint.cs:578`；配置预置程序不绑定它，管理 API 的客户端路由也不携带它，因此它由宿主在已存储的客户端记录上设置）引用在 `ProvisioningApps` 配置节中定义的应用 ID。当用户通过该客户端进行授权时，会经由 TCC 被预配到这些应用中。详情参见[预配](provisioning)。

## 作用域 {#scopes}

自定义的 [OAuth 作用域](scopes)可以从 `Scopes` 数组预置。每个条目在启动时按 `Name` 进行 upsert（没有 `Name` 的条目会被跳过并记录警告）：

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

你设置的字段在每次启动时都会覆盖已存储的值；你省略的字段则保留已存储的值。因此，配置可以添加或更改 `UserClaims` 和 `AllowedRoles`，但不能将其清空（如需清空，请使用 `PUT /api/v1/scopes/{name}`）。各字段的含义参见[作用域模型](scopes#scope-model)。

## 预配应用配置 {#provisioning-apps-1}

定义用户应当被预配到的下游应用：

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

完整的 TCC 协议规范参见[预配](provisioning)。

## MFA 策略 {#mfa-policy}

多因素身份验证通过 `MfaPolicy` 属性按客户端强制执行：

| 值 | 行为 |
|---|---|
| `Disabled`（默认） | 不进行 MFA 质询，即使用户已注册 MFA |
| `Enabled` | 质询已注册 MFA 的用户；不强制注册 |
| `Required` | 质询已注册的用户；强制未注册 MFA 的用户进行注册 |

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

当 `MfaPolicy` 为 `Required` 且用户尚未注册 MFA 时，登录会返回 `{ mfaSetupRequired: true, setupToken: "..." }`。设置令牌（通过 `X-MFA-Setup-Token` 请求头）向 MFA 设置端点证明用户身份，使其可以在获得 Cookie 会话之前完成注册。

联合登录（SAML/OIDC）同样遵循 MFA 策略：已注册 MFA 的用户在外部 IdP 完成身份验证之后，会被引导进行 MFA 质询，而 `Required` 会强制未注册 MFA 的联合用户进行注册。

### 通过 IAuthHook 覆盖 {#iauthhook-override}

`IAuthHook.ResolveMfaPolicyAsync` 方法可以按用户覆盖客户端策略：

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

## 密码策略 {#password-policy}

自定义密码强度要求：

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

| 属性 | 默认值 | 说明 |
|---|---|---|
| `MinLength` | `8` | 密码最小长度 |
| `MinUniqueChars` | `2` | 不同字符的最少数量 |
| `RequireUppercase` | `true` | 至少包含一个大写字母 |
| `RequireLowercase` | `true` | 至少包含一个小写字母 |
| `RequireDigit` | `true` | 至少包含一个数字 |
| `RequireSpecialChar` | `true` | 至少包含一个非字母数字字符 |

该策略在重置密码和管理端注册用户时强制执行。登录界面从 `GET /api/auth/password-policy` 获取当前生效的策略，以动态显示密码要求。

## SAML 提供方 {#saml-providers}

在配置中定义 SAML 身份提供方。它们会在启动时预置：

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

| 属性 | 必填 | 说明 |
|---|---|---|
| `ConnectionId` | 是 | 稳定的标识符（用于 `/saml/{connectionId}/login` 这样的 URL） |
| `ConnectionName` | 否 | 显示名称（默认为 ConnectionId） |
| `EntityId` | 是 | **本服务器**的 SP 实体 ID，即你在 IdP 上注册的标识符，而不是 IdP 自身的实体 ID |
| `MetadataLocation` | 是 | IdP 的 SAML 元数据 XML 的 URL。必须是 https，并且必须可公开路由，除非该主机已列在 [`Auth:AllowedInternalTargets`](#outbound-fetches-ssrf-guard) 中：这份文档携带着用于验证每一个断言的证书。如果你的 IdP 不发布 https 元数据端点，请改为通过[管理 API](admin-api) 设置 `metadataXml`；预置配置中没有对应的键。 |
| `AllowedDomains` | 否 | 通过 SSO 路由到该提供方的邮箱域名 |
| `OrganizationId` | 否 | 将该连接限定于某一个[组织](organizations)。为 null（默认）时它是租户级连接；只有租户级连接才会把其 `AllowedDomains` 注册为 SSO 域名路由 |
| `JitProvisioningEnabled` | 否 | 在首次登录时创建用户。默认 `false` |
| `AllowUninvitedJit` | 否 | 允许 JIT 将用户创建到其未被邀请加入的组织中。默认 `false` |
| `ChallengeMfaAfterLogin` | 否 | 在 IdP 登录之后，按应用的 MFA 策略进行质询。默认 `true` |
| `ProvisioningAttributeParams` | 否 | 传递给下游预配的断言属性 |
| `AllowUnsolicitedResponses` | 否 | 在该连接上接受由 IdP 发起的（未经请求的）响应。默认 `false` |

这些布尔值在每次启动时都会从预置配置写入，包括默认值，因此运维人员通过管理 API 修改过的预置连接，会在下次重启时恢复这些值。预置配置中没有对应键的字段（`SpCertificate`、`SignAuthnRequests`、`NameIdFormat`、`MetadataXml`、`IconUrl`）会被保留。

## OIDC 提供方 {#oidc-providers}

在配置中定义 OIDC 身份提供方。它们会在启动时预置：

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

| 属性 | 必填 | 说明 |
|---|---|---|
| `ConnectionId` | 是 | 稳定的标识符（用于 `/oidc/{connectionId}/login` 这样的 URL） |
| `ConnectionName` | 否 | 显示名称（默认为 ConnectionId） |
| `MetadataLocation` | 是 | IdP 的 OpenID Connect 发现文档的 URL |
| `ClientId` | 是 | 在 IdP 上注册的 OAuth2 客户端 ID |
| `ClientSecret` | 是 | OAuth2 客户端密钥（在启动时通过 `ISecretProvider` 保护） |
| `RedirectUrl` | 否 | **被忽略。**重定向 URI 按请求推导为 `{Issuer}/oidc/callback`：请在 IdP 上注册*这个*地址。此处的值不起作用，并会被记录为已忽略。 |
| `AllowedDomains` | 否 | 通过 SSO 路由到该提供方的邮箱域名 |
| `OrganizationId` | 否 | 将该连接限定于某一个[组织](organizations)；null 表示租户级 |
| `JitProvisioningEnabled` | 否 | 在首次登录时创建用户。默认 `false` |
| `AllowUninvitedJit` | 否 | 允许 JIT 将用户创建到其未被邀请加入的组织中。默认 `false` |
| `UseUpstreamSubjectAsUserId` | 否 | 使用上游的 `sub` 作为本地用户 ID。默认 `false` |
| `ShowOnLogin` | 否 | 在登录界面上为该连接显示一个按钮。默认 `true`；按域名路由的连接无论如何都通过先输入邮箱的方式进入 |
| `ChallengeMfaAfterLogin` | 否 | 在 IdP 登录之后，按应用的 MFA 策略进行质询。默认 `true` |
| `AutoLinkExistingByEmail` | 否 | 将首次登录关联到具有相同邮箱的现有本地账户。默认 `false` |
| `PassthroughParams`, `ProvisioningAttributeParams` | 否 | 透传给 IdP / 继续传递给下游预配的参数 |
| `RevalidateOnRefresh` | 否 | 在兑换刷新令牌时重新检查上游会话。默认 `false` |
| `IsExternalConnection`, `SessionExpClaim` | 否 | 联合会话设置；参见[联合会话](federated-sessions) |
| `InteractionPath` | 否 | 在未经身份验证的 `idp_hint` 请求通过该连接进行联合之前显示的登录应用路径（例如 `/guest`）。为空则直接进行联合 |

OIDC 预置在每次启动时覆盖的内容比 SAML 预置更多。这些布尔值会从预置配置写入，包括默认值，因此运维人员通过管理 API 修改过的预置连接，会在下次重启时恢复这些值。`AllowedDomains`、`PassthroughParams`、`ProvisioningAttributeParams`、`SessionExpClaim` 和 `InteractionPath` 也是如此：预置配置省略的键会被重置为空或其默认值，而不是保留已存储的值。只有 `IconUrl` 和 `CreatedAt` 始终被保留，`ConnectionName` 和 `OrganizationId` 则在预置配置省略它们时被保留。

> **注意：**提供方也可以在运行时通过[管理 API](admin-api) 进行管理。通过配置预置的提供方会在每次启动时被 upsert，因此配置更改会在重启后生效。

## 密钥提供程序 {#secret-provider}

上游 OIDC 客户端密钥以及 TOTP / MFA 种子可以存储在 Azure Key Vault 中，而不是以明文存储：

| 设置 | 说明 |
|---|---|
| `SecretProvider:VaultUri` | Key Vault URI（例如 `https://my-vault.vault.azure.net/`）。如果未设置，则使用**明文**提供程序，密钥会原样存储在 Table Storage 中。 |
| `SecretProvider:RequireVaultReferences` | 默认为 `false`。为 `true` 时，不带保管库前缀（Key Vault 为 `kv:`，AWS Secrets Manager 为 `sm:`）的已存储引用会被视为**错误**，而不是作为明文值采用。迁移到保管库完成后再设置它。 |

配置后，看起来像 Key Vault 引用的密钥值会在运行时被解析。使用 `DefaultAzureCredential` 进行身份验证。

### 迁移到保管库，并在之后关上这扇门 {#migrating-into-a-vault-and-closing-the-door-afterwards}

两个基于保管库的提供程序都会原样返回不带前缀的引用，将其视为部署启用保管库之前写入的明文值。正是这一点让运行中的系统可以逐个密钥地迁移，而不必一次性全部迁移；但如果一直保持开放，它就是一条永久的降级路径：任何能够写入某个配置列的东西（一次未完成的迁移、一条在应该存放引用的地方存入原始值的管理路径、一个拥有存储访问权限但无法访问保管库的攻击者），都可以用自己选择的值替换受保管库保护的密钥，而且验证会完全通过，因为对于不带前缀的引用来说，引用*就是*值。

迁移完成后，请设置 `SecretProvider:RequireVaultReferences`。此后，解析不带前缀的引用会抛出异常，而不是悄悄返回明文。如果在解析出的提供程序是明文提供程序时设置它，启动会被拒绝，因为这种组合不存在可工作的状态：明文提供程序写入的每一个引用都不带前缀。

只要非 Development 环境的宿主最终使用的是明文提供程序，服务器也会在启动时记录一条警告。

> ⚠️ **生产环境：请设置 `SecretProvider:VaultUri`。**默认的密钥提供程序是**明文**的。未设置 `SecretProvider:VaultUri` 时，上游 OIDC 客户端密钥以及 TOTP / MFA 种子会以明文写入 Azure Table Storage，因此也会以明文出现在任何[备份](backup-restore)中。对于任何生产部署，请配置 `SecretProvider:VaultUri`，使这些密钥存储在 Key Vault 中。

## 管理 API {#admin-api}

| 设置 | 默认值 | 说明 |
|---|---|---|
| `AdminApi:Enabled` | `true` | **默认启用。**设置为 `false` 可禁用所有管理端点（它们不会被注册）。 |
| `AdminApi:Scope` | `authagonal-admin` | 访问管理端点所需的 JWT 作用域。可将其改为与你现有的作用域名称一致（例如 IdentityServer 迁移时使用的 `projects-identity-admin`）。 |

> ⚠️ **管理 API 默认启用，且权限极高。**管理作用域授予完整的管理和用户模拟权限，任何持有带 `AdminApi:Scope` 的令牌的人，都可以为任何用户签发令牌、管理客户端，并读写所有配置。请在网络层面限制管理端点（`/api/v1/*` 管理路由）的访问，并严格控制谁可以被签发管理作用域。作为纵深防御措施，该作用域是*保留的*：它永远不能被授予 OAuth 客户端（参见[管理 API](admin-api)），也不能通过模拟端点签发。如果不使用管理 API，请将 `AdminApi:Enabled = false` 设置为完全关闭。

## 同意 {#consent}

可以通过 `RequireConsent` 属性按客户端启用同意：

| 值 | 行为 |
|---|---|
| `false`（默认） | 身份验证之后立即继续授权 |
| `true` | 向用户显示列出所请求作用域的同意界面。同意会被持久保存 5 年，只有在请求新的作用域时才会再次提示。 |

用户可以在 `GET /consent/grants` 和 `DELETE /consent/grants/{clientId}` 查看和撤销自己的同意授权。

## 后端通道注销 {#back-channel-logout}

在客户端上注册 `BackChannelLogoutUri`，即可接收 OIDC Back-Channel Logout 1.0 通知。当用户注销时，Authagonal 会向每个客户端注册的 URI 发送一个已签名的注销令牌（JWT）。

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

## 邮件 {#email}

内置的邮件发送器使用 [Resend](https://resend.com)，并且在配置了 `Email:ResendApiKey` 时**自动启用**，无需注册服务。如需使用其他提供商，请在调用 `AddAuthagonal()` 之前注册你自己的 `IEmailService` 实现（无论 `Email:*` 键如何设置，它都优先）。

| 设置 | 说明 |
|---|---|
| `Email:ResendApiKey` | Resend API 密钥。设置后使用内置的 Resend 发送器。 |
| `Email:SenderEmail` | 发件人邮箱地址 |
| `Email:SenderName` | 发件人显示名称（默认为 `"Authagonal"`） |

> ⚠️ **没有任何邮件发送器时，自助注册将无法使用。**当未设置 `Email:ResendApiKey` 且未注册自定义的 `IEmailService` 时，一个空操作服务会静默丢弃所有邮件，验证邮件和密码重置邮件永远不会送达；又因为登录默认要求邮箱已确认，自助注册的用户将永远无法登录。在这种状态下，`UseAuthagonal` 会在启动时记录一条警告。开发/测试环境的应急办法：`Auth:AutoConfirmEmailDomains` 会自动确认所列域名的注册。

发往 `@example.com` 地址的邮件会被静默跳过（便于测试）。

## 集群 {#cluster}

集群层提供**领导者选举**（使签名密钥轮换这类仅限领导者的作业恰好只在一个节点上运行）以及**跨节点事件总线**，二者都基于可插拔的后端。默认是进程内实现：单个节点始终是自己的领导者，这是单节点和本地开发的正确设置，无需任何配置。

| 设置 | 环境变量 | 默认值 | 说明 |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | 总开关。为 `false` 时节点独立运行（始终是领导者，使用进程内事件总线）。 |
| `Cluster:Secret` | `Cluster__Secret` | *（无）* | 仅供内部使用的 `/_internal/backchannel-logout` 端点所需的共享密钥。设置后，调用方必须在 `X-Cluster-Secret` 请求头中出示它（以恒定时间进行比较）。**未设置时，该端点不授权任何人**，并返回 404：来源地址不是凭据，而回环地址恰恰是同一主机上的反向代理为其转发的每个请求（包括来自互联网的请求）所呈现的地址。 |
| `Cluster:AllowLoopbackWithoutSecret` | `Cluster__AllowLoopbackWithoutSecret` | `false` | 开发环境的显式选项：在没有 `Cluster:Secret` 时，接受**转发处理之前的对端地址**为回环地址的调用方。私有地址段仍会被拒绝：在共享的集群网络中，那等于信任每一个相邻的工作负载。不要在反向代理之后的宿主上设置它。 |
| `Cluster:RunLeaderElection` | `Cluster__RunLeaderElection` | `true` | 该节点是否运行租约续期循环并可以成为领导者。为 `false` 时仍会加入集群并消费事件总线；只是永远不会争夺租约。适用于必须接收集群事件但绝不能持有领导权的节点。 |
| `Cluster:LeaseTtlSeconds` | `Cluster__LeaseTtlSeconds` | `30` | 领导权租约时长。大约每隔该时长的一半续期一次。 |
| `Cluster:PollIntervalSeconds` | `Cluster__PollIntervalSeconds` | `3` | 事件总线后端轮询其他节点发布的消息的频率。 |

**多节点部署**通过 `AddAuthagonal` / `AddAuthagonalCore` 上的 `configureClustering` 回调换入真正的后端：

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

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` 只注册事件总线，保留进程内租约，适用于必须接收集群事件但绝不能争夺领导权的节点。

关于领导权和事件总线在多个实例之间如何运作，参见[扩展](scaling)。

## 转发请求头（受信任的代理） {#forwarded-headers-trusted-proxy}

Authagonal 以客户端 IP 作为限流和账户锁定的键，并且只在 HTTPS 请求上发出 HSTS。在反向代理 / 入口之后，真实的客户端 IP 和协议方案通过 `X-Forwarded-For` / `X-Forwarded-Proto` 请求头传入。这些设置控制**信任哪些代理跳点**来设置这些值，从而防止调用方伪造 `X-Forwarded-For` 来冒充客户端 IP。

| 设置 | 环境变量 | 默认值 | 说明 |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | 从 `X-Forwarded-For` 链的右侧起所采信的代理跳点数。默认值 `1` 只信任你的入口所追加的那一个跳点，并忽略链中更靠左的任何内容。 |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0`（数组） | *（空）* | 允许设置转发请求头的 CIDR 地址段（字符串数组，例如 `"10.0.0.0/8"`）。将其设置为你的代理 / 入口 / Pod 的 CIDR。只有声明了它，`X-Forwarded-Proto` 才会被采信；见下文。 |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies__0`（数组） | *（空）* | 允许设置转发请求头的单个代理 IP 地址（字符串数组）。可与 `KnownNetworks` 一起使用或代替它。 |

```json
{
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"],
    "KnownProxies": []
  }
}
```

### 两个请求头的信任条件并不相同 {#the-two-headers-are-not-trusted-on-the-same-terms}

`X-Forwarded-For` 调整的是**客户端 IP**，限流、锁定以及 `/_internal` 防护都以它为键。在没有任何声明时，Authagonal 会采信来自回环地址和 RFC1918 地址段的该请求头，并记录一条警告。这是一种尽力而为的默认行为，它胜过框架在信任集合为空时的行为，即采信来自*任何*调用方的该请求头。

`X-Forwarded-Proto` 改变的是**协议方案**，而协议方案决定了 `/connect/*` 是否响应（RFC 6749 §3.1/§3.2）、Cookie 是否被标记为 `Secure`，以及生成的绝对 URL 是否为 https。它**只**采信来自你在 `KnownNetworks` / `KnownProxies` 中声明过的代理。私有地址并不构成声明：Authagonal 以库的形式发布，无法看到它被部署到的网络，因此“对端持有私有地址”只是对网络拓扑的猜测。在扁平的局域网、共享的 VPC 或共享的容器网桥上，每一个相邻的工作负载都在这些地址段之内，都可以为一个以明文到达的请求断言 `https`。

**如果你的代理没有固定地址**（Kubernetes 入口、会轮换的负载均衡器、不告诉你跳点 CIDR 的平台），请将每个对端都声明为代理：

```json
{
  "ForwardedHeaders": {
    "KnownNetworks": ["0.0.0.0/0", "::/0"]
  }
}
```

当且仅当除了代理之外没有任何东西能访问该进程时，这才是安全的，而这正是此类部署本已依赖的假设。把它写下来，是把它放到一个可以被审查的地方，而不是让库去推断它。如果其他工作负载*能够*直接访问 Kestrel，那么在此设置下它们就可以伪造协议方案和客户端 IP，因此请改为固定真实的 CIDR。

### 未声明代理时：所有按来源计算的配额都是共享的 {#undeclared-proxy-every-per-source-quota-is-shared}

以调用方地址为键的限流（登录、注册、忘记密码、动态客户端注册、SAML ACS）需要知道是哪个客户端发出了请求。在反向代理之后，那就是转发而来的客户端 IP，而只有当你声明了写入它的代理时，转发而来的客户端 IP 才算是证据。在没有任何声明时，Authagonal 会以它实际观察到的对端作为这些配额的键，而在代理之后，那就是代理本身：**所有客户端共享同一个额度，任何单个调用方都可以替所有人把它用完**（登录的默认值是每 5 分钟 30 次尝试）。

这是有意为之而不是缺陷，而且无法在服务器端修复。另一种做法，即无论如何都以转发而来的值为键，会让调用方只需改变一个请求头就能为每个请求获得一个全新的额度，因为在 L4 负载均衡器之后，最右侧的转发跳点*就是*调用方自己写入的请求头。你处于这两种情况中的哪一种，正是声明告诉服务器的信息，而且没有其他任何东西能做到。声明代理之后，配额就会变为按客户端计算。

> ⚠️ **必须使用终止 TLS 的代理，并且必须对其进行声明。**Authagonal 必须运行在终止 TLS 的反向代理之后（或自行终止 TLS）。HSTS（`Strict-Transport-Security`）只在 HTTPS 请求上发出，而除非设置了 `Auth:AllowInsecureHttp`，OAuth 端点会直接拒绝明文请求，因此代理必须转发 `X-Forwarded-Proto: https`，**并且**必须在 `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` 中被列出，HSTS 才会被发送，`/connect/*` 才会响应。什么都不声明是升级时常见的失败原因：请求头到达了，却没有任何一方有资格据此行事，于是在一个确实运行在 TLS 上的部署中，每个 `/connect/*` 请求都返回 400。启动日志会说明这一点，拒绝响应的正文也会说明。

## 出站请求（SSRF 防护） {#outbound-fetches-ssrf-guard}

Authagonal 会向并非由它自己选择的 URL 发起服务器端 HTTP 请求：上游 IdP 的 SAML 元数据或 OIDC 发现文档、`private_key_jwt` 身份验证期间客户端的 `jwks_uri`、后端通道注销 URI、预配回调。其中一些 URL 由注册客户端的人提供，于是一个指向 `169.254.169.254` 或你集群内部主机的 URL，就成了 Authagonal 代替攻击者发出的请求。

每一个这样的请求都受到两重防护。**URL 检查**在接受 URL 的那一刻（一次管理端写入、一次动态客户端注册）拒绝非 http(s) 协议方案、字面形式的内部地址，以及 `localhost` / `.local` / `.internal` 名称，此时错误可以归咎于输入它的人。**地址检查**在套接字层运行：它解析主机名，拒绝返回的每一个内部地址，并连接到一个它实际检查过的地址，而不是把名称交还给操作系统。第二重检查是文本检查做不到的，因为主机名并不是攻击者必须如实提供的文本：`logout.attacker.test` 能通过所有后缀规则和字面规则，然后解析为云元数据地址。由于重定向是一次新的连接，地址检查会在每一跳上重新运行。

两者默认都是开启的，大多数部署根本不会注意到它们。有两种情况会让它们显现出来。

### 有意访问内部目标 {#reaching-an-internal-destination-on-purpose}

与只能通过你的私有网络访问的 IdP 进行联合，或者为运行在同一集群中的应用进行预配，会被与阻止攻击完全相同的规则拒绝。请列出这些目标：

```json
{
  "Auth": {
    "AllowedInternalTargets": ["idp.corp.internal", "*.svc.corp.internal", "10.4.0.0/16"]
  }
}
```

| 条目形式 | 允许 |
|---|---|
| `idp.corp.internal` | 该确切主机，以及它解析到的每一个地址 |
| `*.corp.internal` | 该后缀下的任何主机，以及它们解析到的每一个地址 |
| `10.4.0.0/16`, `fd00:1234::/48` | 该网络，无论使用什么名称 |
| `10.4.1.7` | 该单个地址，无论使用什么名称 |

环境变量形式为 `Auth__AllowedInternalTargets__0`、`__1`，依此类推。格式错误的 CIDR 条目会在启动时失败，而不是悄悄地什么也不允许。

**此列表只作用于由你提供的 URL。**即上游 SAML 元数据请求、上游 OIDC 发现文档（包括该文档中指定的 `token_endpoint`、`userinfo_endpoint` 和 `jwks_uri`）以及预配回调。它有意**不**作用于客户端注册的 `jwks_uri` 或后端通道注销 URI，在那里内部主机从来都不是合理的部署形态，因此开放一个联合目标不会同时把元数据服务暴露给匿名的 `/connect/token` 请求。不存在全局的“关闭”开关。

注意，无论此列表如何，两个联合元数据 URL 都仍然要求使用 https。那份文档携带着用于验证每一个上游断言的密钥和证书，而私有网络并不是安全信道。

> ⚠️ **多租户宿主：在列出任何内容之前，先确认是谁在写入元数据 URL。**此列表的作用范围是*你*所配置的目标，而在单租户部署中，连接管理员就是你自己。如果你为他人运行 Authagonal（一个由租户管理员通过门户或管理 API 配置自己的 SAML/OIDC 连接的 SaaS），那么 `MetadataLocation` 就是由**客户**提供的，你在这里添加的每一个条目，任何把连接指向它的租户都能访问。在这样的宿主上请将其保持为空（默认），如果某个租户确实需要本地部署的 IdP，请为其提供一条在你的网络之外终止的出口路径，而不是从网络内部开放一条路径。

### 如果你的出口流量需要 HTTP 代理 {#if-your-egress-requires-an-http-proxy}

地址检查挂接在 `SocketsHttpHandler.ConnectCallback` 上，而在使用代理时，.NET 调用该回调时传入的是**代理的**端点，永远不是目标的端点，因此该检查会去检查代理，发现它完全可路由，然后放行一切。它会恰恰在最可能使用代理的网络中失效放行。因此，受防护的客户端设置了 `UseProxy = false`，在只能通过代理出网的网络中，它们的请求会失败。

`Auth:AllowOutboundProxy` 会让由运维人员配置的请求（SAML 元数据、OIDC 发现文档、预配回调）重新经由代理发送。对于这些请求，你保留 URL 检查，但失去地址检查：解析到内部地址的主机名将不再被拦截。它**不**作用于客户端 `jwks_uri` 请求或后端通道注销的投递：这些目标由注册者选择，并且可以从匿名请求触发，因此没有针对它们的开关。必须为这些请求使用代理的网络，需要在它们前面放置一个具备 SSRF 过滤能力的出口网关。

当 `UseAuthagonal()` 发现设置了 `HTTPS_PROXY`、`HTTP_PROXY` 或 `ALL_PROXY` 时，会在启动时记录一条警告，指出哪些客户端绕过了代理；否则，症状只会是“SSO 不能用了”，而没有任何线索指向原因。

### 不受防护的部分 {#what-is-not-guarded}

BFF 的出站客户端和邮件投递。`AuthagonalBffOptions.Upstreams[].TargetBaseUrl` 是你自己的配置，其文档示例本身就是一个内部地址；BFF 的令牌客户端与你配置的颁发机构通信；而代理已经会拒绝任何离开了所配置上游颁发机构的组合目标，因此调用方无法操纵这些请求的去向。`Resend` 向一个编译时常量发送请求。这三者都会正常使用环境中的代理。

## 限流 {#rate-limiting}

内置的限流保护着容易被滥用的端点：

| 端点 | 上限 | 时间窗口 | 计数键 |
|---|---|---|---|
| `POST /api/auth/login` | 30（`Auth:MaxLoginAttemptsPerIp`） | 5 分钟（`Auth:LoginWindowMinutes`） | 来源地址，以及另行计算的所提交邮箱 |
| `POST /api/auth/register` | 5（`Auth:MaxRegistrationsPerIp`） | 1 小时（`Auth:RegistrationWindowMinutes`） | 客户端 IP |
| `POST /api/auth/forgot-password` | 3（`Auth:MaxPasswordResetsPerEmail`） | 1 小时（`Auth:PasswordResetWindowMinutes`） | 目标邮箱 |
| `POST /api/auth/forgot-password` | 15（`Auth:MaxPasswordResetsPerIp`） | 1 小时（`Auth:PasswordResetWindowMinutes`） | 客户端 IP |
| `POST /connect/register`（启用时） | 10 | 1 小时 | 客户端 IP |
| SCIM 端点 | 200 | 1 分钟 | SCIM 客户端 |

默认情况下，限流**在每个节点的进程内**执行（基于 `IRateLimiter` 接入点），因此在 N 个实例的情况下，实际上限是所配置值的 N 倍。请把这些限制当作兜底，并在边缘层（WAF / 入口 / CDN）实施权威的全局限制。参见[扩展](scaling#rate-limiting)。

### 集群范围的限流（`Auth:DurableRateLimiting`） {#cluster-wide-limits-authdurableratelimiting}

将 `Auth:DurableRateLimiting` 设置为 `true`，即可把计数器移到部署已在运行的存储中，
使所有副本共享同一个额度，上限也不再随实例数成倍增加。

| | 进程内（默认） | 持久化 |
|---|---|---|
| N 个副本时的上限 | 所配置值的 N 倍 | 所配置的值 |
| 每次检查的开销 | 无 | 一次存储往返 |
| Pod 重启后是否保留 | 否 | 是 |
| 后端 | 任意 | Azure Table、SQL、DynamoDB |

当某个额度保护着可被猜测的东西时，值得开启它，首先就是设备流程的 `user_code`：
在那里，尝试次数限制是攻击者与一个可授予活跃会话的代码之间的唯一屏障，
而随副本数扩展的额度并不是合适的形态。对于流量类限制则用处不大，
因为在那些地方，边缘层无论如何都是权威的限制。

在生产环境中重要的细节：

- **它不是免费的。**每次限流检查都会变成一次存储往返，包括登录、令牌
  和 SCIM 路径。单节点部署不会因此获益（在那里，按节点*就是*集群范围），
  应当保持关闭。
- **采用固定窗口，因此突发流量可能跨越窗口边界。**N 的额度意味着“每个窗口 N 次，跨越边界时最多 2N 次”，
  而随附的额度都留有这样的余量。正因如此，计数器在每个后端上都可以是一次
  原子递增，而正确性正依赖于这一性质。
- **它会失效放行。**如果存储无法访问，请求会被允许并记录一条错误：
  限流器保护的是登录路径，绝不能成为让登录路径瘫痪的手段。请保留边缘层的规则。
- **如果你在没有提供 `IRateLimitCounterStore` 的提供程序的情况下设置此项，宿主将不会启动。**
  它会拒绝启动，而不是悄悄退回你刚刚关闭的按节点限流。
- **计数器行会被自动回收**：DynamoDB 通过原生 TTL，SQL 通过 `SqlExpiryReaper`，Azure
  Table 通过仅在领导者上运行的清扫（Table Storage 既没有 TTL，也没有服务器端算术运算，因此它也是
  一次递增需要一次读取加一次条件写入的后端）。

## CORS {#cors}

CORS 是动态配置的，并且**按路径限定作用范围**：之前那句一行描述（“所有已注册客户端的来源都会被自动允许”）
描述的范围远远超出了提供程序实际所做的。

- **客户端注册的来源**（客户端上的 `AllowedCorsOrigins`）只在 `/connect/` 和
  `/.well-known/` 下生效。它们**不会**开放 `/api/auth/`、`/api/v1/` 或 `/scim/`。已禁用的客户端
  不贡献任何来源，格式错误的来源会被丢弃。
- 在 `/api/auth/`、`/api/v1/`、`/scim/`、`/consent` 或 `/approvals` 下**永远不允许携带凭据**，
  无论来源是运维人员配置的还是客户端注册的。浏览器客户端从另一个来源以
  `credentials: 'include'` 调用这些路径时，无论如何配置都会失败；请使用
  后端即前端（参见 `@authagonal/bff` 包），而不是携带凭据的跨源调用。
- 解析出的策略会被缓存 60 分钟。

因此，添加到客户端 `AllowedCorsOrigins` 中的来源会让 `/connect/*` 可用，但不会让 `/api/v1/*`
可用。这是有意为之：这些路径承载着会话 Cookie 和管理接口面。

## HashiCorp Vault Transit {#hashicorp-vault-transit}

`VaultTransitClient` 与 Vault 的 Transit 机密引擎通信：签名、验证、加密、解密以及带密钥的 HMAC。
它是基于 Vault 的 `IFieldCipher` 或 `IIndexTokenizer` 的构建基础，需要由你自行注册。

**JWT 签名并没有委托给 Vault。**`ProtocolKeyManager` 始终使用
`ISigningKeyStore` 中的密钥签名，没有任何接入点能用 Vault 密钥替换它。实现这一点需要什么，参见
[可扩展性](extensibility)。

作为库托管时，这部分以编程方式进行配置。

## 完整示例 {#full-example}

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
