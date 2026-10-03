---
layout: default
title: Mở rộng quy mô
locale: vi
---

# Mở rộng quy mô

Authagonal được thiết kế để mở rộng cả theo chiều dọc lẫn chiều ngang mà không cần cấu hình đặc biệt.

## Không trạng thái theo thiết kế {#stateless-by-design}

Mọi trạng thái bền vững được lưu trong store nền (Azure Table Storage, DynamoDB với backend AWS, hoặc PostgreSQL với backend SQL tự host). Không có trạng thái nào trong tiến trình đòi hỏi sticky session hay sự phối hợp giữa các instance:

- **Khóa ký**: được tải từ Table Storage, làm mới mỗi giờ
- **Authorization code và refresh token**: được lưu trong Table Storage với cơ chế bảo đảm chỉ dùng một lần
- **Chống phát lại SAML**: ID của request được theo dõi trong Table Storage với thao tác xóa nguyên tử
- **State OIDC và PKCE verifier**: được lưu trong Table Storage
- **Cấu hình client và provider**: được tải theo từng request từ Table Storage

## Mã hóa cookie (data protection) {#cookie-encryption-data-protection}

Key ring Data Protection của ASP.NET Core bảo vệ cookie xác thực, nên mọi instance phải dùng chung một key ring. Nó được lưu bền vững tự động, theo thứ tự sau:

1. `DataProtection:BlobUri`, nếu được đặt (một blob tường minh, xác thực bằng `DefaultAzureCredential`).
2. Một container `dataprotection` trong tài khoản được chỉ định bởi `Storage:ConnectionString`, trừ khi đó là Azurite.
3. Trên đường managed identity (`Storage:TableServiceUri`), blob endpoint song song của cùng tài khoản, `https://{account}.blob.…/dataprotection/keys.xml`. Identity cần quyền Storage Blob Data Contributor trên tài khoản.

Chỉ một table endpoint không nhận diện được (Azurite, các trình giả lập kiểu path-style) mới rơi về file store theo từng máy, vốn tạm thời và riêng từng pod: khởi động lại sẽ đăng xuất mọi người và các replica không đọc được cookie của nhau. Bước kiểm tra lúc khởi động ghi log `Critical` khi điều đó xảy ra.

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

