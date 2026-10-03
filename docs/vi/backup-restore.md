---
layout: default
title: Sao lưu & khôi phục
locale: vi
---

# Sao lưu & khôi phục

Authagonal cung cấp hai công cụ CLI để sao lưu và khôi phục dữ liệu Azure Table Storage. Cả hai đều là ứng dụng console .NET trong thư mục `tools/`, và cả hai đều là lớp bọc mỏng bên ngoài gói NuGet `Authagonal.Backup`. Các host cần sao lưu theo lịch, đa tenant, hoặc không dựa trên hệ thống tệp có thể dùng trực tiếp thư viện (xem [Dùng thư viện](#using-the-library)).

## Sao lưu {#backup}

```bash
dotnet run --project tools/Authagonal.Backup -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --output ./backups
```

### Tùy chọn {#options}

| Tùy chọn | Mô tả |
|---|---|
| `--connection-string <conn>` | Connection string của Azure Table Storage (hoặc đặt biến môi trường `STORAGE_CONNECTION_STRING`) |
| `--output <dir>` | Thư mục đầu ra (mặc định: `./backups`) |
| `--incremental` | Chỉ sao lưu các entity đã thay đổi kể từ lần sao lưu trước |
| `--tables <t1,t2,...>` | Danh sách bảng phân cách bằng dấu phẩy (mặc định: mọi bảng của Authagonal) |
| `--prefix <prefix>` | Tiền tố tên bảng (cho lưu trữ đa tenant) |
| `--gzip` | Nén các tệp sao lưu bằng gzip (`.jsonl.gz`) |
| `--encryption-key <base64>` | Khóa mã hóa khóa (KEK) AES-256 dài 32 byte. Mã hóa mọi tệp dữ liệu. Hãy giữ nó **bên ngoài** đích sao lưu. Cũng đọc từ `BACKUP_ENCRYPTION_KEY` (nên dùng cách này; xem bên dưới). |
| `--manifest-key <base64>` | Khóa HMAC dài ≥32 byte. Ký manifest để bước khôi phục chứng minh được rằng các hash đã ghi không bị viết lại cùng với các tệp. Hãy giữ nó **bên ngoài** đích sao lưu. Cũng đọc từ `BACKUP_MANIFEST_KEY` (nên dùng cách này; xem bên dưới). |
| `--dry-run` | Hiển thị những gì sẽ được sao lưu mà không ghi |

### Định dạng đầu ra {#output-format}

Mỗi lần sao lưu tạo một thư mục gắn dấu thời gian:

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

Với `--prefix`, các bản sao lưu được lồng sâu thêm một cấp, dưới tiền tố: `backups/acmecorp/20260329-120000/`.
Chính điều này giúp bản sao lưu đầy đủ của hai tenant cùng rơi vào một thư mục `--output` trong cùng
một giây không bị đụng nhau. Bản thân backup id vẫn chỉ là một dấu thời gian `yyyyMMdd-HHmmss[-incr]` trần với
độ phân giải một giây và không chứa tiền tố, nên nếu không lồng thư mục, hai tiền tố được sao lưu trong
cùng một giây sẽ nhận id giống hệt nhau và do đó cùng một thư mục. Trỏ `--input` vào
thư mục lồng đó để khôi phục từ nó (`--input backups/acmecorp/20260329-120000`); các lần chạy không có tiền tố
không bị ảnh hưởng và giữ bố cục phẳng như trên.

Mỗi tệp `.jsonl` chứa một đối tượng JSON trên mỗi dòng (mỗi đối tượng ứng với một entity trong bảng). Với `--gzip`, các tệp được nén thành `.jsonl.gz`. `_manifest.json` ghi lại backup id, dấu thời gian, chế độ (`full` hoặc `incremental`), kiểu nén, watermark tăng dần, số entity theo từng bảng, số tombstone, những bảng nào (nếu có) được đọc qua change-log (`ChangeLogTables`, null nghĩa là có độ phủ quét toàn bộ), và hash SHA-256 của các tệp để kiểm tra tính toàn vẹn.

Bản sao lưu tăng dần cũng ghi một tệp `_tombstones.jsonl(.gz)` ghi lại các thao tác xóa kể từ watermark: mỗi dòng ứng với một hàng bị xóa, gồm `Table`, `PartitionKey`, `RowKey` và `DeletedAt`. Bước khôi phục phát lại các dòng này để hàng đã xóa không bị sống lại (xem [Phát lại tombstone](#tombstone-replay)).

Giá trị entity được giữ nguyên chính xác qua vòng sao lưu và khôi phục: mỗi hàng được sao lưu mang một dấu định dạng `"@v"` và một chú thích `"{column}@odata.type"` tường minh (`Edm.Guid`, `Edm.DateTime`, `Edm.Binary`, `Edm.Int64`, `Edm.Double`) cho mọi cột mà JSON không thể biểu diễn một cách không mơ hồ, nên bước khôi phục ghi lại đúng kiểu gốc thay vì giá trị bị chuyển thành chuỗi hoặc bị suy luận lại.

### Kiểm tra tính toàn vẹn {#integrity-verification}

Mỗi manifest sao lưu có một từ điển `FileHashes` ánh xạ tên tệp tới hash SHA-256 của chúng. Trong khi khôi phục, mỗi tệp được kiểm tra với hash đã ghi (dựa trên chính lượt đọc mà các entity được áp dụng từ đó, nên những byte được kiểm tra chính là những byte được ghi) trước khi bất kỳ dữ liệu nào của nó tới được bảng. Một tệp không qua được bước kiểm tra, một tệp dữ liệu không có trong manifest, hay một tệp có trong manifest nhưng thiếu trong kho đều làm hủy bước khôi phục. Các bản sao lưu được ghi trước khi có tính năng hash toàn vẹn (không có `FileHashes`) không thể được kiểm tra và bị từ chối trừ khi có `--allow-unverified`. Có thể tắt việc kiểm tra bằng code qua `RestoreOptions.VerifyIntegrity` (mặc định `true`).

### Truyền khóa qua biến môi trường, không qua dòng lệnh {#pass-the-keys-by-environment-variable-not-on-the-command-line}

Cả hai công cụ đều đọc `BACKUP_ENCRYPTION_KEY` và `BACKUP_MANIFEST_KEY`, và một bản sao lưu theo lịch nên dùng chúng.

Một cờ sẽ trở thành một phần dòng lệnh của tiến trình. Trong Kubernetes, điều đó có nghĩa là đặc tả CronJob chứa nguyên văn
KEK dạng base64 và khóa HMAC, nên bất kỳ ai có quyền `get`/`list` trên cronjob hoặc pod trong namespace đó đều đọc được cả hai
bằng `kubectl get cronjob -o yaml`, một tập chủ thể rộng hơn nhiều so với những ai nắm giữ Secret, và là quyền
thường được cấp cho dashboard chỉ đọc và service account của CI. Các giá trị đó cũng hiển thị trong
`/proc/<pid>/cmdline` với mọi tiến trình trên node, và trong bất kỳ lịch sử shell hay log CI nào đã ghép nên
lệnh. `--connection-string` đã có đường truyền qua biến môi trường chính vì lý do này; hai khóa bảo vệ
kho lưu trữ thì trước đây chưa có.

```yaml
env:
  - name: BACKUP_ENCRYPTION_KEY
    valueFrom: { secretKeyRef: { name: authagonal-backup, key: encryption-key } }
  - name: BACKUP_MANIFEST_KEY
    valueFrom: { secretKeyRef: { name: authagonal-backup, key: manifest-key } }
```

Nếu đặt cả hai thì cờ vẫn được ưu tiên, nên một lần khôi phục thủ công tương tác không cần thay đổi gì.

Hash xác lập rằng kho lưu trữ khớp với manifest, chứ không chứng minh cái nào là xác thực: manifest nằm trên cùng đích với dữ liệu, nên ai có thể viết lại `Clients.jsonl.gz` cũng có thể viết lại dòng ghi hash của nó. `--manifest-key` bịt lỗ hổng đó: bản sao lưu tính HMAC cho manifest, bước khôi phục kiểm tra nó, và khóa nằm ở nơi mà bên ghi bản sao lưu không với tới được. **Khôi phục đóng khi lỗi**: không có `--manifest-key` thì nó từ chối thay vì chỉ cảnh báo, và `--allow-unauthenticated-manifest` là lựa chọn tường minh để bỏ qua cho các kho lưu trữ được ghi trước khi có tính năng ký manifest.

### Sao lưu tăng dần {#incremental-backups}

Truyền `--incremental` để chỉ sao lưu các entity bị sửa đổi kể từ lần sao lưu thành công gần nhất. Công cụ dùng thuộc tính `Timestamp` dựng sẵn của Azure Table Storage để lọc và theo dõi watermark cao nhất trong một tệp `.lastbackup` ở thư mục đầu ra.

Nếu chưa có tệp `.lastbackup`, lần chạy tăng dần đầu tiên sẽ thực hiện sao lưu đầy đủ.

Mọi bộ lọc `Timestamp` tăng dần đều trừ đi một biên an toàn nhỏ (`BackupDefaults.WatermarkSkewMargin`, 5 phút) trước khi lọc. Watermark đến từ đồng hồ của bên gọi trong khi dấu thời gian của hàng do dịch vụ lưu trữ đóng, nên nếu không có biên này, một thay đổi được commit trong khoảng lệch đồng hồ sẽ bị lần chạy này và mọi lần chạy sau bỏ sót. Việc đọc lại phần biên chỉ tốn vài hàng trùng lặp mỗi lần chạy, và ngữ nghĩa upsert của bước khôi phục sẽ loại trùng.

### Bảng mặc định {#default-tables}

Công cụ sao lưu mặc định bao gồm mọi bảng của Authagonal (`BackupDefaults.Tables`):

`Users`, `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `UserOrganizations`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UpstreamRefreshTokens`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `ScimGroupRoleMappings`, `Roles`, `UserRoles`, `Scopes`, `AgentProfiles`, `ProvisioningApps`, `Organizations`, `OrganizationSlugs`, `OrganizationMembers`, `UserMemberships`

`AgentProfiles`, `UserRoles` và `UpstreamRefreshTokens` được đưa vào tập này có chủ đích: thiếu chúng, một bản triển khai được khôi phục sẽ âm thầm yếu hơn bản đã được sao lưu (agent client mất giới hạn trần và các cổng chấp thuận, vai trò được định nghĩa nhưng không ai nắm giữ, refresh token phía trên biến mất).

Các bảng tạm thời (`SamlReplayCache`, `OidcStateStore`, `RevokedTokens`) mặc định bị loại trừ vì các mục trong đó bị giới hạn bởi thời gian sống của token; hãy đưa chúng vào một cách tường minh bằng `--tables` nếu cần. Bảng change-log `Tombstones` được engine sao lưu xử lý riêng và không nên được liệt kê.

### Khóa ký mặc định bị loại trừ {#signing-keys-are-excluded-by-default}

Bảng `SigningKeys` có trong danh sách bảng mặc định nhưng **mặc định bị lọc khỏi bản sao lưu** (`BackupOptions.IncludeSigningKeys`, mặc định `false`; CLI không bao giờ bật nó). Với các host dùng nguồn khóa cục bộ (lưu trong bảng), bảng này chứa **khóa riêng** dùng để ký JWT, và ghi nó vào một tệp sao lưu plaintext sẽ cho phép bất kỳ ai đọc được bản sao lưu giả mạo token. Điều này áp dụng cho **mọi** host: việc ký JWT không được ủy thác cho Vault Transit, nên không có cấu hình nào mà bảng `SigningKeys` không chứa khóa riêng.

> ⚠️ Chỉ chọn bật qua `BackupOptions.IncludeSigningKeys` khi bản thân đích sao lưu đã được mã hóa khi lưu trữ và được kiểm soát truy cập. Điều tương tự áp dụng cho phần còn lại của bản sao lưu: với secret provider **plaintext** mặc định, bản sao lưu cũng chứa client secret OIDC phía upstream và seed TOTP / MFA dưới dạng văn bản rõ. Xem [Cấu hình → Secret Provider](configuration#secret-provider).

### `--tables` chỉ định bảng thuộc tập sao lưu {#--tables-names-tables-from-the-backup-set}

Chỉ những bảng trong tập bảng đã khai báo (`BackupDefaults.Tables`, hoặc `KnownTables` bên dưới) mới được chỉ định. Một bảng nằm ngoài tập đó bị từ chối ngay từ đầu thay vì
tạo ra một kho lưu trữ mà bước khôi phục sẽ từ chối. Danh sách cho phép của bước khôi phục chính là tập đó, nên một kho lưu trữ chỉ định
bất kỳ thứ gì khác có thể được ghi, tính hash và ký, rồi không bao giờ khôi phục được. Các bảng tạm thời (mục token
bị thu hồi, bộ đếm giới hạn tốc độ) bị loại trừ có chủ đích: chúng tự hết hạn, và khôi phục các hàng cũ
chẳng đạt được gì.

## Khôi phục {#restore}

```bash
dotnet run --project tools/Authagonal.Restore -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --input ./backups/20260329-120000
```

### Tùy chọn {#options-1}

| Tùy chọn | Mô tả |
|---|---|
| `--connection-string <conn>` | Connection string của Azure Table Storage (hoặc đặt biến môi trường `STORAGE_CONNECTION_STRING`) |
| `--input <dir>` | Thư mục sao lưu để khôi phục từ đó |
| `--mode <mode>` | Chế độ khôi phục: `upsert` (mặc định), `merge`, hoặc `clean` |
| `--tables <t1,t2,...>` | Danh sách bảng cần khôi phục, phân cách bằng dấu phẩy (mặc định: mọi tệp `.jsonl`/`.jsonl.gz` trong bản sao lưu) |
| `--prefix <prefix>` | Tiền tố tên bảng (cho lưu trữ đa tenant) |
| `--clean-env <env>` | Với `--mode clean`, chỉ xóa các hàng của môi trường này (tiền tố PartitionKey `<env>|`) |
| `--allow-clean-from-incremental` | Cho phép `--mode clean` với một bản sao lưu tăng dần |
| `--allow-clean-all-envs` | Cho phép `--mode clean` không kèm `--clean-env`, làm trống toàn bộ bảng |
| `--encryption-key <base64>` | Khóa mã hóa khóa dài 32 byte đã dùng khi ghi bản sao lưu. Bắt buộc với kho lưu trữ được mã hóa. Cũng đọc từ `BACKUP_ENCRYPTION_KEY`. |
| `--manifest-key <base64>` | Khóa HMAC đã dùng để ký bản sao lưu. **Bắt buộc** trừ khi có `--allow-unauthenticated-manifest`. Cũng đọc từ `BACKUP_MANIFEST_KEY`. |
| `--allow-unauthenticated-manifest` | Khôi phục mà không có `--manifest-key`, chấp nhận các hash chỉ phát hiện được hư hỏng chứ không phát hiện được giả mạo |
| `--allow-unverified` | Khôi phục một bản sao lưu mà manifest hoàn toàn không có hash tệp nào |
| `--dry-run` | Hiển thị những gì sẽ được khôi phục mà không ghi |

### Chế độ khôi phục {#restore-modes}

| Chế độ | Hành vi |
|---|---|
| `upsert` | Chèn hoặc thay thế từng entity. Dữ liệu hiện có bị ghi đè. |
| `merge` | Chèn hoặc hợp nhất. Các thuộc tính hiện có không nằm trong bản sao lưu được giữ lại. |
| `clean` | Xóa mọi dữ liệu hiện có trong từng bảng trước khi khôi phục. |

Các tệp sao lưu nén gzip (`.jsonl.gz`) được tự động phát hiện và giải nén; không cần thêm cờ nào.

### Phát lại tombstone {#tombstone-replay}

Sau các tệp dữ liệu, bước khôi phục áp dụng tệp `_tombstones` của bản sao lưu: mỗi khóa được ghi lại sẽ bị xóa khỏi các bảng đã khôi phục (`RestoreOptions.ApplyTombstones`, mặc định `true`). Các thao tác xóa của một bản tăng dần cũng là một phần trạng thái của nó không kém các thao tác upsert; bỏ qua chúng sẽ làm sống lại các hàng đã xóa, kể cả những hàng đã bị xóa theo GDPR, khi khôi phục một chuỗi gồm bản đầy đủ cộng các bản tăng dần. Bản sao lưu đầy đủ không mang tệp tombstone. Khi khôi phục một bản đầy đủ theo sau là các bản tăng dần, hãy áp dụng chúng từ cũ nhất tới mới nhất để một lần tạo lại về sau được ghi sau một lần xóa trước đó. Hash của tệp tombstone được kiểm tra với manifest giống như các tệp dữ liệu.

### Giữ nguyên chính xác kiểu dữ liệu {#exact-type-round-trip}

Các hàng được ghi với dấu định dạng `"@v"` mang chú thích kiểu EDM tường minh, nên bước khôi phục tái tạo đúng kiểu cột gốc (`Int64`, `Guid`, `Binary`, `DateTime`, `Double`); một chuỗi không có chú thích được khôi phục thành chuỗi. Các tệp sao lưu kiểu cũ không có dấu này sẽ quay về cách suy luận dựa trên hình dạng giá trị, chỉ được giữ lại để các bản sao lưu cũ vẫn khôi phục được (suy luận có thể gán sai kiểu cho các cột chuỗi trông giống GUID hoặc ngày tháng).

### Mã thoát {#exit-codes}

| Mã | Ý nghĩa |
|---|---|
| `0` | Thành công |
| `1` | Lỗi (thiếu tham số, đầu vào không hợp lệ) |
| `2` | Thành công một phần (một số entity gặp lỗi) |

### Host có bảng riêng: `KnownTables` {#a-host-with-its-own-tables-knowntables}

`BackupOptions.KnownTables` và `RestoreOptions.KnownTables` (đều là `string[]?`, null nghĩa là `BackupDefaults.Tables`) khai báo tập bảng mà một kho lưu trữ của bản triển khai của bạn được phép chỉ định một cách hợp lệ. Một host lưu dữ liệu riêng bên cạnh dữ liệu của Authagonal và sao lưu cả hai trong cùng một kho lưu trữ phải đặt giá trị này, nếu không mọi bản sao lưu chỉ định các bảng đó sẽ bị từ chối ngay từ đầu (`BackupService.cs:48`) và mọi lần khôi phục sẽ từ chối kho lưu trữ đó (`RestoreService.cs:17,167`).

- Host khai báo tập này từ trước. Nó không bao giờ được suy ra từ kho lưu trữ, và đó chính là mục đích: kho lưu trữ không được quyền chọn những bảng mà bước khôi phục sẽ ghi.
- Truyền **cùng một** tập cho cả hai tùy chọn. Một bản sao lưu được tạo với tập rộng hơn chỉ khôi phục được qua một lần khôi phục khai báo cùng tập đó.

## Dùng thư viện {#using-the-library}

Gói NuGet `Authagonal.Backup` cung cấp các thao tác tương tự để dùng bằng code, cho các background service hoặc điều phối tùy chỉnh:

| Kiểu | Mục đích |
|---|---|
| `BackupService` | Chạy sao lưu đầy đủ hoặc tăng dần với một `TableServiceClient`, ghi vào một `IBackupTarget` |
| `RestoreService` | Kiểm tra hash và ghi một bản sao lưu trở lại Table Storage |
| `MergeService` | Đọc tuần tự một bản sao lưu đầy đủ cộng các bản tăng dần (và tombstone của chúng) thành một khung nhìn trạng thái hiện tại |
| `RollupService` | Gộp các bản tăng dần thành một bản sao lưu đầy đủ mới, có thể xóa các bản đầu vào |
| `BackupOptions` / `RestoreOptions` | Cấu hình cho từng lần chạy |
| `BackupDefaults` | Danh sách bảng mặc định và các preset change-log |
| `IBackupSource` / `IBackupTarget` | Lớp trừu tượng lưu trữ; `FileSystemBackupSource` / `FileSystemBackupTarget` là các hiện thực dựng sẵn. Hiện thực `IBackupTarget` để ghi vào blob storage hoặc nơi khác. |

```csharp
var serviceClient = new TableServiceClient(connectionString);
var target = new FileSystemBackupTarget("./backups");
var options = new BackupOptions { Incremental = true, Gzip = true };
var manifest = await new BackupService(serviceClient, target, options).RunAsync(ct);
```

### Sao lưu tăng dần dựa trên change-log {#change-log-driven-incrementals}

Azure Table Storage chỉ đánh chỉ mục `PartitionKey` và `RowKey`, nên một bản sao lưu tăng dần lọc theo `Timestamp` vẫn là một lượt quét toàn bộ từng bảng. Để tránh điều đó, các store của Authagonal ghi lại mọi thay đổi vào một change-log qua điểm mở rộng `IChangeWriter` (`Authagonal.Core`), được hiện thực cho Azure bởi `TableChangeWriter` (`Authagonal.AzureProvider`). Đó là một bảng vật lý duy nhất, vẫn mang tên `Tombstones`: PK = tên bảng logic, RK = `"{pk}|{rk}"`, một cột `Op` có giá trị `"U"` (upsert) hoặc `"D"` (xóa), cùng các cột `OrigPK`/`OrigRK` có thẩm quyền (một ký tự `|` bên trong PartitionKey gốc khiến việc tách RowKey ghép trở nên mơ hồ, nên bên đọc bản sao lưu tin vào các cột này và chỉ quay về cách tách chuỗi với các hàng kiểu cũ). Mỗi khóa giữ một hàng (upsert-replace), nên thao tác cuối cùng trong một cửa sổ sao lưu là thao tác được giữ lại.

Khi bật đường change-log, một bản sao lưu tăng dần liệt kê các mục change-log `Op = "U"` của một bảng kể từ watermark và đọc điểm từng hàng đang tồn tại thay vì quét bảng. Tính năng này **phải chủ động bật và mặc định tắt**: `BackupOptions.ChangeLoggedTables` null hoặc rỗng nghĩa là mọi bảng vẫn đi theo đường quét, nên cơ chế này được phát hành ở trạng thái chưa hoạt động cho tới khi có một lần bật có chủ đích (một lần triển khai không thể âm thầm bỏ sót các hàng bị thay đổi bởi code từ trước khi có cơ chế ghi nhận). Hai preset:

| Preset | Nội dung |
|---|---|
| `BackupDefaults.ChangeLoggedTables` | Các bảng có mọi thao tác ghi đều được change-log ghi nhận đầy đủ: `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `UserOrganizations`, `ScimGroupRoleMappings`, `ProvisioningApps`, `Organizations`, `OrganizationSlugs`, `OrganizationMembers`, `UserMemberships` |
| `BackupDefaults.ChangeLoggedTablesWithUsers` | Cùng tập đó cộng thêm `Users`. Các thao tác ghi trạng thái đăng nhập của Users cố ý không được ghi nhận (đường nóng, giá trị thấp), nên preset này **chỉ an toàn khi bạn cũng chạy lượt quét toàn bộ làm lưới an toàn bên dưới** |

Thuộc tính `ChangeLogTables` của manifest liệt kê những bảng mà một lần chạy đã đọc qua change-log; null hoặc rỗng nghĩa là lần chạy có độ phủ quét toàn bộ (một bản sao lưu đầy đủ, một bản tăng dần quét thông thường, hoặc một lượt quét lưới an toàn).

### Lượt quét toàn bộ làm lưới an toàn {#full-scan-backstop}

Vì việc ghi nhận qua change-log có thể bỏ sót thao tác ghi (các trường trạng thái đăng nhập, bên ghi không đi qua store, các pod chạy code từ trước khi có cơ chế ghi nhận trong lúc triển khai), hãy kết hợp các bản tăng dần dựa trên change-log với một lượt quét lại toàn bộ định kỳ. Đặt `BackupOptions.WatermarkOverride` thành dấu thời gian của lượt quét có độ phủ toàn bộ gần nhất và để trống `ChangeLoggedTables` cho lần chạy đó: khi đó bản tăng dần lọc theo `Timestamp` trên toàn bộ khoảng thời gian kể từ lượt quét đó, thu lại mọi thứ mà change-log chưa từng ghi nhận. Một lượt lưới an toàn hằng ngày song song với các bản tăng dần dựa trên change-log hằng giờ là nhịp độ hợp lý. Xóa là loại thay đổi duy nhất không tự phục hồi được (một lượt quét hàng đang tồn tại không thể thấy một hàng đã biến mất), và đó là lý do các store ghi tombstone xóa **trước** khi xóa hàng dữ liệu.

Mọi bộ lọc tăng dần, kể cả lưới an toàn, đều trừ `BackupDefaults.WatermarkSkewMargin` (5 phút) khỏi watermark; bên gọi nào dọn change-log sau một lần sao lưu phải giới hạn việc dọn theo cùng biên đó, nếu không họ sẽ xóa các hàng mà lần chạy kế tiếp vẫn cần.

### Rollup {#rollups}

`RollupService.RollupAsync` hợp nhất một bản sao lưu đầy đủ và các bản tăng dần của nó thành một bản sao lưu đầy đủ mới; `RollupAndCleanAsync` còn xóa các bản đầu vào sau đó. Tham số tùy chọn `newBackupId` đặt tên cho kết quả (null thì suy ra một id dạng dấu thời gian); một snapshot được giữ lại đặc biệt (ví dụ rollup hằng tuần) phải truyền id của nó ở đây, vì cơ chế lưu giữ theo id liệt kê các backup id vật lý, không phải manifest.

Trong khi hợp nhất, tombstone được áp dụng theo thứ tự thời gian: một thao tác xóa chỉ loại bỏ một hàng đã ghi nhận khi `Timestamp` của hàng không muộn hơn `DeletedAt` của tombstone. Một khóa bị xóa sớm trong cửa sổ và được tạo lại sau đó có cả một tombstone lẫn một bản ghi nhận đang tồn tại, và hàng được tạo lại sẽ tồn tại qua bước rollup. Các tombstone kiểu cũ không có `DeletedAt` sẽ xóa vô điều kiện.

## Docker {#docker}

Công cụ sao lưu có sẵn một Dockerfile (`tools/Authagonal.Backup/Dockerfile`) để chạy trong CI hoặc không cần cài .NET SDK:

```bash
docker build -f tools/Authagonal.Backup/Dockerfile -t authagonal-backup .

docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  authagonal-backup --output /backups
```

Công cụ khôi phục không có image; hãy chạy nó bằng .NET SDK (`dotnet run --project tools/Authagonal.Restore`).

## Lên lịch sao lưu {#scheduling-backups}

Để dùng trong production, hãy chạy công cụ sao lưu theo lịch (ví dụ bản đầy đủ hằng ngày + bản tăng dần hằng giờ):

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```

Các host nhúng thư viện thường chạy bản tăng dần hằng giờ với đường change-log được bật, một lượt quét toàn bộ làm lưới an toàn hằng ngày, và các lần rollup định kỳ để giới hạn độ dài chuỗi tăng dần.
