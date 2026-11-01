---
layout: default
title: 管理 API
locale: ja
---

# 管理 API

管理エンドポイントには、`authagonal-admin` スコープ (`AdminApi:Scope` で変更可能) を持つ JWT アクセストークンが必要です。

すべてのエンドポイントは `/api/v1/` 配下にあります。

## 最初の管理トークンのブートストラップ {#bootstrapping-the-first-admin-token}

`/api/v1/*` のすべてのエンドポイントは管理スコープを持つベアラートークンを要求しますが、管理 API 自体 (および [動的クライアント登録](client-registration)) は**そのスコープを持つクライアントの作成や更新をすべて拒否します** (`403 forbidden_scope`)。そのため、実行時に作成されたクライアントが管理者権限に昇格することは決してありません。管理トークンを発行する唯一の方法は、**構成で初期投入されたクライアント**を使うことです。構成の `Clients:` セクションのエントリは起動時に `ClientSeedService` によってアップサートされます。構成は信頼されており、禁止スコープのチェックは実行時の API にのみ適用されます。

`appsettings.json` (または同等の環境変数 / シークレットストア) で、管理スコープを持つ `client_credentials` クライアントを初期投入します。

```json
{
  "Clients": [
    {
      "Id": "admin-cli",
      "Name": "Admin CLI",
      "ClientSecret": "a-long-random-secret",
      "GrantTypes": ["client_credentials"],
      "Scopes": ["authagonal-admin"]
    }
  ]
}
```

(`ClientSecret` は起動時にハッシュ化されます。事前にハッシュ化した値だけを構成に置きたい場合は、代わりに `SecretHashes` を指定してください。`ClientId`/`ClientName`/`AllowedGrantTypes`/`AllowedScopes` は、`Id`/`Name`/`GrantTypes`/`Scopes` の別名として受け付けられます。)

次に、標準のトークンエンドポイントで資格情報をトークンに交換します。

```bash
curl -X POST https://auth.example.com/connect/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=admin-cli" \
  -d "client_secret=a-long-random-secret" \
  -d "scope=authagonal-admin"
```

```json
{ "access_token": "eyJhbGci...", "token_type": "Bearer", "expires_in": 1800, "scope": "authagonal-admin" }
```

`client_credentials` グラントは、要求されたスコープをクライアントの `AllowedScopes` と照合します。初期投入されたクライアントは `authagonal-admin` を持っているので、トークンが発行されます。管理 API の呼び出しのたびに、それを `Authorization: Bearer {access_token}` として使います。

```bash
curl https://auth.example.com/api/v1/clients -H "Authorization: Bearer eyJhbGci..."
```

初期投入したクライアントのシークレットは、デプロイメントのシークレットストアに保管してください。ローテーションは構成の変更と再起動で行います。

## ユーザー {#users}

### ユーザーの取得 {#get-user}

```
GET /api/v1/profile/{userId}
```

プロファイルに加えて、サポートコンソールがサインインの問題を診断するのに必要な情報を返します。`emailConfirmed`、`isActive`、`lockoutEnd`、`accessFailedCount`、`roles`、リンクされた `externalLogins`、そして `hasPassword` (有無のみで、ハッシュは決して返しません) です。最後の項目は、「パスワードを忘れた」のか「パスワードを持ったことがなく、SSO でサインインしている」のかを分けるもので、この 2 つに対する助言は正反対になります。

外部ログインのリンクを含むユーザーの詳細を返します。

### ユーザーの存在確認 {#user-exists}

```
GET /api/v1/profile/{userId}/exists
```

ユーザーが存在すれば `204`、存在しなければ `404` を返します (本文なしの軽量な存在確認)。

### ユーザーの登録 {#register-user}

