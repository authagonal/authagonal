---
layout: default
title: 可扩展性
locale: zh-Hans
---

# 可扩展性

Authagonal 可以作为库托管在你自己的 ASP.NET Core 项目中，你可以完全控制各项服务的实现。

## 扩展方法 {#extension-methods}

三个方法即可将 Authagonal 组装进任何 ASP.NET Core 应用：

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

### 多租户托管 {#multi-tenant-hosting}

对于多租户部署，请改用 `AddAuthagonalCore()`。它会注册端点、中间件和核心服务，但跳过存储和后台服务；这些由你按租户提供。签名密钥管理默认使用 `Authagonal.Protocol` 的 `ProtocolKeyManager` 单例；如果宿主在 `AddAuthagonalCore()` 之前注册了自己的 `IKeyManager`，则保留宿主的注册：

```csharp
builder.Services.AddScoped<ITenantContext, MyTenantContext>();
builder.Services.AddScoped<IKeyManager, MyPerTenantKeyManager>();
builder.Services.AddAuthagonalCore(builder.Configuration);
```

`IKeyManager` 和各存储接口（`IClientStore`、`IScimTokenStore` 等）在请求时从 `HttpContext.RequestServices` 中解析，因此作用域注册可以正确地实现按租户隔离。

### 单独嵌入 `Authagonal.Protocol` {#embedding-authagonalprotocol-alone}

只想要 OIDC 协议接口面（自有的身份验证、自有的管道，以及可直接接入的 `/connect/*` 端点）的宿主，可以调用 `AddAuthagonalProtocol()` + `MapAuthagonalProtocolEndpoints()`，而完全不使用 `Authagonal.Server`。

在这种形式下，`/connect/authorize`、`/connect/token`、`/connect/userinfo` 和 `/connect/par` 同样会依据 RFC 6749 §3.1/§3.2 拒绝明文 http。由于该包被映射到一个不属于它的管道中，这项要求以过滤器而不是中间件的形式附加在端点上，因此无论你如何组装管道，也无论你是映射整个接口面还是逐个映射端点，它都会生效。升级之前值得了解两个后果：

- **在终止 TLS 的代理之后，请在声明代理的前提下调用 `UseForwardedHeaders`。**该过滤器在路由之后读取协议方案，因此转发而来的 `X-Forwarded-Proto: https` 可以满足它。没有该中间件，你的宿主看到的就是明文，这也意味着你的 Cookie 不会被标记为 `Secure`，生成的绝对 URL 也是错误的，因此这值得修复，而不是绕过。注册时请填写 `KnownProxies` / `KnownNetworks`：ASP.NET Core 会把空的信任集合理解为“每个调用方都是受信任的代理”，这等于把协议方案交给任何能访问你宿主的人。如果拒绝响应的正文提到了未被应用的 `X-Forwarded-Proto`，它所要求的正是这个中间件。
- **确实通过 http 提供协议接口面的宿主需要设置显式启用选项**，方式与服务器相同：

```csharp
builder.Services.AddAuthagonalProtocol(o =>
{
    o.AuthenticationScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    o.AllowInsecureHttp = builder.Environment.IsDevelopment();   // never in production
});
```

发现文档和 JWKS 有意不受此限制：它们是公开的元数据，而一个无法读取它们的客户端，从一开始就无从得知自己需要 https。

当你使用 `AddAuthagonal()`（完整服务器）时，无需单独设置此项：`Auth:AllowInsecureHttp` 会自动传递到协议选项中，因此一个开关即可控制整个接口面。

## 覆盖服务 {#overriding-services}

请在调用 `AddAuthagonal()` **之前**注册你的自定义实现。Authagonal 内部使用 `TryAdd`，因此你的注册优先：

