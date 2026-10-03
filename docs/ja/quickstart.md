---
layout: default
title: クイックスタート
locale: ja
---

# クイックスタート

Authagonal を 5 分でローカル実行します。

## 1. サーバーを起動する {#1-start-the-server}

```bash
docker compose up
```

これにより、ストレージに Azurite を使った Authagonal が `http://localhost:8080` で起動します。

> compose ファイルは `Auth__AllowInsecureHttp=true` を設定しています。RFC 6749 §3.1/§3.2 は認可エンドポイントとトークンエンドポイントに TLS を要求しており、Authagonal はそうでなければ `/connect/*` への平文のリクエストを拒否するためです。このスイッチはノート PC での利用のためのものです。他の誰かがアクセスできる環境では、`X-Forwarded-Proto: https` を転送する TLS 終端プロキシの背後に置き、このスイッチは外してください。詳しくは [インストール](installation) を参照してください。

## 2. 動作を確認する {#2-verify-its-running}

```bash
# Health check
curl http://localhost:8080/health

# OIDC discovery
curl http://localhost:8080/.well-known/openid-configuration

# Login page (returns the SPA)
curl http://localhost:8080/login
```

## 3. クライアントを登録する {#3-register-a-client}

`appsettings.json` にクライアントを追加します (環境変数で渡すこともできます)。

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

クライアントは起動時に初期投入されるため、デプロイのたびに実行しても安全です。

## 4. ログインを開始する {#4-initiate-a-login}

ユーザーを次の URL にリダイレクトします。

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

ユーザーにはログインページが表示され、認証が済むと認可コード付きでリダイレクトされて戻ってきます。

> **最初のユーザー:** `http://localhost:8080/login/register` で登録するか、[管理 API](admin-api) で作成してください。セルフ登録では確認メールが送信されますが、メール送信元が構成されていない場合 (ローカルの既定) そのメールは破棄されます。ローカルでのテストでは、`Auth__AutoConfirmEmailDomains__0=example.dev` (登録に使う任意のドメイン) を設定して確認を省略するか、`Email:ResendApiKey` と `Email:SenderEmail` を構成してください。[構成 → メール](configuration#email) を参照してください。

## 5. コードを交換する {#5-exchange-the-code}

```bash
curl -X POST http://localhost:8080/connect/token \
  -d grant_type=authorization_code \
  -d code=THE_CODE \
  -d redirect_uri=http://localhost:3000/callback \
  -d client_id=my-web-app \
  -d code_verifier=THE_VERIFIER
```

レスポンス:

```json
{
  "access_token": "eyJ...",
  "id_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 1800,
  "scope": "openid profile email"
}
```

`expires_in` はクライアントの `AccessTokenLifetimeSeconds` です (設定しない限り、初期投入されたクライアントでは 1800)。ここには `refresh_token` は含まれません。クライアントが refresh_token を受け取るのは、`AllowOfflineAccess` を設定し、かつリクエストが `offline_access` スコープを要求した場合だけです。

## 動作するデモ {#working-demo}

`demos/sample-app/` ディレクトリには、上記の OIDC フロー全体を実装した完全な React SPA と API が含まれています。手順は [デモの README](https://github.com/authagonal/authagonal/tree/master/demos) を参照してください。

## 次のステップ {#next-steps}

- [構成](configuration): すべての設定の完全なリファレンス
- [拡張性](extensibility): ライブラリとしてホストし、独自のフックを追加する
- [ブランディング](branding): ログイン UI をカスタマイズする
- [SAML](saml): SAML SSO プロバイダーを追加する
- [プロビジョニング](provisioning): 下流アプリにユーザーをプロビジョニングする