```
POST /api/v1/profile/
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

ユーザーを作成し、確認メールを送信します。そのメールアドレスが既に使われている場合は `409 user_exists` を返します。

管理者専用のオプションのフィールド: `userId` (呼び出し側が指定する ID。衝突した場合は `409 user_id_in_use`)、`emailConfirmed` (確認済みの状態でユーザーを作成し、確認メールを省略する)、`companyName`、`organizationId`、`phone`、`locale`、そして `customAttributes` (ユーザーに保存され、プロビジョニング先に転送される文字列のマップ)。

`skipProvisioning: true` は、プロビジョニングを実行せずに ID を作成します。これは、それ自体がプロビジョニング先であり、既にこのユーザーのセットアップを途中まで進めているファーストパーティーのアプリのためのものです。そのアプリがここを呼び出すのは ID を発行するためであり、作成途中のユーザーについてコールバックを受けるためではありません。これがないと、そのアプリは作りかけのユーザーについて自分自身への Try を受け取ることになり、その Try には往復の過程で残った属性しか含まれません。さらに、そこから回復できたとしても、ユーザーを 2 回プロビジョニングする結果になります。

### ユーザーの更新 {#update-user}

```
PUT /api/v1/profile/
Content-Type: application/json

{
  "userId": "user-id",
  "firstName": "Jane",
  "lastName": "Smith",
  "organizationId": "new-org-id"
}
```

`userId` は必須で、他のフィールドはすべてオプションです。指定したフィールドだけが更新されます。

`isActive` はアカウントを無効化または再有効化します。`emailConfirmed` (`emailVerified` としても受け付けます) は、確認メールを送信せずにアドレスを確認済みにします。所有が別の方法で確認できている場合に使います。

`organizationId` の変更や無効化を行うと、次の処理が行われます。
- SecurityStamp のローテーション (30 分以内にすべての Cookie セッションを無効化)
- すべてのリフレッシュトークンの失効

次回のログインまで効果が出ないブロックはブロックとは言えません。だからこそ、無効化は有効期限切れを待たずに失効させます。

### ユーザーの検索 {#search-users}

```
GET /api/v1/profile/search?q=jane&maxResults=20
```

メールアドレスと名前のインデックスに対する前方一致検索です。`{ "users": [ ... ] }` を返します。

### メールアドレスによるユーザーの取得 {#get-user-by-email}

```
GET /api/v1/profile/by-email?email=jane@example.com
```

完全一致の検索です。前方一致で複数の人を返すことがある検索とは異なります。「このアドレス」を「このアカウント」に解決したい呼び出し側が求めているのは、1 件の答えか、該当なしかのどちらかです。そのようなユーザーがいなければ `404` です。

### ユーザーの一覧表示 {#list-users}

```
GET /api/v1/profile?organizationId=&count=100&continuationToken=
```

カーソルでページングされるディレクトリの一覧です。次のページを取得するには返された `continuationToken` を渡し、それが null になったら終了します。オフセットではなくカーソルを使うのは、ストアがトークン単位でページングするためです。オフセットでは、ページのたびに最初から再スキャンすることになります。

### 存在するユーザーの判定 {#which-users-exist}

```
POST /api/v1/profile/exists
Content-Type: application/json

{ "userIds": [ "a", "b", "c" ] }
```

存在するものの部分集合を返します。リクエストが ID 500 件の上限を超えた場合は `truncated: true` も返すので、呼び出し側は 600 件のうち 500 件分だけを黙って回答されるのではなく、バッチが切り詰められたことを知らされます。ID の集合を別のシステムのものと突き合わせる用途に使います。

### 複数ユーザーの MFA 状態 {#mfa-status-for-many-users}

```
POST /api/v1/profile/mfa-status
Content-Type: application/json

{ "userIds": [ "a", "b", "c" ] }
```

`{ "statuses": { "a": true, "b": false }, "truncated": false }` を返します。`true` は、そのユーザーが少なくとも 1 つの MFA 資格情報を持っていることを意味します。上限は ID 500 件で、`truncated: true` はリクエストが切り詰められたことを示します。ディレクトリの画面に「MFA 使用中」のバッジを表示する用途に使います。

### パスワードの設定 {#set-a-password}

```
POST /api/v1/profile/{userId}/set-password
Content-Type: application/json

