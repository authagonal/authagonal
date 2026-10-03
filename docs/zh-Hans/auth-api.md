---
layout: default
title: 身份验证 API
locale: zh-Hans
---

# 身份验证 API

这些端点为登录 SPA 提供支撑。它们使用 Cookie 身份验证（`SameSite=Lax`、`HttpOnly`）。

如果你要构建自定义登录界面，这些就是你需要对接的端点。

## 端点 {#endpoints}

### 登录 {#login}

```
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**成功（200）：**设置身份验证 Cookie 并返回：

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe",
  "mfaAvailable": false
}
```

当客户端的 `MfaPolicy` 为 `Enabled` 但用户尚未注册 MFA 时，`mfaAvailable` 为 `true`（界面可以提供设置入口）；这种情况下还会包含一个 `clientId` 字段。

**需要 MFA（200）：**如果用户已注册 MFA，则**始终**会被质询，与发起请求的客户端的 `MfaPolicy` 无关（MFA 是用户/会话的属性，而不是客户端的属性）：

```json
{
  "mfaRequired": true,
  "challengeId": "a1b2c3...",
  "methods": ["totp", "webauthn", "recoverycode"],
  "webAuthn": { /* PublicKeyCredentialRequestOptions */ }
}
```

客户端应重定向到 MFA 质询页面，并调用 `POST /api/auth/mfa/verify`。

**需要设置 MFA（200）：**如果 `MfaPolicy` 为 `Required` 且用户尚未注册任何 MFA：

```json
{
  "mfaSetupRequired": true,
  "setupToken": "abc123..."
}
```

客户端应重定向到 MFA 设置页面。设置令牌通过 `X-MFA-Setup-Token` 请求头，向 MFA 设置端点证明用户身份。

**错误响应：**

| `error` | 状态 | 说明 |
|---|---|---|
| `invalid_credentials` | 401 | 邮箱或密码错误。对于未知邮箱的响应有意保持完全相同（防止枚举）。 |
| `locked_out` | 423 | 失败尝试次数过多。会包含 `retryAfter`（秒）。 |
| `account_disabled` | 403 | 账户已停用（仅在密码正确后才会显示） |
| `email_not_confirmed` | 403 | 邮箱尚未验证（仅在密码正确后才会显示） |
| `sso_required` | 409 | 该域名要求使用 SSO。`redirectUrl` 指向 SSO 登录。 |
| `captcha_failed` | 400 | Turnstile 验证失败（仅在配置了 Turnstile 时出现；此时请求需要携带 `turnstileToken` 字段） |
| `email_required` | 400 | 邮箱字段为空 |
| `password_required` | 400 | 密码字段为空 |

### 注册账户 {#register}

```
POST /api/auth/register
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

创建新的用户账户并发送验证邮件。返回 `201 { "success": true, "userId": "..." }`。可选字段：`locale`（持久化保存在用户上的 BCP-47 标记）和 `customAttributes`（字符串映射）。

注册有意设计为**不暴露枚举信息**：如果该邮箱已注册，响应同样是中立的 `201`（附带一个一次性的 `userId`），而真正的所有者会收到一封登录/重置通知邮件。注册还按 IP 限流，超出时返回 `429 rate_limited`（时间窗口和上限可通过 `Auth:MaxRegistrationsPerIp` / `Auth:RegistrationWindowMinutes` 配置）。

### 确认邮箱 {#confirm-email}

```
GET  /api/auth/confirm-email?token={token}
POST /api/auth/confirm-email?token={token}
```

使用验证邮件中的令牌确认用户的邮箱地址。`GET` 是邮件中可点击的链接，它会重定向到 `/login?email_confirmed=1`（当注册源自 OAuth 流程时，还会附加 `continue_client` 参数）。`POST` 是程序化路径，返回 JSON（令牌也可以在 JSON 正文中以 `{ "token": "..." }` 的形式提供）；响应包含一个可选的 `appLink`（“继续前往应用”的目标）。

### 提供方 {#providers}

```
GET /api/auth/providers
```

返回已配置的外部身份提供方列表（用于渲染 SSO 按钮）：

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "type": "oidc", "iconUrl": null, "loginUrl": "/oidc/google/login" }
  ],
  "turnstileSiteKey": null
}
```

