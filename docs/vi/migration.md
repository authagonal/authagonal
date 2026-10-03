---
layout: default
title: Di chuyển dữ liệu
locale: vi
---

# Di chuyển từ Duende IdentityServer

Gói `Authagonal.Migration` thực hiện một lần di chuyển duy nhất từ Duende IdentityServer + SQL
Server sang các store của Authagonal. Cùng một engine có thể được dùng theo hai cách:

- **Hosted runner** (khuyến nghị): một background service bên trong host Authagonal của bạn, chạy
  việc di chuyển một lần khi triển khai, chỉ chạy trên node đang giữ quyền leader của cluster, mà không chặn quá trình khởi động.
- **CLI**: `tools/Authagonal.Migration.Cli`, dùng cho các lần chạy cục bộ/offline nhắm vào đích Table Storage.

SqlClient chỉ nằm trong gói này, nên các host không di chuyển dữ liệu sẽ không bao giờ phải kéo theo nó.

## Hosted runner {#hosted-runner}

Thêm nó sau `AddAuthagonal` (nó phụ thuộc vào các store, secret provider và quyền leader của cluster):

```csharp
builder.Services.AddAuthagonal(builder.Configuration, c => c.UseAzureStorage(blob, table));
builder.Services.AddAuthagonalDuendeMigration(builder.Configuration);

var app = builder.Build();
app.MapAuthagonalEndpoints();
app.MapAuthagonalDuendeMigration();   // GET /admin/migration/status
```

Lời gọi `Map` thứ hai là bắt buộc và tách biệt: gói này tham chiếu `Authagonal.Server`, nên
`MapAuthagonalEndpoints` không thể với tới nó. Thiếu lời gọi này, `GET /admin/migration/status` sẽ trả 404,
không thể phân biệt với việc policy `IdentityAdmin` từ chối bạn, và lần chạy sẽ ghi một cảnh báo lúc khởi động
nói rõ điều đó.

Cấu hình qua mục `Migration`:

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

Runner sẽ:

1. Chờ `StartupDelaySeconds` (các dịch vụ nạp sẵn dữ liệu chạy xong trước; quá trình khởi động không bao giờ bị chặn).
2. Bỏ qua nếu đã có một marker `Completed`, không phải `DryRun`, cho `Version`.
3. Chờ tối đa `LeaseWaitMinutes` để trở thành leader của cluster (chỉ một pod chạy việc di chuyển).
4. Ghi một marker `Started`, chạy engine, rồi ghi một marker `Completed`/`Failed` kèm báo cáo.

Mất quyền leader giữa chừng sẽ hủy engine; leader mới sẽ chạy lại, điều này an toàn vì mọi lượt chạy đều
idempotent. Kiểm tra tiến độ tại `GET /admin/migration/status` (được bảo vệ bởi policy `IdentityAdmin`).

## CLI {#cli}

```bash
docker run authagonal-migration \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  --DryRun true --UsersMode CreateOnly
```

(Không có dấu phân tách `--` sau tên image.) Hoặc từ mã nguồn:

```bash
dotnet run --project tools/Authagonal.Migration.Cli -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  --DryRun true
```

## Những gì được di chuyển {#what-gets-migrated}

| Nguồn (SQL Server) | Đích | Ghi chú |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Người dùng + chỉ mục email/tên | Id được giữ nguyên văn. Gộp claim: `given_name`→FirstName, `family_name`→LastName, `company`→CompanyName, `org_id`→OrganizationId (cả các biến thể xmlsoap); claim email bị bỏ; mọi thứ khác → thuộc tính tùy chỉnh. Hash mật khẩu null (người dùng chỉ dùng SSO bên ngoài) không thành vấn đề. Hash BCrypt / ASP.NET Identity V3 xác minh được mà không cần thay đổi và được nâng cấp lên PBKDF2 gốc ở lần đăng nhập kế tiếp. |
| `AspNetUserLogins` | UserLogins | `409 Conflict` = bỏ qua (idempotent) |
| `AspNetRoles` + `AspNetUserRoles` | Role + liên kết role của người dùng | Bảng ánh xạ id role→tên phân giải các phân công cho người dùng |
| `ApiScopes` + `IdentityResources` | Scope | Các tên đã tồn tại (từ dữ liệu nạp sẵn) bị bỏ qua; claim của scope được sao chép |
| `Clients` của Duende + các bảng con | Client | Secret được gắn nhãn `SHA256$`/`SHA512$` theo độ dài digest (các loại khác bị bỏ kèm cảnh báo); secret hết hạn bị bỏ qua; client được nạp sẵn từ cấu hình được ưu tiên (bản di chuyển bị bỏ qua) |
| `ApiResources` của Duende | (làm phẳng) | Audience → các client do việc di chuyển tạo ra; claim của resource → các scope do việc di chuyển tạo ra |
| `SamlProviderConfigurations` | SamlProviders + SsoDomains | CSV `AllowedDomains` được tách thành các bản ghi tên miền SSO |
| `OidcProviderConfigurations` | OidcProviders + SsoDomains | Tách tên miền theo cùng cách |
| `AspNetUserTokens` (`AuthenticatorKey`, `RecoveryCodes`) | MfaCredentials | Secret TOTP base32→được bảo vệ (`duende-totp`); mã khôi phục được băm (`duende-rc-{n}`); người dùng bị bỏ qua nếu đã có MFA |
| `PersistedGrants` của Duende (refresh token) | Grant | **Không thể thực hiện với Duende nguyên bản**, xem bên dưới. Cần `MigrateRefreshTokens` *và* `SourceGrantKeysAreUnhashed`; nếu không sẽ bị bỏ qua kèm cảnh báo và người dùng phải đăng nhập lại. |

