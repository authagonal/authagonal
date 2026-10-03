---
layout: default
title: 组织
locale: zh-Hans
nav_order: 14
---

# 组织

组织是你租户内的一个客户。一个部署可以服务许多组织：每个组织都有自己的身份、自己的成员，以及在你的应用所收到的令牌上属于它自己的 `org_id`。

## 概述 {#overview}

在引入组织之前，用户记录携带一个 `OrganizationId` 字符串（由 TCC 预配或 SCIM 令牌绑定写入），该字符串以 `org_id` 声明的形式发出。但没有任何地方能说明这个组织*是什么*、谁属于它，或者它到底能否被用作身份验证的目标。

`Organization` 为其提供了一条记录：一个不可变的不透明 ID、一个不可变且在租户内唯一的 slug、一个显示名称、一个启用标志、一个元数据集合，以及一个品牌覆盖。`OrganizationMembership` 记录了谁属于该组织，它才是真正授权为该组织签发令牌的依据。

**它的用途。**一个按客户部署产品的 ISV（每个客户一个应用实例、一个数据库，按主机名解析）注册一个租户，并为每个客户注册一个组织。它的应用从访问令牌上读取 `org_id`，并拒绝任何不属于它所服务实例的请求。应用过去自己做出的路由决策，现在由授权服务器做出，并由一个签名声明加以证明。

**租户仍然是隔离边界。**一个签名密钥、一个签发者、一个用户存储。组织在该边界*之内*对身份进行划分；它不会创建第二个边界。同一租户中的两个组织共享一个用户目录，一个用户可以属于多个组织。

**尚不支持：**

