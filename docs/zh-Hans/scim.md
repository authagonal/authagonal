---
layout: default
title: SCIM 2.0 预配
locale: zh-Hans
nav_order: 13
---

# SCIM 2.0 预配

Authagonal 支持 SCIM 2.0（System for Cross-domain Identity Management，跨域身份管理系统），用于从 Microsoft Entra ID、Okta、OneLogin 等企业身份提供方自动预配用户。

## 概述 {#overview}

SCIM 是一种入站预配协议：由你的身份提供方将用户和组的变更推送到 Authagonal。它与现有的 TCC（Try-Confirm-Cancel）出站预配互为补充，后者负责将用户推送到下游应用。

**支持的操作：**
- 用户 CRUD（创建、读取、更新，以及通过软停用实现的删除）
- 组 CRUD 及成员管理
- 过滤（对 `userName`、`externalId`、`displayName` 使用 `eq` 和 `co` 运算符）
- 分页：用户和组都使用基于游标的分页（`cursor`/`nextCursor`）；为兼容现有客户端，组仍接受 `startIndex`，但不对外公布
- 用于部分更新的 PATCH（包括 `active=false` 停用）
- 在令牌签发时解析组到角色的映射

**不支持：**批量操作、排序、ETag、通过 SCIM 管理密码。

所有资源都限定在预配它们的那个 SCIM 客户端范围内：由某个 SCIM 令牌所属客户端创建的用户或组，对其他任何 SCIM 客户端都不可见（404）。

## 生成 SCIM 令牌 {#generating-a-scim-token}

SCIM 端点使用静态 Bearer 令牌进行身份验证。通过 Admin API 生成令牌：

```http
POST /api/v1/scim/tokens
Authorization: Bearer {admin-token}
Content-Type: application/json

{
  "clientId": "your-client-id",
  "description": "Entra ID SCIM token",
  "expiresInDays": 365,
  "organizationId": "org_acme",
  "allowedEmailDomains": ["acme.example", "acme-eu.example"]
}
```

响应中**仅此一次**包含原始令牌。令牌以 SHA-256 哈希形式存储，之后无法找回，因此请妥善保存：

```json
{
  "tokenId": "abc123",
  "clientId": "your-client-id",
  "token": "base64-encoded-token",
  "description": "Entra ID SCIM token",
  "createdAt": "2024-01-01T00:00:00Z",
  "expiresAt": "2025-01-01T00:00:00Z",
  "organizationId": "org_acme",
  "allowedEmailDomains": ["acme.example", "acme-eu.example"]
}
```

省略 `expiresInDays`（或传入 `0`）即可得到永不过期的令牌。

### 为连接器的用户标记组织 {#tagging-a-connectors-users-with-an-organization}

`organizationId` 是可选的。设置后，通过该令牌预配的每个用户在写入时都会带上该
`OrganizationId`，并在其令牌中以 `org_id` 声明发出。SCIM 没有办法让连接器
说明它正在同步的是你的哪个客户：核心 SCIM 没有定义组织属性，而
企业扩展也未实现（参见下文的*架构支持*）。将组织绑定到凭据上，
无需为每个客户各建一个 OAuth 客户端就能解决这个问题。

如果省略，用户就不会被标记，这也是该功能出现之前所有令牌的行为。系统绝不会
从客户端 ID 推导出任何组织。

两条规则：

- **仅在创建时生效。**之后通过带有不同标记的令牌进行同步，不会重新标记已有的账户。
- **它先于预配执行。**TCC `/try` 响应只会填充仍为空的组织
  （参见[预配](provisioning.md)），因此凭据上的显式绑定优先；并且 `/try` 载荷
  会携带绑定的值，以便下游应用知道这次同步来自哪个客户。

当 `organizationId` 指向一个已存在的[组织](organizations)时，创建操作还会写入该组织的一条 `active` 成员资格（不带角色），并审计 `scim.organization_member_added`，这样该用户之后就不会被组织的成员资格检查拒绝签发令牌。如果 ID 不指向任何组织，则仍只是一个单纯的 `org_id` 标记，这与组织功能出现之前签发的令牌行为一致。与标记一样，成员资格也只在创建时写入。