## Tùy chọn {#options}

| Tùy chọn | Mặc định | Mô tả |
|---|---|---|
| `Enabled` | `false` | Công tắc chính cho hosted runner |
| `DryRun` | `false` | Duyệt nguồn và tạo báo cáo kiểm tra đầy đủ (bộ ký tự/độ dài id, email trùng lặp, danh mục bảng/cột, số lượng theo từng lượt) mà không ghi gì |
| `Version` | `"1"` | Marker của lần chạy. Tăng giá trị để chạy lại một đợt quét phần chênh lệch. Chỉ marker `Completed`, không phải `DryRun`, mới chặn việc chạy lại |
| `UsersMode` | `CreateOnly` | `CreateOnly` bỏ qua người dùng đã tồn tại; `Upsert` ghi đè. **Không bao giờ dùng `Upsert` sau khi chuyển đổi**, nó sẽ ghi đè mật khẩu đã được băm lại và MFA mới |
| `MigrateClients` | `true` | Di chuyển các OAuth client. Client được nạp sẵn từ cấu hình luôn được ưu tiên và client đã tồn tại bị bỏ qua |
| `MigrateRefreshTokens` | `false` | Bao gồm các refresh token đang hoạt động. Cần `SourceGrantKeysAreUnhashed` |
| `SourceGrantKeysAreUnhashed` | `false` | Khẳng định rằng `PersistedGrants.Key` ở nguồn chứa handle nguyên văn. Chỉ đúng với một bản fork có grant store tùy chỉnh |
| `Source:ConnectionString` | *(không có)* | Kết nối tới SQL Server Duende nguồn |
| `MaxDegreeOfParallelism` | `32` | Mức đồng thời ghi có giới hạn cho các lượt có khối lượng lớn (người dùng, đăng nhập bên ngoài, MFA, refresh token). Hãy giảm nó với các tài khoản nhỏ hoặc dễ bị giới hạn tần suất; `1` là hoàn toàn tuần tự |
| `LeaseWaitMinutes` | `10` | Hosted runner: thôi chờ quyền leader của cluster sau khoảng thời gian này; một lần khởi động lại sau đó sẽ thử lại |
| `StartupDelaySeconds` | `30` | Hosted runner: độ trễ trước khi bắt đầu, để các dịch vụ nạp sẵn dữ liệu chạy xong và quá trình khởi động không bị chặn |

## Idempotency và đợt quét phần chênh lệch {#idempotency--delta-sweeps}

Mọi lượt chạy đều idempotent (bỏ qua nếu đã tồn tại, id MFA tất định), nên việc di chuyển có thể chạy lại một cách an toàn.
Hãy chạy nó trước thời điểm chuyển đổi vài ngày, rồi tăng `Version` cho một đợt quét phần chênh lệch cuối cùng sát thời điểm chuyển đổi để lấy
những người dùng đã đăng ký kể từ đó. Các bản ghi đã tồn tại bị bỏ qua (hoặc được cập nhật khi dùng `Upsert`), không bao giờ bị nhân đôi.

## Những gì KHÔNG được di chuyển {#what-is-not-migrated}