配置了 `AllowedDomains` 的连接会被**排除**：这些连接通过 `/api/auth/sso-check` 以先输入邮箱的方式进入，而不是通过按钮。配置了 Cloudflare Turnstile 时会设置 `turnstileSiteKey`（此时登录界面必须在登录/注册/密码请求中发送 `turnstileToken`）。

### 注销 {#logout}

```
POST /api/auth/logout
```

以与 `/connect/endsession` 相同的方式结束调用方的会话：向已注册 URI 的依赖方发送后端通道注销令牌，撤销为该会话签发的授权，并清除身份验证 Cookie。需要 Cookie 身份验证和同源请求。返回 `200`：

```json
{
  "success": true,
  "frontchannel_logout_uris": ["https://myapp.example.com/oidc/frontchannel"]
}
```

`frontchannel_logout_uris` 列出调用方应加载（通过隐藏的 iframe）的前端通道注销 URL，以便在浏览器中完成注销；如果没有任何客户端注册此类 URL，则为空。参见[前端通道注销](front-channel-logout)。

### 忘记密码 {#forgot-password}

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

始终返回 `200`（防止枚举）。如果用户存在，则发送重置邮件。

### 重置密码 {#reset-password}

```
POST /api/auth/reset-password
Content-Type: application/json

{
  "token": "base64-encoded-token",
  "newPassword": "NewSecurePass1!"
}
```

| `error` | 说明 |
|---|---|
| `weak_password` | 不满足强度要求 |
| `invalid_token` | 令牌格式错误 |
| `token_expired` | 令牌已过期（默认有效期 60 分钟，可通过 `Auth:PasswordResetExpiryMinutes` 配置） |

### 会话 {#session}

```
GET /api/auth/session
```

已通过身份验证时返回当前会话信息：

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

未通过身份验证时返回 `401`。

### 应用 {#apps}

```
GET /api/auth/apps
```

返回租户的应用链接，供账户页面的“返回应用”启动器使用：即已启用且拥有主页 URI 的客户端（优先使用 `initiateLoginUri`，其次是 `clientUri`）。每个条目为 `{ clientId, clientName, homeUri, logoUri, isDefault }`；恰好有一个应用被标记为默认（被标记的那个客户端，或唯一拥有主页 URI 的客户端）。需要 Cookie 身份验证。

### 个人资料（自助服务） {#profile-self-service}

```
GET   /api/auth/profile
PATCH /api/auth/profile
```

已通过身份验证的用户读取/更新自己的非敏感个人资料字段：`firstName`、`lastName`、`companyName`、`phone`、`locale`。值为 null 的字段保持不变；邮箱、密码、角色、活跃状态和组织在这里**不可**编辑。两者都返回个人资料 `{ email, emailConfirmed, firstName, lastName, companyName, phone, locale }`。

### 会话管理（自助服务） {#sessions-self-service}

```
GET    /api/auth/sessions
DELETE /api/auth/sessions/{sessionId}
POST   /api/auth/sessions/revoke-others
```

列出并结束已通过身份验证的用户自己的 SSO 会话。这些功能需要服务器端会话，而服务器端会话需要显式启用：在 `AddAuthagonal` 之后调用 `AddAuthagonalServerSideSessions(configuration)`（Azure Table Storage，读取 `Storage:ConnectionString` 或 `Storage:TableServiceUri`），或者注册你自己的 `ITicketStore` 和 `IUserSessionRegistry`。没有注册表时，`GET` 返回空列表，`revoke-others` 返回 `{ "revoked": 0 }`，`DELETE` 返回 `404 not_supported`。`DELETE` 和 `POST` 路由要求同源请求。

`GET` 返回会话，按最近活动时间倒序排列：

```json
{
  "sessions": [
    {
      "sessionId": "...",
      "current": true,
      "createdAt": "2026-10-01T02:11:40+00:00",
      "lastSeenAt": "2026-10-04T05:30:12+00:00",
      "expiresAt": "2026-10-08T02:11:40+00:00",
      "ip": "203.0.113.7",
      "userAgent": "Mozilla/5.0 ..."
    }
  ]
}
```