> **标记不等于隔离。**所有权按**客户端**而不是按令牌来强制执行。针对同一客户端
> 签发的两个令牌是同一个身份、两个密钥，每一个都可以读取、重命名、停用和
> 删除另一个创建的内容。如果所有令牌都由同一方持有，这没有问题。如果互不信任的
> 多个连接器各自持有令牌，请为它们各分配一个客户端。

### 限定连接器可以创建哪些身份 {#bounding-which-identities-a-connector-may-create}

`allowedEmailDomains` 是唯一能控制 SCIM 凭据可以预配**哪些**用户的手段。请设置它。

省略它会得到一个不受限制的令牌，而“不受限制”的范围比听起来更大。SCIM 创建的用户在写入时
带有 `EmailConfirmed = true`（从那一刻起，该地址即被视为已证明），因此不受限制的
连接器可以把 `ceo@some-other-company.example` 创建为一个预先验证的账户。当真正的
所有者之后通过联合登录时，一个没有任何现有外部登录的记录会被接管而不是被
拒绝，于是他们的登录会绑定到这个账户上；又因为 `ScimProvisionedByClientId` 仍然指向
创建它的连接器，该连接器对此对象保有完全的所有权：它可以读取个人资料、重命名
`userName`、停用账户（这会撤销所有授权），或者删除账户，删除会清除该用户的通行密钥和
组成员资格，并为该行写入墓碑，使该域名合法的连接器在每个
操作上都得到 404。

省略该字段的令牌会在签发时记录一条警告，其中注明令牌 ID。

请提供裸域名（`acme.example`，而不是 `@acme.example`，也不是一个邮箱地址）。永远不可能匹配的值会
被拒绝而不是保存，因为一个什么都不允许的限定，看起来与一个配置错误的连接器毫无二致。

运维人员也可以在配置中设置限定：

```json
{
  "Scim": {
    "Clients": {
      "your-client-id": { "AllowedEmailDomains": ["acme.example"] }
    }
  }
}
```

两者取**交集**，任一来源的空列表都表示“此来源不施加限定”。因此两者都
为空时不受限制；只设置其中一个时，就单独应用那一个；两者都设置时，只有同时出现在两者中的域名才
被允许：签发令牌可以收窄运维人员配置的限定，但永远不能放宽它。

创建、`PUT` 和 `PATCH` 都同样执行该检查，因此重命名无法把账户移到凭据
不允许预配的域名中。

### 列出令牌 {#listing-tokens}

```http
GET /api/v1/scim/tokens?clientId=your-client-id
Authorization: Bearer {admin-token}
```

### 撤销令牌 {#revoking-a-token}

```http
DELETE /api/v1/scim/tokens/{tokenId}?clientId=your-client-id
Authorization: Bearer {admin-token}
```

## 配置你的身份提供方 {#configuring-your-identity-provider}

### 租户 URL {#tenant-url}

```
https://your-authagonal-instance/scim/v2
```

### 身份验证 {#authentication}

使用 **OAuth Bearer Token**，填入上面生成的令牌。

### Microsoft Entra ID {#microsoft-entra-id}

1. 在 Azure 门户中，进入 **Enterprise Applications** > 你的应用 > **Provisioning**
2. 将 Provisioning Mode 设置为 **Automatic**
3. 输入 Tenant URL：`https://your-instance/scim/v2`
4. 输入 Secret Token：生成步骤中得到的原始令牌
5. 点击 **Test Connection** 进行验证
6. 配置属性映射（见下文）

### Okta {#okta}

1. 在 Okta 管理控制台中，进入 **Applications** > 你的应用 > **Provisioning**
2. 启用 **SCIM connector**
3. 设置 Base URL：`https://your-instance/scim/v2`
4. 设置 Authentication Mode：**HTTP Header**
5. 输入 Bearer 令牌