- **Refresh token đang hoạt động, với Duende nguyên bản.** `DefaultGrantStore` của Duende không bao giờ lưu
  handle của refresh token: `PersistedGrants.Key` chứa `base64(SHA-256(handle + ":" + grantType))`, và
  handle được xuất trình sẽ lại được băm khi tra cứu. Vì vậy không thể khôi phục handle từ cơ sở dữ liệu
  nguồn, và các dòng được di chuyển sẽ vĩnh viễn không đổi được, điều này còn tệ hơn là không di chuyển,
  vì báo cáo tính chúng là đã tạo và sự cố chỉ lộ ra ở lần refresh token đầu tiên sau khi chuyển đổi.
  Hãy lên kế hoạch chuyển đổi với một lần đăng nhập lại, hoặc chạy một lớp đệm đọc kép trong khoảng
  thời gian chuyển tiếp. `SourceGrantKeysAreUnhashed` chỉ tồn tại cho một bản fork có grant store
  lưu handle nguyên văn, và bản fork đó cũng tự chịu trách nhiệm chuyển `PersistedGrants.Data` từ
  dạng `RefreshToken` của Duende sang `RefreshTokenData`.
- **Token và nhóm SCIM**, **bản ghi cấp phát người dùng**: không có tương đương trong Duende; bắt đầu từ trống.
- **Khóa ký**: không được tự động hóa. Để giữ cho các token hiện có còn hiệu lực qua thời điểm chuyển đổi, hãy export khóa
  ký RSA từ Duende và import nó vào bảng `SigningKeys` sát thời điểm chuyển đổi.

## Chiến lược chuyển đổi {#cutover-strategy}

1. Triển khai ở trạng thái chưa kích hoạt (`Enabled=false`).
2. `Enabled=true, DryRun=true` → khởi động lại → xem lại báo cáo tại `/admin/migration/status`.
3. `DryRun=false` → khởi động lại → xác minh marker là `Completed` + kiểm tra ngẫu nhiên vài lần đăng nhập.
4. Tăng `Version` cho đợt quét phần chênh lệch cuối cùng, rồi trỏ lại các client/BFF sang Authagonal. **Hãy dự kiến một
   lần buộc đăng nhập lại**, xem ở trên.
5. Theo dõi; quay lui = trỏ lại về bản triển khai Duende chưa bị đụng tới.

## Import người dùng từ NDJSON {#ndjson-user-import}

Một nguồn import thứ hai, độc lập, trong cùng gói `Authagonal.Migration`: một tệp NDJSON phẳng
(mỗi dòng một đối tượng JSON) thay vì một kết nối cơ sở dữ liệu trực tiếp, và chỉ có người dùng, không có client, role,
scope hay cấu hình liên kết. Được xây dựng để di chuyển bảng người dùng riêng của một ứng dụng cũ (một store
ASP.NET Identity tự viết, một bảng Rails/Devise được export sang bcrypt, một ứng dụng Node dùng scrypt, ...) để mọi người
tiếp tục đăng nhập bằng mật khẩu cũ trong khi mật khẩu được băm lại một cách trong suốt sang PBKDF2 gốc ở lần
đăng nhập thành công kế tiếp, cùng cơ chế băm lại lười mà công cụ import Duende ở trên dựa vào.

### Lược đồ bản ghi {#record-schema}

Mỗi dòng một đối tượng JSON. `email` là trường bắt buộc duy nhất; mọi trường khác đều tùy chọn. **Các trường cấp
cao nhất không xác định sẽ làm dòng đó thất bại** (mặc định là nghiêm ngặt) trừ khi truyền `--AllowUnknownFields true`.

| Trường | Kiểu | Ghi chú |
|---|---|---|
| `email` | string | Bắt buộc. Phải là một địa chỉ email hợp lý. Khóa trùng lặp không phân biệt hoa thường. |
| `username` | string | Không có cột `AuthUser` riêng, được lưu trong `CustomAttributes["username"]`. |
| `givenName` | string | → `AuthUser.FirstName` |
| `familyName` | string | → `AuthUser.LastName` |
| `displayName` | string | Không có cột riêng, được lưu trong `CustomAttributes["displayName"]`. |
| `emailVerified` | bool | → `AuthUser.EmailConfirmed`. Mặc định là `false` khi vắng mặt. |
| `passwordHash` | string | → `AuthUser.PasswordHash`, được lưu **nguyên văn**. Mọi định dạng mà `PasswordHasher` nhận diện khi đăng nhập (bcrypt `$2a$`/`$2b$`/`$2x$`/`$2y$`, ASP.NET Identity V3, scrypt `$s2$`) đều xác minh được mà không cần thay đổi và từ đó được nâng cấp lên PBKDF2 gốc. Không được kiểm tra gì ngoài việc không rỗng; một hash sai định dạng đơn giản là không xác minh được khi đăng nhập, giống như khi không di chuyển. Bỏ qua với người dùng chỉ dùng SSO / không mật khẩu. |
| `roles` | string[] | → `AuthUser.Roles` |
| `organizationId` | string | → `AuthUser.OrganizationId` |
| `attributes` | object (string→string) | Được gộp vào `AuthUser.CustomAttributes` |
| `phoneNumber` | string | → `AuthUser.Phone` |
| `disabled` | bool | → `AuthUser.IsActive = !disabled`. Mặc định là đang hoạt động khi vắng mặt. |
| `createdAt` | string (ISO 8601) | → `AuthUser.CreatedAt`. Mặc định là thời điểm import khi vắng mặt. |
| `externalId` | string | → `AuthUser.ExternalId`, cũng là trường mà công cụ import Duende dùng để chứa id người dùng của cơ sở dữ liệu nguồn. |

