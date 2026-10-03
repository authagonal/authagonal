---
layout: default
title: 前端通道注销
locale: zh-Hans
---

# 前端通道注销

Authagonal 实现了 **OpenID Connect Front-Channel Logout 1.0**，这是一种由浏览器驱动的注销机制，与[后端通道注销](index#key-features)互为补充。后端通道注销是服务器到服务器的 POST 请求，而前端通道注销会在隐藏的 iframe 中渲染每个依赖方的注销 URL，从而在用户的浏览器内部清理每个应用的浏览器会话（Cookie、本地存储）。

## 何时使用哪一种 {#when-to-use-which}

| 关注点 | 后端通道 | 前端通道 |
|---|---|---|
| 服务器端会话 | ✅ | ❌ |
| 浏览器 Cookie / 本地存储 | ❌ | ✅ |
| 用户浏览器离线时仍然有效 | ✅ | ❌ |
| 能够承受网络错误（重试） | ✅ | ❌（仅尽力尝试一次） |

大多数应用都能从**同时**配置两者中受益。后端通道确保服务器得到通知；前端通道负责清理浏览器。

## 客户端配置 {#client-configuration}

在 `OAuthClient` 记录中添加前端通道注销 URI：

```json
{
  "clientId": "myapp",
  "frontChannelLogoutUri": "https://myapp.example.com/oidc/frontchannel",
  "frontChannelLogoutSessionRequired": true
}
```

| 字段 | 说明 |
|---|---|
| `FrontChannelLogoutUri` | 客户端在浏览器中可见的注销端点 |
| `FrontChannelLogoutSessionRequired` | 为 `true`（默认）时，调用该 URL 会附带 `iss` 和 `sid` 查询参数，以便客户端将注销与具体会话关联起来 |

## 工作原理 {#how-it-works}

当浏览器访问 `/connect/endsession`（GET 或 POST）时：

1. **确认（CSRF 防护）。** 如果浏览器有已登录的会话，而请求没有携带 `sub` 与该会话匹配的 `id_token_hint`，服务器会先渲染一个带确认按钮的“确定注销？”页面，而不是直接让用户注销。该按钮会携带一个绑定到该会话的短期（15 分钟）令牌以 POST 方式提交回来。正是这一点阻止了第三方页面通过导航到该端点来结束用户的会话（会话 Cookie 是 `SameSite=Lax`，因此会随跨站点的顶层 GET 请求一起发送）。匹配的 `id_token_hint` 可以代替确认步骤。
2. 服务器找出用户当前拥有授权的所有客户端。
3. 对于每个设置了 `FrontChannelLogoutUri` 且该 URI 通过出站 URL 检查的客户端（允许回环地址，因为请求是由用户自己的浏览器发出的，但不允许私有网段和链路本地地址），服务器会构建一个 URL；如果 `FrontChannelLogoutSessionRequired` 为 `true`，则附加 `iss=<issuer>`（以及在会话具有会话 ID 时附加 `sid=<session_id>`）。
4. 服务器撤销为该会话签发的授权，让用户从授权服务器的 Cookie 中注销，在后台触发后端通道注销通知；如果至少构建出了一个前端通道 URL，则返回一个 HTML 页面，为每个 URL 包含一个隐藏的 `<iframe>`：
   ```html
   <iframe src="https://myapp.example.com/oidc/frontchannel?iss=https%3A%2F%2Fauth.example.com&sid=abc123" style="display:none"></iframe>
   ```
   该页面带有一个 `Content-Security-Policy`，其 `frame-src` 仅限于这些 URL 的源，并且页面中不含任何脚本。
5. 无论是否涉及 iframe，注销后的目标地址都以相同的方式确定。只有当请求能够识别出客户端（通过 `id_token_hint` 的受众或 `client_id` 参数），并且该 URI 在该客户端已注册的 `PostLogoutRedirectUris` 中时，才会采用 `post_logout_redirect_uri`（如果提供了 `state` 参数，会将其附加上去）。有 iframe 时，页面会等待 2 秒（一个 `meta refresh`）后再重定向；如果没有有效的目标地址，则显示“已注销”消息。没有前端通道 URL 时，服务器会立即重定向（`302`）；如果没有有效的目标地址，则返回 `200` 和一个 JSON `message`。

只有当 `id_token_hint` 是本服务器签名的 ID 令牌（ES256，`typ: JWT`）且只有单一受众时，才会被接受。已过期的令牌也会被接受。访问令牌和注销令牌作为提示都会被拒绝。如果同时发送了 `client_id` 和 `id_token_hint`，且两者指向不同的客户端，请求会以 `400 invalid_request` 失败。

JSON 端点 `POST /api/auth/logout`（登录应用的“注销”按钮使用它）会执行相同的撤销和通知步骤。它不渲染 iframe：而是在 `frontchannel_logout_uris` 中返回这些 URL，由调用方自行加载（参见[认证 API](auth-api#logout)）。

## 客户端注销处理程序 {#client-side-logout-handler}

每个依赖方都应实现 `FrontChannelLogoutUri` 所指向的 URL。一个最简处理程序：

```http
GET /oidc/frontchannel?iss=https://auth.example.com&sid=abc123
```

1. 验证 `iss` 与预期的授权服务器一致。
2. 如果提供了 `sid`，确认它与会话 Cookie 中的会话 ID 一致。
3. 清除本地会话（Cookie、服务器端会话、SPA 存储）。
4. 返回 `200 OK` 和空正文（或一个极小的页面），该响应永远不会被用户看到。

```csharp
app.MapGet("/oidc/frontchannel", (HttpContext ctx) =>
{
    var iss = ctx.Request.Query["iss"].ToString();
    var sid = ctx.Request.Query["sid"].ToString();
    // Validate iss/sid, then clear local session
    ctx.SignOutAsync();
    return Results.Ok();
});
```

## 发现文档 {#discovery-document}

前端通道注销在 `/.well-known/openid-configuration` 中公布：

```json
{
  "frontchannel_logout_supported": true,
  "frontchannel_logout_session_supported": true
}
```

## 动态客户端注册 {#dynamic-client-registration}

通过[动态客户端注册](client-registration)注册的客户端可以包含：

```json
{
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

注册时会拒绝不是外部地址的注销 URI（回环、链路本地、私有网段以及 `.localhost`/`.local`/`.internal` 名称都会以 `invalid_client_metadata` 被拒绝）。

## 局限性 {#limitations}

- **尽力而为**：iframe 只加载一次。如果网络错误或浏览器扩展阻止了它们，不会重试。为了可靠性，请与后端通道注销配合使用。
- **第三方 Cookie**：某些浏览器默认会在跨站点 iframe 中阻止 Cookie。如果你的依赖方依赖第一方 Cookie，请确认注销处理程序不依赖于 Cookie 被发送。
- **超时**：页面会在重定向之前等待约 2 秒。较重的依赖方注销处理程序可能来不及完成。

## 相关内容 {#related}

- [动态客户端注册](client-registration)，注册请求中的前端通道参数
- [OAuth 作用域](scopes)，感知作用域的同意流程与注销流程相辅相成
