---
layout: default
title: 管理 API
locale: zh-Hans
---

# 管理 API

管理端点要求使用带有 `authagonal-admin` 作用域（可通过 `AdminApi:Scope` 配置）的 JWT 访问令牌。

所有端点都位于 `/api/v1/` 之下。

## 获取第一个管理令牌 {#bootstrapping-the-first-admin-token}

每个 `/api/v1/*` 端点都要求携带管理作用域的持有者令牌，但管理 API 本身（以及[动态客户端注册](client-registration)）**拒绝创建或更新任何持有该作用域的客户端**（`403 forbidden_scope`），因此运行时创建的客户端永远无法提权为管理员。签发管理令牌的唯一途径是**通过配置预置的客户端**：`Clients:` 配置节中的条目会在启动时由 `ClientSeedService` 执行 upsert；配置是受信任的，禁止作用域的保护只适用于运行时 API。

在 `appsettings.json`（或等效的环境变量 / 密钥存储）中预置一个带有管理作用域的 `client_credentials` 客户端：

```json
{
  "Clients": [
    {
      "Id": "admin-cli",
      "Name": "Admin CLI",
      "ClientSecret": "a-long-random-secret",
      "GrantTypes": ["client_credentials"],
      "Scopes": ["authagonal-admin"]
    }
  ]
}
```

（`ClientSecret` 会在启动时被哈希；如果你希望配置中只保存预先哈希过的值，可以改为提供 `SecretHashes`。`ClientId`/`ClientName`/`AllowedGrantTypes`/`AllowedScopes` 也可作为 `Id`/`Name`/`GrantTypes`/`Scopes` 的别名使用。）

然后在标准令牌端点用凭据换取令牌：

```bash
curl -X POST https://auth.example.com/connect/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=admin-cli" \
  -d "client_secret=a-long-random-secret" \
  -d "scope=authagonal-admin"
```

```json
{ "access_token": "eyJhbGci...", "token_type": "Bearer", "expires_in": 1800, "scope": "authagonal-admin" }
```

`client_credentials` 授权会根据客户端的 `AllowedScopes` 校验所请求的作用域，由于预置的客户端持有 `authagonal-admin`，令牌就会被签发。在每次管理调用中以 `Authorization: Bearer {access_token}` 的形式使用它：

```bash
curl https://auth.example.com/api/v1/clients -H "Authorization: Bearer eyJhbGci..."
```

请将预置客户端的密钥保存在部署的密钥存储中；轮换它只需修改配置并重启。

## 用户 {#users}

### 获取用户 {#get-user}

```
GET /api/v1/profile/{userId}
```

返回个人资料，以及支持控制台诊断登录问题所需的信息：
`emailConfirmed`、`isActive`、`lockoutEnd`、`accessFailedCount`、`roles`、已关联的
`externalLogins`，以及 `hasPassword`（只表示是否存在，绝不返回哈希）。最后这一项区分了
“他们忘记了密码”和“他们从来没有密码，而是通过 SSO 登录”这两种情况，
而这两种情况需要的建议恰恰相反。

返回用户详情，包括外部登录关联。

### 用户是否存在 {#user-exists}

```
GET /api/v1/profile/{userId}/exists
```

用户存在时返回 `204`，否则返回 `404`（一种低开销的存在性探测，没有响应体）。

### 注册用户 {#register-user}

```
POST /api/v1/profile/
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

创建用户并发送验证邮件。如果邮箱已被占用，返回 `409 user_exists`。

仅限管理员的可选字段：`userId`（由调用方提供的 ID，冲突时返回 `409 user_id_in_use`）、`emailConfirmed`（创建时即为已验证状态，跳过验证邮件）、`companyName`、`organizationId`、`phone`、`locale`，以及 `customAttributes`（一个字符串映射，持久化在用户上并转发给预配目标）。

`skipProvisioning: true` 会创建身份而不运行预配。它用于这样一种第一方应用：
该应用**本身**就是一个预配目标，并且正在为这个用户进行设置的过程中；
它调用此处是为了创建身份，而不是为了就一个它正在创建的用户
收到回调。如果不设置此项，该应用会针对一个尚未建好的用户收到自己的 Try 请求，其中只携带
在往返过程中保留下来的属性；即使它能处理过来，最终也会把该用户预配两次。

### 更新用户 {#update-user}

```
PUT /api/v1/profile/
Content-Type: application/json