Với backend AWS, hãy truyền một S3 client + bucket vào `AddAuthagonalAwsStorage` để lưu key ring vào S3; nếu không, key ring nằm trong bộ nhớ và cookie hỏng khi khởi động lại cũng như giữa các node. Xem [Cài đặt → Backend AWS](installation#aws-backend). Với backend SQL, key ring được lưu bền vững bởi `AddAuthagonalPostgres` / `AddAuthagonalSqlite`.

Lưu bền vững không có nghĩa là mã hóa: key ring là XML văn bản thuần trừ khi `DataProtection:KeyVaultKeyId` hoặc `DataProtection:CertificateThumbprint` được đặt. Lúc khởi động, một key ring không mã hóa và chưa có khóa nào sẽ bị từ chối, còn một key ring đã có khóa sẽ khởi động kèm một log `Critical` (`DataProtection:AllowUnencryptedKeyRing=true` chấp nhận nó một cách có chủ ý). Xem [Cấu hình](configuration) để có bảng `DataProtection:*` đầy đủ.

## Cache theo từng instance {#per-instance-caches}

Một số ít giá trị được đọc nhiều và ít thay đổi được cache trong bộ nhớ theo từng instance để giảm số lượt truy cập Table Storage:

| Dữ liệu | Thời gian cache | Ảnh hưởng khi dữ liệu cũ |
|---|---|---|
| Tài liệu discovery OIDC | 60 phút (cấu hình được) | Chậm nhận biết việc IdP xoay vòng khóa |
| Metadata của IdP SAML | 60 phút (cấu hình được) | Tương tự |
| Các origin CORS được phép | 60 phút (cấu hình được) | Origin mới mất tới một giờ để lan truyền |

Các cache này chấp nhận được khi dùng trong production. Mọi thời gian đều cấu hình được qua mục cấu hình `Cache`, xem [Cấu hình](configuration). Nếu bạn cần lan truyền ngay lập tức, hãy khởi động lại các instance bị ảnh hưởng.

## Giới hạn tần suất {#rate-limiting}

Các endpoint dễ bị lạm dụng (đăng ký theo IP, đặt lại mật khẩu theo email đích, SCIM theo client, đăng ký client động theo IP, xem [Cấu hình → Giới hạn tần suất](configuration#rate-limiting)) được bảo vệ bởi một bộ giới hạn tần suất tích hợp.

Theo mặc định, giới hạn được áp đặt **trong tiến trình, theo từng node** đằng sau điểm mở rộng `IRateLimiter`, nên với N instance, mức trần thực tế là N lần giá trị cấu hình. Đó là chủ ý: bộ giới hạn là lớp chặn dự phòng chống lại việc lạm dụng mất kiểm soát trên một node đơn lẻ, còn giới hạn toàn cục có thẩm quyền thuộc về lớp biên (WAF / ingress / CDN), nơi thấy toàn bộ lưu lượng trước khi được cân bằng tải.

Sự đánh đổi đó đúng với các giới hạn về khối lượng nhưng sai trong một trường hợp: một hạn mức bảo vệ một **bí mật có thể đoán được**. `user_code` của device flow là một chuỗi ngắn từ một bảng chữ cái nhỏ, và giới hạn số lần thử là thứ duy nhất đứng giữa kẻ tấn công và một code cấp quyền vào một phiên đang hoạt động. Một mức trần nhân lên theo số replica là hình thức sai ở đó, và nó khiến giới hạn thực sự trở thành thuộc tính của cấu hình ingress thay vì của server.

Đặt **`Auth:DurableRateLimiting=true`** để chuyển các bộ đếm vào store mà bạn đang chạy sẵn, nhờ đó mọi replica dùng chung một hạn mức. Cái giá là một lượt truy cập store cho mỗi lần kiểm tra giới hạn tần suất; nó dùng cửa sổ cố định (hạn mức N cho phép tối đa 2N khi vắt qua ranh giới cửa sổ), và mở khi lỗi (fail open) nếu không truy cập được store, nên nó bổ sung cho quy tắc ở lớp biên chứ không thay thế. Các dòng bộ đếm được dọn tự động trên cả ba backend. Xem [Cấu hình → Giới hạn trên toàn cluster](configuration#cluster-wide-limits-authdurableratelimiting).

## Clustering {#clustering}

Nhiều instance phối hợp với nhau qua **bầu chọn leader** và một **event bus giữa các node**, cả hai đều nằm sau các backend có thể thay thế:

- **Bầu chọn leader**: bầu chọn dựa trên lease (`Cluster:LeaseTtlSeconds`, mặc định 30 giây, được gia hạn sau khoảng một nửa thời gian đó). Đúng một node giữ lease; quyền leader tự động chuyển giao khi leader ngừng hoạt động. Công việc giới hạn cho leader chỉ chạy trên leader: *vô hiệu hóa* khóa ký khi hết hạn (khi `Auth:KeyRotationEnabled` được bật), đợt quét đối soát grant (chỉ backend Azure), việc bổ sung dữ liệu khi lưu trữ (khi `Auth:AtRestBackfillEnabled` được bật; node không phải leader chờ quyền leader một lúc ngắn rồi bỏ qua), và đợt quét bộ đếm giới hạn tần suất (backend Azure với `Auth:DurableRateLimiting`). Với `Cluster:Enabled=false`, node duy nhất là leader vĩnh viễn, nên một bản triển khai độc lập vẫn chạy tất cả các việc đó.
- **Event bus**: thông báo giữa các node (ví dụ vô hiệu hóa cache trong các host multi-tenant), được thăm dò mỗi `Cluster:PollIntervalSeconds` (mặc định 3 giây).

Mỗi instance tạo một node ID ngẫu nhiên gồm 12 ký tự hex lúc khởi động để tự định danh; ID này không được lưu bền vững.

### Backend {#backends}

**Mặc định là trong tiến trình**: một node đơn lẻ luôn là leader của chính nó, và các sự kiện chỉ cục bộ, đúng cho một instance mà không cần cấu hình gì. Các bản triển khai nhiều node thay vào một backend thực qua callback `configureClustering` trên `AddAuthagonal`:

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

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` chỉ đăng ký event bus, giữ lại lease trong tiến trình (luôn là leader); hãy dùng chúng trên các node phải nhận sự kiện của cluster nhưng không bao giờ được tranh quyền leader.

> **Lưu ý:** với mặc định trong tiến trình trên nhiều node, *mọi* node đều tin rằng mình là leader. Điều đó vô hại với hầu hết khối lượng công việc, nhưng hãy bật một backend lease thực trước khi bật `Auth:KeyRotationEnabled` trên nhiều instance.

Việc **tạo** khóa ký tách biệt với việc vô hiệu hóa giới hạn cho leader nói trên, và không do nó điều khiển: mọi node gọi `EnsureActiveKeyAsync` lúc khởi động và ở mỗi lần làm mới theo `Auth:SigningKeyCacheRefreshMinutes`, nên khi `KeyRotationEnabled` tắt, tức mặc định, việc chuyển khóa ở mốc hết hạn 90 ngày hoàn toàn do đường đó điều khiển. Việc tạo khóa dùng một lease cluster ngắn riêng, nên nó chỉ có một bên ghi ở bất cứ đâu có cấu hình backend lease thực. Với mặc định trong tiến trình trên nhiều node thì không có sự phối hợp như vậy, và hai node cùng gặp một khóa hết hạn vào đúng một thời điểm có thể mỗi node tạo một khóa; cả hai đều nằm trong JWKS và token được ký bởi khóa nào cũng xác minh được, nhưng khóa nào được báo là đang hoạt động có thể dao động qua lại. Đây là thêm một lý do để cấu hình backend lease thực cho các bản triển khai nhiều node.

Xem trang [Cấu hình](configuration#cluster) để biết mọi thiết lập cluster.

### Triển khai multi-tenant {#multi-tenant-deployments}

Ở chế độ multi-tenant (`AddAuthagonalCore()`), `TokenCleanupService`, `GrantReconciliationService`, `SigningKeyRotationService` và các dịch vụ nạp sẵn cấu hình (client, provider, scope, role) không được đăng ký: chúng thuộc về tổ hợp single-tenant `AddAuthagonal()`, và host tự quản lý công việc đó theo từng tenant.

## Partition nóng của chỉ mục tên {#name-index-hot-partition}

Tìm kiếm theo tiền tố tên dành cho quản trị viên dựa trên các bảng chỉ mục `UserFirstNames` / `UserLastNames`, vốn dùng **một partition nóng duy nhất**. Ở quy mô lớn, điều này giới hạn thông lượng ghi chỉ mục ở khoảng 2.000 thao tác/giây, có thể trở thành nút thắt khi tạo/cập nhật người dùng dưới tải nặng. Nếu bạn không cung cấp tính năng tìm kiếm tên cho quản trị viên, hãy đặt `Storage:NameIndexesEnabled = false` để bỏ hẳn các thao tác ghi này. Xem [Cấu hình](configuration).

## Proxy tin cậy và endpoint nội bộ {#trusted-proxy-and-internal-endpoints}

Khi chạy nhiều instance phía sau một load balancer:

- **Forwarded header**: giới hạn tần suất và khóa tài khoản dựa trên IP của client, được phân giải từ `X-Forwarded-For`. Đặt `ForwardedHeaders:KnownNetworks` thành CIDR của ingress / pod để IP của client không thể bị giả mạo giữa các instance. `ForwardedHeaders:ForwardLimit` mặc định là `1`. Xem [Cấu hình](configuration#forwarded-headers-trusted-proxy).
- **Endpoint nội bộ**: `/_internal/backchannel-logout` yêu cầu `Cluster:Secret` trong header `X-Cluster-Secret` (so sánh trong thời gian hằng định). Không có nó, endpoint không cấp quyền cho ai và trả 404; IP nguồn không được coi là thông tin xác thực, vì loopback là địa chỉ mà một reverse proxy cùng host trình ra cho mọi request được chuyển tiếp, còn một dải riêng là mọi workload lân cận trong một mạng cluster dùng chung. `Cluster:AllowLoopbackWithoutSecret` là tùy chọn bật chỉ dành cho môi trường phát triển, cho phép lại một peer loopback chưa qua chuyển tiếp. Sản phẩm phát hành không bao giờ gọi route này (việc lan truyền phiên diễn ra trong tiến trình qua `SessionTermination`), nên nó chỉ có ý nghĩa với cơ chế lan truyền do bạn tự xây dựng.

## Khuyến nghị về mở rộng quy mô {#scaling-recommendations}

**Mở rộng theo chiều dọc**: tăng CPU và bộ nhớ cho một instance đơn lẻ. Hữu ích để xử lý nhiều request đồng thời hơn trên mỗi instance.

**Mở rộng theo chiều ngang**: chạy nhiều instance phía sau một load balancer. Không cần sticky session hay cache dùng chung. Mỗi instance hoàn toàn độc lập.

**Thu nhỏ về không**: Authagonal hỗ trợ các bản triển khai thu nhỏ về không (ví dụ Azure Container Apps với `minReplicas: 0`). Request đầu tiên sau thời gian nhàn rỗi sẽ có một lần khởi động nguội vài giây trong khi runtime .NET khởi tạo và khóa ký được tải từ storage.
