---
layout: default
title: 動的クライアント登録
locale: ja
---

# 動的クライアント登録

Authagonal は **OAuth 2.0 動的クライアント登録** ([RFC 7591](https://datatracker.ietf.org/doc/html/rfc7591)) を実装しており、クライアントアプリケーションは管理者の関与なしに実行時に自らを登録できます。

## エンドポイントの有効化 {#enabling-the-endpoint}

動的登録は**既定で無効**です。設定でオプトインします。

```json
{
  "Auth": {
    "DynamicClientRegistrationEnabled": true
  }
}
```

または、環境変数として `Auth__DynamicClientRegistrationEnabled=true` を設定します。マルチテナントのホストは、`ITenantContext.DynamicClientRegistrationEnabled` を通じてテナントごとにこの設定を上書きできます。テナント自身の値が優先され、`null` の場合はホスト全体のオプションが使われます。

有効にすると、ディスカバリドキュメントがエンドポイントを公開します。

```
GET /.well-known/openid-configuration
```
```json
{
  "registration_endpoint": "https://auth.example.com/connect/register"
}
```

## クライアントの登録 {#registering-a-client}

```
POST /connect/register
Content-Type: application/json

{
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "token_endpoint_auth_method": "client_secret_basic",
  "scope": "openid profile email offline_access",
  "audiences": ["https://api.myapp.example.com"],
  "allowed_cors_origins": ["https://myapp.example.com"],
  "backchannel_logout_uri": "https://myapp.example.com/oidc/backchannel",
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

### レスポンス {#response}

```
HTTP/1.1 201 Created
Content-Type: application/json

{
  "client_id": "a1b2c3d4e5f6...",
  "client_secret": "xkCd2_base64url...",
  "client_id_issued_at": 1745000000,
  "client_secret_expires_at": 0,
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "scope": "openid profile email offline_access",
  "token_endpoint_auth_method": "client_secret_basic"
}
```

`client_secret` は**一度だけ**返され、後から取得することはできません。安全に保管してください。レスポンスは `Cache-Control: no-store` 付きで送信されます。`client_id` は 32 文字の小文字の 16 進数で、`client_secret_expires_at` は常に `0` です (シークレットは失効しません)。パブリッククライアント (`none`) と `private_key_jwt` クライアントのレスポンスには `client_secret` が含まれません。レスポンスがそのまま返すのは示したフィールドだけです。`audiences`、`jwks`、`jwks_uri`、`allowed_cors_origins`、およびログアウト関連のフィールドは保存されますが、返されません。

## リクエストパラメーター {#request-parameters}

| パラメーター | 必須 | 備考 |
|---|---|---|
| `client_name` | いいえ | 省略すると、生成された `client_id` が既定値になります |
| `redirect_uris` | 条件付き | `grant_types` に `authorization_code` が含まれる場合は必須です。絶対 URI でなければならず、`javascript:`/`data:`/`vbscript:`/`file:` スキームは拒否されます (モバイルのディープリンク用のネイティブのカスタムスキームは問題ありません)。フラグメントは拒否され (RFC 6749 §3.1.2)、平文の `http` はループバックホストに対してのみ受け付けられます (RFC 8252 §7.3)。最大 20 件で、各エントリは最大 2048 文字です。 |
| `post_logout_redirect_uris` | いいえ | ログアウト後の有効なリダイレクト先です。`redirect_uris` と同じく、20 件 / 2048 文字の上限があります。 |
| `grant_types` | いいえ | 既定値は `["authorization_code"]` です。**登録できるのは `authorization_code` と `refresh_token` だけです**。`client_credentials`、`implicit`、デバイスフロー、その他のグラントタイプは `invalid_client_metadata` で拒否されるため、オープンな登録によってマシン間 (M2M) クライアントが作られることは決してありません。`offline_access` が要求された場合、`refresh_token` は自動的に追加されます。 |
| `token_endpoint_auth_method` | いいえ | `client_secret_basic` (既定)、`client_secret_post`、`private_key_jwt`、またはパブリッククライアント用の `none` です。それ以外の値は `invalid_client_metadata` で拒否されます。 |
| `jwks` / `jwks_uri` | `private_key_jwt` の場合 | クライアントの公開鍵です。`private_key_jwt` にはどちらか一方が必要で (ない場合は `invalid_client_metadata`)、`jwks_uri` は送信先 URL のガード (外部アドレスであること) を通過しなければなりません。`private_key_jwt` クライアントにはシークレットが発行されません。 |
| `scope` | いいえ | スペース区切りのスコープです。登録できるのは、OIDC 組み込みの 5 つ (`openid`、`profile`、`email`、`phone`、`offline_access`) と、`Auth:DynamicClientRegistrationScopes` に列挙されたものだけです。スコープストアに存在するだけでは**不十分**です ([スコープ](scopes)を参照)。ロールでゲートされたスコープと管理用スコープ (`AdminApi:Scope`、既定値 `authagonal-admin`) は決して登録できません。 |
| `audiences` | いいえ | アクセストークンに追加される JWT の `aud` の値です。最大 20 件、各 512 文字以内で、それぞれフラグメントを含まない絶対 URI でなければなりません。不正な値は `invalid_client_metadata` になります。 |
| `allowed_cors_origins` | いいえ | 各エントリは有効なオリジンでなければなりませんが (そうでなければ `invalid_client_metadata`)、値は**送信されたとおりには保存されません**。クライアントの許可オリジンは、そのクライアント自身の `https` の `redirect_uris` のオリジンから導出されるため、登録者が到達できるのは、すでにリダイレクト URI で証明したオリジンだけです。 |
| `backchannel_logout_uri` | いいえ | [バックチャネルログアウト](index#key-features)を有効にします |
| `frontchannel_logout_uri` | いいえ | [フロントチャネルログアウト](front-channel-logout)を有効にします |
| `frontchannel_logout_session_required` | いいえ | 既定値は `true` です。`true` の場合、ログアウト URL に `iss` と `sid` パラメーターが付きます |

## 既定値と不変条件 {#defaults--invariants}

- **PKCE 必須**: 動的に登録されたクライアントでは、`RequirePkce` は常に `true` です。
- **同意必須**: `RequireConsent` は常に `true` です。そのため、静的に初期投入されたクライアントなら同意画面を省略する場面でも、自己登録されたクライアントではユーザーに同意画面が表示されます。
- **パブリッククライアント**: `token_endpoint_auth_method: "none"` を指定すると、シークレットを持たないクライアントが作られます。PKCE は引き続き必須です。
- **オフラインアクセス**: スコープ `offline_access` を要求すると、`grant_types` に `refresh_token` が暗黙的に追加されます。

## エラーレスポンス {#error-responses}

| HTTP | `error` | 原因 |
|---|---|---|
| `400` | `invalid_redirect_uri` | `redirect_uris` のいずれかが有効な絶対 URI でない、スクリプト/データ/ファイルの疑似スキームを使っている、フラグメントを含む、ループバック以外のホストへの平文の `http` である、または (どちらの URI リストでも) 2048 文字を超えている |
| `400` | `invalid_client_metadata` | 登録できないグラントタイプが要求された、それを必要とするグラントタイプで `redirect_uris` がない、`token_endpoint_auth_method` がサポートされていない、`private_key_jwt` に `jwks`/`jwks_uri` がない (または `jwks_uri` が安全でない)、`audiences` が不正、`allowed_cors_origins` のエントリがオリジンでない、またはログアウト URI が外部アドレスでない |
| `400` | `invalid_scope` | 要求されたスコープが組み込みでも登録済みでもない |
| `400` | `invalid_client_metadata` | `redirect_uris` / `post_logout_redirect_uris` が 20 件を超えている |
| `403` | `invalid_scope` | 要求されたスコープが登録可能でない (`Auth:DynamicClientRegistrationScopes` にない、またはロールでゲートされている) |
| `403` | `invalid_scope` | 管理用スコープが要求された (登録を通じて付与されることは決してありません) |
| `403` | `invalid_scope` | 登録済みの `IClientScopeGuard` が要求されたスコープを拒否した (匿名の呼び出し元がガードに渡されます) |
| `403` | `not_supported` | 動的クライアント登録が有効になっていない |
| `429` | `rate_limited` | この IP からの登録が多すぎる (1 時間あたり 10 件) |

## セキュリティ上の考慮事項 {#security-considerations}

登録エンドポイントは**認証なし**ですが、設計上制約が課されています。

- **レート制限**: 送信元アドレスごとに、直近 1 時間あたり 10 件の登録まで (`429 rate_limited`) なので、クライアントストアを大量の登録で溢れさせることはできません。対象となるアドレスは呼び出し元が選べないものです (転送ヘッダーの値を無条件に信頼することはありません)。
- **グラントタイプの制限**: `authorization_code` + `refresh_token` のみです。登録されたクライアントは常にユーザーを介したフローを必要とし、マシン間クライアントとして振る舞うことは決してできません。
- **スコープは許可リスト方式で、継承されない**: オペレーターが `Auth:DynamicClientRegistrationScopes` にスコープを列挙しない限り、登録者が宣言できるのは OIDC 組み込みの 5 つだけです。スコープストアに存在することは許可ではありません。スコープが存在するのはいずれかのクライアントが必要としているからであって、匿名の登録者なら誰でもそれを要求してよいからではありません。
- **管理用スコープは予約済み**: `authagonal-admin` スコープ (または `AdminApi:Scope` に設定された値) は拒否されるため、登録によって [管理 API](admin-api) に到達できるクライアントが作られることは決してありません。
- **ログアウト URI の検証**: `backchannel_logout_uri` と `frontchannel_logout_uri` にはサーバーがアクセスするため、外部の http(s) エンドポイントでなければなりません。ループバック、RFC1918、リンクローカル (クラウドのメタデータアドレスを含む)、`.internal`/`.local` のホストは拒否されます。
- **レコードサイズの上限**: リダイレクト URI は最大 20 件、各 2048 文字以内なので、1 回の登録でクライアントストアを肥大化させることはできません。
- **CORS オリジンは導出され、信頼されない**: 保存されるオリジンはクライアント自身の `https` リダイレクト URI に由来し、リクエストボディから取られることはありません。
- 登録されたクライアントでは **PKCE が常に必須**で、**同意も常に必須**です。

登録者がオプトインしない限り制約されない**もの**が、オーディエンスです。RFC 7591 にはそのためのフィールドがないため、標準的な登録では `audiences` (Authagonal の拡張) を完全に省略します。この場合クライアントには一度も尋ねられておらず、そのリストは「未設定」で、認可エンドポイントで任意の絶対 URI を `resource` として指定し、その値を `aud` に持つトークンを受け取れます。これは意図的なものであり (MCP の認可仕様はクライアントが MCP サーバーをリソースとして指定することを求めており、MCP クライアントは DCR クライアントです)、その結果、リソースサーバーは `iss` + `aud` + `sub` ではなく `scope` に基づいて認可する責任を負います。`audiences` を**送信する**と、たとえ空のリストであってもそれが回答となり、クライアントはその値に固定されます。空でないリストは `resource` の許可リストとなり、明示的な `[]` はクライアントがリソースを一切指定できないことを意味します。トークン交換は例外で、そこでは `Audiences` が未設定だと即座に拒否されるため、登録されたクライアントが交換済みトークンをどこかに向けることはできません。[オーディエンスとリソースインジケーター](configuration#audiences-and-resource-indicators-rfc-8707)を参照してください。

より強力なゲート (初期アクセストークン、mTLS、ソフトウェアステートメント) が必要な場合は、独自のミドルウェアまたは `IAuthHook` をエンドポイントの前段に置いてください。セルフサービス登録が要件でない環境では、動的登録を完全に無効にし、管理 API でクライアントを管理することを検討してください。