{ "password": "N3w!Password" }
```

アドレスがもはや本人に届かないアカウントからロックアウトされた人のためのサポート手段です。パスワードポリシーが適用されます。すべてのリフレッシュトークンを失効させ、セキュリティスタンプをローテーションします。古いセッションが動き続けるパスワード変更では、その人として行動できる者は変わっていないからです。

### ユーザーのロック解除 {#unlock-a-user}

```
POST /api/v1/profile/{userId}/unlock
```

ロックアウトとその失敗回数をクリアし、ロックアウトがたまたま期限切れになるのを待たずに、今すぐ再びアクセスできるようにします。

### ユーザーの削除 {#delete-user}

```
DELETE /api/v1/profile/{userId}
```

ユーザーを削除し、すべてのグラントを失効させ、すべての下流アプリからプロビジョニングを解除します (ベストエフォート)。

### メールアドレスの確認 {#confirm-email}

```
POST /api/v1/profile/confirm-email?token={token}
```

### 確認メールの送信 {#send-verification-email}

```
POST /api/v1/profile/{userId}/send-verification-email
```

### 外部 ID のリンク {#link-external-identity}

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### 外部 ID のリンク解除 {#unlink-external-identity}

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

### ユーザーの組織を一覧表示 {#list-a-users-organizations}

```
GET /api/v1/profile/{userId}/organizations
```

`{ "items": [ { "organizationId", "slug", "name", "status", "roles" } ] }` を返します。ユーザーがメンバーシップを持つすべての組織が、ステータスにかかわらず含まれます。組織がすでに存在しないメンバーシップは除外されます。

### ユーザーを招待 {#invite-user}

```
POST /api/v1/profile/invite
Content-Type: application/json

