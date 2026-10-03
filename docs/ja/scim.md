---
layout: default
title: SCIM 2.0 プロビジョニング
nav_order: 13
locale: ja
---

# SCIM 2.0 プロビジョニング

Authagonal は、Microsoft Entra ID、Okta、OneLogin などのエンタープライズ ID プロバイダーからユーザーを自動的にプロビジョニングするための SCIM 2.0 (System for Cross-domain Identity Management) をサポートしています。

## 概要 {#overview}

SCIM はインバウンドのプロビジョニングプロトコルです。ID プロバイダーがユーザーとグループの変更を Authagonal にプッシュします。これは、ユーザーを下流のアプリケーションにプッシュする既存の TCC (Try-Confirm-Cancel) アウトバウンドプロビジョニングを補完するものです。

**サポートされている操作:**
- ユーザーの CRUD (作成、読み取り、更新、論理的な無効化による削除)
- メンバー管理を含むグループの CRUD
- フィルタリング (`userName`、`externalId`、`displayName` に対する `eq` と `co` 演算子)
- ページング: ユーザーとグループの両方でカーソルベース (`cursor`/`nextCursor`)。グループでは既存のクライアントのために `startIndex` も引き続き受け付けますが、公開はしていません
- 部分更新のための PATCH (`active=false` による無効化を含む)
- トークン発行時に解決されるグループからロールへのマッピング

**サポートされていないもの:** 一括操作、ソート、ETag、SCIM によるパスワード管理。

すべてのリソースは、それをプロビジョニングした SCIM クライアントにスコープされます。ある SCIM トークンのクライアントが作成したユーザーやグループは、他のすべての SCIM クライアントからは見えません (404)。

## SCIM トークンの生成 {#generating-a-scim-token}

SCIM エンドポイントは静的なベアラートークンで認証されます。トークンは管理 API で生成します。

```http
POST /api/v1/scim/tokens
Authorization: Bearer {admin-token}
Content-Type: application/json

{
  "clientId": "your-client-id",
  "description": "Entra ID SCIM token",
  "expiresInDays": 365,
  "organizationId": "org_acme",
  "allowedEmailDomains": ["acme.example", "acme-eu.example"]
}
```

レスポンスには生のトークンが **1 回だけ**含まれます。トークンは SHA-256 ハッシュとして保存され、後から復元することはできないため、安全に保管してください。

```json
{
  "tokenId": "abc123",
  "clientId": "your-client-id",
  "token": "base64-encoded-token",
  "description": "Entra ID SCIM token",
  "createdAt": "2024-01-01T00:00:00Z",
  "expiresAt": "2025-01-01T00:00:00Z",
  "organizationId": "org_acme",
  "allowedEmailDomains": ["acme.example", "acme-eu.example"]
}
```

有効期限のないトークンにするには、`expiresInDays` を省略します (または `0` を渡します)。

### コネクターのユーザーに組織のタグを付ける {#tagging-a-connectors-users-with-an-organization}

`organizationId` はオプションです。設定すると、そのトークンを通じてプロビジョニングされるすべてのユーザーにその `OrganizationId` が書き込まれ、ユーザーのトークンには `org_id` クレームとして出力されます。SCIM には、コネクターが自分がどの顧客のデータを同期しているかを伝える手段がありません。コアの SCIM は組織の属性を定義しておらず、エンタープライズ拡張は実装されていないからです (後述の *スキーマのサポート* を参照)。これを資格情報に結び付けることで、顧客ごとに OAuth クライアントを用意しなくてもこの問題を解決できます。

省略するとユーザーにはタグが付きません。この機能ができる前は、すべてのトークンがそのように動作していました。クライアント ID から何かが導き出されることはありません。

ルールは 2 つです。

- **作成時のみ。** 後で別のタグが付いたトークンを通じて同期しても、既存のアカウントのタグが付け替えられることはありません。
- **プロビジョニングより先に適用されます。** TCC の `/try` レスポンスは、まだ空の組織だけを埋めます ([プロビジョニング](provisioning.md) を参照)。そのため、資格情報への明示的な結び付けが優先されます。また `/try` のペイロードは結び付けられた値を運ぶので、下流のアプリは同期がどの顧客からのものかを知ることができます。

`organizationId` が既存の [組織](organizations) を指している場合、作成時にはその組織の `active` メンバーシップ (ロールなし) も書き込まれ、`scim.organization_member_added` が監査されます。そのため、そのユーザーが組織のメンバーシップによる制限でトークンを拒否されることはありません。どの組織も指していない ID は、単なる `org_id` タグのままです。組織機能ができる前に発行されたトークンもそのように動作します。タグと同様、メンバーシップも作成時にのみ書き込まれます。

