---
layout: default
title: OAuth スコープ
locale: ja
---

# OAuth スコープ

Authagonal は、**組み込み**の OAuth/OIDC スコープと、実行時に管理される**カスタム**スコープの両方をサポートしています。カスタムスコープは永続化され、ディスカバリドキュメントで公開され、同意画面で組み込みのスコープと並んで表示されます。

## 組み込みのスコープ {#built-in-scopes}

これらのスコープは常に利用でき、登録する必要はありません。

| スコープ | 目的 |
|---|---|
| `openid` | OIDC フローを開始するのに必須です。ID トークンを発行します。 |
| `profile` | 標準のプロフィールクレーム (name、family_name、given_name など) |
| `email` | メールアドレスと `email_verified` クレーム |
| `phone` | `phone_number` と `phone_number_verified` クレーム (OIDC Core 5.4) |
| `roles` | `roles` クレームです。OIDC の標準スコープではありません。ロールのメンバーシップは、エンドユーザーが開示に同意するクレームです |
| `groups` | `groups` クレーム (SCIM グループのメンバーシップ) です。OIDC の標準スコープではなく、`roles` と同様に制御されます |
| `offline_access` | アクセストークンとともにリフレッシュトークンを発行します |

クライアントが要求できるのは、自身の `AllowedScopes` に含まれるスコープだけです。`/connect/authorize` は、そのリストにないスコープを除外するのではなく `invalid_scope` で拒否します。そのため、クライアントに `roles` を追加せずにアプリケーションのリクエストに `roles` を追加すると、すべてのログインが失敗します。

## カスタムスコープ {#custom-scopes}

カスタムスコープは、`/api/v1/scopes` の管理 API で管理します。`authagonal-admin` スコープ (`AdminApi:Scope` で変更可能) を持つ JWT アクセストークンが必要です。

### スコープのモデル {#scope-model}

```csharp
public sealed class Scope
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }
    public string? Group { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> AllowedRoles { get; set; } = [];
    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

| フィールド | 説明 |
|---|---|
| `Name` | トークンリクエストで送信されるスコープの識別子 (例: `billing.read`) |
| `DisplayName` | 同意画面に表示される、人が読める名前 |
| `Description` | 同意画面に表示される、より長い説明 |
| `Emphasize` | `true` の場合、同意画面はこのスコープを機密性の高いものとして強調表示します |
| `Group` | このスコープを分類する同意画面上の見出しです。表示のためだけのもので、付与される内容には一切影響しません |
| `Required` | `true` の場合、ユーザーは同意の際にこのスコープの選択を外せません |
| `ShowInDiscoveryDocument` | `true` の場合、スコープは `/.well-known/openid-configuration` の `scopes_supported` に表示されます |
| `AllowedRoles` | このスコープを付与されるためにユーザーが持っている必要があるロールです。空 (既定) の場合は制限されません。[ロールで制限されたスコープ](#role-gated-scopes)を参照してください |
| `UserClaims` | このスコープが付与されたときにトークンに載せる、カスタム属性のクレーム名の許可リストです。予約されたプロトコルのクレーム (`org_id` など) がこの方法で出力されることは決してないため、保存された属性でそれらを偽造することはできません |

### ロールで制限されたスコープ {#role-gated-scopes}

クライアントの `AllowedScopes` が答えるのは、*このアプリケーションがこのスコープを要求してよいか*という、誰かがログインする前に決まる問いです。`AllowedRoles` はもう半分の問い、*この人がそれを持ってよいか*に答えます。両方の制限が適用され、一方が他方の代わりになることはありません。

```json
{
  "name": "staff-admin",
  "displayName": "Staff administration",
  "allowedRoles": ["staff", "super-admin"]
}
```

列挙されたロールを 1 つも持たないユーザーについては、スコープは拒否されるのではなく、**グラントから除外**されます。クライアントは要求したセット全体を求め、トークンレスポンスで返される `scope` (RFC 6749 §3.3) によって、受け取ったものがそれより少ないことを知らされます。これにより、1 つのアプリケーションでスタッフとそれ以外の全員の両方に対応できます。スタッフ向けの機能は複数あるスコープの 1 つであり、それを受け取るのは資格のある人だけです。

要求されたスコープが*すべて*除外されたリクエストは、トークンを発行する対象が何も残らないため、`access_denied` で失敗します。

この制限は、人に対してトークンが発行されるすべての場面に適用されます。

| フロー | 適用される場所 |
|---|---|
| 認可コード | `/connect/authorize` で、ユーザーが判明した後、同意の**前**に適用されます。そのため画面が、付与できない権限を提示することはありません |
| デバイスコード | `/api/auth/device/approve` で適用されます。そのフローでサブジェクトが判明する最初の時点です |
| リフレッシュ | ローテーションのたびに、新たに解決したロールに対して適用されます。グラントにはログイン時に承認された内容が記録されたままなので、ロールの取り消しが実際に効力を持つのはここです |
| トークン交換 | 個別には制限されません。交換ではサブジェクトトークン自身のスコープの範囲内でしか絞り込めないため、サブジェクトが付与されていないスコープに到達することはありません |

クライアントクレデンシャルのグラントにはサブジェクトがないため、意図的に対象外としています。マシンクライアントの権限は、その登録内容そのものです。

設定からスコープを初期投入すると、`AllowedRoles` を追加または変更できますが、空にはできません (`UserClaims` と同様に、省略したフィールドは保存済みの値を維持します)。制限を外すには、明示的な空の配列を指定してスコープを `PUT` してください。

## 設定からの初期投入 {#seeding-from-configuration}

スコープは `Scopes` 設定セクションで宣言できます。それらは起動時に、[クライアントの初期投入](configuration#clients)とともにスコープストアに書き込まれます。

```json
{
  "Scopes": [
    {
      "Name": "billing.read",
      "DisplayName": "Billing (read-only)",
      "Description": "View invoices and payment history",
      "UserClaims": ["billing_plan"],
      "ShowInDiscoveryDocument": true,
      "Emphasize": false,
      "Group": "Billing",
      "Required": false,
      "AllowedRoles": ["finance"]
    }
  ]
}
```

| フィールド | 説明 |
|---|---|
| `Name` | 必須です。名前のないエントリは警告とともに省略されます |
| `DisplayName`、`Description`、`UserClaims`、`ShowInDiscoveryDocument`、`Emphasize`、`Group`、`Required`、`AllowedRoles` | [スコープのモデル](#scope-model)と同じです |

初期投入は `Name` による upsert です。設定したフィールドは起動のたびに保存済みの値より優先されるため、初期投入もされているフィールドを管理 API で編集しても、次回の起動時に上書きされます。省略したフィールドは、保存されている値 (新しいスコープの場合はモデルの既定値) を維持します。省略は「維持」を意味するため、設定で `UserClaims` と `AllowedRoles` を追加または変更することはできますが、空にすることはできません。空にするには、明示的な空の配列を指定して `PUT /api/v1/scopes/{name}` を使ってください。

## 管理エンドポイント {#admin-endpoints}

### スコープの一覧 {#list-scopes}

```
GET /api/v1/scopes
```

`{ "scopes": [ ... ] }` を返します。

### スコープの取得 {#get-scope}

```
GET /api/v1/scopes/{name}
```

スコープを返します。見つからない場合は `404` を返します。

### スコープの作成 {#create-scope}

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing (read-only)",
  "description": "View invoices and payment history",
  "emphasize": false,
  "required": false,
  "showInDiscoveryDocument": true,
  "userClaims": ["billing_plan"]
}
```