Tệp ví dụ (5 dòng):

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

Cùng đích (Azure Table Storage) và cùng cổng chặn PII dạng văn bản thuần như CLI Duende ở trên: nguồn này ghi
các dòng `AuthUser` thẳng vào Table Storage mà không có `IFieldCipher`/`IIndexTokenizer` do host đăng ký, nên
nó từ chối chạy trừ khi `--AllowPlaintextPii true` xác nhận rằng đích không cấu hình thứ nào trong hai thứ đó (hoặc bạn
nối `NdjsonUserImportEngine` vào DI container của chính host, nơi các điểm mở rộng đó được phân giải).
Khác với CLI Duende, không có cổng `--AllowPlaintextSecrets`: nguồn này không bao giờ ghi seed TOTP của MFA
hay secret của OAuth client, chỉ ghi các trường hồ sơ người dùng và một hash mật khẩu được lưu nguyên văn.

### Tùy chọn {#options-1}

| Tùy chọn | Mặc định | Mô tả |
|---|---|---|
| `--Input` | *(bắt buộc)* | Đường dẫn tới tệp NDJSON |
| `--Target:ConnectionString` | *(bắt buộc)* | Chuỗi kết nối Azure Table Storage |
| `--DryRun` | `false` | Phân tích + kiểm tra mọi dòng, giải quyết trùng lặp với đích, và tạo báo cáo đầy đủ, không ghi gì |
| `--OnDuplicate` | `skip` | Cách xử lý một dòng có email (không phân biệt hoa thường) đã trùng với một người dùng hiện có: `skip` (giữ nguyên, idempotent), `update` (gộp các trường có trong dòng vào người dùng hiện có), hoặc `fail` (hủy ngay toàn bộ lần chạy) |
| `--BatchSize` | `500` | Số dòng giữa hai dòng log tiến độ. Không phải cơ chế gom lô khi ghi: `IUserStore` không có API hàng loạt, nên mỗi lần import/cập nhật vẫn là một lời gọi store |
| `--AllowUnknownFields` | `false` | Chấp nhận và bỏ qua các thuộc tính JSON cấp cao nhất nằm ngoài lược đồ ở trên, thay vì làm dòng đó thất bại |
| `--ContinueOnError` | `false` | Thoát với mã 0 ngay cả khi một hoặc nhiều dòng không phân tích/kiểm tra được. Không áp dụng cho `--OnDuplicate fail`, vốn luôn hủy lần chạy bất kể cờ này |

### Kết quả tóm tắt và mã thoát {#summary-output--exit-codes}

Báo cáo được in ra dưới dạng JSON: `TotalLines`, `Imported`, `Updated`, `Skipped`, `Failed`, và 20
`Failures` đầu tiên (`LineNumber` + `Reason`). Dòng trống không được tính ở đâu cả. Mã thoát:

- `0`: thành công (hoặc `--ContinueOnError true` với một hoặc nhiều dòng thất bại)
- `1`: một hoặc nhiều dòng không phân tích/kiểm tra được, và `--ContinueOnError` không được đặt
- `2`: lần chạy bị hủy: `--OnDuplicate fail` gặp một email đã tồn tại, hoặc thiếu một tùy chọn bắt buộc

### Idempotency {#idempotency}

Giá trị mặc định `--OnDuplicate skip` khiến việc chạy lại với một tệp không đổi không có tác dụng gì ở lần thứ hai:
mọi dòng có email đã tồn tại được tính là bị bỏ qua và không có gì được ghi. `update` cũng an toàn khi
chạy lại (nó luôn áp dụng lại cùng các trường); `fail` dành cho một lần import duy nhất không bao giờ được
âm thầm va chạm với các tài khoản hiện có.

### Những gì KHÔNG được import {#what-is-not-imported}

- **Role, scope, OAuth client, cấu hình liên kết.** Nguồn này chỉ có người dùng: xem công cụ import
  Duende ở trên nếu bạn cũng cần những thứ đó.
- **Thông tin xác thực MFA, đăng nhập bên ngoài.** Không thuộc lược đồ; hãy thêm chúng qua các flow thiết lập MFA
  / SSO tiêu chuẩn sau khi import.
