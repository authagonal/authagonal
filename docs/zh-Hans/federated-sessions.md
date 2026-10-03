---
layout: default
title: 联合会话
locale: zh-Hans
---

# 让联合会话与上游保持同步

当用户通过[外部 IdP](oidc-federation) 登录时，Authagonal 会签发*自己的*会话和令牌。默认情况下，这个本地会话此后就独立存在：如果客户在其目录中禁用了该用户，或者访客的共享链接在上游被撤销，本地的 Authagonal 会话仍会继续有效，直到其 Cookie 过期。

若希望离职处理和撤销能够迅速生效，请开启 **`RevalidateOnRefresh`**。开启后，每次本地刷新令牌时，Authagonal 都会向 IdP 兑换上游刷新令牌；如果上游表示该凭据已失效，本地刷新就会被拒绝，会话将在一个访问令牌的生命周期内停止获得新令牌。

`RevalidateOnRefresh` 只是 **OIDC 连接**的设置。SAML 没有可兑换的刷新令牌，因此 SAML 连接无法重新验证；请改用断言自身的会话过期时间来限制这些会话（参见 [SAML](saml)）。

## 启用 {#enable-it}

按连接设置（按下文所示从配置中预置，或者在通过[管理 API](admin-api) 创建连接时设置），并且上游必须真的会颁发刷新令牌：

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "MetadataLocation": "https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration",
      "ClientId": "...", "ClientSecret": "...",
      "AllowedDomains": ["acme.com"],
      "RevalidateOnRefresh": true
    }
  ]
}
```

设置就这么多。开启该标志后，Authagonal 会在向上游请求的作用域中加入 `offline_access`（如果下游请求尚未携带它），保存上游返回的刷新令牌，并在每次本地刷新时以服务器到服务器的方式兑换它。上游刷新令牌**永远不会**发给任何客户端。它以加密形式保存在一个按会话划分的持久化存储中，在登录时写入，统一设置七天的过期时间（即会话的绝对上限），并且只用于重新验证。

上游必须配合：如果其应用注册始终没有获得 `offline_access`（例如未授予同意），就不会返回刷新令牌，也就没有可兑换的东西。处于这种状态时，Authagonal 会在每次刷新时记录一条警告（`RevalidateOnRefresh is enabled for connection ... but no upstream refresh token is held`），并且**不会**重新检查上游。

## 刷新时会发生什么 {#what-happens-on-refresh}

1. 依赖方照常刷新 Authagonal 令牌（在 `/connect/token` 使用 `grant_type=refresh_token`）。
2. Authagonal 在 IdP 的令牌端点兑换上游刷新令牌：
   - **成功** → 本地刷新继续进行；如果上游轮换了令牌，新令牌会被保存，并由该会话中所有依赖方的授权共享。
   - **`invalid_grant`** → 联合凭据已失效（用户被禁用、会话被撤销、令牌已过期）。本地刷新会被**拒绝**：依赖方从 `/connect/token` 收到 `invalid_grant`，保存的上游令牌会被删除。这次拒绝只让该请求失败；它不会撤销 Authagonal 的授权，因此依赖方的刷新令牌不会被消耗，并且只要上游仍处于撤销状态，就会一直被拒绝。
   - **任何其他 4xx**（例如密钥轮换或配置错误导致的 `invalid_client`，或者 429）、5xx、无法解析的错误正文、传输失败，或者无法加载连接（已删除、发现失败或密钥失败）→ 视为**暂时性**错误：会话保留，以免运维失误导致所有联合用户被集体注销。修复配置即可，不会丢失任何东西。会话仍然受会话绝对上限的约束。

由于每个浏览器会话只有**一个**上游令牌（以用户 + 连接 + 会话为键），用户打开的第二个依赖方读取和轮换的是*同一个*令牌：因此一个应用的刷新不会让另一个应用手里留下一个已失效的副本。

## 无需实现任何东西 {#nothing-to-implement}

这里没有需要编写的接口。持久化存储（`IUpstreamRefreshTokenStore`）由 Azure、AWS 和 SQL 存储提供程序自动注册，兑换过程在内部完成。你只需要在上游拥有可撤销凭据的连接上打开 `RevalidateOnRefresh`。

无论会话通过哪种途径结束，只要该途径能触及存储，保存的令牌都会被删除：注销、在账户页面撤销会话、“在所有地方注销”，以及过期清理。如果宿主没有注册存储，则以会话 Cookie 中携带的副本作为后备。

每个 OIDC 联合会话都会记录它属于哪个连接（`upstream_connection_id` 声明），无论该连接是否重新验证。这只是记录信息：对于没有设置该标志的连接，不会兑换任何东西。

> **适用范围：**只要注册了存储（Azure Table、DynamoDB 和 SQL 提供程序），此功能就会生效。请在连接到**可信**上游的连接上启用它，特别是会对刷新令牌进行一次性轮换的上游（Entra、Auth0），对于第三方 IdP，请将其与 `IsExternalConnection` 结合使用（参见[自助 SSO](self-service-sso)）。

## 补充手段：硬性会话上限 {#complementary-a-hard-session-cap}

`RevalidateOnRefresh` 让会话能够及时反映上游的撤销。如果你希望（或者还希望）本地会话永远不会*比*上游声明的会话*活得更久*，请将 `SessionExpClaim` 设置为某个携带过期时间（Unix 秒）的 id_token 声明的名称。Authagonal 会把本地会话以及由它签发的所有令牌（包括刷新轮换，也包括通过该会话批准的设备码授权）限制在该时间以内。参见 [OIDC 联合 → 会话生命周期上限](oidc-federation)。

之所以要特别提到设备流，是因为它直到最近还是例外：设备码记录没有地方携带批准它的会话的时间上限，因此通过联合会话批准的设备在该会话结束后，仍会在客户端完整的刷新令牌绝对生命周期内持续获得令牌，而 `RevalidateOnRefresh` 也从未就此重新询问上游。现在两者都会跟随批准时的会话。通过*非联合*会话批准的设备没有可继承的上限，这与授权流程的结果相同。