{
  "userId": "user-id",
  "firstName": "Jane",
  "lastName": "Smith",
  "organizationId": "new-org-id"
}
```

`userId` 是必需的；其他字段都是可选的，只更新提供了的字段。

`isActive` 用于停用或重新激活账户。`emailConfirmed`（也接受 `emailVerified`）
会在不发送验证邮件的情况下将地址标记为已确认，适用于已通过其他方式确认
地址归属的情况。

修改 `organizationId` 或停用账户会触发：
- SecurityStamp 轮换（在 30 分钟内使所有 Cookie 会话失效）
- 撤销所有刷新令牌

一个要到下次登录才生效的封禁并不算封禁，这就是为什么停用会立即撤销，
而不是等待令牌过期。

### 搜索用户 {#search-users}

```
GET /api/v1/profile/search?q=jane&maxResults=20
```

在邮箱和姓名索引上进行前缀搜索。返回 `{ "users": [ ... ] }`。

### 按邮箱获取用户 {#get-user-by-email}

```
GET /api/v1/profile/by-email?email=jane@example.com
```

精确查找，与搜索不同：搜索是前缀匹配，可能返回多个人。将“这个地址”
解析为“这个账户”的调用方需要的是一个答案或没有答案。如果不存在该用户，返回 `404`。

### 列出用户 {#list-users}

```
GET /api/v1/profile?organizationId=&count=100&continuationToken=
```

基于游标分页的目录列表；将返回的 `continuationToken` 传回以获取下一页，
当它为 null 时停止。之所以使用游标而不是偏移量，是因为存储按令牌分页：使用偏移量
会让每一页都从头重新扫描。

### 哪些用户存在 {#which-users-exist}

```
POST /api/v1/profile/exists
Content-Type: application/json

{ "userIds": [ "a", "b", "c" ] }
```

返回其中存在的那部分 ID；当请求超出 500 个 ID 的上限时，还会返回 `truncated: true`，这样
调用方会被告知其批次已被截断，而不是在 600 个中只得到关于 500 个的回答却毫不知情。用于
将一组 ID 与另一个系统中的 ID 集合进行核对。

### 批量查询用户的 MFA 状态 {#mfa-status-for-many-users}

```
POST /api/v1/profile/mfa-status
Content-Type: application/json

{ "userIds": [ "a", "b", "c" ] }
```

返回 `{ "statuses": { "a": true, "b": false }, "truncated": false }`：`true` 表示该用户至少有一个 MFA 凭据。上限为 500 个 ID；`truncated: true` 表示请求已被截断。用于在目录视图中显示“使用 MFA”徽标。

### 设置密码 {#set-a-password}

```
POST /api/v1/profile/{userId}/set-password
Content-Type: application/json

{ "password": "N3w!Password" }
```

这是一条支持路径，用于帮助那些被锁在账户之外、而账户邮箱已无法联系到本人的用户。
受密码策略约束。会撤销所有刷新令牌并轮换安全戳：如果修改密码后旧会话仍在运行，
那么能以该用户身份行事的人其实并没有改变。

### 解锁用户 {#unlock-a-user}

```
POST /api/v1/profile/{userId}/unlock
```

清除锁定及其失败尝试计数，让用户现在就能重新登录，而不必等到锁定
自然过期。

### 删除用户 {#delete-user}

```
DELETE /api/v1/profile/{userId}
```

删除用户，撤销所有授权，并从所有下游应用中取消预配（尽力而为）。

### 确认邮箱 {#confirm-email}

```
POST /api/v1/profile/confirm-email?token={token}
```

### 发送验证邮件 {#send-verification-email}

```
POST /api/v1/profile/{userId}/send-verification-email
```

### 关联外部身份 {#link-external-identity}

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### 取消关联外部身份 {#unlink-external-identity}

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

### 列出用户的组织 {#list-a-users-organizations}

```
GET /api/v1/profile/{userId}/organizations
```

返回 `{ "items": [ { "organizationId", "slug", "name", "status", "roles" } ] }`:用户拥有成员关系的每个组织,无论其状态如何。组织已不存在的成员关系会被省略。

### 邀请用户 {#invite-user}

```
POST /api/v1/profile/invite
Content-Type: application/json

