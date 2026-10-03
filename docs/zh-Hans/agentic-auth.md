---
layout: default
title: 智能体授权
locale: zh-Hans
---

# 智能体授权

Authagonal 提供了一组构建模块，用于将用户的权限安全地委托给 AI 智能体（或任何非人类工作负载）：已注册的智能体、细粒度的权限授予、复合委托令牌、长期有效的用户同意、即时审批、能力票据，以及可感知委托关系的审计接口。库负责提供这些原语并保证不变式成立；由宿主应用将它们组装成产品（连接器实现、审批界面、通知投递和业务策略都留在宿主一侧）。

## 不变式 {#the-invariant}

每个委托令牌都遵循：

```
effective authority = admin ceiling ∩ user consent ∩ task request ∩ subject-token authority
```

下游的任何环节都无法扩大它，每多一跳委托都会再求一次交集，因此权限只会收窄。交集运算只实现了一次（`AuthoritySet.Intersect`），并在所有地方复用。

## 实体 {#entities}

| 实体 | 类型 | 说明 |
|---|---|---|
| 智能体 | 机密 `OAuthClient` 上的 `AgentProfile` | 注册配置文件是使客户端成为智能体的方式；删除它会让该客户端恢复为普通 OAuth 客户端。 |
| 权限 | `AuthoritySet` / `AuthorityGrant` | RFC 9396 `authorization_details` 结构：连接器 `type`、`actions`、`locations`、约束条件，以及按操作设置的 `auto`/`ask`/`deny` 策略。 |
| 上限 | `AgentProfile.Ceiling` | 经由该智能体的任何委托所能携带的最宽权限。由管理员管理（`/api/v1/agents`）。 |
| 同意（下限） | 类型为 `agent_consent` 的 `PersistedGrant` | 按（用户，智能体）维度存储，在 `/consent/agents` 管理。存储时已预先与上限求交集，并在每次签发时重新求交集。 |
| 委托 | RFC 8693 令牌交换 | 复合身份：`sub` = 用户，`act` = 智能体（每跳嵌套一层），`authorization_details` = 有效交集。生命周期短，永远不可刷新。 |
| 审批 | 类型为 `approval` 的 `PersistedGrant` | 针对 `ask` 策略操作的即时关卡；采用设备流的轮询语义；一次性使用，并绑定请求结构。 |
| 能力票据 | `ICapabilityTicketService` | 绑定到某个令牌的不透明一次性句柄：即 BFF ws-ticket 的通用化版本，基于授权存储原子化实现。 |
| 审计 | `IAuthHook` | `OnDelegationMintedAsync`、`OnApprovalRequested/ResolvedAsync`、`OnAgentConsentChangedAsync`、`OnCapabilityTicketRedeemedAsync`，以及签发前的 `OnTokenIssuingAsync` 关卡。 |

## 注册智能体 {#registering-an-agent}

1. 创建一个机密客户端，允许 `urn:ietf:params:oauth:grant-type:token-exchange`（委托模式）和/或 `client_credentials`（服务模式）。
2. `PUT /api/v1/agents/{clientId}`：

```json
{
  "mode": "delegated",
  "ceiling": [
    {
      "type": "email",
      "actions": ["send", "read"],
      "action_policies": { "send": "ask" },
      "recipient_domains": ["@acme.com", "*.partners.acme.com"]
    },
    { "type": "calendar", "actions": ["read"] }
  ],
  "maxDelegationDepth": 0,
  "maxTokenLifetimeSeconds": 300,
  "highRiskDefault": "ask"
}
```

`mode` 取值为 `delegated`、`service` 或 `both`（更新时省略 `mode` 会保留现有值）。`maxDelegationDepth` 必须在 0 到 8 之间（默认 0），`maxTokenLifetimeSeconds` 在 30 到 86400 之间（默认 300），`highRiskDefault` 取值为 `auto`、`ask` 或 `deny`；其他任何值都会返回 400。

约束成员的类型由其 JSON 结构决定：字符串/字符串数组 → 允许列表（合并时取集合交集；条目支持精确匹配、`*.host` 通配符匹配和 `@suffix` 后缀匹配），数字 → 上限值（合并时取最小值），布尔值 → 开关（合并时取 AND）。无法解释的成员会原样保留，并在求值时按失败关闭处理。`GET /api/v1/agents/{clientId}/effective-grant?subjectId=…` 为管理界面预览上限 ∩ 同意的结果。

## 用户同意（下限） {#user-consent-the-floor}

- `GET /consent/agents/{clientId}/info`：根据连接器目录渲染上限（注册一个 `IConnectorCatalog` 以提供显示名称、操作说明和高风险标记；其中的类型会在发现文档中以 `authorization_details_types_supported` 公布）。
- `POST /consent/agents` `{ "clientId": …, "authority": […] }`：授予下限（省略 `authority` 即同意整个上限）。用户可以收紧策略（`auto` → `ask`），但永远不能放宽或扩大，因为存储时会与当前生效的上限预先求交集。
- `GET /consent/agents` / `DELETE /consent/agents/{clientId}`：列出和撤销。撤销会阻止下一次签发；已签发的委托不可刷新，会在其（较短的）生命周期内过期。没有同意 → 交换失败，返回 `invalid_grant` / `consent_required`，仅有上限不会授予任何权限。

## 签发委托 {#minting-a-delegation}

智能体以自身身份进行认证，并交换用户的令牌：

```
POST /connect/token
grant_type=urn:ietf:params:oauth:grant-type:token-exchange
client_id=agent&client_secret=…            (or private_key_jwt, below)
subject_token={user access token}
subject_token_type=urn:ietf:params:oauth:token-type:access_token
authorization_details=[{"type":"email","actions":["read"]}]   (the task slice; omit = everything grantable)
```