### OneLogin {#onelogin}

1. 在 OneLogin 管理后台中，进入 **Applications** > 你的应用 > **Provisioning**
2. 启用预配
3. 设置 SCIM Base URL：`https://your-instance/scim/v2`
4. 设置 SCIM Bearer Token

## SCIM 端点 {#scim-endpoints}

| 方法 | 路径 | 说明 |
|--------|------|-------------|
| GET | `/scim/v2/Users` | 列出/过滤用户 |
| GET | `/scim/v2/Users/{id}` | 获取用户 |
| POST | `/scim/v2/Users` | 创建用户 |
| PUT | `/scim/v2/Users/{id}` | 替换用户 |
| PATCH | `/scim/v2/Users/{id}` | 部分更新 |
| DELETE | `/scim/v2/Users/{id}` | 写入墓碑（停用；之后的 GET 返回 404） |
| GET | `/scim/v2/Groups` | 列出/过滤组 |
| GET | `/scim/v2/Groups/{id}` | 获取组 |
| POST | `/scim/v2/Groups` | 创建组 |
| PUT | `/scim/v2/Groups/{id}` | 替换组 |
| PATCH | `/scim/v2/Groups/{id}` | 添加/移除成员 |
| DELETE | `/scim/v2/Groups/{id}` | 删除组 |
| GET | `/scim/v2/ServiceProviderConfig` | 能力说明 |
| GET | `/scim/v2/Schemas` | 架构定义 |
| GET | `/scim/v2/ResourceTypes` | 资源类型 |

每个端点也都映射了不带 `/v2` 段的版本（例如 `/scim/Users`），以兼容会自行追加路径的身份提供方。发现端点（`ServiceProviderConfig`、`Schemas`、`ResourceTypes`，以及返回 ServiceProviderConfig 的裸 `/scim/` 和 `/scim/v2/` 基础 URL）允许匿名访问；其余所有端点都需要 SCIM Bearer 令牌。

用户和组端点按每个 SCIM 客户端每分钟 200 个请求进行限流；超出的请求会收到状态为 `429` 的 SCIM 错误。

## 属性映射 {#attribute-mapping}

### 用户属性 {#user-attributes}

| SCIM 属性 | Authagonal 字段 |
|---------------|------------------|
| `userName` | `Email` |
| `name.givenName` | `FirstName` |
| `name.familyName` | `LastName` |
| `displayName` | `FirstName LastName` |
| `emails[type eq "work"].value` | `Email` |
| `active` | `IsActive` |
| `externalId` | `ExternalId` |
| `preferredLanguage`（没有时退回使用 `locale`） | `Locale` |

### 组属性 {#group-attributes}

| SCIM 属性 | Authagonal 字段 |
|---------------|------------------|
| `displayName` | `DisplayName` |
| `externalId` | `ExternalId` |
| `members` | `MemberUserIds` |

### 架构支持 {#schema-support}

仅支持核心 SCIM 2.0 的 `User` 和 `Group`（RFC 7643）。上面的表格就是完整的支持集合。

**企业用户扩展未实现**，因此 `employeeNumber`、`costCenter`、`organization`、
`division`、`department` 和 `manager` 会被接受但忽略，不会保存；创建、替换和
PATCH 都是如此。Entra 和 Okta 的默认属性映射中包含其中几项，因此使用现成的连接器
无需删除这些映射。（在 0.27.0 之前，携带其中任何一项的 PATCH 会被**整个**拒绝，返回
`400 invalidPath`，导致每次增量同步都失败，而创建却能成功；并且可能让一次
`active: false` 的取消预配因为一个无关属性而被搁置。）

这项放宽是有限度的：拼写错误的**核心**路径（例如 `name.givenNam`）仍然返回 `400`，
只读属性（`id`、`meta`、`groups`）仍会因 `mutability` 而被拒绝。

注意，企业扩展的 `organization` 属性**不会**成为用户的 `org_id`。该值
由客户自己的身份提供方声明，而上文的凭据绑定由
运维人员设置；请改用令牌上的 `organizationId`。

