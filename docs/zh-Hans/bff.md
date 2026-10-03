---
layout: default
title: 前端专属后端（BFF）
locale: zh-Hans
---

# 前端专属后端（BFF）

浏览器中的 SPA 如果把访问令牌或刷新令牌保存在 JavaScript 可以访问的存储中，两者都会暴露在 XSS 攻击之下。BFF 是一个**托管在你自己后端上的机密 OIDC 客户端**。它在服务器端执行授权码 + PKCE 流程，把令牌保存在服务器端会话中，只给浏览器一个 httpOnly 会话 Cookie。SPA 对你的 API 的调用会经过 BFF 的代理，代理在转发时附加会话的访问令牌。

它有两个实现，使用同一套协议：

| 包 | 适用于 | 源码 |
|---|---|---|
| `Authagonal.Bff`（NuGet） | ASP.NET Core 宿主 | `src/Authagonal.Bff/` |
| `@authagonal/bff`（npm） | Express 和 Next.js（App Router） | `bff-lib/` |

BFF 是认证宿主的一个普通机密客户端：它使用 OIDC 发现，以及授权、令牌、撤销和结束会话端点，因此它对认证宿主的唯一要求就是一个已注册的客户端。

## 1. 注册 BFF 客户端 {#1-register-a-bff-client}

该客户端必须是**机密**客户端（拥有密钥），必须要求 PKCE；如果你需要在服务器端刷新令牌，还必须允许 `offline_access`。注册以下内容：