{
  "email": "ada@acme.com",
  "firstName": "Ada",
  "lastName": "Lovelace",
  "locale": "en",
  "organizationId": "org_0123456789abcdef0123456789abcdef",
  "organizationRoles": ["Auditor"]
}
```

必須なのは `email` だけです。アカウントを作成し(未確認で、使えるパスワードはなし)、`/accept-invite` への 1 回限りで 7 日間有効なリンクをメールで送ります。`organizationId`(組織の ID。スラッグではありません)を指定すると、その組織への招待となり、`organizationRoles` を持つ `invited` メンバーシップが書き込まれ、被招待者がパスワードを設定すると `active` になります。メールは、リクエストの `Accept-Language` のうちライブラリが対応する最初のもの、なければ `locale`、それもなければ英語で作成されます。[組織](organizations#inviting-people)を参照してください。

`200` で `{ "userId": "...", "email": "ada@acme.com" }`(アドレスは小文字)を返します。失敗は `{ "error", "error_description" }` です。

| ステータス | `error` | 条件 |
|---|---|---|
| 400 | `email_required` | `email` がない、または空白 |
| 400 | `invalid_email` | `email` が使えるアドレスではない |
| 400 | `invalid_role` | 組織ロールが `tenant:` または `platform:` で始まる |
| 404 | `organization_not_found` | `organizationId` がどの組織も指さない |
| 409 | `user_already_exists` | そのアドレスにはすでにアカウントがある |

### 招待を再送 {#resend-invite}

```
POST /api/v1/profile/{userId}/resend-invite
```

新しいリンクを生成して送信します。`200` で `{ "sent": true }` を返します。不明なユーザーは `404 user_not_found`、アドレスが確認済みの場合は `400 already_accepted`(パスワードリセットを使用してください)、メールを送信できない場合は `502 invite_email_failed` です。

## MFA の管理 {#mfa-management}

### MFA 状態の取得 {#get-mfa-status}

```
GET /api/v1/profile/{userId}/mfa
```

ユーザーの MFA の状態と登録済みの方式を返します。

### すべての MFA のリセット {#reset-all-mfa}

```
DELETE /api/v1/profile/{userId}/mfa
```

すべての MFA 資格情報を削除し、`MfaEnabled=false` を設定します。必要であれば、ユーザーは再登録する必要があります。

### 特定の MFA 資格情報の削除 {#remove-specific-mfa-credential}

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

特定の MFA 資格情報 (例: 紛失した認証アプリ) を削除します。最後の主要な方式が削除されると、MFA は無効になります。

## SSO プロバイダー {#sso-providers}

### SAML プロバイダー {#saml-providers}

```
POST   /api/v1/saml/connections                    # Create
GET    /api/v1/saml/connections/{connectionId}     # Get one
PUT    /api/v1/saml/connections/{connectionId}     # Update (partial: only supplied fields change)
DELETE /api/v1/saml/connections/{connectionId}     # Delete
```

作成には、`connectionName`、`entityId`、そして `metadataLocation` (メタデータの URL) と `metadataXml` (貼り付けた IdP のメタデータ。メタデータの URL を持たない IdP 向けで、保存時に解析による検証と圧縮が行われます) の**どちらか一方だけ**が必要です。オプション: `nameIdFormat` (省略すると既定の emailAddress、`"none"` を指定すると NameIDPolicy を省略 (ADFS に推奨)、または NameID 形式の URN)、`signAuthnRequests`、`iconUrl`、`allowedDomains`、`disableJitProvisioning`、`organizationId`。すべての接続にはサーバーが生成した SP キーペアが付与されますが、API から返されることは決してありません。詳細は [SAML](saml) を参照してください。

`organizationId` は接続を 1 つの [組織](organizations) にスコープします。その接続は、その組織が選択されている場合にのみ提示され、その `allowedDomains` はその組織の中でのみ照合され (テナント全体の SSO ドメインインデックスには書き込まれ*ません*)、その接続でサインインした人は全員その組織のメンバーになります。省略または `null` の場合は、テナントレベルの接続になります。存在しない組織を指定すると `400 unknown_organization` です。更新時には、`null` (フィールドなし) ならスコープは変更されず、`""` なら接続はテナントレベルに戻ります。どちらの方向に変更しても、それに応じてドメインインデックスが書き換えられます。[組織スコープの接続](self-service-sso#organisation-scoped-connections) を参照してください。

### OIDC プロバイダー {#oidc-providers}

```
POST   /api/v1/oidc/connections                    # Create
GET    /api/v1/oidc/connections/{connectionId}     # Get one
DELETE /api/v1/oidc/connections/{connectionId}     # Delete
```

作成には `connectionName`、`metadataLocation`、`clientId`、`clientSecret`、`redirectUrl` が必要です。オプション: `iconUrl`、`allowedDomains`、`passthroughParams`、`organizationId` (前述の SAML 接続の場合と同じ意味)。クライアントシークレットは保存時に保護され、返されることはありません。[OIDC フェデレーション](oidc-federation) を参照してください。

### SSO ドメイン {#sso-domains}

```
GET    /api/v1/sso/domains                 # List all
```

## クライアント {#clients}

実行時に OAuth クライアントを管理します。すべてのルートで `IdentityAdmin` ポリシー (管理スコープ) が必要です。

```
GET    /api/v1/clients              # List all clients
GET    /api/v1/clients/{clientId}   # Get one client
POST   /api/v1/clients              # Create a client
PUT    /api/v1/clients/{clientId}   # Update a client
DELETE /api/v1/clients/{clientId}   # Delete a client
```

### クライアントの作成 / 更新 {#create--update-client}

```
POST /api/v1/clients
Content-Type: application/json

