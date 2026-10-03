---
layout: default
title: Cài đặt
locale: vi
---

# Cài đặt

## Docker (khuyến nghị) {#docker-recommended}

Kéo về và chạy image dựng sẵn:

```bash
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  drawboardci/authagonal
```

## Docker Compose {#docker-compose}

Để phát triển cục bộ với Azurite (trình giả lập Azure Storage):

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

> ⚠️ **`Auth:AllowInsecureHttp` là thiết lập dành cho môi trường phát triển.** RFC 6749 §3.1/§3.2 yêu cầu TLS tại authorization endpoint và token endpoint, nên Authagonal từ chối các request không phải https tới `/connect/*` trừ khi thiết lập này được bật. Scheme được đọc sau khi xử lý forwarded header, nên một proxy kết thúc TLS và chuyển tiếp `X-Forwarded-Proto: https` sẽ đáp ứng yêu cầu này mà vẫn để thiết lập ở trạng thái tắt, và đó là điều mọi bản triển khai mà bất kỳ ai ngoài bạn có thể truy cập đều nên làm. Khi bật thiết lập này, kẻ quan sát trên đường truyền đọc được authorization code, client secret trong header `Authorization: Basic`, cùng access token và refresh token. Xem [Cấu hình](configuration#authentication).

## Build từ mã nguồn {#building-from-source}

### Điều kiện tiên quyết {#prerequisites}

- .NET 10 SDK
- Node.js 24+

Authagonal nhắm tới `net9.0` và `net10.0`, và khi chạy cần một shared framework **đã vá lỗi**: **tối thiểu 9.0.18 hoặc 10.0.10**. Xem [danh sách kiểm tra bảo mật cho production](#production-security-checklist) để biết lý do, và `Auth:RequireMinimumRuntime` để biến bước kiểm tra lúc khởi động thành từ chối khởi động.

### Build {#build}

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

### Docker Build {#docker-build}

```bash
# Server image (multi-stage: builds SPA + .NET in one image)
docker build -t authagonal .

# Migration tool
docker build -f Dockerfile.migration -t authagonal-migration .
```

## Dùng như một thư viện (NuGet) {#as-a-library-nuget}

Tham chiếu các gói Authagonal trong dự án ASP.NET Core của riêng bạn:

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.AzureProvider" Version="x.y.z" />
```

Gói storage provider có thể thay thế: `Authagonal.AzureProvider` cho Azure Table Storage (cách kết nối mặc định của `AddAuthagonal()`), `Authagonal.SqlProvider` cho PostgreSQL hoặc SQLite tự vận hành (xem [Backend SQL](#sql-backend)), hoặc `Authagonal.AwsProvider` cho DynamoDB / S3 / Secrets Manager (xem [Backend AWS](#aws-backend)).

> **Thứ tự đăng ký là quan trọng.** Storage provider phải được đăng ký **trước** `AddAuthagonal()`. Chính đăng ký `IUserStore` đã có sẵn đó khiến `AddAuthagonal()` bỏ qua phần kết nối Azure Table Storage dựng sẵn của nó; một provider được đăng ký sau sẽ mất mọi interface mà `AddAuthagonal()` đã điền trước, một cách âm thầm, vì các đăng ký đó dùng `TryAdd`.
>
> Ba interface (`IOrganizationStore`, `IOrganizationMembershipStore` và `IScimGroupRoleMappingStore`) có bản dự phòng trong bộ nhớ, rỗng và chỉ đọc, để DI vẫn phân giải được trên một host hoàn toàn không kết nối store nào cho chúng. `AddAuthagonal()` tạm gỡ các bản dự phòng đó ra trước khi storage provider đăng ký và khôi phục chúng bằng `TryAdd` sau đó, nên store bền vững của provider luôn được ưu tiên, còn bản dự phòng vẫn che cho host không có store. Nếu bạn đăng ký hiện thực riêng cho bất kỳ interface nào trong ba interface này, hãy đăng ký trước `AddAuthagonal()` như mọi store khác.

Sau đó ghép nó vào `Program.cs`:

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

Xem [Khả năng mở rộng](extensibility) để biết mọi điểm ghi đè và [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server) để xem một ví dụ hoàn chỉnh.

### Email {#email}

Bên gửi [Resend](https://resend.com) dựng sẵn tự động kích hoạt khi `Email:ResendApiKey` và `Email:SenderEmail` được cấu hình, không cần đăng ký dịch vụ nào. Khi không có `IEmailService` nào, email xác minh và email đặt lại mật khẩu bị **loại bỏ một cách âm thầm**, và vì theo mặc định đăng nhập đòi hỏi email đã được xác nhận, người dùng tự đăng ký sẽ không bao giờ đăng nhập được (`UseAuthagonal` ghi một cảnh báo lúc khởi động). Hãy đặt các khóa `Email:*`, hoặc đăng ký `IEmailService` của riêng bạn trước `AddAuthagonal()`, hoặc liệt kê các tên miền của bạn trong `Auth:AutoConfirmEmailDomains` để bỏ qua bước xác minh (chỉ cho dev/test). Xem [Cấu hình → Email](configuration#email).

## Backend SQL {#sql-backend}

Để chạy trên cơ sở dữ liệu của riêng bạn thay vì một dịch vụ cloud, hãy tham chiếu `Authagonal.SqlProvider` và đăng ký nó **trước** `AddAuthagonal()`, chính các đăng ký đó khiến `AddAuthagonal()` bỏ qua phần kết nối Azure Table Storage:

```csharp
using Authagonal.SqlProvider;

// PostgreSQL: the production self-hosted backend
builder.Services.AddAuthagonalPostgres(
    "Host=db;Database=authagonal;Username=auth;Password=…;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/db-ca.pem");

// or SQLite: one file, no server. Suits embedded hosts, CI and small single-node deployments
builder.Services.AddAuthagonalSqlite("Data Source=authagonal.db");

builder.Services.AddAuthagonal(builder.Configuration);
```

Các bảng có cấu trúc tương ứng một-một với bố cục của Azure và DynamoDB, và được tạo lúc khởi động nếu chưa tồn tại (mọi câu lệnh đều là `IF NOT EXISTS`, nên nhiều pod chạy đồng thời vẫn an toàn và không có tác dụng gì với một schema bạn đã tự cấp phát). Không cần cấu hình `Storage:*`. Vòng khóa DataProtection được lưu bền vững vào cùng cơ sở dữ liệu đó, nên cookie và antiforgery token vẫn còn hiệu lực sau khi khởi động lại và hoạt động trên nhiều pod mà không cần thêm dịch vụ nào.

SQLite tuần tự hóa các thao tác ghi, nên nó là backend một node: lease trong tiến trình và cluster event bus được đăng ký mặc định là cặp đi kèm đúng trong trường hợp này. Một bản triển khai PostgreSQL nhiều pod cần `clustering.UseSql(dataSource)` để bầu leader.

> **Collation.** Trên PostgreSQL, các cột khóa được cố định ở `COLLATE "C"`. Toàn bộ sơ đồ khóa đều theo thứ tự byte (giới hạn tiền tố, khoảng phân vùng theo môi trường, lượt quét dọn grant hết hạn, phân trang keyset), và một cơ sở dữ liệu được tạo với collation theo ngôn ngữ (`en_US.UTF-8` và các locale ICU là mặc định phổ biến) sẽ sắp xếp dấu câu và chữ hoa/thường theo cách khác rồi âm thầm trả về sai hàng. Việc cố định này khiến bố cục độc lập với cách cơ sở dữ liệu được tạo; bạn không cần tạo nó theo cách cụ thể nào.

> ⚠️ **Vật liệu khóa nằm trong cơ sở dữ liệu đó.** Trên Azure, khóa ký token nằm trong Table Storage và vòng khóa DataProtection nằm trong một Blob container, mỗi thứ có RBAC cấp quyền độc lập; trên AWS là DynamoDB và S3. Trên SQL, cả hai đều là bảng nằm sau cùng một connection string với mọi thứ khác, nên hãy coi connection string tương đương với khóa ký: nếu không, một `pg_dump`, một read replica, một role phân tích có quyền `SELECT`, hay một bản sao lưu được khôi phục đều mang lại cả khả năng tạo token cho bất kỳ subject nào lẫn các khóa đứng sau mọi cookie xác thực. Hãy đăng ký một `IFieldCipher` trước `AddAuthagonalPostgres()` để mã hóa `SigningKeys.keyMaterialJson` khi lưu trữ, và đặt `DataProtection:KeyVaultKeyId` hoặc `DataProtection:CertificateThumbprint` để vòng khóa không bị lưu kèm một `<masterKey>` trần: bản triển khai mới lưu bền vững vòng khóa mà không có một trong hai sẽ bị từ chối lúc khởi động, còn bản triển khai hiện có sẽ bị cảnh báo ở mức `Critical` mỗi lần khởi động. Xem [README của gói](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider#dataprotection-keys) để biết cả hai cách, và cách trỏ vòng khóa sang một schema riêng với role riêng.

Xem [README của gói](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider) để biết bố cục bảng, các cơ chế đồng thời đứng sau từng bảo đảm dùng một lần, và cách thêm dialect cho một engine khác.

## Backend AWS {#aws-backend}

Để chạy trên AWS thay vì Azure, hãy tham chiếu `Authagonal.AwsProvider` và đăng ký gói AWS **trước** `AddAuthagonal()`, chính các đăng ký đó khiến `AddAuthagonal()` bỏ qua phần kết nối Azure Table Storage:

```csharp
using Authagonal.AwsProvider;

builder.Services.AddAuthagonalAwsStorage(
    dynamoDb,                // IAmazonDynamoDB: required
    secretsManager,          // IAmazonSecretsManager: optional; replaces the plaintext ISecretProvider
    s3,                      // IAmazonS3: optional; used for DataProtection keys
    "my-auth-keys-bucket");  // S3 bucket for the DataProtection key ring
builder.Services.AddAuthagonal(builder.Configuration);
```

Các bảng DynamoDB có cấu trúc tương ứng một-một với bố cục Azure và được bảo đảm tồn tại lúc khởi động (idempotent, không có tác dụng gì khi chúng đã được Terraform cấp phát). Thông tin xác thực được phân giải qua chuỗi AWS tiêu chuẩn (env / EC2 instance role / IRSA), nên không có sự phân tách giữa connection string và managed identity, không cần cấu hình `Storage:*`.

> ⚠️ **Khóa DataProtection trên S3.** Nếu không có S3 client + bucket, vòng khóa ASP.NET Core Data Protection được giữ trong bộ nhớ, ổn cho một node duy nhất ở môi trường dev, nhưng trong production cookie và antiforgery token sẽ hỏng khi khởi động lại và giữa các node. Luôn truyền S3 client và bucket cho một bản triển khai AWS production.

## SPA đăng nhập (npm) {#login-spa-npm}

Giao diện đăng nhập được phát hành dưới dạng một gói npm để tùy biến:

```bash
npm install @authagonal/login react react-dom react-router
```

Gói này đi kèm JS và CSS đã biên dịch, hãy import trực tiếp các component và style vào ứng dụng React của riêng bạn. Xem [Máy chủ tùy chỉnh](custom-server) để có hướng dẫn đầy đủ.

`react`, `react-dom` và `react-router` là các dependency dạng **peer**: bản build đưa chúng ra ngoài (externalize), nên các component dùng bản sao của ứng dụng bạn thay vì bản sao riêng. Nhờ đó các trang được export có thể gọi `useNavigate` bên trong `<BrowserRouter>` của bạn và chạy hook trên chính instance React đang render chúng. Hãy cài chúng cùng với gói, đừng để gói tự mang theo bản riêng.

## Backend-for-Frontend (BFF) {#backend-for-frontend-bff}

Nếu SPA của bạn gọi API bằng bearer token, hãy giữ token trong một BFF thay vì trong trình duyệt. BFF được phát hành dưới dạng gói NuGet (`Authagonal.Bff`) và gói npm (`@authagonal/bff`); không gói nào thuộc server image. Xem [Backend-for-Frontend](bff).

## Danh sách kiểm tra bảo mật cho production {#production-security-checklist}

Trước khi để Authagonal tiếp nhận lưu lượng thật, hãy xác nhận các mục sau. Mỗi mục được trình bày chi tiết trên trang [Cấu hình](configuration).

- **Chạy trên .NET runtime đã vá lỗi: tối thiểu 9.0.18 hoặc 10.0.10.** Các bản sửa cho GHSA-37gx-xxp4-5rgx và GHSA-w3x6-4m5h-cxqf (một lỗi vòng lặp vô hạn và một cặp lỗi XXE / cạn kiệt tài nguyên trong `System.Security.Cryptography.Xml`, đều có thể bị kích hoạt từ SAML ACS endpoint **ẩn danh**) nằm trong shared framework, không nằm trong gói nào mà Authagonal có thể tham chiếu, nên không gì trong cây dependency của bạn có thể bảo đảm chúng. Authagonal ghi log mức `Critical` lúc khởi động khi runtime đang chạy thấp hơn mức sàn; đặt `Auth:RequireMinimumRuntime = true` để nó từ chối khởi động. Các container image được phát hành sẵn đều đã dùng runtime bằng hoặc cao hơn mức sàn.
- **Chạy sau một proxy kết thúc TLS, và khai báo proxy đó.** Authagonal phải đứng sau một reverse proxy / ingress kết thúc TLS (hoặc tự kết thúc TLS). HSTS chỉ được phát trên HTTPS và `/connect/*` từ chối plaintext, nên proxy phải chuyển tiếp `X-Forwarded-Proto: https`, và header đó bị bỏ qua trừ khi bạn đặt `ForwardedHeaders:KnownNetworks` (hoặc `KnownProxies`) thành CIDR / địa chỉ của proxy. Dùng `["0.0.0.0/0", "::/0"]` nếu proxy không có địa chỉ cố định và không có gì khác truy cập được tiến trình. `ForwardedHeaders:ForwardLimit` mặc định là `1` (chỉ tin chặng cuối).
- **Đặt `SecretProvider:VaultUri`.** Secret provider mặc định là **plaintext**: không có Key Vault, client secret OIDC phía upstream và seed TOTP / MFA được lưu dưới dạng văn bản rõ trong Table Storage (và trong các bản sao lưu). Hãy cấu hình Key Vault cho mọi bản triển khai production.
- **Khóa chặt admin API.** `AdminApi:Enabled` mặc định là **true**. Scope quản trị (`AdminApi:Scope`, mặc định `authagonal-admin`) cấp toàn quyền quản lý và quyền mạo danh người dùng. Hãy giới hạn ở tầng mạng các route quản trị `/api/v1/*` và kiểm soát chặt chẽ ai được cấp scope quản trị, hoặc đặt `AdminApi:Enabled = false` nếu không dùng.
- **Bảo vệ các endpoint nội bộ.** Đặt `Cluster:Secret` để endpoint nội bộ `/_internal/backchannel-logout` yêu cầu header `X-Cluster-Secret` (so sánh trong thời gian hằng). Khi không có secret, endpoint này **không** cấp quyền cho ai và trả lời 404: địa chỉ nguồn không phải là thông tin xác thực, và loopback chính là thứ mà một reverse proxy trên cùng host trình ra cho mọi request nó chuyển tiếp. `Cluster:AllowLoopbackWithoutSecret` cho phép lại một peer loopback trước khi chuyển tiếp, chỉ dành cho phát triển cục bộ. Không có gì trong sản phẩm được phát hành gọi tới endpoint này, nên việc đóng khi lỗi không làm hỏng luồng nào của chính Authagonal; vì vậy hãy đặt secret nếu bạn tự xây dựng cơ chế phát tán giữa các pod dựa trên nó.
- **Mã hóa bản sao lưu.** Với secret provider plaintext, bản sao lưu chứa secret. Bảng `SigningKeys` mặc định bị loại khỏi bản sao lưu; nếu bạn chọn đưa vào qua `Backup:IncludeSigningKeys`, đích sao lưu phải được mã hóa khi lưu trữ. Xem [Sao lưu & khôi phục](backup-restore).

## Công cụ di chuyển dữ liệu {#migration-tool}

Để di chuyển từ Duende IdentityServer + SQL Server:

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

Xem [Di chuyển dữ liệu](migration) để biết chi tiết.
