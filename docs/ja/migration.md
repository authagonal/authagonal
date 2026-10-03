---
layout: default
title: 移行
locale: ja
---

# Duende IdentityServer からの移行

`Authagonal.Migration` パッケージは、Duende IdentityServer + SQL Server から Authagonal のストアへの一度限りの移行を行います。同じエンジンを 2 通りの方法で利用できます。

- **ホスト型ランナー** (推奨): Authagonal ホスト内で動作するバックグラウンドサービスで、デプロイ時に一度だけ移行を実行します。クラスターのリーダーシップでゲートされ、起動をブロックしません。
- **CLI**: `tools/Authagonal.Migration.Cli` です。Table Storage をターゲットとして、ローカルやオフラインで実行するためのものです。

SqlClient はこのパッケージにしか含まれないため、移行を行わないホストがこれを引き継ぐことはありません。

## ホスト型ランナー {#hosted-runner}

`AddAuthagonal` の後に追加します (ストア、シークレットプロバイダー、クラスターのリーダーシップに依存するためです)。

```csharp
builder.Services.AddAuthagonal(builder.Configuration, c => c.UseAzureStorage(blob, table));
builder.Services.AddAuthagonalDuendeMigration(builder.Configuration);

var app = builder.Build();
app.MapAuthagonalEndpoints();
app.MapAuthagonalDuendeMigration();   // GET /admin/migration/status
```

2 つ目の `Map` 呼び出しは必須で、別個に行う必要があります。このパッケージは `Authagonal.Server` を参照しているため、`MapAuthagonalEndpoints` からはこれに到達できないからです。これがないと `GET /admin/migration/status` は 404 を返し、`IdentityAdmin` ポリシーによって拒否された場合と見分けがつきません。その場合、実行時には起動時にその旨を伝える警告がログに出力されます。

`Migration` セクションで設定します。

```json
{
  "Migration": {
    "Enabled": true,
    "DryRun": false,
    "Version": "1",
    "UsersMode": "CreateOnly",
    "MigrateClients": true,
    "MigrateRefreshTokens": false,
    "LeaseWaitMinutes": 10,
    "StartupDelaySeconds": 30,
    "Source": { "ConnectionString": "Server=...;Database=Identity;..." }
  }
}
```

ランナーは次のように動作します。

1. `StartupDelaySeconds` だけ待機します (初期投入サービスが先に完了します。起動がブロックされることはありません)。
2. `Version` に対して、`DryRun` でない `Completed` マーカーがすでに存在する場合はスキップします。
3. クラスターのリーダーになるまで最大 `LeaseWaitMinutes` 待ちます (移行を実行するのは 1 つのポッドだけです)。
4. `Started` マーカーを書き込み、エンジンを実行し、その後レポート付きで `Completed`/`Failed` マーカーを書き込みます。

実行途中でリーダーシップを失うとエンジンはキャンセルされ、新しいリーダーが再実行します。どのパスも冪等なので、これは安全です。進捗は `GET /admin/migration/status` で確認できます (`IdentityAdmin` ポリシーでゲートされます)。

## CLI {#cli}

```bash
docker run authagonal-migration \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  --DryRun true --UsersMode CreateOnly
```

(イメージ名の後に `--` 区切りは付けません。) ソースから実行する場合:

```bash
dotnet run --project tools/Authagonal.Migration.Cli -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  --DryRun true
```

## 移行される内容 {#what-gets-migrated}

