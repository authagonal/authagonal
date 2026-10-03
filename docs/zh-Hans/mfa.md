---
layout: default
title: 多因素认证
locale: zh-Hans
---

# 多因素认证（MFA）

Authagonal 支持多因素认证。共有三种方式：TOTP（身份验证器应用）、WebAuthn/通行密钥（硬件密钥和生物识别）以及一次性恢复码。通行密钥还可以用于[无密码登录](#passwordless-passkey-login)。

联合登录（SAML/OIDC）同样适用：SAML 或 OIDC 断言证明的是第一因素，而不是第二因素。已注册 MFA 的联合用户会经过与密码登录相同的本地 MFA 质询，而 `Required` 策略会在签发任何会话之前强制要求注册。只有在 MFA 既未注册也不是必需时，联合登录才会单独生效。连接可以通过 `ChallengeMfaAfterLogin: false` 选择不使用本地质询（见下文）。

## 支持的方式 {#supported-methods}

| 方式 | 说明 |
|---|---|
| **TOTP** | 基于时间的一次性密码（RFC 6238）：6 位数字，30 秒步长，SHA-1，验证时允许一个步长的时钟偏差。适用于任何身份验证器应用（Google Authenticator、Authy、1Password 等）。已被接受的验证码在其有效期内不能重放。 |
| **WebAuthn / 通行密钥** | FIDO2 硬件安全密钥、平台生物识别（Touch ID、Windows Hello）以及同步的通行密钥。用户可以注册多个通行密钥，并且可以使用通行密钥进行无密码登录。 |
| **恢复码** | 10 个一次性备用码（由 32 个字符的字母表中的 10 个字符组成，显示为 `XXXXX-XXXXX`），用于在其他方式不可用时恢复账户。以哈希形式存储，并在静态存储时加密。 |

## MFA 策略 {#mfa-policy}

MFA 的强制执行通过 `appsettings.json` 中的 `MfaPolicy` 属性**按客户端**配置：

| 值 | 行为 |
|---|---|
| `Disabled`（默认） | 不强制注册；当每个客户端都是 `Disabled` 时，自助设置界面会隐藏 MFA |
| `Enabled` | 提供 MFA 注册；但不强制 |
| `Required` | 强制尚未设置 MFA 的用户进行注册 |

已注册 MFA 的用户**在登录时始终会受到质询，与客户端策略无关**。MFA 是用户及其会话的属性，而不是发起请求的客户端的属性，因此无法通过经由 `Disabled` 客户端路由请求来跳过已注册用户的第二因素。

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "MfaPolicy": "Enabled"
    },
    {
      "ClientId": "admin-portal",
      "MfaPolicy": "Required"
    }
  ]
}
```

默认值为 `Disabled`，因此在你主动启用之前，现有客户端不受影响。

### 按用户覆盖 {#per-user-override}

实现 `IAuthHook.ResolveMfaPolicyAsync`，即可针对特定用户覆盖客户端策略：

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    // Exempt service accounts
    if (email.EndsWith("@service.internal"))
        return Task.FromResult(MfaPolicy.Disabled);

    return Task.FromResult(clientPolicy);
}
```

解析出的策略决定的是注册（是提供还是强制）。它不会让已注册的用户免于质询；已注册的用户始终会受到质询。

完整的钩子文档请参见[可扩展性](extensibility)。

## 登录流程 {#login-flow}

带 MFA 的登录流程如下：

1. 用户向 `POST /api/auth/login` 提交邮箱和密码
2. 服务器验证密码，然后解析实效 MFA 策略
3. 根据策略和用户的注册状态：

| 策略 | 用户是否已设置 MFA？ | 结果 |
|---|---|---|
| 任意 | 是 | 返回 `mfaRequired`：用户必须完成验证 |
| `Disabled` / `Enabled` | 否 | 设置 Cookie，登录完成 |
| `Required` | 否 | 返回 `mfaSetupRequired`：用户必须注册 |

### MFA 质询 {#mfa-challenge}

返回 `mfaRequired` 时，登录响应中包含 `challengeId`、用户可用的 `methods`，以及（用户拥有通行密钥时）`webAuthn` 断言选项。客户端重定向到 MFA 质询页面，用户在该页面通过 `POST /api/auth/mfa/verify` 使用已注册的某种方式完成验证：

```json
{
  "challengeId": "...",
  "method": "totp",
  "code": "123456"
}
```

`method` 为 `totp`、`recovery` 或 `webauthn`（WebAuthn 发送的是 `assertion`，而不是 `code`）。

质询在 5 分钟后过期（可通过 `Auth:MfaChallengeExpiryMinutes` 配置），并在验证成功后被消耗。

#### 重试额度 {#retry-budget}

输错验证码不会让质询作废。验证端点会先校验验证码，只有成功时才消耗质询，因此 TOTP 输错一位时，只需针对同一个 `challengeId` 重试即可。失败的尝试会返回 401 和 `invalid_code`（WebAuthn 为 `assertion_failed`），并使质询上一个有上限的计数器加一；第五次错误尝试会消耗该质询并返回 `too_many_attempts`，迫使用户重新登录。这适用于全部三种方式。

