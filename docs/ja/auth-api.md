---
layout: default
title: 認証 API
locale: ja
---

# 認証 API

これらのエンドポイントはログイン SPA を支えるものです。Cookie 認証 (`SameSite=Lax`、`HttpOnly`) を使用します。

独自のログイン UI を構築する場合は、これらのエンドポイントに対して実装することになります。

## エンドポイント {#endpoints}

### ログイン {#login}

```
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**成功 (200):** 認証 Cookie を設定し、次の内容を返します。

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe",
  "mfaAvailable": false
}
```

`mfaAvailable` は、クライアントの `MfaPolicy` が `Enabled` で、ユーザーがまだ登録していない場合に `true` になります (UI はセットアップを勧めることができます)。その場合は `clientId` フィールドも含まれます。

**MFA が必要 (200):** ユーザーが MFA を登録済みの場合、要求元のクライアントの `MfaPolicy` にかかわらず、**常に**チャレンジが行われます (MFA はクライアントではなく、ユーザーとセッションの属性です)。

```json
{
  "mfaRequired": true,
  "challengeId": "a1b2c3...",
  "methods": ["totp", "webauthn", "recoverycode"],
  "webAuthn": { /* PublicKeyCredentialRequestOptions */ }
}
```

クライアントは MFA チャレンジのページにリダイレクトし、`POST /api/auth/mfa/verify` を呼び出す必要があります。

**MFA のセットアップが必要 (200):** `MfaPolicy` が `Required` で、ユーザーが MFA を登録していない場合です。

```json
{
  "mfaSetupRequired": true,
  "setupToken": "abc123..."
}
```

クライアントは MFA セットアップのページにリダイレクトする必要があります。セットアップトークンは、`X-MFA-Setup-Token` ヘッダーを通じて、MFA セットアップのエンドポイントに対してユーザーを認証します。

**エラーレスポンス:**

| `error` | ステータス | 説明 |
|---|---|---|
| `invalid_credentials` | 401 | メールアドレスまたはパスワードが間違っています。未登録のメールアドレスでも意図的に同じ応答になります (列挙対策)。 |
| `locked_out` | 423 | 失敗した試行が多すぎます。`retryAfter` (秒) が含まれます。 |
| `account_disabled` | 403 | アカウントが無効化されています (正しいパスワードの後にのみ表示) |
| `email_not_confirmed` | 403 | メールアドレスがまだ確認されていません (正しいパスワードの後にのみ表示) |
| `sso_required` | 409 | ドメインで SSO が必須です。`redirectUrl` は SSO ログインを指します。 |
| `captcha_failed` | 400 | Turnstile の検証に失敗しました (Turnstile が構成されている場合のみ。その場合、リクエストには `turnstileToken` フィールドが必要です) |
| `email_required` | 400 | メールアドレスのフィールドが空です |
| `password_required` | 400 | パスワードのフィールドが空です |

### 登録 {#register}

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

新しいユーザーアカウントを作成し、確認メールを送信します。`201 { "success": true, "userId": "..." }` を返します。省略可能なフィールドは `locale` (ユーザーに保存される BCP-47 タグ) と `customAttributes` (文字列のマップ) です。

登録は意図的に**列挙に対して中立**になっています。メールアドレスが既に登録されている場合も、レスポンスは同じ中立的な `201` (使い捨ての `userId` を含む) であり、代わりに実際の所有者にサインインまたはリセットの通知がメールで送られます。登録には IP ごとのレート制限もあり、超過すると `429 rate_limited` になります (期間と上限は `Auth:MaxRegistrationsPerIp` / `Auth:RegistrationWindowMinutes` で構成できます)。

### メールアドレスの確認 {#confirm-email}

```
GET  /api/auth/confirm-email?token={token}
POST /api/auth/confirm-email?token={token}
```

確認メールに含まれるトークンを使って、ユーザーのメールアドレスを確認します。`GET` はメール内のクリック可能なリンクで、`/login?email_confirmed=1` にリダイレクトします (登録が OAuth フローから始まった場合は `continue_client` パラメーターも付きます)。`POST` はプログラムから使う経路で、JSON を返します (トークンは JSON 本文の `{ "token": "..." }` として渡すこともできます)。レスポンスには省略可能な `appLink` (「アプリに戻る」の遷移先) が含まれます。