{
  "email": "ada@acme.com",
  "firstName": "Ada",
  "lastName": "Lovelace",
  "locale": "en",
  "organizationId": "org_0123456789abcdef0123456789abcdef",
  "organizationRoles": ["Auditor"]
}
```

只有 `email` 是必填的。创建账户(未确认,没有可用的密码),并通过邮件发送一个指向 `/accept-invite` 的一次性链接,有效期 7 天。提供 `organizationId`(组织的 id,而不是 slug)时,邀请的是该组织:会写入一条带有 `organizationRoles` 的 `invited` 成员关系,受邀者设置密码后它变为 `active`。邮件使用你的请求中库所支持的第一个 `Accept-Language` 撰写,否则使用 `locale`,再否则使用英语。参见[组织](organizations#inviting-people)。

返回 `200`,内容为 `{ "userId": "...", "email": "ada@acme.com" }`(地址为小写)。失败为 `{ "error", "error_description" }`:

| 状态 | `error` | 何时 |
|---|---|---|
| 400 | `email_required` | 缺少 `email` 或为空白 |
| 400 | `invalid_email` | `email` 不是可用的地址 |
| 400 | `invalid_role` | 某个组织角色以 `tenant:` 或 `platform:` 开头 |
| 404 | `organization_not_found` | `organizationId` 不指向任何组织 |
| 409 | `user_already_exists` | 该地址已有账户 |

### 重新发送邀请 {#resend-invite}

```
POST /api/v1/profile/{userId}/resend-invite
```

生成新链接并发送。返回 `200`,内容为 `{ "sent": true }`。用户未知时为 `404 user_not_found`,地址确认后为 `400 already_accepted`(请使用密码重置),邮件无法发送时为 `502 invite_email_failed`。

## MFA 管理 {#mfa-management}

### 获取 MFA 状态 {#get-mfa-status}

```
GET /api/v1/profile/{userId}/mfa
```

返回用户的 MFA 状态和已注册的方式。

### 重置所有 MFA {#reset-all-mfa}

```
DELETE /api/v1/profile/{userId}/mfa
```

移除所有 MFA 凭据并设置 `MfaEnabled=false`。如有要求，用户需要重新注册。

### 移除特定的 MFA 凭据 {#remove-specific-mfa-credential}

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

移除特定的 MFA 凭据（例如丢失的身份验证器）。如果移除的是最后一个主要方式，MFA 会被禁用。

## SSO 提供方 {#sso-providers}

### SAML 提供方 {#saml-providers}

```
POST   /api/v1/saml/connections                    # Create
GET    /api/v1/saml/connections/{connectionId}     # Get one
PUT    /api/v1/saml/connections/{connectionId}     # Update (partial: only supplied fields change)
DELETE /api/v1/saml/connections/{connectionId}     # Delete
```

创建时需要 `connectionName`、`entityId`，以及 `metadataLocation`（元数据 URL）或 `metadataXml`（粘贴的 IdP 元数据，用于没有元数据 URL 的 IdP，保存时会进行解析校验并压缩）**两者中的恰好一个**。可选：`nameIdFormat`（省略则使用默认的 emailAddress，`"none"` 表示省略 NameIDPolicy，推荐用于 ADFS，或者填写一个 NameID 格式 URN）、`signAuthnRequests`、`iconUrl`、`allowedDomains`、`disableJitProvisioning`、`organizationId`。每个连接都会获得一个由服务器生成的 SP 密钥对；它永远不会通过 API 返回。详情参见 [SAML](saml)。

`organizationId` 将连接限定到某一个[组织](organizations)：只有选中该组织时才会提供此连接，其 `allowedDomains` 只在该组织内匹配（并且*不会*写入租户范围的 SSO 域名索引），通过它登录的每个人都会成为该组织的成员。省略或为 `null` 表示租户级连接。不存在的组织会返回 `400 unknown_organization`。更新时，`null`（即字段缺失）保持范围不变，`""` 将连接恢复为租户级，无论哪个方向的变更都会相应地重写域名索引。参见[组织范围的连接](self-service-sso#organisation-scoped-connections)。

### OIDC 提供方 {#oidc-providers}

```
POST   /api/v1/oidc/connections                    # Create
GET    /api/v1/oidc/connections/{connectionId}     # Get one
DELETE /api/v1/oidc/connections/{connectionId}     # Delete
```

创建时需要 `connectionName`、`metadataLocation`、`clientId`、`clientSecret`、`redirectUrl`。可选：`iconUrl`、`allowedDomains`、`passthroughParams`、`organizationId`（含义与上文 SAML 连接上的相同）。客户端密钥在存储时受到保护，并且永远不会被返回。参见 [OIDC 联合](oidc-federation)。

### SSO 域名 {#sso-domains}

```
GET    /api/v1/sso/domains                 # List all
```

## 客户端 {#clients}

在运行时管理 OAuth 客户端。所有路由都要求 `IdentityAdmin` 策略（即管理作用域）。

```
GET    /api/v1/clients              # List all clients
GET    /api/v1/clients/{clientId}   # Get one client
POST   /api/v1/clients              # Create a client
PUT    /api/v1/clients/{clientId}   # Update a client
DELETE /api/v1/clients/{clientId}   # Delete a client
```

### 创建 / 更新客户端 {#create--update-client}

```
POST /api/v1/clients
Content-Type: application/json

