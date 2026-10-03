---
layout: default
title: 迁移
locale: zh-Hans
---

# 从 Duende IdentityServer 迁移

`Authagonal.Migration` 包执行一次性迁移，将 Duende IdentityServer + SQL Server 中的数据迁移到 Authagonal 的存储中。同一个迁移引擎有两种使用方式：

- **托管运行器**（推荐）：运行在你的 Authagonal 宿主内部的后台服务，在部署时执行一次迁移，以获得集群领导权为前提，并且不会阻塞启动。
- **CLI**：`tools/Authagonal.Migration.Cli`，用于针对 Table Storage 目标进行本地/离线运行。

SqlClient 只存在于这个包中，因此不做迁移的宿主永远不会引入它。

## 托管运行器 {#hosted-runner}

在 `AddAuthagonal` 之后添加它（它依赖于存储、密钥提供程序和集群领导权）：

```csharp
builder.Services.AddAuthagonal(builder.Configuration, c => c.UseAzureStorage(blob, table));
builder.Services.AddAuthagonalDuendeMigration(builder.Configuration);

var app = builder.Build();
app.MapAuthagonalEndpoints();
app.MapAuthagonalDuendeMigration();   // GET /admin/migration/status
```

第二个 `Map` 调用是必需的，并且需要单独调用：这个包引用了 `Authagonal.Server`，因此 `MapAuthagonalEndpoints` 无法触及它。缺少这一调用时，`GET /admin/migration/status` 会返回 404，与 `IdentityAdmin` 策略拒绝你的访问时无法区分，此时运行器会在启动时记录一条警告说明这一点。

通过 `Migration` 配置节进行配置：

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

运行器会：

1. 等待 `StartupDelaySeconds`（让预置数据的服务先完成；启动永远不会被阻塞）。
2. 如果 `Version` 已经存在一个 `Completed` 且非 `DryRun` 的标记，则跳过。
3. 最多等待 `LeaseWaitMinutes` 以成为集群领导者（只有一个 Pod 运行迁移）。
4. 写入 `Started` 标记，运行迁移引擎，然后写入带有报告的 `Completed`/`Failed` 标记。

运行中途失去领导权会取消迁移引擎；新的领导者会重新运行，这是安全的，因为每一轮处理都是幂等的。可在 `GET /admin/migration/status`（受 `IdentityAdmin` 策略保护）查看进度。

## CLI {#cli}

```bash
docker run authagonal-migration \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  --DryRun true --UsersMode CreateOnly
```

（镜像名称之后不加 `--` 分隔符。）或者从源码运行：

```bash
dotnet run --project tools/Authagonal.Migration.Cli -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  --DryRun true
```

## 迁移哪些内容 {#what-gets-migrated}

| 来源（SQL Server） | 目标 | 说明 |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | 用户 + 邮箱/姓名索引 | ID 原样保留。声明折叠：`given_name`→FirstName，`family_name`→LastName，`company`→CompanyName，`org_id`→OrganizationId（xmlsoap 变体同样适用）；邮箱声明被丢弃；其余一切 → 自定义属性。空的密码哈希（仅使用外部 SSO 的用户）没有问题。BCrypt / ASP.NET Identity V3 哈希可直接验证，并在下次登录时升级为原生 PBKDF2。 |
| `AspNetUserLogins` | UserLogins | `409 Conflict` = 跳过（幂等） |
| `AspNetRoles` + `AspNetUserRoles` | 角色 + 用户角色关联 | 角色 ID→名称映射用于解析用户的角色分配 |
| `ApiScopes` + `IdentityResources` | 作用域 | 跳过已存在（预置）的名称；复制作用域声明 |
| Duende `Clients` + 子表 | 客户端 | 密钥按摘要长度标记为 `SHA256$`/`SHA512$`（其他的会被丢弃并给出警告）；跳过已过期的密钥；配置中预置的客户端优先（迁移时跳过） |
| Duende `ApiResources` | （扁平化） | 受众 → 由迁移创建的客户端；资源声明 → 由迁移创建的作用域 |
| `SamlProviderConfigurations` | SamlProviders + SsoDomains | `AllowedDomains` CSV 被拆分为 SSO 域名记录 |
| `OidcProviderConfigurations` | OidcProviders + SsoDomains | 同样拆分域名 |
| `AspNetUserTokens`（`AuthenticatorKey`、`RecoveryCodes`） | MfaCredentials | TOTP 密钥从 base32 转为受保护形式（`duende-totp`）；恢复码经过哈希（`duende-rc-{n}`）；如果用户已存在 MFA 则跳过该用户 |
| Duende `PersistedGrants`（刷新令牌） | 授权 | **针对标准 Duende 无法实现**，见下文。需要同时设置 `MigrateRefreshTokens` *和* `SourceGrantKeysAreUnhashed`；否则会跳过并给出警告，用户需要重新登录。 |

## 选项 {#options}

