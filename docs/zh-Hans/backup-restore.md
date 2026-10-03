---
layout: default
title: 备份与恢复
locale: zh-Hans
---

# 备份与恢复

Authagonal 提供两个用于备份和恢复 Azure Table Storage 数据的命令行工具。两者都是位于 `tools/` 目录中的 .NET 控制台应用，并且都只是 `Authagonal.Backup` NuGet 包的轻量封装。需要定时备份、多租户备份或非文件系统备份的宿主可以直接使用该库（参见[使用库](#using-the-library)）。

## 备份 {#backup}

```bash
dotnet run --project tools/Authagonal.Backup -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --output ./backups
```

### 选项 {#options}

| 选项 | 说明 |
|---|---|
| `--connection-string <conn>` | Azure Table Storage 连接字符串（或设置 `STORAGE_CONNECTION_STRING` 环境变量） |
| `--output <dir>` | 输出目录（默认：`./backups`） |
| `--incremental` | 只备份自上次备份以来发生变更的实体 |
| `--tables <t1,t2,...>` | 以逗号分隔的表列表（默认：所有 Authagonal 表） |
| `--prefix <prefix>` | 表名前缀（用于多租户存储） |
| `--gzip` | 使用 gzip 压缩备份文件（`.jsonl.gz`） |
| `--encryption-key <base64>` | 32 字节的 AES-256 密钥加密密钥。用于加密每个数据文件。请将其存放在备份目标**之外**。也会读取 `BACKUP_ENCRYPTION_KEY`（推荐使用该方式，见下文）。 |
| `--manifest-key <base64>` | 不少于 32 字节的 HMAC 密钥。用于对清单签名，使恢复时能够证明记录的哈希没有随文件一起被改写。请将其存放在备份目标**之外**。也会读取 `BACKUP_MANIFEST_KEY`（推荐使用该方式，见下文）。 |
| `--dry-run` | 显示将要备份的内容，但不写入 |

### 输出格式 {#output-format}

每次备份都会创建一个带时间戳的目录：

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

使用 `--prefix` 时，备份会在前缀下再嵌套一层：`backups/acmecorp/20260329-120000/`。
正是这一点避免了两个租户的全量备份在同一秒写入同一个 `--output` 目录时
发生冲突。备份 ID 本身仍是一个精度为一秒、不含前缀的裸
`yyyyMMdd-HHmmss[-incr]` 时间戳，因此如果没有这层嵌套，在同一秒内备份的两个前缀
会得到完全相同的 ID，从而落入完全相同的目录。要从中恢复，请将 `--input` 指向
嵌套后的目录（`--input backups/acmecorp/20260329-120000`）；不带前缀的运行不受
影响，仍保持上面所示的扁平布局。

每个 `.jsonl` 文件每行包含一个 JSON 对象（每个表实体一行）。使用 `--gzip` 时，文件压缩为 `.jsonl.gz`。`_manifest.json` 记录了备份 ID、时间戳、模式（`full` 或 `incremental`）、压缩方式、增量水位线、每个表的实体数量、墓碑记录数量、哪些表（如果有）是通过变更日志读取的（`ChangeLogTables`，为 null 表示全量扫描覆盖），以及用于完整性校验的 SHA-256 文件哈希。

增量备份还会写入一个 `_tombstones.jsonl(.gz)` 文件，记录自水位线以来的删除：每个被删除的行一行，包含 `Table`、`PartitionKey`、`RowKey` 和 `DeletedAt`。恢复时会重放这些记录，使已删除的行不会复活（参见[墓碑记录重放](#tombstone-replay)）。

实体值可以精确往返：每个备份的行都带有一个 `"@v"` 格式标记，并且对于每个 JSON 无法无歧义表示的列，都带有显式的 `"{column}@odata.type"` 注解（`Edm.Guid`、`Edm.DateTime`、`Edm.Binary`、`Edm.Int64`、`Edm.Double`），因此恢复时写回的是原始类型，而不是字符串化或重新推断的值。

### 完整性校验 {#integrity-verification}

每个备份清单都包含一个 `FileHashes` 字典，将文件名映射到其 SHA-256 哈希。恢复期间，每个文件在其任何数据写入表之前，都会根据记录的哈希进行验证（验证所用的正是应用实体时的那次读取，因此被检查的字节就是被写入的字节）。文件未通过检查、数据文件未列在清单中、或清单中列出的文件在存储中缺失，都会中止恢复。在引入完整性哈希之前写入的备份（没有 `FileHashes`）无法验证，除非使用 `--allow-unverified`，否则会被拒绝。可以通过 `RestoreOptions.VerifyIntegrity`（默认 `true`）以编程方式禁用验证。

### 通过环境变量而不是命令行传递密钥 {#pass-the-keys-by-environment-variable-not-on-the-command-line}

两个工具都会读取 `BACKUP_ENCRYPTION_KEY` 和 `BACKUP_MANIFEST_KEY`，定时备份应当使用它们。

命令行标志会成为进程的命令行。在 Kubernetes 中，这意味着 CronJob 规约中会原样包含
base64 编码的 KEK 和 HMAC 密钥，因此任何在该命名空间中对 cronjob 或 pod 拥有 `get`/`list` 权限的人，都可以
用 `kubectl get cronjob -o yaml` 读到这两个密钥；这类主体的范围远大于 Secret 的持有者，而且这一权限
经常授予只读仪表板和 CI 服务账户。同样的值对节点上的任何进程都可见于
`/proc/<pid>/cmdline`，也会出现在拼装该命令的 shell 历史记录或 CI 日志中。`--connection-string` 正是出于这个原因
早已支持通过环境变量传入；而保护归档的两个密钥此前却没有。

```yaml
env:
  - name: BACKUP_ENCRYPTION_KEY
    valueFrom: { secretKeyRef: { name: authagonal-backup, key: encryption-key } }
  - name: BACKUP_MANIFEST_KEY
    valueFrom: { secretKeyRef: { name: authagonal-backup, key: manifest-key } }
```

如果两者都设置了，命令行标志仍然优先，因此交互式的一次性恢复无需任何改动。

哈希能确认归档与清单相符，但不能确认两者中的任何一个是真实的：清单与数据位于同一个目标上，因此能够改写 `Clients.jsonl.gz` 的人，也能改写记录其哈希的那一行。`--manifest-key` 弥补了这一点：备份时对清单计算 HMAC，恢复时进行验证，而密钥存放在备份写入方无法触及的地方。**恢复默认拒绝**：没有 `--manifest-key` 时，它会拒绝恢复而不仅仅是发出警告；`--allow-unauthenticated-manifest` 是针对清单签名功能出现之前写入的归档的显式例外选项。

### 增量备份 {#incremental-backups}

传入 `--incremental`，只备份自上次成功备份以来修改过的实体。该工具使用 Azure Table Storage 内置的 `Timestamp` 属性进行过滤，并在输出目录的 `.lastbackup` 文件中记录高水位线。

如果不存在 `.lastbackup` 文件，第一次增量运行会执行全量备份。

每个增量 `Timestamp` 过滤器在过滤前都会减去一个小的安全余量（`BackupDefaults.WatermarkSkewMargin`，5 分钟）。水位线来自调用方的时钟，而行时间戳由存储服务打上，因此如果不这样做，在时钟偏差范围内提交的变更会被本次以及之后的每次运行漏掉。重新读取这段余量，每次运行只会多出少量重复行，而恢复的 upsert 语义会对其去重。

### 默认表 {#default-tables}

备份工具默认包含所有 Authagonal 表（`BackupDefaults.Tables`）：

`Users`、`UserEmails`、`UserFirstNames`、`UserLastNames`、`UserLogins`、`UserExternalIds`、`UserEmailDomains`、`UserEmailLocalPrefixes`、`UserOrganizations`、`Clients`、`Grants`、`GrantsBySubject`、`GrantsByExpiry`、`SigningKeys`、`SsoDomains`、`SamlProviders`、`OidcProviders`、`UpstreamRefreshTokens`、`UserProvisions`、`MfaCredentials`、`MfaChallenges`、`MfaWebAuthnIndex`、`ScimTokens`、`ScimGroups`、`ScimGroupExternalIds`、`ScimGroupRoleMappings`、`Roles`、`UserRoles`、`Scopes`、`AgentProfiles`、`ProvisioningApps`、`Organizations`、`OrganizationSlugs`、`OrganizationMembers`、`UserMemberships`

`AgentProfiles`、`UserRoles` 和 `UpstreamRefreshTokens` 是有意纳入的：没有它们，恢复后的部署会在不知不觉中比备份时的部署更弱（智能体客户端失去其权限上限和同意检查，角色虽有定义却没有任何人持有，上游刷新令牌全部消失）。

临时表（`SamlReplayCache`、`OidcStateStore`、`RevokedTokens`）默认不纳入，因为其条目受令牌生命周期约束；如有需要，可通过 `--tables` 显式包含。`Tombstones` 变更日志表由备份引擎单独处理，不应列出。

### 签名密钥默认不纳入 {#signing-keys-are-excluded-by-default}

`SigningKeys` 表在默认表列表中，但**默认会从备份中过滤掉**（`BackupOptions.IncludeSigningKeys`，默认 `false`；命令行工具从不启用它）。对于使用本地（存储在表中的）密钥来源的宿主，此表保存的是 JWT 签名**私钥**，将其写入明文备份文件，会让任何能读取备份的人伪造令牌。这适用于**每一个**宿主：JWT 签名不会委托给 Vault Transit，因此不存在 `SigningKeys` 表不保存私钥的配置。

> ⚠️ 只有当备份目标本身启用了静态加密并有访问控制时，才通过 `BackupOptions.IncludeSigningKeys` 选择纳入。备份的其余部分也是如此：使用默认的**明文**密钥提供程序时，备份中还会以明文形式包含上游 OIDC 客户端密钥和 TOTP / MFA 种子。参见[配置 → 密钥提供程序](configuration#secret-provider)。

### `--tables` 指定的是备份集合中的表 {#--tables-names-tables-from-the-backup-set}

只能指定已声明表集合（`BackupDefaults.Tables`，或下文的 `KnownTables`）中的表。集合之外的表会被预先拒绝，而不是
生成一个恢复时会被拒绝的归档。恢复的允许列表正是同一个集合，因此指定了
其他表的归档可以被写入、计算哈希并签名，却永远无法恢复。临时表（已撤销令牌
条目、限流计数器）是有意排除的：它们会自行过期，恢复过时的行
没有任何意义。

## 恢复 {#restore}

```bash
dotnet run --project tools/Authagonal.Restore -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --input ./backups/20260329-120000
```

### 选项 {#options-1}

| 选项 | 说明 |
|---|---|
| `--connection-string <conn>` | Azure Table Storage 连接字符串（或设置 `STORAGE_CONNECTION_STRING` 环境变量） |
| `--input <dir>` | 要从中恢复的备份目录 |
| `--mode <mode>` | 恢复模式：`upsert`（默认）、`merge` 或 `clean` |
| `--tables <t1,t2,...>` | 以逗号分隔的要恢复的表列表（默认：备份中所有 `.jsonl`/`.jsonl.gz` 文件） |
| `--prefix <prefix>` | 表名前缀（用于多租户存储） |
| `--clean-env <env>` | 与 `--mode clean` 一起使用时，只清除该环境的行（PartitionKey 前缀为 `<env>|`） |
| `--allow-clean-from-incremental` | 允许对增量备份使用 `--mode clean` |
| `--allow-clean-all-envs` | 允许在不带 `--clean-env` 的情况下使用 `--mode clean`，即清空整个表 |
| `--encryption-key <base64>` | 写入备份时所用的 32 字节密钥加密密钥。加密的归档必须提供。也会读取 `BACKUP_ENCRYPTION_KEY`。 |
| `--manifest-key <base64>` | 对备份签名时所用的 HMAC 密钥。除非使用 `--allow-unauthenticated-manifest`，否则**必须**提供。也会读取 `BACKUP_MANIFEST_KEY`。 |
| `--allow-unauthenticated-manifest` | 在没有 `--manifest-key` 的情况下恢复，接受只能检测损坏、无法检测篡改的哈希 |
| `--allow-unverified` | 恢复清单中完全没有文件哈希的备份 |
| `--dry-run` | 显示将要恢复的内容，但不写入 |

### 恢复模式 {#restore-modes}

| 模式 | 行为 |
|---|---|
| `upsert` | 插入或替换每个实体。现有数据会被覆盖。 |
| `merge` | 插入或合并。保留备份中没有的现有属性。 |
| `clean` | 恢复前删除每个表中的所有现有数据。 |

gzip 压缩的备份文件（`.jsonl.gz`）会被自动检测并解压，无需额外的标志。

### 墓碑记录重放 {#tombstone-replay}

在处理完数据文件之后，恢复会应用备份的 `_tombstones` 文件：从恢复后的表中删除每个记录的键（`RestoreOptions.ApplyTombstones`，默认 `true`）。增量备份中的删除与其中的 upsert 一样，都是其状态的一部分；如果跳过它们，在恢复“全量加增量”序列时，已删除的行就会复活，包括那些依据 GDPR 抹除的行。全量备份不带墓碑记录文件。在恢复一个全量备份及其后续增量备份时，请按从旧到新的顺序应用，使后面的重新创建发生在前面的删除之后。墓碑记录文件的哈希与数据文件一样，会根据清单进行验证。

### 精确的类型往返 {#exact-type-round-trip}

带有 `"@v"` 格式标记写入的行携带显式的 EDM 类型注解，因此恢复时会重建与原始完全相同的列类型（`Int64`、`Guid`、`Binary`、`DateTime`、`Double`）；没有注解的字符串按字符串恢复。没有该标记的旧版备份文件会退回到基于形态的推断，保留这种推断只是为了让旧备份仍可恢复（推断可能会把形似 GUID 或形似日期的字符串列识别成错误的类型）。

### 退出码 {#exit-codes}

| 代码 | 含义 |
|---|---|
| `0` | 成功 |
| `1` | 错误（缺少参数、输入无效） |
| `2` | 部分成功（部分实体出错） |

### 拥有自己的表的宿主：`KnownTables` {#a-host-with-its-own-tables-knowntables}

`BackupOptions.KnownTables` 和 `RestoreOptions.KnownTables`（两者都是 `string[]?`，为 null 表示 `BackupDefaults.Tables`）声明了你的部署的归档可以合法指定的表集合。如果宿主在 Authagonal 的数据旁边存放了自己的数据，并把两者作为一个归档一起备份，就需要设置它；否则，每个指定了这些表的备份都会被预先拒绝（`BackupService.cs:48`），每次恢复也都会拒绝该归档（`RestoreService.cs:17,167`）。

- 由宿主预先声明该集合。它绝不是从归档中推导出来的，这正是关键所在：归档无权决定恢复会写入哪些表。
- 向两个选项传入**相同**的集合。用更宽集合创建的备份，只能通过声明了同一集合的恢复来还原。

## 使用库 {#using-the-library}

`Authagonal.Backup` NuGet 包以编程方式公开了相同的操作，供后台服务或自定义编排使用：

| 类型 | 用途 |
|---|---|
| `BackupService` | 针对 `TableServiceClient` 运行全量或增量备份，写入 `IBackupTarget` |
| `RestoreService` | 验证哈希，并将备份写回 Table Storage |
| `MergeService` | 将一个全量备份及其增量备份（以及它们的墓碑记录）以流的方式合并为一个当前状态视图 |
| `RollupService` | 将增量备份折叠为一个新的全量备份，可选择删除输入 |
| `BackupOptions` / `RestoreOptions` | 每次运行的配置 |
| `BackupDefaults` | 默认表列表和变更日志预设 |
| `IBackupSource` / `IBackupTarget` | 存储抽象；`FileSystemBackupSource` / `FileSystemBackupTarget` 是内置实现。实现 `IBackupTarget` 即可写入 Blob 存储或其他位置。 |

```csharp
var serviceClient = new TableServiceClient(connectionString);
var target = new FileSystemBackupTarget("./backups");
var options = new BackupOptions { Incremental = true, Gzip = true };
var manifest = await new BackupService(serviceClient, target, options).RunAsync(ct);
```

### 由变更日志驱动的增量备份 {#change-log-driven-incrementals}

Azure Table Storage 只对 `PartitionKey` 和 `RowKey` 建立索引，因此按 `Timestamp` 过滤的增量备份仍然是对每个表的全量扫描。为避免这种情况，Authagonal 的存储会通过 `IChangeWriter` 扩展点（`Authagonal.Core`）把每次变更记录到变更日志中，其 Azure 实现为 `TableChangeWriter`（`Authagonal.AzureProvider`）。它是一个物理表，名称仍为 `Tombstones`：PK = 逻辑表名，RK = `"{pk}|{rk}"`，`Op` 列为 `"U"`（upsert）或 `"D"`（删除），另有权威的 `OrigPK`/`OrigRK` 列（原始 PartitionKey 中如果含有 `|`，拆分复合 RowKey 就会产生歧义，因此备份读取器以这两列为准，只有对旧行才退回到拆分方式）。每个键只保留一行（upsert-replace），因此备份窗口内的最后一次操作生效。

启用变更日志路径后，增量备份会枚举某个表自水位线以来 `Op = "U"` 的变更日志条目，并逐一点读对应的现存行，而不是扫描整个表。该功能是**可选启用的，默认关闭**：`BackupOptions.ChangeLoggedTables` 为 null 或空时，所有表都保持在扫描路径上，因此该机制在被有意开启之前不会生效（这样部署就不会悄无声息地漏掉由未接入捕获的旧代码修改的行）。两个预设：

| 预设 | 内容 |
|---|---|
| `BackupDefaults.ChangeLoggedTables` | 写入已被变更日志完整捕获的表：`UserEmails`、`UserFirstNames`、`UserLastNames`、`UserLogins`、`UserExternalIds`、`UserEmailDomains`、`UserEmailLocalPrefixes`、`UserOrganizations`、`ScimGroupRoleMappings`、`ProvisioningApps`、`Organizations`、`OrganizationSlugs`、`OrganizationMembers`、`UserMemberships` |
| `BackupDefaults.ChangeLoggedTablesWithUsers` | 同一集合外加 `Users`。Users 表中登录状态的写入有意不被捕获（热路径，价值低），因此该预设**只有在同时运行下文的全量扫描兜底时才是安全的** |

清单的 `ChangeLogTables` 属性列出了本次运行中哪些表是通过变更日志读取的；为 null 或空表示本次运行具有全量扫描覆盖（全量备份、普通的扫描式增量备份，或兜底扫描）。

### 全量扫描兜底 {#full-scan-backstop}

由于变更日志捕获可能会漏掉写入（登录状态字段、不经过存储的写入方、部署期间仍在运行未接入捕获的旧代码的 Pod），请将变更日志增量备份与定期的全量重新扫描搭配使用。将 `BackupOptions.WatermarkOverride` 设置为上一次全覆盖扫描的时间戳，并在该次运行中不设置 `ChangeLoggedTables`：这样增量备份就会在自那次扫描以来的整个窗口内按 `Timestamp` 过滤，捕获变更日志从未记录到的任何内容。每天一次兜底扫描配合每小时一次的变更日志增量备份是合理的节奏。删除是唯一一类无法自我修复的变更（扫描现存行看不到已经消失的行），这就是为什么存储会在删除数据行**之前**写入删除墓碑记录。

所有增量过滤器（包括兜底扫描）都会从水位线中减去 `BackupDefaults.WatermarkSkewMargin`（5 分钟）；在备份后清理变更日志的调用方必须以相同的余量限定清理范围，否则会删除下一次运行仍然需要的行。

### 汇总 {#rollups}

`RollupService.RollupAsync` 将一个全量备份及其增量备份合并为一个新的全量备份；`RollupAndCleanAsync` 还会在之后删除输入。可选的 `newBackupId` 参数为结果命名（为 null 时生成时间戳 ID）；专门保留的快照（例如每周汇总）必须在此传入其 ID，因为基于 ID 的保留策略列出的是物理备份 ID，而不是清单。

合并期间，墓碑记录按时间戳顺序应用：只有当行的 `Timestamp` 不晚于墓碑记录的 `DeletedAt` 时，删除才会移除已捕获的行。在窗口早期被删除、之后又被重新创建的键，既有墓碑记录又有现存的捕获，重新创建的行会在汇总后保留下来。没有 `DeletedAt` 的旧版墓碑记录会无条件删除。

## Docker {#docker}

备份工具附带一个 Dockerfile（`tools/Authagonal.Backup/Dockerfile`），用于在 CI 中运行，或在不安装 .NET SDK 的情况下运行：

```bash
docker build -f tools/Authagonal.Backup/Dockerfile -t authagonal-backup .

docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  authagonal-backup --output /backups
```

恢复工具没有镜像；请使用 .NET SDK 运行它（`dotnet run --project tools/Authagonal.Restore`）。

## 定时备份 {#scheduling-backups}

在生产环境中，请按计划运行备份工具（例如每天一次全量备份 + 每小时一次增量备份）：

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```

嵌入该库的宿主通常会开启变更日志路径、每小时运行增量备份，每天进行一次全量扫描兜底，并定期汇总以限制增量链的长度。
