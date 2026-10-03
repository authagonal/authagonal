---
layout: default
title: 快速入门
locale: zh-Hans
---

# 快速入门

5 分钟内在本地运行 Authagonal。

## 1. 启动服务器 {#1-start-the-server}

```bash
docker compose up
```

这会在 `http://localhost:8080` 上启动 Authagonal，并使用 Azurite 作为存储。

> compose 文件设置了 `Auth__AllowInsecureHttp=true`，因为 RFC 6749 §3.1/§3.2 要求授权端点和令牌端点使用 TLS，否则 Authagonal 会拒绝发往 `/connect/*` 的明文请求。这个开关只适用于本机开发。任何其他人能够访问的部署，都必须放在一个终止 TLS 并转发 `X-Forwarded-Proto: https` 的代理之后，并移除该开关：参见[安装](installation)。

## 2. 确认服务正在运行 {#2-verify-its-running}

```bash
# Health check
curl http://localhost:8080/health

# OIDC discovery
curl http://localhost:8080/.well-known/openid-configuration

# Login page (returns the SPA)
curl http://localhost:8080/login
```

## 3. 注册客户端 {#3-register-a-client}

在 `appsettings.json` 中添加一个客户端（也可以通过环境变量传入）：

```json
{
  "Clients": [
    {
      "ClientId": "my-web-app",
      "ClientName": "My Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["http://localhost:3000/callback"],
      "PostLogoutRedirectUris": ["http://localhost:3000"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["http://localhost:3000"],
      "RequirePkce": true,
      "RequireClientSecret": false
    }
  ]
}
```

客户端会在启动时预置，每次部署都运行也是安全的。

## 4. 发起登录 {#4-initiate-a-login}

将用户重定向到：

```
http://localhost:8080/connect/authorize
  ?client_id=my-web-app
  &redirect_uri=http://localhost:3000/callback
  &response_type=code
  &scope=openid profile email
  &state=random-state
  &code_challenge=...
  &code_challenge_method=S256
```

用户会看到登录页面，完成身份验证后，携带授权码被重定向回来。

> **第一个用户：**在 `http://localhost:8080/login/register` 注册一个，或通过 [Admin API](admin-api) 创建。自助注册会发送一封验证邮件，而在未配置邮件发送方的情况下（本地默认如此），这封邮件会被丢弃。因此在本地测试时，可以设置 `Auth__AutoConfirmEmailDomains__0=example.dev`（填写你注册时使用的任意域名）以跳过验证，或者配置 `Email:ResendApiKey` + `Email:SenderEmail`。参见[配置 → 邮件](configuration#email)。

## 5. 兑换授权码 {#5-exchange-the-code}

```bash
curl -X POST http://localhost:8080/connect/token \
  -d grant_type=authorization_code \
  -d code=THE_CODE \
  -d redirect_uri=http://localhost:3000/callback \
  -d client_id=my-web-app \
  -d code_verifier=THE_VERIFIER
```

响应：

```json
{
  "access_token": "eyJ...",
  "id_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 1800,
  "scope": "openid profile email"
}
```

`expires_in` 即客户端的 `AccessTokenLifetimeSeconds`（预置客户端在未设置时为 1800）。这里不会出现 `refresh_token`：只有当客户端设置了 `AllowOfflineAccess`，并且请求中包含 `offline_access` 作用域时，客户端才会收到刷新令牌。

## 可运行的演示 {#working-demo}

`demos/sample-app/` 目录包含一个完整的 React SPA + API，实现了上述完整的 OIDC 流程。使用说明参见 [demos README](https://github.com/authagonal/authagonal/tree/master/demos)。

## 后续步骤 {#next-steps}

- [配置](configuration)：所有设置的完整参考
- [可扩展性](extensibility)：作为库托管，添加自定义钩子
- [品牌定制](branding)：自定义登录界面
- [SAML](saml)：添加 SAML SSO 提供方
- [预配](provisioning)：将用户预配到下游应用