{
  "clientId": "my-app",
  "clientName": "My Application",
  "allowedGrantTypes": ["authorization_code"],
  "redirectUris": ["https://app.example.com/callback"],
  "allowedScopes": ["openid", "profile", "email"]
}
```

クライアントが既に存在する場合、`POST` は `409` を返します。`PUT` は既存のクライアントを更新します (見つからない場合は `404`)。更新時には、新たに追加されたスコープだけが権限昇格のチェックを受けます。

注意事項:

- **シークレットのハッシュは決して返されません。** `clientSecretHashes` はすべてのレスポンス (一覧、取得、作成、更新) から取り除かれます。更新時に `clientSecretHashes` を省略すると、保存済みのシークレットは保持されます。新しいハッシュを指定すると、シークレットがローテーションされます。
- **管理スコープはクライアントに付与できません。** `allowedScopes` で `AdminApi:Scope` (既定値 `authagonal-admin`) を要求すると `403 forbidden_scope` が返されます。管理スコープを持てるクライアントはありません。そうでなければ、`client_credentials` クライアントが管理トークンを無期限に発行できてしまいます。
- 呼び出し側が付与を許可されていないスコープを追加すると、`403` が返されます。

## スコープ {#scopes}

実行時に独自の OAuth スコープを管理します。スコープのモデル全体については [OAuth スコープ](scopes) を参照してください。

```
GET    /api/v1/scopes           # List all scopes
GET    /api/v1/scopes/{name}    # Get one scope
POST   /api/v1/scopes           # Create a scope
PUT    /api/v1/scopes/{name}    # Update a scope (only supplied fields change)
DELETE /api/v1/scopes/{name}    # Delete a scope
```

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing, read-only",
  "description": "View invoices and payment history",
  "userClaims": ["billing_plan"]
}
```

作成時は `201` (スコープが既に存在する場合は `409`)、取得/更新時はスコープの JSON、削除時は `204` を返します。

## プロビジョニングアプリ {#provisioning-apps}

実行時に下流のプロビジョニング先を管理します。すべてのルートで `IdentityAdmin` ポリシーが必要です。

```
GET    /api/v1/provisioning/apps               # List apps (also returns the configured limit)
POST   /api/v1/provisioning/apps               # Create an app
PUT    /api/v1/provisioning/apps/{appId}       # Update an app
DELETE /api/v1/provisioning/apps/{appId}       # Delete an app
POST   /api/v1/provisioning/apps/{appId}/test  # Send a test /try call to the app's callback
```

### プロビジョニングアプリの作成 / 更新 {#create--update-provisioning-app}

```
POST /api/v1/provisioning/apps
Content-Type: application/json

{
  "name": "Backend",
  "callbackUrl": "https://api.example.com/provisioning",
  "apiKey": "secret-api-key",
  "tryTimeoutSeconds": 30
}
```

- `name` と `callbackUrl` は必須です。`callbackUrl` は絶対 `http(s)` URL でなければなりません。
- `tryTimeoutSeconds` は 5～300 の範囲に収められます。
- **API キーは決して返されません。** レスポンスはキーそのものではなく `hasApiKey` (ブール値) を公開します。更新時に `apiKey` を省略すると変更されず、空文字列ならクリアされ、値を指定すると置き換えられます。
- 作成には、デプロイメントごとに構成可能なクォータ (`IProvisioningAppQuota`) が適用されます。超過すると `400 provisioning_app_limit` が返されます。一覧のレスポンスには現在の `limit` が含まれます。

### プロビジョニングアプリのテスト {#test-a-provisioning-app}

```
POST /api/v1/provisioning/apps/{appId}/test
```

サンプルのペイロードを付けた合成の `POST {callbackUrl}/try` を (API キーが設定されていればそれをベアラートークンとして) 送信し、`{ success, statusCode, body }` を返します。これにより、管理 UI から接続性を確認できます。

## ロール {#roles}

### ロールの一覧表示 {#list-roles}

```
GET /api/v1/roles
```

### ロールの取得 {#get-role}

```
GET /api/v1/roles/{roleId}
```

### ロールの作成 {#create-role}