> **タグ付けは分離ではありません。** 所有権はトークンごとではなく**クライアント**ごとに適用されます。同じクライアントに対して発行された 2 つのトークンは、シークレットが 2 つある 1 つの ID であり、どちらも他方が作成したものを読み取り、名前を変更し、無効化し、削除できます。1 つの当事者がすべてを保持する場合はそれで問題ありません。互いに信頼しないコネクターがそれぞれ自分のトークンを保持する場合は、それぞれに 1 つずつクライアントを割り当ててください。

### コネクターが作成できる ID の範囲を制限する {#bounding-which-identities-a-connector-may-create}

`allowedEmailDomains` は、SCIM の資格情報が**どの**ユーザーをプロビジョニングできるかを制御する唯一の手段です。必ず設定してください。

省略すると制限のないトークンになり、その「制限なし」は見た目以上に広い範囲です。SCIM で作成されたユーザーは `EmailConfirmed = true` で書き込まれる (そのアドレスはその時点から証明済みとして扱われる) ため、制限のないコネクターは `ceo@some-other-company.example` を確認済みのアカウントとして作成できてしまいます。後で本当の所有者がフェデレーションでサインインすると、既存の外部ログインを持たないレコードは拒否されずに引き継がれるため、そのサインインはそのアカウントに結び付きます。さらに、`ScimProvisionedByClientId` には作成したコネクターの名前が残ったままなので、そのコネクターはオブジェクトの完全な所有権を持ち続けます。プロファイルを読み取り、`userName` を変更し、無効化し (これによりすべてのグラントが失効します)、あるいは削除できます。削除するとユーザーのパスキーとグループのメンバーシップが消去され、行はトゥームストーン化されるため、そのドメインの正当なコネクターはあらゆる操作で 404 を受け取ることになります。

このフィールドを省略したトークンは、発行時に、そのトークン ID を示した警告をログに出力します。

ドメインはそのまま指定してください (`@acme.example` やメールアドレスではなく `acme.example`)。決して一致し得ない値は、保存されずに拒否されます。何も許可しない制限は、構成を誤ったコネクターと見分けがつかないからです。

オペレーターは構成で制限を設定することもできます。

```json
{
  "Scim": {
    "Clients": {
      "your-client-id": { "AllowedEmailDomains": ["acme.example"] }
    }
  }
}
```

両者は**共通部分**がとられ、どちらかのソースのリストが空であれば「そのソースからの制限はない」ことを意味します。つまり、両方が空なら制限なし、どちらか一方だけが設定されていればそれ単独で適用され、両方が設定されていれば両方に含まれるドメインだけが許可されます。トークンの発行によってオペレーターが構成した制限を狭めることはできますが、広げることは決してできません。

作成、`PUT`、`PATCH` のいずれでも同じように適用されるため、名前の変更によって、資格情報がプロビジョニングを許可されていないドメインにアカウントを移すことはできません。

### トークンの一覧表示 {#listing-tokens}

```http
GET /api/v1/scim/tokens?clientId=your-client-id
Authorization: Bearer {admin-token}
```

### トークンの失効 {#revoking-a-token}

```http
DELETE /api/v1/scim/tokens/{tokenId}?clientId=your-client-id
Authorization: Bearer {admin-token}
```

## ID プロバイダーの構成 {#configuring-your-identity-provider}

### テナント URL {#tenant-url}

```
https://your-authagonal-instance/scim/v2
```

### 認証 {#authentication}

上で生成したトークンを使って **OAuth Bearer Token** を使用します。

### Microsoft Entra ID {#microsoft-entra-id}

1. Azure portal で、**Enterprise Applications** > 対象のアプリ > **Provisioning** に移動します
2. Provisioning Mode を **Automatic** に設定します
3. Tenant URL に `https://your-instance/scim/v2` を入力します
4. Secret Token に、生成手順で得た生のトークンを入力します
5. **Test Connection** をクリックして確認します
6. 属性のマッピングを構成します (後述)

### Okta {#okta}

1. Okta 管理コンソールで、**Applications** > 対象のアプリ > **Provisioning** に移動します
2. **SCIM connector** を有効にします
3. Base URL に `https://your-instance/scim/v2` を設定します
4. Authentication Mode に **HTTP Header** を設定します
5. ベアラートークンを入力します

### OneLogin {#onelogin}

