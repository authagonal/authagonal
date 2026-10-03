---
layout: default
title: 推送授权请求
locale: zh-Hans
---

# 推送授权请求（PAR）

[RFC 9126](https://www.rfc-editor.org/rfc/rfc9126) 允许客户端使用标准的客户端认证，将其授权请求参数直接 POST 到服务器，并获得一个生命周期很短的不透明 `request_uri`，再把它交给浏览器。之后浏览器访问 `/connect/authorize?request_uri=...&client_id=...`，而不必在 URL 中携带全部参数。

使用它的理由：

- 授权参数永远不会出现在浏览器历史记录、服务器日志或 `Referer` 请求头中。
- 服务器在推送时就对客户端进行认证，因此参数在任何重定向发生之前就已经过完整性校验。
- 很长的参数集（大型 `claims` 请求、多资源流程）不会超出 URL 长度限制。

## 端点 {#endpoint}

```
POST /connect/par
Content-Type: application/x-www-form-urlencoded
```

认证方式与 `/connect/token` 相同：使用 `client_id`/`client_secret` 的 HTTP Basic，或者表单编码的凭据。机密客户端必须进行认证；公共客户端在不带密钥的情况下提交。客户端认证失败返回 `401`（遵循 RFC 9126，这一点与令牌端点不同，令牌端点只有 `invalid_client` 才返回 401）。

表单正文携带的参数与通常放在 `/connect/authorize` 上的参数相同（`response_type`、`redirect_uri`、`scope`、`state`、`code_challenge`、`code_challenge_method`、`nonce`、`resource` 等）。`request_uri` 本身会被拒绝，因为规范 §2.1 禁止链式 PAR。如果正文携带了 `client_id`，它必须与已认证的客户端一致。与令牌端点一样，除非设置了 `AuthagonalProtocolOptions.AllowInsecureHttp`，否则该路由会拒绝明文 `http` 请求。

请求在推送时就会按照 `/connect/authorize` 的方式进行校验（已注册的 `redirect_uri`、允许的作用域、PKCE、`prompt` 值等）。无效的请求会立即以 `400 invalid_request` 被拒绝，并且不会颁发 `request_uri`，因此错误会暴露给客户端，而不是在流程进行到一半时暴露给最终用户。`authorization_details` 会以 `invalid_authorization_details` 被拒绝（富授权请求属于令牌端点，而不属于这里）。

### 限制 {#limits}

- 正文上限为 32 KB，最多 64 个表单字段，字段名最长 256 个字符，每个值最大 8 KB。超出的请求会以 `413 invalid_request` 被拒绝。
- 请求按每个客户端和来源地址每分钟 60 次、每个客户端总计每分钟 300 次进行限流，超出时返回 `429 temporarily_unavailable`。

### 响应 {#response}

```
HTTP/1.1 201 Created
```
```json
{
  "request_uri": "urn:ietf:params:oauth:request_uri:abc123...",
  "expires_in": 90
}
```

`request_uri` 只能使用一次。为它颁发授权码时，它会从存储中删除。如果它从未被兑换，会在 90 秒后过期。

### 授权步骤 {#authorization-step}

```
GET /connect/authorize?client_id=my-rp&request_uri=urn:ietf:params:oauth:request_uri:abc123...
```

当存在 `request_uri` 时，所有其他参数都从推送的载荷中读取，URL 上的其他任何内容都会被忽略（`client_id` 除外，它必须与推送该载荷的客户端一致；联合认证往返失败时附加的 `error` 参数也除外）。未知、已过期、已被消耗或由其他客户端推送的 `request_uri` 会以 `invalid_request` 被拒绝。只接受由本服务器自身的 PAR 端点颁发的不透明 URN：任何其他 `request_uri` 值都会以 `request_uri_not_supported` 被拒绝，RFC 9101 的 `request` 参数则以 `request_not_supported` 被拒绝。

推送的 `prompt` 和 `max_age` 值会被遵循。携带 `prompt=login`（或者会话已超过其 `max_age`）的 PAR 请求，只有 `auth_time` 不早于请求推送时刻的会话才能满足，因此已有的会话会被注销并重新认证一次，而从登录返回时会颁发授权码，不会陷入循环。

## 按客户端要求使用 PAR {#requiring-par-per-client}

在客户端上设置 `RequirePushedAuthorizationRequests = true`，即可拒绝该客户端发来的普通 `/connect/authorize` 请求。任何非 PAR 的授权尝试都会返回 `invalid_request`，描述为“This client requires requests to be pushed via /connect/par”。

```csharp
new OAuthClient
{
    ClientId = "high-risk-rp",
    RequirePushedAuthorizationRequests = true,
    // ...
}
```

对于处理敏感作用域的客户端，推荐采用这种做法，与 PKCE 结合使用时，它把地址栏从攻击面中移除了。

## 生命周期与存储 {#lifetime-and-storage}

推送返回的 `expires_in` 为 90 秒，这个时间窗口涵盖从推送到第一个 `/connect/authorize` 请求之间的这一步。记录第一次被读取后，会（一次性地）延长到从推送时刻起 15 分钟的绝对截止时间，以便用户完成登录、MFA 和同意。90 秒和 15 分钟这两个值是常量，不可配置。推送的载荷与授权码和刷新令牌一样通过 `IGrantStore` 存储，因此会自动继承宿主的持久化和复制策略。

## 发现 {#discovery}

PAR 端点在 `.well-known/openid-configuration` 中按如下方式公布自己：

```json
{
  "pushed_authorization_request_endpoint": "https://auth.example.com/connect/par"
}
```