{
  "clientId": "my-app",
  "clientName": "My Application",
  "allowedGrantTypes": ["authorization_code"],
  "redirectUris": ["https://app.example.com/callback"],
  "allowedScopes": ["openid", "profile", "email"]
}
```

如果客户端已存在，`POST` 返回 `409`。`PUT` 更新现有客户端（不存在时返回 `404`）；更新时只对新增的作用域进行提权检查。

注意：

- **永远不返回密钥哈希。**`clientSecretHashes` 会从每个响应中剥离（列表、获取、创建、更新）。更新时，省略 `clientSecretHashes` 会保留已存储的密钥；提供新的哈希则会轮换密钥。
- **管理作用域不能授予客户端。**在 `allowedScopes` 中请求 `AdminApi:Scope`（默认 `authagonal-admin`）会返回 `403 forbidden_scope`。任何客户端都不得持有管理作用域，否则 `client_credentials` 客户端就能无限期地签发管理令牌。
- 添加调用方无权授予的作用域会返回 `403`。

## 作用域 {#scopes}

在运行时管理自定义 OAuth 作用域。完整的作用域模型参见 [OAuth 作用域](scopes)。

```
GET    /api/v1/scopes           # List all scopes
GET    /api/v1/scopes/{name}    # Get one scope
POST   /api/v1/scopes           # Create a scope
PUT    /api/v1/scopes/{name}    # Update a scope (only supplied fields change)
DELETE /api/v1/scopes/{name}    # Delete a scope
```

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing, read-only",
  "description": "View invoices and payment history",
  "userClaims": ["billing_plan"]
}
```

创建时返回 `201`（如果作用域已存在则返回 `409`），获取/更新时返回作用域 JSON，删除时返回 `204`。

## 预配应用 {#provisioning-apps}

在运行时管理下游预配目标。所有路由都要求 `IdentityAdmin` 策略。

```
GET    /api/v1/provisioning/apps               # List apps (also returns the configured limit)
POST   /api/v1/provisioning/apps               # Create an app
PUT    /api/v1/provisioning/apps/{appId}       # Update an app
DELETE /api/v1/provisioning/apps/{appId}       # Delete an app
POST   /api/v1/provisioning/apps/{appId}/test  # Send a test /try call to the app's callback
```

### 创建 / 更新预配应用 {#create--update-provisioning-app}