- 重定向 URI `https://app.example.com/bff/callback`
- 注销后重定向 URI `https://app.example.com/`（如果注销时使用 `returnUrl`，还需注册 `https://app.example.com/bff/logout-callback`，参见[注销](#logout)）

如需通过[后端通道注销](index#key-features)实现针对整个主体的“在所有地方注销”，请以 `BackChannelLogoutSessionRequired = false` 注册该客户端。BFF 接受携带 `sid` 或仅携带 `sub` 的注销令牌。

## 2. 接入（.NET） {#2-wire-it-up-net}

```csharp
builder.Services.AddAuthagonalBff(o =>
{
    o.Authority    = "https://auth.example.com";
    o.ClientId     = builder.Configuration["Bff:ClientId"]!;
    o.ClientSecret = builder.Configuration["Bff:ClientSecret"]!;
    o.Scope        = ["openid", "profile", "email", "offline_access"];
    o.PostLogoutRedirectUri = "https://app.example.com/";
});

var app = builder.Build();
app.UseForwardedHeaders();   // required behind a reverse proxy or ingress
app.MapAuthagonalBff();
app.MapFallbackToFile("index.html");
app.Run();
```

`UseForwardedHeaders` 很重要：在终止 TLS 的代理之后，BFF 看到的是普通 http，因此如果不调用它，`__Host-` 会话 Cookie 写出时将不带 `Secure`，浏览器会直接丢弃它。关于如何声明代理，请参见[安装](installation#production-security-checklist)。

### Node（Express） {#node-express}

```ts
import { authagonalBff } from '@authagonal/bff/express';

app.set('trust proxy', 1);
app.use(authagonalBff({
  authority: 'https://auth.example.com',
  clientId: process.env.BFF_CLIENT_ID!,
  clientSecret: process.env.BFF_CLIENT_SECRET!,
  scope: ['openid', 'profile', 'email', 'offline_access'],
  cookieSecret: process.env.BFF_COOKIE_SECRET!,   // encrypts the session cookie
  postLogoutRedirectUri: 'https://app.example.com/',
}));
```

对于 Next.js，请在 `app/bff/[...bff]/route.ts` 中使用 `@authagonal/bff/next` 的 `createBffRoute`。两者的用法都见 `bff-lib/README.md`。

## 端点 {#endpoints}

挂载在 `BasePath`（默认 `/bff`）之下。

| 路由 | 用途 |
|---|---|
| `GET /bff/login?returnUrl=/` | 开始登录：设置一个每次登录专用的关联 Cookie，并携带 PKCE（`S256`）、`state` 和 `nonce` 重定向到 `/connect/authorize`。 |
| `GET /bff/callback` | OIDC 重定向 URI（`CallbackPath`）。兑换授权码并创建会话。 |
| `GET /bff/user` | `{ isAuthenticated, claims, sessionExpiresAt }`。需要防伪请求头。始终返回 `Cache-Control: no-store`。 |
| `GET\|POST /bff/logout` | 在本地和认证宿主处结束会话。`POST` 需要防伪请求头；`GET` 是普通的页面导航。 |
| `GET /bff/logout-callback` | 当注销时提供了 `returnUrl`，结束会话往返流程完成后的落地页。 |
| `POST /bff/backchannel-logout` | 用于 OIDC 后端通道注销的服务器到服务器接收端。通过签名的注销令牌进行认证，因此不需要 CSRF 请求头。 |
| `GET /bff/ws-ticket` | 需显式启用（`WsTicketsEnabled`），仅限 .NET。参见 [WebSocket 认证](websocket-auth)。 |
| `GET /bff/token?resource=...` | 需显式启用（`TokenEndpointEnabled`），仅限 .NET。参见[面向其他源的交换令牌](#exchanged-tokens-for-another-origin)。 |
| `ANY /bff/api/**` | 注入令牌的代理。仅当 `Upstreams` 非空时才会映射。 |

`/bff/user` 中的 `claims` 是 id_token 声明组成的扁平字符串映射，其中去掉了协议层面的字段（`iss`、`aud`、`exp`、`iat`、`nbf`、`nonce`、`at_hash`、`c_hash`、`s_hash`、`azp`、`jti`、`sid`、`auth_time`、`acr`、`amr`、`typ`）。`roles` 和 `groups` 等数组声明以空格连接。每次刷新得到的 id_token 都会重新读取声明，因此登录后才授予的角色会在下一次刷新时到达 SPA，而不必等到下一次登录。

## 从浏览器调用 {#from-the-browser}

每个非导航调用都携带一个固定的请求头，它与 `SameSite=Lax` 一起防御 CSRF。任何值都可以接受；只检查该请求头是否存在。

```js
const me = await fetch('/bff/user', { headers: { 'X-Authagonal-Bff': '1' } }).then(r => r.json());
if (!me.isAuthenticated) location.href = '/bff/login?returnUrl=' + encodeURIComponent(location.pathname);
```

登录和注销应通过**页面导航**（`location.href = '/bff/login'`）完成，而不是通过 `fetch`。请求头名称由 `AntiForgeryHeader` 设置。

## 代理 {#the-proxy}

配置上游后，SPA 调用 `/bff/api/<prefix>/...`：

```csharp
o.Upstreams.Add(new BffUpstream
{
    Prefix = "/orders",
    TargetBaseUrl = "https://api.internal.example.com",
});
```

代理要求防伪请求头和一个有效的会话；如果访问令牌距离过期不足 `RefreshThresholdSeconds`，会先刷新它；然后携带 `Authorization: Bearer` 转发请求，并以流式方式返回响应。会话 Cookie 永远不会被转发。入站的 `X-Forwarded-*`、`Forwarded` 和 `X-Real-IP` 请求头会被剥离，并根据 BFF 自身的状态重新设置，因此 SPA 中的脚本无法为客户端 IP 或协议作担保。上游返回的重定向会转交给浏览器，而不是由代理跟随。

每个上游（`BffUpstream`）的属性：

| 属性 | 含义 |
|---|---|
| `Prefix` | `/bff/api` 之后用于选中该上游的路径。 |
| `TargetBaseUrl` | 匹配的请求被转发到的地址。 |
| `StripPrefix` | 在拼接到目标地址之前去掉已匹配的前缀。可让一个 BFF 分发到共享同一路径命名空间的多个后端。 |
| `RequiredAuthority` | `"type:action"` 对。代理会检查出站令牌的 RFC 9396 `authorization_details`，除非每一对都被允许，否则返回 403。参见[智能体授权](agentic-auth)。 |
| `AuthorityLocation` | 当该上游对外使用的 `locations` 根与 `TargetBaseUrl` 不同时，指定该根。 |
| `StrictAuthority` | 当授权中带有代理无法求值的约束时拒绝该调用，而不是将其放行。 |

相关选项：`AllowAnonymousProxyRequests` 会把没有会话（或会话无法刷新）的请求在不带 `Authorization` 请求头的情况下转发，而不是返回 401，适用于自行决定访问控制的 API。受 `RequiredAuthority` 保护的路由永远不会以匿名方式转发。`ExchangeRoutes` 将代理路由绑定到 [RFC 8693 令牌交换](agentic-auth)，使上游收到的是范围更窄、绑定上下文的令牌，而不是会话的主令牌：每个路由都有一个恰好包含一个占位符的 `PathPattern`（唯一支持的约束是 `:guid`），捕获到的路径段作为交换参数发送，交换被拒绝时返回 403。未知的约束会在启动时失败，而不是悄悄转发权限更宽的令牌。

## 注销 {#logout}

`/bff/logout` 会撤销会话的刷新令牌（尽力而为）、删除会话、清除 Cookie，然后携带会话的 `id_token_hint` 重定向到认证宿主的结束会话端点。如果没有会话，认证宿主处也就没有需要结束的会话，因此会直接重定向到 `PostLogoutRedirectUri`。如果提供了 `returnUrl`，认证宿主会重定向回 `/bff/logout-callback`，后者会根据 `ReturnUrlAllowlist` 重新校验目标地址，然后重定向过去。请将该回调地址注册为客户端的注销后重定向 URI。

后端通道注销在服务器端删除会话：如果注销令牌带有 `sid`，则按 `sid` 删除，否则删除该 `sub` 的所有会话。删除范围限定在签发该令牌的签发者所属的租户内，因为 `sub` 只在同一签发者内唯一。注销令牌必须携带 `iat`，并且必须是近期签发的。

## 选项参考（.NET） {#options-reference-net}

| 选项 | 默认值 | 说明 |
|---|---|---|
| `Authority`、`ClientId`、`ClientSecret` | 必填 | 设置了 `TenantQueryParam` 时不是必填。 |
| `Scope` | `openid profile offline_access` | `offline_access` 用于启用刷新。 |
| `BasePath` | `/bff` | |
| `CallbackPath` | `/bff/callback` | 必须与已注册的重定向 URI 一致。 |
| `CookieName` | `__Host-agbff` | `__Host-` 前缀强制要求 Secure、`Path=/` 且不设置 Domain，因此需要 https。本地 http 开发时可覆盖此值。 |
| `SessionLifetime` | 8 小时 | 绝对上限，不受刷新影响。 |
| `PersistentCookie` | `false` | 为 true 时，Cookie 会获得一个不超过 `SessionLifetime` 的 `Max-Age`，在浏览器重启后依然保留（“保持登录”）。后端通道注销仍会结束该会话。 |
| `CorrelationLifetime` | 30 分钟 | 从 `/bff/login` 到回调之间，一次登录允许持续的时长。涵盖注册、验证邮件和登录。 |
| `RefreshThresholdSeconds` | 60 | |
| `ReturnUrlAllowlist` | 空 | 非相对路径的 `returnUrl` 可以指向的源。相对路径始终允许；其他任何值都会变成 `/`。 |
| `LoginPassthroughParams` | 空 | 从 `/bff/login` 复制到 `/connect/authorize` 的查询参数名（例如 `idp_hint`）。标准参数始终优先。 |
| `AntiForgeryHeader` | `X-Authagonal-Bff` | |
| `PostLogoutRedirectUri` | 无 | |
| `WsTicketsEnabled`、`WsTicketLifetime`、`TicketExchangeParams` | 关闭、30 秒、空 | 参见 [WebSocket 认证](websocket-auth)。 |
| `TokenEndpointEnabled`、`TokenEndpointResources`、`TokenEndpointExchangeParams` | 关闭、空、空 | 启用它但不配置任何资源会在启动时失败。 |
| `Upstreams`、`ExchangeRoutes`、`AllowAnonymousProxyRequests` | 空、空、`false` | 参见[代理](#the-proxy)。 |
| `TenantQueryParam` | 无 | 多租户模式，见下文。 |

`SessionMode` 只实现了 `Store`；`Stateless` 为保留值，使用时会在启动时失败。

Node 包接受以下选项的 camelCase 对应形式：`authority`、`clientId`、`clientSecret`、`scope`、`basePath`、`callbackPath`、`cookieName`、`refreshThresholdSeconds`、`returnUrlAllowlist`、`postLogoutRedirectUri`、`antiForgeryHeader`、`sessionLifetimeSeconds`、`upstreams` 和 `tenantQueryParam`，另外还有 `cookieSecret`、`sessionStore`、`cookieProtector`、`tenantResolver` 和 `clientIp`。它不提供 WebSocket 票据端点和令牌端点。

## 会话与运行多个实例 {#sessions-and-running-more-than-one-instance}

会话通过 `IBffSessionStore` 存储。默认实现是 `IDistributedCache`，除非你在 `AddAuthagonalBff` **之前**注册了真正的缓存（例如 Redis），否则它保存在内存中。

仅有共享缓存还不够。刷新的并发去重（同一时刻只执行一次刷新）仅在单个进程内生效，而会话及其轮换的刷新令牌保存在共享缓存中。两个副本可能读取到同一个会话，都发现需要刷新，于是都去兑换同一个刷新令牌。认证宿主会把第二次兑换视为被盗令牌的重放，从而撤销整个授权族，使该用户在所有地方被注销。请通过以下两种方式之一提供跨副本的锁：

- **注册一个 `ILeaseProvider`**（任何后端均可）。Azure、AWS 和 SQL 提供程序通过 `AddAuthagonalClustering` 提供了现成实现。参见[扩展](scaling)。
- **在你的会话存储上实现 `IBffRefreshLockStore`**（`TryAcquireRefreshLockAsync(sessionId, ttl)` 和 `ReleaseRefreshLockAsync`）。它是一个带 TTL 的条件写入，例如 Redis 上的 `SET NX PX`。默认存储无法提供它，因为 `IDistributedCache` 没有“不存在时才写入”的操作。Node 会话存储提供了等价的 `acquireRefreshLock` / `releaseRefreshLock`。

如果两者都没有，部署就只能依赖认证宿主的 `Auth:RefreshTokenReuseGraceSeconds`，而它在 Server 宿主中默认为 0（严格）。当会话存储看起来是共享的且没有锁时，BFF 会在启动时记录一条警告。

自定义的 `IBffSessionStore` 必须遵循 `RemoveBySidAsync` 和 `RemoveBySubjectAsync` 上的 `tenantKey` 参数。其他扩展点是 `ICookieProtector`（默认：ASP.NET Data Protection）和 `ITokenClient`。

## 一个 BFF 服务多个租户 {#many-tenants-from-one-bff}

设置 `TenantQueryParam`（例如 `"slug"`）并注册一个 `IBffTenantResolver`。`/bff/login?slug=acme` 会解析出该租户的 `BffTenantConfig`（authority、客户端 ID、密钥、作用域），该键会保存在会话中，以便后续请求重新解析；后端通道注销则通过 `ResolveByIssuerAsync` 根据令牌的 `iss` 解析租户。未设置 `TenantQueryParam` 时，BFF 为单租户模式，使用静态选项。

## 面向其他源的交换令牌 {#exchanged-tokens-for-another-origin}

Cookie 模式无法访问位于其他源上的资源服务器（例如 SPA 嵌入的 iframe 应用）。`TokenEndpointEnabled` 会添加 `GET /bff/token?resource=<audience>`，它返回一个 RFC 8693 **交换**令牌的 `{ accessToken, expiresInSeconds }`：该令牌只面向 `TokenEndpointResources` 中的一个资源（其他任何资源都返回 400 `resource_not_allowed`），绑定查询中的任何 `TokenEndpointExchangeParams` 值，并且生命周期很短。浏览器永远不会拿到会话的主令牌，并且应只把交换得到的令牌保存在内存中。租户客户端需要拥有令牌交换授权类型，并且必须把这些资源声明为其受众。交换被拒绝时返回 403。