每个质询的尝试额度只是一道便捷的第一层限制，而不是安全边界，因此 `POST /api/auth/mfa/verify` 还受到另外两道关卡的约束：

- **按用户限流。** 同一用户每分钟超过 10 次验证尝试会返回 429 和 `too_many_attempts`，无论使用的是哪个 `challengeId`。
- **共享的账户锁定。** 每个失败的验证码也会计入与密码步骤相同的失败尝试计数器（`Auth:MaxFailedAttempts`、`Auth:LockoutDurationMinutes`）。一旦触发，质询会被消耗，响应为 `locked_out`（423）。账户处于锁定状态期间，验证请求会在检查验证码之前就以 `locked_out` 被拒绝。

只有已确认的凭据才能满足验证；已开始但从未完成的注册不算作一个因素。

缺失、已过期或已被消耗的质询会返回 `invalid_challenge`。

### 联合登录 {#federated-logins}

SAML 或 OIDC 断言成功后，服务器会解析同样的实效 MFA 策略。已注册 MFA 的用户会被重定向到托管的 MFA 质询页面（带 `challengeId`），而不是直接获得会话；在 `Required` 策略下尚未设置 MFA 的用户会被重定向到 MFA 设置页面（带 `setupToken`）。只有在验证完成后，会话才会被标记为已通过 MFA 认证。

该质询按连接生效：`ChallengeMfaAfterLogin` 设为 `false` 的 SAML 或 OIDC 连接，会对经由它登录的用户跳过本地质询。默认值为 `true`。

### 强制注册 {#forced-enrollment}

返回 `mfaSetupRequired` 时，响应中包含一个 `setupToken`。该令牌（通过 `X-MFA-Setup-Token` 请求头）向 MFA 设置端点认证用户身份，使用户能够在获得 Cookie 会话之前注册一种方式。设置令牌在 15 分钟后过期（可通过 `Auth:MfaSetupTokenExpiryMinutes` 配置）。

## 注册 MFA {#enrolling-mfa}

用户通过自助设置端点注册 MFA。这些端点要求已认证的 Cookie 会话或设置令牌二者之一。

### TOTP 设置 {#totp-setup}

1. 调用 `POST /api/auth/mfa/totp/setup`，它会返回一个二维码（`data:image/png;base64,...`）、一个 `manualKey`（用于手动输入的 Base32 密钥）和设置令牌
2. 用户使用身份验证器应用扫描二维码
3. 用户输入 6 位验证码进行确认：`POST /api/auth/mfa/totp/confirm`

确认步骤与验证一样受到限流：同一用户每分钟超过 10 次尝试会返回 `too_many_attempts`（429），并且在使用设置令牌时，第五次错误的验证码会消耗该设置质询。未确认的注册会在 30 分钟后过期（`setup_expired`）。

### WebAuthn / 通行密钥设置 {#webauthn--passkey-setup}

1. 调用 `POST /api/auth/mfa/webauthn/setup`，它会返回一个 `setupToken` 和 `PublicKeyCredentialCreationOptions`
2. 客户端使用这些选项调用 `navigator.credentials.create()`
3. 将证明（attestation）响应发送到 `POST /api/auth/mfa/webauthn/confirm`

注册通行密钥之前，必须先有一个已确认的 TOTP 凭据（`totp_required_first`）。通行密钥是叠加在可移植的基础因素之上、按设备提供的便利手段，因此每个账户都保留一个不依赖设备的因素，并且 `Required` 策略不能仅靠通行密钥来满足。

用户可以注册多个通行密钥（每台设备一个）。已经注册过的凭据 ID（无论注册到哪个账户，包括正在注册的用户自己的账户）会以 `credential_already_registered`（409）被拒绝。重新注册一个已注册的身份验证器，会产生第二条共享同一凭据 ID 的凭据记录：其签名计数器会重新开始，削弱克隆检测，而且删除其中任何一条记录，都会移除两者共同依赖的查找条目。查找条目通过“不存在才插入”的写入方式来占用，因此同一凭据 ID 的两次注册不可能都成功。邮箱域名通过强制 SSO 路由到外部 IdP 的用户不能注册本地通行密钥（`sso_managed`），因为那样会绕过 IdP 及其账户撤销流程。

### 依赖方主机 {#relying-party-host}

FIDO2 依赖方 ID 和源会根据每个请求的主机名解析，因此每个租户主机名都是各自独立的依赖方。请将 `Auth:WebAuthnAllowedHosts` 设置为你所服务的主机名，这样不在该列表中的主机就无法充当依赖方。空列表（默认值）会保持以前的行为，以免升级时把现有的通行密钥用户锁在门外，并会在首次使用时作为一个缺口记录到日志中。它不是可以长期停留的安全状态。同时在 `appsettings.json` 中设置 `AllowedHosts`，让 ASP.NET Core 的主机过滤在任何处理程序运行之前就拒绝无法识别的 `Host` 请求头，是成本更低的外层防护。