1. OneLogin の管理画面で、**Applications** > 対象のアプリ > **Provisioning** に移動します
2. プロビジョニングを有効にします
3. SCIM Base URL に `https://your-instance/scim/v2` を設定します
4. SCIM Bearer Token を設定します

## SCIM エンドポイント {#scim-endpoints}

| メソッド | パス | 説明 |
|--------|------|-------------|
| GET | `/scim/v2/Users` | ユーザーの一覧表示/フィルタリング |
| GET | `/scim/v2/Users/{id}` | ユーザーの取得 |
| POST | `/scim/v2/Users` | ユーザーの作成 |
| PUT | `/scim/v2/Users/{id}` | ユーザーの置換 |
| PATCH | `/scim/v2/Users/{id}` | 部分更新 |
| DELETE | `/scim/v2/Users/{id}` | トゥームストーン化 (無効化し、以降の GET は 404) |
| GET | `/scim/v2/Groups` | グループの一覧表示/フィルタリング |
| GET | `/scim/v2/Groups/{id}` | グループの取得 |
| POST | `/scim/v2/Groups` | グループの作成 |
| PUT | `/scim/v2/Groups/{id}` | グループの置換 |
| PATCH | `/scim/v2/Groups/{id}` | メンバーの追加/削除 |
| DELETE | `/scim/v2/Groups/{id}` | グループの削除 |
| GET | `/scim/v2/ServiceProviderConfig` | 機能 |
| GET | `/scim/v2/Schemas` | スキーマ定義 |
| GET | `/scim/v2/ResourceTypes` | リソースタイプ |

独自のパスを付け加える ID プロバイダーのために、すべてのエンドポイントは `/v2` セグメントなし (例: `/scim/Users`) でもマッピングされています。ディスカバリーエンドポイント (`ServiceProviderConfig`、`Schemas`、`ResourceTypes`、および ServiceProviderConfig を返すベース URL の `/scim/` と `/scim/v2/`) は匿名でアクセスできます。それ以外はすべて SCIM のベアラートークンが必要です。

ユーザーとグループのエンドポイントは、SCIM クライアントごとに 1 分あたり 200 リクエストにレート制限されています。超過したリクエストには、ステータス `429` の SCIM エラーが返されます。

## 属性のマッピング {#attribute-mapping}

### ユーザーの属性 {#user-attributes}

| SCIM 属性 | Authagonal のフィールド |
|---------------|------------------|
| `userName` | `Email` |
| `name.givenName` | `FirstName` |
| `name.familyName` | `LastName` |
| `displayName` | `FirstName LastName` |
| `emails[type eq "work"].value` | `Email` |
| `active` | `IsActive` |
| `externalId` | `ExternalId` |
| `preferredLanguage` (なければ `locale`) | `Locale` |

### グループの属性 {#group-attributes}

| SCIM 属性 | Authagonal のフィールド |
|---------------|------------------|
| `displayName` | `DisplayName` |
| `externalId` | `ExternalId` |
| `members` | `MemberUserIds` |

### スキーマのサポート {#schema-support}

コアの SCIM 2.0 の `User` と `Group` (RFC 7643) のみです。上の表がサポートされている属性のすべてです。

**エンタープライズユーザー拡張は実装されていない**ため、`employeeNumber`、`costCenter`、`organization`、`division`、`department`、`manager` は、作成、置換、PATCH のいずれでも、保存されずに受け付けたうえで無視されます。Entra と Okta は既定の属性マッピングでこれらのいくつかをマッピングしているため、この扱いにより、標準のコネクターからそれらを取り除かなくても済みます。(0.27.0 より前は、これらを 1 つでも含む PATCH は `400 invalidPath` で丸ごと拒否されていました。そのため、作成は成功するのに増分同期はすべて失敗し、無関係な属性のせいで `active: false` によるプロビジョニング解除が実行されずに残ることがありました。)

この緩和は限定的です。`name.givenNam` のような綴りを誤ったコアのパスには引き続き `400` が返され、読み取り専用の属性 (`id`、`meta`、`groups`) は `mutability` による拒否のままです。

エンタープライズ拡張の `organization` 属性は、ユーザーの `org_id` には**なりません**。その値は顧客自身の ID プロバイダーが主張するものである一方、前述の資格情報への結び付けはオペレーターが設定するものだからです。代わりにトークンの `organizationId` を使ってください。

## 動作の詳細 {#behavior-details}