### プロバイダー {#providers}

```
GET /api/auth/providers
```

構成されている外部 ID プロバイダーの一覧を返します (SSO ボタンの表示用)。

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "type": "oidc", "iconUrl": null, "loginUrl": "/oidc/google/login" }
  ],
  "turnstileSiteKey": null
}
```

`AllowedDomains` が構成された接続は**除外されます**。それらはボタンではなく、`/api/auth/sso-check` を通じてメールアドレスを先に入力する形で到達します。Cloudflare Turnstile が構成されている場合は `turnstileSiteKey` が設定されます (その場合、ログイン UI はログイン、登録、パスワードのリクエストで `turnstileToken` を送信しなければなりません)。

### ログアウト {#logout}

```
POST /api/auth/logout
```

呼び出し元のセッションを `/connect/endsession` と同じ方法で終了します。URI を登録しているリライングパーティーにはバックチャネルログアウトトークンが送信され、そのセッションのために発行されたグラントは失効し、認証 Cookie は消去されます。Cookie 認証と同一オリジンのリクエストが必要です。`200` を返します。

```json
{
  "success": true,
  "frontchannel_logout_uris": ["https://myapp.example.com/oidc/frontchannel"]
}
```

`frontchannel_logout_uris` には、ブラウザーでのサインアウトを完了するために呼び出し元が (非表示の iframe で) 読み込むべきフロントチャネルログアウトの URL が列挙されます。どのクライアントも登録していなければ空です。[フロントチャネルログアウト](front-channel-logout) を参照してください。

### パスワードを忘れた場合 {#forgot-password}

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

常に `200` を返します (列挙対策)。ユーザーが存在する場合は、リセット用のメールを送信します。

### パスワードのリセット {#reset-password}

```
POST /api/auth/reset-password
Content-Type: application/json

{
  "token": "base64-encoded-token",
  "newPassword": "NewSecurePass1!"
}
```

| `error` | 説明 |
|---|---|
| `weak_password` | 強度の要件を満たしていません |
| `invalid_token` | トークンの形式が不正です |
| `token_expired` | トークンの有効期限が切れています (既定の有効期間は 60 分で、`Auth:PasswordResetExpiryMinutes` で構成できます) |

### セッション {#session}

```
GET /api/auth/session
```

認証されている場合、現在のセッションの情報を返します。

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

認証されていない場合は `401` を返します。

### アプリ {#apps}

```
GET /api/auth/apps
```

アカウントページの「アプリに戻る」ランチャー用に、テナントのアプリケーションへのリンクを返します。対象はホーム URI を持つ有効なクライアントです (`clientUri` よりも `initiateLoginUri` が優先されます)。各エントリは `{ clientId, clientName, homeUri, logoUri, isDefault }` で、ちょうど 1 つのアプリが既定としてマークされます (フラグが設定されたクライアント、またはホーム URI を持つ唯一のクライアント)。Cookie 認証が必要です。

### プロファイル (セルフサービス) {#profile-self-service}

```
GET   /api/auth/profile
PATCH /api/auth/profile
```

認証されたユーザーが、自分の機密性の低いプロファイルフィールド (`firstName`、`lastName`、`companyName`、`phone`、`locale`) を読み取り、更新します。null のフィールドは変更されません。メールアドレス、パスワード、ロール、有効状態、組織はここでは編集**できません**。どちらもプロファイル `{ email, emailConfirmed, firstName, lastName, companyName, phone, locale }` を返します。

### セッション一覧 (セルフサービス) {#sessions-self-service}

```
GET    /api/auth/sessions
DELETE /api/auth/sessions/{sessionId}
POST   /api/auth/sessions/revoke-others
```

認証されたユーザー自身の SSO セッションを一覧表示し、終了します。これにはサーバー側セッションが必要で、これはオプトインです。`AddAuthagonal` の後に `AddAuthagonalServerSideSessions(configuration)` を呼び出す (Azure Table Storage を使い、`Storage:ConnectionString` または `Storage:TableServiceUri` を読み取ります) か、独自の `ITicketStore` と `IUserSessionRegistry` を登録してください。レジストリがない場合、`GET` は空のリストを、`revoke-others` は `{ "revoked": 0 }` を、`DELETE` は `404 not_supported` を返します。`DELETE` と `POST` のルートには同一オリジンのリクエストが必要です。

`GET` は、最近のアクティビティが新しい順にセッションを返します。

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

`DELETE` は 1 つのセッションを終了して `{ "revoked": 1 }` を返すか、`404 session_not_found` を返します。`POST /revoke-others` は呼び出し元のセッション以外のすべてのセッションを終了し、`{ "revoked": <count> }` を返します。どちらも、終了した各セッションのリライングパーティーに通知し (バックチャネルとフロントチャネルのログアウト)、そのセッションに結び付いたグラントを失効させるため、そのデバイスが持つリフレッシュトークンは機能しなくなります。レジストリが登録されている場合、ログイン UI のアカウントページにこの一覧が表示されます。

### SSO チェック {#sso-check}

```
GET /api/auth/sso-check?email=user@acme.com
```

メールアドレスのドメインで SSO が必須かどうかを確認します。

```json
{
  "ssoRequired": true,
  "providerType": "saml",
  "connectionId": "acme-azure",
  "redirectUrl": "/saml/acme-azure/login"
}
```

SSO が必須でない場合:

```json
{
  "ssoRequired": false
}
```

### パスワードポリシー {#password-policy}

```
GET /api/auth/password-policy
```

サーバーのパスワード要件 (設定の `PasswordPolicy` で構成) を返します。

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

既定のログイン UI は、パスワードのリセットページでこのエンドポイントを取得し、要件を動的に表示します。

## 既定のパスワード要件 {#default-password-requirements}

既定の構成では、パスワードは次のすべてを満たす必要があります。

- 8 文字以上
- 大文字を 1 文字以上
- 小文字を 1 文字以上
- 数字を 1 文字以上
- 英数字以外の文字を 1 文字以上
- 異なる文字を 2 種類以上

これらは `PasswordPolicy` 構成セクションでカスタマイズできます。[構成](configuration) を参照してください。

## MFA のエンドポイント {#mfa-endpoints}

### MFA の検証 {#mfa-verify}

```
POST /api/auth/mfa/verify
Content-Type: application/json