与该列表无关，每个凭据都会记录它注册时所属的依赖方，在其他任何地方都会被拒绝。这是请求无法影响的部分：否则两个流程都会根据它们正在验证的同一个 `Host` 请求头来构建预期值，于是源和 `rpIdHash` 实际上是在与调用方提供的值进行比较，而一个转发自身 `Host` 的中间人主机，就会让源绑定（正是这一属性使通行密钥能够抵御钓鱼）为它背书，而不是阻止它。在记录依赖方 ID 之前注册的凭据没有该记录，仍可继续使用；它们会在重新注册时获得该绑定。

### 恢复码 {#recovery-codes}

调用 `POST /api/auth/mfa/recovery/generate` 生成 10 个一次性恢复码。必须先注册至少一种已确认的主要方式（TOTP 或 WebAuthn）（`primary_method_required`），并且该调用需要真正的已认证会话：使用设置令牌会得到 `session_required`（403）。

每个恢复码由 32 个字符的字母表中的 10 个字符组成，显示为两组各五个字符（`XXXXX-XXXXX`）。

重新生成恢复码会替换所有现有的恢复码。每个恢复码只能使用一次；已兑换的恢复码会被标记为已消耗，不再被接受。

恢复码永远不会以明文存储：每个恢复码都会被哈希，哈希值还会使用租户的密钥提供程序在静态存储时进一步加密，因此存储转储得到的是密文，而不是可以离线暴力破解的哈希值。

## 无密码通行密钥登录 {#passwordless-passkey-login}

通行密钥不只是第二因素：已注册通行密钥的用户可以不使用密码登录。

1. `POST /api/auth/mfa/passwordless/begin` 返回一个 `challengeId` 以及面向可发现凭据的断言 `options`，因此身份验证器会提供该站点的任何驻留通行密钥
2. 客户端使用这些选项调用 `navigator.credentials.get()`
3. 以 `{ challengeId, assertion }` 调用 `POST /api/auth/mfa/passwordless/complete`：服务器根据通行密钥本身解析出用户，并让其登录

托管登录页面通过条件式中介（通行密钥自动填充）将这一流程接入邮箱输入框：浏览器支持时，可用的通行密钥会作为自动填充建议出现，无需任何额外界面。

通行密钥是抗钓鱼的强认证，因此由此产生的会话带有 MFA 标记，不会再次受到质询。如果用户的邮箱域名通过强制 SSO 路由到外部 IdP，无密码登录会被拒绝，返回 409 `sso_required` 响应，其中包含 SSO 重定向 URL，因此本地通行密钥无法绕过 IdP。

## 管理 MFA {#managing-mfa}

### 用户自助 {#user-self-service}

- `GET /api/auth/mfa/status`，查看已注册的方式（还会报告是否有任何客户端提供 MFA）
- `DELETE /api/auth/mfa/credentials/{id}`，删除指定的凭据

删除凭据需要真正的已认证会话；设置令牌只授权添加第一个因素，在这里会得到 `session_required`，因此泄露的设置令牌无法降低用户的 MFA 安全级别。

如果删除了最后一种主要方式，该用户的 MFA 就会被禁用。

### 管理 API {#admin-api}

管理员可以通过[管理 API](admin-api) 管理任何用户的 MFA：

- `GET /api/v1/profile/{userId}/mfa`，查看用户的 MFA 状态
- `DELETE /api/v1/profile/{userId}/mfa`，重置全部 MFA（用于被锁定的用户）
- `DELETE /api/v1/profile/{userId}/mfa/{id}`，删除指定的凭据

### 审计钩子 {#audit-hooks}

实现 `IAuthHook.OnMfaVerifiedAsync` 以记录 MFA 事件：

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

整个 MFA 生命周期都可以挂接钩子：`OnMfaVerifyFailedAsync`（一次验证尝试失败）、`OnMfaEnrolledAsync`（一种方式已确认）、`OnMfaCredentialRemovedAsync`（一个凭据被删除，并带有一个标志表示此举是否禁用了 MFA），以及 `OnRecoveryCodesRegeneratedAsync`。

## 自定义登录界面 {#custom-login-ui}

如果你在构建自定义登录界面，请处理 `POST /api/auth/login` 的以下响应：

1. **正常登录**：`{ userId, email, name }`，并设置 Cookie。重定向到 `returnUrl`。
2. **需要 MFA**：`{ mfaRequired: true, challengeId, methods, webAuthn? }`。显示 MFA 质询表单。
3. **需要设置 MFA**：`{ mfaSetupRequired: true, setupToken }`。显示 MFA 注册流程。

处理 `POST /api/auth/mfa/verify` 的错误时：`invalid_code` 和 `assertion_failed` 可以针对同一个 `challengeId` 重试（在尝试额度之内）；`too_many_attempts` 和 `invalid_challenge` 是终止性的，请将用户送回登录表单。

完整的端点参考请参见[认证 API](auth-api)。
