---
layout: default
title: プロビジョニング
locale: ja
---

# TCC プロビジョニング

Authagonal は、**Try-Confirm-Cancel (TCC)** パターンを使って下流のアプリケーションにユーザーをプロビジョニングします。これにより、ユーザーがアクセスを得る前にすべてのアプリが合意し、いずれかのアプリが拒否した場合はきれいにロールバックされます。

## プロビジョニングが実行されるタイミング {#when-provisioning-runs}

プロビジョニングは、作成の経路にかかわらず、ユーザーが作成されるたびに自動的に実行されます。

| エンドポイント | きっかけ |
|---|---|
| `POST /api/v1/profile/` | 管理者によるユーザーの作成 |
| `POST /api/auth/register` | セルフサービス登録 |
| SAML ACS (`POST /saml/{id}/acs`) | 初回の SSO ログイン (新規ユーザー) |
| OIDC コールバック (`GET /oidc/callback`) | 初回の SSO ログイン (新規ユーザー) |
| SCIM (`POST /scim/v2/Users`) | アイデンティティプロバイダーによるプロビジョニング |
| `GET /connect/authorize` | `ProvisioningApps` を持つクライアントを通じた初回の認可 |

すでにプロビジョニング済みのアプリとユーザーの組み合わせは省略されます (`UserProvisions` テーブルで追跡されます)。

ユーザー作成の経路では、**設定されたすべてのアプリ**にプロビジョニングします。認可エンドポイントは、クライアントの `ProvisioningApps` リストにあるアプリにだけプロビジョニングします。

**拒否された場合:** いずれかのプロビジョニングのアプリが Try フェーズでユーザーを拒否した (またはコールバックが失敗した) 場合、新しく作成されたユーザーは削除されます。これにより、中途半端に作成されたユーザーが残るのを防ぎます。呼び出し元に返されるものは経路によって異なります。

| 経路 | レスポンス |
|---|---|
| 管理者による作成 (`POST /api/v1/profile/`)、セルフサービス登録 | 拒否の理由を含む `422 Unprocessable Entity` |
| SAML ACS、OIDC コールバック | `400 Bad Request`、`{ "error": "provisioning_rejected", "message": "..." }` |
| SCIM での作成 | SCIM の `400`、`scimType: invalidValue`、固定のメッセージ付き (下流のアプリのテキストはアイデンティティプロバイダーに返されません) |
| パスワードのないアカウントの引き継ぎの確認 | JSON の `400 provisioning_rejected`、またはブラウザでのクリックの場合は `/login?error=provisioning_rejected&error_description=...` へのリダイレクト ([ユーザーのアップグレード](user-upgrade)を参照) |
| `GET /connect/authorize` | `error=access_denied` を付けてクライアントにリダイレクト |

管理者による作成のリクエストは `skipProvisioning: true` を受け付けます。これは、それ自体がプロビジョニングの対象であり、ユーザーのセットアップの途中で自分のコールバックが再び呼び出されることを望まないファーストパーティの呼び出し元のためのものです。そのユーザーについては何もプロビジョニングされず、どのアプリも呼び出されません。

## 設定 {#configuration}

### 1. プロビジョニングのアプリを定義する {#1-define-provisioning-apps}