| ソース (SQL Server) | ターゲット | 備考 |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Users + メール/名前のインデックス | ID はそのまま保持されます。クレームの取り込み: `given_name`→FirstName、`family_name`→LastName、`company`→CompanyName、`org_id`→OrganizationId (xmlsoap の変種も対象)。メールのクレームは破棄され、それ以外はすべてカスタム属性になります。パスワードハッシュが null (外部 SSO 専用ユーザー) でも問題ありません。BCrypt / ASP.NET Identity V3 のハッシュはそのまま検証でき、次回ログイン時にネイティブの PBKDF2 にアップグレードされます。 |
| `AspNetUserLogins` | UserLogins | `409 Conflict` = スキップ (冪等) |
| `AspNetRoles` + `AspNetUserRoles` | Roles + ユーザーとロールの関連付け | ロール ID→名前のマップによってユーザーへの割り当てを解決します |
| `ApiScopes` + `IdentityResources` | Scopes | 既存の (初期投入された) 名前はスキップされ、スコープのクレームはコピーされます |
| Duende の `Clients` + 子テーブル | Clients | シークレットはダイジェスト長によって `SHA256$`/`SHA512$` のタグが付けられます (それ以外は警告付きで破棄)。失効したシークレットはスキップされます。設定で初期投入されたクライアントが優先されます (スキップ) |
| Duende の `ApiResources` | (平坦化) | オーディエンス → 移行で作成されるクライアント。リソースのクレーム → 移行で作成されるスコープ |
| `SamlProviderConfigurations` | SamlProviders + SsoDomains | `AllowedDomains` の CSV が分割され、SSO ドメインのレコードになります |
| `OidcProviderConfigurations` | OidcProviders + SsoDomains | 同様にドメインを分割します |
| `AspNetUserTokens` (`AuthenticatorKey`、`RecoveryCodes`) | MfaCredentials | TOTP シークレットは base32→保護された形式 (`duende-totp`)、リカバリーコードはハッシュ化されます (`duende-rc-{n}`)。すでに MFA を持つユーザーはスキップされます |
| Duende の `PersistedGrants` (リフレッシュトークン) | Grants | **標準の Duende からは不可能です**。後述を参照してください。`MigrateRefreshTokens` *と* `SourceGrantKeysAreUnhashed` の両方が必要です。それ以外の場合は警告付きでスキップされ、ユーザーは再ログインします。 |

## オプション {#options}

| オプション | 既定値 | 説明 |
|---|---|---|
| `Enabled` | `false` | ホスト型ランナーのマスタースイッチです |
| `DryRun` | `false` | 書き込みを行わずにソースを走査し、完全な検証レポート (ID の文字種/長さ、重複したメールアドレス、テーブル/列の一覧、パスごとの件数) を作成します |
| `Version` | `"1"` | 実行マーカーです。差分の再実行を行うには値を上げます。再実行を妨げるのは、`DryRun` でない `Completed` マーカーだけです |
| `UsersMode` | `CreateOnly` | `CreateOnly` は既存のユーザーをスキップし、`Upsert` は上書きします。**切り替え後に `Upsert` を使ってはいけません**。再ハッシュされたパスワードや新しい MFA を上書きしてしまいます |
| `MigrateClients` | `true` | OAuth クライアントを移行します。設定で初期投入されたクライアントが常に優先され、既存のクライアントはスキップされます |
| `MigrateRefreshTokens` | `false` | 有効なリフレッシュトークンを含めます。`SourceGrantKeysAreUnhashed` が必要です |
| `SourceGrantKeysAreUnhashed` | `false` | ソースの `PersistedGrants.Key` がハンドルをそのまま保持していることを表明します。独自のグラントストアを持つフォークでのみ true になります |
| `Source:ConnectionString` | *(なし)* | ソースの Duende SQL Server への接続です |
| `MaxDegreeOfParallelism` | `32` | 件数の多いパス (ユーザー、外部ログイン、MFA、リフレッシュトークン) における書き込みの並行数の上限です。小規模なアカウントやスロットリングされやすいアカウントでは下げてください。`1` で完全に逐次実行になります |
| `LeaseWaitMinutes` | `10` | ホスト型ランナー: この時間が経過したら、クラスターのリーダーシップを待つのをあきらめます。後の再起動で再試行されます |
| `StartupDelaySeconds` | `30` | ホスト型ランナー: 開始前の待機時間です。初期投入サービスが完了し、起動がブロックされないようにします |

## 冪等性と差分の再実行 {#idempotency--delta-sweeps}

どのパスも冪等 (存在すればスキップ、MFA の ID は決定的) なので、移行は安全に再実行できます。切り替えの数日前に実行し、切り替えの直前に `Version` を上げて最終的な差分の再実行を行い、その間に登録されたユーザーを取り込んでください。既存のレコードはスキップされ (`Upsert` の場合は更新され)、重複することはありません。

## 移行されない内容 {#what-is-not-migrated}

- **標準の Duende からの有効なリフレッシュトークン。** Duende の `DefaultGrantStore` はリフレッシュトークンのハンドルを永続化しません。`PersistedGrants.Key` には `base64(SHA-256(handle + ":" + grantType))` が格納され、提示されたハンドルは検索時に再度ハッシュ化されます。したがってハンドルはソースデータベースから復元できず、移行した行は永久に引き換えられないものになります。これは移行しないよりも悪い結果です。レポートはそれらを作成済みとして数え、不具合は切り替え後の最初のトークンリフレッシュで初めて表面化するからです。1 回の再ログインを前提に切り替えを計画するか、移行期間中は両方を読み取るシムを動かしてください。`SourceGrantKeysAreUnhashed` は、グラントストアがハンドルをそのまま永続化するフォークのためだけに存在します。そのようなフォークでは、`PersistedGrants.Data` を Duende の `RefreshToken` の形から `RefreshTokenData` に変換する作業もフォーク側の責任になります。
- **SCIM トークンとグループ**、**ユーザーのプロビジョニング情報**: Duende に相当するものがないため、空の状態から始まります。
- **署名鍵**: 自動化されていません。切り替えをまたいで既存のトークンを有効なままにするには、切り替えの直前に Duende から RSA 署名鍵をエクスポートし、`SigningKeys` テーブルにインポートしてください。

