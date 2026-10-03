---
layout: default
title: 预配
locale: zh-Hans
---

# TCC 预配

Authagonal 使用 **Try-Confirm-Cancel（TCC）** 模式将用户预配到下游应用中。这确保在用户获得访问权限之前所有应用都已同意，并且任何应用拒绝时都能干净地回滚。

## 预配何时运行 {#when-provisioning-runs}

无论用户通过哪条路径创建，只要创建了用户，预配就会自动运行：

| 端点 | 触发条件 |
|---|---|
| `POST /api/v1/profile/` | 管理员创建用户 |
| `POST /api/auth/register` | 自助注册 |
| SAML ACS（`POST /saml/{id}/acs`） | 首次 SSO 登录（新用户） |
| OIDC 回调（`GET /oidc/callback`） | 首次 SSO 登录（新用户） |
| SCIM（`POST /scim/v2/Users`） | 身份提供方预配 |
| `GET /connect/authorize` | 首次通过配置了 `ProvisioningApps` 的客户端进行授权 |

已经预配过的应用/用户组合会被跳过（记录在 `UserProvisions` 表中）。

用户创建路径会预配到**所有已配置的应用**中。授权端点只预配到该客户端的 `ProvisioningApps` 列表中的应用。

**被拒绝时：**如果任何预配应用在 Try 阶段拒绝了该用户（或某个回调失败），新创建的用户会被删除。这避免了创建一半的用户。调用方看到的结果取决于路径：

| 路径 | 响应 |
|---|---|
| 管理员创建（`POST /api/v1/profile/`）、自助注册 | `422 Unprocessable Entity`，附带拒绝原因 |
| SAML ACS、OIDC 回调 | `400 Bad Request`，`{ "error": "provisioning_rejected", "message": "..." }` |
| SCIM 创建 | SCIM `400`，`scimType: invalidValue`，附带固定的消息（下游应用的文本不会回传给身份提供方） |
| 无密码账户认领确认 | JSON 形式的 `400 provisioning_rejected`；若是浏览器点击，则重定向到 `/login?error=provisioning_rejected&error_description=...`（参见[升级用户](user-upgrade)） |
| `GET /connect/authorize` | 带着 `error=access_denied` 重定向回客户端 |

管理员创建请求接受 `skipProvisioning: true`，适用于本身就是预配目标的第一方调用方：它正在设置该用户，不希望自己的回调在中途被再次调用。此时不会为该用户预配任何内容，也不会调用任何应用。

## 配置 {#configuration}

### 1. 定义预配应用 {#1-define-provisioning-apps}

在 `appsettings.json` 中：

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-bearer-token",
      "TryTimeoutSeconds": 60
    }
  }
}
```

`TryTimeoutSeconds` 是可选的（默认 60）。如果下游应用在 Try 期间要执行实际工作，请调高该值。Confirm、Cancel 和 Deprovision 始终使用较短的固定超时（10 秒），不可调整；这些操作应当始终是轻量的。

只有在未注册 `IProvisioningAppStore` 时才会读取 `ProvisioningApps` 配置节。Azure Table、AWS 和 SQL 提供程序各自都会注册一个，此时库会改为从存储中解析应用（参见[自定义应用解析](#custom-app-resolution)），因此使用持久化提供程序时，请通过管理 API 而不是在 `appsettings.json` 中定义应用。

### 2. 将应用分配给客户端 {#2-assign-apps-to-clients}

每个客户端通过客户端记录上的 `provisioningApps` 字段声明其用户必须被预配到哪些应用中。请通过客户端管理 API 设置该字段（`Clients` 预置配置不包含此字段）。创建客户端时会绑定整条记录，而 `PUT /api/v1/clients/{clientId}` 会把你发送的字段合并到已存储的客户端上，因此只携带 `provisioningApps` 的请求不会改动客户端的其余部分：

```
PUT /api/v1/clients/web-app
{
  "provisioningApps": ["my-backend"]
}
```

当用户通过 `web-app` 授权时，如果尚未预配到 `my-backend`，就会被预配进去。

## TCC 协议 {#tcc-protocol}

Authagonal 会向你的预配端点发起三类 HTTP 调用。它们都使用 `POST`、JSON 正文以及 `Authorization: Bearer {ApiKey}`。

### 阶段 1：Try {#phase-1-try}

**请求：**`POST {CallbackUrl}/try`

```json
{
  "transactionId": "a1b2c3d4...",
  "userId": "user-id",
  "email": "user@example.com",
  "firstName": "Jane",
  "lastName": "Doe",
  "organizationId": "org-id-or-null",
  "customAttributes": { "key": "value" }
}
```

值为 null 的字段（包括用户没有自定义属性时的 `customAttributes`）会从载荷中省略。

**预期响应：**

| 状态码 | 正文 | 含义 |
|---|---|---|
| `200` | `{ "approved": true }` | 用户可以被预配。应用创建一条**待定**记录。 |
| `200` | `{ "approved": false, "reason": "..." }` | 用户被拒绝。不创建记录。 |
| `2xx` | 空正文或无法解析的正文 | 视为批准。 |
| 非 2xx | 任意 | 视为失败。 |

请返回明确的 `approved` 值。正文无法按 JSON 读取的响应会被视为批准，因此一个配置错误、以 HTML 页面回应 `200` 的端点会批准所有用户。

`transactionId` 标识本次预配尝试。你的应用应将其与待定记录一起保存。

批准的响应还可以返回 `organizationId`、`customAttributes` 和 `emailVerified`。Authagonal 会把它们合并到用户上：`organizationId` 仅在用户尚无组织时生效（同一事务中后面的应用能看到先前的分配）；`customAttributes` 的条目逐键合并；`emailVerified: true` 会将用户的电子邮件标记为已确认（当下游应用已经验证过该地址时使用；随后自助注册会跳过验证邮件）。`organizationId` 和这些属性都会进入令牌（`org_id` 声明；自定义属性则通过作用域的 `UserClaims` 配置）。合并后的值会在所有应用都确认之后保存到用户上。

### 阶段 2：Confirm {#phase-2-confirm}

只有当**所有**应用在 Try 阶段都返回 `approved: true` 时才会调用。

**请求：**`POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**预期响应：**`2xx`（正文任意）。你的应用将待定记录提升为已确认。非 2xx 响应或超时（10 秒）都算作确认失败。