`appsettings.json` で次のように指定します。

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-bearer-token",
      "TryTimeoutSeconds": 60
    }
  }
}
```

`TryTimeoutSeconds` は任意です (既定値 60)。下流のアプリが Try の間に実際の処理を行う場合は値を大きくしてください。Confirm、Cancel、Deprovision は常に短い固定のタイムアウト (10 秒) を使い、調整はできません。これらは常に軽い処理であるべきです。

`ProvisioningApps` 設定セクションが読み込まれるのは、`IProvisioningAppStore` が登録されていない場合だけです。Azure Table、AWS、SQL の各プロバイダーはそれぞれこれを登録し、その場合ライブラリは代わりにストアからアプリを解決します ([カスタムのアプリ解決](#custom-app-resolution)を参照)。そのため、永続的なプロバイダーを使う場合は、`appsettings.json` ではなく管理 API でアプリを定義してください。

### 2. アプリをクライアントに割り当てる {#2-assign-apps-to-clients}

各クライアントは、クライアントレコードの `provisioningApps` フィールドで、自身のユーザーをどのアプリにプロビジョニングする必要があるかを宣言します。これはクライアントの管理 API で設定してください (`Clients` の初期投入の設定にはこのフィールドがありません)。クライアントを作成するとレコード全体がバインドされ、`PUT /api/v1/clients/{clientId}` は送信したフィールドを保存済みのクライアントにマージします。そのため、`provisioningApps` だけを含むリクエストは、クライアントの他の部分を変更しません。

```
PUT /api/v1/clients/web-app
{
  "provisioningApps": ["my-backend"]
}
```

ユーザーが `web-app` を通じて認可すると、まだプロビジョニングされていなければ `my-backend` にプロビジョニングされます。

## TCC プロトコル {#tcc-protocol}

Authagonal は、プロビジョニングのエンドポイントに対して 3 種類の HTTP 呼び出しを行います。いずれも JSON ボディと `Authorization: Bearer {ApiKey}` を伴う `POST` です。

### フェーズ 1: Try {#phase-1-try}

**リクエスト:** `POST {CallbackUrl}/try`

```json
{
  "transactionId": "a1b2c3d4...",
  "userId": "user-id",
  "email": "user@example.com",
  "firstName": "Jane",
  "lastName": "Doe",
  "organizationId": "org-id-or-null",
  "customAttributes": { "key": "value" }
}
```

null のフィールド (ユーザーに属性がない場合の `customAttributes` を含む) は、ペイロードから省略されます。

**想定されるレスポンス:**

| ステータス | ボディ | 意味 |
|---|---|---|
| `200` | `{ "approved": true }` | ユーザーをプロビジョニングできます。アプリは**保留中**のレコードを作成します。 |
| `200` | `{ "approved": false, "reason": "..." }` | ユーザーは拒否されます。レコードは作成されません。 |
| `2xx` | 空または解析できないボディ | 承認として扱われます。 |
| 2xx 以外 | 任意 | 失敗として扱われます。 |

`approved` の値を明示的に返してください。ボディを JSON として読み取れないレスポンスは承認として扱われるため、HTML ページを付けて `200` を返すよう誤って設定されたエンドポイントは、すべてのユーザーを承認してしまいます。

`transactionId` は、このプロビジョニングの試行を識別します。アプリはこれを保留中のレコードとともに保存してください。

承認したレスポンスでは、`organizationId`、`customAttributes`、`emailVerified` を返すこともできます。Authagonal はそれらをユーザーにマージします。`organizationId` はユーザーがまだ持っていない場合にのみ適用され (同じトランザクション内の後のアプリには、先に割り当てられた値が見えます)、`customAttributes` のエントリはキーごとにマージされ、`emailVerified: true` はユーザーのメールアドレスを確認済みにします (下流のアプリがすでにアドレスを確認済みの場合に使います。その場合、セルフサービス登録は確認メールを省略します)。`organizationId` と属性はどちらもトークンに載ります (`org_id` クレーム。カスタム属性はスコープの `UserClaims` の設定によります)。マージされた値は、すべてのアプリが確認した時点でユーザーに保存されます。

### フェーズ 2: Confirm {#phase-2-confirm}

Try フェーズで**すべての**アプリが `approved: true` を返した場合にのみ呼び出されます。

**リクエスト:** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**想定されるレスポンス:** `2xx` (ボディは任意)。アプリは保留中のレコードを確定済みに昇格させます。2xx 以外のレスポンスやタイムアウト (10 秒) は、確認の失敗として扱われます。

### フェーズ 3: Cancel {#phase-3-cancel}

**いずれかの**アプリの Try が拒否されたか失敗した場合に、Try フェーズで成功したアプリを後始末するために呼び出されます。

**リクエスト:** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**想定されるレスポンス:** `200` (ボディは任意)。アプリは保留中のレコードを削除します。

Cancel はベストエフォートです。失敗した場合、Authagonal はエラーをログに出力して処理を続けます。安全策として、アプリは**確定されていないレコードを TTL (たとえば 1 時間) の後に回収する**必要があります。

## フロー図 {#flow-diagram}

```
Authorize Endpoint
    │
    ├─ User authenticated ✓
    ├─ Client requires apps: [A, B]
    ├─ User already provisioned into: [A]
    ├─ Need to provision: [B]
    │
    ├─ TRY B ──────────► App B: create pending record
    │   └─ approved: true
    │
    ├─ CONFIRM B ──────► App B: promote to confirmed
    │   └─ 200 OK
    │
    ├─ Store provision record (userId, "B")
    ├─ Issue authorization code
    └─ Redirect to client