{
  "challengeId": "a1b2c3...",
  "method": "totp",
  "code": "123456"
}
```

MFA チャレンジを検証します。成功すると、認証 Cookie を設定してユーザー情報を返します。

**方式:**

| `method` | 必須フィールド | 説明 |
|---|---|---|
| `totp` | `code` (6 桁) | 認証アプリが生成する時間ベースのワンタイムパスワード |
| `webauthn` | `assertion` (JSON 文字列) | `navigator.credentials.get()` からの WebAuthn アサーションレスポンス |
| `recovery` | `code` (`XXXX-XXXX`) | 1 回限りのリカバリーコード (使用時に消費されます) |

**再試行の動作:** 間違ったコードでチャレンジが無駄になることは**ありません**。コードが先に検証され、チャレンジは成功した場合にのみ消費されるため、ユーザーは数字を打ち間違えても同じ `challengeId` で再試行できます (`401 invalid_code` / `assertion_failed`)。各チャレンジは**失敗を 5 回まで**許容します。5 回目の失敗でチャレンジは消費され、`401 too_many_attempts` が返されて、新たなログインが必要になります (これにより、TOTP の総当たりはチャレンジあたり 5 回の推測に抑えられます)。チャレンジには有効期限もあります (既定は 5 分、`Auth:MfaChallengeExpiryMinutes`)。期限切れ、不明、または消費済みの `challengeId` は `invalid_challenge` を返します。TOTP コードにはさらにリプレイ対策があり、既に使用された時間ステップのコードは拒否されます。

### MFA の状態 {#mfa-status}

```
GET /api/auth/mfa/status
```

ユーザーが登録済みの MFA 方式を返します。Cookie 認証または `X-MFA-Setup-Token` ヘッダーが必要です。

```json
{
  "enabled": true,
  "offered": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

すべてのクライアントの `MfaPolicy` が `Disabled` である (テナントで MFA がオフになっている) 場合、`offered` は `false` になり、セットアップ UI は自身を非表示にできます。リカバリーコードのエントリには、さらに `isConsumed` が含まれます。

### TOTP のセットアップ {#totp-setup}

```
POST /api/auth/mfa/totp/setup
→ { "setupToken": "...", "qrCodeDataUri": "data:image/png;base64,...", "manualKey": "BASE32..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
→ { "success": true }
```

### WebAuthn / パスキーのセットアップ {#webauthn--passkey-setup}

```
POST /api/auth/mfa/webauthn/setup
→ { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
→ { "success": true, "credentialId": "..." }
```

パスキーの登録には、**先に確認済みの TOTP 資格情報**が必要です (`400 totp_required_first`)。パスキーは、持ち運び可能な基本の要素の上に重ねるデバイスごとの利便機能なので、アカウントがパスキーだけになってデバイスに縛られることは決してありません。メールアドレスのドメインが SSO にルーティングされるユーザーは、ローカルのパスキーを登録できません (`400 sso_managed`)。テナントの IdP を迂回することになるからです。**いずれかの**アカウント (登録しようとしているユーザー自身のアカウントを含む) に既に登録されている資格情報 ID は、`409 credential_already_registered` で拒否されます。重複があると、その資格情報の署名カウンターがリセットされ、1 つの検索エントリを 2 つの行が共有することになるからです。

### リカバリーコード {#recovery-codes}

```
POST /api/auth/mfa/recovery/generate
→ { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

1 回限りのリカバリーコードを 10 個生成します。少なくとも 1 つの主要な方式 (TOTP または WebAuthn) が登録されている必要があります。再生成すると、既存のリカバリーコードはすべて置き換えられます。

### MFA 資格情報の削除 {#remove-mfa-credential}

```
DELETE /api/auth/mfa/credentials/{credentialId}
→ { "success": true }
```

特定の MFA 資格情報を削除します。最後の主要な方式が削除されると、そのユーザーの MFA は無効になります。実際の Cookie セッションが必要で、セットアップトークンは `403 session_required` で拒否されます (セットアップトークンは第1要素を追加するためだけに存在し、MFA を弱めるためのものではありません)。

### パスワードレスのパスキーログイン {#passwordless-passkey-login}

```
POST /api/auth/mfa/passwordless/begin
→ { "challengeId": "...", "options": { /* PublicKeyCredentialRequestOptions */ } }

POST /api/auth/mfa/passwordless/complete
{ "challengeId": "...", "assertion": "..." }
→ { "userId": "...", "email": "...", "name": "..." }
```

事前のユーザーコンテキストなしで行う、検出可能な資格情報 (常駐型のパスキー) によるログインです。`begin` は空の `allowCredentials` リストを含むアサーションのチャレンジを発行し、`complete` は選ばれたパスキー**から**ユーザーを特定し、アサーションを検証して、そのユーザーをサインインさせます (セッションには MFA のマーカーが付きます。パスキーはフィッシング耐性のある強力な認証だからです)。セレモニーの前にユーザーが特定されていないため、WebAuthn §7.2 のステップ 6 により、ここでは認証器のユーザーハンドルが必須になります。ユーザーハンドルのないアサーションは `401 user_handle_required` で拒否され、資格情報の所有者以外のアカウントを示すものは `401 credential_not_found` で拒否されます。特定されたユーザーのメールアドレスのドメインが SSO にルーティングされる場合、ログインは `409 sso_required` と `redirectUrl` で拒否されるため、ローカルのパスキーで強制された IdP を回避することはできません。

## デバイス認可 (RFC 8628) {#device-authorization-rfc-8628}

### デバイスコードの要求 {#request-device-code}

```
POST /connect/deviceauthorization
Content-Type: application/x-www-form-urlencoded

client_id=my-cli&scope=openid+profile
```

デバイスコード、ユーザーコード、検証 URI を返します。

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

`expires_in` はクライアントの `DeviceCodeLifetimeSeconds` (既定は 300) から取得されます。デバイスは `verification_uri` と `user_code` をユーザーに表示し、`device_code` を使ってトークンエンドポイントをポーリングします。ポーリングの間隔は `interval` 秒以上空ける必要があり、それより短いとトークンエンドポイントは `slow_down` を返します (RFC 8628 §3.5)。ユーザーがまだ承認していない間、トークンエンドポイントは `authorization_pending` を返します。ユーザーは検証 URI にアクセスしてログインし、ユーザーコードを入力して承認します。

### 承認前にリクエストを表示する {#show-the-request-before-approving}

```
GET /api/auth/device/info?user_code=ABCD-EFGH
```

Cookie 認証が必要です。そのコードによって何が付与されるかを説明するもので、承認画面はユーザーが承認する前に、どのアプリケーションが要求しているかを表示できます (攻撃者が開始したデバイスフローが、内容のわからないプロンプトで承認されることは、RFC 8628 §5.4 が警告する不正な同意のパターンです)。

```json
{
  "clientId": "my-cli",
  "clientName": "My CLI",
  "clientUri": "https://example.com",
  "logoUri": null,
  "scopes": ["openid", "profile"]
}
```

`scopes` は、ロールで制限されたスコープに対するユーザーごとのロールによる制限を適用した後に、実際に付与されるものであり、生のリクエストではありません。エラーは `401 not_authenticated`、`400 user_code_required`、`400 invalid_user_code` (不明、消費済み、または期限切れ)、`400 expired` です。承認と同じレート制限のバケット (後述) を共有します。

### デバイスの承認 {#approve-device}

```
POST /api/auth/device/approve
Content-Type: application/x-www-form-urlencoded

user_code=ABCD-EFGH&scopes=openid+profile
```

Cookie 認証と同一オリジンのリクエストが必要です。`scopes` は省略可能 (スペース区切り) で、ユーザーに認められている範囲を狭めることはできますが、広げることは決してできません。省略すると、認められているものがすべて付与されます。現在のユーザーに対してデバイスコードを承認し、`200 { "approved": true }` を返します。その後、デバイスはグラントタイプ `urn:ietf:params:oauth:grant-type:device_code` を使って、トークンエンドポイントでデバイスコードをトークンに交換できます。

送信されたコードは、検索の前に RFC 8628 §6.1 に従って正規化されます。大文字に変換され、31 文字のコード用アルファベットに含まれない文字はすべて取り除かれます。`ABCD-EFGH`、`abcd-efgh`、`ABCDEFGH`、`ABCD EFGH`、そしてコピー＆ペーストでハイフンがエムダッシュに変わってしまったものは、すべて同じコードです。ハイフンは、コードを読み上げやすくするためだけに存在します。

| ステータス | `error` | 意味 |
|---|---|---|
| 400 | `user_code_required`, `invalid_user_code`, `expired` | `info` と同じ |
| 400 | `invalid_scope` | `scopes` が指定されたが、そのいずれもユーザーに認められたスコープではない |
| 403 | `access_denied` | 要求されたスコープのいずれもユーザーに認められていない (`Scope.AllowedRoles`) |
| 403 | `mfa_enrolment_required` | クライアントの実効 MFA ポリシーが `Required` で、ユーザーが第2要素を持っていない。登録してから再度承認する |

入力はサブジェクトごとに 1 分あたり 10 回の試行にレート制限されており (RFC 8628 §5.1)、この制限は `info`、`approve`、`deny` で共有されます。11 回目は `429` を返します。既定のインプロセスのレートリミッターでは、このカウンターはノードごとであるため、複数のレプリカを持つデプロイメントでは、エッジでもこの制限を適用する必要があります。

### デバイスの拒否 {#deny-device}

```
POST /api/auth/device/deny
Content-Type: application/x-www-form-urlencoded

user_code=ABCD-EFGH
```

Cookie 認証と同一オリジンのリクエストが必要です。ユーザーの拒否を記録し、`200 { "success": true }` を返します。デバイスによる次のトークンエンドポイントへのポーリングは、コードの有効期限が切れるまで、`authorization_pending` ではなく `access_denied` を受け取ります (RFC 8628 §3.5)。エラーとレート制限のバケットは `info` と同じです。

## トークンイントロスペクション (RFC 7662) {#token-introspection-rfc-7662}

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

token=eyJhbGci...
```

または、フォームエンコードの資格情報を使う場合:

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded

token=eyJhbGci...&client_id=my-app&client_secret=secret
```

トークンのメタデータを返します。

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

無効または不正なトークンは `{ "active": false }` を返します。JWT のアクセストークンと不透明なリフレッシュトークンの両方をサポートします。

## 同意のエンドポイント {#consent-endpoints}

### 同意の情報 {#consent-info}

```
GET /consent/info?client_id=my-app
```

Cookie 認証が必要です。同意ページ用に、クライアントの詳細と要求されたスコープを返します。スコープはクエリ文字列から取得されるのではなく、認可エンドポイントがこのユーザーとクライアントについて (ロールによる権限のフィルタリングの後に) 記録した提示内容です。そのため、細工したリンクで、信頼されたクライアントの名前の下に呼び出し側が選んだ権限の一覧を表示させることはできません。

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

`scopeDetails` は `scopes` と並行しており (同じ順序で、スコープごとに 1 エントリ)、`scopes` だけを読み取るログインアプリもそのまま動作します。各エントリには、そのスコープに登録された表示用の情報が含まれます。

| フィールド | 意味 |
|---|---|
| `name` | スコープ名。`scopes` と同じです。 |
| `displayName` | 登録された表示名。スコープが登録されていない場合は `null`。 |
| `description` | 登録された説明、または `null`。 |
| `emphasize` | スコープが重要なものとして登録されている場合は `true` で、画面はそのスコープに注意を引くことができます。既定は `false`。 |
| `required` | スコープが拒否できないものとして登録されている場合は `true` で、画面はチェック済みでロックされた状態で表示します。既定は `false`。 |
| `group` | スコープをまとめる見出し、または単独で表示する場合は `null`。 |

登録されていないスコープでは、`displayName`、`description`、`group` が `null`、2 つのフラグが `false` になり、ログインアプリは独自の文言にフォールバックします。文言の登録については [スコープ](scopes) を参照してください。

エラー:

| ステータス | 本文 | 発生する場合 |
|---|---|---|
| `401` | なし | サインインしているユーザーがいない。 |
| `404` | `{ "error": "client_not_found" }` | 不明な `client_id`。 |
| `400` | `{ "error": "no_pending_consent_request" }` | このユーザーとクライアントについて有効な同意の提示がない (記録されていないか、期限切れ)。 |

### 同意の送信 {#submit-consent}

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

ユーザーの同意の判断を記録し (Cookie 認証が必要)、SPA が遷移する先として `{ "redirect": "..." }` を返します。許可した場合、付与されたスコープが保存され (クライアントの `AllowedScopes` でフィルタリングされるため、改ざんされた本文でクライアントが要求できなかったスコープを記録することはできません)、リダイレクト先は認可フローに戻ります。`"decision": "deny"` の場合、リダイレクト先は `access_denied` エラーを付けたクライアントの `redirect_uri` になります。

### グラントの一覧 {#list-grants}

```
GET /consent/grants
```

ユーザーが認可したすべてのアプリケーションを返します。

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

### グラントの取り消し {#revoke-grant}

```
DELETE /consent/grants/{clientId}
```

特定のアプリケーションへの同意を取り消します。ユーザーは次回のログイン時に、再度同意を求められます。

## ディスカバリーと署名鍵 (JWKS) {#discovery-and-signing-keys-jwks}

どちらも公開されており、匿名でアクセスできます。リソースサーバーは、このサーバーが発行したトークンを検証するためにこれらを使います。

```
GET /.well-known/openid-configuration
GET /.well-known/oauth-authorization-server
GET /.well-known/openid-configuration/jwks
```

- 2 つのメタデータのパスは同じディスカバリードキュメントを返します。その `jwks_uri` は `{issuer}/.well-known/openid-configuration/jwks` です。
- JWKS には、期限切れでないすべての署名鍵 (`kty`、`use`、`kid`、`alg`、および EC 鍵の場合は `crv`/`x`/`y`) が列挙されます。ローテーションでは次の鍵が数日前に公開されるため、キャッシュされたコピーに、トークンの署名に使われた鍵が欠けていることはありません。
- レスポンスには `Cache-Control: public, max-age=3600` が付きます。
- 署名は ES256 のみです。ディスカバリーは `id_token_signing_alg_values_supported: ["ES256"]` を公開します。
- 発行者は `ITenantContext` から、鍵は `IKeyManager` から取得されるため、テナントごとの鍵マネージャーを持つマルチテナントのホストは、テナントごとの鍵を提供します。

## 認可エンドポイントの動作 {#authorization-endpoint-behaviour}

`GET /connect/authorize` は認可コードフローの入口です。これに対してクライアントやログイン UI を構築する人にとって重要な動作が 2 つあります。

### レスポンスに含まれる発行者 (RFC 9207) {#issuer-in-the-response-rfc-9207}

クライアントの `redirect_uri` に戻るすべてのリダイレクトには、発行者を保持する `iss` クエリパラメーターが付きます。成功時 (`code` と `state` と一緒に) もエラー時 (`error`、`error_description`、`state` と一緒に) も同様です。ユーザーが `/consent` で同意を拒否したときのエラーのリダイレクトにも同じことが当てはまります。ディスカバリードキュメントは、これを `authorization_response_iss_parameter_supported: true` で公開します。複数の認可サーバーとやり取りするクライアントは、`iss` をフローを開始した発行者と比較する必要があり、これによって混同攻撃 (mix-up attack) を防げます。このパラメーターを無視するクライアントは影響を受けません。信頼できる `redirect_uri` が判明する前に発生したエラー (不明な `client_id`、登録されていないリダイレクト URI) は、リダイレクトではなく JSON のエラー本文として返されるため、それらには `iss` はありません。

### `prompt` と `max_age` {#prompt-and-max_age}

| リクエスト | 動作 |
|---|---|
| `prompt=login` | 既存のセッションはサインアウトされ、ユーザーは再度認証するために `/login` に送られます。新しいサインインでループのように再認証を強制されないよう、`prompt` は `returnUrl` から取り除かれます。[プッシュ型リクエスト](par) では、prompt は保存されたペイロードに含まれており、セッションの `auth_time` がリクエストがプッシュされた時点以降であることを要求することで、ループを断ち切ります |
| `prompt=select_account` | `prompt=login` として扱われます。サーバーはブラウザーごとに 1 つのセッションを保持するため、アカウントの選択はログイン画面で行います |
| `prompt=create` | 認証されていないユーザーは、サインインのフォームではなく `/login/register` に送られます。既存のセッションがあれば、そのまま進みます |
| `prompt=consent` | 保存されたグラントでリクエストを満たせる場合でも、同意画面が表示されます。リクエストごとに 1 回です (満たされたことを示すマーカーは 1 回限りです) |
| `prompt=none` | UI は一切表示されません。サーバーは、`login_required` (セッションなし)、`interaction_required` (MFA のステップアップまたは登録が必要)、または `consent_required` (同意が必要) を付けたリダイレクトで応答します |
| `max_age=N` | セッションの `auth_time` が `N` 秒より古いか存在しない場合、`prompt=login` とまったく同じようにユーザーは再認証されます。`max_age=0` は常に再認証します |

`prompt=none` と他の値の組み合わせは `invalid_request` で拒否されます。`none`、`login`、`consent`、`select_account`、`create` 以外の値も同様です。組み込み可能な `Authagonal.Protocol` のホストも `prompt=login`、`select_account`、`none`、`max_age` を同じように扱いますが、同意のインターフェイスを持たないため、`prompt=consent` には `consent_required` で応答します。

## 独自のログイン UI の構築 {#building-a-custom-login-ui}

既定の SPA (`login-app/`) は、この API の実装の 1 つです。独自のものを構築するには、次のようにします。

1. UI を `/login`、`/forgot-password`、`/reset-password`、`/consent`、`/device` の各パスで提供します
2. 認可エンドポイントは、認証されていないユーザーを `/login?returnUrl={encoded-authorize-url}` にリダイレクトします
3. ログインに成功したら (Cookie が設定されたら)、ユーザーを `returnUrl` にリダイレクトします
4. パスワードのリセットリンクは `{Issuer}/login/reset-password?p={token}` を使います (ログイン SPA は `/login` の下にマウントされています)

UI は API と**同一オリジン**から提供する必要があります。理由は次のとおりです。
- Cookie 認証は `SameSite=Lax` + `HttpOnly` を使います
- 認可エンドポイントは (相対パスの) `/login` にリダイレクトします
- リセットリンクは `{Issuer}/login/reset-password` を使います