`DELETE` 结束一个会话并返回 `{ "revoked": 1 }`，或返回 `404 session_not_found`。`POST /revoke-others` 结束除调用方自己之外的所有会话，并返回 `{ "revoked": <count> }`。两者还会通知每个被结束会话的依赖方（后端通道和前端通道注销），并撤销绑定到该会话的授权，因此该设备上持有的刷新令牌将不再可用。注册了注册表时，登录界面中的账户页面会显示这个列表。

### SSO 检查 {#sso-check}

```
GET /api/auth/sso-check?email=user@acme.com
```

检查该邮箱域名是否要求 SSO：

```json
{
  "ssoRequired": true,
  "providerType": "saml",
  "connectionId": "acme-azure",
  "redirectUrl": "/saml/acme-azure/login"
}
```

如果不要求 SSO：

```json
{
  "ssoRequired": false
}
```

### 密码策略 {#password-policy}

```
GET /api/auth/password-policy
```

返回服务器的密码要求（通过设置中的 `PasswordPolicy` 配置）：

```json
{
  "rules": [
    { "rule": "minLength", "value": 8, "label": "At least 8 characters" },
    { "rule": "uppercase", "value": null, "label": "Uppercase letter" },
    { "rule": "lowercase", "value": null, "label": "Lowercase letter" },
    { "rule": "digit", "value": null, "label": "Number" },
    { "rule": "specialChar", "value": null, "label": "Special character" }
  ]
}
```

默认登录界面会在重置密码页面上请求此端点，以动态显示密码要求。

## 默认密码要求 {#default-password-requirements}

在默认配置下，密码必须满足以下全部要求：

- 至少 8 个字符
- 至少一个大写字母
- 至少一个小写字母
- 至少一个数字
- 至少一个非字母数字字符
- 至少 2 个不同的字符

这些要求可以通过 `PasswordPolicy` 配置节进行自定义，参见[配置](configuration)。

## MFA 端点 {#mfa-endpoints}

### MFA 验证 {#mfa-verify}

```
POST /api/auth/mfa/verify
Content-Type: application/json

{
  "challengeId": "a1b2c3...",
  "method": "totp",
  "code": "123456"
}
```

验证一个 MFA 质询。成功时设置身份验证 Cookie 并返回用户信息。

**方法：**

| `method` | 必填字段 | 说明 |
|---|---|---|
| `totp` | `code`（6 位数字） | 来自身份验证器应用的基于时间的一次性密码 |
| `webauthn` | `assertion`（JSON 字符串） | 来自 `navigator.credentials.get()` 的 WebAuthn 断言响应 |
| `recovery` | `code`（`XXXX-XXXX`） | 一次性恢复码（使用后即被消耗） |

**重试语义：**错误的验证码**不会**作废质询，系统会先校验验证码，只在成功时才消耗质询，因此用户输错数字后可以用同一个 `challengeId` 重试（`401 invalid_code` / `assertion_failed`）。每个质询最多容忍 **5 次失败尝试**；第 5 次失败会消耗该质询并返回 `401 too_many_attempts`，强制重新登录（这将 TOTP 暴力破解限制为每个质询 5 次猜测）。质询还会过期（默认 5 分钟，`Auth:MfaChallengeExpiryMinutes`）；已过期、未知或已被消耗的 `challengeId` 会返回 `invalid_challenge`。TOTP 验证码另外还有防重放保护，来自已使用时间步长的验证码会被拒绝。

### MFA 状态 {#mfa-status}

```
GET /api/auth/mfa/status
```

返回用户已注册的 MFA 方法。需要 Cookie 身份验证或 `X-MFA-Setup-Token` 请求头。