```

### 失敗した場合 {#on-failure}

```
    ├─ TRY A ──────────► App A: create pending record
    │   └─ approved: true
    │
    ├─ TRY B ──────────► App B: rejects
    │   └─ approved: false, reason: "No license available"
    │
    ├─ CANCEL A ───────► App A: delete pending record
    │
    └─ Redirect with error=access_denied
```

### 一部の確認が失敗した場合 {#on-partial-confirm-failure}

確認が失敗すると、Authagonal はトランザクション全体をロールバックします。

1. まだ確認されていないアプリには `POST {CallbackUrl}/cancel` が送られます。
2. **このトランザクションで**すでに確認したアプリには、`DELETE {CallbackUrl}/users/{userId}` ([プロビジョニング解除](#deprovisioning)と同じ呼び出し) で補償処理が行われ、それらのプロビジョニングのレコードは削除されます。以前のトランザクションでユーザーがプロビジョニングされたアプリには手を付けません。
3. プロビジョニングのエラーが発生し、呼び出し元の経路は新しく作成されたユーザーを削除します (認可エンドポイントの場合はエラーで応答します)。

プロビジョニングのレコードはすべての確認が成功した後にのみ保存されるため、再試行ではすべてのアプリが再び試行されます。補償処理はベストエフォートです。`DELETE` が失敗した場合はログに出力され、アプリのアカウントを手動で削除する必要があるかもしれません。

## カスタムのアプリ解決 {#custom-app-resolution}

ライブラリはアプリの取得元を自動的に選びます。

- `IProvisioningAppStore` が登録されている場合 (Azure Table、AWS、SQL の各プロバイダーはいずれも登録します)、アプリはストア (`StoreProvisioningAppProvider`) から取得され、下記の管理 API で管理されます。
- それ以外の場合は、`ProvisioningApps` 設定セクション (`ConfigProvisioningAppProvider`) から読み込まれます。

たとえばテナントごとになど、別の方法でアプリを解決するには、`AddAuthagonal` の前に独自の `IProvisioningAppProvider` を登録します。ライブラリの既定は、何も登録されていない場合にのみ追加されます。

```csharp
builder.Services.AddSingleton<IProvisioningAppProvider, MyAppProvider>();
builder.Services.AddAuthagonal(builder.Configuration);
```

プロバイダーは、アプリとそのコールバック URL のリストを返します。`TccProvisioningOrchestrator` が、それぞれに対して Try/Confirm/Cancel を呼び出します。

> **`CallbackUrl` は、既定では公開ルーティング可能である必要があります。** Authagonal は、書き込み時と、それに対して行うすべてのリクエストのたびにこれを検証し、ループバック、RFC1918、リンクローカル、`.internal`/`.local` のターゲットを拒否します (プロビジョニングのコールバックは、サーバーが取得する URL だからです)。自社のネットワーク内で動作するプロビジョニングのアプリも、サポートされるデプロイです。それを [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard) に指定してください。

### 管理 API {#admin-api}

ストアに保持されたアプリは `/api/v1/provisioning/apps` で管理します (ポリシーは `IdentityAdmin` で、すべての変更は監査されます)。

| ルート | 動作 |
|---|---|
| `GET /` | `{ "apps": [{ "appId", "name", "callbackUrl", "hasApiKey", "tryTimeoutSeconds" }], "limit": n }`。API キーが返されることは決してなく、返されるのは `hasApiKey` だけです。`limit` はアプリの割り当て上限で、上限がない場合は null です。 |
| `POST /` | 作成します。`name` と `callbackUrl` は必須で、`apiKey` と `tryTimeoutSeconds` は任意です。12 文字の `appId` が生成されます。割り当て上限を超えると `400 provisioning_app_limit` になります。 |
| `PUT /{appId}` | `name`、`callbackUrl`、`tryTimeoutSeconds` を置き換えます (`name` と `callbackUrl` はここでも必須です)。`apiKey` を省略するか null にするとキーは変更されず、空文字列にするとキーが消去されます。未知のアプリには `404 app_not_found` を返します。 |
| `DELETE /{appId}` | `{ "removed": true }`。 |
| `POST /{appId}/test` | 固定のテストユーザー (`test-user`、`test@example.com`) で、10 秒のタイムアウト付きの Try をアプリに送信します。`{ "success", "statusCode", "body" }` を返します (ボディは 1000 文字に切り詰められます)。接続の失敗は、エラーのステータスではなく `success: false, statusCode: 0` を返します。 |

`callbackUrl` は、上で説明したとおり、外部ホスト上の絶対的な `http` または `https` の URL でなければなりません。`tryTimeoutSeconds` は 5 から 300 秒の範囲に丸められます。クライアントが `provisioningApps` に列挙するのは `appId` です。

## プロビジョニング解除 {#deprovisioning}

ユーザーが管理 API で削除された場合 (`DELETE /api/v1/profile/{userId}`) や、SCIM でプロビジョニングを解除された場合 (`DELETE /scim/v2/Users/{id}`。ユーザーを無効化する論理削除) は、Authagonal はそのユーザーがプロビジョニングされていた各アプリに対して 10 秒のタイムアウト付きで `DELETE {CallbackUrl}/users/{userId}` を呼び出し、プロビジョニングのレコードを削除します。これはベストエフォートです。失敗はログに出力されますが、削除を妨げることはありません。もう設定されていないアプリは、警告とともに省略されます。

`IProvisioningOrchestrator` の `ReprovisionAsync` は、ユーザーがすでにプロビジョニングされている場合でも、すべてのアプリに対して Try と Confirm を再実行します。ライブラリがこれを使うのは、パスワードのないアカウントが引き継がれたときです ([ユーザーのアップグレード](user-upgrade)を参照)。単に再ログインしただけで使われることはありません。

## 上流のエンドポイントの実装 {#implementing-the-upstream-endpoints}

### 最小限の例 (Node.js/Express) {#minimal-example-nodejsexpress}

```javascript
const pending = new Map(); // transactionId → user data

app.post('/provisioning/try', (req, res) => {
  const { transactionId, userId, email } = req.body;

  // Your business logic: can this user be provisioned?
  if (!isAllowed(email)) {
    return res.json({ approved: false, reason: 'Domain not allowed' });
  }

  // Store pending record with TTL
  pending.set(transactionId, { userId, email, createdAt: Date.now() });

  res.json({ approved: true });
});

app.post('/provisioning/confirm', (req, res) => {
  const { transactionId } = req.body;
  const data = pending.get(transactionId);

  if (data) {
    createUser(data); // Promote to real record
    pending.delete(transactionId);
  }

  res.sendStatus(200);
});

app.post('/provisioning/cancel', (req, res) => {
  pending.delete(req.body.transactionId);
  res.sendStatus(200);
});

// Cleanup unconfirmed records older than 1 hour
setInterval(() => {
  const cutoff = Date.now() - 3600000;
  for (const [id, data] of pending) {
    if (data.createdAt < cutoff) pending.delete(id);
  }
}, 600000);
```