```
POST /api/v1/roles
Content-Type: application/json

{
  "name": "admin",
  "description": "Administrator role"
}
```

### ロールの更新 {#update-role}

```
PUT /api/v1/roles/{roleId}
Content-Type: application/json

{
  "name": "admin",
  "description": "Updated description"
}
```

### ロールの削除 {#delete-role}

```
DELETE /api/v1/roles/{roleId}
```

### ユーザーへのロールの割り当て {#assign-role-to-user}

```
POST /api/v1/roles/assign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

割り当てはロール ID ではなく**ロール名**で行います。ユーザーの更新後のロールのリストを返します。

### ユーザーからのロールの割り当て解除 {#unassign-role-from-user}

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

### ユーザーのロールの取得 {#get-users-roles}

```
GET /api/v1/roles/user/{userId}
```

### ロールを持つユーザー {#users-in-a-role}

```
GET /api/v1/roles/{roleName}/users?maxResults=200
```

上記の逆 (このロールを誰が持っているか) で、すべてのユーザーを読み取るのではなく、ロールのメンバーシップインデックスから回答します。`{ "roleName": "...", "members": [ { "userId", "email", "firstName", "lastName", "roles" } ] }` を返します。1 つのロールを一覧表示するコンソールは、ほぼ必ずそのメンバーが他に何を持っているかも表示したいので、各メンバーには完全なロールの集合が含まれます。

存在しないロールには、空のリストではなく `404 role_not_found` を返します。「誰も持っていない」と「ロール名の綴りを間違えている」は別の問題だからです。構成されたストアがロールのメンバーシップのインデックスを作成しない場合は、同じ理由で `501 not_supported` を返します。空のメンバーシップのリストは「誰もこれを管理していない」と読めてしまうからです。

インデックスが導入される前に書き込まれたアカウントは、再インデックスされるまでインデックスからは見えません (`IUserStore.ReindexUserAsync`。ユーザーのメンバーシップを、何も削除せずにアップサートします)。

## 組織 {#organizations}

組織、そのメンバー、およびメールドメインです(意味については[組織](organizations)を参照)。失敗は `{ "error": "<code>", "error_description": "..." }` で、ステータスは `400`(入力が不正)、`404`(見つからない)、`409`(競合)のいずれかです。

一覧は `?cursor={cursor}&limit={limit}` でページ分割されます。`limit` は 1..200 に制限され(既定は 50)、応答は `{ "items": [...], "nextCursor": "..." }` で、最後のページでは `nextCursor` が null になります。`nextCursor` はそのまま返してください。一覧が発行していないカーソルは `400 invalid_cursor` です。

組織は `{ "id", "slug", "name", "brandingJson", "metadata", "enabled", "requireMembershipForTokens", "allowAutoMembership", "createdAt", "updatedAt", "domains" }` です。

### 組織の一覧 {#list-organizations}

```
GET /api/v1/organizations?cursor={cursor}&limit={limit}
```

`200`。ID 順です。`400 invalid_cursor`。

### 組織の作成 {#create-organization}

```
POST /api/v1/organizations
Content-Type: application/json

{
  "slug": "acme",
  "name": "Acme Corp",
  "brandingJson": "{\"appName\":\"Acme\",\"primaryColor\":\"#1a56db\"}",
  "enabled": true,
  "requireMembershipForTokens": true,
  "allowAutoMembership": false,
  "metadata": { "plan": "enterprise" }
}
```

`slug` と `name` は必須で、それ以外は任意です。省略した場合は組織の既定値のままになります。`brandingJson` は JSON オブジェクトを含む文字列です。`Location` ヘッダーと組織を伴って `201` を返します。`id` は `org_` に 32 桁の 16 進数が続く形式です。

| ステータス | `error` | 条件 |
|---|---|---|
| 400 | `slug_required` | `slug` がない、または空白 |
| 400 | `slug_invalid` | `slug` が、小文字、数字、内部のハイフンによる 1〜64 文字ではない |
| 400 | `name_required` | `name` がない、または空白 |
| 400 | `invalid_branding_json` | `brandingJson` が JSON オブジェクトではない |
| 409 | `slug_taken` | 別の組織がその値をスラッグまたは ID として持っている(同時作成の場合も含む) |

### 組織の取得 {#get-organization}

```
GET /api/v1/organizations/{idOrSlug}
```

`200`。まず ID で、次にスラッグで検索されます。`404 organization_not_found`。

### 組織の更新 {#update-organization}

```
PUT /api/v1/organizations/{id}
Content-Type: application/json

