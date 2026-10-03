---
layout: default
title: OIDC 联合
locale: zh-Hans
---

# OIDC 联合

Authagonal 可以将认证联合到外部 OIDC 身份提供方（Google、Apple、Azure AD 等）。这样可以实现“使用 Google 登录”之类的流程，同时 Authagonal 仍然是中心认证服务器。

## 工作原理 {#how-it-works}

进入联合认证有两条路径：

**基于域名（交互式登录）：**

1. 用户在登录页面输入邮箱
2. SPA 调用 `/api/auth/sso-check`，如果该邮箱域名关联了某个 OIDC 提供方，则必须使用 SSO
3. 用户点击“使用 SSO 继续”，被重定向到外部 IdP（如果该邮箱是授权请求的 `login_hint`，且其域名被路由到某个连接，用户会直接前往该 IdP，并转发 `login_hint`）
4. 完成认证后，IdP 重定向回 `/oidc/callback`
5. Authagonal 验证 id_token，关联该用户（如果连接允许 JIT 预配，则创建用户），并设置会话 Cookie

**依赖方提示（`idp_hint`）：**

下游依赖方可以直接路由到指定的上游 IdP，而无需经过邮箱/SSO 域名这一步。在 `/connect/authorize` 后附加 `idp_hint={connectionId}`：

```
/connect/authorize?client_id=my-rp&scope=openid+email&...&idp_hint=google
```