```csharp
// Custom implementations, registered first so they won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

`IAuthHook` 比较特殊：它是一个可多次注册的管道。你可以注册任意多个钩子（任意生命周期，包括 `AddScoped`），它们全部按注册顺序运行。只有在 `AddAuthagonal()` / `AddAuthagonalCore()` 运行时尚未注册任何钩子的情况下，才会添加空操作的 `NullAuthHook`，因此请始终先注册你的钩子。

### 扩展点 {#extensibility-points}

| 接口 | 默认值 | 用途 |
|---|---|---|
| `IAuthHook` | `NullAuthHook`（空操作，仅在未注册任何钩子时添加） | 身份验证事件的生命周期钩子：审计日志、自定义校验、Webhook。可以注册多个钩子；全部按顺序运行 |
| `IEmailService` | `NullEmailService`（空操作），或在配置了 `Email:ResendApiKey` 时使用内置的 Resend 发送器 | 投递验证邮件、密码重置邮件和账户已存在通知 |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator`（作用域） | 将用户预配到下游应用 |
| `ISecretProvider` | `PlaintextSecretProvider`，或在配置了 `SecretProvider:VaultUri` 时使用内置的 `KeyVaultSecretProvider` | 可逆的密钥存储（Key Vault、AWS Secrets Manager、Vault Transit 等） |
| `ITenantContext` | `DefaultTenantContext`（从 `IConfiguration` 读取） | 多租户部署中的租户解析 |
| `IKeyManager` | `ProtocolKeyManager`（单例，来自 `Authagonal.Protocol`） | 签名密钥管理；覆盖它以实现按租户的密钥隔离 |
| `IProvisioningAppProvider` | `ConfigProvisioningAppProvider`（作用域） | 解析可用的预配应用；覆盖它以实现动态或按租户的应用解析 |
| `IAuditLogger` | `NullAuditLogger`（空操作） | 配置变更和安全相关事件的审计记录 |
| `IClientCredentialsClaimsTransformer` | `NullClientCredentialsClaimsTransformer`（单例，来自 `Authagonal.Protocol`） | 在 `client_credentials` 签发时校验调用方提供的上下文，并将声明强制写入令牌，或拒绝签发 |
| `ITokenExchangeSubjectTransformer` | `NullTokenExchangeSubjectTransformer`（单例，来自 `Authagonal.Protocol`） | RFC 8693 令牌交换的主体映射；参见[智能体身份验证](agentic-auth) |
| `ITurnstileKeyProvider` | `OptionsTurnstileKeyProvider`（作用域，读取 `TurnstileOptions`） | 本次请求适用哪个 Turnstile 站点密钥和密钥 |
| `IInteractiveCorsOriginPolicy` | `DenyInteractiveCorsOriginPolicy`（单例，拒绝所有来源） | 允许对 `/api/auth/*` 发起携带凭据的跨源调用的来源 |

另有三个接入点位于**存储层**，而不是 DI 中：`IFieldCipher`、`IIndexTokenizer` 和 `IChangeWriter`（都在 `Authagonal.Core.Services` 中）。存储提供程序将它们作为可选的构造函数参数接受；参见下文各自的章节。

## IAuthHook {#iauthhook}

`IAuthHook` 接口提供了接入身份验证生命周期的钩子。关键路径上的方法（身份验证、用户创建、令牌签发）可以抛出异常来中止操作；较新的方法则是事后通知。可以注册多个 `IAuthHook` 实现，它们全部按注册顺序运行。

```csharp
public interface IAuthHook
{
    // Core lifecycle: implement these
    Task OnUserAuthenticatedAsync(string userId, string email, string method,
        string? clientId = null, CancellationToken ct = default);
    Task OnUserCreatedAsync(string userId, string email, string createdVia,
        CancellationToken ct = default);
    Task OnLoginFailedAsync(string email, string reason,
        CancellationToken ct = default);
    Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType,
        CancellationToken ct = default);
    Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email,
        MfaPolicy clientPolicy, string clientId, CancellationToken ct = default);
    Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default);
    Task OnUserUpdatedAsync(string userId, string email, string updatedVia,
        CancellationToken ct = default);
    Task OnUserDeletedAsync(string userId, string email, string deletedVia,
        CancellationToken ct = default);

    // Additive notifications: default no-op implementations, so existing
    // hooks keep compiling as the interface grows
    Task OnMfaVerifyFailedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnEmailConfirmedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaEnrolledAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaCredentialRemovedAsync(string userId, string email, string mfaMethod,
        bool mfaDisabled, CancellationToken ct = default) => Task.CompletedTask;
    Task OnRecoveryCodesRegeneratedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnPasswordChangedAsync(string userId, string email, string changedVia,
        CancellationToken ct = default) => Task.CompletedTask;

    // Token gate and agentic / consent notifications (also default no-ops)
    Task OnTokenIssuingAsync(TokenIssuanceContext context,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnDelegationMintedAsync(DelegationAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnApprovalRequestedAsync(ApprovalAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnApprovalResolvedAsync(ApprovalAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnAgentConsentChangedAsync(string subjectId, string clientId, string change,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnConsentRevokedAsync(string subjectId, string clientId, int grantsRemoved,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnCapabilityTicketRedeemedAsync(string ticketId, string? subjectId, string clientId,
        CancellationToken ct = default) => Task.CompletedTask;
}
```