```json
{
  "enabled": true,
  "offered": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

当每个客户端的 `MfaPolicy` 都为 `Disabled` 时，即租户关闭了 MFA，`offered` 为 `false`，这样设置界面就可以隐藏自己。恢复码条目还额外携带 `isConsumed`。

### TOTP 设置 {#totp-setup}

```
POST /api/auth/mfa/totp/setup
→ { "setupToken": "...", "qrCodeDataUri": "data:image/png;base64,...", "manualKey": "BASE32..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
→ { "success": true }
```

### WebAuthn / 通行密钥设置 {#webauthn--passkey-setup}

```
POST /api/auth/mfa/webauthn/setup
→ { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
→ { "success": true, "credentialId": "..." }
```

注册通行密钥要求**先有一个已确认的 TOTP 凭据**（`400 totp_required_first`）：通行密钥是叠加在可移植的基础因素之上、按设备提供的便利手段，因此账户永远不会落到只有通行密钥、被锁定在某台设备上的境地。邮箱域名被路由到 SSO 的用户不能注册本地通行密钥（`400 sso_managed`），否则会绕过租户的 IdP。已注册到**任何**账户（包括正在注册的用户自己的账户）的凭据 ID 会被拒绝，返回 `409 credential_already_registered`，因为重复注册会重置该凭据的签名计数器，并让两行共享同一个查找条目。

### 恢复码 {#recovery-codes}

```
POST /api/auth/mfa/recovery/generate
→ { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

生成 10 个一次性恢复码。要求至少已注册一种主要方法（TOTP 或 WebAuthn）。重新生成会替换所有现有的恢复码。

### 移除 MFA 凭据 {#remove-mfa-credential}

```
DELETE /api/auth/mfa/credentials/{credentialId}
→ { "success": true }
```

移除一个指定的 MFA 凭据。如果移除的是最后一种主要方法，则该用户的 MFA 会被禁用。需要真实的 Cookie 会话，设置令牌会被拒绝并返回 `403 session_required`（设置令牌的存在只是为了添加第一个因素，绝不能用于降低 MFA）。

### 无密码通行密钥登录 {#passwordless-passkey-login}

```
POST /api/auth/mfa/passwordless/begin
→ { "challengeId": "...", "options": { /* PublicKeyCredentialRequestOptions */ } }

POST /api/auth/mfa/passwordless/complete
{ "challengeId": "...", "assertion": "..." }
→ { "userId": "...", "email": "...", "name": "..." }
```

无需任何预先用户上下文的可发现凭据（常驻通行密钥）登录：`begin` 签发一个 `allowCredentials` 列表为空的断言质询，`complete` 则**根据**所选的通行密钥解析出用户，验证断言并让其登录（会话带有 MFA 标记，因为通行密钥是抗网络钓鱼的强身份验证）。由于在该流程之前没有识别出任何用户，WebAuthn §7.2 第 6 步规定此处身份验证器的用户句柄是必需的：不带用户句柄的断言会被拒绝并返回 `401 user_handle_required`，而用户句柄指向凭据所有者以外账户的断言会被拒绝并返回 `401 credential_not_found`。如果解析出的用户的邮箱域名被路由到 SSO，登录会被拒绝并返回 `409 sso_required` + `redirectUrl`，使本地通行密钥无法绕开强制使用的 IdP。

## 设备授权（RFC 8628） {#device-authorization-rfc-8628}

### 请求设备代码 {#request-device-code}

```
POST /connect/deviceauthorization
Content-Type: application/x-www-form-urlencoded

client_id=my-cli&scope=openid+profile
```

返回设备代码、用户代码和验证 URI：

```json
{
  "device_code": "abc123...",
  "user_code": "ABCD-EFGH",
  "verification_uri": "https://auth.example.com/device",
  "verification_uri_complete": "https://auth.example.com/device?user_code=ABCD-EFGH",
  "expires_in": 300,
  "interval": 5
}
```

`expires_in` 来自客户端的 `DeviceCodeLifetimeSeconds`（默认 300）。设备向用户显示 `verification_uri` 和 `user_code`，然后使用 `device_code` 轮询令牌端点，间隔不得短于 `interval` 秒，否则令牌端点会返回 `slow_down`（RFC 8628 §3.5）。在用户尚未批准时，令牌端点返回 `authorization_pending`。用户访问验证 URI、登录，并输入用户代码以批准。

### 批准前显示请求内容 {#show-the-request-before-approving}

```
GET /api/auth/device/info?user_code=ABCD-EFGH
```

需要 Cookie 身份验证。描述该代码将授予的内容，以便批准界面在用户批准之前向其显示是哪个应用在请求（由攻击者发起、并在一个不透明的提示上被批准的设备流程，正是 RFC 8628 §5.4 所警告的非法同意模式）：

```json
{
  "clientId": "my-cli",
  "clientName": "My CLI",
  "clientUri": "https://example.com",
  "logoUri": null,
  "scopes": ["openid", "profile"]
}
```

`scopes` 是实际将被授予的内容，即在对受角色限制的作用域应用按用户的角色检查之后的结果，而不是原始请求。错误：`401 not_authenticated`、`400 user_code_required`、`400 invalid_user_code`（未知、已被消耗或已过期）、`400 expired`。它与批准操作共享同一个限流桶（见下文）。

### 批准设备 {#approve-device}

```
POST /api/auth/device/approve
Content-Type: application/x-www-form-urlencoded

user_code=ABCD-EFGH&scopes=openid+profile
```

需要 Cookie 身份验证和同源请求。`scopes` 是可选的（以空格分隔）：它只能收窄用户有权获得的范围，绝不能扩大；省略时授予全部有权获得的范围。为当前用户批准该设备代码，并返回 `200 { "approved": true }`。之后设备即可通过令牌端点，使用授权类型 `urn:ietf:params:oauth:grant-type:device_code` 将设备代码兑换为令牌。

提交的代码在查找之前会按 RFC 8628 §6.1 进行规范化：转换为大写，并丢弃 31 个字符的代码字母表之外的每一个字符。`ABCD-EFGH`、`abcd-efgh`、`ABCDEFGH`、`ABCD EFGH`，以及复制粘贴时把连字符变成了长破折号的写法，都是同一个代码。连字符的存在只是为了让代码更便于朗读。

| 状态 | `error` | 含义 |
|---|---|---|
| 400 | `user_code_required`, `invalid_user_code`, `expired` | 与 `info` 相同 |
| 400 | `invalid_scope` | 提供了 `scopes`，但其中没有任何一个是用户有权获得的作用域 |
| 403 | `access_denied` | 用户对所请求的作用域均无权获得（`Scope.AllowedRoles`） |
| 403 | `mfa_enrolment_required` | 客户端实际生效的 MFA 策略为 `Required`，而用户没有第二因素；请先注册，然后再次批准 |

输入操作按每个主体每分钟十次尝试进行限流（RFC 8628 §5.1），由 `info`、`approve` 和 `deny` 共享；第十一次返回 `429`。在默认的进程内限流器下，该计数器是按节点计算的，因此多副本部署还应在边缘层实施该限制。

### 拒绝设备 {#deny-device}

```
POST /api/auth/device/deny
Content-Type: application/x-www-form-urlencoded

user_code=ABCD-EFGH
```

需要 Cookie 身份验证和同源请求。记录用户的拒绝并返回 `200 { "success": true }`。在代码过期之前，设备对令牌端点的下一次轮询会得到 `access_denied`（RFC 8628 §3.5），而不是 `authorization_pending`。错误和限流桶与 `info` 相同。

## 令牌内省（RFC 7662） {#token-introspection-rfc-7662}

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

token=eyJhbGci...
```

或使用表单编码的凭据：

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded

token=eyJhbGci...&client_id=my-app&client_secret=secret
```

返回令牌元数据：

```json
{
  "active": true,
  "sub": "user-id",
  "client_id": "my-app",
  "scope": "openid profile",
  "iss": "https://auth.example.com",
  "exp": 1234567890,
  "iat": 1234567890,
  "token_type": "Bearer"
}
```

非活动或无效的令牌返回 `{ "active": false }`。同时支持 JWT 访问令牌和不透明的刷新令牌。

## 同意端点 {#consent-endpoints}

### 同意信息 {#consent-info}

```
GET /consent/info?client_id=my-app
```

需要 Cookie 身份验证。返回同意页面所需的客户端详情和所请求的作用域。这些作用域并不取自查询字符串：它们是授权端点为该用户和客户端记录下的提议（经过角色权限过滤之后），因此精心构造的链接无法把一个受信任客户端的名称放在由调用方任意选择的权限列表之上。

```json
{
  "clientId": "my-app",
  "clientName": "My Application",
  "description": null,
  "clientUri": null,
  "logoUri": null,
  "scopes": ["openid", "profile", "email"],
  "scopeDetails": [
    { "name": "openid", "displayName": null, "description": null, "emphasize": false, "required": false, "group": null },
    { "name": "profile", "displayName": null, "description": null, "emphasize": false, "required": false, "group": null },
    { "name": "email", "displayName": null, "description": null, "emphasize": false, "required": false, "group": null }
  ]
}
```

`scopeDetails` 与 `scopes` 平行（顺序相同，每个作用域一个条目），因此只读取 `scopes` 的登录应用仍可正常工作。每个条目携带为该作用域注册的展示信息：

| 字段 | 含义 |
|---|---|
| `name` | 作用域名称，与 `scopes` 中的相同。 |
| `displayName` | 已注册的显示名称；作用域未注册时为 `null`。 |
| `description` | 已注册的描述，或 `null`。 |
| `emphasize` | 当该作用域被注册为影响重大时为 `true`，界面可以突出显示它。默认为 `false`。 |
| `required` | 当该作用域被注册为不可拒绝时为 `true`：界面将其显示为已勾选并锁定。默认为 `false`。 |
| `group` | 该作用域归入的标题，或 `null` 表示单独显示。 |

未注册的作用域会在 `displayName`、`description` 和 `group` 上得到 `null`，在两个标志上得到 `false`，登录应用则会退回使用自己的措辞。注册措辞的方法参见[作用域](scopes)。

错误：

| 状态 | 正文 | 时机 |
|---|---|---|
| `401` | 无 | 没有已登录的用户。 |
| `404` | `{ "error": "client_not_found" }` | 未知的 `client_id`。 |
| `400` | `{ "error": "no_pending_consent_request" }` | 该用户和客户端没有有效的同意提议（从未记录，或已过期）。 |

### 提交同意 {#submit-consent}

```
POST /consent
Content-Type: application/json

{
  "clientId": "my-app",
  "decision": "allow",
  "scopes": ["openid", "profile", "email"],
  "returnUrl": "/connect/authorize?..."
}
```

记录用户的同意决定（需要 Cookie 身份验证），并返回 `{ "redirect": "..." }` 供 SPA 跳转。允许时，被授予的作用域会被持久化（会按客户端的 `AllowedScopes` 过滤，因此被篡改的正文无法记录该客户端本无法请求的作用域），重定向会指回授权流程。在 `"decision": "deny"` 时，重定向指向客户端的 `redirect_uri`，并带有 `access_denied` 错误。

### 列出授权 {#list-grants}

```
GET /consent/grants
```

返回用户已授权的所有应用：

```json
[
  {
    "clientId": "my-app",
    "clientName": "My Application",
    "scopes": ["openid", "profile", "email"],
    "consentedAt": "2026-04-09T12:00:00Z"
  }
]
```

### 撤销授权 {#revoke-grant}

```
DELETE /consent/grants/{clientId}
```

撤销对某个特定应用的同意。用户在下次登录时会被提示重新同意。

## 发现文档与签名密钥（JWKS） {#discovery-and-signing-keys-jwks}

两者都是公开且匿名的。资源服务器正是使用它们来验证本服务器签发的令牌。

```
GET /.well-known/openid-configuration
GET /.well-known/oauth-authorization-server
GET /.well-known/openid-configuration/jwks
```

- 两个元数据路径返回相同的发现文档；其 `jwks_uri` 为 `{issuer}/.well-known/openid-configuration/jwks`。
- JWKS 列出每一个未过期的签名密钥（`kty`、`use`、`kid`、`alg`，以及 EC 密钥的 `crv`/`x`/`y`）。轮换会提前数天发布下一个密钥，因此缓存的副本永远不会缺少签名某个令牌所用的密钥。
- 响应携带 `Cache-Control: public, max-age=3600`。
- 签名仅使用 ES256；发现文档声明 `id_token_signing_alg_values_supported: ["ES256"]`。
- 签发者来自 `ITenantContext`，密钥来自 `IKeyManager`，因此使用按租户密钥管理器的多租户宿主会提供按租户的密钥。

## 授权端点行为 {#authorization-endpoint-behaviour}

`GET /connect/authorize` 是授权码流程的入口。对于任何基于它构建客户端或登录界面的人来说，有两项行为很重要。

### 响应中的签发者（RFC 9207） {#issuer-in-the-response-rfc-9207}

每一次重定向回客户端 `redirect_uri` 的响应都携带一个保存签发者的 `iss` 查询参数，成功时（与 `code` 和 `state` 一起）和出错时（与 `error`、`error_description` 和 `state` 一起）都是如此。用户在 `/consent` 拒绝同意时的错误重定向也同样适用。发现文档通过 `authorization_response_iss_parameter_supported: true` 声明这一点。与多个授权服务器通信的客户端应将 `iss` 与其发起流程时所针对的签发者进行比较，这正是抵御混淆攻击的方法；忽略该参数的客户端不受影响。在得知可信的 `redirect_uri` 之前产生的错误（未知的 `client_id`、未注册的重定向 URI）会以 JSON 错误正文而不是重定向的形式返回，因此这些错误上没有 `iss`。

### `prompt` 与 `max_age` {#prompt-and-max_age}

| 请求 | 行为 |
|---|---|
| `prompt=login` | 现有会话被注销，用户被引导到 `/login` 重新进行身份验证。`prompt` 会从 `returnUrl` 中移除，使新的登录不会陷入被反复强制重新验证的循环。对于[推送的请求](par)，prompt 随存储的载荷一起传递，而循环的打破方式是要求会话的 `auth_time` 不早于该请求被推送的时刻 |
| `prompt=select_account` | 按 `prompt=login` 处理：服务器每个浏览器只保留一个会话，因此选择账户就是登录界面 |
| `prompt=create` | 未通过身份验证的用户被引导到 `/login/register`，而不是登录表单。已有会话则直接继续 |
| `prompt=consent` | 即使已存储的授权足以满足请求，也会显示同意界面，每个请求一次（已满足标记只能使用一次） |
| `prompt=none` | 永远不显示任何界面。服务器以重定向作答，携带 `login_required`（没有会话）、`interaction_required`（需要 MFA 升级验证或注册）或 `consent_required`（需要同意） |
| `max_age=N` | 如果会话的 `auth_time` 早于 `N` 秒之前，或不存在，则用户会像 `prompt=login` 一样被重新验证身份。`max_age=0` 始终重新验证 |

`prompt=none` 与任何其他值组合时会以 `invalid_request` 拒绝，`none`、`login`、`consent`、`select_account` 和 `create` 之外的任何值也同样如此。可嵌入的 `Authagonal.Protocol` 宿主以相同方式处理 `prompt=login`、`select_account`、`none` 和 `max_age`，但它没有同意界面，因此对 `prompt=consent` 回应 `consent_required`。

## 构建自定义登录界面 {#building-a-custom-login-ui}

默认 SPA（`login-app/`）是此 API 的一种实现。要构建你自己的界面：

1. 在路径 `/login`、`/forgot-password`、`/reset-password`、`/consent`、`/device` 上提供你的界面
2. 授权端点会将未通过身份验证的用户重定向到 `/login?returnUrl={encoded-authorize-url}`
3. 登录成功（Cookie 已设置）后，将用户重定向到 `returnUrl`
4. 密码重置链接使用 `{Issuer}/login/reset-password?p={token}`（登录 SPA 挂载在 `/login` 之下）

你的界面必须与 API 位于**同一来源**，因为：
- Cookie 身份验证使用 `SameSite=Lax` + `HttpOnly`
- 授权端点重定向到 `/login`（相对路径）
- 重置链接使用 `{Issuer}/login/reset-password`