{ "name": "Acme Corporation", "enabled": true }
```

すべてのフィールドは任意で、指定したものだけが変更されます。`name`、`brandingJson`、`enabled`、`requireMembershipForTokens`、`allowAutoMembership`、`metadata`(全体が置き換えられ、`{}` は `legacyOrganizationId` 以外のすべてのキーを削除します。このキーはバックフィルが記録し、置き換えの後も残ります)です。`slug` は、異なる場合に拒否するためだけに受け付けられます。`200` で組織を返します。`404 organization_not_found`、`400 slug_immutable`、`400 invalid_branding_json`。

### 組織の削除 {#delete-organization}

```
DELETE /api/v1/organizations/{id}
```

すべてのメンバーシップを削除してから、組織を削除します。`204`、`404 organization_not_found`。

### メンバーの一覧 {#list-members}

```
GET /api/v1/organizations/{id}/members?cursor={cursor}&limit={limit}
```

ユーザー ID 順の 1 ページで、`{ "organizationId", "userId", "email", "name", "status", "roles", "createdAt", "updatedAt" }` が並びます。`email` と `name` はユーザーのレコードから取得され、ユーザーがもう存在しない場合は null です。`404 organization_not_found`、`400 invalid_cursor`。

### メンバーの追加 {#add-member}

```
POST /api/v1/organizations/{id}/members
Content-Type: application/json

{ "userId": "user-id", "status": "active", "roles": ["Auditor"] }
```

既存のユーザーを `userId`(先に確認されます)または `email` で指定します。`status` は `invited`、`active`(既定)、`suspended` のいずれかで、`roles` は任意です。`Location` ヘッダーとメンバーシップを伴って `201` を返します。

| ステータス | `error` | 条件 |
|---|---|---|
| 404 | `organization_not_found` | その組織がない |
| 404 | `user_not_found` | `userId` も `email` もユーザーを指さない |
| 409 | `membership_exists` | ユーザーはすでにメンバーである |
| 400 | `invalid_role` | ロールが `tenant:` または `platform:` で始まる |
| 400 | `invalid_status` | `status` が 3 つのいずれでもない |

### メンバーの更新 {#update-member}

```
PUT /api/v1/organizations/{id}/members/{userId}
Content-Type: application/json

{ "status": "suspended", "roles": [] }
```

どちらのフィールドも任意です。`200` でメンバーシップを返します。`404 membership_not_found`、`400 invalid_status`、`400 invalid_role`。

### メンバーの削除 {#remove-member}

```
DELETE /api/v1/organizations/{id}/members/{userId}
```

`204`、`404 membership_not_found`。

### ドメインの追加 {#add-domain}

```
POST /api/v1/organizations/{id}/domains
Content-Type: application/json