### 参数 {#parameters}

| 方法 | 说明及 `method` / `via` 取值 |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`、`"passkey"`、`"saml"`、`"oidc"` |
| `OnUserCreatedAsync` | `"admin"`、`"saml"`、`"oidc"` |
| `OnUserUpdatedAsync` | `"admin"`、`"self"`（宿主可以传入自己的值，例如 SCIM 来源） |
| `OnUserDeletedAsync` | `"admin"`；仅为通知，该记录可能已无法读取 |
| `OnLoginFailedAsync` | `"user_not_found"`、`"invalid_password"` 等 |
| `OnTokenIssuedAsync` | 授权类型：`"authorization_code"`、`"refresh_token"`、`"client_credentials"` |
| `ResolveMfaPolicyAsync` | 在密码验证之后调用；返回该用户实际生效的 MFA 策略。默认：原样返回 `clientPolicy`。 |
| `OnMfaVerifiedAsync` | `"totp"`、`"webauthn"`、`"recovery"` |
| `OnMfaVerifyFailedAsync` | 方法取值与 `OnMfaVerifiedAsync` 相同。只在第一因素凭据有效之后才触发，因此密集出现是试图绕过 MFA 的强烈信号（区别于密码阶段的 `OnLoginFailedAsync`） |
| `OnEmailConfirmedAsync` | 用户通过验证链接确认了邮箱；已持久化 |
| `OnMfaEnrolledAsync` | `"totp"`、`"webauthn"`；该凭据已处于活跃状态 |
| `OnMfaCredentialRemovedAsync` | `"totp"`、`"webauthn"`、`"recoverycode"`；当移除后不再有主要因素时，`mfaDisabled` 为 true |
| `OnRecoveryCodesRegeneratedAsync` | 先前的恢复码集合已失效 |
| `OnPasswordChangedAsync` | 例如 `"reset"`；更改已持久化，现有会话已失效 |
| `OnTokenIssuingAsync` | 签发前的关卡，不同于 `OnTokenIssuedAsync`。在 `authorization_code`、`refresh_token` 和 `device_code` 上触发，也在两个智能体签发路径上触发（委托的令牌交换，以及为带有智能体配置文件的客户端执行的 `client_credentials`）。抛出异常即可拒绝：普通异常会变为携带其消息的 `access_denied`；抛出 `ProtocolTokenException` 可以指定你自己的 OAuth 错误。在刷新时它在轮换之前运行，因此拒绝后所出示的刷新令牌仍可使用。上下文携带 `ClientId`、`SubjectId`、`GrantType`、`Scopes`、`RequestedAuthorityJson`，并在请求选择了组织时携带 `OrganizationId` / `OrganizationSlug` |
| `OnDelegationMintedAsync` | 通过令牌交换签发了一个委托（复合身份）令牌；仅为通知 |
| `OnApprovalRequestedAsync` | 一次委托交换停在了一个需要询问策略的操作上，并创建了一个待处理的审批 |
| `OnApprovalResolvedAsync` | 一个待处理的审批已被用户批准或拒绝 |
| `OnAgentConsentChangedAsync` | `change` 为 `"granted"` 或 `"revoked"`（长期有效的智能体同意） |
| `OnConsentRevokedAsync` | 用户撤销了一个已授权的应用；该同意以及该客户端与会话绑定的授权都已被移除。`grantsRemoved` 是被移除的数量（0 表示没有） |
| `OnCapabilityTicketRedeemedAsync` | 一张能力票据已兑换为其绑定的令牌 |

### 示例：审计日志记录器 {#example-audit-logger}

```csharp
public sealed class AuditAuthHook(ILogger<AuditAuthHook> logger) : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Login: {Email} via {Method}", email, method);
        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email,
        string createdVia, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] User created: {Email} via {Via}", email, createdVia);
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct)
    {
        logger.LogWarning("[AUDIT] Login failed: {Email} ({Reason})", email, reason);
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId,
        string grantType, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Token issued: {ClientId} ({GrantType})",
            clientId, grantType);
        return Task.CompletedTask;
    }

    // ... remaining required methods return Task.CompletedTask
}
```

### 示例：域名限制 {#example-domain-restriction}

```csharp
public sealed class DomainRestrictionHook : IAuthHook
{
    private static readonly HashSet<string> BlockedDomains = ["competitor.com"];

    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        var domain = email.Split('@').Last();
        if (BlockedDomains.Contains(domain))
            throw new InvalidOperationException($"Domain {domain} is not allowed");