### ユーザーの作成 {#user-creation}
- SCIM でプロビジョニングされたユーザーは `EmailConfirmed = true` で作成されます (SSO のみで、パスワードはありません)。
- `ScimProvisionedByClientId` フィールドは、どの SCIM クライアントがユーザーを作成したかを記録します。
- クライアントに `ProvisioningApps` が構成されている場合は、TCC プロビジョニングが自動的に開始されます。プロビジョニングがユーザーを拒否した場合、SCIM による作成はロールバックされ、レスポンスは `scimType: invalidValue` と固定のメッセージを持つ SCIM の `400` になります (下流アプリ自身のテキストは、意図的に SCIM クライアントには返しません)。
- `userName` または `externalId` が既に存在するユーザーを作成しようとすると、SCIM の `409` 競合が返されます。PUT または PATCH によるメールアドレスの変更も、同じように競合がチェックされます。

### ユーザーの無効化 {#user-deactivation}
- `DELETE /scim/v2/Users/{id}` はリソースを**トゥームストーン化**します。ユーザーを無効化し、ローカルのレコードは保持したまま、`ScimDeletedAt` を記録します。RFC 7644 §3.6 が要求するとおり (「the service provider MUST return a 404 for all operations associated with the previously deleted resource」)、その後の `GET /scim/v2/Users/{id}` は **404** を返します。リソースを読み戻して `active: false` を期待する方法でプロビジョニング解除を確認しないでください。読み取りは 404 になり、それが成功を意味します。
- 再雇用された人を再作成できるよう、レコードは消去されずに保持されます。トゥームストーンは新しいリソースが必要とする `userName`/`externalId` を解放し、ローカルのアカウント、その監査履歴、グループのメンバーシップは残ります。
- `active = false` の `PATCH` でもユーザーは無効化されます。
- 無効化されたユーザーは、パスワード、SAML、OIDC のいずれでもログインできません。
- 無効化されると、すべてのグラント (リフレッシュトークン、セッション) が失効します。
- 下流アプリのプロビジョニング解除は `DELETE` によってのみ開始されます。`PATCH` による無効化はグラントを失効させますが、下流アプリには手を付けません。

### フィルタリング {#filtering}
RFC 7644 §3.4.2.2 のフィルター文法全体がサポートされています。

**演算子:** `eq`、`ne`、`co`、`sw`、`ew`、`gt`、`ge`、`lt`、`le`、`pr` (存在)。
**論理演算子:** `and`、`or`、`not (...)`、および括弧によるグループ化。`and` は `or` より強く結合します。
**パス:** サブ属性 (`name.givenName`)、複数値の属性 (`emails.value`)、値パス (`emails[type eq "work"].value`)、URN プレフィックス付きの名前 (`urn:ietf:params:scim:schemas:core:2.0:User:userName`)。

```
userName eq "user@example.com"
userName sw "sales-" and active eq true
emails[type eq "work"].value co "@acme.com"
not (title pr)
meta.lastModified gt "2026-01-01T00:00:00Z"
```

意味論は RFC に従います。文字列の比較は大文字と小文字を区別せず、複数値の属性はいずれかの要素が一致すれば一致し、存在しない属性に対する比較は `ne` を除いてすべて偽になります。有効な SCIM フィルターでない入力は、`400` と `scimType: invalidFilter` で、問題点を示したうえで拒否されます。

**パフォーマンス。** `userName eq` と `externalId eq` (Entra と Okta が作成や更新の前に毎回発行する検索) は、一覧のスキャンではなくインデックスによるポイント検索で解決されるため、ユーザー数がいくら多くても高速なままです。それ以外のフィルターは、クライアントのユーザーをページングしながら、範囲を限定して評価されます。ユーザーの PII は保存時に暗号化されており、ブラインドインデックスを通じてしか検索できないため、より複雑な条件はストレージ側に押し下げられないからです。カーソルによるページングでは、`nextCursor` が存在する間 `totalResults` は**省略され**、`nextCursor` がなくなった時点で正確な合計になります。「ページング」を参照してください。

### ページング {#pagination}
ユーザーの一覧は**カーソルによるページング**を使います。`GET /scim/v2/Users` の各ページは、一覧のレスポンスに `nextCursor` プロパティを返します。次のページを取得するには、それを `?cursor=` として渡します。`nextCursor` がなければ、一覧は完了しています。ページサイズは `count` で制御します (既定値 100、最大 200)。