{ "domain": "acme.com" }
```

未検証のままドメインを申請します。ドメインは先に小文字化され、空白が除去され、`acme.com` のような単純な名前である必要があります。`Location` ヘッダーと `{ "domain", "verified", "verifiedAt", "createdAt", "recordName", "recordValue" }` を伴って `201` を返します。`recordName` に値 `recordValue` の TXT レコードを公開してください([ドメインの検証](organizations#domain-verification)を参照)。

| ステータス | `error` | 条件 |
|---|---|---|
| 404 | `organization_not_found` | その組織がない |
| 400 | `domain_invalid` | 単純なドメイン名ではない |
| 409 | `domain_exists` | この組織がすでに申請している |
| 409 | `domain_taken` | 別の組織が申請している(検証済みかどうかを問わない) |

### ドメインの検証 {#verify-domain}

```
POST /api/v1/organizations/{id}/domains/{domain}/verify
```

TXT レコードを照会します。すでに検証済みの場合も含め、`200` でドメイン(`verified: true`)を返します。`404 organization_not_found`、`404 domain_not_found`、`409 domain_taken`、およびレコードがない、または誤っている場合の `409 verification_failed` があります。その `error_description` には、公開すべき名前と値、および見つかったものが記されます。

### ドメインの削除 {#remove-domain}

```
DELETE /api/v1/organizations/{id}/domains/{domain}
```

`204`。メンバーには触れません。`404 organization_not_found`、`404 domain_not_found`。

### 組織のバックフィル {#backfill-organizations}

```
POST /api/v1/organizations/backfill
Content-Type: application/json

{ "dryRun": true }
```

ユーザーの従来の `OrganizationId` から、組織とメンバーシップを作成します。`dryRun` は、本文の有無にかかわらず既定で true であり、その場合は何も書き込まれません。`200` で `{ "dryRun", "organizationsCreated", "membershipsCreated", "usersSkipped", "organizations": [ { "legacyValue", "slug", "users" } ], "truncated" }` を返します。[バックフィル](organizations#backfill)を参照してください。

## SCIM トークン {#scim-tokens}

### トークンの生成 {#generate-token}

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id",
  "description": "Entra provisioning",
  "expiresInDays": 365
}
```

`description` と `expiresInDays` はオプションです (有効期限のないトークンにするには `expiresInDays` を省略します)。生のトークンを 1 回だけ返します。再取得はできないので、安全に保管してください。

`organizationId` は任意で、トークンが作成する各ユーザーにタグを付けます([組織](organizations#membership-from-a-scim-token)を参照)。既存の組織を指す必要があり、そうでない場合は `400 unknown_organization` となり、形式が不正な値は `400 invalid_request` になります。

### トークンの一覧表示 {#list-tokens}

```
GET /api/v1/scim/tokens?clientId=client-id
```

生のトークンの値を含めずに、トークンのメタデータ (ID、作成日) を返します。

### トークンの失効 {#revoke-token}

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## トークン {#tokens}

### ユーザーのなりすまし {#impersonate-user}

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid%20profile
```

ユーザーの資格情報を必要とせずに、そのユーザーに代わってトークン (アクセストークン、リフレッシュトークン、`openid` が要求された場合は ID トークン) を発行します。テストやサポートに役立ちます。パラメーターはクエリ文字列で渡します。

| クエリパラメーター | 必須 | 説明 |
|---|---|---|
| `clientId` | はい | トークンを発行する対象のクライアント。トークンの有効期間はこのクライアントの構成から取得されます。 |
| `userId` | はい | なりすます対象のユーザー。 |
| `scopes` | いいえ | **スペース区切り**のスコープのリスト (スペースは URL エンコードしてください)。省略した場合はクライアントの `AllowedScopes` が既定値になります。 |

制限事項:

- スコープはクライアントの `AllowedScopes` に制限されます。クライアント自身が要求できないスコープを要求すると `400 invalid_scope` が返されます。
- 管理スコープ (`AdminApi:Scope`、既定値 `authagonal-admin`) はこのエンドポイントで発行することは**できません**。要求すると `403 forbidden_scope` が返されます。これにより、(有効期間が限られている可能性のある) 管理トークンから、長期間有効な管理用のアクセストークンやリフレッシュトークンを発行することを防ぎます。

レスポンスは、`access_token`、`refresh_token`、オプションの `id_token`、`expires_in`、付与された `scope` (スペース区切り) を含む標準のトークンレスポンスです。