当请求未经认证时，Authagonal 会重定向到 `/oidc/{connectionId}/login`，并将原始的 `/authorize` URL 保存为 `returnUrl`。联合认证完成后，用户会带着会话 Cookie 回到 `/authorize`，流程照常继续。如果连接设置了 `InteractionPath`，用户会先被送到该登录应用页面（参见[联合认证前先收集信息](self-service-sso#collect-something-before-federating)）。设置了 `ShowOnLogin: false` 的连接永远不会作为登录按钮出现，只能通过这种方式访问。

## 设置 {#setup}

### 1. 创建 OIDC 提供方 {#1-create-an-oidc-provider}

**方式 A，配置（推荐用于静态部署）：**

添加到 `appsettings.json`：

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

提供方会在启动时预置。`ConnectionId`、`MetadataLocation`、`ClientId` 和 `ClientSecret` 为必填项（缺少它们启动会失败）。`RedirectUrl` 出于兼容性而被接受，但会被忽略：重定向 URI 按每个请求推导为 `{Issuer}/oidc/callback`，因为它必须位于浏览器当前所在的源上，而这也是需要在 IdP 处注册的 URI（预置的值如果不同，会被记录为已忽略）。`ClientSecret` 通过 `ISecretProvider` 保护（配置了 Key Vault 时使用 Key Vault，否则为明文）。SSO 域名映射会根据 `AllowedDomains` 自动注册，但组织范围的连接除外，其域名只在所属组织内匹配。

预置配置也可以设置下表中的每一个行为标志。**预置条目会在每次启动时替换已存储的连接**：你省略的标志会恢复为默认值，因此请在配置中写明所有希望保留的标志（只有 `ConnectionName`、`IconUrl` 和 `OrganizationId` 在省略时会保留，`CreatedAt` 也会保留）。

| 字段 | 默认值 | 作用 |
|---|---|---|
| `JitProvisioningEnabled` | `false` | 在未知的联合用户首次登录时创建该用户。关闭时，未知用户会以 `access_denied` 被拒绝 |
| `AllowUninvitedJit` | `false` | 在声明了 `ProvisioningAttributeParams` 的情况下，也为不带该上下文到达的用户进行预配。参见[自助 SSO](self-service-sso) |
| `ProvisioningAttributeParams` | 无 | 从授权请求中捕获、作为预配属性写入 JIT 预配用户的查询键（与 `PassthroughParams` 方向相反） |
| `PassthroughParams` | 无 | 转发到上游授权 URL 的查询键，参见[透传查询参数](#passthrough-query-parameters) |
| `SessionExpClaim` | 无 | 参见[会话生命周期上限](#session-lifetime-cap) |
| `ShowOnLogin` | `true` | `false` 会隐藏“通过……继续”按钮；只能通过 `idp_hint` 访问该连接 |
| `ChallengeMfaAfterLogin` | `true` | `false` 表示信任上游自身的 MFA，并跳过本地质询 |
| `IsExternalConnection` | `false` | 将其标记为客户自有的第三方 IdP。即使设置了 `UseUpstreamSubjectAsUserId` 和 `AutoLinkExistingByEmail`，也会使其失效 |
| `UseUpstreamSubjectAsUserId` | `false` | JIT 用户的本地 ID 使用上游 `sub`，而不是新生成的 GUID。仅限第一方连接 |
| `AutoLinkExistingByEmail` | `false` | 即使 `AllowedDomains` 没有覆盖该域名，也按邮箱关联到现有的本地账户。仅限第一方连接 |
| `RevalidateOnRefresh` | `false` | 参见[联合会话](federated-sessions) |
| `InteractionPath` | 无 | 在对 `idp_hint` 请求进行联合认证之前显示的登录应用路径（必须以 `/` 开头） |
| `OrganizationId` | 无 | 将连接限定在某个组织范围内，参见[自助 SSO](self-service-sso#organisation-scoped-connections) |

> **位于你自己私有网络中的 IdP。** `MetadataLocation` 必须是 https，并且默认必须解析为可公开路由的地址：对于它获取的每个 URL，Authagonal 都会拒绝内部目标，在 URL 层面检查一次，在套接字层面再检查一次。若要与本地部署的 IdP 进行联合，请在 [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard) 中列出它。这会覆盖整个交互过程，包括发现文档中指定的 `token_endpoint`、`userinfo_endpoint` 和 `jwks_uri`。https 仍然是必需的：该文档提供了用于验证每个上游 `id_token` 的密钥，而私有网络并不是安全信道。

**方式 B，管理 API（用于运行时管理）：**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"],
    "jitProvisioningEnabled": true
  }'
```

创建请求正文接受 `connectionName`、`metadataLocation`、`clientId` 和 `clientSecret`（均为必填），以及 `iconUrl`、`redirectUrl`（会被忽略，可选）、`organizationId`、`allowedDomains`、`passthroughParams`、`jitProvisioningEnabled`（默认 `false`）、`challengeMfaAfterLogin`（默认 `true`）和 `interactionPath`。连接 ID 由服务器生成，并在 `201` 响应正文中返回（客户端密钥永远不会返回）。`metadataLocation` 必须是 https，并会在创建时接受出站获取防护的检查。上表中的其他标志（`SessionExpClaim`、`ShowOnLogin`、`IsExternalConnection`、`RevalidateOnRefresh` 等）无法通过创建路由设置：请通过配置预置，或者在宿主代码中通过 `IOidcProviderStore` 写入。OIDC 连接没有更新路由；如需修改，请删除后重新创建（或编辑预置配置）。`GET /api/v1/oidc/connections/{connectionId}` 和 `DELETE` 构成了完整的操作集。

### 2. SSO 域名路由 {#2-sso-domain-routing}

指定了 `AllowedDomains` 时（在配置中或通过创建 API），SSO 域名映射会自动注册。即使没有域名路由，仍然可以通过 `/oidc/{connectionId}/login` 将用户引导到 OIDC 登录。

## 端点 {#endpoints}

| 端点 | 说明 |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...&loginHint=...` | 发起 OIDC 登录。生成 PKCE + state + nonce，从 `returnUrl` 推导上游作用域和透传参数，重定向到 IdP 的授权端点（如果存在 `loginHint`，会以 `login_hint` 的形式发送给上游）。未知连接返回 `404`。 |
| `GET /oidc/callback` | 处理 IdP 回调。用授权码兑换令牌，验证 id_token，将每个非协议声明以 `federated:*` 的形式捕获到 Cookie 中，创建用户或让用户登录。 |

## 作用域与声明的传递 {#scope-and-claim-flow-through}

下游依赖方在 `/connect/authorize` 请求的作用域集合会转发给上游 IdP，**但会过滤为标准 OIDC 集合**：`openid`、`profile`、`email`、`address`、`phone`，并且始终包含 `openid`。依赖方请求的其他任何内容（自定义 API 作用域、`offline_access` 等）都会在调用上游之前被丢弃（唯一的例外是设置了 `RevalidateOnRefresh` 的连接，它会重新加入 `offline_access`，以便获取上游刷新令牌）：像 Google 这样严格的 IdP 遇到未知值会返回 `invalid_scope`，而且上游只需要识别用户，依赖方自身的作用域体现在 Authagonal 签发的令牌上，而不是上游令牌上。上游 IdP 根据作用域放入 id_token 的任何声明都会返回给 Authagonal，以 `federated:<name>` 声明的形式暂存在 Cookie 票据中，并在下一次经过 `/connect/authorize` 时传入 `OidcSubject.FederationClaims`。之后 `ProtocolTokenService` 会在 Authagonal 签发的令牌上重新输出它们，并受与限制 `CustomAttributes` 相同的 `Scope.UserClaims` 允许列表约束。键冲突时，以 Authagonal 自身用户存储中的值为准：这些声明是从上游 IdP 原样传来的，如果允许它们覆盖，客户控制的 IdP 就可以对其自身用户重新声明任何通过作用域释放的声明，并压过本服务器对该用户的记录。没有对应存储值的上游声明仍会传递下去。

最终效果：无需按连接维护需要保留的声明的允许列表。上游放在 id_token 上的每个非协议声明都会被捕获；其中哪些能到达下游令牌，由下游作用域的 `UserClaims` 控制，在那里声明该声明，其值就会传递下去。

`FederationClaims` 在刷新轮换后会保留下来，与 `CustomAttributes` 相互独立，因此每个会话的联合上下文（例如在最初授权时捕获的共享链接令牌）会保持不变，而每个用户的属性仍会从用户存储中重新读取最新值。

## 透传查询参数 {#passthrough-query-parameters}

`OidcProviderConfig.PassthroughParams` 是按连接设置的查询键允许列表，这些键会从原始 `/authorize` 请求传递到上游 IdP 的授权 URL 上。标准集合（`scope`、`state`、`nonce`、PKCE）始终会被转发；该设置用于依赖方指定的额外值，例如上游进行认证所需的一次性凭据（例如共享链接类 IdP 使用的 `link_token`）。

当某个键在允许列表中时，Authagonal 会从原始 `/authorize` 查询（经由 `returnUrl` 携带）中取出它的值，并附加到上游 URL 上。不在允许列表中的任何内容都会被静默丢弃。

## 会话生命周期上限 {#session-lifetime-cap}

`OidcProviderConfig.SessionExpClaim` 是一个可选的 id_token 声明名称（Unix 秒），该声明的值为本地会话的生命周期设定上限。存在时，上游的值会以 `session_max_exp` 的形式随 Cookie 票据传递，并进入签发的授权码；访问令牌 / ID 令牌 / 刷新令牌都会被限制，使任何令牌（包括由轮换签发的令牌）都不会比上游会话存活得更久。当上游 IdP 实施的会话期限比 Authagonal 默认的更短时，这非常有用。

## 安全特性 {#security-features}

- **PKCE**：每个授权请求都使用 S256 的 code_challenge
- **nonce 验证**：nonce 与 state 一起存储，必须出现在 id_token 中并且一致
- **state 验证**：一次性使用（通过 `IOidcStateStore` 原子消费，带过期时间持久化）**并且绑定浏览器**：登录时会设置一个作用于 `/oidc` 的 `SameSite=Lax` Cookie，它必须与回调中的 `state` 一致，因此攻击者无法把自己发起的联合流程的回调 URL 交给受害者来完成（登录 CSRF）
- **id_token 签名验证**：密钥从 IdP 的 JWKS 端点获取；验证签发者、受众和有效期
- **userinfo 后备**：如果 id_token 中不含邮箱，会尝试 userinfo 端点。userinfo 的 `sub` 必须与 id_token 的 `sub` 一致（OIDC Core 5.3.2），否则忽略该响应
- **稳定的身份关联**：回访用户按提供方 + `sub` 解析，绝不仅凭邮箱。要按邮箱将联合身份关联到**已存在的**本地账户，需要连接的 `AllowedDomains` 覆盖该邮箱的域名（即管理员明确担保该 IdP 拥有该域名），或者在第一方连接上设置 `AutoLinkExistingByEmail`；当该域名被路由到其他连接时，关联会被拒绝。已经绑定到其他连接联合身份的账户，只有在当前连接是该域名的权威连接时才会被接管，此时旧的绑定会被移除。上游声明的 `email_verified` *不足以*夺取一个现有账户
- **域名强制**：设置了 `AllowedDomains` 时，该连接只能声明这些域名内的身份（否则返回 `access_denied`）
- **JIT 需显式启用**：除非连接设置了 `JitProvisioningEnabled`，否则未知用户会以 `access_denied` 被拒绝。即使 JIT 适用，不声明 `email_verified` 的上游也无法创建账户，邮箱域名被路由到其他连接的连接同样不能
- **开放重定向防护**：`returnUrl` 必须是同站点的相对路径；协议相对形式（`//`）和反斜杠形式都会被拒绝
- **默认仍然适用本地 MFA**：联合认证只证明第一因素。已注册 MFA（或其客户端策略要求 MFA）的用户在回调之后会被引导到本地 MFA 质询/设置页面，而不是直接登录；只有完成之后，会话才会带有 MFA 标记。设置了 `ChallengeMfaAfterLogin: false` 的连接会跳过这一步，仅凭联合认证就让用户以已通过 MFA 认证的状态登录
- **对元数据的信任范围很窄**：发现文档必须是 https，其 URL 必须与它所声明的签发者绑定，并且上游 id_token 只接受非对称签名算法（RS/PS/ES 256、384、512）
- **组织绑定**：通过组织范围的连接登录的用户会成为该组织的成员，会话会携带该组织的 `org_id`

## Azure AD 的特殊情况 {#azure-ad-specifics}

Azure AD 有时会在 `emails` 声明中以 JSON 数组的形式返回邮箱（尤其是 B2C）。Authagonal 会同时检查 `email` 声明和 `emails` 数组（JSON 数组或单个字符串）来处理这种情况。

## 支持的提供方 {#supported-providers}

任何符合 OIDC 规范并支持以下功能的提供方：
- 授权码流程
- PKCE（S256）
- 发现文档（`.well-known/openid-configuration`）

已测试：
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C

## 相关指南 {#related-guides}

- [自助 SSO](self-service-sso)：JIT 预配模式（仅限邀请与自助）、连接信任级别以及联合认证前的中间页面。
- [联合会话](federated-sessions)：通过 `RevalidateOnRefresh` 将上游的撤销传递到本地会话。
- [升级用户](user-upgrade)：让联合账户 / 访客账户获得第一方密码。