## 行为细节 {#behavior-details}

### 用户创建 {#user-creation}
- 通过 SCIM 预配的用户在创建时带有 `EmailConfirmed = true`（仅 SSO，无密码）。
- `ScimProvisionedByClientId` 字段记录是哪个 SCIM 客户端创建了该用户。
- 如果客户端配置了 `ProvisioningApps`，会自动触发 TCC 预配。如果预配拒绝了该用户，SCIM 创建会被回滚，响应为 SCIM `400`，带有 `scimType: invalidValue` 和一条固定消息（有意不把下游应用自己的文本回显给 SCIM 客户端）。
- 创建一个 `userName` 或 `externalId` 已存在的用户会返回 SCIM `409` 冲突。通过 PUT 或 PATCH 修改邮箱时，也以同样的方式检查冲突。

### 用户停用 {#user-deactivation}
- `DELETE /scim/v2/Users/{id}` 会为资源**写入墓碑**：停用该用户，保留本地记录，并标记 `ScimDeletedAt`。之后的 `GET /scim/v2/Users/{id}` 会返回 **404**，这是 RFC 7644 §3.6 的要求（“the service provider MUST return a 404 for all operations associated with the previously deleted resource”）。不要通过回读资源并期望得到 `active: false` 来确认取消预配。读取返回 404，这本身就代表成功。
- 记录被保留而不是被抹除，以便重新入职的员工可以被重新创建：墓碑会释放新资源所需的 `userName`/`externalId`，而本地账户、其审计历史和组成员资格都会保留下来。
- 带 `active = false` 的 `PATCH` 同样会停用用户。
- 已停用的用户无法通过密码、SAML 或 OIDC 登录。
- 停用时会撤销所有授权（刷新令牌、会话）。
- 下游应用的取消预配只由 `DELETE` 触发；`PATCH` 停用会撤销授权，但不会触及下游应用。

### 过滤 {#filtering}
支持 RFC 7644 §3.4.2.2 的完整过滤器语法。

**运算符：**`eq`、`ne`、`co`、`sw`、`ew`、`gt`、`ge`、`lt`、`le` 以及 `pr`（存在性）。
**逻辑运算：**`and`、`or`、`not (...)`，支持括号分组。`and` 的优先级高于 `or`。
**路径：**子属性（`name.givenName`）、多值属性（`emails.value`）、值路径（`emails[type eq "work"].value`）以及带 URN 前缀的名称（`urn:ietf:params:scim:schemas:core:2.0:User:userName`）。

```
userName eq "user@example.com"
userName sw "sales-" and active eq true
emails[type eq "work"].value co "@acme.com"
not (title pr)
meta.lastModified gt "2026-01-01T00:00:00Z"
```

语义遵循 RFC：字符串比较不区分大小写；多值属性只要有任一元素匹配即视为匹配；属性不存在时，除 `ne` 外的所有比较都为假。不是有效 SCIM 过滤器的输入会以 `400` 和 `scimType: invalidFilter` 拒绝，并指明问题所在。

**性能。**`userName eq` 和 `externalId eq`（Entra 和 Okta 在每次创建或更新之前发出的查询）通过索引点查询解析，而不是扫描列表，因此无论用户数量多少都能保持快速。其他所有过滤器都在分页遍历该客户端的用户时求值，且有边界：用户 PII 是静态加密的，只能通过盲索引搜索，因此更复杂的谓词无法下推到存储层。在游标分页下，只要存在 `nextCursor`，`totalResults` 就会被**省略**；当 `nextCursor` 不再出现时，它才是准确的总数。参见“分页”。

### 分页 {#pagination}
用户列表使用**游标分页**。`GET /scim/v2/Users` 的每一页都会在列表响应中返回一个 `nextCursor` 属性；将它作为 `?cursor=` 传回即可获取下一页。当 `nextCursor` 不存在时，列表即已完整。页面大小由 `count` 控制（默认 100，最大 200）。