### 阶段 3：Cancel {#phase-3-cancel}

当**任何**应用的 Try 被拒绝或失败时调用，用于清理那些在 Try 阶段已成功的应用。

**请求：**`POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**预期响应：**`200`（正文任意）。你的应用删除待定记录。

Cancel 是尽力而为的：如果失败，Authagonal 会记录错误并继续。作为安全网，你的应用应当**在一段 TTL 之后对未确认的记录进行垃圾回收**（例如 1 小时）。

## 流程图 {#flow-diagram}

```
Authorize Endpoint
    │
    ├─ User authenticated ✓
    ├─ Client requires apps: [A, B]
    ├─ User already provisioned into: [A]
    ├─ Need to provision: [B]
    │
    ├─ TRY B ──────────► App B: create pending record
    │   └─ approved: true
    │
    ├─ CONFIRM B ──────► App B: promote to confirmed
    │   └─ 200 OK
    │
    ├─ Store provision record (userId, "B")
    ├─ Issue authorization code
    └─ Redirect to client
```

### 失败时 {#on-failure}

```
    ├─ TRY A ──────────► App A: create pending record
    │   └─ approved: true
    │
    ├─ TRY B ──────────► App B: rejects
    │   └─ approved: false, reason: "No license available"
    │
    ├─ CANCEL A ───────► App A: delete pending record
    │
    └─ Redirect with error=access_denied
