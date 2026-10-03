---
layout: default
title: OAuth 作用域
locale: zh-Hans
---

# OAuth 作用域

Authagonal 同时支持**内置**的 OAuth/OIDC 作用域和在运行时管理的**自定义**作用域。自定义作用域会被持久化，通过发现文档公布，并与内置作用域一起显示在同意屏幕上。

## 内置作用域 {#built-in-scopes}

这些作用域始终可用，无需注册：

| 作用域 | 用途 |
|---|---|
| `openid` | 发起 OIDC 流程所必需。会签发 ID 令牌。 |
| `profile` | 标准个人资料声明（name、family_name、given_name 等） |
| `email` | 电子邮件地址和 `email_verified` 声明 |
| `phone` | `phone_number` 和 `phone_number_verified` 声明（OIDC Core 5.4） |
| `roles` | `roles` 声明。它不是 OIDC 标准作用域：角色成员资格是需要最终用户同意披露的声明 |
| `groups` | `groups` 声明（SCIM 组成员资格）。它不是 OIDC 标准作用域，管控方式与 `roles` 相同 |
| `offline_access` | 在访问令牌之外签发刷新令牌 |

客户端只能请求其自身 `AllowedScopes` 中列出的作用域。对于不在该列表中的作用域，`/connect/authorize` 会以 `invalid_scope` 拒绝，而不是将其过滤掉，因此如果在应用的请求中加入 `roles` 却没有将其加到客户端上，所有登录都会失败。

## 自定义作用域 {#custom-scopes}

自定义作用域通过位于 `/api/v1/scopes` 的管理 API 进行管理。调用这些接口需要带有 `authagonal-admin` 作用域的 JWT 访问令牌（可通过 `AdminApi:Scope` 配置）。

### 作用域模型 {#scope-model}

```csharp
public sealed class Scope
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }
    public string? Group { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> AllowedRoles { get; set; } = [];
    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

| 字段 | 说明 |
|---|---|
| `Name` | 在令牌请求中发送的作用域标识符（例如 `billing.read`） |
| `DisplayName` | 显示在同意屏幕上的易读名称 |
| `Description` | 显示在同意屏幕上的较长说明 |
| `Emphasize` | 为 `true` 时，同意屏幕会将此作用域作为敏感项突出显示 |
| `Group` | 此作用域在同意屏幕上归入的标题。仅用于展示：它从不影响授予的内容 |
| `Required` | 为 `true` 时，用户在同意时无法取消选择此作用域 |
| `ShowInDiscoveryDocument` | 为 `true` 时，此作用域会出现在 `/.well-known/openid-configuration` 的 `scopes_supported` 下 |
| `AllowedRoles` | 用户必须持有其中之一才能被授予此作用域的角色。为空（默认）时不加管控，参见[按角色管控的作用域](#role-gated-scopes) |
| `UserClaims` | 授予此作用域时会写入令牌的自定义属性声明名称白名单。保留的协议声明（例如 `org_id`）永远不会通过这种方式释放，因此存储的属性无法伪造它们 |

### 按角色管控的作用域 {#role-gated-scopes}

客户端的 `AllowedScopes` 回答的是*这个应用可以请求这个作用域吗*，这个问题在任何人登录之前就已确定。`AllowedRoles` 回答的是另一半：*这个人可以拥有它吗*。两道关卡都会生效，任何一道都不能替代另一道。

```json
{
  "name": "staff-admin",
  "displayName": "Staff administration",
  "allowedRoles": ["staff", "super-admin"]
}
```

对于不持有任何所列角色的用户，该作用域会**从授权中移除**，而不是被拒绝：客户端请求了完整的作用域集合，并通过令牌响应中回显的 `scope`（RFC 6749 §3.3）得知自己得到的少于所请求的。这正是同一个应用能够同时服务员工和其他所有人的原因：员工功能只是若干作用域中的一个，只有有权获得它的人才会收到它。

如果请求的*每一个*作用域都被移除，请求会以 `access_denied` 失败，因为已经没有任何可以为之签发令牌的内容了。

只要是为人类用户签发令牌，这道关卡就会生效：

| 流程 | 执行位置 |
|---|---|
| 授权码 | 在 `/connect/authorize`，一旦确定了用户，且在同意**之前**执行，因此同意屏幕永远不会提供无法授予的权限 |
| 设备码 | 在 `/api/auth/device/approve`，这是该流程中第一个能确定主体的位置 |
| 刷新 | 每次轮换时都会根据重新解析的角色执行。撤销角色真正生效的地方就在这里，因为授权记录中保存的仍然是登录时批准的内容 |
| 令牌交换 | 不单独管控：交换只能在主体令牌自身的作用域范围内缩小权限，因此永远无法获得主体未被授予的作用域 |

客户端凭据授权没有主体，因此有意不受影响：机器客户端的权限就是它的注册信息。

从配置预置作用域时可以添加或更改 `AllowedRoles`，但不能将其清空（与 `UserClaims` 一样，省略的字段会保留已存储的值）。要移除管控，请使用显式的空数组对该作用域执行 `PUT`。

## 从配置预置 {#seeding-from-configuration}

可以在 `Scopes` 配置节中声明作用域。它们会在启动时写入作用域存储，与[客户端预置](configuration#clients)一同进行。

```json
{
  "Scopes": [
    {
      "Name": "billing.read",
      "DisplayName": "Billing (read-only)",
      "Description": "View invoices and payment history",
      "UserClaims": ["billing_plan"],
      "ShowInDiscoveryDocument": true,
      "Emphasize": false,
      "Group": "Billing",
      "Required": false,
      "AllowedRoles": ["finance"]
    }
  ]
}
```

| 字段 | 说明 |
|---|---|
| `Name` | 必填。没有名称的条目会被跳过并记录警告 |
| `DisplayName`, `Description`, `UserClaims`, `ShowInDiscoveryDocument`, `Emphasize`, `Group`, `Required`, `AllowedRoles` | 与 [作用域模型](#scope-model)中相同 |

预置是按 `Name` 进行的 upsert。你设置的字段在每次启动时都会覆盖已存储的值，因此通过管理 API 对同样被预置的字段所做的修改会在下次启动时被覆盖。你省略的字段会保留已存储的值（对于新作用域则使用模型默认值）。由于省略意味着“保留”，配置可以添加或更改 `UserClaims` 和 `AllowedRoles`，但不能将它们清空：请使用 `PUT /api/v1/scopes/{name}` 并传入显式的空数组来完成。

## 管理端点 {#admin-endpoints}

### 列出作用域 {#list-scopes}

```
GET /api/v1/scopes
```

返回 `{ "scopes": [ ... ] }`。

### 获取作用域 {#get-scope}

```
GET /api/v1/scopes/{name}
```

返回该作用域，如果不存在则返回 `404`。

### 创建作用域 {#create-scope}

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing (read-only)",
  "description": "View invoices and payment history",
  "emphasize": false,
  "required": false,
  "showInDiscoveryDocument": true,
  "userClaims": ["billing_plan"]
}
```

