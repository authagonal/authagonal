---
layout: default
title: 構成
locale: ja
---

# 構成

Authagonal は `appsettings.json` または環境変数で構成します。環境変数ではセクションの区切りとして `__` を使います (例: `Storage__ConnectionString`)。

## 必須の設定 {#required-settings}

ストレージは 2 つの方法のいずれかで構成できます。`Storage:ConnectionString` **または** `Storage:TableServiceUri` (マネージド ID を使う方法で、本番環境ではこちらを推奨) の**どちらか**を指定してください。

| 設定 | 環境変数 | 説明 |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | アカウントキーを含む Azure Table Storage の接続文字列。開発環境や Azurite に適しています。 |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | マネージド ID を使う Table Storage のエンドポイント。例: `https://{account}.table.core.windows.net/`。`Storage:ConnectionString` の代わりとなるもので、**本番環境ではこちらを推奨します**。`DefaultAzureCredential` で認証するため、アクセスキーがシークレットに入ることは一切ありません。ホストはワークロード ID に **Storage Table Data Contributor** ロールを付与する必要があります。 |
| `Issuer` | `Issuer` | このサーバーの公開ベース URL (例: `https://auth.example.com`) |

## ストレージ {#storage}

| 設定 | 環境変数 | 既定値 | 説明 |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *(なし)* | アカウントキーを含む接続文字列 (必須の設定を参照)。 |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *(なし)* | マネージド ID を使う Table Storage の URI (必須の設定を参照)。両方が設定されている場合は、`Storage:ConnectionString` より優先されます。 |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | 管理機能での名前の前方一致検索を支える、`UserFirstNames` / `UserLastNames` の前方一致検索用インデックステーブルを維持するかどうか。管理機能で名前の検索を公開しないホストでは、`false` に設定してそれらの書き込みを省略できます。**スケーリングに関する注意:** これらのインデックスは単一のホットパーティションを使うため、大規模な環境ではスループットがおよそ 2,000 ops/秒に制限されます。名前の検索が不要であれば無効にしてください。 |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | `/connect/authorize` エンドポイントがログイン SPA (ログイン、ステップアップ、同意の各画面) のためにリダイレクトするベース URL。ログイン UI をサーバーとは別のオリジンから提供する場合に設定します。既定値は、同梱の SPA が提供する相対パス `/login` です。 |

## 認証 {#authentication}

