---
layout: default
title: バックアップとリストア
locale: ja
---

# バックアップとリストア

Authagonal は、Azure Table Storage のデータをバックアップ・リストアするための 2 つの CLI ツールを提供しています。どちらも `tools/` ディレクトリにある .NET のコンソールアプリケーションで、`Authagonal.Backup` NuGet パッケージの薄いラッパーです。スケジュール実行、マルチテナント、ファイルシステム以外へのバックアップが必要なホストは、ライブラリを直接使えます ([ライブラリの利用](#using-the-library) を参照)。

## バックアップ {#backup}

```bash
dotnet run --project tools/Authagonal.Backup -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --output ./backups
```

### オプション {#options}

| オプション | 説明 |
|---|---|
| `--connection-string <conn>` | Azure Table Storage の接続文字列 (または環境変数 `STORAGE_CONNECTION_STRING` を設定) |
| `--output <dir>` | 出力ディレクトリ (既定: `./backups`) |
| `--incremental` | 前回のバックアップ以降に変更されたエンティティだけをバックアップする |
| `--tables <t1,t2,...>` | カンマ区切りのテーブルのリスト (既定: Authagonal のすべてのテーブル) |
| `--prefix <prefix>` | テーブル名のプレフィックス (マルチテナントのストレージ用) |
| `--gzip` | バックアップファイルを gzip で圧縮する (`.jsonl.gz`) |
| `--encryption-key <base64>` | 32 バイトの AES-256 鍵暗号化鍵。すべてのデータファイルを暗号化します。バックアップ先の**外部**に保管してください。`BACKUP_ENCRYPTION_KEY` からも読み取ります (こちらを推奨。後述)。 |
| `--manifest-key <base64>` | 32 バイト以上の HMAC 鍵。マニフェストに署名し、記録されたハッシュがファイルと一緒に書き換えられていないことをリストア時に証明できるようにします。バックアップ先の**外部**に保管してください。`BACKUP_MANIFEST_KEY` からも読み取ります (こちらを推奨。後述)。 |
| `--dry-run` | 書き込みを行わずに、バックアップされる内容を表示する |

### 出力形式 {#output-format}

バックアップのたびに、タイムスタンプ付きのディレクトリが作成されます。

```
backups/
  20260329-120000/          (full backup)
    Users.jsonl
    Clients.jsonl
    Grants.jsonl
    ...
    _manifest.json
  20260329-180000-incr/     (incremental, compressed)
    Users.jsonl.gz
    _tombstones.jsonl.gz
    _manifest.json
```

`--prefix` を指定すると、バックアップはプレフィックスの下に 1 階層深くネストされます: `backups/acmecorp/20260329-120000/`。これにより、2 つのテナントのフルバックアップが同じ秒に同じ `--output` ディレクトリに書き込まれても衝突しません。バックアップ ID 自体は、プレフィックスを含まない 1 秒単位の素の `yyyyMMdd-HHmmss[-incr]` タイムスタンプのままです。そのためネストがなければ、同じ秒にバックアップされた 2 つのプレフィックスは同一の ID を得て、結果として同一のディレクトリを使うことになります。そこからリストアするには、`--input` にネストされたディレクトリを指定します (`--input backups/acmecorp/20260329-120000`)。プレフィックスなしの実行は影響を受けず、上に示したフラットなレイアウトのままです。

各 `.jsonl` ファイルには、1 行に 1 つの JSON オブジェクト (テーブルエンティティ 1 つにつき 1 つ) が含まれます。`--gzip` を指定すると、ファイルは `.jsonl.gz` として圧縮されます。`_manifest.json` には、バックアップ ID、タイムスタンプ、モード (`full` または `incremental`)、圧縮の有無、増分のウォーターマーク、テーブルごとのエンティティ数、トゥームストーンの数、どのテーブルを変更ログ経由で読み取ったか (`ChangeLogTables`。null はフルスキャンでカバーされたことを意味します)、そして整合性検証のための SHA-256 のファイルハッシュが記録されます。

増分バックアップでは、ウォーターマーク以降の削除を記録する `_tombstones.jsonl(.gz)` ファイルも書き込まれます。削除された行ごとに 1 行で、`Table`、`PartitionKey`、`RowKey`、`DeletedAt` を持ちます。リストアはこれを再生するため、削除された行がよみがえることはありません ([トゥームストーンの再生](#tombstone-replay) を参照)。

エンティティの値は正確に往復します。バックアップされた各行は `"@v"` 形式マーカーを持ち、JSON では曖昧さなく表現できないすべての列について、明示的な `"{column}@odata.type"` 注釈 (`Edm.Guid`、`Edm.DateTime`、`Edm.Binary`、`Edm.Int64`、`Edm.Double`) が付きます。そのためリストアは、文字列化された値や推論し直した値ではなく、元の型で書き戻します。

### 整合性の検証 {#integrity-verification}

各バックアップのマニフェストには、ファイル名を SHA-256 ハッシュに対応付ける `FileHashes` ディクショナリが含まれます。リストア時には、各ファイルのデータがテーブルに到達する前に、そのファイルが記録されたハッシュと照合されます (照合はエンティティを適用するのと同じ読み取りで行われるため、検査したバイト列がそのまま書き込まれます)。検査に失敗したファイル、マニフェストに記載のないデータファイル、マニフェストに記載されているのにストアに存在しないファイルは、いずれもリストアを中止させます。整合性ハッシュが導入される前に書き込まれたバックアップ (`FileHashes` なし) は検証できないため、`--allow-unverified` を指定しない限り拒否されます。検証はプログラムから `RestoreOptions.VerifyIntegrity` (既定値 `true`) で無効にできます。

### 鍵はコマンドラインではなく環境変数で渡す {#pass-the-keys-by-environment-variable-not-on-the-command-line}

どちらのツールも `BACKUP_ENCRYPTION_KEY` と `BACKUP_MANIFEST_KEY` を読み取ります。スケジュール実行するバックアップではこれらを使ってください。

フラグはプロセスのコマンドラインになります。Kubernetes では、CronJob の spec に base64 の KEK と HMAC 鍵がそのまま含まれることになり、その名前空間の cronjob または Pod に対する `get`/`list` 権限を持つ者なら誰でも `kubectl get cronjob -o yaml` で両方を読み取れます。これは Secret を保持する者よりはるかに広いプリンシパルの集合であり、読み取り専用のダッシュボードや CI のサービスアカウントにも日常的に付与されている権限です。同じ値は、そのノード上の任意のプロセスから `/proc/<pid>/cmdline` で見えますし、コマンドを組み立てたシェルの履歴や CI のログにも残ります。`--connection-string` にはまさにこの理由で環境変数による経路が用意されていましたが、アーカイブを守る 2 つの鍵にはありませんでした。

```yaml
env:
  - name: BACKUP_ENCRYPTION_KEY
    valueFrom: { secretKeyRef: { name: authagonal-backup, key: encryption-key } }
  - name: BACKUP_MANIFEST_KEY
    valueFrom: { secretKeyRef: { name: authagonal-backup, key: manifest-key } }
```

両方が設定されている場合はフラグが優先されるため、対話的に行う一回限りのリストアは何も変える必要がありません。

ハッシュが立証するのはアーカイブがマニフェストと一致することであり、どちらかが本物であることではありません。マニフェストはデータと同じバックアップ先に置かれているため、`Clients.jsonl.gz` を書き換えられる者は、そのハッシュを記録した行も書き換えられます。`--manifest-key` はこの穴をふさぎます。バックアップはマニフェストの HMAC を計算し、リストアはそれを検証し、鍵はバックアップの書き込み側が到達できない場所に置かれます。**リストアはフェイルクローズです**。`--manifest-key` がない場合、警告ではなく拒否します。マニフェスト署名の導入前に書き込まれたアーカイブのための明示的なオプトアウトが `--allow-unauthenticated-manifest` です。

### 増分バックアップ {#incremental-backups}

`--incremental` を渡すと、最後に成功したバックアップ以降に変更されたエンティティだけをバックアップします。ツールはフィルタリングに Azure Table Storage 組み込みの `Timestamp` プロパティを使い、出力ディレクトリの `.lastbackup` ファイルで最高水位点を追跡します。

`.lastbackup` ファイルが存在しない場合、最初の増分実行はフルバックアップを行います。

増分の `Timestamp` フィルターはすべて、フィルタリングの前に小さな安全マージン (`BackupDefaults.WatermarkSkewMargin`、5 分) を差し引きます。ウォーターマークは呼び出し側の時計に由来する一方、行のタイムスタンプはストレージサービスが付与するため、マージンがなければ、時計のずれの範囲内でコミットされた変更は今回の実行でも以降のどの実行でも取りこぼされてしまいます。マージン分を読み直すと実行ごとに数行の重複が生じますが、リストアのアップサートの動作によって重複は解消されます。

### 既定のテーブル {#default-tables}

バックアップツールは既定で Authagonal のすべてのテーブルを含めます (`BackupDefaults.Tables`)。

`Users`, `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `UserOrganizations`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UpstreamRefreshTokens`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `ScimGroupRoleMappings`, `Roles`, `UserRoles`, `Scopes`, `AgentProfiles`, `ProvisioningApps`, `Organizations`, `OrganizationSlugs`, `OrganizationMembers`, `UserMemberships`

`AgentProfiles`、`UserRoles`、`UpstreamRefreshTokens` は意図的にこの集合に含まれています。これらがないと、リストアしたデプロイメントはバックアップ元より気付かないうちに弱くなります (エージェントクライアントは上限と同意の制限を失い、ロールは定義されていても誰も保持しておらず、上流のリフレッシュトークンは消えます)。

一時的なテーブル (`SamlReplayCache`、`OidcStateStore`、`RevokedTokens`) は、エントリがトークンの有効期間で限られるため、既定で除外されています。必要なら `--tables` で明示的に含めてください。`Tombstones` 変更ログテーブルはバックアップエンジンが別途扱うため、列挙しないでください。

### 署名鍵は既定で除外される {#signing-keys-are-excluded-by-default}

`SigningKeys` テーブルは既定のテーブルリストに含まれていますが、**既定ではバックアップから除外**されます (`BackupOptions.IncludeSigningKeys`、既定値 `false`。CLI がこれを有効にすることはありません)。ローカル (テーブル保存) の鍵ソースを使うホストでは、このテーブルが JWT 署名の**秘密鍵**を保持しており、それを平文のバックアップファイルに書き出すと、バックアップを読める者は誰でもトークンを偽造できるようになります。これは**すべての**ホストに当てはまります。JWT の署名は Vault Transit に委任されないため、`SigningKeys` テーブルが秘密鍵を保持しない構成は存在しません。

> ⚠️ `BackupOptions.IncludeSigningKeys` でオプトインするのは、バックアップ先自体が保存時に暗号化され、アクセス制御されている場合だけにしてください。バックアップの他の部分にも同じことが言えます。既定の**平文**のシークレットプロバイダーでは、バックアップには上流の OIDC クライアントシークレットと TOTP / MFA のシードも平文で含まれます。[構成 → シークレットプロバイダー](configuration#secret-provider) を参照してください。

### `--tables` はバックアップ対象の集合からテーブルを指定する {#--tables-names-tables-from-the-backup-set}

指定できるのは、宣言されたテーブルの集合 (`BackupDefaults.Tables`、または後述の `KnownTables`) に含まれるテーブルだけです。集合外のテーブルは、リストアが拒否するアーカイブを作る前に、最初の段階で拒否されます。リストアの許可リストも同じ集合なので、それ以外のテーブルを含むアーカイブは、書き込み、ハッシュ計算、署名まではできても、決してリストアできません。一時的なテーブル (失効トークンのエントリ、レート制限のカウンター) は意図的に除外されています。これらは自然に期限切れになり、古い行をリストアしても何の意味もないからです。

## リストア {#restore}

```bash
dotnet run --project tools/Authagonal.Restore -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --input ./backups/20260329-120000
```

### オプション {#options-1}

| オプション | 説明 |
|---|---|
| `--connection-string <conn>` | Azure Table Storage の接続文字列 (または環境変数 `STORAGE_CONNECTION_STRING` を設定) |
| `--input <dir>` | リストア元のバックアップディレクトリ |
| `--mode <mode>` | リストアモード: `upsert` (既定)、`merge`、`clean` |
| `--tables <t1,t2,...>` | リストアするテーブルのカンマ区切りのリスト (既定: バックアップ内のすべての `.jsonl`/`.jsonl.gz` ファイル) |
| `--prefix <prefix>` | テーブル名のプレフィックス (マルチテナントのストレージ用) |
| `--clean-env <env>` | `--mode clean` と併用し、この環境の行 (PartitionKey のプレフィックス `<env>|`) だけを消去する |
| `--allow-clean-from-incremental` | 増分バックアップに対する `--mode clean` を許可する |
| `--allow-clean-all-envs` | `--clean-env` なしの `--mode clean` を許可し、テーブル全体を空にする |
| `--encryption-key <base64>` | バックアップの書き込みに使われた 32 バイトの鍵暗号化鍵。暗号化されたアーカイブでは必須です。`BACKUP_ENCRYPTION_KEY` からも読み取ります。 |
| `--manifest-key <base64>` | バックアップの署名に使われた HMAC 鍵。`--allow-unauthenticated-manifest` を指定しない限り**必須**です。`BACKUP_MANIFEST_KEY` からも読み取ります。 |
| `--allow-unauthenticated-manifest` | `--manifest-key` なしでリストアし、破損は検出できても改ざんは検出できないハッシュを受け入れる |
| `--allow-unverified` | マニフェストにファイルハッシュがまったくないバックアップをリストアする |
| `--dry-run` | 書き込みを行わずに、リストアされる内容を表示する |

### リストアモード {#restore-modes}

| モード | 動作 |
|---|---|
| `upsert` | 各エンティティを挿入または置換します。既存のデータは上書きされます。 |
| `merge` | 挿入またはマージします。バックアップにない既存のプロパティは保持されます。 |
| `clean` | リストアの前に、各テーブルの既存データをすべて削除します。 |

gzip で圧縮されたバックアップファイル (`.jsonl.gz`) は自動的に検出・展開されます。追加のフラグは不要です。

### トゥームストーンの再生 {#tombstone-replay}

データファイルの後、リストアはバックアップの `_tombstones` ファイルを適用します。記録された各キーは、リストアされたテーブルから削除されます (`RestoreOptions.ApplyTombstones`、既定値 `true`)。増分バックアップにおける削除は、アップサートと同じくその状態の一部です。これを飛ばすと、フルバックアップと一連の増分バックアップをリストアするときに、削除された行 (GDPR に基づいて消去された行を含む) がよみがえってしまいます。フルバックアップにはトゥームストーンファイルはありません。フルバックアップに続けて増分バックアップをリストアする場合は、古いものから順に適用してください。そうすれば、後で再作成された行は先に行われた削除の後に書き込まれます。トゥームストーンファイルのハッシュも、データファイルと同様にマニフェストと照合されます。

### 型の正確な往復 {#exact-type-round-trip}

`"@v"` 形式マーカー付きで書き込まれた行は明示的な EDM 型注釈を持つため、リストアは元の列の型 (`Int64`、`Guid`、`Binary`、`DateTime`、`Double`) を正確に再構築します。注釈のない文字列は文字列としてリストアされます。マーカーのない旧形式のバックアップファイルは、値の形からの推論にフォールバックします。これは古いバックアップを引き続きリストアできるようにするためだけに残されています (推論では、GUID や日付の形をした文字列の列が誤った型になることがあります)。

### 終了コード {#exit-codes}

| コード | 意味 |
|---|---|
| `0` | 成功 |
| `1` | エラー (引数の不足、無効な入力) |
| `2` | 部分的な成功 (一部のエンティティでエラーが発生) |

### 独自のテーブルを持つホスト: `KnownTables` {#a-host-with-its-own-tables-knowntables}

`BackupOptions.KnownTables` と `RestoreOptions.KnownTables` (いずれも `string[]?`。null は `BackupDefaults.Tables` を意味します) は、あなたのデプロイメントのアーカイブが正当に指定できるテーブルの集合を宣言します。Authagonal のデータと並べて独自のデータを保存し、両者を 1 つのアーカイブとしてバックアップするホストは、これを設定します。そうしないと、それらのテーブルを指定するバックアップはすべて最初の段階で拒否され (`BackupService.cs:48`)、リストアはすべてそのアーカイブを拒否します (`RestoreService.cs:17,167`)。

- 集合はホストが事前に宣言します。アーカイブから導き出されることは決してありません。それがこの仕組みの要点です。リストアがどのテーブルに書き込むかをアーカイブが決めることはできません。
- 両方のオプションに**同じ**集合を渡してください。より広い集合で取得したバックアップは、同じ集合を宣言したリストアでしかリストアできません。

## ライブラリの利用 {#using-the-library}

`Authagonal.Backup` NuGet パッケージは、バックグラウンドサービスや独自のオーケストレーション向けに、同じ操作をプログラムから利用できるようにしています。

| 型 | 目的 |
|---|---|
| `BackupService` | `TableServiceClient` に対してフルバックアップまたは増分バックアップを実行し、`IBackupTarget` に書き込む |
| `RestoreService` | ハッシュを検証し、バックアップを Table Storage に書き戻す |
| `MergeService` | フルバックアップと増分バックアップ (およびそのトゥームストーン) をストリーミングし、1 つの現在の状態のビューにまとめる |
| `RollupService` | 増分バックアップを新しいフルバックアップに畳み込み、必要に応じて入力を削除する |
| `BackupOptions` / `RestoreOptions` | 実行ごとの構成 |
| `BackupDefaults` | 既定のテーブルリストと変更ログのプリセット |
| `IBackupSource` / `IBackupTarget` | ストレージの抽象化。`FileSystemBackupSource` / `FileSystemBackupTarget` が組み込みの実装です。Blob Storage などに書き込むには `IBackupTarget` を実装してください。 |

```csharp
var serviceClient = new TableServiceClient(connectionString);
var target = new FileSystemBackupTarget("./backups");
var options = new BackupOptions { Incremental = true, Gzip = true };
var manifest = await new BackupService(serviceClient, target, options).RunAsync(ct);
```

### 変更ログ駆動の増分バックアップ {#change-log-driven-incrementals}

Azure Table Storage がインデックスを作成するのは `PartitionKey` と `RowKey` だけなので、`Timestamp` でフィルタリングする増分バックアップも、結局は各テーブルのフルスキャンになります。これを避けるため、Authagonal のストアは `IChangeWriter` の拡張点 (`Authagonal.Core`) を通じてすべての変更を変更ログに記録します。Azure 向けには `TableChangeWriter` (`Authagonal.AzureProvider`) が実装しています。変更ログは 1 つの物理テーブルで、名前は今も `Tombstones` です。PK = 論理テーブル名、RK = `"{pk}|{rk}"`、`Op` 列は `"U"` (アップサート) または `"D"` (削除)、そして正規の値を保持する `OrigPK`/`OrigRK` 列があります (元の PartitionKey に `|` が含まれていると複合 RowKey の分割が曖昧になるため、バックアップの読み取り側はこれらの列を信頼し、旧形式の行の場合だけ分割にフォールバックします)。各キーは 1 行だけを持つ (アップサートで置換する) ため、バックアップのウィンドウ内で最後に行われた操作が優先されます。

変更ログの経路を有効にすると、増分バックアップは、ウォーターマーク以降のそのテーブルの `Op = "U"` の変更ログエントリを列挙し、テーブルをスキャンする代わりに各行の現在の状態をポイント読み取りします。この機能は**オプトインで、既定では無効**です。`BackupOptions.ChangeLoggedTables` が null または空なら、すべてのテーブルはスキャンの経路のままです。そのため、この仕組みは意図的に切り替えるまで何もしない状態で出荷されます (変更を記録する前のコードが変更した行を、デプロイによって気付かないうちに取りこぼすことはありません)。プリセットは 2 つあります。

| プリセット | 内容 |
|---|---|
| `BackupDefaults.ChangeLoggedTables` | 書き込みが変更ログに完全に記録されるテーブル: `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `UserOrganizations`, `ScimGroupRoleMappings`, `ProvisioningApps`, `Organizations`, `OrganizationSlugs`, `OrganizationMembers`, `UserMemberships` |
| `BackupDefaults.ChangeLoggedTablesWithUsers` | 同じ集合に `Users` を加えたもの。Users のログイン状態の書き込みは意図的に記録されない (ホットパスで価値が低い) ため、このプリセットは**後述のフルスキャンによる補完も実行する場合にのみ安全**です |

マニフェストの `ChangeLogTables` プロパティには、その実行で変更ログ経由で読み取ったテーブルが列挙されます。null または空は、その実行がフルスキャンでカバーされたこと (フルバックアップ、通常のスキャンによる増分、または補完のスキャン) を意味します。

### フルスキャンによる補完 {#full-scan-backstop}

変更ログへの記録は書き込みを取りこぼすことがある (ログイン状態のフィールド、ストアを経由しない書き込み、デプロイ中に変更を記録する前のコードを実行している Pod) ため、変更ログによる増分バックアップは、定期的なフルの再スキャンと組み合わせてください。その実行では、`BackupOptions.WatermarkOverride` を最後にフルカバーのスキャンを行ったタイムスタンプに設定し、`ChangeLoggedTables` は未設定のままにします。すると増分バックアップは、そのスキャン以降のウィンドウ全体にわたって `Timestamp` でフィルタリングし、変更ログが記録しなかったものをすべて拾い上げます。1 時間ごとの変更ログによる増分バックアップに、1 日 1 回の補完を組み合わせるのが妥当な頻度です。削除は、自己修復の手段がない唯一の種類の変更です (現在の行をスキャンしても、なくなった行は見えません)。そのため、ストアはデータ行を削除する**前**に削除のトゥームストーンを書き込みます。

補完を含むすべての増分フィルターは、ウォーターマークから `BackupDefaults.WatermarkSkewMargin` (5 分) を差し引きます。バックアップ後に変更ログを削除する呼び出し側は、削除の範囲を同じマージンで制限しなければなりません。そうしないと、次の実行がまだ必要とする行を削除してしまいます。

### ロールアップ {#rollups}

`RollupService.RollupAsync` は、フルバックアップとその増分バックアップを新しいフルバックアップにマージします。`RollupAndCleanAsync` はさらに、その後で入力を削除します。オプションの `newBackupId` パラメーターで結果に名前を付けます (null ならタイムスタンプの ID が導出されます)。特別に保持するスナップショット (たとえば週次のロールアップ) は、ここで ID を渡さなければなりません。ID に基づく保持は、マニフェストではなく物理的なバックアップ ID を列挙するからです。

マージの際、トゥームストーンはタイムスタンプの順序に従って適用されます。削除が記録済みの行を取り除くのは、その行の `Timestamp` がトゥームストーンの `DeletedAt` より後でない場合だけです。ウィンドウの早い時点で削除され、後で再作成されたキーにはトゥームストーンと現在の行の記録の両方があり、再作成された行はロールアップ後も残ります。`DeletedAt` のない旧形式のトゥームストーンは無条件に削除します。

## Docker {#docker}

バックアップツールには、CI で実行したり .NET SDK をインストールせずに実行したりするための Dockerfile (`tools/Authagonal.Backup/Dockerfile`) が付属しています。

```bash
docker build -f tools/Authagonal.Backup/Dockerfile -t authagonal-backup .

docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  authagonal-backup --output /backups
```

リストアツールにはイメージがありません。.NET SDK で実行してください (`dotnet run --project tools/Authagonal.Restore`)。

## バックアップのスケジュール {#scheduling-backups}

本番環境では、バックアップツールをスケジュールに従って実行してください (例: 毎日のフルバックアップ + 1 時間ごとの増分バックアップ)。

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```

ライブラリを組み込んだホストでは通常、変更ログの経路を有効にした 1 時間ごとの増分バックアップ、毎日のフルスキャンによる補完、そして増分の連鎖を一定の長さに抑えるための定期的なロールアップを実行します。
