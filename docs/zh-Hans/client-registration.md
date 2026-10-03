---
layout: default
title: 动态客户端注册
locale: zh-Hans
---

# 动态客户端注册

Authagonal 实现了 **OAuth 2.0 动态客户端注册**（[RFC 7591](https://datatracker.ietf.org/doc/html/rfc7591)），允许客户端应用在运行时自行注册，无需管理员介入。

## 启用该端点 {#enabling-the-endpoint}

动态注册**默认处于禁用状态**。通过配置显式启用：

```json
{
  "Auth": {
    "DynamicClientRegistrationEnabled": true
  }
}
```

也可以将 `Auth__DynamicClientRegistrationEnabled=true` 设置为环境变量。多租户宿主可以通过 `ITenantContext.DynamicClientRegistrationEnabled` 按租户覆盖此设置：租户自身的取值优先，`null` 则回退到宿主级别的选项。

启用后，发现文档会公布该端点：

```
GET /.well-known/openid-configuration
```
```json
{
  "registration_endpoint": "https://auth.example.com/connect/register"
}
```

## 注册客户端 {#registering-a-client}

```
POST /connect/register
Content-Type: application/json

{
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "token_endpoint_auth_method": "client_secret_basic",
  "scope": "openid profile email offline_access",
  "audiences": ["https://api.myapp.example.com"],
  "allowed_cors_origins": ["https://myapp.example.com"],
  "backchannel_logout_uri": "https://myapp.example.com/oidc/backchannel",
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

### 响应 {#response}

```
HTTP/1.1 201 Created
Content-Type: application/json

{
  "client_id": "a1b2c3d4e5f6...",
  "client_secret": "xkCd2_base64url...",
  "client_id_issued_at": 1745000000,
  "client_secret_expires_at": 0,
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "scope": "openid profile email offline_access",
  "token_endpoint_auth_method": "client_secret_basic"
}
```

`client_secret` 只返回**一次**，之后无法再次获取。请妥善保存。响应带有 `Cache-Control: no-store`。`client_id` 是 32 个小写十六进制字符，`client_secret_expires_at` 始终为 `0`（密钥不会过期）。公共客户端（`none`）和 `private_key_jwt` 客户端在响应中不会获得 `client_secret`。响应只回显上面列出的字段：`audiences`、`jwks`、`jwks_uri`、`allowed_cors_origins` 以及各注销字段会被存储，但不会返回。

## 请求参数 {#request-parameters}

| 参数 | 是否必填 | 说明 |
|---|---|---|
| `client_name` | 否 | 省略时默认为生成的 `client_id` |
| `redirect_uris` | 视情况而定 | 当 `grant_types` 包含 `authorization_code` 时必填。必须是绝对 URI；`javascript:`/`data:`/`vbscript:`/`file:` 协议会被拒绝（用于移动端深层链接的原生自定义协议没有问题）。带有片段的 URI 会被拒绝（RFC 6749 §3.1.2），明文 `http` 仅接受回环主机（RFC 8252 §7.3）。最多 20 个条目，每个最长 2048 个字符。 |
| `post_logout_redirect_uris` | 否 | 注销后有效的重定向目标。与 `redirect_uris` 一样受 20 个条目 / 2048 个字符的限制。 |
| `grant_types` | 否 | 默认为 `["authorization_code"]`。**只能注册 `authorization_code` 和 `refresh_token`**：`client_credentials`、`implicit`、设备授权以及任何其他授权类型都会以 `invalid_client_metadata` 被拒绝，因此开放注册永远无法创建机器对机器客户端。如果请求了 `offline_access`，会自动添加 `refresh_token`。 |
| `token_endpoint_auth_method` | 否 | `client_secret_basic`（默认）、`client_secret_post`、`private_key_jwt`，或公共客户端使用的 `none`。其他任何值都会以 `invalid_client_metadata` 被拒绝。 |
| `jwks` / `jwks_uri` | 使用 `private_key_jwt` 时 | 客户端的公钥。`private_key_jwt` 要求二者之一（否则返回 `invalid_client_metadata`）；`jwks_uri` 必须通过出站 URL 防护检查（必须是外部地址）。`private_key_jwt` 客户端不会被颁发密钥。 |
| `scope` | 否 | 以空格分隔的作用域。只有五个 OIDC 内置作用域（`openid`、`profile`、`email`、`phone`、`offline_access`）以及 `Auth:DynamicClientRegistrationScopes` 中列出的作用域可以注册：仅仅存在于作用域存储中**并不**足够（参见 [作用域](scopes)）。受角色限制的作用域和管理作用域（`AdminApi:Scope`，默认 `authagonal-admin`）永远无法注册。 |
| `audiences` | 否 | 添加到访问令牌中的 JWT `aud` 值。最多 20 个条目，每个最长 512 个字符，且必须是不带片段的绝对 URI；无效值返回 `invalid_client_metadata`。 |
| `allowed_cors_origins` | 否 | 每个条目都必须是有效的源（否则返回 `invalid_client_metadata`），但该值**不会按原样存储**：客户端允许的源是从其自身 `https` `redirect_uris` 的源推导出来的，因此注册方只能访问它已经通过重定向 URI 证明过的源。 |
| `backchannel_logout_uri` | 否 | 启用[后端通道注销](index#key-features) |
| `frontchannel_logout_uri` | 否 | 启用[前端通道注销](front-channel-logout) |
| `frontchannel_logout_session_required` | 否 | 默认为 `true`；为 `true` 时，注销 URL 会携带 `iss` 和 `sid` 参数 |

## 默认值与不变式 {#defaults--invariants}

- **要求 PKCE**：对于动态注册的客户端，`RequirePkce` 始终为 `true`。
- **要求同意**：`RequireConsent` 始终为 `true`，因此即使在静态预置的客户端会跳过同意页面的情况下，用户也会看到自注册客户端的同意页面。
- **公共客户端**：`token_endpoint_auth_method: "none"` 会创建一个没有密钥的客户端。仍然要求 PKCE。
- **离线访问**：请求 `offline_access` 作用域会隐式地向 `grant_types` 添加 `refresh_token`。

## 错误响应 {#error-responses}

| HTTP | `error` | 原因 |
|---|---|---|
| `400` | `invalid_redirect_uri` | `redirect_uris` 中的某一项不是有效的绝对 URI、使用了 script/data/file 伪协议、带有片段、是指向非回环主机的明文 `http`，或者（对于两个 URI 列表中的任一个）长度超过 2048 个字符 |
| `400` | `invalid_client_metadata` | 请求了不可注册的授权类型、需要 `redirect_uris` 的授权类型缺少该字段、`token_endpoint_auth_method` 不受支持、`private_key_jwt` 没有 `jwks`/`jwks_uri`（或 `jwks_uri` 不安全）、`audiences` 无效、`allowed_cors_origins` 中的某个条目不是源，或者某个注销 URI 不是外部地址 |
| `400` | `invalid_scope` | 请求的作用域既不是内置的，也没有注册 |
| `400` | `invalid_client_metadata` | `redirect_uris` / `post_logout_redirect_uris` 超过 20 个 |
| `403` | `invalid_scope` | 请求的作用域不可注册：不在 `Auth:DynamicClientRegistrationScopes` 中，或受角色限制 |
| `403` | `invalid_scope` | 请求了管理作用域，它永远无法通过注册获得 |
| `403` | `invalid_scope` | 已注册的 `IClientScopeGuard` 拒绝了某个请求的作用域（传给它的是匿名调用方） |
| `403` | `not_supported` | 动态客户端注册未启用 |
| `429` | `rate_limited` | 来自该 IP 的注册次数过多（每小时 10 次） |

## 安全注意事项 {#security-considerations}

注册端点**无需认证**，但在设计上受到以下约束：

- **限流**：每个来源地址在滚动的一小时内最多注册 10 次（`429 rate_limited`），因此客户端存储不会被灌满。所用地址是调用方无法自行选择的那个（不会盲目信任转发的值）。
- **授权类型受限**：只有 `authorization_code` + `refresh_token`；注册的客户端始终需要用户参与的流程，永远不能充当机器对机器客户端。
- **作用域采用允许列表，而非继承**：注册方只能声明五个 OIDC 内置作用域，除非运营方在 `Auth:DynamicClientRegistrationScopes` 中列出了某个作用域。存在于作用域存储中并不等于获得许可：一个作用域之所以存在，是因为某个客户端需要它，而不是因为每个匿名注册方都可以认领它。
- **管理作用域保留**：`authagonal-admin` 作用域（或 `AdminApi:Scope` 设置的任何值）会被拒绝，因此注册永远无法产生一个能访问[管理 API](admin-api) 的客户端。
- **注销 URI 经过校验**：`backchannel_logout_uri` 和 `frontchannel_logout_uri` 会由服务器去访问，因此它们必须是外部 http(s) 端点：回环、RFC1918、链路本地（包括云元数据地址）以及 `.internal`/`.local` 主机都会被拒绝。
- **记录大小有界**：最多 20 个重定向 URI，每个最长 2048 个字符，因此无法利用一次注册来撑大客户端存储。
- **CORS 源是推导出来的，而非直接信任**：存储的源来自客户端自身的 `https` 重定向 URI，绝不来自请求正文。
- 已注册的客户端**始终要求 PKCE**，并且**始终要求同意**。

它**不**约束的是受众，除非注册方主动声明。RFC 7591 中没有对应的字段，因此标准的注册请求完全不包含 `audiences`（Authagonal 的扩展字段）：该客户端从未被问及此项，其列表处于“未设置”状态，它可以在授权端点上将任何绝对 URI 指定为其 `resource`，并获得一个以该值作为 `aud` 的令牌。这是有意为之（MCP 授权规范要求客户端将 MCP 服务器指定为资源，而 MCP 客户端就是 DCR 客户端），这也使得资源服务器需要基于 `scope` 进行授权，而不是基于 `iss` + `aud` + `sub`。**发送** `audiences`，即使是空列表，也算作给出了答复，并会把客户端固定在该值上：非空列表就是 `resource` 的允许列表，而显式的 `[]` 表示该客户端完全不能指定资源。令牌交换是例外：在令牌交换中，未设置的 `Audiences` 会直接拒绝，因此已注册的客户端无法让交换得到的令牌指向任何地方。参见[受众与资源指示符](configuration#audiences-and-resource-indicators-rfc-8707)。

如需更严格的准入控制（初始访问令牌、mTLS、软件声明），请在该端点前面加上你自己的中间件或 `IAuthHook`。在不需要自助注册的环境中，可以考虑完全禁用动态注册，并通过管理 API 管理客户端。
