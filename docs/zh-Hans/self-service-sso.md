---
layout: default
title: 自助 SSO
locale: zh-Hans
---

# 自助 SSO 接入

当你已经将一个连接[联合](oidc-federation)到客户的 IdP 之后，接下来的问题是：**一个从未登录过的人出现时会发生什么？** 对于这类未知用户，Authagonal 提供了三种模式，从最严格到最开放依次为：拒绝所有未知用户、要求邀请上下文、从允许的域名自动预配；此外还提供了一些控制项，防止*外部* IdP 变成安全隐患。前两种合并在模式 1 中介绍，第三种在模式 2 中介绍。本指南讲的是如何选择并配置你想要的模式。

所有这些都是按连接进行的配置：`OidcProviders` 和 `SamlProviders` 预置配置节、已存储的 `OidcProviderConfig` / `SamlProviderConfig`，以及管理 API。相关的设置项：

| 设置项 | 作用 | 协议 |
|---|---|---|
| `JitProvisioningEnabled` | 是否允许创建未知用户？ | OIDC、SAML |
| `ProvisioningAttributeParams` | 在创建用户之前，要求请求中携带*邀请上下文*。 | OIDC、SAML |
| `AllowUninvitedJit` | 允许在**没有**邀请的情况下自助创建（并标记来源连接）。 | OIDC、SAML |
| `IsExternalConnection` | 标记第三方 IdP，使仅限第一方的标志无法生效。 | 仅 OIDC |
| `InteractionPath` | 在联合*之前*显示一个登录应用页面（姓名/条款）。 | 仅 OIDC |

每个设置项可以在哪里设置很重要，因为管理 API 并没有公开所有设置项：

- **预置配置（`OidcProviders`、`SamlProviders`）：**上述所有适用于该协议的设置项。预置的连接在每次启动时都会根据配置重新应用，因此对于预置的连接，`JitProvisioningEnabled` 和 `AllowUninvitedJit` 来自预置配置，而不是来自上一次存储的值。
- **SAML 管理 API**（`POST` / `PUT /api/v1/saml/connections`）：`JitProvisioningEnabled`、`ProvisioningAttributeParams` 和 `AllowUninvitedJit`。
- **OIDC 管理 API**（`POST /api/v1/oidc/connections`）：`JitProvisioningEnabled` 和 `InteractionPath`（必须以 `/` 开头）。在 OIDC 上，`ProvisioningAttributeParams`、`AllowUninvitedJit` 和 `IsExternalConnection` 只能通过预置配置设置，而且 OIDC 连接没有更新路由。参见[管理 API](admin-api) 和 [OIDC 联合](oidc-federation)。

## 模式 1：仅限邀请（拒绝未受邀者） {#posture-1-invite-only-reject-the-uninvited}

这是默认设置。当 `JitProvisioningEnabled: false` 时，未知的 SSO 用户会被直接拒绝（`access_denied`，“contact your administrator”），当每个用户都必须由管理员或 SCIM 预先创建时，这正是你想要的。

