---
layout: default
title: インストール
locale: ja
---

# インストール

## Docker (推奨) {#docker-recommended}

ビルド済みのイメージを取得して実行します。

```bash
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  drawboardci/authagonal
```

## Docker Compose {#docker-compose}

Azurite (Azure Storage エミュレーター) を使ったローカル開発の場合:

```yaml
services:
  azurite:
    image: mcr.microsoft.com/azure-storage/azurite
    ports:
      - "10000:10000"
      - "10001:10001"
      - "10002:10002"

  authagonal:
    build: .
    ports:
      - "8080:8080"
    environment:
      - Storage__ConnectionString=DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://azurite:10002/devstoreaccount1;
      - Issuer=http://localhost:8080
      # Local development only: the OAuth endpoints answer plain http. See below.
      - Auth__AllowInsecureHttp=true
    depends_on:
      - azurite
```

```bash
docker compose up
```

> ⚠️ **`Auth:AllowInsecureHttp` は開発用の設定です。** RFC 6749 §3.1/§3.2 は認可エンドポイントとトークンエンドポイントに TLS を要求しているため、この設定がない限り、Authagonal は `/connect/*` への https 以外のリクエストを拒否します。スキームは転送ヘッダーの処理後に読み取られるので、TLS を終端して `X-Forwarded-Proto: https` を転送するプロキシがあれば、この設定をオフにしたままで要件を満たせます。自分以外の誰かがアクセスできるデプロイメントでは、必ずそうしてください。この設定をオンにすると、通信経路上の観測者が認可コード、`Authorization: Basic` ヘッダー内のクライアントシークレット、アクセストークンとリフレッシュトークンを読み取れてしまいます。[構成](configuration#authentication) を参照してください。

## ソースからのビルド {#building-from-source}

### 前提条件 {#prerequisites}

- .NET 10 SDK
- Node.js 24+

Authagonal は `net9.0` と `net10.0` をターゲットとしており、実行時には**パッチ適用済み**の共有フレームワークが必要です。**最低でも 9.0.18 または 10.0.10** です。理由は [本番環境のセキュリティチェックリスト](#production-security-checklist) を、起動時のチェックを起動拒否に変える方法は `Auth:RequireMinimumRuntime` を参照してください。

### ビルド {#build}

```bash
# Build everything
dotnet build

# Build the login SPA
cd login-app
npm ci
npm run build

# Run the server
dotnet run --project src/Authagonal.Server
```

### Docker ビルド {#docker-build}

```bash
# Server image (multi-stage: builds SPA + .NET in one image)
docker build -t authagonal .

# Migration tool
docker build -f Dockerfile.migration -t authagonal-migration .
```

## ライブラリとして利用する (NuGet) {#as-a-library-nuget}

自分の ASP.NET Core プロジェクトで Authagonal パッケージを参照します。

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.AzureProvider" Version="x.y.z" />
```

ストレージプロバイダーのパッケージは差し替え可能です。Azure Table Storage には `Authagonal.AzureProvider` (`AddAuthagonal()` の既定の構成)、セルフホストの PostgreSQL または SQLite には `Authagonal.SqlProvider` ([SQL バックエンド](#sql-backend) を参照)、DynamoDB / S3 / Secrets Manager には `Authagonal.AwsProvider` ([AWS バックエンド](#aws-backend) を参照) を使います。

> **登録の順序が重要です。** ストレージプロバイダーは `AddAuthagonal()` より**前**に登録しなければなりません。既に存在する `IUserStore` の登録があることで、`AddAuthagonal()` は組み込みの Azure Table Storage の構成を行わずにスキップします。後から登録したプロバイダーは、`AddAuthagonal()` が既に埋めたインターフェイスをすべて取りこぼします。それらの登録は `TryAdd` を使っているため、エラーにもなりません。
>
> 3 つのインターフェイス (`IOrganizationStore`、`IOrganizationMembershipStore`、`IScimGroupRoleMappingStore`) には空で読み取り専用のインメモリのフォールバックがあり、これらのストアをまったく構成しないホストでも DI の解決が成功します。`AddAuthagonal()` は、ストレージプロバイダーが登録される前にこれらのフォールバックをいったん取り除き、その後 `TryAdd` で元に戻します。そのため、プロバイダーの永続ストアが常に優先され、ストアを持たないホストではフォールバックが引き続き機能します。この 3 つのいずれかについて独自の実装を登録する場合は、他のストアと同様に `AddAuthagonal()` より前に登録してください。

次に、`Program.cs` に組み込みます。

```csharp
builder.Services.AddSingleton<IAuthHook, MyAuditHook>();   // Custom hook
builder.Services.AddSingleton<IEmailService, MyEmailService>(); // Custom email
builder.Services.AddAuthagonal(builder.Configuration);

var app = builder.Build();
app.UseAuthagonal();
app.MapAuthagonalEndpoints();
app.MapFallbackToFile("index.html");
app.Run();
```

すべての上書きポイントについては [拡張性](extensibility) を、完全な例については [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server) を参照してください。

### メール {#email}

組み込みの [Resend](https://resend.com) 送信機能は、`Email:ResendApiKey` と `Email:SenderEmail` が構成されると自動的に有効になり、サービスの登録は不要です。`IEmailService` がまったくない場合、確認メールとパスワードリセットのメールは**何も通知されずに破棄されます**。また既定ではログインに確認済みのメールアドレスが必要なため、セルフ登録したユーザーはいつまでもサインインできません (`UseAuthagonal` は起動時に警告をログに出力します)。`Email:*` キーを設定するか、`AddAuthagonal()` より前に独自の `IEmailService` を登録するか、`Auth:AutoConfirmEmailDomains` にドメインを列挙して確認を省略してください (開発/テスト専用)。[構成 → メール](configuration#email) を参照してください。

## SQL バックエンド {#sql-backend}

クラウドサービスの代わりに自前のデータベースで実行するには、`Authagonal.SqlProvider` を参照し、`AddAuthagonal()` より**前**に登録します。これらの登録があることで、`AddAuthagonal()` は Azure Table Storage の構成をスキップします。

```csharp
using Authagonal.SqlProvider;

// PostgreSQL: the production self-hosted backend
builder.Services.AddAuthagonalPostgres(
    "Host=db;Database=authagonal;Username=auth;Password=…;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/db-ca.pem");

// or SQLite: one file, no server. Suits embedded hosts, CI and small single-node deployments
builder.Services.AddAuthagonalSqlite("Data Source=authagonal.db");

builder.Services.AddAuthagonal(builder.Configuration);
```

テーブルは Azure と DynamoDB のレイアウトを 1 対 1 で反映しており、存在しなければ起動時に作成されます (すべてのステートメントが `IF NOT EXISTS` なので、複数の Pod が同時に実行しても安全で、自分でプロビジョニングしたスキーマに対しては何もしません)。`Storage:*` の構成は不要です。DataProtection のキーリングは同じデータベースに永続化されるため、Cookie と偽造防止トークンは再起動後も有効で、追加のサービスなしに複数の Pod 間で機能します。

SQLite は書き込みを直列化するため、単一ノード向けのバックエンドです。既定で登録されるプロセス内のリースとクラスターイベントバスが、その場合の正しい組み合わせです。複数 Pod の PostgreSQL デプロイメントでは、リーダー選出に `clustering.UseSql(dataSource)` を使ってください。

> **照合順序。** PostgreSQL では、キー列は `COLLATE "C"` に固定されています。キーの体系は一貫してバイト順序 (プレフィックスの境界、環境パーティションの範囲、グラントの有効期限スイープ、キーセットページング) に基づいています。言語的な照合順序 (`en_US.UTF-8` や ICU ロケールがよくある既定値です) で作成されたデータベースでは、記号や大文字小文字の並び順が変わり、誤った行がエラーもなく返されてしまいます。この固定によって、レイアウトはデータベースの作成方法に左右されなくなります。データベースを特定の方法で作成する必要はありません。

> ⚠️ **鍵マテリアルはそのデータベースに保存されます。** Azure では、トークン署名鍵は Table Storage に、DataProtection のキーリングは Blob コンテナーにあり、それぞれ独立して RBAC を付与できます。AWS では DynamoDB と S3 です。SQL ではどちらも、他のすべてと同じ接続文字列の背後にあるテーブルです。そのため、接続文字列は署名鍵と同等のものとして扱ってください。そうしないと、`pg_dump`、リードレプリカ、`SELECT` 権限を持つ分析用ロール、あるいはリストアしたバックアップから、任意のサブジェクトのトークンを発行する能力と、すべての認証 Cookie を支える鍵の両方が得られてしまいます。`AddAuthagonalPostgres()` より前に `IFieldCipher` を登録して `SigningKeys.keyMaterialJson` を保存時に暗号化し、`DataProtection:KeyVaultKeyId` または `DataProtection:CertificateThumbprint` を設定してキーリングがむき出しの `<masterKey>` のまま保存されないようにしてください。どちらもなしにキーリングを永続化する新規デプロイメントは起動時に拒否され、既存のデプロイメントには起動のたびに `Critical` レベルの警告が出ます。両者の詳細と、独自のロールを持つ別のスキーマにキーリングを置く方法については [パッケージの README](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider#dataprotection-keys) を参照してください。

テーブルのレイアウト、各単一使用保証を支える並行性プリミティブ、別のエンジン用のダイアレクトの追加方法については [パッケージの README](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider) を参照してください。

## AWS バックエンド {#aws-backend}

Azure の代わりに AWS で実行するには、`Authagonal.AwsProvider` を参照し、`AddAuthagonal()` より**前**に AWS のバンドルを登録します。これらの登録があることで、`AddAuthagonal()` は Azure Table Storage の構成をスキップします。

```csharp
using Authagonal.AwsProvider;

builder.Services.AddAuthagonalAwsStorage(
    dynamoDb,                // IAmazonDynamoDB: required
    secretsManager,          // IAmazonSecretsManager: optional; replaces the plaintext ISecretProvider
    s3,                      // IAmazonS3: optional; used for DataProtection keys
    "my-auth-keys-bucket");  // S3 bucket for the DataProtection key ring
builder.Services.AddAuthagonal(builder.Configuration);
```

DynamoDB のテーブルは Azure のレイアウトを 1 対 1 で反映しており、起動時に存在が保証されます (冪等で、Terraform で既にプロビジョニングされている場合は何もしません)。資格情報は標準の AWS チェーン (環境変数 / EC2 インスタンスロール / IRSA) で解決されるため、接続文字列とマネージド ID の使い分けはなく、`Storage:*` の構成も不要です。

> ⚠️ **S3 の DataProtection 鍵。** S3 クライアントとバケットがない場合、ASP.NET Core Data Protection のキーリングはメモリ内に保持されます。開発環境の単一ノードなら問題ありませんが、本番環境では再起動時やノード間で Cookie と偽造防止トークンが機能しなくなります。本番の AWS デプロイメントでは、必ず S3 クライアントとバケットを渡してください。

## ログイン SPA (npm) {#login-spa-npm}

ログイン UI はカスタマイズ用の npm パッケージとして公開されています。

```bash
npm install @authagonal/login react react-dom react-router
```

このパッケージはコンパイル済みの JS と CSS を同梱しているので、自分の React アプリでコンポーネントとスタイルを直接インポートできます。詳しい手順は [カスタムサーバー](custom-server) を参照してください。

`react`、`react-dom`、`react-router` は **peer** 依存関係です。ビルドはこれらを外部化しているため、コンポーネントは独自のコピーではなくアプリケーション側のものを使います。これにより、エクスポートされたページはアプリの `<BrowserRouter>` の内部で `useNavigate` を呼び出せ、フックはページを描画する React インスタンスに対して実行されます。これらはパッケージと一緒にインストールし、パッケージに独自のコピーを持ち込ませないでください。

## Backend-for-Frontend (BFF) {#backend-for-frontend-bff}

SPA がベアラートークンで API を呼び出す場合は、トークンをブラウザーではなく BFF に保持してください。BFF は NuGet パッケージ (`Authagonal.Bff`) と npm パッケージ (`@authagonal/bff`) として公開されており、どちらもサーバーイメージには含まれていません。[Backend-for-Frontend](bff) を参照してください。

## 本番環境のセキュリティチェックリスト {#production-security-checklist}

Authagonal を実際のトラフィックに公開する前に、次の点を確認してください。各項目の詳細は [構成](configuration) ページにあります。

- **パッチ適用済みの .NET ランタイムで実行してください。最低でも 9.0.18 または 10.0.10 です。** GHSA-37gx-xxp4-5rgx と GHSA-w3x6-4m5h-cxqf (`System.Security.Cryptography.Xml` における無限ループと、XXE / リソース枯渇の組み合わせで、いずれも**匿名**でアクセスできる SAML ACS エンドポイントから到達可能) の修正は、Authagonal が参照できるパッケージではなく共有フレームワークに含まれています。そのため、依存関係グラフの中のどれもその修正を保証できません。実行中のランタイムが下限を下回っている場合、Authagonal は起動時に `Critical` をログに出力します。代わりに起動を拒否させるには `Auth:RequireMinimumRuntime = true` を設定してください。公開されているコンテナーイメージは、既に下限以上のランタイムを使っています。
- **TLS 終端プロキシの背後で実行し、そのことを宣言してください。** Authagonal は TLS を終端するリバースプロキシ / イングレスの背後に置く (または自身で TLS を終端する) 必要があります。HSTS は HTTPS でのみ出力され、`/connect/*` は平文を拒否するため、プロキシは `X-Forwarded-Proto: https` を転送しなければなりません。また、`ForwardedHeaders:KnownNetworks` (または `KnownProxies`) にプロキシの CIDR / アドレスを設定しない限り、このヘッダーは無視されます。プロキシに固定アドレスがなく、他に何もプロセスに到達できない場合は `["0.0.0.0/0", "::/0"]` を使ってください。`ForwardedHeaders:ForwardLimit` の既定値は `1` です (最後のホップだけを信頼します)。
- **`SecretProvider:VaultUri` を設定してください。** 既定のシークレットプロバイダーは**平文**です。Key Vault がないと、上流の OIDC クライアントシークレットと TOTP / MFA のシードは Table Storage に (そしてバックアップにも) 平文で保存されます。本番環境のデプロイメントでは必ず Key Vault を構成してください。
- **管理 API を保護してください。** `AdminApi:Enabled` の既定値は **true** です。管理スコープ (`AdminApi:Scope`、既定値 `authagonal-admin`) は、完全な管理権限とユーザーのなりすまし (インパーソネーション) を許可します。`/api/v1/*` の管理ルートへのネットワークアクセスを制限し、管理スコープを発行する相手を厳しく管理するか、使わない場合は `AdminApi:Enabled = false` を設定してください。
- **内部エンドポイントを保護してください。** `Cluster:Secret` を設定すると、内部の `/_internal/backchannel-logout` エンドポイントは `X-Cluster-Secret` ヘッダーを要求します (定数時間で比較されます)。シークレットがない場合、このエンドポイントは**誰も**認可せず 404 を返します。送信元アドレスは資格情報ではなく、同一ホストのリバースプロキシは転送するすべてのリクエストでループバックとして現れるからです。`Cluster:AllowLoopbackWithoutSecret` は、ローカル開発に限り、転送前のピアがループバックであれば再び受け入れるようにします。出荷時の製品にはこのエンドポイントを呼び出すものがないため、閉じた状態で失敗してもファーストパーティーのフローは壊れません。このエンドポイントの上に Pod 間の独自のファンアウトを構築する場合は、シークレットを設定してください。
- **バックアップを暗号化してください。** 平文のシークレットプロバイダーを使っている場合、バックアップにはシークレットが含まれます。`SigningKeys` テーブルは既定でバックアップから除外されています。`Backup:IncludeSigningKeys` でオプトインする場合は、バックアップ先を保存時に暗号化しなければなりません。[バックアップとリストア](backup-restore) を参照してください。

## 移行ツール {#migration-tool}

Duende IdentityServer + SQL Server からの移行用です。

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

詳細は [移行](migration) を参照してください。