| 选项 | 默认值 | 说明 |
|---|---|---|
| `Enabled` | `false` | 托管运行器的总开关 |
| `DryRun` | `false` | 遍历来源并生成完整的验证报告（ID 字符集/长度、重复邮箱、表/列清单、每轮处理的计数），但不写入任何内容 |
| `Version` | `"1"` | 运行标记。递增它即可重新运行一次增量同步。只有 `Completed` 且非 `DryRun` 的标记会阻止重新运行 |
| `UsersMode` | `CreateOnly` | `CreateOnly` 跳过已存在的用户；`Upsert` 会覆盖。**切换后切勿使用 `Upsert`**，它会覆盖已重新哈希的密码和新的 MFA |
| `MigrateClients` | `true` | 迁移 OAuth 客户端。配置中预置的客户端始终优先，已存在的客户端会被跳过 |
| `MigrateRefreshTokens` | `false` | 包含有效的刷新令牌。需要 `SourceGrantKeysAreUnhashed` |
| `SourceGrantKeysAreUnhashed` | `false` | 声明来源的 `PersistedGrants.Key` 原样保存了句柄。只有使用自定义授权存储的 fork 版本才成立 |
| `Source:ConnectionString` | *（无）* | 来源 Duende SQL Server 的连接字符串 |
| `MaxDegreeOfParallelism` | `32` | 高数据量处理轮次（用户、外部登录、MFA、刷新令牌）的写入并发上限。对于规模较小或容易被限流的账户请调低；`1` 为完全顺序执行 |
| `LeaseWaitMinutes` | `10` | 托管运行器：等待集群领导权超过此时长后放弃；之后的重启会再次尝试 |
| `StartupDelaySeconds` | `30` | 托管运行器：开始前的延迟，以便预置数据的服务先完成，并且不阻塞启动 |

## 幂等性与增量同步 {#idempotency--delta-sweeps}

每一轮处理都是幂等的（已存在则跳过，MFA ID 具有确定性），因此可以安全地重新运行迁移。可以在切换前几天先运行一次，然后在临近切换时递增 `Version`，执行最后一次增量同步，以纳入此后注册的用户。已存在的记录会被跳过（在 `Upsert` 模式下则会被更新），永远不会重复。

## 不会迁移的内容 {#what-is-not-migrated}

- **针对标准 Duende 的有效刷新令牌。** Duende 的 `DefaultGrantStore` 从不持久化刷新令牌的句柄：`PersistedGrants.Key` 保存的是 `base64(SHA-256(handle + ":" + grantType))`，查找时会对提交的句柄再次进行哈希。因此无法从来源数据库中恢复句柄，迁移过来的记录将永远无法兑换，这比不迁移更糟糕，因为报告会把它们计为已创建，而问题直到切换后的第一次令牌刷新时才会暴露出来。请按照用户需要重新登录一次来规划切换，或者在过渡期间运行一个双读兼容层。`SourceGrantKeysAreUnhashed` 只为授权存储原样持久化句柄的 fork 版本而存在，并且这样的 fork 版本还需要自行负责把 `PersistedGrants.Data` 从 Duende 的 `RefreshToken` 结构转换为 `RefreshTokenData`。
- **SCIM 令牌和组**、**用户预配记录**：Duende 中没有对应项；从空开始。
- **签名密钥**：没有自动化。若要让现有令牌在切换后继续有效，请从 Duende 导出 RSA 签名密钥，并在临近切换时将其导入 `SigningKeys` 表。

## 切换策略 {#cutover-strategy}

1. 以不启用的状态部署（`Enabled=false`）。
2. `Enabled=true, DryRun=true` → 重启 → 在 `/admin/migration/status` 查看报告。
3. `DryRun=false` → 重启 → 确认标记为 `Completed`，并抽查登录情况。
4. 递增 `Version` 执行最后一次增量同步，然后将客户端/BFF 重新指向 Authagonal。**预计会有一次强制重新登录**，见上文。
5. 持续监控；回滚 = 重新指向未受改动的 Duende 部署。

## NDJSON 用户导入 {#ndjson-user-import}

同一个 `Authagonal.Migration` 包中还有第二个独立的导入来源：使用扁平的 NDJSON 文件（每行一个 JSON 对象）而不是实时数据库连接，并且只导入用户，不导入客户端、角色、作用域或联合配置。它专为迁移旧应用自有的用户表而设计（手工实现的 ASP.NET Identity 存储、导出为 bcrypt 的 Rails/Devise 表、使用 scrypt 的 Node 应用等），让用户可以继续使用原来的密码登录，同时在其下次成功登录时透明地重新哈希为原生 PBKDF2，这与上文 Duende 导入器所依赖的延迟重新哈希路径相同。

### 记录结构 {#record-schema}

每行一个 JSON 对象。`email` 是唯一的必填字段；其他所有字段都是可选的。**未知的顶层字段会导致该行失败**（默认严格模式），除非传入 `--AllowUnknownFields true`。