        return Task.CompletedTask;
    }

    // ... other methods return Task.CompletedTask
}
```

## IClientCredentialsClaimsTransformer {#iclientcredentialsclaimstransformer}

`client_credentials` 令牌没有主体，因此令牌交换的接入点无法作用于它。这个接入点面向第一方服务调用方：其令牌必须在没有用户的情况下指明它所代表的上下文（一个组织、一个租户）。它在客户端、其作用域以及任何 RFC 8707 资源都校验完毕之后、令牌签发之前运行。

```csharp
public interface IClientCredentialsClaimsTransformer
{
    Task<ClientCredentialsClaimsResult> TransformAsync(
        OAuthClient client,
        IReadOnlyList<string> grantedScopes,
        IReadOnlyDictionary<string, string> extraParameters,
        CancellationToken ct = default);
}
```

- `extraParameters` 保存令牌请求中的非协议表单参数（单值，以第一个为准），例如调用方发送的 `organization_id`。
- 返回 `ClientCredentialsClaimsResult.Allow(claims)` 可将 `claims` 强制写入令牌（null 或空则保持不变），返回 `ClientCredentialsClaimsResult.Reject(error, description)` 则以该 OAuth 错误拒绝签发。
- 保留的协议声明名称在签发时仍会被阻止。
- 请依据你自己的权威数据校验调用方提供的绑定；不要未经检查就把它复制到令牌上。
- 默认的 `NullClientCredentialsClaimsTransformer` 通过 `TryAddSingleton` 注册，因此请先注册你自己的实现来替换它。

## ITurnstileKeyProvider {#iturnstilekeyprovider}

两个 Turnstile 密钥来自同一个对象，因此浏览器渲染的小组件与服务器用于验证的密钥永远不会不一致。默认的 `OptionsTurnstileKeyProvider` 从 `TurnstileOptions` 读取 `SiteKey` 和 `SecretKey`，适合只服务一个域名的宿主。服务客户自有域名的宿主（Cloudflare 会限制一个小组件可用的主机名数量）应注册自己的作用域实现，返回分配给发起请求的主机的那个小组件的密钥对。

```csharp
public interface ITurnstileKeyProvider
{
    string? SiteKey { get; }     // null when disabled
    string? SecretKey { get; }   // null or empty disables enforcement
}
```

它通过 `TryAddScoped` 注册，因此在 `AddAuthagonal` 之前完成的注册优先。

## IInteractiveCorsOriginPolicy {#iinteractivecorsoriginpolicy}

交互式身份验证 API（`/api/auth/*`）默认拒绝携带凭据的跨源调用，因为它由同源提供的登录应用驱动。允许租户在另一个来源上构建自己登录界面的宿主，应实现此接口来为特定来源担保。

```csharp
public interface IInteractiveCorsOriginPolicy
{
    ValueTask<bool> IsAllowedAsync(HttpContext context, string origin, string path);
}
```

- 按请求、按来源调用；调用它时租户解析已经完成。
- 返回 true 会允许该来源读取账户、会话、个人资料和 MFA 设置端点为当前登录者返回的已认证响应。只为宿主控制或已验证的来源作答，绝不为从请求中取得的来源作答。
- 默认实现（`DenyInteractiveCorsOriginPolicy`，`TryAddSingleton`）对每个来源都返回 false。

## ISecretProvider {#isecretprovider}

`ISecretProvider`（位于 `Authagonal.Core.Services`）是用于已存储密钥（例如 SSO 客户端密钥、SMTP 密码和 TOTP 种子）的可逆加密接入点。`ProtectAsync` 把明文转换为存储所持久化的引用；`ResolveAsync` 把引用还原为明文。默认的 `PlaintextSecretProvider` 原样存储值（引用就是值本身）。

```csharp
public interface ISecretProvider
{
    Task<string> ResolveAsync(string secretReference, CancellationToken ct = default);
    Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default);
}
```

设置 `SecretProvider:VaultUri` 会自动接入内置的 `KeyVaultSecretProvider`（通过 `DefaultAzureCredential` 访问 Azure Key Vault）。如需其他方案，请在 `AddAuthagonal()` 之前注册你自己的实现。

## PII 字段加密：IFieldCipher {#pii-field-encryption-ifieldcipher}

`IFieldCipher` 对单个用户 PII 字段的值（电话、公司、自定义属性，以及个人资料行上的邮箱和姓名）进行静态加密。它是一个存储层接入点：存储提供程序将其作为可选的构造函数参数接受（例如 `TableUserStore`），缺省时使用直通的 `NullFieldCipher`，因此加密严格需要显式启用，未配置的宿主会继续存储明文。

```csharp
public interface IFieldCipher
{
    Task<string> ProtectAsync(string plaintext, CancellationToken ct = default);
    Task<string> ResolveAsync(string stored, CancellationToken ct = default);

    // Batch variants have default loop implementations; override for backends
    // with a one-round-trip batch primitive (e.g. Vault Transit)
    Task<IReadOnlyList<string>> ProtectManyAsync(IReadOnlyList<string> plaintexts,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResolveManyAsync(IReadOnlyList<string> stored,
        CancellationToken ct = default);
}
```

有两点契约很重要。`ProtectAsync` 必须返回一个自描述的密文标记（例如 Vault Transit 的 `vault:v{n}:...`），而 `ResolveAsync` 对于不认为是自身密文的值，必须原样透传。正是这条透传规则让加密可以在现有行上逐步推行：读取尚未迁移的行时返回旧的明文，而下一次写入会重新保护它。

## 盲索引搜索：IIndexTokenizer {#blind-index-search-iindextokenizer}

`IIndexTokenizer` 让已加密的字段保持可搜索。它把规范化后的明文值转换为确定性的、可安全用作表键的盲索引标记，通常是一个密钥保存在数据库之外的带密钥 HMAC。确定性意味着相等查找仍然有效（“email = x”变为“token = HMAC(x)”），而数据库转储既无法重新计算也无法逆推出某个标记。前缀搜索在此基础上实现：对一个值的每个前缀分别生成标记，因为带密钥的 HMAC 会破坏顺序和范围扫描。

> **转储仍会暴露什么。**“既无法重新计算也无法逆推”对单个标记成立，对整个索引则不成立。
> 有三类残留信息依然存在，在依赖这一机制之前值得了解：
>
>   *（已修复。）*~~**结构。**前缀索引为每个前缀写入一行，因此一条记录的行数
>   等于被索引字段的长度。~~现在每个被索引的值都写入固定数量的行，
>   并以任何查询都无法产生、转储也无法与真实前缀区分开的诱饵行填充。
> - **相等性与频率。**标记在构造上就是确定性的，这正是查找得以实现的原因，
>   因此转储会显示哪些记录共享同一个值，以及每个值有多常见。域名索引
>   会按雇主对你的用户群体进行分组，这往往无需还原出邮箱地址就能识别出具体的人。
> - **选择明文。**一个既能读取存储*又*能促使值被索引的攻击者
>   （注册一个账户，或通过 SCIM 被预配）可以提交一个候选值并查找它的标记。
>   无论密钥存放在哪里，这都能还原出任何可猜测的值（常见域名、常见名字），
>   因为这个预言机是写入路径，而不是密码算法。
>
> 标记化所防御的正是它为之设计的场景：某人手里只有一份转储，别无其他，
> 试图读出邮箱地址。剩下的两类残留信息，恰恰是注册预言机本来就会泄露的内容。
> 如果它们不可接受，请不要配置前缀索引表和域名索引表
> （精确匹配查找不涉及这两类信息），而不是假定 HMAC 能覆盖它们。

```csharp
public interface IIndexTokenizer
{
    Task<string> TokenizeAsync(string value, CancellationToken ct = default);
    Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values,
        CancellationToken ct = default);
}
```

与 `IFieldCipher` 一样，它是一个带有直通默认实现（`NullIndexTokenizer`）的可选存储构造函数参数，因此在你显式启用之前，索引行会一直以明文为键。返回的标记必须可以安全地用作 Azure Table 的 PartitionKey/RowKey 值（不得包含 `/ \ # ?` 或控制字符）。

## 变更日志捕获：IChangeWriter {#change-log-capture-ichangewriter}

`IChangeWriter`（在 0.6.0 中由 `ITombstoneWriter` 更名而来）把每一个发生变更的行的键记录到一个专用的变更日志表中，使增量备份无需扫描活动表上未建索引的 `Timestamp` 列就能找到变更内容。所有表的删除都会被捕获（对活动行的扫描看不到已经不存在的行）；对于备份从日志读取而不是扫描的那些表，还会捕获 upsert。内置实现：`TableChangeWriter`（Azure Table Storage）、`DynamoChangeWriter`（DynamoDB）和 `SqlChangeWriter`（PostgreSQL / SQLite）。

```csharp
public interface IChangeWriter
{
    // Deletes
    Task WriteAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);

    // Upserts
    Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteUpsertBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);
}
```

面向实现者和调用方的顺序契约：先写入删除的墓碑记录，再删除数据行。如果顺序相反，一旦发生崩溃，这次删除就会从此后的所有备份中丢失，因为删除是重新扫描唯一无法自我修复的一类变更。反向的崩溃是安全的：之后对该键的写入会重新标记一个更新的时间戳，而合并/恢复会保留在墓碑记录之后写入的行。

## 自定义端点 {#custom-endpoints}

你可以在 Authagonal 的端点之外添加自己的端点：

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## HashiCorp Vault Transit 集成 {#hashicorp-vault-transit-integration}

> **JWT 签名并没有委托给 Vault。**本节以前展示过一段看似能启用它的 DI 代码片段。
> 注册 `VaultTransitCryptoProvider` **对令牌签名没有任何影响**：
> `ProtocolKeyManager` 调用 `ProtocolSigningKeyOps.BuildSigningCredentials`，后者根据
> `ISigningKeyStore` 中的密钥材料构建一个 `ECDsaSecurityKey`，没有任何组件会用
> `VaultTransitSecurityKey` 替换它。按照旧代码片段操作的宿主会看到 ES256 令牌能通过 JWKS 验证，
> 并合理地得出 Vault 在为它们签名的结论，而实际上私钥是在首次启动时于本地生成的，
> 并被持久化到主数据存储中；除非恰好注册了 `IFieldCipher`，否则以明文存储。
> 对该存储的读取权限就等于可以完全冒充签发者。如果你有签名密钥绝不能离开 HSM 的合规要求，
> 这并不能满足它。
>
> 现在，服务器在启动时如果发现注册了 `VaultTransitCryptoProvider`，会记录一条错误日志，
> 因此这种误解不会再悄无声息地延续下去。
>
> 要真正实现它，需要的不只是一个 DI 注册：`ISigningKeyStore` 必须能表示一个没有本地材料的密钥
> （一个 Transit 密钥*名称*，而不是私有标量），`BuildSigningCredentials` 需要一个
> 返回 `VaultTransitSecurityKey` 的接入点，`BuildJwksAsync` 必须发布从 Vault 读回的公钥，
> 而轮换和提前发布必须创建并提升 Transit 密钥版本，而不是在本地生成。
> `VaultTransitClient`、`VaultTransitSecurityKey`、`VaultTransitSignatureProvider` 和
> `VaultTransitCryptoProvider` 被保留下来，因为它们是能正常工作的部分；缺少的是接线。

`VaultTransitClient` 如今**真正**适用的是加密和 HMAC 接入点：基于 Vault 的
`IFieldCipher` 用于 PII 静态加密，或者 `IIndexTokenizer` 用于带密钥的盲索引：

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("Vault", client =>
{
    client.BaseAddress = new Uri("https://vault.example.com");
    client.DefaultRequestHeaders.Add("X-Vault-Token", "hvs.xxx");
});

builder.Services.AddSingleton<VaultTransitClient>();

// Your own adapters over the client. These are the seams Authagonal actually consumes.
builder.Services.AddSingleton<IFieldCipher, MyVaultFieldCipher>();
builder.Services.AddSingleton<IIndexTokenizer, MyVaultIndexTokenizer>();

builder.Services.AddAuthagonal(builder.Configuration);
```

注册 `IFieldCipher` 也能消除 `PlaintextSigningKeyWarning`，因为签名密钥存储
通过同一个接入点处理其密钥材料，这是目前所能实现的、最接近原先说法的方案：
私钥仍然存在于本地，但不是以明文形式。

`VaultTransitClient` 提供以下操作：

| 方法 | 说明 |
|---|---|
| `SignAsync(keyName, data)` | 使用 Vault Transit 密钥对数据签名 |
| `VerifyAsync(keyName, data, signature)` | 通过 Transit 的 verify 端点验证 JWS 编组格式的签名 |
| `EncryptAsync` / `DecryptAsync`（+ `EncryptBatchAsync` / `DecryptBatchAsync`） | 使用 `aes256-gcm96` 密钥进行对称加密；返回应原样存储的 `vault:v{n}:...` 标记 |
| `HmacAsync` / `HmacBatchAsync` | 使用 `hmac` 密钥计算带密钥的 HMAC（盲索引标记） |
| `CreateKeyAsync(keyName, type)` | 创建新的 Transit 密钥（默认：`ecdsa-p256`） |
| `EnsureKeyTypeAsync(keyName, type)` | 以幂等方式确保密钥存在且类型符合要求（类型不匹配时重新创建；Transit 密钥无法原地更改类型） |
| `RotateKeyAsync(keyName)` | 将密钥轮换到新版本 |
| `DeleteKeyAsync(keyName)` | 删除密钥（会先启用 `deletion_allowed`） |
| `ReadKeyAsync(keyName)` | 读取密钥元数据、版本和公钥 |
| `KeyExistsAsync(keyName)` | 检查密钥是否存在 |

`VaultTransitCryptoProvider` 与 .NET 的 `JsonWebTokenHandler` 集成，使 JWT 签名透明地使用 Vault。`VaultTransitSecurityKey` 和 `VaultTransitSignatureProvider` 负责底层集成。

## 邮件 {#email}

配置了 `Email:ResendApiKey` 时，内置的 Resend 发送器会自动启用（同时请设置 `Email:SenderEmail`）。如果没有任何 `IEmailService`，邮件会经由 `NullEmailService` 被丢弃；又因为“邮箱已确认才可登录”的限制默认开启，自助注册的用户将永远无法登录；在这种状态下，`UseAuthagonal()` 会在启动时记录一条醒目的警告。

如需使用其他提供商，请在 `AddAuthagonal()` 之前注册你自己的 `IEmailService`：

```csharp
public sealed class SmtpEmailService(SmtpClient smtp) : IEmailService
{
    public async Task SendVerificationEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Verify your email", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }

    public async Task SendPasswordResetEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Reset your password", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }
}
```

`IEmailService` 还声明了 `SendAccountExistsEmailAsync`（当有人尝试用已注册的邮箱注册时发送，使注册响应保持中立，防止账户枚举）。它带有空操作的默认实现，因此现有实现仍能编译通过。

## 另请参阅 {#see-also}

- [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server)：完整的可运行示例
- [demos/sample-app/](https://github.com/authagonal/authagonal/tree/master/demos/sample-app)：客户端应用示例