```
POST /api/v1/provisioning/apps
Content-Type: application/json

{
  "name": "Backend",
  "callbackUrl": "https://api.example.com/provisioning",
  "apiKey": "secret-api-key",
  "tryTimeoutSeconds": 30
}
```

- `name` 和 `callbackUrl` 是必需的；`callbackUrl` 必须是绝对的 `http(s)` URL。
- `tryTimeoutSeconds` 会被限制在 5–300 的范围内。
- **永远不返回 API 密钥。**响应中公开的是 `hasApiKey`（布尔值），而不是密钥本身。更新时，省略 `apiKey` 会保持不变，空字符串会清除它，提供一个值则会替换它。
- 创建受一个可按部署配置的配额（`IProvisioningAppQuota`）约束；超出时返回 `400 provisioning_app_limit`。列表响应中包含当前的 `limit`。

### 测试预配应用 {#test-a-provisioning-app}

```
POST /api/v1/provisioning/apps/{appId}/test
```

发送一个带有示例载荷的合成 `POST {callbackUrl}/try`（如果设置了应用的 API 密钥，则将其作为持有者令牌一并发送），并返回 `{ success, statusCode, body }`，以便你从管理界面验证连通性。

## 角色 {#roles}

### 列出角色 {#list-roles}

```
GET /api/v1/roles
```

### 获取角色 {#get-role}

```
GET /api/v1/roles/{roleId}
```

### 创建角色 {#create-role}

```
POST /api/v1/roles
Content-Type: application/json

{
  "name": "admin",
  "description": "Administrator role"
}
```

### 更新角色 {#update-role}

```
PUT /api/v1/roles/{roleId}
Content-Type: application/json

{
  "name": "admin",
  "description": "Updated description"
}
```

### 删除角色 {#delete-role}

```
DELETE /api/v1/roles/{roleId}
```

### 为用户分配角色 {#assign-role-to-user}

```
POST /api/v1/roles/assign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

分配依据的是**角色名称**，而不是角色 ID。返回用户更新后的角色列表。

### 取消用户的角色分配 {#unassign-role-from-user}

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

### 获取用户的角色 {#get-users-roles}

```
GET /api/v1/roles/user/{userId}
```

### 拥有某角色的用户 {#users-in-a-role}

```
GET /api/v1/roles/{roleName}/users?maxResults=200
```

与上面相反（谁持有这个角色），通过角色成员资格索引来回答，而不是
逐一读取每个用户。返回 `{ "roleName": "...", "members": [ { "userId", "email", "firstName",
"lastName", "roles" } ] }`；每个成员都带有其完整的角色集合，因为列出某个角色的控制台
几乎总是想同时显示其成员还拥有哪些其他角色。

对于不存在的角色，返回 `404 role_not_found`，而不是空列表：“没有人持有这个角色”
和“你把角色名拼错了”是两个不同的问题。如果配置的存储不为角色成员资格建立索引，
则出于同样的原因返回 `501 not_supported`：空的成员列表会被理解为
“没有人管理这个”。

在该索引出现之前写入的账户在重新索引之前对它是不可见的
（`IUserStore.ReindexUserAsync`，它会 upsert 用户的成员资格，而不会移除任何成员资格）。

## 组织 {#organizations}

组织、它们的成员以及邮箱域名(其含义参见[组织](organizations))。失败为 `{ "error": "<code>", "error_description": "..." }`,状态为 `400`(输入无效)、`404`(未找到)或 `409`(冲突)。

列表使用 `?cursor={cursor}&limit={limit}` 分页:`limit` 限制在 1..200(默认 50),响应为 `{ "items": [...], "nextCursor": "..." }`,最后一页的 `nextCursor` 为 null。请原样传回 `nextCursor`;列表未发出过的游标返回 `400 invalid_cursor`。

组织的结构为 `{ "id", "slug", "name", "brandingJson", "metadata", "enabled", "requireMembershipForTokens", "allowAutoMembership", "createdAt", "updatedAt", "domains" }`。

### 列出组织 {#list-organizations}

```
GET /api/v1/organizations?cursor={cursor}&limit={limit}
```

`200`,按 id 排序。`400 invalid_cursor`。

### 创建组织 {#create-organization}

```
POST /api/v1/organizations
Content-Type: application/json