返回 `201 Created` 及该作用域。如果缺少 `name` 或其中包含空白字符，返回 `400`（`invalid_request`）；如果已存在同名作用域，返回 `409`（`scope_exists`）。

### 更新作用域 {#update-scope}

```
PUT /api/v1/scopes/{name}
Content-Type: application/json

{
  "displayName": "Billing (read)",
  "description": "View invoices",
  "emphasize": true
}
```

只有提供的字段会被更新；省略的字段保留其当前值。

### 删除作用域 {#delete-scope}

```
DELETE /api/v1/scopes/{name}
```

返回 `204 No Content`（如果该作用域不存在则返回 `404`）。已经签发且包含此作用域的令牌在过期之前仍然有效，如有需要，请通过 `/connect/revocation` 显式撤销它们。

## 发现文档 {#discovery-document}

`ShowInDiscoveryDocument = true` 的作用域会出现在 `/.well-known/openid-configuration` 的 `scopes_supported` 下。七个内置作用域始终会被公布。

```json
{
  "scopes_supported": ["openid", "profile", "email", "phone", "roles", "groups", "offline_access", "billing.read"]
}
```

## 同意屏幕 {#consent-screen}

当客户端请求的作用域不在其免同意列表中时，同意页面会按 `DisplayName`（缺失时回退到 `Name`）列出每个请求的作用域，并在其下方显示 `Description`。`Emphasize = true` 的作用域会以醒目的样式显示。`Required` 作用域无法取消选择。

面向用户的流程请参见 [OAuth 同意屏幕](index#key-features)。

## 动态客户端注册 {#dynamic-client-registration}

通过[动态客户端注册](client-registration)注册的客户端只能声明 OIDC 内置作用域（`openid`、`profile`、`email`、`phone`、`offline_access`），以及 `Auth:DynamicClientRegistrationScopes` 中列出的任何作用域。某个作用域仅仅存在于存储中，并不意味着自行注册的客户端可以声明它，而按角色管控的作用域（带有 `AllowedRoles` 的作用域）永远不可注册。其他任何作用域都会以 `invalid_scope` 被拒绝。