| 字段 | 类型 | 说明 |
|---|---|---|
| `email` | string | 必填。必须是看起来合理的邮箱地址。作为不区分大小写的去重键。 |
| `username` | string | `AuthUser` 中没有专门的列，存储在 `CustomAttributes["username"]` 中。 |
| `givenName` | string | → `AuthUser.FirstName` |
| `familyName` | string | → `AuthUser.LastName` |
| `displayName` | string | 没有专门的列，存储在 `CustomAttributes["displayName"]` 中。 |
| `emailVerified` | bool | → `AuthUser.EmailConfirmed`。缺省时默认为 `false`。 |
| `passwordHash` | string | → `AuthUser.PasswordHash`，**原样**存储。`PasswordHasher` 在登录时能识别的任何格式（bcrypt `$2a$`/`$2b$`/`$2x$`/`$2y$`、ASP.NET Identity V3、scrypt `$s2$`）都可直接验证，并从那时起升级为原生 PBKDF2。除了非空之外不做任何检查，格式错误的哈希只会在登录时验证失败，这与不经迁移时的情况相同。对于仅使用 SSO / 无密码的用户请省略。 |
| `roles` | string[] | → `AuthUser.Roles` |
| `organizationId` | string | → `AuthUser.OrganizationId` |
| `attributes` | object (string→string) | 合并到 `AuthUser.CustomAttributes` 中 |
| `phoneNumber` | string | → `AuthUser.Phone` |
| `disabled` | bool | → `AuthUser.IsActive = !disabled`。缺省时默认为启用状态。 |
| `createdAt` | string (ISO 8601) | → `AuthUser.CreatedAt`。缺省时默认为导入时间。 |
| `externalId` | string | → `AuthUser.ExternalId`，Duende 导入器用来存放来源数据库用户 ID 的同一个字段。 |

示例文件（5 行）：

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

目标（Azure Table Storage）和 PII 明文关卡与上文的 Duende CLI 相同，该来源会直接把 `AuthUser` 行写入 Table Storage，而不经过宿主注册的 `IFieldCipher`/`IIndexTokenizer`，因此除非 `--AllowPlaintextPii true` 确认目标两者均未配置，否则它会拒绝运行（或者你也可以改为把 `NdjsonUserImportEngine` 接入宿主自己的 DI 容器，在那里这些扩展点能够正常解析）。与 Duende CLI 不同，这里没有 `--AllowPlaintextSecrets` 关卡：该来源从不写入 MFA TOTP 种子或 OAuth 客户端密钥，只写入用户资料字段和原样存储的密码哈希。

### 选项 {#options-1}

| 选项 | 默认值 | 说明 |
|---|---|---|
| `--Input` | *（必填）* | NDJSON 文件的路径 |
| `--Target:ConnectionString` | *（必填）* | Azure Table Storage 连接字符串 |
| `--DryRun` | `false` | 解析并校验每一行，针对目标解析重复项，生成完整报告，不写入任何内容 |
| `--OnDuplicate` | `skip` | 当某行的邮箱（不区分大小写）已与现有用户匹配时如何处理：`skip`（保持不变，幂等）、`update`（将该行中出现的字段合并到现有用户上）或 `fail`（直接中止整个运行） |
| `--BatchSize` | `500` | 每隔多少行输出一条进度日志。这不是批量写入机制，`IUserStore` 没有批量 API，因此每次导入/更新仍然是一次存储调用 |
| `--AllowUnknownFields` | `false` | 接受并忽略上述结构之外的顶层 JSON 属性，而不是让该行失败 |
| `--ContinueOnError` | `false` | 即使有一行或多行解析/校验失败，也以 0 退出。不适用于 `--OnDuplicate fail`，后者无论是否设置此标志都会中止运行 |

### 汇总输出与退出码 {#summary-output--exit-codes}

报告以 JSON 格式输出：`TotalLines`、`Imported`、`Updated`、`Skipped`、`Failed`，以及前 20 条 `Failures`（`LineNumber` + `Reason`）。空行不计入任何项。退出码：

- `0`：成功（或者在 `--ContinueOnError true` 下有一行或多行失败）
- `1`：有一行或多行解析/校验失败，且未设置 `--ContinueOnError`
- `2`：运行被中止：`--OnDuplicate fail` 遇到了已存在的邮箱，或者缺少必需的选项

### 幂等性 {#idempotency}

在默认的 `--OnDuplicate skip` 下，使用未改动的文件重新运行时，第二次将不产生任何效果：邮箱已存在的每一行都计为跳过，不写入任何内容。`update` 也可以安全地重新运行（它总是重新应用相同的字段）；`fail` 适用于永远不应悄悄与现有账户冲突的一次性导入。

### 不会导入的内容 {#what-is-not-imported}

- **角色、作用域、OAuth 客户端、联合配置。** 该来源只包含用户：如果你还需要这些，请参见上文的 Duende 导入器。
- **MFA 凭据、外部登录。** 不属于该结构；请在导入后通过标准的 MFA 设置 / SSO 流程添加。