- **没有组织选择器。**属于多个组织的用户，在未指定任何组织的请求上会得到 `account_selection_required`，依赖方可以据此带上参数重试。没有让用户进行选择的托管界面。
- **没有委派的组织管理。**没有任何权限能让客户自己的管理员管理其成员。
- **没有按组织隔离的 SCIM。**绑定到组织的 SCIM 令牌（`ScimToken.OrganizationId`）会标记其创建的用户；当该 ID 指向一个真实的组织时，还会使这些用户成为活跃成员（参见[来自 SCIM 令牌的成员资格](#membership-from-a-scim-token)）。所有权检查仍以 OAuth 客户端而不是组织为键，因此该绑定决定的是标记，而不是访问。
- **本库中没有邀请流程。**没有邀请端点，也没有邀请邮件。宿主需要自行写入 `invited` 成员资格；它变为 `active` 的途径见[邀请](#invitations)。
- **没有组织范围的组。**`groups` 声明和 SCIM 组成员资格仍是租户范围的；只有角色是组织范围的。
- **没有组织 Webhook 事件，也没有组织范围的审计。**`IAuthHook` 没有组织生命周期事件（创建、授予或撤销成员资格），现有的钩子载荷不携带 `organizationId`，审计日志上也没有组织列或索引。
- **受角色限制的作用域在授权时依据租户角色进行过滤。**`Scope.AllowedRoles` 在 `/connect/authorize` 上依据账户直接分配的角色进行检查，这发生在组织解析之前，因此一个只有组织范围角色才能满足其 `AllowedRoles` 的作用域会在授权时被丢弃；如果所请求的作用域无一保留，则以 `access_denied` 拒绝。刷新时，同样的检查依据已解析主体的角色运行，而这些角色确实包含组织的角色。在两者达成一致之前，请让作用域依据租户角色进行限制。
- **本库中没有组织品牌定制。**`Organization.BrandingJson` 被存储下来，供宿主将其合并到租户的品牌配置之上；本库中没有任何组件读取它。当宿主的启动载荷携带 `organization`（`{ id, slug, name }`）时，登录应用会显示组织名称（“正在登录 {name}”）；除了通过 `organization` 参数、只含一个条目的客户端限制、组织范围的连接，或宿主的 `ITenantContext.OrganizationId` 之外，本库本身在身份验证之前不会解析任何组织。
- **没有组织管理 REST API。**接口面是 `IOrganizationStore` 和 `IOrganizationMembershipStore`；需要端点的宿主自行构建。列出组织或成员的宿主应使用 `ListPageAsync` / `ListByOrganizationPageAsync`（见下文）。

## 创建组织 {#creating-an-organization}

组织通过 `IOrganizationStore` 存储。在创建任何组织之前，必须先有一个持久化实现。内置的默认实现是空的且只读的，会拒绝写入，并在消息中指出缺失的注册。这是有意为之：组织记录决定令牌的签发，而进程本地的字典会让每个尚未得知撤销的节点继续签发令牌。

```csharp
await organizationStore.UpsertAsync(new Organization
{
    Id = "org_7f3a",              // opaque, immutable, emitted as org_id
    Slug = "international-sos",   // tenant-unique, immutable, emitted as org_slug
    DisplayName = "International SOS",
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Slug` 必须匹配 `^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$`：1 到 64 个字符，由小写字母、数字和位于中间的连字符组成，不能以连字符开头或结尾。之所以要求小写，是因为 `organization` 参数在查找 slug 之前会被转换为小写，所以大写的 slug 将是任何请求都无法解析到的值。

`Slug` 在租户内必须唯一，并且 **ID 和 slug 共享同一个命名空间**：如果 upsert 的 slug 已被另一个组织占用，存储会拒绝它；同样，如果其 slug 等于另一个组织的 ID，或其 ID 等于另一个组织的 slug，也会被拒绝。两条记录对应同一个值，会使 `organization` 参数指向一个组织，而每个已存储的 ID 却指向另一个组织。

`Id` 必须匹配 `^[A-Za-z0-9._~-]{1,200}$`：与 `organization` 参数的形式相同，因此每个 ID 都始终可以作为该参数发送。不符合此形式的 ID 是任何请求都无法选中的 ID，而被限制到这种 ID 的客户端会拒绝每一个请求。

`Id` 还**应当**至少包含一个 slug 中不允许出现的字符：大写字母、`.`、`_` 或 `~`。ID 和 slug 共享同一个查找命名空间，而全小写的值会优先按 slug 解析，因此本身形似 slug 的 ID，之后可能会因为有人占用了这个 slug 而在创建时被拒绝；而带有非 slug 字符的 ID 则永远不会。新 ID 的推荐形式是带 `org_` 前缀的不透明值（`org_7f3a9c`）：`_` 在 slug 中不合法，因此仅凭这个前缀就能保证这一点。系统不强制任何约定，而该字段中已有的值是任意的：它们来自下游应用的 TCC `/try` 响应（`TccProvisioningOrchestrator`），或运维人员的 SCIM 令牌绑定（`ScimToken.OrganizationId`，由 `ScimUserEndpoints` 标记在新用户上）。

`Id` 和 `Slug` 在实践中都是不可变的。依赖方会将它们与自己所服务的实例进行比较，并会把它们硬编码，因此修改其中任何一个都会造成没有任何错误消息的服务中断。`DisplayName` 可以自由修改，它是界面上显示的内容。

## 授予成员资格 {#granting-membership}

```csharp
await membershipStore.UpsertAsync(new OrganizationMembership
{
    OrganizationId = "org_7f3a",
    UserId = user.Id,
    Status = MembershipStatus.Active,
    JoinedAt = DateTimeOffset.UtcNow,
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Status` 为 `invited`、`active` 或 `suspended`。**只有 `active` 才授权签发令牌。**采用暂停而不是删除，可以保留谁邀请了谁的记录。

### 邀请 {#invitations}

`invited` 行携带 `InvitedByUserId`、`InvitedAt`，以及向被邀请者提供的任何角色。本库从不发送邀请；它会在两种情况下将该行提升为 `active`（保留角色、邀请者和邀请时间，并标记 `JoinedAt`）：

- **通过组织范围的 SAML 或 OIDC 连接登录。**由该组织自己的 IdP 为此人担保，即视为接受邀请（`FederatedOrganizationBinding`，0.30.2）。否则，一个只通过 SSO 登录的被邀请者会被成员资格检查拒绝签发令牌。
- **自动成员资格**（见下文），当用户符合条件时。

`suspended` 行永远不会被上述任一途径提升，也永远不会因登录而被修改。

### 已验证域名与自动成员资格 {#verified-domains-and-automatic-membership}

`Organization.Domains` 保存组织已声明的邮箱域名，每个都是一个 `OrganizationDomain { Domain, VerificationToken, CreatedAt, VerifiedAt }`。`Domain` 以小写、去除首尾空白、不带末尾点号的形式存储。本库存储该声明并读取 `VerifiedAt`；证明对域名的控制权（通常是一条携带 `VerificationToken` 的 DNS TXT 记录）是宿主的工作，宿主在证明成功后标记 `VerifiedAt`。

`Organization.AllowAutoMembership`（默认关闭）允许用户无需邀请即可加入。当组织被**显式**选中（携带而来的刷新授权、`organization` 参数、组织范围的连接，或恰好被限制到该组织的客户端），且用户没有活跃的成员资格时，如果以下条件**全部**成立，用户就会成为成员：

- 组织已启用，且 `AllowAutoMembership` 已开启，
- `AuthUser.EmailConfirmed` 为 true，
- 邮箱中最后一个 `@` 之后的部分，转换为小写后，与某个设置了 `VerifiedAt` 的域名**完全**相等。已验证的 `acme.com` 不会接纳 `user@eu.acme.com`。

缺失的行会被创建为不带角色的 `active`；`invited` 行会被提升；`suspended` 行永远不会被触及。这一规则在授权时以及每次刷新时都会应用，因此在该标志开启期间，删除一个符合条件的成员的行并不能把他们拒之门外（他们会在下一次获取令牌时重新加入）；请改为暂停他们。仅从 `AuthUser.OrganizationId` 继承而来的组织永远不会自动加入。每次自动加入都会以 Information 级别记录日志。

### 来自 SCIM 令牌的成员资格 {#membership-from-a-scim-token}

使用 `organizationId` 签发的 SCIM 令牌会把该值作为 `org_id` 标记在它创建的每个用户上。当该 ID 指向一个已存在的组织时，创建操作还会写入一条不带角色的 `active` 成员资格，并审计 `scim.organization_member_added`。不指向任何组织的 ID 仍只是一个单纯的标记。仅在创建时生效：之后的同步不会重新标记或添加成员。参见 [SCIM](scim#tagging-a-connectors-users-with-an-organization)。

### 列出与删除 {#listing-and-deleting}

`IOrganizationStore.ListPageAsync(cursor, limit)` 和 `IOrganizationMembershipStore.ListByOrganizationPageAsync(organizationId, cursor, limit)` 返回一页结果（`Items` 和一个不透明的 `NextCursor`，最后一页时为 null）。`limit` 会被限制在 1..200 之间，格式错误的游标会抛出 `ArgumentException`。游标基于键集，因此在两次读取之间添加或删除的行永远不会导致页面错位或重复。两者都有基于不分页列表的默认实现，因此自定义存储仍能编译通过；Azure Table 存储则以服务器端范围查询重写了它们。

通过管理端的 `DELETE /api/v1/profile/{userId}`、SCIM 的 `DELETE /scim/v2/Users/{id}` 或 SCIM 回收路径删除用户时，也会删除该用户持有的每一条成员资格（使用带 `IOrganizationMembershipStore` 的 `AccountArtefactPurge.PurgeAsync`；三存储参数的重载不会清除任何成员资格）。拥有自己删除路径的宿主也必须传入成员资格存储，否则组织会继续列出已删除的成员。

## 组织范围的角色 {#organization-scoped-roles}

`OrganizationMembership.Roles` 保存用户在该组织**之内**拥有的角色。这些名称来自租户现有的角色目录：ISV 只需声明一次“Auditor”，每个客户都可以把它授予自己的人员。

```csharp
membership.Roles = ["Auditor", "Site Manager"];
```

它们会与用户直接分配的角色以及通过 SCIM 组成员资格授予的角色一起并入 `roles` 声明，并受同一个 `roles` 作用域限制。资源服务器无需知道某个角色是在租户范围还是按组织授予的，但它**必须**在读取 `roles` 的同时读取 `org_id`，因为同一个角色名称现在的含义是“在这个组织中”。

有四条规则对其加以约束：

- **只有被显式选中的组织才贡献角色**：即由 `organization` 参数或只含一个条目的客户端限制指定的组织。从 `AuthUser.OrganizationId` 继承而来的组织不贡献任何角色，这与成员资格检查的不对称性相同。
- **只有 `active` 成员资格才贡献角色。**已邀请但未接受的成员或被暂停的成员不授予任何角色，正如他们不授权任何东西一样。
- **角色永远不会跨越组织。**角色从以所选组织为键的成员资格行中读取，因此在一个组织中持有的角色不可能出现在为另一个组织签发的令牌中。
- **保留前缀会被剥离。**以 `tenant:` 或 `platform:` 开头的角色会在合并时被丢弃，并以 Warning 级别记录日志。成员资格行是客户范围的数据，因此一个能够授予 `tenant:admin` 的成员资格，会把“可以管理我自己的组织”变成“可以管理整个租户”。直接分配的角色和 SCIM 组→角色映射不受影响：它们由运维人员通过经过身份验证的管理界面写入，而这正是成员资格行所不具备的权限。

每次刷新轮换时都会从成员资格行重新读取角色，因此对角色的修改会在下一次刷新时到达活跃会话。

租户范围的角色会与组织的角色**合并**，而不是被其替换：`tenant:admin` 是门户权限，在选中某个组织后依然保留。

## 在授权请求中选择组织 {#selecting-an-organization-on-an-authorization-request}

发送 `organization`，值为组织的 slug 或 ID：

```http
GET /connect/authorize
  ?client_id=mobiom-web
  &response_type=code
  &redirect_uri=https://audit.example.com/callback
  &scope=openid%20profile
  &organization=international-sos
  &code_challenge=...&code_challenge_method=S256
```

该值必须匹配 `^[A-Za-z0-9._~-]{1,200}$`（RFC 3986 的非保留字符集）；其他任何值都会返回 `invalid_request`。它的解析方式取决于大小写：

- **含有任何大写字符 → 只按 ID 精确解析。**slug 只能是小写，因此这样的值不可能是 slug。如果仍将其转为小写后去查询 slug 索引，就相当于在问“是否有某个组织的 slug 恰好是这个 ID 的小写形式？”，如果真有，那么指定了某个 ID 的调用方就会被交给另一个客户。
- **全部小写 → 先按 slug，再按 ID。**它可能是两者中的任何一个，而依赖方通常发送的是 slug。这不会产生歧义，因为存储不允许 ID 和 slug 共用同一个值。

`org_slug` 和 `org_id` 被接受为别名：两者在其他提供方那里都有使用，而悄悄忽略本服务器没有选用的那一个，比两者都接受更糟。如果发送的两个参数指向*不同*的组织，会以 `invalid_request` 拒绝：这个请求同时表达了两种含义，无论服务器选择哪一个，依赖方得到的都会是另一个。重复发送三者中的任何一个都会被拒绝，理由与 `redirect_uri` 相同。

该参数在经过登录界面的往返后依然保留，因为整个授权 URL 会作为 `returnUrl` 传递。它也无需额外工作即可通过[推送授权请求](par)使用：PAR 端点会存储收到的每个字段，而 `/connect/authorize` 会读取推送的载荷而不是查询字符串。

### 优先级 {#precedence}

组织按以下顺序解析：

1. **刷新时，授权签发时所针对的组织。**
2. **[组织范围的 SSO 连接](self-service-sso#organisation-scoped-connections)为本次会话完成身份验证时所针对的组织。**这是这里唯一一个经过*证明*而非由调用方断言的来源：用户在一个恰好属于一个组织的 IdP 上完成了登录。指定了另一个组织的请求会以 `access_denied` 拒绝，而不是悄悄地为另一个组织签发。
3. **`organization` 参数。**
4. **`OAuthClient.RestrictedToOrganizationIds`，当它恰好只含一个条目时。**按客户部署的应用在注册时指定一次其组织，其依赖方根本无需发送任何参数。这正是大多数“每个客户一个实例”的产品所需要的形式。
5. **`AuthUser.OrganizationId`**：账户自身存储的组织。

规则 1-4 是*显式*选择，必须满足成员资格要求。规则 5 则不是：账户记录本身就是对归属关系的断言，如果再要求第二个断言，那么在对应组织被创建的那一刻，所有已有用户都会被锁在门外。

在任何人完成身份验证之前（主域发现、登录页面的提供方列表、`/sso-check`），既没有用户也没有授权，因此只会单独解析规则 3、规则 4，然后是 `ITenantContext.OrganizationId`。参见[组织范围的连接](self-service-sso#organisation-scoped-connections)。

## 将客户端限制到组织 {#restricting-a-client-to-an-organization}

```csharp
client.RestrictedToOrganizationIds = ["org_7f3a"];
```

每个条目都必须匹配组织 ID 的形式 `^[A-Za-z0-9._~-]{1,200}$`；对于空的或格式错误的条目，管理 API 会返回 `400 invalid_request`，因为一个列出了任何 `organization` 参数都无法发送的 ID 的限制什么也匹配不到，而什么也匹配不到的限制会拒绝每一个请求。`null` 列表会被规范化为空列表。

空列表（所有现有客户端都是如此）表示不受限制。组织不在列表中的请求会以 `access_denied` 拒绝。只含一个条目的列表同时也会进行选择，即上文的规则 4。含有多个条目的列表只进行限制而不进行选择：请求仍必须指定其中一个，否则会以 `account_selection_required` 拒绝。

## 声明 {#the-claims}

在 ID 令牌和访问令牌上都有：

| 声明 | 值 | 作用域 |
|---|---|---|
| `org_id` | `Organization.Id` | 无；只要主体有组织就始终存在 |
| `org_slug` | `Organization.Slug` | 无；只要组织是一条真实记录就始终存在 |
| `org_name` | `Organization.DisplayName` | `profile` |

**`org_id` 和 `org_slug` 有意不受作用域限制。**它们是授权上下文，而不是个人资料数据：它们说明令牌可以代表哪个客户行事，而这正是多客户资源服务器首先要检查的内容，在它决定是否关心名称之前就要检查，而且往往是在一个根本没有请求个人资料的令牌上检查。如果受 `profile` 限制，一个只请求 `openid` 的纯 API 客户端会收到一个不带任何组织的令牌，这会被理解为“不属于任何人”：资源服务器要么拒绝一个合法的调用方，要么把该令牌视为不受范围限制，并用它提供所有客户的数据。第二种失败是悄无声息的，而它才是真正要紧的那一种。

不加限制地发布它们，并不会泄露客户端尚未确定的任何信息：组织是它选择的，或者它本身就被限制到某个组织。`org_name` 保留 `profile` 限制，因为它属于展示信息，不应据此做任何授权。

没有组织的账户不会发出这三个声明中的任何一个，因此以前不携带组织声明的令牌，现在仍然不携带。

这三个声明都是保留的：任何作用域的 `UserClaims` 列表和任何自定义用户属性都无法生成或覆盖它们。这一点对 `org_slug` 尤为重要，它是依赖方与其所服务的客户实例进行比较的稳定键。如果它可以由用户自行断言，那它就成了这次比较的答案。

如果账户携带的组织 ID 无法解析到任何记录，则只发出 `org_id`。没有 `org_slug` 表示“不存在 slug”，而绝不是“被隐藏”。

**在你的应用中验证 `org_id`：**

```csharp
var orgId = User.FindFirst("org_id")?.Value;
if (!string.Equals(orgId, ThisInstanceOrganizationId, StringComparison.Ordinal))
    return Results.Forbid();
```

## Userinfo、内省与令牌交换 {#userinfo-introspection-and-token-exchange}

**`/connect/userinfo`** 根据**所出示的令牌**而不是用户记录来返回 `org_id`、`org_slug`、`org_name` 和 `roles`。只要令牌携带 `org_id` 和 `org_slug`，就会返回它们，不受作用域限制，理由与它们在令牌本身上不受限制相同；`org_name` 需要 `profile`。一旦用户可能属于多个组织，这就是唯一可能正确的来源：账户只携带一个默认值，而令牌指明的是该授权实际签发时所针对的组织。个人资料字段（`email`、`name`、`phone_number`）则保持实时：它们是主体当前的详细信息，而这正是 userinfo 的用途。

因此，重新标记账户不会改变 userinfo 对已签发令牌的描述，而登录到组织 B 的用户也绝不会从那个在其 ID 令牌中写入了 B 的服务器那里被告知 `org_id` 是 A。

**`/connect/introspect`** 在令牌携带 `org_id` 和 `org_slug` 时会包含它们。自行验证 JWT 的资源服务器从令牌上读取它们；改用内省的资源服务器现在也能得到相同的答案。

**RFC 8693 令牌交换**会把 `org_id`、`org_slug` 和 `org_name` 从主体令牌带到交换后的令牌上，并依据**发起交换的**客户端的 `RestrictedToOrganizationIds` 对其进行检查：注册为服务某个客户的客户端，不能交换另一个客户的令牌，也不能交换完全不携带组织的令牌。由于 `org_id` 不受作用域限制，这项检查对于未带 `profile` 作用域签发的资源服务器令牌同样有效：在旧的限制方式下，这样的令牌看起来没有归属，受限客户端连自己的流量都会被拒绝。拒绝时返回 `invalid_target`，与该路径上其他目标策略的拒绝保持一致。交换是对现有会话的一种投影，而一个丢弃了其所代表组织的投影并不是范围更窄，而是失去了归属。宿主的 `ITokenExchangeSubjectTransformer` 仍可以有意地将交换重新绑定到另一个组织（这正是上下文绑定交换的用途），但它必须明确这样做。

## 刷新 {#refresh}

授权签发时所针对的组织会在每次刷新轮换中延续下去，并在每次轮换时重新检查。因此，以下三件事会在下一次轮换时生效，而不必等到刷新生命周期结束：

- 撤销或暂停成员资格，
- 禁用组织（`Enabled = false`），
- 收窄客户端的 `RestrictedToOrganizationIds`。

**每一项都会拒绝刷新；但没有一项会撤销授权。**所出示的刷新令牌不会被消耗，令牌家族也保持完整，因此只要条件仍然成立，这条链就会一直被拒绝；条件一旦不再成立，就会立即恢复：恢复成员资格或重新启用组织，会让会话恢复，而无需重新登录。授权仍会按其自身的绝对生命周期过期。这与已停用用户的情形相同：当 `IsActive` 为 false 时，其刷新会被拒绝。

要真正结束会话，请撤销授权：使用刷新令牌调用 `POST /connect/revocation`，或在宿主端调用 `GrantRevocation`。禁用组织是一道关卡，而不是撤销。

而仅仅继承了账户组织的授权，会在每次轮换时重新推导，因此重新标记账户仍然会生效。

**切换组织是一个新的授权请求**，而不是刷新。请带上不同的 `organization` 再次发送 `/connect/authorize`；现有会话会被复用，因此无需再次登录，并且会开始一个新的授权。不要指望刷新端点能更换组织：它没有用户代理，也没有同意环节，而授权记录的是针对其签发时所属组织而批准的作用域。

## 关闭成员资格检查 {#turning-the-membership-gate-off}

```csharp
organization.RequireMembershipForTokens = false;
```

默认开启。对于将组织用于品牌和路由而不是访问控制的部署，可以将其关闭：此时任何能够指定该组织的人都会获得该组织的令牌。成员资格只是参考性的组织并不构成边界；请慎重做出这一选择。

## 通过宿主钩子拒绝签发 {#refusing-an-issuance-from-a-host-hook}

`IAuthHook.OnTokenIssuingAsync` 会在 `authorization_code`、`refresh_token` 和 `device_code` 授权签发任何内容之前立即触发，并带有已解析的主体：

```csharp
public Task OnTokenIssuingAsync(TokenIssuanceContext context, CancellationToken ct = default)
{
    if (IsOffboarded(context.SubjectId, context.ClientId))
        throw new InvalidOperationException("This account is being offboarded.");
    return Task.CompletedTask;
}
```

抛出异常会以 `access_denied` 拒绝签发，并以异常消息作为 `error_description`；改为抛出 `ProtocolTokenException` 则可以指定你自己的 OAuth 错误。在刷新路径上，该检查在轮换**之前**运行，因此拒绝时所出示的刷新令牌不会被消耗，令牌家族也保持完整：“现在不行”并不等于“结束这个会话”。

它是一个默认接口成员，因此未重写它的现有 `IAuthHook` 不受影响。两个智能体签发路径（`client_credentials` 和令牌交换，各自带有智能体配置文件）会像以前一样触发它。

## 拒绝情况 {#refusals}

| 条件 | 错误 |
|---|---|
| 两个选择参数指向不同的组织 | `invalid_request` |
| 任一选择参数重复出现 | `invalid_request`（直接返回，不回传到 `redirect_uri`） |
| 指定的组织不存在 | `access_denied` |
| 组织已被禁用 | `access_denied` |
| 客户端不允许用于该组织 | `access_denied` |
| 用户不是活跃成员（显式选择时） | `access_denied` |
| 客户端服务多个组织，而请求未指定任何组织 | `account_selection_required` |

## 设备流程 {#device-flow}

设备授权没有可以携带参数的授权请求，因此会依次退回到客户端限制和账户默认值。必须固定到某一个组织的设备客户端，应在注册时使用只含一个条目的 `RestrictedToOrganizationIds`。

## 升级现有部署 {#upgrading-an-existing-deployment}

在组织存在之前，什么都不会改变。没有任何记录时：

- 任何请求都无法选择组织，
- 不会启用任何成员资格检查，
- 携带旧版 `OrganizationId` 的账户会继续从用户记录中发出 `org_id`，与以前完全相同，
- 没有组织的用户的令牌不携带这三个声明中的任何一个。

一旦你创建了组织，请在将客户端或依赖方指向某个组织**之前**先授予成员资格：显式选择要求活跃的成员资格，而如果某个客户的用户有记录却没有成员资格，就会被拒绝。
