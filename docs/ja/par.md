---
layout: default
title: プッシュ型認可リクエスト
locale: ja
---

# プッシュ型認可リクエスト (PAR)

[RFC 9126](https://www.rfc-editor.org/rfc/rfc9126) により、クライアントは標準のクライアント認証を使って認可リクエストのパラメーターをサーバーに直接 POST し、ブラウザに渡すための短命で不透明な `request_uri` を受け取れます。ブラウザは、すべてのパラメーターを URL に載せる代わりに `/connect/authorize?request_uri=...&client_id=...` にアクセスします。

使う理由:

- 認可パラメーターがブラウザの履歴、サーバーのログ、`Referer` ヘッダーに現れることがありません。
- サーバーはプッシュの時点でクライアントを認証するため、リダイレクトが行われる前にパラメーターの完全性が確認されます。
- 長いパラメーターのセット (大きな `claims` リクエスト、複数リソースのフロー) でも URL の長さ制限を超えません。

## エンドポイント {#endpoint}

```
POST /connect/par
Content-Type: application/x-www-form-urlencoded
```

認証は `/connect/token` と同じで、`client_id`/`client_secret` による HTTP Basic か、フォームエンコードされた資格情報を使います。機密クライアントは認証が必須で、パブリッククライアントはシークレットなしで POST します。クライアント認証の失敗は `401` を返します (RFC 9126 に従ったもので、`invalid_client` だけが 401 になるトークンエンドポイントとは異なります)。

フォームのボディには、通常 `/connect/authorize` に載せるのと同じパラメーター (`response_type`、`redirect_uri`、`scope`、`state`、`code_challenge`、`code_challenge_method`、`nonce`、`resource` など) を含めます。`request_uri` 自体は拒否されます。PAR の連鎖は仕様の §2.1 で禁止されているからです。ボディに `client_id` が含まれる場合は、認証されたクライアントと一致する必要があります。トークンエンドポイントと同様に、このルートは `AuthagonalProtocolOptions.AllowInsecureHttp` が設定されていない限り、平文の `http` リクエストを拒否します。

リクエストはプッシュの時点で、`/connect/authorize` が検証するのと同じ方法で検証されます (登録済みの `redirect_uri`、許可されたスコープ、PKCE、`prompt` の値など)。不正なリクエストは直ちに `400 invalid_request` で拒否され、`request_uri` は発行されません。そのため誤りは、フローの途中でエンドユーザーにではなく、クライアントに明らかになります。`authorization_details` は `invalid_authorization_details` で拒否されます (リッチ認可リクエストはここではなく、トークンエンドポイントで扱うものです)。

### 制限 {#limits}

- ボディの上限は 32 KB で、フォームのフィールドは最大 64 個、名前は 256 文字、値は 1 つあたり 8 KB までです。それを超えるものは `413 invalid_request` で拒否されます。
- リクエストには、クライアントと送信元アドレスごとに毎分 60 回、クライアントごとの合計で毎分 300 回のレート制限があり、超えると `429 temporarily_unavailable` を返します。

### レスポンス {#response}

```
HTTP/1.1 201 Created
```
```json
{
  "request_uri": "urn:ietf:params:oauth:request_uri:abc123...",
  "expires_in": 90
}
```

`request_uri` は 1 回限りです。それに対して認可コードが発行された時点でストアから削除されます。一度も使われなかった場合は、90 秒後に失効します。

### 認可ステップ {#authorization-step}

```
GET /connect/authorize?client_id=my-rp&request_uri=urn:ietf:params:oauth:request_uri:abc123...
```

`request_uri` が存在する場合、他のパラメーターはすべてプッシュされたペイロードから取り出され、URL 上のそれ以外のものは無視されます (例外は、ペイロードをプッシュしたクライアントと一致する必要がある `client_id` と、フェデレーションの往復が失敗した際に付加される `error` パラメーターです)。未知、失効済み、使用済み、または別のクライアントがプッシュした `request_uri` は、`invalid_request` で拒否されます。受け付けられるのは、このサーバー自身の PAR エンドポイントが発行した不透明な URN だけです。それ以外の `request_uri` の値は `request_uri_not_supported` で拒否され、RFC 9101 の `request` パラメーターは `request_not_supported` で拒否されます。

プッシュされた `prompt` と `max_age` の値は尊重されます。`prompt=login` (またはセッションがすでに超えている `max_age`) を含む PAR リクエストを満たせるのは、`auth_time` がリクエストのプッシュ時点以降であるセッションだけです。そのため既存のセッションは一度サインアウトされて再認証され、ログインからの戻りではループせずにコードが発行されます。

## クライアントごとに PAR を必須にする {#requiring-par-per-client}

クライアントに `RequirePushedAuthorizationRequests = true` を設定すると、そのクライアントからの通常の `/connect/authorize` リクエストが拒否されます。PAR を使わない認可の試みには、「This client requires requests to be pushed via /connect/par」という説明付きで `invalid_request` が返されます。

```csharp
new OAuthClient
{
    ClientId = "high-risk-rp",
    RequirePushedAuthorizationRequests = true,
    // ...
}
```

機密性の高いスコープを扱うクライアントには、これが推奨される構成です。PKCE と組み合わせることで、アドレスバーを攻撃対象から外せます。

## 有効期間とストレージ {#lifetime-and-storage}

プッシュで返される `expires_in` は 90 秒で、この時間はプッシュから最初の `/connect/authorize` リクエストまでの移動をカバーします。レコードが最初に取り出されると、有効期限はプッシュから 15 分後という絶対的な期限まで (1 回だけ) 延長され、ユーザーはログイン、MFA、同意を完了できます。90 秒と 15 分の値は定数で、設定はできません。プッシュされたペイロードは、認可コードやリフレッシュトークンと同じ `IGrantStore` に保存されるため、ホストの永続化とレプリケーションの方式を自動的に引き継ぎます。

## ディスカバリ {#discovery}

PAR エンドポイントは `.well-known/openid-configuration` で次のように公開されます。

```json
{
  "pushed_authorization_request_endpoint": "https://auth.example.com/connect/par"
}
```