スコープとともに `201 Created` を返します。`name` がないか空白を含む場合は `400` (`invalid_request`) を、同じ名前のスコープがすでに存在する場合は `409` (`scope_exists`) を返します。

### スコープの更新 {#update-scope}

```
PUT /api/v1/scopes/{name}
Content-Type: application/json

{
  "displayName": "Billing (read)",
  "description": "View invoices",
  "emphasize": true
}
```

指定したフィールドだけが更新され、省略したフィールドは現在の値を維持します。

### スコープの削除 {#delete-scope}

```
DELETE /api/v1/scopes/{name}
```

`204 No Content` を返します (スコープが存在しない場合は `404`)。このスコープを含む発行済みのトークンは、有効期限が切れるまで有効なままです。必要に応じて `/connect/revocation` で明示的に取り消してください。

## ディスカバリドキュメント {#discovery-document}

`ShowInDiscoveryDocument = true` のスコープは、`/.well-known/openid-configuration` の `scopes_supported` に表示されます。7 つの組み込みスコープは常に公開されます。

```json
{
  "scopes_supported": ["openid", "profile", "email", "phone", "roles", "groups", "offline_access", "billing.read"]
}
```

## 同意画面 {#consent-screen}

クライアントが同意の省略リストにないスコープを要求すると、同意ページは要求された各スコープを `DisplayName` (なければ `Name`) で一覧表示し、その下に `Description` を表示します。`Emphasize = true` のスコープは、ほかとは異なる見た目で表示されます。`Required` のスコープは選択を外せません。

ユーザー側のフローについては [OAuth 同意画面](index#key-features)を参照してください。

## 動的クライアント登録 {#dynamic-client-registration}

[動的クライアント登録](client-registration)で登録されたクライアントが宣言できるのは、OIDC の組み込みスコープ (`openid`、`profile`、`email`、`phone`、`offline_access`) と、`Auth:DynamicClientRegistrationScopes` で指定されたスコープだけです。スコープがストアに存在するというだけでは、自己登録したクライアントがそれを宣言してよいことにはなりません。また、ロールで制限されたスコープ (`AllowedRoles` を持つもの) は決して登録できません。それ以外のものは `invalid_scope` で拒否されます。