签发时按以下顺序执行检查：智能体模式、长期同意、子委托深度（`act` 链中已有的每个参与者都需要有足够的 `maxDelegationDepth` 额度再多一跳）、交集运算、显式请求的拒绝（`invalid_target`，智能体不应误以为自己拥有实际并不具备的权限）、ask 关卡，以及生命周期限制（客户端生命周期 ∩ 主体令牌剩余有效期 ∩ `maxTokenLifetimeSeconds`）。令牌携带 `act`（RFC 8693；每跳嵌套一层）和 `authorization_details`（RFC 9396）；响应会回显实际授予的详情；内省也会输出这两者。对委托令牌再次进行交换时，权限会自动进一步收窄，因为主体令牌自身的声明会加入交集运算。

**没有**智能体配置文件的客户端，其交换行为与现在完全相同，唯一的区别是 `authorization_details` 请求参数现在会收窄（永远不会扩大）交换得到的令牌。

## 审批（ask 关卡） {#approvals-ask-gate}

当有效权限切片中包含 `ask` 操作时，交换会被挂起：

```json
{ "error": "authorization_pending", "approval_id": "…", "interval": 5 }
```

宿主通过 `IAuthHook.OnApprovalRequestedAsync` 收到通知（邮件/推送/聊天投递由宿主负责）。用户通过 `GET /approvals`、`POST /approvals/{id}` `{ "decision": "approve" | "deny" }` 做出处理，同时智能体带上 `approval_id` 重试完全相同的请求，全程使用设备流的术语（`slow_down`、`access_denied`、`expired_token`）。审批只能使用一次（原子消费），在 `ApprovalLifetimeSeconds`（默认 300）后过期，并且绑定到确切的请求结构*以及当前的策略状态*：如果管理员在挂起与轮询之间修改了上限，该审批就会失效，而不是签发过时的权限。被消费的审批在签发时，其中的 `ask` 操作会被视为 `auto`（已经询问并得到答复）。

服务模式（`client_credentials`）中没有用户参与：只有上限单独生效，`ask` 会降级为 `deny`。

## 资源端强制执行 {#resource-side-enforcement}

- 在任何资源服务器中使用 `AuthorityEvaluator.Permits(user, type, action, context, location, strict)`（上下文键与约束名称进行匹配，请传入你能推导出的内容，例如发送邮件时传入 `recipient_domains`）。不含该声明的令牌按不受限制求值（为了兼容旧版本）；格式损坏的声明按全部拒绝求值。
  - `location` 是你正在操作的 RFC 9396 `locations` 值。指定了位置的授权只在这些位置生效；被授予的位置是一个**根**，因此 `https://api.example.com/orders` 涵盖 `/orders/17`，但不涵盖 `/orders-admin`。
  - `strict: true` 会在调用方没有为某个约束提供上下文时拒绝，而不是跳过该约束。凡是能列举出你所支持的全部键的地方，都应使用它：`AuthoritySet.UncheckedConstraints(type, context)` 会列出你没有检查的那些。
- BFF 关口：`BffUpstream.RequiredAuthority = ["email:send"]` 会让代理在转发前检查出站的 bearer 令牌，失败时返回 403，不允许匿名通过。它提供的位置是请求实际将到达的上游（当权限是针对公开标识符而非内部地址签发时，用 `AuthorityLocation` 覆盖根）；`StrictAuthority` 会让代理拒绝它无法求值的约束，而不是把它留给上游处理。

## 能力票据 {#capability-tickets}

`ICapabilityTicketService`（默认实现为 `GrantStoreCapabilityTicketService`，由 `AddAuthagonalCore` 通过 `TryAdd` 注册，因此 `AddAuthagonal` 也会获得它）签发绑定到令牌的不透明一次性句柄，通过授权存储的条件删除来原子兑换，具备持久性，并且在多个 Pod 之间可防重放，这一点与普通缓存的先读取再删除不同。BFF 的 ws-ticket 保留其现有的分布式缓存契约（`WsTicketKey` / `TryRedeemWsTicketAsync`），因为它的兑换方通常是只共享 Redis 的另一个宿主；同宿主部署的代理服务应优先使用能力票据服务。

## private_key_jwt {#private_key_jwt}

智能体是工作负载；共享密钥是整条链中最薄弱的环节。设置 `OAuthClient.JwksJson`（内联 JWKS）或 `JwksUri`（远程获取，缓存约 10 分钟），并使用 RFC 7523 客户端断言（`client_assertion_type=…:jwt-bearer`）进行认证。强制执行的检查包括：针对已注册 JWKS 校验签名，`iss` = `sub` = `client_id`，受众 = 签发者或令牌端点，`exp` 有上限（≤ 10 分钟），以及一次性 `jti`（基于 `IRevokedTokenStore` 的重放缓存）。只要请求中带有断言，就绝不会回退到密钥认证路径。

## 兼容性 {#compatibility}

- 没有智能体配置文件 → 任何流程的行为都不变。所有新增的表/列都是可空默认值，并在两种存储提供程序上自动创建（`AgentProfiles` 表；客户端上的 `JwksJson`/`JwksUri`；同意/审批/票据沿用现有的授权表）。
- 新增的 `IAuthHook` 成员是默认接口方法，现有钩子无需修改即可编译。
- `ITokenExchangeSubjectTransformer` 仍会在每次交换时运行，可以拒绝或绑定上下文声明；但它永远不能扩大委托范围（其输出会被重新求交集），也不能修改 `act` 链（保留声明）。