在 Users 端点上请求大于 1 的 `startIndex` 会返回 `400` 错误，并引导你改用游标分页；不提供越过第一页的偏移分页。只要存在 `nextCursor`，`totalResults` 就会被**完全省略**，只有在最后一页才携带准确的总数。它有意不报告所返回页面的大小：曾有同步客户端读取 `totalResults`，发现它等于刚收到的资源数量，便断定自己已拿到整个目录，结果悄无声息地漏读了租户的数据。请以 `nextCursor` 驱动循环，永远不要以 `totalResults` 驱动，并把缺失的 `totalResults` 视为“尚未知晓”，而不是零。

**组列表同样使用游标分页。**`GET /scim/v2/Groups` 无论是否带过滤器都会返回 `nextCursor`；
以同样的方式跟随它即可。为兼容已在使用的客户端，Groups 仍接受 `startIndex`，
但它**不会**在 `ServiceProviderConfig` 中公布，也不应依赖它：`pagination.index` 是
对整个提供方的声明，而不是针对某一个集合，而 `/Users` 不支持它，所以唯一处处
成立的值是 `false`。请使用游标，它在两者上都可用。

带过滤器的组列表按有限窗口扫描，而不是把整个租户都加载出来，因此它可能在后面
仍有匹配项时返回一个空页。出现这种情况时，它会返回 `nextCursor` 并**省略**
`totalResults`：带游标的空页表示“继续”，不带游标的空页则表示过滤后的集合确实为空。
不要把第一个空页当作集合的末尾。

在两个集合上，`count=0` 都会返回 `totalResults` 而不返回任何资源（RFC 7644 §3.4.2.4），负数的
`count` 会以 `400` 拒绝，而不是被截取到有效范围。

### 通过 PATCH 管理组成员 {#group-membership-via-patch}
`PATCH /scim/v2/Groups/{id}` 接受主流身份提供方实际发送的各种成员资格格式：

- **添加成员：**`op: "add"`，`path: "members"`，值为由 `{ "value": "user-id" }` 对象组成的数组。重复项会被忽略。
- **替换成员：**`op: "replace"`，`path: "members"`，用提供的数组替换全部成员。
- **移除特定成员（值数组）：**`op: "remove"`，`path: "members"`，值为要移除的成员 ID 数组（Entra ID 发送的格式）。
- **移除特定成员（路径过滤器）：**`op: "remove"`，`path: 'members[value eq "user-id"]'`，ID 携带在路径过滤器中，不带值（Okta 在取消预配时发送的格式）。
- **移除所有成员：**`op: "remove"`，`path: "members"` 且不带值，会清空该组。

### 组到角色的映射 {#group-to-role-mapping}
SCIM 组的成员资格可以授予应用角色。映射按每个（组，角色）对一行存储，一个组可以授予多个角色。映射在**令牌签发时**解析：用户的有效角色等于其直接分配的角色加上其所属的每个已映射组的角色，因此添加或移除组成员会在下一个令牌中生效，而无需修改用户记录。映射存储为空时不产生任何作用。

映射通过 `IScimGroupRoleMappingStore` 持久化（由 Azure 和 AWS 存储提供程序实现；否则注册一个内存中的默认实现），并由托管应用自己的管理界面管理，而不是通过 SCIM API 本身。

此外，启用了 `IncludeGroupsInTokens` 的客户端还会在签发的令牌中以 `groups` 声明收到用户所属 SCIM 组的显示名称。

## 已知限制 {#known-limitations}

- **没有批量操作：**用户和组必须逐个预配。
- **没有排序：**在游标分页下，用户列表按存储顺序返回；组列表按创建日期排序。
- **没有密码管理：**通过 SCIM 预配的用户只能通过 SSO 进行身份验证。
- **写入墓碑而不是抹除：**`DELETE` 会停用资源并为其写入墓碑（根据 RFC 7644 §3.6，之后的 `GET` 返回 404），而不是永久删除本地用户记录。如需抹除，请使用管理 API。