## 切り替えの手順 {#cutover-strategy}

1. 無効な状態でデプロイします (`Enabled=false`)。
2. `Enabled=true, DryRun=true` → 再起動 → `/admin/migration/status` でレポートを確認します。
3. `DryRun=false` → 再起動 → マーカーが `Completed` であることを確認し、ログインを抜き取りで確認します。
4. 最終的な差分の再実行のために `Version` を上げ、その後クライアント/BFF の接続先を Authagonal に切り替えます。**1 回の強制再ログインが発生することを想定してください**。前述を参照してください。
5. 監視します。ロールバック = 手を付けていない Duende のデプロイに接続先を戻します。

## NDJSON によるユーザーのインポート {#ndjson-user-import}

同じ `Authagonal.Migration` パッケージに含まれる、2 つ目の独立したインポート元です。稼働中のデータベース接続ではなくフラットな NDJSON ファイル (1 行に 1 つの JSON オブジェクト) を使い、対象はユーザーだけで、クライアント、ロール、スコープ、フェデレーションの設定は含みません。レガシーアプリ独自のユーザーテーブル (手作りの ASP.NET Identity ストア、bcrypt 形式でエクスポートした Rails/Devise のテーブル、scrypt を使う Node アプリなど) を移行するためのもので、ユーザーは古いパスワードでそのままログインでき、次回ログインに成功した時点でパスワードが透過的にネイティブの PBKDF2 に再ハッシュされます。これは上記の Duende インポーターが依存しているのと同じ遅延再ハッシュの経路です。

### レコードのスキーマ {#record-schema}

1 行に 1 つの JSON オブジェクトです。必須フィールドは `email` だけで、それ以外のフィールドはすべて任意です。`--AllowUnknownFields true` を渡さない限り、**未知のトップレベルフィールドがあるとその行は失敗します** (既定では厳格です)。

| フィールド | 型 | 備考 |
|---|---|---|
| `email` | string | 必須です。メールアドレスとして妥当な形式でなければなりません。大文字小文字を区別しない重複判定のキーです。 |
| `username` | string | `AuthUser` に専用の列はなく、`CustomAttributes["username"]` に保存されます。 |
| `givenName` | string | → `AuthUser.FirstName` |
| `familyName` | string | → `AuthUser.LastName` |
| `displayName` | string | 専用の列はなく、`CustomAttributes["displayName"]` に保存されます。 |
| `emailVerified` | bool | → `AuthUser.EmailConfirmed`。存在しない場合の既定値は `false` です。 |
| `passwordHash` | string | → `AuthUser.PasswordHash`。**そのまま**保存されます。ログイン時に `PasswordHasher` が認識する形式 (bcrypt の `$2a$`/`$2b$`/`$2x$`/`$2y$`、ASP.NET Identity V3、scrypt の `$s2$`) であれば、そのまま検証でき、そこからネイティブの PBKDF2 にアップグレードされます。空でないこと以外は検査されず、不正な形式のハッシュはログイン時に検証に失敗するだけです。これは移行以外の場合と同じです。SSO 専用 / パスワードレスのユーザーでは省略してください。 |
| `roles` | string[] | → `AuthUser.Roles` |
| `organizationId` | string | → `AuthUser.OrganizationId` |
| `attributes` | object (string→string) | `AuthUser.CustomAttributes` にマージされます |
| `phoneNumber` | string | → `AuthUser.Phone` |
| `disabled` | bool | → `AuthUser.IsActive = !disabled`。存在しない場合の既定は有効です。 |
| `createdAt` | string (ISO 8601) | → `AuthUser.CreatedAt`。存在しない場合の既定はインポート時刻です。 |
| `externalId` | string | → `AuthUser.ExternalId`。Duende インポーターがソースデータベースのユーザー ID を入れるのと同じフィールドです。 |

ファイルの例 (5 行):