| 設定 | 既定値 | 説明 |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Cookie セッションの有効期間 (スライディング) |
| `Authentication:AllowInsecureCookie` | `false` | セッション Cookie を平文の http で送信できるようにします (`Always` ではなく `SameAsRequest`)。**開発環境専用です。** Cookie はセッションそのものであり、`SameAsRequest` が同等に見えるのは TLS を終端するプロキシの背後にある場合だけです。それは `X-Forwarded-Proto` が届き、信頼されることに依存しているため、構成を誤ったイングレス、平文の HTTP で行われるヘルスプローブ、ヘッダーを落とすプロキシのいずれかがあると、Secure の付かない Cookie が生成され、それが同じホストへの平文のリクエストに乗って送られます。この失敗は表面化しません。 |
| `Authentication:CookieDomain` | *(未設定)* | セッション Cookie のスコープを親ドメインにし、兄弟関係にあるサブドメイン (`auth.example.com` だけでなく `app.example.com` も) に送信されるようにします。**これによりオリジンへの結び付けが失われます。** Cookie には `__Host-` プレフィックスを付けられなくなります。このプレフィックスこそが、Secure で、`Path=/` で、`Domain` を持たない場合を除いてブラウザーに Cookie を拒否させるものです。そのため、親ドメインに Cookie を設定できるあらゆるサブドメインと、そのいずれかを乗っ取れるあらゆるものが対象範囲に入ります。兄弟のオリジンが本当にセッションを必要とするのでない限り、未設定のままにしてください。 |
| `Auth:AllowInsecureHttp` | `false` | OAuth のエンドポイント (`/connect/*`) が平文の http のリクエストに応答できるようにします。**開発環境専用です。** RFC 6749 §3.1/§3.2 は認可エンドポイントとトークンエンドポイントで TLS を要求しているため、既定では、そのいずれかへの https 以外のリクエストは `invalid_request` で拒否されます。スキームは転送ヘッダーの処理の*後に*評価されるため、TLS を終端して `X-Forwarded-Proto: https` を転送するプロキシは、この設定がオフのままでもこのチェックを通過します。ただし、そのプロキシが [`ForwardedHeaders:KnownNetworks` / `KnownProxies`](#the-two-headers-are-not-trusted-on-the-same-terms) で宣言されている場合に限ります。宣言されていなければ、ヘッダーは無視されます。これが必要なのは本当に平文で動作するデプロイメント (同梱の `docker-compose.yml`、カスタムサーバーのデモ) だけで、これがオンの場合、サーバーは起動時に警告をログに記録します。`AuthagonalProtocolOptions.AllowInsecureHttp` にも反映されるため、`Authagonal.Protocol` が所有するエンドポイントも制御します ([拡張性](extensibility#embedding-authagonalprotocol-alone) を参照)。 |
| `Auth:RequireMinimumRuntime` | `false` | .NET 共有フレームワークが Authagonal の必要とするセキュリティ上の最低バージョン (**9.0.18 / 10.0.10**) より古い場合に、起動を拒否します。この最低バージョンがあるのは、GHSA-37gx-xxp4-5rgx と GHSA-w3x6-4m5h-cxqf (`System.Security.Cryptography.Xml` における無限ループと、XXE / リソース枯渇の組み合わせで、どちらも**匿名の** SAML ACS エンドポイントから到達可能) の修正が、このライブラリが固定できるパッケージではなくランタイムに含まれているためで、あなたのどの依存関係もそれを保証できません。`false` のままの場合、古いランタイムは `Critical` のログになり、サーバーは起動します。既定で拒否すると、ランタイムが 1 パッチ遅れているフリートでは、Authagonal のバージョンアップが障害につながってしまうからです。パッチが適用されていないランタイムで認証されていない XML を処理するよりも、起動しないほうが望ましい環境では `true` に設定してください。 |
| `Auth:MaxFailedAttempts` | `5` | アカウントがロックアウトされるまでのログイン失敗の回数 |
| `Auth:LockoutDurationMinutes` | `10` | 失敗の上限に達した後のアカウントのロックアウト期間 |
| `Auth:MaxLoginAttemptsPerIp` | `30` | `Auth:LoginWindowMinutes` あたりに送信元アドレスごとに許可されるパスワードの試行回数で、(別途) 同じ期間内に送信されたメールアドレスごとの回数でもあります。アカウントごとのロックアウトではスプレー攻撃 (数千のアカウントに 1 回ずつ試行する) を抑えられず、認証されていない試行はそれぞれ完全な PBKDF2 の計算を伴うため、これによって両方を抑えます。超過すると `429 too_many_attempts` を返します (`AuthEndpoints.cs:107-119`)。 |
| `Auth:LoginWindowMinutes` | `5` | `Auth:MaxLoginAttemptsPerIp` の期間 |
| `Auth:MaxRegistrationsPerIp` | `5` | 期間内の IP アドレスごとの登録の最大数 |
| `Auth:RegistrationWindowMinutes` | `60` | 登録のレート制限の期間 |
| `Auth:MaxPasswordResetsPerEmail` | `3` | 期間内の送信先アドレスごとのパスワードリセットメールの最大数 (呼び出し元の IP ではなくメールアドレスをキーとするため、1 つのアドレスにメールを大量に送り付けることはできません) |
| `Auth:MaxPasswordResetsPerIp` | `15` | 期間内の送信元 IP ごとのパスワード忘れリクエストの最大数。メールアドレスごとの上限は 1 人の被害者へのメールを抑えるもので、こちらはアドレスの一覧を順に試す呼び出し元を抑えます。そうしなければ、検証済みの送信ドメインからの匿名のメールが無制限に送られ、アドレスごとにストアの読み取りも発生します。 |
| `Auth:PasswordResetWindowMinutes` | `60` | パスワードリセットのレート制限の期間 |
| `Auth:DurableRateLimiting` | `false` | 各ノードが独自のカウンターを持つのではなく、レート制限のカウンターを構成済みのストアに保持し、すべてのレプリカが 1 つの上限を共有するようにします。チェックごとにストアとのラウンドトリップが発生し、単一ノードのデプロイメントでは得るものはありません。`IRateLimitCounterStore` を提供するプロバイダー (Azure、SQL、AWS) が必要です。そうでない場合、ホストは黙ってノードごとの制限に戻るのではなく、起動を拒否します。[クラスター全体の制限](#cluster-wide-limits-authdurableratelimiting) を参照してください。 |
| `Auth:AutoConfirmEmailDomains` | *(空)* | セルフサービスでの登録が自動的に確認済みになり、確認メールが省略されるメールドメイン (文字列の配列)。空 (既定) の場合、すべての登録で確認が必要です。開発環境とテスト環境のみを想定しています。実際のメールを受信できるドメインは決して列挙しないでください。 |
| `Auth:AllowPasswordlessAccountClaim` | `false` | **ローカルの資格情報を持たない** (フェデレーションまたは JIT でプロビジョニングされた) 既存のアカウントに属するメールアドレスで登録すると、列挙に対して中立な重複時のレスポンスを返す代わりに、そのアカウントにパスワードを仮に設定します。仮に設定された資格情報と属性は、申請者が新しい確認メールのリンクをクリックするまで効力を持たないため、フェデレーションのアカウントのメールアドレスを知っているだけでは乗っ取ることはできません。既にパスワードを持つアカウントが影響を受けることはありません。[ユーザーのアップグレード](user-upgrade) を参照してください。 |
| `Auth:ClaimAllowedAttributeKeys` | *(空)* | パスワードを持たないアカウントの申請で、登録リクエストから申請対象のアカウントに持ち込めるカスタム属性のキー。空の場合はすべてのキーを許可します (後方互換性のため)。申請によって下流のプロビジョニングやトークンに注入できるものを制限するには、キーを列挙してください。 |
| `Auth:EmailVerificationExpiryHours` | `24` | メールアドレスの確認リンクの有効期間 |
| `Auth:PasswordResetExpiryMinutes` | `60` | パスワードリセットリンクの有効期間 |
| `Auth:MfaChallengeExpiryMinutes` | `5` | MFA チャレンジのトークンの有効期間 |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | MFA セットアップトークンの有効期間 (強制的な登録用) |
| `Auth:WebAuthnAllowedHosts` | *(空)* | WebAuthn のリライングパーティーとして動作することを許可するホスト。空の場合はあらゆるホストを受け入れ (既存のデプロイメントは引き続き動作します)、これは穴になります。そうでない場合、RP ID と期待されるオリジンは、検証対象のリクエストから導き出されるからです。マルチテナントのデプロイメントでは、すべてのテナントのホストを列挙してください。[MFA](mfa) を参照してください。 |
| `Auth:Pbkdf2Iterations` | `100000` | パスワードのハッシュ化における PBKDF2 の反復回数 |
| `Auth:FailedLoginMinimumMilliseconds` | `250` | ログインに失敗したとき、`invalid_credentials` を返すまで待たせる実時間の下限で、リクエストの開始から計測されます。ユーザーの列挙につながるタイミングのオラクルを塞ぎます。存在しないアカウントはネイティブの PBKDF2 形式のダミーのハッシュに対して検証されますが、実在するアカウントは、インポートされた bcrypt、Scrypt.NET、ASP.NET Identity V3 のハッシュを異なるコストで保持している可能性があるため、作業量を等しくすることは不可能で、代わりに経過時間を等しくしています。デプロイメントが保持する最も遅いハッシュよりも大きな値にしてください。例えば、コスト 11 を超える bcrypt や、高い `N` の Scrypt.NET の `$s2$` ハッシュをインポートした場合や、`Pbkdf2Iterations` を既定値より大幅に上げた場合です。失敗したログインが初めてこの時間を超えたときに、警告が 1 回ログに記録されます。`0` にすると待機が無効になり、オラクルが再び開きます。 |
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | リフレッシュトークンの同時再利用に対するオプトインの猶予期間 (秒)。`0` (既定) では厳格な姿勢を維持し、消費済みのリフレッシュトークンが再利用されると、そのユーザーとクライアントのすべてのトークンが失効します。`> 0` に設定すると、期間内の再利用を冪等な再試行として扱い (後続のトークンを再送します)、接続が不安定なモバイルクライアントに役立ちます。 |
| `Auth:DynamicClientRegistrationEnabled` | `false` | 動的クライアント登録のエンドポイント `POST /connect/register` (RFC 7591) を有効にします。マルチテナントのデプロイメントではオープンな登録が悪用され得るため、既定ではオフです。[動的クライアント登録](client-registration) を参照してください。 |
| `Auth:DynamicClientRegistrationScopes` | *(空)* | 匿名の登録者が自分自身に割り当てられるスコープで、常に登録可能な OIDC の組み込みスコープ (`openid`、`profile`、`email`、`phone`、`offline_access`) に追加されます。空の場合は組み込みスコープだけです。ストアにスコープが存在しても、自己登録したクライアントがそれを宣言してよいことにはなりません。ロールで制限されたスコープは、これにかかわらず決して登録できません。[動的クライアント登録](client-registration) を参照してください。 |
| `Auth:SigningKeyLifetimeDays` | `90` | 自動ローテーションまでの署名鍵の有効期間 (鍵は ES256 / P-256) |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | 署名鍵をストレージから再読み込みする頻度 |
| `Auth:KeyRotationEnabled` | `false` | 署名鍵の自動ローテーションを有効にします |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | 有効な鍵のローテーションが必要かどうかを確認する頻度 |
| `Auth:KeyRotationLeadTimeDays` | `14` | 有効な鍵がこの日数以内に期限切れになる場合にローテーションします |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Cookie のセキュリティスタンプを確認する間隔 |
| `Auth:AllowedInternalTargets` | *(空)* | **あなた**が URL を指定した経路 (上流の SAML メタデータ、上流の OIDC ディスカバリー、プロビジョニングのコールバック) で、Authagonal が取得を行ってよい内部の宛先。空の場合、すべての内部アドレスが拒否されます。[外部への取得](#outbound-fetches-ssrf-guard) を参照してください。 |
| `Auth:AllowOutboundProxy` | `false` | オペレーターが構成したそれらの取得を、環境の HTTP プロキシ経由で送信します。アドレスのチェックがプロキシの先を見通せないことを受け入れたうえで使います。クライアントが登録した `jwks_uri` やバックチャネルログアウトの URI には決して適用されません。[外部への取得](#outbound-fetches-ssrf-guard) を参照してください。 |
| `Auth:AtRestBackfillEnabled` | `false` | 起動時に 1 回、クラスターのリーダーで保存データのバックフィルを実行します。既存のすべてのユーザー行と、プロファイルから導かれるインデックス行を現在の保存時の方式で書き直します。これは、既にデータを持つデプロイメントで `IFieldCipher` / `IIndexTokenizer` を有効にするための移行手段です ([拡張性](extensibility#pii-field-encryption-ifieldcipher) を参照)。暗号を登録しただけでは、その後に書き込まれる行しか暗号化されません。実際に大量の書き込みが発生し、冪等で、プロセスごとに 1 回実行されるため、ログに完了が報告されたらオフにしてください。 |
| `Auth:MaxScimGroupsPerClient` | `5000` | 1 つのプロビジョニングクライアントが所有できる SCIM グループの最大数。これを超える作成は拒否されます。グループのストレージにはインデックスがないため、テーブルが無制限に大きくなると、トークンの発行のたびにそのコストを払うことになります。 |
| `Auth:MaxScimGroupMembers` | `10000` | 1 つの SCIM グループが持てるメンバーの最大数。これを超える作成、置換、パッチは拒否されます。 |

## データ保護 {#data-protection}

ASP.NET Core のデータ保護キー (セッション Cookie を暗号化するもの) はインスタンス間で共有する必要があります。[スケーリング](scaling#cookie-encryption-data-protection) を参照してください。永続化の方法は、優先順に次のとおりです。

| 設定 | 既定値 | 説明 |
|---|---|---|
| `DataProtection:BlobUri` | *(なし)* | キーリングを保存する Azure Blob の明示的な URI (例: `https://{account}.blob.core.windows.net/dataprotection/keys.xml`)。`DefaultAzureCredential` で認証します。`Storage:TableServiceUri` と並んで、本番環境で推奨される方法です。 |
| *(フォールバック)* | *(なし)* | `DataProtection:BlobUri` が未設定の場合、キーリングは自動的に永続化されます。保存先は、`Storage:ConnectionString` で指定されたアカウントの `dataprotection` コンテナー (Azurite の場合を除く)、またはマネージド ID を使う方法では `Storage:TableServiceUri` から導かれる Blob エンドポイント (`https://{account}.table.…` → `https://{account}.blob.…/dataprotection/keys.xml`) で、後者には同じアカウントの Storage Blob Data Contributor が必要です。認識できないテーブルのエンドポイント (Azurite、パス形式のエミュレーター) の場合に限り、マシンごとのファイルストアにフォールバックします。これは一時的で Pod ごとのものであり、その場合は `KeyRingStartupCheck` が Critical をログに記録します。 |

AWS バックエンドでは、S3 クライアントとバケットを `AddAuthagonalAwsStorage` に渡すと、キーリングが S3 に永続化されます。[インストール → AWS バックエンド](installation#aws-backend) を参照してください。SQL バックエンドでは、キーリングは `AddAuthagonalPostgres` / `AddAuthagonalSqlite` によって永続化されます。[インストール → SQL バックエンド](installation#sql-backend) を参照してください。

永続化は暗号化ではありません。どのバックエンドがキーリングを保持していても、次のいずれかが設定されていない限り、キーリングは (マスターキーを含めて) 平文の XML として書き込まれます。キーリングは認証 Cookie を保護するものなので、ストアを読み取れることは、任意のユーザーのセッションを偽造できることを意味します。

| 設定 | 既定値 | 説明 |
|---|---|---|
| `DataProtection:KeyVaultKeyId` | *(なし)* | キーリングをラップするために使う Azure Key Vault のキーの URI。`DefaultAzureCredential` で認証します。 |
| `DataProtection:CertificateThumbprint` | *(なし)* | キーリングをラップするために使う、マシンストア内の証明書の拇印。 |
| `DataProtection:AllowUnencryptedKeyRing` | `false` | 平文のキーリングを意図的に受け入れます。構成ファイルの中だけでなく監査で目に付くよう、起動のたびに `Critical` で改めて記録されます。 |

起動時のチェックは*解決された*キーリングのオプションに基づいて行われるため、Azure、AWS、SQL、およびホストが登録したあらゆるリポジトリに同じように適用されます。暗号化なしでキーリングを永続化し、**まだキーがない**デプロイメントは拒否されるため、安全でない状態が作られることはありません。キーリングに**既にキーがある**デプロイメントは起動し、`Critical` をログに記録します。そこで拒否すると、バージョンアップによって稼働中のデプロイメントが停止してしまうからです。開発環境では拒否されることはありません。

## キャッシュとタイムアウト {#cache-and-timeouts}

| 設定 | 既定値 | 説明 |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | CORS で許可されたオリジンをキャッシュする期間 |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | OIDC ディスカバリードキュメントのキャッシュ期間 |
| `Cache:SamlMetadataCacheMinutes` | `60` | SAML IdP メタデータのキャッシュ期間 |
| `Cache:OidcStateLifetimeMinutes` | `10` | OIDC の認可における state パラメーターの有効期間 |
| `Cache:SamlReplayLifetimeMinutes` | `10` | SAML AuthnRequest ID の有効期間 (リプレイ防止) |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Table Storage のヘルスチェックのタイムアウト |
| `Cache:HealthCheckCacheSeconds` | `5` | ストレージに再度問い合わせるまで `/health` の応答を再利用する期間 (エンドポイントが公開する `Cache-Control: max-age` と一致します)。`0` にするとリクエストごとに確認するため、キャッシュが塞いでいる匿名の増幅攻撃が再び可能になります。 |

## バックグラウンドサービス {#background-services}

| 設定 | 既定値 | 説明 |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | 期限切れトークンの最初のクリーンアップまでの初期遅延 |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | 期限切れトークンのクリーンアップの間隔 |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | グラントの最初の整合処理までの初期遅延 |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | グラントの整合処理の間隔 |

### 有効期限切れの掃除 (Azure Table) {#expiry-sweeps-azure-table}

Azure Table Storage には TTL がないため、Azure バックエンドでは、サーバーが `MfaChallenges`、`RevokedTokens`、`UpstreamRefreshTokens` について、テーブルごとに `TableExpirySweepService` を (15 分ごとに、クラスターのリーダーでのみ) 実行し、有効期限を過ぎた行を削除します。これは保持期間の管理のためだけのものです。これらの行はいずれも、読み取り時にそれぞれの有効期限のチェックによって既に拒否されます。有効期限が記載されていない行 (`UpstreamRefreshTokens` ではあり得ます) は、意図的に決して削除されません。DynamoDB と SQL は、同じ 3 つのテーブルをネイティブに回収します。構成は不要です。

## ボット対策 (Cloudflare Turnstile) {#bot-protection-cloudflare-turnstile}

オプトインです。シークレットキーが設定されている場合、ログイン、登録、パスワード忘れ、パスワードのリセットは、何らかの処理を行う前に `turnstileToken` を Cloudflare に照らして検証します。シークレットキーがない場合は何も変わらず、ウィジェットも表示されません。

| 設定 | 既定値 | 説明 |
|---|---|---|
| `Turnstile:SiteKey` | *(未設定)* | 公開サイトキー。ウィジェットを表示できるよう、ログイン UI に渡されます (`GET /api/auth/providers` の `turnstileSiteKey`) |
| `Turnstile:SecretKey` | *(未設定)* | サーバー側の検証に使うシークレット。未設定または空の場合、Turnstile は完全に無効になります |

顧客が用意したドメインを提供するホストは、1 つのキーのペアを使えません (Cloudflare がウィジェットのホスト名の数を制限しているため)。その場合は [`ITurnstileKeyProvider`](extensibility#iturnstilekeyprovider) を置き換えます。`captcha_failed` エラーについては [認証 API](auth-api#providers) を参照してください。

## ロール {#roles}

ロールは `Roles` 配列で定義し、クライアント、スコープ、プロバイダーと一緒に起動時に初期投入されます。
初期投入が最も重要になるのは、スコープが [`AllowedRoles`](scopes#role-gated-scopes) で制限されている場合です。
何も作成しないロールで制限されたスコープは、それを構成したオペレーターを含む全員に対して制限されることになり、
しかもその失敗は表面化しません。そのスコープが単に一度も付与されないだけです。

```json
{
  "Roles": [
    {
      "Name": "staff-admin",
      "Description": "Internal staff console",
      "Members": [ "ada@example.com", "grace@example.com" ]
    }
  ]
}
```

| フィールド | 説明 |
|---|---|
| `Name` | ロール名。`Scope.AllowedRoles` と `roles` トークンクレームで使われるものです |
| `Description` | 人が読むための説明。初期投入のデータに記載があれば、以降の起動時に更新されます |
| `Members` | 起動のたびにそのロールに入れられるメールアドレス。まだユーザーが存在しないアドレスは警告とともにスキップされ、次の起動時に再試行されます。そのため、まだ誰も作成していないアカウントに起動が依存することはありません |

初期投入は**追加のみで冪等**です。ロールを削除したり、メンバーシップを取り消したりすることは決してありません。
構成は誰が何を持っているかの記録のシステムではないため、管理 API を通じて付与されたロールは、次の再起動後も残ります。

## クライアント {#clients}

クライアントは `Clients` 配列で定義し、起動時に初期投入されます。各クライアントは次の項目を持てます。

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "ClientName": "My Application",
      "SecretHashes": ["pbkdf2-hash-here"],
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email", "custom-scope"],
      "Audiences": ["https://api.example.com"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "AlwaysIncludeUserClaimsInIdToken": false,
      "AccessTokenLifetimeSeconds": 1800,
      "IdentityTokenLifetimeSeconds": 300,
      "AuthorizationCodeLifetimeSeconds": 300,
      "AbsoluteRefreshTokenLifetimeSeconds": 2592000,
      "SlidingRefreshTokenLifetimeSeconds": 1296000,
      "RefreshTokenUsage": "OneTime",
      "MfaPolicy": "Enabled",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "RestrictedToOrganizationIds": [],
      "InitiateLoginUri": "https://app.example.com/login",
      "ClientUri": "https://app.example.com",
      "IsDefaultApplication": false
    }
  ]
}
```

初期投入は**読み取り、マージ、書き込み**の順に行われます。初期投入のデータに記載のないフィールドは保存されている値を維持するため、再起動によって管理 API で行った変更が元に戻ることはありません (無効化したクライアントは無効のまま、ローテーションしたシークレットは残り、`Audiences` とクライアントの JWKS は保持されます)。初期投入のデータに記載のあるフィールドは、起動のたびに上書きされます。

フィールドに関する補足 (`ClientSeedService.ClientSeedConfig` より):

- **別名。** `ClientId`/`Id`、`ClientName`/`Name`、`AllowedGrantTypes`/`GrantTypes`、`AllowedScopes`/`Scopes`、`AllowedCorsOrigins`/`CorsOrigins`、`RequireClientSecret`/`RequireSecret` は相互に置き換えられます。単一の `SeedClient` オブジェクトも、もう 1 つのエントリとして読み取られます。
- **シークレット。** `SecretHashes` (ハッシュ化済み) または `ClientSecret` (平文で、起動時にハッシュ化され、ハッシュが指定されていない場合にのみ使われます) のどちらかを指定します。初期投入のデータがシークレットを指定している場合にのみ適用されるため、管理 API でローテーションしたシークレットは次の再起動後も残ります。初期投入のデータの形式に `ClientSecretHashes` キーはありません。
- **`BackChannelLogoutUri`**: バックチャネルログアウトトークンを POST する先です。[バックチャネルログアウト](#back-channel-logout) を参照してください。
- **`RestrictedToOrganizationIds`**: そのクライアントを使える組織の ID です。空の場合は制限なしです。エントリが 1 つの場合は、組織を何も指定していないリクエストに対してその組織を選択することも兼ねます ([組織](organizations) を参照)。
- **`InitiateLoginUri`、`ClientUri`、`IsDefaultApplication`**: `/api/auth/apps` の一覧と、ログイン画面の「アプリに進む」ボタンに使われます。
- **初期投入できないもの。** `RequireConsent`、`ProvisioningApps`、`RequirePushedAuthorizationRequests`、クライアントの JWKS、フロントチャネルログアウトのフィールドは、初期投入のデータの形式にキーがないため、構成では設定できません。不明なキーは警告なしに無視されます。
- スコープやオーディエンスが予約済みスコープのルールやオーディエンスのルールに違反している初期投入のデータは、エラーのログとともに拒否され、スキップされます。

### オーディエンスとリソースインジケーター (RFC 8707) {#audiences-and-resource-indicators-rfc-8707}

`Audiences` は、`resource` パラメーター (RFC 8707) と、トークン交換 (RFC 8693) の `audience` パラメーターに対するクライアントの許可リストです。このチェックを通過したものが、発行されるアクセストークンの `aud` クレームになります。リクエストに `resource` がない場合、`aud` は `Audiences` にフォールバックし、どちらもない場合は `client_id` になります。

空の `Audiences` リストは、実際にこの問いに答えたクライアントにとっては **「なし」** を意味します。作成時のリクエストが `audiences` フィールドを含んでいたクライアントです。それが動的登録 (このフィールドは RFC 7591 に対する Authagonal の拡張です)、管理 API、初期投入の構成のいずれによるものかは問いません。そのようなクライアントは、どの経路でも `resource` を一切指定できません。認可、`client_credentials`、トークン交換のいずれでも同じ扱いです。

`audiences` を**省略した**動的登録 (標準的な RFC 7591 のクライアントはすべてそうで、つまりすべての MCP クライアントがそうです) は、この問いを一度も尋ねられていません。そのリストは「未設定」であり、任意の絶対 URI を `resource` として指定できます。MCP の認可仕様はこれに依存しています。`AudiencesDeclared` が存在する前に保存されたクライアントにも同じ解釈が適用されます。アップグレード時に保存済みのすべてのクライアントを厳格にすると、現在動作しているフローが壊れてしまうからです。

| クライアント | 空の `Audiences` の意味 |
|---|---|
| 作成時のリクエストが `audiences` を含んでいた (DCR の拡張フィールド、管理 API、初期投入) | **拒否**: `resource` を一切指定できない |
| `audiences` を省略した DCR の登録 | **「未設定」**: 任意の絶対 URI を `resource` として受け入れる |
| `AudiencesDeclared` が存在する前に保存された | **「未設定」**: 任意の絶対 URI を `resource` として受け入れる |

**旧来のクライアントへの後付け**は、`audiencesDeclared: true` (と、固定すべき `audiences`) を指定して、管理用のクライアント API に `PUT` を送ることで行います。このフラグは厳格にする方向にしか働きません。更新で設定することはできますが、解除することはできないため、無関係な編集によってクライアントが黙って緩い解釈に戻ることは決してありません。

旧来の行に対する影響は、埋もれさせるのではなく、はっきりと述べておく価値があります。

> `Audiences` が構成されていない既存のクライアントは、認可エンドポイントまたは `client_credentials` で**任意の**絶対 URI を `resource` として指定でき、その値を `aud` とし、このテナントの鍵で署名され、要求したユーザーの `sub` と、そのクライアントに許可されたスコープを含むアクセストークンを受け取ることができます。

宣言された `audiences` リストは、書き込まれる時点で検証されます。エントリは最大 20 個、それぞれ最大 512 文字で、明示的なスキームを持ち、フラグメントを含まない絶対 URI でなければなりません。`resource` の値にも同じ形式が求められます。なお、`/admin` のような単なるパスは、.NET の `Uri` パーサーが Linux では絶対 URI の `file:` とみなすにもかかわらず、受け入れられ**ません**。

リソースを指定することは、そのリソースにアクセスできることではありません。しかしそれは、クライアントと、そのクライアントが呼び出すことを想定されていない API との間に立つものが、認可サーバーだけであってはならないことを意味します。そのため、次のようにしてください。

- **リソースサーバーは、`iss` + `aud` + `sub` だけでなく、`scope` (または独自のモデル) に基づいて認可しなければなりません。** `aud` にあなたの API を示すトークンは、クライアントがあなたの API を要求したことを証明します。クライアントがそれを呼び出すことを許可されていることは証明せず、このサーバーにそれを証明させることもできません。
- **リソースサーバーは、単に「何らかの値が存在する」ことではなく、`aud` を自身の識別子と照合しなければなりません。**
- **固定された API の集合に結び付けるべきすべてのクライアントに `Audiences` を設定してください。** これが構成されていれば、リストにない `resource` は、認可エンドポイントと `client_credentials` で `invalid_target` により拒否されます。この制限を適用できるのはここだけです。
- **`audiencesDeclared` が存在する前に作成されたクライアントに `audiencesDeclared: true` を後付けしてください。** そうすれば、空のオーディエンスリストは「何でもよい」ではなく「なし」を意味するようになります。
- **自己登録したクライアントは、登録時に `audiences` を宣言でき**、空のリストを含めて、宣言した内容が適用されます。`Auth:DynamicClientRegistrationEnabled` は引き続き既定でオフです。[動的クライアント登録](client-registration) を参照してください。

### グラントタイプ {#grant-types}

| グラントタイプ | ユースケース |
|---|---|
| `authorization_code` | 対話型のユーザーログイン (Web アプリ、SPA、モバイル) |
| `client_credentials` | サービス間の通信 |
| `refresh_token` | トークンの更新 (`AllowOfflineAccess: true` が必要) |
| `urn:ietf:params:oauth:grant-type:device_code` | 入力が制限されたデバイス向けのデバイス認可グラント (RFC 8628) |

### リフレッシュトークンの使用方法 {#refresh-token-usage}

| 値 | 動作 |
|---|---|
| `OneTime` (既定) | リフレッシュのたびに新しいリフレッシュトークンを発行し、古いものを無効にします。既定 (`Auth:RefreshTokenReuseGraceSeconds = 0`) では、消費済みのトークンが再利用されると、そのユーザーとクライアントのすべてのトークンが直ちに失効します。既定で有効な猶予期間は**ありません**。再試行を許容する期間にオプトインするには、`Auth:RefreshTokenReuseGraceSeconds` を正の値に設定してください。 |
| `ReUse` | 有効期限まで同じリフレッシュトークンを再利用します。 |

### プロビジョニング先アプリ {#provisioning-apps}

クライアントの `ProvisioningApps` 配列 (認可時に読み取られます。`AuthorizeEndpoint.cs:578`。構成の初期投入処理はこれをバインドせず、管理 API のクライアントのルートもこれを扱わないため、ホストが保存済みのクライアントのレコードに設定します) は、`ProvisioningApps` 構成セクションで定義されたアプリの ID を参照します。ユーザーがこのクライアントを通じて認可すると、TCC によってそれらのアプリにプロビジョニングされます。詳しくは [プロビジョニング](provisioning) を参照してください。

## スコープ {#scopes}

独自の [OAuth スコープ](scopes) は `Scopes` 配列から初期投入できます。各エントリは起動時に `Name` をキーとしてアップサートされます (`Name` のないエントリは警告とともにスキップされます)。

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

設定したフィールドは、起動のたびに保存されている値より優先されます。省略したフィールドは保存されている値を維持します。そのため、構成で `UserClaims` と `AllowedRoles` を追加または変更することはできますが、空にすることはできません (そのためには `PUT /api/v1/scopes/{name}` を使ってください)。フィールドの意味は [スコープのモデル](scopes#scope-model) にあります。

## プロビジョニング先アプリ {#provisioning-apps-1}

ユーザーをプロビジョニングすべき下流のアプリケーションを定義します。

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-api-key"
    },
    "analytics": {
      "CallbackUrl": "https://analytics.example.com/provisioning",
      "ApiKey": "another-key"
    }
  }
}
```

TCC プロトコルの完全な仕様については [プロビジョニング](provisioning) を参照してください。

## MFA ポリシー {#mfa-policy}

多要素認証は、`MfaPolicy` プロパティによってクライアントごとに適用されます。

| 値 | 動作 |
|---|---|
| `Disabled` (既定) | ユーザーが MFA を登録していても、MFA のチャレンジは行いません |
| `Enabled` | MFA を登録済みのユーザーにはチャレンジを行います。登録は強制しません |
| `Required` | 登録済みのユーザーにはチャレンジを行い、MFA のないユーザーには登録を強制します |

```json
{
  "Clients": [
    {
      "ClientId": "secure-app",
      "MfaPolicy": "Required"
    }
  ]
}
```

`MfaPolicy` が `Required` で、ユーザーが MFA を登録していない場合、ログインは `{ mfaSetupRequired: true, setupToken: "..." }` を返します。セットアップトークンは (`X-MFA-Setup-Token` ヘッダーを通じて) MFA セットアップのエンドポイントに対してユーザーを認証するため、ユーザーは Cookie セッションを得る前に登録できます。

フェデレーションによるログイン (SAML/OIDC) も MFA ポリシーに従います。MFA を登録済みのユーザーは、外部の IdP による認証の後に MFA のチャレンジに回され、`Required` は MFA のないフェデレーションのユーザーに登録を強制します。

### IAuthHook によるオーバーライド {#iauthhook-override}

`IAuthHook.ResolveMfaPolicyAsync` メソッドで、クライアントのポリシーをユーザーごとにオーバーライドできます。

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    return Task.FromResult(clientPolicy);
}
```

## パスワードポリシー {#password-policy}

パスワードの強度の要件をカスタマイズします。

```json
{
  "PasswordPolicy": {
    "MinLength": 10,
    "MinUniqueChars": 3,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": false
  }
}
```

| プロパティ | 既定値 | 説明 |
|---|---|---|
| `MinLength` | `8` | パスワードの最小の長さ |
| `MinUniqueChars` | `2` | 異なる文字の最小数 |
| `RequireUppercase` | `true` | 大文字を 1 文字以上必須にします |
| `RequireLowercase` | `true` | 小文字を 1 文字以上必須にします |
| `RequireDigit` | `true` | 数字を 1 文字以上必須にします |
| `RequireSpecialChar` | `true` | 英数字以外の文字を 1 文字以上必須にします |

このポリシーは、パスワードのリセットと、管理者によるユーザー登録で適用されます。ログイン UI は `GET /api/auth/password-policy` から有効なポリシーを取得し、要件を動的に表示します。

## SAML プロバイダー {#saml-providers}

SAML ID プロバイダーを構成で定義します。これらは起動時に初期投入されます。

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com", "example.org"]
    }
  ]
}
```

| プロパティ | 必須 | 説明 |
|---|---|---|
| `ConnectionId` | はい | 安定した識別子 (`/saml/{connectionId}/login` のような URL で使われます) |
| `ConnectionName` | いいえ | 表示名 (既定は ConnectionId) |
| `EntityId` | はい | **このサーバーの** SP エンティティ ID。IdP に登録する識別子であり、IdP 自身のエンティティ ID ではありません |
| `MetadataLocation` | はい | IdP の SAML メタデータ XML の URL。https でなければならず、ホストが [`Auth:AllowedInternalTargets`](#outbound-fetches-ssrf-guard) に記載されていない限り、パブリックにルーティング可能でなければなりません。このドキュメントには、すべてのアサーションの検証に使われる証明書が含まれているからです。IdP が https のメタデータのエンドポイントを公開していない場合は、代わりに [管理 API](admin-api) で `metadataXml` を設定してください。構成の初期投入にはそのためのキーがありません。 |
| `AllowedDomains` | いいえ | SSO によってこのプロバイダーにルーティングされるメールドメイン |
| `OrganizationId` | いいえ | この接続のスコープを 1 つの [組織](organizations) にします。null (既定) の場合はテナントレベルの接続になります。`AllowedDomains` を SSO のドメインルートとして登録するのは、テナントレベルの接続だけです |
| `JitProvisioningEnabled` | いいえ | 初回のサインイン時にユーザーを作成します。既定は `false` |
| `AllowUninvitedJit` | いいえ | 招待されていない組織に JIT でユーザーを作成できるようにします。既定は `false` |
| `ChallengeMfaAfterLogin` | いいえ | IdP でのサインインの後、アプリの MFA ポリシーに従ってチャレンジを行います。既定は `true` |
| `ProvisioningAttributeParams` | いいえ | 下流のプロビジョニングに渡されるアサーションの属性 |
| `AllowUnsolicitedResponses` | いいえ | この接続で、IdP 起点の (要求していない) レスポンスを受け入れます。既定は `false` |

真偽値は、既定値を含めて起動のたびに初期投入のデータから書き込まれるため、初期投入された接続をオペレーターが管理 API で変更した場合、次の再起動でそれらは元に戻ります。初期投入のデータにキーがないフィールド (`SpCertificate`、`SignAuthnRequests`、`NameIdFormat`、`MetadataXml`、`IconUrl`) は保持されます。

## OIDC プロバイダー {#oidc-providers}

OIDC ID プロバイダーを構成で定義します。これらは起動時に初期投入されます。

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

| プロパティ | 必須 | 説明 |
|---|---|---|
| `ConnectionId` | はい | 安定した識別子 (`/oidc/{connectionId}/login` のような URL で使われます) |
| `ConnectionName` | いいえ | 表示名 (既定は ConnectionId) |
| `MetadataLocation` | はい | IdP の OpenID Connect ディスカバリードキュメントの URL |
| `ClientId` | はい | IdP に登録した OAuth2 のクライアント ID |
| `ClientSecret` | はい | OAuth2 のクライアントシークレット (起動時に `ISecretProvider` で保護されます) |
| `RedirectUrl` | いいえ | **無視されます。** リダイレクト URI はリクエストごとに `{Issuer}/oidc/callback` として導き出されるので、IdP には*それ*を登録してください。ここに指定した値は効果がなく、無視されたことがログに記録されます。 |
| `AllowedDomains` | いいえ | SSO によってこのプロバイダーにルーティングされるメールドメイン |
| `OrganizationId` | いいえ | この接続のスコープを 1 つの [組織](organizations) にします。null はテナントレベルを意味します |
| `JitProvisioningEnabled` | いいえ | 初回のサインイン時にユーザーを作成します。既定は `false` |
| `AllowUninvitedJit` | いいえ | 招待されていない組織に JIT でユーザーを作成できるようにします。既定は `false` |
| `UseUpstreamSubjectAsUserId` | いいえ | 上流の `sub` をローカルのユーザー ID として使います。既定は `false` |
| `ShowOnLogin` | いいえ | ログイン画面にこの接続のボタンを表示します。既定は `true`。ドメインでルーティングされる接続は、これにかかわらずメールアドレスを先に入力する形で到達します |
| `ChallengeMfaAfterLogin` | いいえ | IdP でのサインインの後、アプリの MFA ポリシーに従ってチャレンジを行います。既定は `true` |
| `AutoLinkExistingByEmail` | いいえ | 初回のサインインを、同じメールアドレスを持つ既存のローカルアカウントにリンクします。既定は `false` |
| `PassthroughParams`、`ProvisioningAttributeParams` | いいえ | IdP にそのまま渡されるパラメーター / 下流のプロビジョニングに渡されるパラメーター |
| `RevalidateOnRefresh` | いいえ | リフレッシュトークンが使われたときに、上流のセッションを再確認します。既定は `false` |
| `IsExternalConnection`、`SessionExpClaim` | いいえ | フェデレーションのセッションの設定。[フェデレーションのセッション](federated-sessions) を参照してください |
| `InteractionPath` | いいえ | 認証されていない `idp_hint` のリクエストがこの接続を通じてフェデレーションされる前に表示される、ログインアプリのパス (例: `/guest`)。空の場合は直接フェデレーションします |

OIDC の初期投入は、SAML のものより多くを起動のたびに上書きします。真偽値は既定値を含めて初期投入のデータから書き込まれるため、初期投入された接続をオペレーターが管理 API で変更した場合、次の再起動でそれらは元に戻ります。`AllowedDomains`、`PassthroughParams`、`ProvisioningAttributeParams`、`SessionExpClaim`、`InteractionPath` も同様です。初期投入のデータで省略されたキーは、保存されている値を維持するのではなく、空または既定値にリセットされます。常に保持されるのは `IconUrl` と `CreatedAt` だけで、`ConnectionName` と `OrganizationId` は初期投入のデータで省略された場合に保持されます。

> **注:** プロバイダーは [管理 API](admin-api) を通じて実行時に管理することもできます。構成で初期投入されたプロバイダーは起動のたびにアップサートされるため、構成の変更は再起動時に反映されます。

## シークレットプロバイダー {#secret-provider}

上流の OIDC クライアントシークレットと TOTP / MFA のシードは、平文ではなく Azure Key Vault に保存できます。

| 設定 | 説明 |
|---|---|
| `SecretProvider:VaultUri` | Key Vault の URI (例: `https://my-vault.vault.azure.net/`)。設定されていない場合は**平文**のプロバイダーが使われ、シークレットは Table Storage にそのまま保存されます。 |
| `SecretProvider:RequireVaultReferences` | 既定では `false`。`true` の場合、Vault のプレフィックス (Key Vault の場合は `kv:`、AWS Secrets Manager の場合は `sm:`) を持たない保存済みの参照は、平文の値として受け入れられるのではなく、**エラー**になります。Vault への移行が完了したら設定してください。 |

構成されている場合、Key Vault の参照のように見えるシークレットの値は、実行時に解決されます。認証には `DefaultAzureCredential` を使います。

### Vault への移行と、その後に扉を閉じること {#migrating-into-a-vault-and-closing-the-door-afterwards}

Vault を使う 2 つのプロバイダーはどちらも、プレフィックスのない参照をそのまま返し、デプロイメントに Vault がなかった頃に書き込まれた平文の値として扱います。これによって、稼働中のシステムを一度にすべてではなく 1 つのシークレットずつ移行できますが、開いたままにしておくと、恒久的なダウングレードの経路になります。構成の列を 1 つ書き込めるもの (中途半端な移行、参照を入れるべき場所に生の値を保存する管理の経路、ストレージにはアクセスできるが Vault にはアクセスできない攻撃者) は何であれ、Vault で保護されたシークレットを自分で選んだ値に置き換えることができ、しかもそれは完全に検証を通ります。プレフィックスのない参照では、参照が値*そのもの*だからです。

移行が完了したら `SecretProvider:RequireVaultReferences` を設定してください。そうすると、プレフィックスのない参照の解決は、黙って平文を返すのではなく例外をスローします。解決されたプロバイダーが平文のものである状態でこれを設定すると、起動時に拒否されます。その組み合わせでは動作する状態があり得ないからです。平文のプロバイダーが書き込む参照はすべてプレフィックスを持ちません。

また、Development 以外のホストで最終的に使われるのが平文のプロバイダーである場合、サーバーは起動時に警告をログに記録します。

> ⚠️ **本番環境: `SecretProvider:VaultUri` を設定してください。** 既定のシークレットプロバイダーは**平文**です。`SecretProvider:VaultUri` が未設定の場合、上流の OIDC クライアントシークレットと TOTP / MFA のシードは Azure Table Storage に平文で書き込まれ、そのため [バックアップ](backup-restore) にも平文で含まれます。本番環境のデプロイメントでは、これらのシークレットが Key Vault に保存されるよう、必ず `SecretProvider:VaultUri` を構成してください。

## 管理 API {#admin-api}

| 設定 | 既定値 | 説明 |
|---|---|---|
| `AdminApi:Enabled` | `true` | **既定で有効です。** `false` に設定すると、すべての管理エンドポイントが無効になります (登録されません)。 |
| `AdminApi:Scope` | `authagonal-admin` | 管理エンドポイントへのアクセスに必要な JWT のスコープ。既存のスコープ名に合わせて変更してください (例: IdentityServer からの移行では `projects-identity-admin`)。 |

> ⚠️ **管理 API は既定で有効であり、非常に強い権限を持ちます。** 管理スコープは完全な管理権限と、ユーザーへのなりすましを可能にします。`AdminApi:Scope` を持つトークンを保持する者は誰でも、任意のユーザーのトークンを発行し、クライアントを管理し、すべての構成を読み書きできます。管理エンドポイント (`/api/v1/*` の管理ルート) へのネットワークアクセスを制限し、管理スコープを発行できる相手を厳しく管理してください。多層防御の一環として、このスコープは*予約*されています。OAuth クライアントに付与することは決してできず ([管理 API](admin-api) を参照)、なりすましのエンドポイントを通じて発行することもできません。管理 API を使わない場合は、`AdminApi:Enabled = false` で完全に無効にしてください。

## 同意 {#consent}

クライアントごとの同意は、`RequireConsent` プロパティで有効にできます。

| 値 | 動作 |
|---|---|
| `false` (既定) | 認証の後、認可が直ちに進みます |
| `true` | 要求されたスコープを一覧にした同意画面がユーザーに表示されます。同意は 5 年間保存され、新しいスコープが要求された場合にのみ再度確認されます。 |

ユーザーは `GET /consent/grants` と `DELETE /consent/grants/{clientId}` で、自分の同意によるグラントを確認し、取り消すことができます。

## バックチャネルログアウト {#back-channel-logout}

OIDC Back-Channel Logout 1.0 の通知を受け取るには、クライアントに `BackChannelLogoutUri` を登録します。ユーザーがログアウトすると、Authagonal は各クライアントの登録済みの URI に、署名されたログアウトトークン (JWT) を送信します。

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback"
    }
  ]
}
```

## メール {#email}

組み込みのメール送信機能は [Resend](https://resend.com) を使い、`Email:ResendApiKey` が構成されると**自動的に有効になります**。サービスの登録は不要です。別のプロバイダーを使うには、`AddAuthagonal()` を呼び出す前に独自の `IEmailService` 実装を登録してください (`Email:*` キーにかかわらず、そちらが優先されます)。

| 設定 | 説明 |
|---|---|
| `Email:ResendApiKey` | Resend の API キー。設定されている場合、組み込みの Resend 送信機能が使われます。 |
| `Email:SenderEmail` | 送信者のメールアドレス |
| `Email:SenderName` | 送信者の表示名 (既定は `"Authagonal"`) |

> ⚠️ **メールの送信機能がまったくないと、自己登録は機能しません。** `Email:ResendApiKey` が未設定で、独自の `IEmailService` も登録されていない場合、何もしないサービスがすべてのメールを黙って破棄するため、確認メールやパスワードリセットのメールは決して届きません。そして、ログインには既定で確認済みのメールアドレスが必要なため、自己登録したユーザーは決してサインインできません。この状態では、`UseAuthagonal` が起動時に警告をログに記録します。開発環境とテスト環境のための回避策として、`Auth:AutoConfirmEmailDomains` を使うと、列挙したドメインの登録が自動的に確認済みになります。

`@example.com` のアドレスへのメールは黙ってスキップされます (テストに便利です)。

## クラスター {#cluster}

クラスタリングの層は、**リーダー選出** (署名鍵のローテーションなど、リーダーに限定されたジョブがちょうど 1 つのノードで実行されるようにするもの) と**ノード間のイベントバス**を、差し替え可能なバックエンドの背後で提供します。既定はインプロセスです。単一のノードは常に自分自身がリーダーであり、単一ノードの環境とローカルでの開発に適した設定で、構成は一切不要です。

| 設定 | 環境変数 | 既定値 | 説明 |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | マスタースイッチ。`false` の場合、ノードは単独で動作します (常にリーダーで、イベントバスはインプロセス)。 |
| `Cluster:Secret` | `Cluster__Secret` | *(なし)* | 内部専用の `/_internal/backchannel-logout` エンドポイントで必要な共有シークレット。設定されている場合、呼び出し元はそれを `X-Cluster-Secret` ヘッダーで提示しなければなりません (定数時間で比較されます)。**未設定の場合、このエンドポイントは誰も認可せず**、404 を返します。送信元アドレスは資格情報ではなく、同じホスト上のリバースプロキシは、インターネットから来たものを含め、転送するすべてのリクエストでまさにループバックを提示するからです。 |
| `Cluster:AllowLoopbackWithoutSecret` | `Cluster__AllowLoopbackWithoutSecret` | `false` | 開発環境向けのオプトイン。`Cluster:Secret` がない場合に、**転送前のピアアドレス**がループバックである呼び出し元を受け入れます。プライベートアドレスの範囲は引き続き拒否されます。共有のクラスターネットワークでは、それは隣接するすべてのワークロードを信頼することになるからです。リバースプロキシの背後にあるホストでは設定しないでください。 |
| `Cluster:RunLeaderElection` | `Cluster__RunLeaderElection` | `true` | このノードがリースの更新ループを実行し、リーダーになり得るかどうか。`false` でもクラスターには参加し、イベントバスを受信しますが、リースを争うことはありません。クラスターのイベントを受信する必要はあるものの、決してリーダーになってはならないノードに適しています。 |
| `Cluster:LeaseTtlSeconds` | `Cluster__LeaseTtlSeconds` | `30` | リーダーのリースの期間。この間隔のおよそ半分ごとに更新されます。 |
| `Cluster:PollIntervalSeconds` | `Cluster__PollIntervalSeconds` | `3` | イベントバスのバックエンドが、他のノードが発行したメッセージをポーリングする頻度。 |

**複数ノードのデプロイメント**では、`AddAuthagonal` / `AddAuthagonalCore` の `configureClustering` コールバックで実際のバックエンドに差し替えます。

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS equivalent (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// Self-hosted PostgreSQL (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` はイベントバスだけを登録し、リースはインプロセスのまま維持します。クラスターのイベントを受信する必要はあるものの、決してリーダーを争ってはならないノード向けです。

リーダーとイベントバスが複数のインスタンスにわたってどのように動作するかについては、[スケーリング](scaling) を参照してください。

## 転送ヘッダー (信頼されたプロキシ) {#forwarded-headers-trusted-proxy}

Authagonal は、レート制限とアカウントのロックアウトをクライアントの IP をキーとして行い、HSTS は HTTPS のリクエストでのみ出力します。リバースプロキシやイングレスの背後では、実際のクライアントの IP とスキームは `X-Forwarded-For` / `X-Forwarded-Proto` ヘッダーで届きます。これらの設定は、それらの値を設定することを**どのプロキシのホップに信頼するか**を制御するもので、呼び出し元が `X-Forwarded-For` を詐称してクライアントの IP を偽造できないようにします。

| 設定 | 環境変数 | 既定値 | 説明 |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | `X-Forwarded-For` のチェーンの右側から受け入れるプロキシのホップ数。既定の `1` では、イングレスが追加する 1 つのホップだけを信頼し、チェーンのそれより左にあるものはすべて無視します。 |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0` (配列) | *(空)* | 転送ヘッダーの設定を許可する CIDR の範囲 (文字列の配列。例: `"10.0.0.0/8"`)。プロキシ、イングレス、Pod の CIDR を設定してください。これを宣言することで、初めて `X-Forwarded-Proto` が受け入れられるようになります。後述を参照してください。 |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies__0` (配列) | *(空)* | 転送ヘッダーの設定を許可する個々のプロキシの IP アドレス (文字列の配列)。`KnownNetworks` と併用するか、その代わりに使います。 |

```json
{
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"],
    "KnownProxies": []
  }
}
```

### 2 つのヘッダーは同じ条件では信頼されない {#the-two-headers-are-not-trusted-on-the-same-terms}

`X-Forwarded-For` は**クライアントの IP** を調整します。これは、レート制限、ロックアウト、`/_internal` のガードがキーとして使うものです。何も宣言されていない場合、Authagonal はループバックと RFC1918 の範囲からのこのヘッダーを受け入れ、警告をログに記録します。これはベストエフォートの既定値であり、信頼セットが空の場合のフレームワークの動作、つまり存在する*あらゆる*呼び出し元からのヘッダーを受け入れる動作よりは優れています。

`X-Forwarded-Proto` は**スキーム**を変更します。スキームは、`/connect/*` がそもそも応答するかどうか (RFC 6749 §3.1/§3.2)、Cookie に `Secure` が付くかどうか、生成される絶対 URL が https になるかどうかを決めます。これが受け入れられるのは、`KnownNetworks` / `KnownProxies` で宣言したプロキシから届いた場合**だけ**です。プライベートアドレスであることは宣言ではありません。Authagonal はライブラリとして提供されるため、自分がデプロイされたネットワークを知ることができず、「ピアがプライベートアドレスを持っている」というのはトポロジーについての推測に過ぎません。フラットな LAN、共有の VPC、共有のコンテナーブリッジでは、隣接するすべてのワークロードがそれらの範囲の中にあり、平文で届いたリクエストについて `https` を主張できてしまいます。

**プロキシに固定のアドレスがない場合** (Kubernetes のイングレス、アドレスが変わるロードバランサー、ホップの CIDR を教えてくれないプラットフォーム)、すべてのピアをプロキシとして宣言します。

```json
{
  "ForwardedHeaders": {
    "KnownNetworks": ["0.0.0.0/0", "::/0"]
  }
}
```

これが安全なのは、プロキシ以外の何もプロセスに到達できない場合に限られます。そのようなデプロイメントは、既にその前提に依存しています。それを明記することで、ライブラリに推測させるのではなく、レビューできる場所にその前提が置かれます。他のワークロードが Kestrel に直接到達*できる*場合、この設定のもとではそれらがスキームとクライアントの IP を詐称できるので、代わりに実際の CIDR を固定してください。

### プロキシを宣言しない場合: 送信元ごとのすべての上限が共有される {#undeclared-proxy-every-per-source-quota-is-shared}

呼び出し元のアドレスをキーとするレート制限 (ログイン、登録、パスワード忘れ、動的クライアント登録、SAML ACS) は、どのクライアントがリクエストを送ったかを知る必要があります。リバースプロキシの背後では、それは転送されたクライアントの IP ですが、転送されたクライアントの IP が証拠になるのは、それを書き込んだプロキシを宣言した場合だけです。何も宣言されていない場合、Authagonal はそれらの上限を実際に観測したピアをキーとして適用します。プロキシの背後では、それはプロキシです。**すべてのクライアントが 1 つの上限を共有し、どの呼び出し元でも 1 人で全員分を使い切ることができます** (ログインの既定値は 5 分あたり 30 回の試行です)。

これはバグではなく意図的なもので、サーバー側で修正することはできません。もう 1 つの選択肢、つまりそれでも転送された値をキーとして使う方法では、呼び出し元が 1 つのヘッダーを変えるだけでリクエストごとに新しい上限を得られてしまいます。L4 のロードバランサーの背後では、最も右の転送されたホップは呼び出し元自身のヘッダー*そのもの*だからです。2 つの状況のどちらにあるかは、まさに宣言がサーバーに伝えるものであり、他の何もそれを伝えることはできません。プロキシを宣言すれば、上限はクライアントごとになります。

> ⚠️ **TLS を終端するプロキシが必要で、それを宣言しなければなりません。** Authagonal は TLS を終端するリバースプロキシの背後で実行する (または自身で TLS を終端する) 必要があります。HSTS (`Strict-Transport-Security`) は HTTPS のリクエストでのみ出力され、OAuth のエンドポイントは `Auth:AllowInsecureHttp` が設定されていない限り平文のリクエストを一切拒否します。そのため、HSTS が送信され、`/connect/*` がそもそも応答するためには、プロキシが `X-Forwarded-Proto: https` を転送し、**かつ** `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` に記載されている必要があります。何も宣言しないことは、アップグレード時によくある失敗です。ヘッダーは届くものの、それに基づいて動作することを許されたものが何もなく、実際に TLS で動作しているデプロイメントで、すべての `/connect/*` のリクエストが 400 を返します。起動時のログにも、拒否のレスポンス本文にも、そのことが示されます。

## 外部への取得 (SSRF ガード) {#outbound-fetches-ssrf-guard}

Authagonal は、自分で選んだのではない URL に対して、サーバー起点の HTTP リクエストを送ります。上流の IdP の SAML メタデータや OIDC ディスカバリードキュメント、`private_key_jwt` 認証の際のクライアントの `jwks_uri`、バックチャネルログアウトの URI、プロビジョニングのコールバックです。それらの URL の一部はクライアントを登録した人が指定したものであり、`169.254.169.254` やクラスター内のホストを指す URL は、Authagonal が攻撃者に代わって送るリクエストになってしまいます。

それらの取得はすべて、2 段階でガードされています。**URL のチェック**は、http(s) 以外のスキーム、リテラルの内部アドレス、`localhost` / `.local` / `.internal` という名前を、URL が受け入れられる時点 (管理者による書き込み、動的クライアント登録) で拒否します。その時点であれば、エラーを入力した人に帰することができます。**アドレスのチェック**はソケットのレベルで実行されます。ホストを解決し、返された内部アドレスをすべて拒否し、名前を OS に渡し直すのではなく、実際にチェックしたアドレスに接続します。2 つ目のチェックは、テキストのチェックにはできないことを行います。ホスト名は、攻撃者が正直である必要のあるテキストではないからです。`logout.attacker.test` は、サフィックスとリテラルのすべてのルールを通過したうえで、クラウドのメタデータのアドレスを返すことができます。リダイレクトは新しい接続なので、アドレスのチェックはすべてのホップで再度実行されます。

どちらも既定でオンになっており、ほとんどのデプロイメントではそれに気付くことはありません。それが目に見えるようになるのは、次の 2 つの場合です。

### 意図的に内部の宛先に到達する {#reaching-an-internal-destination-on-purpose}

プライベートネットワーク経由でしか到達できない IdP とのフェデレーションや、同じクラスター内で動作するアプリへのプロビジョニングは、攻撃を阻止するのとまったく同じルールによって拒否されます。それらの宛先を指定してください。

```json
{
  "Auth": {
    "AllowedInternalTargets": ["idp.corp.internal", "*.svc.corp.internal", "10.4.0.0/16"]
  }
}
```

| エントリの形式 | 許可するもの |
|---|---|
| `idp.corp.internal` | そのホストと完全に一致するもの、およびそれが解決されるすべてのアドレス |
| `*.corp.internal` | そのサフィックスの下にある任意のホスト、およびそれらが解決されるすべてのアドレス |
| `10.4.0.0/16`, `fd00:1234::/48` | そのネットワーク (名前は問わない) |
| `10.4.1.7` | その単一のアドレス (名前は問わない) |

環境変数の形式は `Auth__AllowedInternalTargets__0`、`__1` などです。CIDR のエントリの形式が不正な場合、何も許可しないまま黙って動作するのではなく、起動時に失敗します。

**このリストが及ぶのは、あなたが指定した URL だけです。** 上流の SAML メタデータの取得、上流の OIDC ディスカバリー (そのドキュメントが示す `token_endpoint`、`userinfo_endpoint`、`jwks_uri` を含む)、プロビジョニングのコールバックです。クライアントが登録した `jwks_uri` やバックチャネルログアウトの URI には、意図的に及び**ません**。そこでは内部のホストがデプロイメントの構成であることは決してないため、フェデレーションの宛先を開放しても、匿名の `/connect/token` のリクエストに対してメタデータサービスまで開放されることはありません。全体を「オフ」にする設定はありません。

なお、このリストにかかわらず、フェデレーションの 2 つのメタデータの URL では引き続き https が必要です。そのドキュメントには上流のすべてのアサーションの検証に使われる鍵と証明書が含まれており、プライベートネットワークは安全なチャネルではないからです。

> ⚠️ **マルチテナントのホスト: 何かをリストに加える前に、メタデータの URL を誰が書き込むのかを確認してください。** このリストの対象は*あなた*が構成した宛先であり、シングルテナントのデプロイメントでは接続の管理者はあなた自身です。他の人のために Authagonal を運用している場合 (テナントの管理者がポータルや管理 API を通じて自分の SAML/OIDC 接続を構成する SaaS)、`MetadataLocation` は**顧客**が指定するものであり、ここに加えたエントリはすべて、接続をそこに向けたどのテナントからも到達可能になります。そのようなホストでは空のまま (既定) にしてください。あるテナントが本当にオンプレミスの IdP を必要とする場合は、ネットワークの内側から経路を開くのではなく、ネットワークの外側で終端する外向きの経路を提供してください。

### 外向きの通信に HTTP プロキシが必要な場合 {#if-your-egress-requires-an-http-proxy}

アドレスのチェックは `SocketsHttpHandler.ConnectCallback` に組み込まれていますが、プロキシが有効な場合、.NET はこのコールバックを宛先ではなく**プロキシの**エンドポイントで呼び出します。そのため、チェックはプロキシを検査し、それが完全にルーティング可能であることを確認して、すべてを許可してしまいます。プロキシがある可能性が最も高いネットワークでこそ、フェイルオープンになってしまうのです。そのため、ガードされたクライアントは `UseProxy = false` を設定しており、プロキシ経由でしか外に出られないネットワークでは、それらの取得は失敗します。

`Auth:AllowOutboundProxy` は、オペレーターが構成した取得 (SAML メタデータ、OIDC ディスカバリー、プロビジョニングのコールバック) を再びプロキシ経由で送信します。それらについては URL のチェックは維持され、アドレスのチェックは失われます。内部アドレスに解決されるホスト名は、もはや検出されません。クライアントの `jwks_uri` の取得やバックチャネルログアウトの配信には及び**ません**。それらの宛先は登録者が選んだもので、匿名のリクエストから到達できるため、それらのためのスイッチはありません。それらをプロキシ経由にしなければならないネットワークでは、その前段に SSRF をフィルタリングする外向きのゲートウェイが必要です。

`UseAuthagonal()` は、`HTTPS_PROXY`、`HTTP_PROXY`、`ALL_PROXY` のいずれかが設定されているのを見つけると、それを迂回するクライアントの名前を挙げて、起動時に警告をログに記録します。そうしなければ、症状は「SSO が動かなくなった」というもので、原因を指し示すものは何もありません。

### ガードされないもの {#what-is-not-guarded}

BFF の外向きのクライアントと、メールの配信です。`AuthagonalBffOptions.Upstreams[].TargetBaseUrl` はあなた自身の構成であり、そのドキュメントの例は内部アドレスです。BFF のトークンクライアントはあなたが構成した認可サーバーと通信し、プロキシは構成された上流の認可サーバーから外れた宛先の組み立てを既に拒否するため、呼び出し元がそれらのリクエストの向きを変えることはできません。`Resend` はコンパイル時の定数に POST します。3 つとも、通常どおり環境のプロキシを使います。

## レート制限 {#rate-limiting}

組み込みのレート制限は、悪用されやすいエンドポイントを保護します。

| エンドポイント | 上限 | 期間 | キー |
|---|---|---|---|
| `POST /api/auth/login` | 30 (`Auth:MaxLoginAttemptsPerIp`) | 5 分 (`Auth:LoginWindowMinutes`) | 送信元アドレス、および別途、送信されたメールアドレス |
| `POST /api/auth/register` | 5 (`Auth:MaxRegistrationsPerIp`) | 1 時間 (`Auth:RegistrationWindowMinutes`) | クライアントの IP |
| `POST /api/auth/forgot-password` | 3 (`Auth:MaxPasswordResetsPerEmail`) | 1 時間 (`Auth:PasswordResetWindowMinutes`) | 送信先のメールアドレス |
| `POST /api/auth/forgot-password` | 15 (`Auth:MaxPasswordResetsPerIp`) | 1 時間 (`Auth:PasswordResetWindowMinutes`) | クライアントの IP |
| `POST /connect/register` (有効な場合) | 10 | 1 時間 | クライアントの IP |
| SCIM のエンドポイント | 200 | 1 分 | SCIM クライアント |

既定では、制限は (`IRateLimiter` の拡張点の背後で) **ノードごとにインプロセスで**適用されるため、N 個のインスタンスがある場合、実際の上限は構成された値の N 倍になります。これらは最後の防衛線として扱い、正式なグローバルの制限はエッジ (WAF、イングレス、CDN) で適用してください。[スケーリング](scaling#rate-limiting) を参照してください。

### クラスター全体の制限 (`Auth:DurableRateLimiting`) {#cluster-wide-limits-authdurableratelimiting}

`Auth:DurableRateLimiting` を `true` に設定すると、カウンターがデプロイメントで既に稼働しているストアに移り、
すべてのレプリカが 1 つの上限を共有するようになって、上限がインスタンス数に応じて増えることがなくなります。

| | インプロセス (既定) | 永続 |
|---|---|---|
| N 個のレプリカでの上限 | 構成された値の N 倍 | 構成された値 |
| チェックごとのコスト | なし | ストアとのラウンドトリップ 1 回 |
| Pod の再起動後も維持されるか | いいえ | はい |
| バックエンド | 任意 | Azure Table、SQL、DynamoDB |

上限が推測可能なものを守っている場合、とりわけデバイスフローの `user_code` では、有効にする価値があります。
そこでは試行の上限だけが、攻撃者と、有効なセッションを付与するコードとの間に立っており、レプリカ数に応じて増える
上限は適切な形ではありません。量に関する制限ではあまり役に立ちません。そちらはいずれにせよエッジが正式な上限だからです。

本番環境で重要な詳細:

- **無料ではありません。** レート制限のすべてのチェックが、ログイン、トークン、SCIM の経路を含めて、ストアとのラウンドトリップに
  なります。単一ノードのデプロイメントでは得るものはなく (そこではノードごとの制限が*そのまま*クラスター全体の制限です)、
  オフのままにしておくべきです。
- **固定の期間を使うため、バーストが境界をまたぐことがあります。** N の上限は「期間あたり N、境界をまたぐと最大 2N」であり、
  同梱の上限はその余裕を見込んでいます。これによって、カウンターをすべてのバックエンドで単一のアトミックなインクリメントに
  でき、それこそが正しさの拠り所となる性質です。
- **フェイルオープンです。** ストアに到達できない場合、リクエストは許可され、エラーがログに記録されます。リミッターは
  ログインの経路を守るものであり、それを停止させる手段になってはならないからです。エッジのルールは維持してください。
- `IRateLimitCounterStore` を提供するプロバイダーなしでこれを設定すると、**ホストは起動しません**。
  オフにしたばかりのノードごとの制限に黙ってフォールバックするのではなく、起動を拒否します。
- **カウンターの行は自動的に回収されます。** DynamoDB はネイティブの TTL で、SQL は `SqlExpiryReaper` で、Azure
  Table はリーダーのみが行う掃除で回収します (Table Storage には TTL もサーバー側の演算もないため、インクリメントに
  読み取りと条件付きの書き込みのコストがかかるバックエンドでもあります)。

## CORS {#cors}

CORS は動的に構成され、**パスによってスコープが決まります**。以前の 1 行の説明 (「登録されたすべてのクライアントの
オリジンが自動的に許可される」) は、プロバイダーが実際に行うよりもかなり多くのことを述べていました。

- **クライアントが登録したオリジン** (クライアントの `AllowedCorsOrigins`) が受け入れられるのは、`/connect/` と
  `/.well-known/` の下だけです。`/api/auth/`、`/api/v1/`、`/scim/` は開放され**ません**。無効化されたクライアントは
  何も寄与せず、形式が不正なオリジンは除外されます。
- `/api/auth/`、`/api/v1/`、`/scim/`、`/consent`、`/approvals` の下では、オペレーターが構成したものか
  クライアントが登録したものかを問わず、どのオリジンに対しても**資格情報は決して許可されません**。それらを別のオリジンから
  `credentials: 'include'` で呼び出すブラウザーのクライアントは、構成にかかわらず失敗します。資格情報付きのクロスオリジン
  呼び出しではなく、Backend-for-Frontend (`@authagonal/bff` パッケージを参照) を使ってください。
- 解決されたポリシーは 60 分間キャッシュされます。

つまり、クライアントの `AllowedCorsOrigins` に追加したオリジンでは、`/connect/*` は動作しますが、`/api/v1/*` は
動作しません。これは意図的なものです。それらのパスはセッション Cookie と管理機能を扱うからです。

## HashiCorp Vault Transit {#hashicorp-vault-transit}

`VaultTransitClient` は Vault の Transit シークレットエンジンと通信し、署名、検証、暗号化、復号、鍵付き HMAC を行います。
これは、Vault を使う `IFieldCipher` や `IIndexTokenizer` を作るための部品であり、それらは自分で登録します。

**JWT の署名は Vault に委任されません。** `ProtocolKeyManager` は常に `ISigningKeyStore` 内の鍵で署名し、
それを Vault の鍵に置き換える拡張点はありません。それに何が必要になるかについては、[拡張性](extensibility) を参照してください。

ライブラリとしてホストする場合、これはプログラムで構成します。

## 完全な例 {#full-example}

```json
{
  "Storage": {
    "TableServiceUri": "https://myaccount.table.core.windows.net/",
    "NameIndexesEnabled": true
  },
  "Issuer": "https://auth.example.com",
  "LoginAppUrl": "/login",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "RefreshTokenReuseGraceSeconds": 0,
    "DynamicClientRegistrationEnabled": false,
    "SigningKeyLifetimeDays": 90
  },
  "SecretProvider": {
    "VaultUri": "https://my-vault.vault.azure.net/"
  },
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"]
  },
  "Cluster": {
    "Enabled": true,
    "Secret": "shared-secret-here"
  },
  "AdminApi": {
    "Enabled": true,
    "Scope": "authagonal-admin"
  },
  "Authentication": {
    "CookieLifetimeHours": 48
  },
  "PasswordPolicy": {
    "MinLength": 8,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": true
  },
  "Email": {
    "ResendApiKey": "re_xxx",
    "SenderEmail": "noreply@example.com",
    "SenderName": "Example Auth"
  },
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com"]
    }
  ],
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "...",
      "ClientSecret": "...",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["gmail.com"]
    }
  ],
  "ProvisioningApps": {
    "backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret"
    }
  },
  "Clients": [
    {
      "ClientId": "web",
      "ClientName": "Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "MfaPolicy": "Enabled",
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