Users エンドポイントで 1 より大きい `startIndex` を要求すると、カーソルによるページングを使うよう促す `400` エラーが返されます。最初のページを越えるオフセットによるページングは提供されていません。`totalResults` は `nextCursor` が存在する間は**完全に省略され**、最後のページでのみ正確な合計を持ちます。返されたページのサイズを報告しないのは意図的です。かつて、同期するクライアントが `totalResults` を読み取り、それが直前に受け取ったリソースの数と等しいのを見て、ディレクトリ全体を保持したと判断した結果、テナントの読み取りが知らないうちに不足するということがありました。ループは `totalResults` ではなく必ず `nextCursor` に基づいて回し、`totalResults` がない場合はゼロではなく「まだ不明」として扱ってください。

**グループの一覧もカーソルでページングされます。** `GET /scim/v2/Groups` は、フィルターありとフィルターなしのどちらの形式でも `nextCursor` を返します。同じように従ってください。グループでは、既に `startIndex` を使っているクライアントのために引き続きこれを受け付けますが、`ServiceProviderConfig` では**公開されておらず**、これに頼るべきではありません。`pagination.index` は 1 つのコレクションではなくプロバイダーについての主張であり、`/Users` はそれをサポートしていないため、どこでも正しい値は `false` しかありません。どちらでも機能するカーソルを使ってください。

フィルターを指定したグループの一覧は、テナント全体を実体化するのではなく、範囲を限定したウィンドウ単位でスキャンします。そのため、先にまだ一致するものがあっても空のページを返すことがあります。その場合は `nextCursor` を返し、`totalResults` を**省略します**。カーソル付きの空のページは「続けて取得せよ」を意味し、カーソルのない空のページは、フィルターされた集合が本当に空であることを意味します。最初の空のページをコレクションの終わりとして扱わないでください。

`count=0` を指定すると、どちらのコレクションでもリソースを含まずに `totalResults` を返します (RFC 7644 §3.4.2.4)。負の `count` は、値を丸めずに `400` で拒否されます。

### PATCH によるグループのメンバーシップ {#group-membership-via-patch}
`PATCH /scim/v2/Groups/{id}` は、主要な ID プロバイダーが実際に送信するメンバーシップの形式を受け付けます。

- **メンバーの追加:** `path: "members"` と `{ "value": "user-id" }` オブジェクトの値配列を伴う `op: "add"`。重複は無視されます。
- **メンバーの置換:** `path: "members"` を伴う `op: "replace"` は、メンバーシップ全体を指定された配列で置き換えます。
- **特定のメンバーの削除 (値配列):** `path: "members"` と削除するメンバー ID の値配列を伴う `op: "remove"` (Entra ID が送信する形式)。
- **特定のメンバーの削除 (パスフィルター):** `path: 'members[value eq "user-id"]'` を伴う `op: "remove"`。ID はパスフィルターに含まれ、値はありません (Okta がプロビジョニング解除で送信する形式)。
- **全メンバーの削除:** `path: "members"` を伴い値のない `op: "remove"` は、グループを空にします。

### グループからロールへのマッピング {#group-to-role-mapping}
SCIM グループのメンバーシップによって、アプリケーションのロールを付与できます。マッピングは (グループ, ロール) の組ごとに 1 行で、1 つのグループが複数のロールを付与することもできます。マッピングは**トークン発行時**に解決されます。ユーザーの実効ロールは、直接割り当てられたロールに、所属しているマッピング済みのすべてのグループのロールを加えたものです。そのため、グループのメンバーを追加または削除すると、ユーザーのレコードに触れることなく次のトークンから反映されます。マッピングのストアが空であれば何も起きません。

マッピングは `IScimGroupRoleMappingStore` (Azure と AWS のストレージプロバイダーが実装しています。それ以外の場合はインメモリの既定実装が登録されます) によって永続化され、SCIM API 自体ではなく、ホストするアプリケーションの管理画面で管理されます。

オプションで、`IncludeGroupsInTokens` を有効にしたクライアントは、発行されるトークンの `groups` クレームとして、ユーザーの SCIM グループの表示名も受け取ります。

## 既知の制限事項 {#known-limitations}

- **一括操作なし:** ユーザーとグループは個別にプロビジョニングする必要があります。
- **ソートなし:** ユーザーの一覧は、カーソルによるページングではストレージの順序で返されます。グループの一覧は作成日順です。
- **パスワード管理なし:** SCIM でプロビジョニングされたユーザーは SSO でのみ認証します。
- **消去ではなくトゥームストーン:** `DELETE` は、ローカルのユーザーレコードを完全に削除するのではなく、リソースを無効化してトゥームストーン化します (RFC 7644 §3.6 に従い、以降の `GET` は 404)。消去には管理 API を使ってください。
