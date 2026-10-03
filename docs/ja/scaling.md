---
layout: default
title: スケーリング
locale: ja
---

# スケーリング

Authagonal は、特別な設定なしで垂直方向と水平方向の両方にスケールするよう設計されています。

## ステートレスな設計 {#stateless-by-design}

永続的な状態はすべて、バッキングストア (Azure Table Storage、AWS バックエンドでは DynamoDB、セルフホストの SQL バックエンドでは PostgreSQL) に保存されます。スティッキーセッションやインスタンス間の調整を必要とするプロセス内の状態はありません。

- **署名鍵**: Table Storage から読み込まれ、1 時間ごとに更新されます
- **認可コードとリフレッシュトークン**: 1 回限りの使用を強制したうえで Table Storage に保存されます
- **SAML のリプレイ防止**: リクエスト ID が Table Storage で追跡され、アトミックに削除されます
- **OIDC の state と PKCE の検証子**: Table Storage に保存されます
- **クライアントとプロバイダーの設定**: リクエストごとに Table Storage から取得されます

## Cookie の暗号化 (データ保護) {#cookie-encryption-data-protection}

認証 Cookie は ASP.NET Core のデータ保護キーリングで保護されるため、すべてのインスタンスが 1 つのキーリングを共有する必要があります。キーリングは次の順序で自動的に永続化されます。

1. `DataProtection:BlobUri` が設定されていれば、それを使います (明示的な BLOB で、`DefaultAzureCredential` で認証されます)。
2. `Storage:ConnectionString` で指定されたアカウント内の `dataprotection` コンテナー。ただし、それが Azurite の場合は除きます。
3. マネージド ID の経路 (`Storage:TableServiceUri`) では、同じアカウントの対応する BLOB エンドポイント `https://{account}.blob.…/dataprotection/keys.xml`。ID には、そのアカウントに対する Storage Blob Data Contributor が必要です。

認識できないテーブルエンドポイント (Azurite、パス形式のエミュレーター) の場合に限り、マシンごとのファイルストアにフォールバックします。これは一時的で Pod ごとのストアなので、再起動するとすべてのユーザーがサインアウトされ、レプリカは互いの Cookie を読み取れません。そうなった場合、起動時のチェックが `Critical` をログに出力します。

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

