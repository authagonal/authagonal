---
layout: default
title: 首页
locale: zh-Hans
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

面向 .NET 的 OAuth 2.0 / OpenID Connect / SAML 2.0 身份验证服务器，存储层可插拔：可使用你自己的 PostgreSQL 或 SQLite、Azure Table Storage，或 AWS（DynamoDB / S3 / Secrets Manager）。

单一、自包含的部署。服务器和登录界面打包在同一个 Docker 镜像中，SPA 与 API 由同一源提供，因此 Cookie 身份验证、重定向和 CSP 都无需处理跨源的复杂问题。

> **更想要托管服务？**[Authagonal Cloud](https://authagonal.io) 为你运行这一切：多租户，每个套餐都包含全部功能，SSO 不按连接收费。→ [authagonal.io](https://authagonal.io)

## 主要功能 {#key-features}

- **OIDC 提供方**：authorization_code + PKCE、client_credentials、refresh_token、device_code 授权类型，并支持一次性轮换
- **SAML 2.0 SP**：自研实现，完整支持 Azure AD（签名响应、签名断言或两者皆签），每个连接拥有独立的 SP 密钥对，用于签名 AuthnRequest 和解密 `EncryptedAssertion`，并支持单点注销（SP 发起和 IdP 发起）
- **动态 OIDC 联合**：连接 Google、Apple、Azure AD 或任何兼容 OIDC 的 IdP
- **多因素身份验证**：TOTP、WebAuthn/通行密钥、恢复码；按客户端设置策略（`Disabled` / `Enabled` / `Required`），可通过 `IAuthHook` 按用户覆盖，并且对联合登录同样强制执行
- **SCIM 2.0 预配**：接收来自 Entra ID、Okta、OneLogin 的用户/组预配；基于游标的分页列表，以及由盲索引支撑的 `eq` 过滤器
- **OAuth 同意屏幕**：按客户端征求同意，作用域变化时重新提示，并提供授权管理
- **设备授权许可**：RFC 8628 流程，适用于输入受限的设备（智能电视、命令行工具、物联网设备）
- **令牌内省**：RFC 7662，供资源服务器验证令牌是否有效
- **令牌签名**：仅支持 ES256。访问令牌携带 RFC 9068 的 `typ: at+jwt`，使资源服务器能够将其与 id_token 和注销令牌区分开来，但**并不声称符合 RFC 9068**：§2.1
  要求在支持的算法中包含 RS256，而本服务器既不签发也不接受 RS256。只支持
  单一算法是有意为之的安全姿态：每多接受一种算法，就多一条让验证方
  被诱导使用错误算法的途径。
- **后端通道注销**：向依赖方发送 OIDC Back-Channel Logout 1.0 通知
- **服务器端会话**（*可选启用*）：`AddAuthagonalServerSideSessions` 将 SSO 票据保存在存储中，使身份验证 Cookie 只携带一个不透明的 ID，并启用自助式 `GET /api/auth/sessions` 列表和按设备撤销（[Auth API](auth-api#sessions-self-service)）
- **Backend-for-Frontend**：`Authagonal.Bff`（.NET）和 `@authagonal/bff`（Node），一种机密客户端 BFF，使 SPA 永远不持有令牌（[BFF](bff)）
- **GDPR 自助服务**（*Authagonal Cloud*）：在托管的账户页面中导出数据和安排账户删除。
  登录应用自带相应界面，但它调用的端点
  （`GET /api/v1/account/export`、`POST /api/v1/account/erasure`）由 Cloud 身份验证主机提供，
  **不**属于本库的接口范围。自托管部署必须自行实现这两个端点，或者在账户页面中去掉这两个
  按钮：`MapFallbackToFile` 会以 200 和 SPA 自身的 HTML 响应未实现的路由，
  因此对于未实现的导出，必须能识别出来，而不是把它当作文件下载。
- **TCC 预配**：在授权时以 Try-Confirm-Cancel 方式将用户预配到下游应用
- **可定制品牌的登录界面**：通过 JSON 文件在运行时配置徽标、颜色、CSS 自定义属性，无需重新构建；已本地化为 11 种语言
- **身份验证钩子**：通过 `IAuthHook` 扩展实现审计日志、自定义校验、Webhook
- **PII 加密扩展点**：`IFieldCipher` / `IIndexTokenizer` 扩展点，用于字段级静态加密，并支持带密钥的盲索引（HMAC）搜索；恢复码通过 `ISecretProvider` 加密
- **HashiCorp Vault Transit 客户端**：针对 Vault 的 Transit 引擎执行签名/验证、加密/解密和带密钥 HMAC，可用于构建 `IFieldCipher` 或 `IIndexTokenizer`。远程 JWT 签名未接入：令牌签名密钥始终是 `ISigningKeyStore` 中的那一个。
- **可组合的库**：通过 `AddAuthagonal()` / `UseAuthagonal()` 托管在你自己的项目中，并可覆盖自定义服务
- **支持 Native AOT**：IL 裁剪和源生成的 JSON 序列化，启动迅速
- **可插拔存储**：自托管的 PostgreSQL 或 SQLite（无需云账户），或 Azure Table Storage / AWS（DynamoDB / S3 / Secrets Manager），作为低成本、适合无服务器的后端
- **备份与恢复**：增量备份（由变更日志驱动，并以全量扫描兜底）、完整性校验、基于墓碑记录的删除跟踪
- **管理 API**：用户 CRUD、SAML/OIDC 提供方管理、SSO 域名路由、令牌模拟

## 常见集成 {#common-integrations}

针对团队最常构建的流程的任务型指南：

- **[升级用户](user-upgrade)**：通过无密码的账户认领，将访客 / SSO / 邀请账户转换为带凭据的账户，并在确认时执行你的访客 → 正式成员晋升逻辑。
- **[自助 SSO](self-service-sso)**：企业连接的 JIT 预配：仅限邀请与自助接入的对比、防止外部 IdP 变成隐患，以及联合登录前的中间页。
- **[联合会话](federated-sessions)**：在上游 IdP 撤销会话时撤销本地会话（`RevalidateOnRefresh`）。
- **[Backend-for-Frontend (BFF)](bff)**：让令牌远离浏览器：在你的后端运行机密 OIDC 客户端，配合 httpOnly 会话 Cookie 和注入令牌的 API 代理，支持 .NET 或 Node。
- **[WebSocket 身份验证](websocket-auth)**：通过 BFF 对浏览器 WebSocket 进行身份验证，而不暴露令牌。
- **[智能体身份验证](agentic-auth)**：将用户的权限委托给 AI 智能体：已注册的智能体、细粒度的 RFC 9396 权限、复合委托令牌（RFC 8693 `act`）、长期同意、即时审批、能力票据。
- **[组织](organizations)**：用一个租户服务多个客户：`Organization` 和成员资格记录、`organization` 授权参数、令牌上的 `org_id` / `org_slug` / `org_name`、组织范围的角色，以及拒绝非成员。

## 架构 {#architecture}

```
Client App                    Authagonal                         IdP (Azure AD, etc.)
    │                             │                                    │
    ├─ GET /connect/authorize ──► │                                    │
    │                             ├─ 302 → /login (SPA)                │
    │                             │   ├─ SSO check                     │
    │                             │   └─ SAML/OIDC redirect ─────────► │
    │                             │                                    │
    │                             │ ◄── SAML Response / OIDC callback ─┤
    │                             │   └─ Create user + cookie          │
    │                             │                                    │
    │                             ├─ TCC provisioning (try/confirm)    │
    │                             ├─ Issue authorization code          │
    │ ◄─ 302 ?code=...&state=... ┤                                    │
    │                             │                                    │
    ├─ POST /connect/token ─────► │                                    │
    │ ◄─ { access_token, ... } ──┤                                    │
```

从[安装](installation)指南开始，或直接跳到[快速入门](quickstart)。要在你自己的项目中托管 Authagonal，参见[可扩展性](extensibility)。关于数据管理，参见[备份与恢复](backup-restore)。完整的变更历史参见 [Changelog](https://github.com/authagonal/authagonal/blob/master/CHANGELOG.md)。