```ndjson
{"email":"ada.lovelace@legacy.example.com","givenName":"Ada","familyName":"Lovelace","passwordHash":"$2b$12$KIXQ8N6Qe0m6b6b6b6b6bOQe0m6b6b6b6b6b6b6b6b6b6b6b6b6b6","roles":["admin"],"organizationId":"org-legacy-1","externalId":"42"}
{"email":"bob@legacy.example.com","emailVerified":true,"attributes":{"dept":"eng"},"createdAt":"2019-03-04T00:00:00Z"}
{"email":"carol@legacy.example.com","disabled":true,"phoneNumber":"+61400000000"}
{"email":"dave@legacy.example.com","username":"dave1998","displayName":"Dave K."}
{"email":"erin@legacy.example.com"}
```

### CLI {#cli-1}

```bash
dotnet run --project tools/Authagonal.Migration.Cli -- import-ndjson-users \
    --Input ./users.ndjson \
    --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
    --DryRun true \
    --OnDuplicate skip \
    --BatchSize 500 \
    --AllowUnknownFields false \
    --ContinueOnError false \
    --AllowPlaintextPii true
```

ターゲット (Azure Table Storage) と PII の平文に関するゲートは、上記の Duende CLI と同じです。このインポート元は、ホストに登録された `IFieldCipher`/`IIndexTokenizer` を介さずに `AuthUser` の行を Table Storage に直接書き込みます。そのため、ターゲットにどちらも設定されていないことを `--AllowPlaintextPii true` で確認しない限り、実行を拒否します (あるいは、`NdjsonUserImportEngine` をホスト自身の DI コンテナに組み込めば、それらの拡張点が解決されます)。Duende CLI とは異なり、`--AllowPlaintextSecrets` のゲートはありません。このインポート元は MFA の TOTP シードや OAuth クライアントシークレットを書き込むことはなく、書き込むのはユーザープロファイルのフィールドと、そのまま保存されるパスワードハッシュだけです。

### オプション {#options-1}

| オプション | 既定値 | 説明 |
|---|---|---|
| `--Input` | *(必須)* | NDJSON ファイルのパス |
| `--Target:ConnectionString` | *(必須)* | Azure Table Storage の接続文字列 |
| `--DryRun` | `false` | すべての行を解析して検証し、ターゲットに対して重複を判定し、完全なレポートを作成します。書き込みは行いません |
| `--OnDuplicate` | `skip` | メールアドレス (大文字小文字を区別しない) が既存のユーザーとすでに一致する行の扱いです。`skip` (手を付けない。冪等)、`update` (その行に存在するフィールドを既存のユーザーにマージ)、`fail` (実行をただちに中止) のいずれかです |
| `--BatchSize` | `500` | 進捗のログ行を出力する間隔の行数です。書き込みをまとめる仕組みではありません。`IUserStore` には一括処理の API がないため、インポートや更新はいずれも 1 回のストア呼び出しのままです |
| `--AllowUnknownFields` | `false` | 上記のスキーマにないトップレベルの JSON プロパティがあっても、行を失敗させずに受け付けて無視します |
| `--ContinueOnError` | `false` | 1 行以上の解析や検証に失敗しても終了コード 0 で終了します。`--OnDuplicate fail` には適用されず、そちらはこのフラグにかかわらず常に実行を中止します |

### サマリー出力と終了コード {#summary-output--exit-codes}

レポートは JSON で出力されます。`TotalLines`、`Imported`、`Updated`、`Skipped`、`Failed`、そして最初の 20 件の `Failures` (`LineNumber` + `Reason`) です。空行はどこにも数えられません。終了コード:

- `0`: 成功 (または `--ContinueOnError true` で 1 行以上が失敗した場合)
- `1`: 1 行以上の解析または検証に失敗し、かつ `--ContinueOnError` が設定されていない
- `2`: 実行が中止された (`--OnDuplicate fail` が既存のメールアドレスに一致した、または必須オプションが指定されていなかった)

### 冪等性 {#idempotency}

既定の `--OnDuplicate skip` では、変更していないファイルで再実行すると、2 回目は何も起こりません。メールアドレスがすでに存在する行はすべてスキップとして数えられ、何も書き込まれません。`update` も安全に再実行できます (常に同じフィールドを適用し直します)。`fail` は、既存のアカウントと黙って衝突してはならない一度限りのインポートのためのものです。

### インポートされない内容 {#what-is-not-imported}

- **ロール、スコープ、OAuth クライアント、フェデレーションの設定。** このインポート元はユーザーだけが対象です。それらも必要な場合は上記の Duende インポーターを参照してください。
- **MFA の資格情報、外部ログイン。** スキーマに含まれていません。インポート後に、標準の MFA のセットアップ / SSO フローを通じて追加してください。