{
  "slug": "acme",
  "name": "Acme Corp",
  "brandingJson": "{\"appName\":\"Acme\",\"primaryColor\":\"#1a56db\"}",
  "enabled": true,
  "requireMembershipForTokens": true,
  "allowAutoMembership": false,
  "metadata": { "plan": "enterprise" }
}
```

`slug` 和 `name` 必填;其余可选,省略时保留组织的默认值。`brandingJson` 是包含 JSON 对象的字符串。返回 `201`,带有 `Location` 头和组织本身;其 `id` 为 `org_` 后接 32 位十六进制数字。

| 状态 | `error` | 何时 |
|---|---|---|
| 400 | `slug_required` | 缺少 `slug` 或为空白 |
| 400 | `slug_invalid` | `slug` 不是由 1 到 64 个小写字母、数字和内部连字符组成 |
| 400 | `name_required` | 缺少 `name` 或为空白 |
| 400 | `invalid_branding_json` | `brandingJson` 不是 JSON 对象 |
| 409 | `slug_taken` | 另一个组织已将该值用作 slug 或 id,包括并发创建的情况 |

### 获取组织 {#get-organization}

```
GET /api/v1/organizations/{idOrSlug}
```

`200`,先按 id 查找,再按 slug 查找。`404 organization_not_found`。

### 更新组织 {#update-organization}

```
PUT /api/v1/organizations/{id}
Content-Type: application/json

{ "name": "Acme Corporation", "enabled": true }
```

所有字段都是可选的,并且只更改自身:`name`、`brandingJson`、`enabled`、`requireMembershipForTokens`、`allowAutoMembership` 和 `metadata`(整体替换;`{}` 会清空它)。`slug` 仅为在其不同时拒绝而被接受。返回 `200` 和组织。`404 organization_not_found`、`400 slug_immutable`、`400 invalid_branding_json`。

### 删除组织 {#delete-organization}

```
DELETE /api/v1/organizations/{id}
```

先删除所有成员关系,再删除组织。`204`;`404 organization_not_found`。

### 列出成员 {#list-members}

```
GET /api/v1/organizations/{id}/members?cursor={cursor}&limit={limit}
```

按用户 id 排序的一页 `{ "organizationId", "userId", "email", "name", "status", "roles", "createdAt", "updatedAt" }`;`email` 和 `name` 来自用户记录,用户已不存在时为 null。`404 organization_not_found`、`400 invalid_cursor`。

### 添加成员 {#add-member}

```
POST /api/v1/organizations/{id}/members
Content-Type: application/json

{ "userId": "user-id", "status": "active", "roles": ["Auditor"] }
```

通过 `userId`(优先检查)或 `email` 指定一个已有用户。`status` 为 `invited`、`active`(默认)或 `suspended`;`roles` 可选。返回 `201`,带有 `Location` 头和成员关系。

| 状态 | `error` | 何时 |
|---|---|---|
| 404 | `organization_not_found` | 没有该组织 |
| 404 | `user_not_found` | `userId` 和 `email` 都不指向任何用户 |
| 409 | `membership_exists` | 用户已经是成员 |
| 400 | `invalid_role` | 某个角色以 `tenant:` 或 `platform:` 开头 |
| 400 | `invalid_status` | `status` 不是这三个值之一 |

### 更新成员 {#update-member}

```
PUT /api/v1/organizations/{id}/members/{userId}
Content-Type: application/json

{ "status": "suspended", "roles": [] }
```

两个字段都是可选的。返回 `200` 和成员关系。`404 membership_not_found`、`400 invalid_status`、`400 invalid_role`。

### 移除成员 {#remove-member}

```
DELETE /api/v1/organizations/{id}/members/{userId}
```

`204`;`404 membership_not_found`。

### 添加域名 {#add-domain}

```
POST /api/v1/organizations/{id}/domains
Content-Type: application/json