如果你想要 JIT，但*仅*在存在邀请时才启用，请启用 JIT **并且**声明 `ProvisioningAttributeParams`。它们指定了携带邀请上下文的白名单 `/authorize` 查询参数（例如 `acceptKind`、`acceptToken`）。只有当其中至少一个参数确实带着值到达时，未知用户才会被预配；没有邀请的单纯 SSO 登录会以 `access_denied`（“This login requires an invitation”）被拒绝，因此偶然的登录无法悄无声息地自行预配出新账户/组织。这些参数从用户要返回的 `/authorize` URL 的查询中读取（对于 SAML 是 `RelayState`）；OIDC 还会回退到回调请求自身的查询。

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "ConnectionName": "Acme (Entra)",
      "MetadataLocation": "https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration",
      "ClientId": "…", "ClientSecret": "…",
      "AllowedDomains": ["acme.com"],
      "JitProvisioningEnabled": true,
      "ProvisioningAttributeParams": ["acceptKind", "acceptToken"]
    }
  ]
}
```

无需设置 `RedirectUrl`：回调的 `redirect_uri` 按每个请求推导为 `{issuer}/oidc/callback`，因此请在上游 IdP 上注册该 URI。预置的 `RedirectUrl` 会被忽略。

捕获到的参数会写入 JIT 用户的 `CustomAttributes`，并传递给你的[预配 `Try` 处理程序](provisioning)，它才是对这些*值*的真正把关（例如“这个邀请令牌是否与这个电子邮件匹配？”）。Authagonal 负责捕获白名单中的键；由你的预配程序决定它们是否有效。如果 `Try` 返回 `approved: false`，刚创建的用户会被删除，浏览器会收到 `400 provisioning_rejected`。

## 模式 2：自助（自动预配允许域名中的用户） {#posture-2-self-service-auto-provision-an-allowed-domain-user}

如果希望“客户的任何员工都可以直接登录并获得账户”，请设置 `AllowUninvitedJit: true`。这样，来自**允许域名**的未知用户即使没有邀请上下文也会被预配，而且 Authagonal 会为其标记所经由的连接，以便你的预配程序将其放入正确的租户，而不是新建一个租户。只有当 `AllowedDomains` 非空时才会进行域名检查：未列出任何域名的连接会接受其 IdP 断言的任何域名，因此请在每个自助连接上列出域名。

```json
{
  "ConnectionId": "acme-entra",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true,
  "ProvisioningAttributeParams": ["acceptKind", "acceptToken"],
  "AllowUninvitedJit": true
}
```

该标记以 `federated_connection` 自定义属性的形式到达。它的值是连接的 `ConnectionName`（而不是 `ConnectionId`），并且只有在用户创建时没有邀请上下文的情况下才会写入，因此受邀用户携带的是捕获到的参数。你的 `Try` 处理程序据此分支处理：

```javascript
app.post('/provisioning/try', async (req, res) => {
  const { userId, email, customAttributes } = req.body;

  if (customAttributes?.acceptToken) {
    // Invited: validate the invite and add them to that org.
    const org = await validateInvite(customAttributes.acceptToken, email);
    if (!org) return res.json({ approved: false, reason: 'Invalid invite' });
    stage(userId, { orgId: org.id, role: customAttributes.acceptKind ?? 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  if (customAttributes?.federated_connection) {
    // Self-service: no invite, but they came through a known enterprise connection.
    const org = await orgForConnection(customAttributes.federated_connection);
    stage(userId, { orgId: org.id, role: 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  return res.json({ approved: false, reason: 'No invite and no known connection' });
});
```

`AllowUninvitedJit` 需要按连接显式启用：声明了 `ProvisioningAttributeParams` 但**没有**设置它的连接仍然是仅限邀请的。

无论你选择哪种模式，在创建任何未知用户之前还会运行两项检查：

- **该域名不能属于另一个连接。**如果 SSO 域名索引将用户的电子邮件域名路由到另一个连接，登录会以 `access_denied`（“This email domain is managed by a different identity provider”）被拒绝。
- **仅限 OIDC：上游必须已验证该电子邮件。**当上游没有将 `email_verified` 报告为 true 时（从 id_token 读取；如果电子邮件来自 userinfo 响应，则从该响应读取），登录会以 `access_denied` 被拒绝。SAML 断言没有这样的标志，因此 SAML 改为依赖 `AllowedDomains`。

`federated_connection` 是保留的属性名。它永远不会出现在令牌中，OIDC 上游 id_token 中同名的声明会被丢弃，匿名自助注册也无法设置它，因此只有 SSO 回调才能断言某个账户经由哪个连接而来。

## 防止外部 IdP 成为安全隐患 {#keep-external-idps-from-becoming-foot-guns}

有几个 OIDC 连接标志在**你**控制的连接上是安全的，但在任意第三方 IdP 上则很危险：

- **`UseUpstreamSubjectAsUserId`**：由上游决定本地用户 ID。在你自己的共享链接提供方上，这能让 ID 保持一致；在客户的 IdP 上，这等于让*对方*来挑选你的用户 ID。
- **`AutoLinkExistingByEmail`**：按电子邮件将联合登录附加到已存在的本地账户，并跳过域名所有权检查。对于已验证收件箱的第一方提供方没有问题；在外部 IdP 上，它就是接管账户的杠杆。

将第三方连接标记为**外部**，即使设置了这些标志也会被中和：

```json
{
  "ConnectionId": "acme-entra",
  "IsExternalConnection": true,
  "UseUpstreamSubjectAsUserId": false,
  "AutoLinkExistingByEmail": false
}
```

`IsExternalConnection` 默认为 `false`（第一方），因此已有连接不受影响。请在每个指向他人 IdP 的 OIDC 连接上设置它；这样，以后的配置错误就无法把本地身份的控制权交给那个 IdP。SAML 连接没有这些标志，因此那里没有需要中和的内容。（将联合身份附加到已存在的账户，仍然额外要求连接的 `AllowedDomains` 为该电子邮件的域名担保：参见 [OIDC 联合：安全性](oidc-federation)。）

## 在联合之前收集信息 {#collect-something-before-federating}

有时你需要在把用户跳转到 IdP **之前**先向其展示一个页面：访客的显示名称、条款复选框、套餐选择器。`InteractionPath`（仅限 OIDC 连接）指定一个先行渲染的登录应用路由：

```json
{ "ConnectionId": "guest-link", "InteractionPath": "/guest" }
```

当未认证的 `idp_hint={ConnectionId}` 请求到达 `/connect/authorize` 时，Authagonal 会重定向到 `{LoginAppUrl}{InteractionPath}?returnUrl=<authorize url>&connection={id}`，而不是直接前往 IdP（`LoginAppUrl` 默认为 `/login`，且路径必须以 `/` 开头）。当[组织](#organisation-scoped-connections)唯一的连接或按域名匹配的连接被自动质询时，以及 `prompt=login` 通过 `idp_hint` 强制重新认证时，也会发生同样的重定向。你的页面收集所需的信息，将这些值追加到 `returnUrl` 的查询中（`PassthroughParams` / `ProvisioningAttributeParams` 从那里读取它们），然后自行继续前往 `/oidc/{id}/login`。判断无需交互的页面可以立即继续。

## 组织范围的连接 {#organisation-scoped-connections}

上文描述的都是**租户级**连接：整个租户共享这个连接，其 `AllowedDomains` 为该租户提供的每个登录屏幕声明一个电子邮件域名。当每个租户只联合一个客户时，这是正确的形态。当一个租户服务于许多客户[组织](organizations)、且每个组织都带着自己的 IdP 时，这就是错误的形态：两个客户不能同时声明 `contoso.com`，而一个客户的“通过 Contoso Entra 继续”按钮也不应该出现在另一个客户的登录屏幕上。

在连接上设置 `OrganizationId`，它就改为属于该组织（通过管理 API 在创建时设置，对于 SAML 也可以在更新时设置；不存在的组织会返回 `400 unknown_organization`）：

```json
{
  "ConnectionId": "acme-entra",
  "OrganizationId": "org_7f3a9c",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true
}
```

有三件事会发生变化，其他一切保持不变。

**只有选中该组织时才会提供它。**组织范围的连接永远不会出现在租户自己的登录屏幕上，也永远不会被未解析到任何组织的请求访问到，即使该请求的 `login_hint` 与其域名完全匹配。

**它的域名只在该组织内部匹配。**组织范围的连接有意*不*写入租户范围的 SSO 域名索引，因此一个域名可以在租户级别声明一次，并在每个组织中各声明一次。在同一个组织内的第二次声明仍然会以 `domain_claimed` 被拒绝，且跨两种协议都是如此，因此一个地址不可能路由到同一组织的两个 IdP。将连接移入某个组织会删除其索引行；将其移回（向 SAML 更新端点发送 `"organizationId": ""`）会重新注册这些行。OIDC 连接没有更新路由，因此其范围只能在创建时或在 `OidcProviders` 预置配置中设置。

**通过它登录的每个人都是该组织的成员。**SAML ACS 和 OIDC 回调会将连接所属的组织写为 `org_id`，覆盖账户自身的 `AuthUser.OrganizationId`（后者是下游预配的产物，而不是关于本次登录的断言），并在用户没有成员资格时创建一个活动的成员资格。**受邀**的成员资格会被接受：它变为 `active`，并保留其角色、邀请人和邀请时间，因为该组织自己的 IdP 现在已经为此人担保。任何其他已有的成员资格都保持不变：`suspended` 的行仍然是暂停状态，因此再次登录无法恢复管理员已撤销的访问权限。

### 在任何人登录之前，请求属于哪个组织 {#which-organization-a-request-is-for-before-anyone-signs-in}

主领域发现必须在还没有用户的时候回答这个问题，因此它的解析独立于（但顺序与之相同）[认证后的选择器](organizations#precedence)：

1. 请求上的 **`organization` 参数**（slug 或 ID）。
2. **`OAuthClient.RestrictedToOrganizationIds`**，当它恰好只有一个条目时。两个或更多条目不构成选择：该客户端服务于多个组织，而请求没有指明任何一个。
3. **`ITenantContext.OrganizationId`**：由宿主为每个请求固定一个组织，例如按组织划分的自定义域名。在所有单租户部署中均为 `null`。

该组织必须存在且已启用，并且客户端的限制必须允许它。其他任何情况都解析为*没有组织*，请求会像以前一样继续走租户范围的路径。特别是，如果参数指明的组织被客户端的限制排除在外，这里不会拒绝它：认证之后已经存在相应的拒绝（`access_denied`），而把拒绝提前到登录屏幕之前，会改变未认证调用方能够区分出哪些请求。

### `/connect/authorize` 如何使用它 {#what-connectauthorize-does-with-it}

解析出组织后，在任何租户范围规则之前：

- 指向**该组织**某个连接的 `idp_hint` 会直接前往该连接。这包括 SAML，而租户范围的提示路径（仅限 OIDC）无法到达 SAML。指向其他任何内容的提示会继续往下走。
- **恰好一个连接，且没有相矛盾的 `login_hint`** → 直接前往该连接。未列出任何域名的连接声明了整个组织；列出了域名的连接仍然会被自动质询，除非提示地址的域名不在其中。
- **多个连接** → 由提示的电子邮件域名在它们之间进行选择。
- **没有匹配** → 采用租户范围的 `login_hint` 和登录卡片行为，保持不变。

失败并带着查询中的 `error=` 返回的联合，会把该错误返回给依赖方，而不是再次进行联合，因此自动质询不会陷入循环。

### 登录应用看到的内容 {#what-the-login-app-sees}

`/api/auth/providers` 和 `/api/auth/sso-check` 都接受 `organization` 查询参数（并回退到 `ITenantContext.OrganizationId`），按相同的规则解析。存在组织时：

- `providers` 先列出**该组织**的按钮连接，再列出租户自己的连接。只有当连接未列出任何 `AllowedDomains`（对于 OIDC，还需开启 `ShowOnLogin`）时，它才会显示为按钮；按域名路由的连接则通过 `sso-check` 以先输入电子邮件的方式到达。未解析到组织时，组织范围的连接会被完全排除在列表之外，而其他组织的连接永远不会被列出。
- 当组织恰好只有一个连接时，`providers` 会增加 **`autoChallenge`**：这是一条完整的提供方记录（`connectionId`、`name`、`type`、`loginUrl`、`iconUrl`），指向应用应当跳过卡片直接前往的那个连接。它携带完整记录而不是仅有一个 ID，是因为该连接可能是按域名路由的或被隐藏的，因而不在 `providers` 中。其他情况下会省略该字段，而且它只是**建议性**的：`/connect/authorize` 自己也会执行同样的自动质询，因此忽略它的应用仍然会到达同一个 IdP。
- `sso-check` 会在租户范围索引**之前**先匹配该组织各连接的域名，当该组织没有为该地址声明任何内容时，才回退到租户范围索引。未列出任何域名的唯一连接会声明所有地址。

### 签发令牌时 {#at-token-issuance}

通过组织范围连接建立的会话，会把其组织作为仅次于刷新时所携带值的最高优先级来源（高于 `organization` 参数，也高于客户端的限制），因为它是唯一经过*证明*的来源：用户是在恰好属于该组织的 IdP 上完成认证的。指明另一个组织的请求会以 `access_denied` 被拒绝，而不会悄悄地签发另一个组织的令牌。该组织的 `RequireMembershipForTokens` 仍然适用，这正是回调要创建成员资格的原因。

## 相关内容 {#related}

- [组织](organizations)：记录、成员资格、声明和选择规则。
- [OIDC 联合](oidc-federation)：设置连接以及安全模型。
- [TCC 预配](provisioning)：这些流程所调用的 `Try` 处理程序。
- [保持联合会话同步](federated-sessions)：在上游撤销会话时撤销本地会话。