```

### 部分确认失败时 {#on-partial-confirm-failure}

如果某次确认失败，Authagonal 会回滚整个事务：

1. 尚未确认的应用会收到 `POST {CallbackUrl}/cancel`。
2. **在本次事务中**已经确认的应用会通过 `DELETE {CallbackUrl}/users/{userId}` 进行补偿（与[取消预配](#deprovisioning)的调用相同），并删除它们的预配记录。用户在更早的事务中被预配到的应用不受影响。
3. 抛出预配错误，调用路径会删除新创建的用户（对于授权端点，则以错误作答）。

预配记录只会在所有确认都成功之后才保存，因此重试时会再次尝试所有应用。补偿是尽力而为的：失败的 `DELETE` 会被记录下来，应用中的账户可能需要手动删除。

## 自定义应用解析 {#custom-app-resolution}

库会为你选择应用来源：

- 如果注册了 `IProvisioningAppStore`（Azure Table、AWS 和 SQL 提供程序都会注册），应用来自该存储（`StoreProvisioningAppProvider`），并通过下面的管理 API 进行管理。
- 否则从 `ProvisioningApps` 配置节读取（`ConfigProvisioningAppProvider`）。

在 `AddAuthagonal` 之前注册你自己的 `IProvisioningAppProvider`，即可用其他方式解析应用，例如按租户解析；只有在尚未注册任何实现时，库才会添加默认实现：

```csharp
builder.Services.AddSingleton<IProvisioningAppProvider, MyAppProvider>();
builder.Services.AddAuthagonal(builder.Configuration);
```

该提供程序返回应用列表及其回调 URL。`TccProvisioningOrchestrator` 会对每个应用调用 Try/Confirm/Cancel。

> **默认情况下，`CallbackUrl` 必须是可公开路由的。** Authagonal 在写入时以及每次发起请求时都会校验它，拒绝回环、RFC1918、链路本地以及 `.internal`/`.local` 目标（预配回调是由服务器获取的 URL）。在你自己网络内部运行的预配应用是受支持的部署方式：请在 [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard) 中列出它。

### 管理 API {#admin-api}

存储中的应用在 `/api/v1/provisioning/apps` 下管理（策略 `IdentityAdmin`；每次更改都会被审计）：

| 路由 | 行为 |
|---|---|
| `GET /` | `{ "apps": [{ "appId", "name", "callbackUrl", "hasApiKey", "tryTimeoutSeconds" }], "limit": n }`。API 密钥永远不会返回，只返回 `hasApiKey`。`limit` 是应用配额，没有配额时为 null。 |
| `POST /` | 创建。`name` 和 `callbackUrl` 为必填；`apiKey` 和 `tryTimeoutSeconds` 为可选。会生成一个 12 个字符的 `appId`。超出配额时返回 `400 provisioning_app_limit`。 |
| `PUT /{appId}` | 替换 `name`、`callbackUrl` 和 `tryTimeoutSeconds`（`name` 和 `callbackUrl` 仍为必填）。省略 `apiKey` 或将其设为 null 会保留原密钥；空字符串则清除它。未知应用返回 `404 app_not_found`。 |
| `DELETE /{appId}` | `{ "removed": true }`。 |
| `POST /{appId}/test` | 使用固定的测试用户（`test-user`、`test@example.com`）向该应用发送一次 Try，超时为 10 秒。返回 `{ "success", "statusCode", "body" }`（正文截断为 1000 个字符）。连接失败时返回 `success: false, statusCode: 0`，而不是错误状态码。 |

`callbackUrl` 必须是指向外部主机的绝对 `http` 或 `https` URL，如上所述。`tryTimeoutSeconds` 会被限制在 5 到 300 秒之间。客户端在 `provisioningApps` 中列出的就是 `appId`。

## 取消预配 {#deprovisioning}

当通过管理 API（`DELETE /api/v1/profile/{userId}`）删除用户，或通过 SCIM（`DELETE /scim/v2/Users/{id}`，一种停用用户的软删除）取消预配用户时，Authagonal 会对该用户被预配到的每个应用调用 `DELETE {CallbackUrl}/users/{userId}`，超时为 10 秒，并删除预配记录。这是尽力而为的：失败会被记录，但不会阻止删除。已不再配置的应用会被跳过并记录警告。

`IProvisioningOrchestrator` 上的 `ReprovisionAsync` 会对每个应用重新运行 Try 和 Confirm，即使用户已经被预配过。库会在认领无密码账户时使用它（参见[升级用户](user-upgrade)）；普通的重新登录从不使用。

## 实现上游端点 {#implementing-the-upstream-endpoints}

### 最简示例（Node.js/Express） {#minimal-example-nodejsexpress}

```javascript
const pending = new Map(); // transactionId → user data

app.post('/provisioning/try', (req, res) => {
  const { transactionId, userId, email } = req.body;

  // Your business logic: can this user be provisioned?
  if (!isAllowed(email)) {
    return res.json({ approved: false, reason: 'Domain not allowed' });
  }

  // Store pending record with TTL
  pending.set(transactionId, { userId, email, createdAt: Date.now() });

  res.json({ approved: true });
});

app.post('/provisioning/confirm', (req, res) => {
  const { transactionId } = req.body;
  const data = pending.get(transactionId);

  if (data) {
    createUser(data); // Promote to real record
    pending.delete(transactionId);
  }

  res.sendStatus(200);
});

app.post('/provisioning/cancel', (req, res) => {
  pending.delete(req.body.transactionId);
  res.sendStatus(200);
});

// Cleanup unconfirmed records older than 1 hour
setInterval(() => {
  const cutoff = Date.now() - 3600000;
  for (const [id, data] of pending) {
    if (data.createdAt < cutoff) pending.delete(id);
  }
}, 600000);
```