{ "domain": "acme.com" }
```

申领该域名(未验证)。域名会先转为小写并去除空白,且必须是 `acme.com` 这样的简单名称。返回 `201`,带有 `Location` 头和 `{ "domain", "verified", "verifiedAt", "createdAt", "recordName", "recordValue" }`;请在 `recordName` 处发布值为 `recordValue` 的 TXT 记录(参见[域名验证](organizations#domain-verification))。

| 状态 | `error` | 何时 |
|---|---|---|
| 404 | `organization_not_found` | 没有该组织 |
| 400 | `domain_invalid` | 不是简单的域名 |
| 409 | `domain_exists` | 该组织已申领它 |
| 409 | `domain_taken` | 另一个组织已申领它,无论是否已验证 |

### 验证域名 {#verify-domain}

```
POST /api/v1/organizations/{id}/domains/{domain}/verify
```

查询 TXT 记录。返回 `200` 和域名(`verified: true`),包括已验证的情况。`404 organization_not_found`、`404 domain_not_found`、`409 domain_taken`,以及记录缺失或错误时的 `409 verification_failed`;其 `error_description` 给出需要发布的名称和值,以及实际找到的内容。

### 移除域名 {#remove-domain}

```
DELETE /api/v1/organizations/{id}/domains/{domain}
```

`204`。成员不受影响。`404 organization_not_found`、`404 domain_not_found`。

### 回填组织 {#backfill-organizations}

```
POST /api/v1/organizations/backfill
Content-Type: application/json

{ "dryRun": true }
```

根据用户旧的 `OrganizationId` 创建组织和成员关系。无论有无请求体,`dryRun` 默认为 true,此时不会写入任何内容。返回 `200`,内容为 `{ "dryRun", "organizationsCreated", "membershipsCreated", "usersSkipped", "organizations": [ { "legacyValue", "slug", "users" } ], "truncated" }`。参见[回填](organizations#backfill)。

## SCIM 令牌 {#scim-tokens}

### 生成令牌 {#generate-token}

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id",
  "description": "Entra provisioning",
  "expiresInDays": 365
}
```

`description` 和 `expiresInDays` 是可选的（省略 `expiresInDays` 即可得到永不过期的令牌）。原始令牌只返回一次。请妥善保存，之后无法再次获取。

`organizationId` 是可选的,会给令牌创建的每个用户打上标签(参见[组织](organizations#membership-from-a-scim-token))。它必须指向一个已存在的组织:否则结果为 `400 unknown_organization`,格式错误的值返回 `400 invalid_request`。

### 列出令牌 {#list-tokens}

```
GET /api/v1/scim/tokens?clientId=client-id
```

返回令牌元数据（ID、创建日期），不包含原始令牌值。

### 撤销令牌 {#revoke-token}

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## 令牌 {#tokens}

### 模拟用户 {#impersonate-user}

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid%20profile
```

代表用户签发令牌（访问令牌、刷新令牌，以及在请求 `openid` 时的 ID 令牌），无需用户的凭据。适用于测试和支持场景。参数以查询字符串的形式传递。

| 查询参数 | 必需 | 说明 |
|---|---|---|
| `clientId` | 是 | 令牌签发给的客户端。令牌生命周期取自该客户端的配置。 |
| `userId` | 是 | 要模拟的用户。 |
| `scopes` | 否 | 以**空格分隔**的作用域列表（对空格进行 URL 编码）。省略时默认为客户端的 `AllowedScopes`。 |

限制：

- 作用域受客户端的 `AllowedScopes` 约束，请求任何该客户端自身无法请求的作用域都会返回 `400 invalid_scope`。
- 管理作用域（`AdminApi:Scope`，默认 `authagonal-admin`）**不能**通过此端点签发；请求它会返回 `403 forbidden_scope`。这可以防止一个（可能有时效限制的）管理令牌签发出长期有效的管理访问令牌/刷新令牌。

响应是标准的令牌响应，包含 `access_token`、`refresh_token`、可选的 `id_token`、`expires_in`，以及被授予的 `scope`（以空格分隔）。