AWS バックエンドでは、キーリングを S3 に永続化するために `AddAuthagonalAwsStorage` に S3 クライアントとバケットを渡します。渡さない場合、キーリングはメモリ内に置かれ、再起動時やノード間で Cookie が機能しなくなります。[インストール → AWS バックエンド](installation#aws-backend)を参照してください。SQL バックエンドでは、キーリングは `AddAuthagonalPostgres` / `AddAuthagonalSqlite` によって永続化されます。

永続化は暗号化ではありません。`DataProtection:KeyVaultKeyId` または `DataProtection:CertificateThumbprint` が設定されていない限り、キーリングは平文の XML です。起動時、暗号化がなくまだ鍵もないキーリングは拒否され、すでに鍵があるキーリングは `Critical` のログを出力して起動します (`DataProtection:AllowUnencryptedKeyRing=true` を設定すると、意図的にそれを受け入れます)。`DataProtection:*` の一覧は[構成](configuration)を参照してください。

## インスタンスごとのキャッシュ {#per-instance-caches}

読み取りが多く変化の遅い少数の値は、Table Storage への往復を減らすために、インスタンスごとにメモリ内でキャッシュされます。

| データ | キャッシュ期間 | 古い値による影響 |
|---|---|---|
| OIDC ディスカバリドキュメント | 60 分 (設定可能) | IdP の鍵ローテーションの把握が遅れます |
| SAML IdP メタデータ | 60 分 (設定可能) | 同上 |
| CORS で許可されたオリジン | 60 分 (設定可能) | 新しいオリジンが反映されるまで最大 1 時間かかります |

これらのキャッシュは、本番環境での使用に問題ありません。期間はすべて `Cache` 設定セクションで変更できます。[構成](configuration)を参照してください。即座に反映させる必要がある場合は、該当するインスタンスを再起動してください。

## レート制限 {#rate-limiting}

悪用されやすいエンドポイント (IP ごとの登録、対象メールアドレスごとのパスワードリセット、クライアントごとの SCIM、IP ごとの動的クライアント登録。[構成 → レート制限](configuration#rate-limiting)を参照) は、組み込みのレートリミッターで保護されています。

既定では、制限は `IRateLimiter` の拡張点の背後で**ノードごとにプロセス内で**適用されます。そのため N 個のインスタンスがあると、実効的な上限は設定値の N 倍になります。これは意図的なものです。このリミッターは単一ノードに対する暴走的な悪用への最後の備えであり、権威あるグローバルな制限は、負荷分散される前にすべてのトラフィックを確認できるエッジ (WAF / イングレス / CDN) に置くべきだからです。

このトレードオフは量の制限には適切ですが、1 つのケースには不適切です。**推測可能なシークレット**を守るための予算です。デバイスフローの `user_code` は小さなアルファベットからなる短い文字列で、攻撃者と、有効なセッションを与えるコードとの間にあるのは試行回数の制限だけです。レプリカ数に応じて増える上限はそこでは不適切な形であり、実際の上限がサーバーではなくイングレスの設定に依存するようになってしまいます。

**`Auth:DurableRateLimiting=true`** を設定すると、カウンターがすでに運用しているストアに移り、すべてのレプリカが 1 つの予算を共有します。レート制限のチェックごとにストアへの往復が発生し、固定ウィンドウを使い (予算 N はウィンドウの境界をまたぐと最大 2N まで許容します)、ストアに到達できない場合はフェイルオープンになり、制限せずに通します。そのため、エッジのルールを置き換えるのではなく、その上に重ねるものです。カウンターの行は、3 つのバックエンドすべてで自動的に回収されます。[構成 → クラスター全体の制限](configuration#cluster-wide-limits-authdurableratelimiting)を参照してください。

## クラスタリング {#clustering}

複数のインスタンスは、**リーダー選出**と**ノード間イベントバス**を通じて連携します。どちらも差し替え可能なバックエンドの背後にあります。

- **リーダー選出**: リースベースの選出です (`Cluster:LeaseTtlSeconds`、既定値 30 秒で、その約半分の間隔で更新されます)。リースを保持するノードは常に 1 つだけで、リーダーが停止するとリーダーシップは自動的に移ります。リーダーに限定された処理はリーダー上でのみ実行されます。有効期限での署名鍵の*無効化* (`Auth:KeyRotationEnabled` が有効な場合)、グラントの整合性回復の一掃処理 (Azure バックエンドのみ)、保存データのバックフィル (`Auth:AtRestBackfillEnabled` が有効な場合。リーダーでないノードはリーダーシップを短時間待ってから省略します)、そしてレート制限カウンターの一掃処理 (`Auth:DurableRateLimiting` を使う Azure バックエンド) です。`Cluster:Enabled=false` の場合、単一ノードが恒久的なリーダーになるため、単独のデプロイでもこれらはすべて実行されます。
- **イベントバス**: ノード間の通知 (マルチテナントのホストでのキャッシュの無効化など) で、`Cluster:PollIntervalSeconds` (既定値 3 秒) ごとにポーリングされます。

各インスタンスは、自身を識別するために起動時にランダムな 16 進数 12 文字のノード ID を生成します。これは永続化されません。

### バックエンド {#backends}

**既定はプロセス内**です。単一ノードは常に自身がリーダーであり、イベントはローカルにとどまります。1 つのインスタンスであれば、設定なしでこれが正しい動作です。複数ノードのデプロイでは、`AddAuthagonal` の `configureClustering` コールバックで実際のバックエンドに差し替えます。

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS: leadership + event bus via DynamoDB (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// PostgreSQL: leadership via a conditional-upsert lease row, event bus via an
// append-only log in the same database (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` はイベントバスだけを登録し、プロセス内の (常にリーダーである) リースを維持します。クラスターのイベントを受け取る必要があるものの、リーダーシップを決して争ってはならないノードで使ってください。

> **注:** 複数ノードでプロセス内の既定を使うと、*すべての*ノードが自身をリーダーだと認識します。ほとんどのワークロードでは問題ありませんが、複数のインスタンスで `Auth:KeyRotationEnabled` を有効にする前に、実際のリースのバックエンドを有効にしてください。

署名鍵の**生成**は、このリーダーに限定された無効化とは別のもので、それによって駆動されるわけではありません。すべてのノードが起動時と `Auth:SigningKeyCacheRefreshMinutes` による更新のたびに `EnsureActiveKeyAsync` を呼び出すため、`KeyRotationEnabled` が無効 (既定) の場合、90 日の有効期限でのロールオーバーは完全にこの経路によって行われます。生成は独自の短いクラスターリースを取得するため、実際のリースのバックエンドが設定されていれば、書き込むのは常に 1 つのノードだけです。複数ノードでプロセス内の既定を使う場合はそうした調整がなく、期限切れの鍵に同時に到達した 2 つのノードがそれぞれ鍵を生成する可能性があります。両方とも JWKS に含まれ、どちらで署名されたトークンも検証できますが、どの鍵がアクティブと報告されるかが揺れ動くことがあります。これも、複数ノードのデプロイで実際のリースのバックエンドを設定すべき理由の 1 つです。

クラスターの設定の一覧は[構成](configuration#cluster)ページを参照してください。

### マルチテナントのデプロイ {#multi-tenant-deployments}

マルチテナントモード (`AddAuthagonalCore()`) では、`TokenCleanupService`、`GrantReconciliationService`、`SigningKeyRotationService`、および設定の初期投入サービス (クライアント、プロバイダー、スコープ、ロール) は登録されません。これらはシングルテナントの `AddAuthagonal()` の構成に含まれるもので、その処理はホストがテナントごとに管理します。

## 名前インデックスのホットパーティション {#name-index-hot-partition}

管理画面での名前の前方一致検索は、`UserFirstNames` / `UserLastNames` インデックステーブルに基づいており、これらは**単一のホットパーティション**を使います。大規模になると、インデックスへの書き込みのスループットは毎秒約 2,000 操作に制限され、高負荷時にユーザーの作成/更新のボトルネックになることがあります。管理画面での名前検索を公開しない場合は、`Storage:NameIndexesEnabled = false` を設定してこれらの書き込みを完全に省略してください。[構成](configuration)を参照してください。

## 信頼済みプロキシと内部エンドポイント {#trusted-proxy-and-internal-endpoints}

ロードバランサーの背後で複数のインスタンスを実行する場合:

- **転送ヘッダー**: レート制限とロックアウトは、`X-Forwarded-For` から解決されたクライアント IP をキーにします。インスタンス間でクライアント IP を偽装できないよう、`ForwardedHeaders:KnownNetworks` にイングレス / Pod の CIDR を設定してください。`ForwardedHeaders:ForwardLimit` の既定値は `1` です。[構成](configuration#forwarded-headers-trusted-proxy)を参照してください。
- **内部エンドポイント**: `/_internal/backchannel-logout` は、`X-Cluster-Secret` ヘッダーに `Cluster:Secret` を必要とします (定数時間で比較されます)。それがない場合、エンドポイントは誰も認可せず 404 を返します。送信元 IP は資格情報として扱われません。同じホスト上のリバースプロキシは転送するすべてのリクエストでループバックを示しますし、プライベート範囲は共有クラスターネットワーク内のすべての隣接ワークロードだからです。`Cluster:AllowLoopbackWithoutSecret` は、転送前のループバックのピアを再び受け入れる開発専用のオプトインです。製品自体がこのルートを呼び出すことはない (セッションの展開は `SessionTermination` によってプロセス内で行われる) ため、これが関係するのは自分で構築した展開の仕組みだけです。

## スケーリングの推奨事項 {#scaling-recommendations}

**垂直スケーリング**: 単一インスタンスの CPU とメモリを増やします。インスタンスあたりの同時リクエストを増やすのに役立ちます。

**水平スケーリング**: ロードバランサーの背後で複数のインスタンスを実行します。スティッキーセッションや共有キャッシュは不要です。各インスタンスは完全に独立しています。

**ゼロへのスケール**: Authagonal はゼロへのスケールに対応したデプロイ (たとえば `minReplicas: 0` の Azure Container Apps) をサポートしています。アイドル後の最初のリクエストでは、.NET ランタイムが初期化され、署名鍵がストレージから読み込まれる間、数秒のコールドスタートが発生します。
