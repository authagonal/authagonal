---
layout: default
title: Cấu hình
locale: vi
---

# Cấu hình

Authagonal được cấu hình qua `appsettings.json` hoặc biến môi trường. Biến môi trường dùng `__` làm dấu phân cách section (ví dụ `Storage__ConnectionString`).

## Thiết lập bắt buộc {#required-settings}

Lưu trữ có thể được cấu hình theo một trong hai cách, cung cấp **hoặc** `Storage:ConnectionString` **hoặc** `Storage:TableServiceUri` (đường managed identity, được ưu tiên trong production).

| Thiết lập | Biến môi trường | Mô tả |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Connection string của Azure Table Storage kèm account key. Phù hợp cho dev / Azurite. |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | Endpoint Table Storage dùng managed identity, ví dụ `https://{account}.table.core.windows.net/`. Là lựa chọn thay thế cho `Storage:ConnectionString` và **được ưu tiên trong production**: xác thực qua `DefaultAzureCredential` nên không có access key nào phải nằm trong một secret. Host phải cấp cho workload identity vai trò **Storage Table Data Contributor**. |
| `Issuer` | `Issuer` | URL gốc công khai của máy chủ này (ví dụ `https://auth.example.com`) |

## Lưu trữ {#storage}

| Thiết lập | Biến môi trường | Mặc định | Mô tả |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *(không có)* | Connection string kèm account key (xem Thiết lập bắt buộc). |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *(không có)* | URI Table Storage dùng managed identity (xem Thiết lập bắt buộc). Được ưu tiên hơn `Storage:ConnectionString` khi cả hai đều được đặt. |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | Có duy trì các bảng chỉ mục tìm kiếm tiền tố `UserFirstNames` / `UserLastNames` phục vụ tìm kiếm theo tiền tố tên của admin hay không. Đặt `false` trên các host không cung cấp tìm kiếm tên cho admin để bỏ qua các lần ghi đó. **Lưu ý về mở rộng quy mô:** các chỉ mục này dùng một partition nóng duy nhất và giới hạn thông lượng ở khoảng 2.000 thao tác/giây khi ở quy mô lớn, hãy tắt chúng nếu bạn không cần tìm kiếm theo tên. |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | URL gốc mà endpoint `/connect/authorize` chuyển hướng tới cho SPA đăng nhập (các màn hình đăng nhập, xác thực nâng cấp và chấp thuận). Đặt giá trị này khi giao diện đăng nhập được phục vụ từ một origin khác với máy chủ; mặc định là đường dẫn tương đối `/login` do SPA đi kèm phục vụ. |

## Xác thực {#authentication}

| Thiết lập | Mặc định | Mô tả |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Thời gian sống của phiên cookie (trượt) |
| `Authentication:AllowInsecureCookie` | `false` | Cho phép cookie phiên được gửi qua http không mã hóa (`SameAsRequest` thay vì `Always`). **Chỉ dành cho phát triển.** Cookie CHÍNH LÀ phiên, và `SameAsRequest` chỉ trông tương đương khi đứng sau một proxy kết thúc TLS: nó phụ thuộc vào việc `X-Forwarded-Proto` tới nơi và được tin cậy, nên một ingress cấu hình sai, một health probe dùng HTTP không mã hóa, hoặc một proxy làm rơi header sẽ sinh ra một cookie không có Secure, và cookie đó sau đó đi kèm mọi request không mã hóa tới cùng host. Lỗi này diễn ra âm thầm. |
| `Authentication:CookieDomain` | *(không đặt)* | Giới hạn cookie phiên theo một tên miền cha, để nó được gửi tới các subdomain anh em (`app.example.com` cũng như `auth.example.com`). **Cái giá là mất ràng buộc origin:** cookie không còn mang được tiền tố `__Host-`, chính tiền tố này khiến trình duyệt từ chối cookie trừ khi nó là Secure, `Path=/` và không có `Domain`, nên mọi subdomain có thể đặt cookie trên tên miền cha, và mọi thứ có thể chiếm quyền một subdomain như vậy, đều nằm trong phạm vi ảnh hưởng. Hãy để trống trừ khi một origin anh em thực sự cần phiên. |
| `Auth:AllowInsecureHttp` | `false` | Cho phép các endpoint OAuth (`/connect/*`) trả lời request http không mã hóa. **Chỉ dành cho phát triển.** RFC 6749 §3.1/§3.2 yêu cầu TLS tại authorization endpoint và token endpoint, nên mặc định một request không phải https tới bất kỳ endpoint nào trong số đó đều bị từ chối với `invalid_request`. Scheme được đánh giá *sau* khi xử lý forwarded header, nên một proxy kết thúc TLS và chuyển tiếp `X-Forwarded-Proto: https` sẽ vượt qua cổng chặn này khi tùy chọn để tắt, với điều kiện proxy đó được khai báo trong [`ForwardedHeaders:KnownNetworks` / `KnownProxies`](#the-two-headers-are-not-trusted-on-the-same-terms), nếu không header sẽ bị bỏ qua. Chỉ một bản triển khai thực sự không mã hóa (`docker-compose.yml` đi kèm, bản demo custom-server) mới cần nó, và máy chủ ghi một cảnh báo lúc khởi động mỗi khi nó được bật. Được chuyển vào `AuthagonalProtocolOptions.AllowInsecureHttp`, nên nó cũng điều khiển các endpoint thuộc `Authagonal.Protocol` (xem [Khả năng mở rộng](extensibility#embedding-authagonalprotocol-alone)). |
| `Auth:RequireMinimumRuntime` | `false` | Từ chối khởi động khi shared framework .NET cũ hơn mức sàn bảo mật mà Authagonal yêu cầu (**9.0.18 / 10.0.10**). Mức sàn này tồn tại vì các bản sửa cho GHSA-37gx-xxp4-5rgx và GHSA-w3x6-4m5h-cxqf (một vòng lặp vô hạn và một cặp lỗi XXE / cạn kiệt tài nguyên trong `System.Security.Cryptography.Xml`, đều có thể chạm tới từ endpoint SAML ACS **không cần xác thực**) được phát hành trong runtime, không phải trong một gói mà thư viện này có thể ghim phiên bản, nên không dependency nào của bạn có thể bảo đảm chúng. Để `false`, một runtime cũ sẽ là một log mức `Critical` và máy chủ vẫn khởi động: từ chối theo mặc định sẽ biến một lần nâng phiên bản Authagonal thành sự cố ngừng hoạt động trên một hệ thống có runtime chậm một bản vá. Đặt `true` ở nơi mà việc không khởi động được ưu tiên hơn việc phục vụ XML chưa xác thực trên một runtime chưa vá. |
| `Auth:MaxFailedAttempts` | `5` | Số lần đăng nhập thất bại trước khi tài khoản bị khóa |
| `Auth:LockoutDurationMinutes` | `10` | Thời gian khóa tài khoản sau khi đạt số lần thất bại tối đa |
| `Auth:MaxLoginAttemptsPerIp` | `30` | Số lần thử mật khẩu được phép cho mỗi địa chỉ nguồn trong mỗi `Auth:LoginWindowMinutes`, và (tính riêng) cho mỗi email được gửi lên trong cùng cửa sổ. Khóa theo tài khoản không thể chặn được tấn công rải (mỗi tài khoản một lần thử trên hàng nghìn tài khoản), và mọi lần thử chưa xác thực đều phải trả chi phí một lần PBKDF2 đầy đủ, nên thiết lập này giới hạn cả hai. Vượt quá sẽ trả về `429 too_many_attempts` (`AuthEndpoints.cs:107-119`). |
| `Auth:LoginWindowMinutes` | `5` | Cửa sổ thời gian cho `Auth:MaxLoginAttemptsPerIp` |
| `Auth:MaxRegistrationsPerIp` | `5` | Số lần đăng ký tối đa cho mỗi địa chỉ IP trong cửa sổ thời gian |
| `Auth:RegistrationWindowMinutes` | `60` | Cửa sổ giới hạn tần suất đăng ký |
| `Auth:MaxPasswordResetsPerEmail` | `3` | Số email đặt lại mật khẩu tối đa cho mỗi địa chỉ đích trong cửa sổ thời gian (tính theo email, không theo IP của bên gọi, để một địa chỉ không thể bị dội bom email) |
| `Auth:MaxPasswordResetsPerIp` | `15` | Số request quên mật khẩu tối đa cho mỗi IP nguồn trong cửa sổ thời gian. Giới hạn theo email chặn lượng thư gửi tới một nạn nhân; giới hạn này chặn một bên gọi đang lần lượt đi qua một danh sách địa chỉ, nếu không đó sẽ là thư ẩn danh không giới hạn từ tên miền gửi thư đã xác minh của bạn cộng thêm một lần đọc store cho mỗi địa chỉ. |
| `Auth:PasswordResetWindowMinutes` | `60` | Cửa sổ giới hạn tần suất đặt lại mật khẩu |
| `Auth:DurableRateLimiting` | `false` | Giữ bộ đếm giới hạn tần suất trong store đã cấu hình để mọi replica dùng chung một ngân sách, thay vì mỗi node giữ bộ đếm riêng. Tốn một lượt đi về store cho mỗi lần kiểm tra; một bản triển khai một node không được lợi gì. Yêu cầu một provider cung cấp `IRateLimitCounterStore` (Azure, SQL, AWS). Nếu không có, host từ chối khởi động thay vì âm thầm quay về giới hạn theo từng node. Xem [Giới hạn trên toàn cụm](#cluster-wide-limits-authdurableratelimiting). |
| `Auth:AutoConfirmEmailDomains` | *(rỗng)* | Các tên miền email (mảng chuỗi) mà các lần tự đăng ký được tự động xác nhận, bỏ qua email xác minh. Rỗng (mặc định) nghĩa là mọi lần đăng ký đều phải xác minh. Chỉ dành cho dev/test; không bao giờ liệt kê một tên miền có thể nhận thư thật. |
| `Auth:AllowPasswordlessAccountClaim` | `false` | Đăng ký một email thuộc về một tài khoản hiện có **không có thông tin xác thực cục bộ** (liên kết hoặc được cấp phát JIT) sẽ đặt sẵn một mật khẩu trên tài khoản đó thay vì trả về phản hồi trùng lặp trung lập trước việc dò tìm tài khoản. Thông tin xác thực đặt sẵn và mọi thuộc tính vẫn ở trạng thái chưa có hiệu lực cho tới khi người nhận quyền bấm vào một email xác minh mới, nên biết email của một tài khoản liên kết là không đủ để chiếm tài khoản đó. Một tài khoản đã có mật khẩu không bao giờ bị ảnh hưởng. Xem [Nâng cấp người dùng](user-upgrade). |
| `Auth:ClaimAllowedAttributeKeys` | *(rỗng)* | Các khóa thuộc tính tùy chỉnh mà một lần nhận lại tài khoản không mật khẩu có thể mang từ request đăng ký sang tài khoản được nhận lại. Rỗng cho phép mọi khóa (tương thích ngược); liệt kê khóa để giới hạn những gì một lần nhận quyền có thể chèn vào việc cấp phát phía sau và vào token. |
| `Auth:EmailVerificationExpiryHours` | `24` | Thời gian sống của liên kết xác minh email |
| `Auth:PasswordResetExpiryMinutes` | `60` | Thời gian sống của liên kết đặt lại mật khẩu |
| `Auth:MfaChallengeExpiryMinutes` | `5` | Thời gian sống của token thử thách MFA |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | Thời gian sống của setup token MFA (cho việc bắt buộc đăng ký) |
| `Auth:WebAuthnAllowedHosts` | *(rỗng)* | Các host được phép đóng vai trò relying party WebAuthn. Rỗng chấp nhận mọi host (các bản triển khai hiện có tiếp tục hoạt động) và là một lỗ hổng: khi đó RP ID và origin mong đợi được suy ra từ chính request đang được kiểm tra. Trên một bản triển khai đa tenant, hãy liệt kê mọi host của tenant. Xem [MFA](mfa). |
| `Auth:Pbkdf2Iterations` | `100000` | Số vòng lặp PBKDF2 để hash mật khẩu |
| `Auth:FailedLoginMinimumMilliseconds` | `250` | Mức sàn thời gian thực mà một lần đăng nhập thất bại bị giữ lại trước khi `invalid_credentials` được trả về, tính từ lúc bắt đầu request. Đóng kênh dò thời gian dùng để dò tìm người dùng: một tài khoản không tồn tại được kiểm tra với một hash giả ở định dạng PBKDF2 gốc, nhưng một tài khoản thật vẫn có thể giữ một hash bcrypt, Scrypt.NET hoặc ASP.NET Identity V3 được nhập vào với chi phí khác, nên không thể bảo đảm khối lượng công việc bằng nhau, và thứ được áp đặt là thời gian trôi qua bằng nhau. Hãy nâng giá trị này lên cao hơn hash chậm nhất mà bản triển khai đang giữ, ví dụ nếu bạn đã nhập bcrypt với cost trên 11, một hash Scrypt.NET `$s2$` với `N` cao, hoặc đã nâng `Pbkdf2Iterations` vượt xa mặc định. Một cảnh báo duy nhất được ghi vào lần đầu tiên một lần đăng nhập thất bại vượt quá mức này. `0` tắt phần độn thời gian và mở lại kênh dò. |
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | Cửa sổ ân hạn tùy chọn (giây) cho việc dùng lại refresh token đồng thời. `0` (mặc định) giữ tư thế nghiêm ngặt: mọi lần dùng lại một refresh token đã bị tiêu thụ sẽ thu hồi mọi token của cặp người dùng+client đó. Đặt `> 0` để coi một lần dùng lại trong cửa sổ là một lần thử lại lũy đẳng (giao lại các token kế tiếp), hữu ích cho các client di động có kết nối chập chờn. |
| `Auth:DynamicClientRegistrationEnabled` | `false` | Bật endpoint đăng ký client động `POST /connect/register` (RFC 7591). Mặc định tắt vì đăng ký mở có thể bị lạm dụng trong các bản triển khai đa tenant. Xem [Đăng ký client động](client-registration). |
| `Auth:DynamicClientRegistrationScopes` | *(rỗng)* | Các scope mà một bên đăng ký ẩn danh có thể tự gán cho mình, ngoài các scope OIDC dựng sẵn luôn đăng ký được (`openid`, `profile`, `email`, `phone`, `offline_access`). Rỗng nghĩa là chỉ các scope dựng sẵn và không gì khác: việc một scope tồn tại trong store không có nghĩa là một client tự đăng ký được phép khai báo nó. Các scope giới hạn theo vai trò không bao giờ đăng ký được trong mọi trường hợp. Xem [Đăng ký client động](client-registration). |
| `Auth:SigningKeyLifetimeDays` | `90` | Thời gian sống của khóa ký trước khi tự động xoay vòng (khóa là ES256 / P-256) |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Tần suất tải lại khóa ký từ bộ lưu trữ |
| `Auth:KeyRotationEnabled` | `false` | Bật tự động xoay vòng khóa ký |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | Tần suất kiểm tra xem khóa đang dùng có cần xoay vòng hay không |
| `Auth:KeyRotationLeadTimeDays` | `14` | Xoay vòng khi khóa đang dùng sẽ hết hạn trong vòng số ngày này |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Khoảng thời gian giữa các lần kiểm tra security stamp của cookie |
| `Auth:AllowedInternalTargets` | *(rỗng)* | Các đích nội bộ mà Authagonal được phép lấy dữ liệu trên những đường mà **bạn** đã cung cấp URL: metadata SAML phía upstream, OIDC discovery phía upstream, callback cấp phát. Rỗng nghĩa là mọi địa chỉ nội bộ đều bị từ chối. Xem [Truy xuất ra ngoài](#outbound-fetches-ssrf-guard). |
| `Auth:AllowOutboundProxy` | `false` | Gửi chính những lần truy xuất do người vận hành cấu hình đó qua HTTP proxy của môi trường, chấp nhận rằng phép kiểm tra địa chỉ không nhìn xuyên qua được proxy. Không bao giờ áp dụng cho một `jwks_uri` hay URI đăng xuất back-channel do client đăng ký. Xem [Truy xuất ra ngoài](#outbound-fetches-ssrf-guard). |
| `Auth:AtRestBackfillEnabled` | `false` | Chạy việc bổ sung dữ liệu khi lưu trữ một lần lúc khởi động, trên leader của cụm. Nó viết lại mọi bản ghi người dùng hiện có và các dòng chỉ mục suy ra từ hồ sơ theo cơ chế lưu trữ hiện tại, đây là đường chuyển đổi để bật `IFieldCipher` / `IIndexTokenizer` trên một bản triển khai đã có dữ liệu (xem [Khả năng mở rộng](extensibility#pii-field-encryption-ifieldcipher)). Chỉ đăng ký một cipher thì chỉ mã hóa các dòng được ghi về sau. Đây là khối lượng ghi thực sự, lũy đẳng, và chạy một lần cho mỗi tiến trình, nên hãy tắt nó khi log báo đã chạy xong hoàn toàn. |
| `Auth:MaxScimGroupsPerClient` | `5000` | Số nhóm SCIM tối đa mà một client cấp phát có thể sở hữu; thao tác tạo bị từ chối khi vượt quá. Bộ lưu trữ nhóm không có chỉ mục, nên một bảng không giới hạn sẽ khiến mọi lần phát hành token phải trả giá cho nó. |
| `Auth:MaxScimGroupMembers` | `10000` | Số thành viên tối đa mà một nhóm SCIM có thể chứa; các thao tác tạo, thay thế và patch bị từ chối khi vượt quá. |

## Data Protection {#data-protection}

Các khóa ASP.NET Core Data Protection (dùng để mã hóa cookie phiên) phải được dùng chung giữa các instance, xem [Mở rộng quy mô](scaling#cookie-encryption-data-protection). Các tùy chọn lưu trữ, theo thứ tự ưu tiên:

| Thiết lập | Mặc định | Mô tả |
|---|---|---|
| `DataProtection:BlobUri` | *(không có)* | URI Azure Blob tường minh cho vòng khóa (ví dụ `https://{account}.blob.core.windows.net/dataprotection/keys.xml`). Xác thực qua `DefaultAzureCredential`, là đường production được ưu tiên cùng với `Storage:TableServiceUri`. |
| *(dự phòng)* | *(không có)* | Khi `DataProtection:BlobUri` không được đặt, vòng khóa được lưu tự động: vào container `dataprotection` trong tài khoản được nêu bởi `Storage:ConnectionString` (trừ khi đó là Azurite), hoặc, trên đường managed identity, vào blob endpoint suy ra từ `Storage:TableServiceUri` (`https://{account}.table.…` → `https://{account}.blob.…/dataprotection/keys.xml`), đường này cần quyền Storage Blob Data Contributor trên cùng tài khoản. Chỉ một table endpoint không nhận diện được (Azurite, các trình giả lập kiểu path-style) mới quay về kho tệp theo từng máy, vốn tạm thời và riêng từng pod; `KeyRingStartupCheck` ghi log mức Critical khi điều đó xảy ra. |

Trên backend AWS, truyền một S3 client + bucket vào `AddAuthagonalAwsStorage` để lưu vòng khóa vào S3, xem [Cài đặt → Backend AWS](installation#aws-backend). Trên backend SQL, vòng khóa được lưu bởi `AddAuthagonalPostgres` / `AddAuthagonalSqlite`, xem [Cài đặt → Backend SQL](installation#sql-backend).

Lưu trữ không có nghĩa là mã hóa. Dù backend nào giữ vòng khóa, nó cũng được ghi dưới dạng XML bản rõ (bao gồm cả master key) trừ khi một trong các thiết lập sau được đặt. Vòng khóa đó bảo vệ cookie xác thực, nên quyền đọc store đồng nghĩa với khả năng giả mạo phiên cho bất kỳ người dùng nào:

| Thiết lập | Mặc định | Mô tả |
|---|---|---|
| `DataProtection:KeyVaultKeyId` | *(không có)* | URI khóa Azure Key Vault dùng để bọc vòng khóa. Xác thực qua `DefaultAzureCredential`. |
| `DataProtection:CertificateThumbprint` | *(không có)* | Thumbprint của một chứng chỉ trong kho của máy dùng để bọc vòng khóa. |
| `DataProtection:AllowUnencryptedKeyRing` | `false` | Chủ động chấp nhận một vòng khóa bản rõ. Được nhắc lại ở mức `Critical` mỗi lần khởi động để nó hiện ra trong một lần audit thay vì chỉ nằm trong tệp cấu hình. |

Lúc khởi động, quy tắc này được áp đặt dựa trên các tùy chọn vòng khóa *đã phân giải*, nên nó áp dụng giống hệt nhau cho Azure, AWS, SQL và mọi repository do host đăng ký. Một bản triển khai lưu vòng khóa không mã hóa và **chưa có khóa nào** sẽ bị từ chối, nên trạng thái không an toàn không bao giờ được tạo ra; một bản triển khai mà vòng khóa **đã có khóa** sẽ khởi động và ghi log mức `Critical`, vì từ chối ở đó sẽ đánh sập một bản triển khai đang chạy chỉ vì một lần nâng phiên bản. Môi trường Development không bao giờ bị từ chối.

## Cache và thời gian chờ {#cache-and-timeouts}

| Thiết lập | Mặc định | Mô tả |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | Thời gian lưu đệm danh sách origin CORS được phép |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | Thời gian lưu đệm tài liệu OIDC discovery |
| `Cache:SamlMetadataCacheMinutes` | `60` | Thời gian lưu đệm metadata của SAML IdP |
| `Cache:OidcStateLifetimeMinutes` | `10` | Thời gian sống của tham số state trong ủy quyền OIDC |
| `Cache:SamlReplayLifetimeMinutes` | `10` | Thời gian sống của ID AuthnRequest SAML (chống phát lại) |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Thời gian chờ health check của Table Storage |
| `Cache:HealthCheckCacheSeconds` | `5` | Thời gian câu trả lời của `/health` được dùng lại trước khi truy vấn bộ lưu trữ lần nữa (khớp với `Cache-Control: max-age` mà endpoint công bố). `0` thăm dò ở mọi request, điều này mở lại lỗ hổng khuếch đại ẩn danh mà cơ chế cache đã đóng lại. |

## Service chạy nền {#background-services}

| Thiết lập | Mặc định | Mô tả |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | Độ trễ ban đầu trước lần dọn token hết hạn đầu tiên |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | Chu kỳ dọn token hết hạn |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | Độ trễ ban đầu trước lần đối chiếu grant đầu tiên |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | Chu kỳ đối chiếu grant |

### Quét dữ liệu hết hạn (Azure Table) {#expiry-sweeps-azure-table}

Azure Table Storage không có TTL, nên trên backend Azure, máy chủ chạy một `TableExpirySweepService` cho mỗi bảng (cứ 15 phút một lần, chỉ trên leader của cụm) đối với `MfaChallenges`, `RevokedTokens` và `UpstreamRefreshTokens`, xóa các dòng đã quá hạn. Đây chỉ là việc dọn dữ liệu lưu giữ: mọi dòng trong số đó vốn đã bị từ chối khi đọc nhờ phép kiểm tra hết hạn của chính nó. Một dòng không có thời hạn được nêu (có thể xảy ra trên `UpstreamRefreshTokens`) cố ý không bao giờ bị quét. DynamoDB và SQL tự thu dọn ba bảng này bằng cơ chế sẵn có. Không có gì cần cấu hình.

## Chống bot (Cloudflare Turnstile) {#bot-protection-cloudflare-turnstile}

Tùy chọn bật thêm. Khi một secret key được đặt, các thao tác đăng nhập, đăng ký, quên mật khẩu và đặt lại mật khẩu sẽ xác minh một `turnstileToken` với Cloudflare trước khi làm bất cứ việc gì; khi không có secret key, không có gì thay đổi và không widget nào được hiển thị.

| Thiết lập | Mặc định | Mô tả |
|---|---|---|
| `Turnstile:SiteKey` | *(không đặt)* | Sitekey công khai, được cung cấp cho giao diện đăng nhập (`turnstileSiteKey` trên `GET /api/auth/providers`) để nó có thể hiển thị widget |
| `Turnstile:SecretKey` | *(không đặt)* | Secret cho việc xác minh phía máy chủ. Không đặt hoặc để rỗng sẽ tắt hoàn toàn Turnstile |

Một host phục vụ các tên miền do khách hàng cung cấp không thể dùng một cặp khóa duy nhất (Cloudflare giới hạn số hostname của một widget); nó thay thế [`ITurnstileKeyProvider`](extensibility#iturnstilekeyprovider). Xem [Auth API](auth-api#providers) về lỗi `captcha_failed`.

## Vai trò {#roles}

Vai trò được định nghĩa trong mảng `Roles` và được nạp sẵn lúc khởi động, cùng với client, scope và
provider. Việc nạp sẵn vai trò quan trọng nhất khi một scope được giới hạn bằng
[`AllowedRoles`](scopes#role-gated-scopes): một scope giới hạn theo một vai trò mà không có gì tạo ra thì bị chặn
với tất cả mọi người, kể cả người vận hành đã cấu hình nó, và lỗi này diễn ra âm thầm: scope
đơn giản là không bao giờ được cấp.

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

| Trường | Mô tả |
|---|---|
| `Name` | Tên vai trò, như được dùng trong `Scope.AllowedRoles` và trên claim `roles` của token |
| `Description` | Dành cho người đọc; được cập nhật ở các lần khởi động sau khi dữ liệu nạp sẵn có nêu giá trị |
| `Members` | Các email được đưa vào vai trò ở mỗi lần khởi động. Một địa chỉ chưa có người dùng sẽ bị bỏ qua kèm cảnh báo và được thử lại ở lần khởi động sau, nên việc khởi động không bao giờ phụ thuộc vào một tài khoản mà ai đó chưa tạo |

Việc nạp sẵn là **chỉ cộng thêm và lũy đẳng**. Nó không bao giờ xóa vai trò hay thu hồi tư cách thành viên: cấu hình
không phải là nguồn dữ liệu gốc về việc ai nắm giữ gì, nên một vai trò được cấp qua admin API vẫn còn sau
lần khởi động lại tiếp theo.

## Client {#clients}

Client được định nghĩa trong mảng `Clients` và được nạp sẵn lúc khởi động. Mỗi client có thể có:

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

Việc nạp sẵn theo kiểu **đọc-trộn-ghi**: một trường mà dữ liệu nạp sẵn không nêu sẽ giữ giá trị đã lưu, nên một lần khởi động lại không bao giờ hoàn tác một thay đổi được thực hiện qua admin API (một client bị tắt vẫn bị tắt, một secret đã xoay vòng vẫn còn, `Audiences` và JWKS của client được giữ nguyên). Một trường mà dữ liệu nạp sẵn có nêu sẽ bị ghi đè ở mỗi lần khởi động.

Ghi chú về các trường (từ `ClientSeedService.ClientSeedConfig`):

- **Bí danh.** `ClientId`/`Id`, `ClientName`/`Name`, `AllowedGrantTypes`/`GrantTypes`, `AllowedScopes`/`Scopes`, `AllowedCorsOrigins`/`CorsOrigins` và `RequireClientSecret`/`RequireSecret` dùng thay thế cho nhau được. Một đối tượng `SeedClient` đơn lẻ cũng được đọc như thêm một mục nữa.
- **Secret.** Cung cấp hoặc `SecretHashes` (đã hash sẵn) hoặc `ClientSecret` (bản rõ, được hash lúc khởi động, chỉ dùng khi không có hash nào được cung cấp). Dữ liệu nạp sẵn chỉ được áp dụng khi nó cung cấp một giá trị, nên một secret đã xoay vòng qua admin API vẫn còn sau lần khởi động lại tiếp theo. Không có khóa `ClientSecretHashes` trong cấu trúc dữ liệu nạp sẵn.
- **`BackChannelLogoutUri`**: nơi logout token back-channel được POST tới; xem [Đăng xuất back-channel](#back-channel-logout).
- **`RestrictedToOrganizationIds`**: các id tổ chức mà client có thể được dùng cùng. Rỗng nghĩa là không giới hạn; một mục duy nhất còn chọn luôn tổ chức đó cho một request không nêu tổ chức nào (xem [Tổ chức](organizations)).
- **`InitiateLoginUri`, `ClientUri`, `IsDefaultApplication`**: cung cấp dữ liệu cho danh sách `/api/auth/apps` và nút tiếp tục tới ứng dụng trên màn hình đăng nhập.
- **Không nạp sẵn được.** `RequireConsent`, `ProvisioningApps`, `RequirePushedAuthorizationRequests`, JWKS của client và các trường đăng xuất front-channel không có khóa trong cấu trúc dữ liệu nạp sẵn, nên cấu hình không thể đặt chúng. Các khóa không xác định bị bỏ qua mà không có cảnh báo.
- Một dữ liệu nạp sẵn có scope hoặc audience vi phạm quy tắc scope dành riêng hoặc quy tắc audience sẽ bị từ chối kèm một log lỗi và bị bỏ qua.

### Audience và resource indicator (RFC 8707) {#audiences-and-resource-indicators-rfc-8707}

`Audiences` là danh sách cho phép của client đối với tham số `resource` (RFC 8707) và tham số `audience` của một token exchange (RFC 8693). Bất cứ giá trị nào vượt qua phép kiểm tra đó sẽ trở thành claim `aud` của access token được phát hành; khi request không có `resource`, `aud` dùng `Audiences` làm dự phòng, và khi không có cả hai thì nó là `client_id`.

Một danh sách `Audiences` rỗng nghĩa là **"không có gì"** đối với mọi client đã thực sự trả lời câu hỏi này: client mà request tạo ra nó có mang trường `audiences`, dù qua đăng ký động (nơi trường này là một phần mở rộng của Authagonal cho RFC 7591), admin API, hay cấu hình nạp sẵn. Client như vậy không được nêu bất kỳ `resource` nào, trên mọi đường: authorize, `client_credentials` và token exchange đều thống nhất.

Một lần đăng ký động **bỏ qua** `audiences` (mọi client RFC 7591 tiêu chuẩn, tức là mọi client MCP) thì chưa từng được hỏi. Danh sách của nó là "chưa đặt" và nó có thể nêu bất kỳ URI tuyệt đối nào làm `resource`; đặc tả ủy quyền của MCP phụ thuộc vào điều này. Cách hiểu tương tự áp dụng cho các client được lưu trước khi có `AudiencesDeclared`, vì siết chặt mọi client đã lưu khi nâng cấp sẽ làm hỏng các luồng đang hoạt động hiện nay.

| Client | `Audiences` rỗng nghĩa là |
|---|---|
| Request tạo client có mang `audiences` (trường mở rộng DCR, admin API, dữ liệu nạp sẵn) | **từ chối**: không được nêu `resource` nào |
| Lần đăng ký DCR bỏ qua `audiences` | **"chưa đặt"**: mọi URI tuyệt đối đều được chấp nhận làm `resource` |
| Được lưu trước khi có `AudiencesDeclared` | **"chưa đặt"**: mọi URI tuyệt đối đều được chấp nhận làm `resource` |

**Cập nhật một client cũ** là một lệnh `PUT` tới admin client API với `audiencesDeclared: true` (và bất kỳ `audiences` nào nó cần được ghim vào). Cờ này chỉ siết chặt: một lần cập nhật có thể đặt nó và không thể xóa nó, nên một chỉnh sửa không liên quan sẽ không bao giờ âm thầm đưa client trở về cách hiểu dễ dãi.

Hệ quả đối với các bản ghi cũ đáng được nói thẳng ra thay vì giấu đi:

> Một client có từ trước mà không có `Audiences` được cấu hình có thể nêu **bất kỳ** URI tuyệt đối nào làm `resource` tại authorization endpoint hoặc với `client_credentials`, và nhận một access token có `aud` là giá trị đó, được ký bằng khóa của tenant này, mang `sub` của người dùng đang yêu cầu và bất kỳ scope nào mà client được phép.

Một danh sách `audiences` đã khai báo được kiểm tra tại nơi nó được ghi: tối đa 20 mục, mỗi mục tối đa 512 ký tự, mỗi mục là một URI tuyệt đối có scheme tường minh và không có fragment. Các giá trị `resource` cũng phải tuân theo cùng dạng; lưu ý rằng một đường dẫn trần như `/admin` **không** được chấp nhận, dù trình phân tích `Uri` của .NET sẽ coi nó là một URI `file:` tuyệt đối trên Linux.

Nêu tên một resource không đồng nghĩa với có quyền truy cập nó. Nhưng điều đó có nghĩa là authorization server không thể là thứ duy nhất đứng giữa một client và một API mà nó vốn không được phép gọi, vì vậy:

- **Resource server PHẢI ủy quyền dựa trên `scope`** (hoặc mô hình của riêng nó), không chỉ dựa trên `iss` + `aud` + `sub`. Một token nêu API của bạn trong `aud` chứng minh rằng client đã yêu cầu API của bạn. Nó không chứng minh rằng client được phép gọi API đó, và máy chủ này không thể khiến nó chứng minh điều đó.
- **Resource server PHẢI kiểm tra `aud` với định danh của chính nó**, không chỉ kiểm tra "có một giá trị nào đó".
- **Đặt `Audiences` trên mọi client cần được ghim vào một tập API cố định.** Khi được cấu hình, một `resource` không có trong danh sách sẽ bị từ chối với `invalid_target` tại authorization endpoint và với `client_credentials`. Đây là nơi duy nhất có thể áp đặt giới hạn này.
- **Cập nhật `audiencesDeclared: true` cho các client được tạo trước khi có cờ này**, để danh sách audience rỗng của chúng mang nghĩa "không có gì" thay vì "bất cứ thứ gì".
- **Một client tự đăng ký có thể khai báo `audiences`** khi đăng ký và bị ràng buộc theo những gì nó khai báo, kể cả một danh sách rỗng. `Auth:DynamicClientRegistrationEnabled` vẫn mặc định tắt; xem [Đăng ký client động](client-registration).

### Loại grant {#grant-types}

| Loại grant | Trường hợp sử dụng |
|---|---|
| `authorization_code` | Đăng nhập tương tác của người dùng (ứng dụng web, SPA, di động) |
| `client_credentials` | Giao tiếp giữa các service |
| `refresh_token` | Gia hạn token (yêu cầu `AllowOfflineAccess: true`) |
| `urn:ietf:params:oauth:grant-type:device_code` | Device authorization grant (RFC 8628) cho các thiết bị hạn chế khả năng nhập liệu |

### Cách dùng refresh token {#refresh-token-usage}

| Giá trị | Hành vi |
|---|---|
| `OneTime` (mặc định) | Mỗi lần refresh phát hành một refresh token mới và vô hiệu hóa token cũ. Mặc định (`Auth:RefreshTokenReuseGraceSeconds = 0`) mọi lần dùng lại một token đã bị tiêu thụ sẽ thu hồi ngay lập tức mọi token của cặp người dùng+client đó, **không** có cửa sổ ân hạn nào được bật theo mặc định. Đặt `Auth:RefreshTokenReuseGraceSeconds` thành một giá trị dương để bật cửa sổ chịu đựng việc thử lại. |
| `ReUse` | Cùng một refresh token được dùng lại cho tới khi hết hạn. |

### Ứng dụng cấp phát {#provisioning-apps}

Mảng `ProvisioningApps` của một client (được đọc lúc authorize, `AuthorizeEndpoint.cs:578`; bộ nạp sẵn cấu hình không bind nó và các route client của Admin API không mang nó, nên host đặt nó trên bản ghi client đã lưu) tham chiếu tới các ID ứng dụng được định nghĩa trong section cấu hình `ProvisioningApps`. Khi người dùng ủy quyền qua client này, họ được cấp phát vào các ứng dụng đó qua TCC. Xem [Cấp phát](provisioning) để biết chi tiết.

## Scope {#scopes}

Các [scope OAuth](scopes) tùy chỉnh có thể được nạp sẵn từ mảng `Scopes`. Mỗi mục được upsert theo `Name` lúc khởi động (một mục không có `Name` sẽ bị bỏ qua kèm cảnh báo):

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

Một trường bạn đặt sẽ ghi đè giá trị đã lưu ở mỗi lần khởi động; một trường bạn bỏ qua sẽ giữ nguyên giá trị đã lưu. Do đó cấu hình có thể thêm hoặc thay đổi `UserClaims` và `AllowedRoles` nhưng không thể làm rỗng chúng (hãy dùng `PUT /api/v1/scopes/{name}` cho việc đó). Ý nghĩa của các trường nằm trong [Mô hình scope](scopes#scope-model).

## Ứng dụng cấp phát {#provisioning-apps-1}

Định nghĩa các ứng dụng downstream mà người dùng cần được cấp phát vào:

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

Xem [Cấp phát](provisioning) để biết đặc tả đầy đủ của giao thức TCC.

## Chính sách MFA {#mfa-policy}

Xác thực đa yếu tố được áp đặt theo từng client qua thuộc tính `MfaPolicy`:

| Giá trị | Hành vi |
|---|---|
| `Disabled` (mặc định) | Không có thử thách MFA, kể cả khi người dùng đã đăng ký MFA |
| `Enabled` | Thử thách những người dùng đã đăng ký MFA; không bắt buộc đăng ký |
| `Required` | Thử thách người dùng đã đăng ký; bắt buộc đăng ký với người dùng chưa có MFA |

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

Khi `MfaPolicy` là `Required` và người dùng chưa đăng ký MFA, thao tác đăng nhập trả về `{ mfaSetupRequired: true, setupToken: "..." }`. Setup token xác thực người dùng với các endpoint thiết lập MFA (qua header `X-MFA-Setup-Token`) để họ có thể đăng ký trước khi nhận phiên cookie.

Đăng nhập liên kết (SAML/OIDC) cũng tuân theo chính sách MFA: một người dùng đã đăng ký MFA được chuyển qua thử thách MFA sau khi IdP bên ngoài xác thực họ, và `Required` bắt buộc người dùng liên kết chưa có MFA phải đăng ký.

### Ghi đè bằng IAuthHook {#iauthhook-override}

Phương thức `IAuthHook.ResolveMfaPolicyAsync` có thể ghi đè chính sách của client theo từng người dùng:

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

## Chính sách mật khẩu {#password-policy}

Tùy chỉnh yêu cầu về độ mạnh của mật khẩu:

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

| Thuộc tính | Mặc định | Mô tả |
|---|---|---|
| `MinLength` | `8` | Độ dài mật khẩu tối thiểu |
| `MinUniqueChars` | `2` | Số ký tự khác nhau tối thiểu |
| `RequireUppercase` | `true` | Yêu cầu ít nhất một chữ hoa |
| `RequireLowercase` | `true` | Yêu cầu ít nhất một chữ thường |
| `RequireDigit` | `true` | Yêu cầu ít nhất một chữ số |
| `RequireSpecialChar` | `true` | Yêu cầu ít nhất một ký tự không phải chữ hoặc số |

Chính sách được áp đặt khi đặt lại mật khẩu và khi admin đăng ký người dùng. Giao diện đăng nhập lấy chính sách đang hiệu lực từ `GET /api/auth/password-policy` để hiển thị các yêu cầu một cách động.

## Provider SAML {#saml-providers}

Định nghĩa các identity provider SAML trong cấu hình. Chúng được nạp sẵn lúc khởi động:

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

| Thuộc tính | Bắt buộc | Mô tả |
|---|---|---|
| `ConnectionId` | Có | Định danh ổn định (dùng trong URL như `/saml/{connectionId}/login`) |
| `ConnectionName` | Không | Tên hiển thị (mặc định là ConnectionId) |
| `EntityId` | Có | Entity ID SP **của máy chủ này**, tức định danh bạn đăng ký tại IdP, không phải entity ID của chính IdP |
| `MetadataLocation` | Có | URL tới XML metadata SAML của IdP. Phải là https, và phải định tuyến công khai được trừ khi host được nêu trong [`Auth:AllowedInternalTargets`](#outbound-fetches-ssrf-guard): tài liệu này mang các chứng chỉ mà mọi assertion được kiểm tra dựa vào. Nếu IdP của bạn không công bố endpoint metadata qua https, hãy đặt `metadataXml` qua [Admin API](admin-api); dữ liệu nạp sẵn từ cấu hình không có khóa cho trường này. |
| `AllowedDomains` | Không | Các tên miền email được định tuyến tới provider này qua SSO |
| `OrganizationId` | Không | Giới hạn kết nối này vào một [tổ chức](organizations). Null (mặc định) khiến nó là một kết nối cấp tenant; chỉ các kết nối cấp tenant mới đăng ký `AllowedDomains` của chúng làm tuyến tên miền SSO |
| `JitProvisioningEnabled` | Không | Tạo người dùng ở lần đăng nhập đầu tiên. Mặc định `false` |
| `AllowUninvitedJit` | Không | Cho phép JIT tạo người dùng vào một tổ chức mà họ chưa được mời. Mặc định `false` |
| `ChallengeMfaAfterLogin` | Không | Thử thách theo chính sách MFA của ứng dụng sau khi đăng nhập tại IdP. Mặc định `true` |
| `ProvisioningAttributeParams` | Không | Các thuộc tính trong assertion được chuyển tới việc cấp phát downstream |
| `AllowUnsolicitedResponses` | Không | Chấp nhận phản hồi do IdP khởi tạo (không được yêu cầu) trên kết nối này. Mặc định `false` |

Các giá trị boolean được ghi từ dữ liệu nạp sẵn ở mỗi lần khởi động, kể cả giá trị mặc định, nên một kết nối nạp sẵn mà người vận hành đã thay đổi qua Admin API sẽ bị đưa các giá trị đó về như cũ ở lần khởi động lại tiếp theo. Các trường không có khóa trong dữ liệu nạp sẵn (`SpCertificate`, `SignAuthnRequests`, `NameIdFormat`, `MetadataXml`, `IconUrl`) được giữ nguyên.

## Provider OIDC {#oidc-providers}

Định nghĩa các identity provider OIDC trong cấu hình. Chúng được nạp sẵn lúc khởi động:

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

| Thuộc tính | Bắt buộc | Mô tả |
|---|---|---|
| `ConnectionId` | Có | Định danh ổn định (dùng trong URL như `/oidc/{connectionId}/login`) |
| `ConnectionName` | Không | Tên hiển thị (mặc định là ConnectionId) |
| `MetadataLocation` | Có | URL tới tài liệu OpenID Connect discovery của IdP |
| `ClientId` | Có | Client ID OAuth2 đã đăng ký với IdP |
| `ClientSecret` | Có | Client secret OAuth2 (được bảo vệ qua `ISecretProvider` lúc khởi động) |
| `RedirectUrl` | Không | **Bị bỏ qua.** Redirect URI được suy ra theo từng request là `{Issuer}/oidc/callback`: hãy đăng ký *giá trị đó* với IdP. Một giá trị ở đây không có tác dụng và được ghi log là bị bỏ qua. |
| `AllowedDomains` | Không | Các tên miền email được định tuyến tới provider này qua SSO |
| `OrganizationId` | Không | Giới hạn kết nối này vào một [tổ chức](organizations); null nghĩa là cấp tenant |
| `JitProvisioningEnabled` | Không | Tạo người dùng ở lần đăng nhập đầu tiên. Mặc định `false` |
| `AllowUninvitedJit` | Không | Cho phép JIT tạo người dùng vào một tổ chức mà họ chưa được mời. Mặc định `false` |
| `UseUpstreamSubjectAsUserId` | Không | Dùng `sub` phía upstream làm user id cục bộ. Mặc định `false` |
| `ShowOnLogin` | Không | Hiển thị một nút cho kết nối này trên màn hình đăng nhập. Mặc định `true`; các kết nối định tuyến theo tên miền dù sao vẫn được truy cập theo kiểu nhập email trước |
| `ChallengeMfaAfterLogin` | Không | Thử thách theo chính sách MFA của ứng dụng sau khi đăng nhập tại IdP. Mặc định `true` |
| `AutoLinkExistingByEmail` | Không | Liên kết lần đăng nhập đầu tiên với một tài khoản cục bộ hiện có cùng email. Mặc định `false` |
| `PassthroughParams`, `ProvisioningAttributeParams` | Không | Các tham số được chuyển thẳng tới IdP / tiếp tới việc cấp phát downstream |
| `RevalidateOnRefresh` | Không | Kiểm tra lại phiên phía upstream khi một refresh token được đổi. Mặc định `false` |
| `IsExternalConnection`, `SessionExpClaim` | Không | Thiết lập phiên liên kết; xem [Phiên liên kết](federated-sessions) |
| `InteractionPath` | Không | Đường dẫn trong ứng dụng đăng nhập (ví dụ `/guest`) được hiển thị trước khi một request `idp_hint` chưa xác thực được liên kết qua kết nối này. Để trống thì liên kết trực tiếp |

Dữ liệu nạp sẵn OIDC ghi đè nhiều hơn dữ liệu nạp sẵn SAML, ở mỗi lần khởi động. Các giá trị boolean được ghi từ dữ liệu nạp sẵn, kể cả giá trị mặc định, nên một kết nối nạp sẵn mà người vận hành đã thay đổi qua Admin API sẽ bị đưa các giá trị đó về như cũ ở lần khởi động lại tiếp theo. Điều tương tự áp dụng cho `AllowedDomains`, `PassthroughParams`, `ProvisioningAttributeParams`, `SessionExpClaim` và `InteractionPath`: một khóa mà dữ liệu nạp sẵn bỏ qua sẽ bị đặt lại thành rỗng hoặc giá trị mặc định thay vì giữ giá trị đã lưu. Chỉ `IconUrl` và `CreatedAt` luôn được giữ nguyên, còn `ConnectionName` và `OrganizationId` được giữ nguyên khi dữ liệu nạp sẵn bỏ qua chúng.

> **Lưu ý:** Provider cũng có thể được quản lý lúc chạy qua [Admin API](admin-api). Các provider nạp sẵn từ cấu hình được upsert ở mỗi lần khởi động, nên thay đổi cấu hình có hiệu lực khi khởi động lại.

## Secret Provider {#secret-provider}

Client secret OIDC phía upstream và seed TOTP / MFA có thể được lưu trong Azure Key Vault thay vì dưới dạng bản rõ:

| Thiết lập | Mô tả |
|---|---|
| `SecretProvider:VaultUri` | URI Key Vault (ví dụ `https://my-vault.vault.azure.net/`). Nếu không đặt, provider **bản rõ** được dùng và secret được lưu nguyên dạng trong Table Storage. |
| `SecretProvider:RequireVaultReferences` | Mặc định `false`. Khi là `true`, một tham chiếu đã lưu không có tiền tố vault (`kv:` cho Key Vault, `sm:` cho AWS Secrets Manager) là một **lỗi** thay vì được chấp nhận như một giá trị bản rõ. Hãy đặt nó khi việc chuyển vào vault đã hoàn tất. |

Khi được cấu hình, các giá trị secret có dạng tham chiếu Key Vault được phân giải lúc chạy. Dùng `DefaultAzureCredential` để xác thực.

### Chuyển vào vault, và đóng cửa lại sau đó {#migrating-into-a-vault-and-closing-the-door-afterwards}

Cả hai provider dựa trên vault đều trả về nguyên văn một tham chiếu không có tiền tố, coi nó là một giá trị bản rõ được ghi trước khi bản triển khai có vault. Đó là điều cho phép một hệ thống đang chạy được chuyển đổi từng secret một thay vì tất cả cùng lúc, nhưng nếu để mở thì nó là một đường hạ cấp vĩnh viễn: bất cứ thứ gì có thể ghi một cột cấu hình (một lần chuyển đổi làm dở, một đường admin lưu giá trị thô vào chỗ lẽ ra là tham chiếu, một kẻ tấn công có quyền truy cập bộ lưu trữ nhưng không có quyền vào vault) đều có thể thay một secret được vault bảo vệ bằng một giá trị do chính nó chọn, và giá trị đó được xác minh hoàn hảo, vì với một tham chiếu không có tiền tố thì tham chiếu *chính là* giá trị.

Hãy đặt `SecretProvider:RequireVaultReferences` khi việc chuyển đổi đã xong. Khi đó, việc phân giải một tham chiếu không có tiền tố sẽ ném lỗi thay vì lặng lẽ trả về bản rõ. Đặt nó trong khi provider được phân giải là provider bản rõ sẽ bị từ chối lúc khởi động, vì tổ hợp đó không có trạng thái nào hoạt động được: mọi tham chiếu mà provider bản rõ ghi đều không có tiền tố.

Máy chủ cũng ghi một cảnh báo lúc khởi động mỗi khi một host không phải Development kết thúc với provider bản rõ.

> ⚠️ **Production: hãy đặt `SecretProvider:VaultUri`.** Secret provider mặc định là **bản rõ**. Khi `SecretProvider:VaultUri` không được đặt, client secret OIDC phía upstream và seed TOTP / MFA được ghi vào Azure Table Storage dưới dạng bản rõ, và do đó xuất hiện dưới dạng bản rõ trong mọi [bản sao lưu](backup-restore). Với mọi bản triển khai production, hãy cấu hình `SecretProvider:VaultUri` để các secret này được lưu trong Key Vault.

## Admin API {#admin-api}

| Thiết lập | Mặc định | Mô tả |
|---|---|---|
| `AdminApi:Enabled` | `true` | **Bật theo mặc định.** Đặt `false` để tắt mọi endpoint admin (chúng sẽ không được đăng ký). |
| `AdminApi:Scope` | `authagonal-admin` | Scope JWT cần có để truy cập các endpoint admin. Đổi giá trị này cho khớp với tên scope hiện có của bạn (ví dụ `projects-identity-admin` khi chuyển từ IdentityServer). |

> ⚠️ **Admin API được bật theo mặc định và có đặc quyền rất cao.** Scope admin cấp toàn quyền quản lý và quyền mạo danh người dùng: bất kỳ ai giữ một token có `AdminApi:Scope` đều có thể tạo token cho bất kỳ người dùng nào, quản lý client, và đọc/ghi mọi cấu hình. Hãy giới hạn truy cập mạng tới các endpoint admin (các route admin `/api/v1/*`), và kiểm soát chặt chẽ ai có thể được cấp scope admin. Như một biện pháp phòng thủ nhiều lớp, scope này là *dành riêng*: nó không bao giờ có thể được cấp cho một client OAuth (xem [Admin API](admin-api)) và không thể được phát hành qua endpoint mạo danh. Đặt hẳn `AdminApi:Enabled = false` nếu không dùng admin API.

## Chấp thuận {#consent}

Chấp thuận theo từng client có thể được bật bằng thuộc tính `RequireConsent`:

| Giá trị | Hành vi |
|---|---|
| `false` (mặc định) | Ủy quyền tiếp tục ngay sau khi xác thực |
| `true` | Người dùng được hiển thị một màn hình chấp thuận liệt kê các scope được yêu cầu. Chấp thuận được lưu trong 5 năm và chỉ hỏi lại khi có scope mới được yêu cầu. |

Người dùng có thể xem và thu hồi các grant chấp thuận của mình tại `GET /consent/grants` và `DELETE /consent/grants/{clientId}`.

## Đăng xuất back-channel {#back-channel-logout}

Đăng ký một `BackChannelLogoutUri` trên client để nhận thông báo OIDC Back-Channel Logout 1.0. Khi người dùng đăng xuất, Authagonal gửi một logout token đã ký (JWT) tới URI đã đăng ký của từng client.

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

## Email {#email}

Bộ gửi email dựng sẵn dùng [Resend](https://resend.com) và **tự động kích hoạt** khi `Email:ResendApiKey` được cấu hình, không cần đăng ký service. Để dùng một nhà cung cấp khác, hãy đăng ký triển khai `IEmailService` của riêng bạn trước khi gọi `AddAuthagonal()` (nó được ưu tiên bất kể các khóa `Email:*`).

| Thiết lập | Mô tả |
|---|---|
| `Email:ResendApiKey` | API key của Resend. Khi được đặt, bộ gửi Resend dựng sẵn được dùng. |
| `Email:SenderEmail` | Địa chỉ email người gửi |
| `Email:SenderName` | Tên hiển thị của người gửi (mặc định là `"Authagonal"`) |

> ⚠️ **Không có bộ gửi email nào thì tự đăng ký bị hỏng.** Khi `Email:ResendApiKey` không được đặt và không có `IEmailService` tùy chỉnh nào được đăng ký, một service không làm gì sẽ âm thầm bỏ đi mọi thư, email xác minh và email đặt lại mật khẩu không bao giờ tới nơi, và vì đăng nhập mặc định yêu cầu email đã xác nhận, người dùng tự đăng ký sẽ không bao giờ đăng nhập được. `UseAuthagonal` ghi một cảnh báo lúc khởi động khi ở trạng thái này. Lối thoát cho dev/test: `Auth:AutoConfirmEmailDomains` tự động xác nhận các lần đăng ký cho những tên miền được liệt kê.

Email gửi tới các địa chỉ `@example.com` bị âm thầm bỏ qua (hữu ích khi kiểm thử).

## Cụm {#cluster}

Lớp cụm cung cấp **bầu chọn leader** (để các job chỉ chạy trên leader như xoay vòng khóa ký chạy trên đúng một node) và một **event bus giữa các node**, phía sau các backend có thể thay thế. Mặc định là trong tiến trình: một node duy nhất luôn là leader của chính nó, là thiết lập đúng cho môi trường một node và phát triển cục bộ, không cần cấu hình gì.

| Thiết lập | Biến môi trường | Mặc định | Mô tả |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Công tắc chính. Khi `false`, node chạy độc lập (luôn là leader, event bus trong tiến trình). |
| `Cluster:Secret` | `Cluster__Secret` | *(không có)* | Secret dùng chung được yêu cầu trên endpoint chỉ dùng nội bộ `/_internal/backchannel-logout`. Khi được đặt, bên gọi phải xuất trình nó trong header `X-Cluster-Secret` (so sánh trong thời gian hằng định). Khi **không đặt, endpoint không ủy quyền cho ai cả** và trả về 404: một địa chỉ nguồn không phải là thông tin xác thực, và loopback chính là thứ mà một reverse proxy cùng host xuất trình cho mọi request nó chuyển tiếp, kể cả các request bắt nguồn từ internet. |
| `Cluster:AllowLoopbackWithoutSecret` | `Cluster__AllowLoopbackWithoutSecret` | `false` | Tùy chọn bật cho môi trường phát triển: khi không có `Cluster:Secret`, chấp nhận một bên gọi có **địa chỉ peer trước khi xử lý forwarding** là loopback. Các dải địa chỉ riêng vẫn bị từ chối: trong một mạng cụm dùng chung, điều đó sẽ tin cậy mọi workload lân cận. Không đặt nó trên một host đứng sau reverse proxy. |
| `Cluster:RunLeaderElection` | `Cluster__RunLeaderElection` | `true` | Node này có chạy vòng lặp gia hạn lease và có thể trở thành leader hay không. `false` vẫn tham gia cụm và tiêu thụ event bus; nó chỉ không bao giờ tranh lease. Phù hợp cho một node phải nhận sự kiện của cụm nhưng không bao giờ được giữ vai trò leader. |
| `Cluster:LeaseTtlSeconds` | `Cluster__LeaseTtlSeconds` | `30` | Thời hạn lease leader. Được gia hạn ở khoảng một nửa chu kỳ này. |
| `Cluster:PollIntervalSeconds` | `Cluster__PollIntervalSeconds` | `3` | Tần suất backend event bus thăm dò các thông điệp do các node khác phát hành. |

**Các bản triển khai nhiều node** thay vào một backend thực qua callback `configureClustering` trên `AddAuthagonal` / `AddAuthagonalCore`:

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

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` chỉ đăng ký event bus, giữ lease trong tiến trình, dành cho các node phải nhận sự kiện của cụm nhưng không bao giờ được tranh vai trò leader.

Xem [Mở rộng quy mô](scaling) để biết vai trò leader và event bus hoạt động thế nào giữa các instance.

## Forwarded header (proxy tin cậy) {#forwarded-headers-trusted-proxy}

Authagonal dùng IP của client làm khóa cho giới hạn tần suất và khóa tài khoản, và chỉ phát HSTS trên các request HTTPS. Khi đứng sau một reverse proxy / ingress, IP và scheme thực của client tới trong các header `X-Forwarded-For` / `X-Forwarded-Proto`. Các thiết lập này kiểm soát **những hop proxy nào được tin cậy** để đặt các giá trị đó, để một bên gọi không thể giả mạo `X-Forwarded-For` nhằm làm giả IP của client.

| Thiết lập | Biến môi trường | Mặc định | Mô tả |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | Số hop proxy được chấp nhận tính từ bên phải của chuỗi `X-Forwarded-For`. Mặc định `1` chỉ tin cậy đúng một hop mà ingress của bạn thêm vào và bỏ qua mọi thứ xa hơn về bên trái chuỗi. |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0` (mảng) | *(rỗng)* | Các dải CIDR (mảng chuỗi, ví dụ `"10.0.0.0/8"`) được phép đặt forwarded header. Đặt giá trị này thành CIDR của proxy / ingress / pod. Việc khai báo nó là điều cho phép `X-Forwarded-Proto` được chấp nhận; xem bên dưới. |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies__0` (mảng) | *(rỗng)* | Các địa chỉ IP proxy riêng lẻ (mảng chuỗi) được phép đặt forwarded header. Dùng cùng với hoặc thay cho `KnownNetworks`. |

```json
{
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"],
    "KnownProxies": []
  }
}
```

### Hai header không được tin cậy theo cùng điều kiện {#the-two-headers-are-not-trusted-on-the-same-terms}

`X-Forwarded-For` điều chỉnh **IP của client**, khóa mà giới hạn tần suất, khóa tài khoản và cổng chặn `/_internal` dựa vào. Khi không có gì được khai báo, Authagonal chấp nhận nó từ loopback và các dải RFC1918 và ghi một cảnh báo. Đó là một mặc định ở mức cố gắng tốt nhất, và nó tốt hơn hành vi của framework khi tập tin cậy rỗng, vốn là chấp nhận header từ *bất kỳ* bên gọi nào.

`X-Forwarded-Proto` thay đổi **scheme**, và scheme quyết định `/connect/*` có trả lời hay không (RFC 6749 §3.1/§3.2), cookie có được đánh dấu `Secure` hay không, và các URL tuyệt đối được sinh ra có là https hay không. Nó **chỉ** được chấp nhận từ một proxy bạn đã khai báo trong `KnownNetworks` / `KnownProxies`. Một địa chỉ riêng không phải là một lời khai báo: Authagonal được phát hành dưới dạng thư viện và không thể nhìn thấy mạng mà nó được triển khai lên, nên "peer có một địa chỉ riêng" chỉ là một phỏng đoán về topology. Trên một LAN phẳng, một VPC dùng chung hoặc một bridge container dùng chung, mọi workload lân cận đều nằm trong các dải đó và có thể khẳng định `https` cho một request thực ra đến dưới dạng không mã hóa.

**Nếu proxy của bạn không có địa chỉ cố định** (một ingress Kubernetes, một load balancer luân chuyển địa chỉ, một nền tảng không cho bạn biết CIDR của hop), hãy khai báo mọi peer là proxy:

```json
{
  "ForwardedHeaders": {
    "KnownNetworks": ["0.0.0.0/0", "::/0"]
  }
}
```

Điều đó an toàn đúng khi không có gì ngoài proxy có thể chạm tới tiến trình, đây cũng là giả định mà một bản triển khai như vậy vốn đang dựa vào. Viết nó ra giúp đặt giả định đó ở một nơi có thể được rà soát, thay vì để thư viện tự suy đoán. Nếu các workload khác *có thể* chạm trực tiếp tới Kestrel, chúng có thể giả mạo scheme và IP của client với thiết lập này, nên hãy ghim CIDR thực thay vào đó.

### Proxy không được khai báo: mọi hạn mức theo nguồn đều dùng chung {#undeclared-proxy-every-per-source-quota-is-shared}

Các giới hạn tần suất dựa theo địa chỉ của bên gọi (đăng nhập, đăng ký, quên mật khẩu, đăng ký client động, SAML ACS) cần biết client nào đã gửi request. Khi đứng sau reverse proxy, đó là IP client được chuyển tiếp, và IP client được chuyển tiếp chỉ là bằng chứng nếu bạn đã khai báo proxy đã ghi nó. Khi không có gì được khai báo, Authagonal dùng làm khóa cho các hạn mức đó chính peer mà nó thực sự quan sát được, mà khi đứng sau proxy thì đó là proxy: **mọi client dùng chung một ngân sách, và bất kỳ một bên gọi nào cũng có thể tiêu hết nó cho tất cả mọi người** (mặc định cho đăng nhập là 30 lần thử mỗi 5 phút).

Đó là có chủ đích chứ không phải lỗi, và không thể sửa được ở phía máy chủ. Phương án thay thế, cứ dùng giá trị được chuyển tiếp làm khóa, sẽ trao cho bên gọi một ngân sách mới ở mỗi request chỉ bằng cách thay đổi một header, vì khi đứng sau một load balancer L4, hop chuyển tiếp ngoài cùng bên phải *chính là* header của bên gọi. Bạn đang ở trường hợp nào trong hai trường hợp đó chính là điều mà lời khai báo cho máy chủ biết và không gì khác có thể cho biết. Hãy khai báo proxy và các hạn mức sẽ trở thành theo từng client.

> ⚠️ **Bắt buộc có proxy kết thúc TLS, và proxy đó phải được khai báo.** Authagonal phải chạy sau một reverse proxy kết thúc TLS (hoặc tự kết thúc TLS). HSTS (`Strict-Transport-Security`) chỉ được phát trên các request HTTPS, và các endpoint OAuth từ chối thẳng các request không mã hóa trừ khi `Auth:AllowInsecureHttp` được đặt, nên proxy phải chuyển tiếp `X-Forwarded-Proto: https` **và** được nêu trong `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` thì HSTS mới được gửi và `/connect/*` mới trả lời. Không khai báo gì là lỗi nâng cấp thường gặp: header tới nơi, không gì có quyền dựa vào nó, và mọi request `/connect/*` trả về 400 trên một bản triển khai thực sự đang chạy TLS. Log lúc khởi động có nói điều này, và nội dung phản hồi từ chối cũng vậy.

## Truy xuất ra ngoài (chống SSRF) {#outbound-fetches-ssrf-guard}

Authagonal thực hiện các request HTTP do máy chủ khởi tạo tới những URL mà nó không tự chọn: metadata SAML hoặc tài liệu OIDC discovery của một IdP phía upstream, `jwks_uri` của một client trong quá trình xác thực `private_key_jwt`, một URI đăng xuất back-channel, một callback cấp phát. Một số URL trong đó do chính người đăng ký client cung cấp, và khi đó một URL nêu `169.254.169.254` hoặc một host bên trong cụm của bạn là một request mà Authagonal thực hiện thay cho kẻ tấn công.

Mọi lần truy xuất như vậy đều được kiểm tra hai lần. **Phép kiểm tra URL** từ chối các scheme không phải http(s), các địa chỉ nội bộ dạng literal, và các tên `localhost` / `.local` / `.internal`, ngay tại thời điểm URL được chấp nhận (một lần ghi của admin, một lần đăng ký client động), nơi lỗi có thể quy cho người đã nhập nó. **Phép kiểm tra địa chỉ** chạy ở tầng socket: nó phân giải host, từ chối mọi địa chỉ trả về là nội bộ, và kết nối tới một địa chỉ mà nó thực sự đã kiểm tra thay vì trả tên lại cho hệ điều hành. Phép kiểm tra thứ hai này là điều mà kiểm tra văn bản không làm được, vì hostname không phải là văn bản mà kẻ tấn công buộc phải trung thực: `logout.attacker.test` vượt qua mọi quy tắc về hậu tố và literal rồi trả lời bằng địa chỉ metadata của cloud. Vì một lần chuyển hướng là một kết nối mới, phép kiểm tra địa chỉ chạy lại ở mọi hop.

Cả hai đều bật theo mặc định và hầu hết các bản triển khai không bao giờ nhận ra chúng. Có hai trường hợp khiến chúng lộ ra.

### Chủ động truy cập một đích nội bộ {#reaching-an-internal-destination-on-purpose}

Liên kết với một IdP chỉ truy cập được qua mạng riêng của bạn, hoặc cấp phát vào một ứng dụng chạy trong cùng cụm, bị từ chối bởi chính quy tắc chặn cuộc tấn công. Hãy nêu tên các đích đó:

```json
{
  "Auth": {
    "AllowedInternalTargets": ["idp.corp.internal", "*.svc.corp.internal", "10.4.0.0/16"]
  }
}
```

| Dạng mục | Cho phép |
|---|---|
| `idp.corp.internal` | Đúng host đó, và mọi địa chỉ mà nó phân giải ra |
| `*.corp.internal` | Mọi host dưới hậu tố đó, và mọi địa chỉ mà chúng phân giải ra |
| `10.4.0.0/16`, `fd00:1234::/48` | Mạng đó, dưới bất kỳ tên nào |
| `10.4.1.7` | Đúng một địa chỉ đó, dưới bất kỳ tên nào |

Dạng biến môi trường là `Auth__AllowedInternalTargets__0`, `__1`, v.v. Một mục CIDR sai định dạng sẽ gây lỗi lúc khởi động thay vì âm thầm không cho phép gì.

**Danh sách này chỉ áp dụng cho các URL do bạn cung cấp.** Lần truy xuất metadata SAML phía upstream, OIDC discovery phía upstream (bao gồm `token_endpoint`, `userinfo_endpoint` và `jwks_uri` mà tài liệu đó nêu) và các callback cấp phát. Nó cố ý **không** áp dụng cho một `jwks_uri` hay URI đăng xuất back-channel do client đăng ký, nơi một host nội bộ không bao giờ là một phần của bản triển khai, để việc mở một đích liên kết không thể đồng thời mở dịch vụ metadata cho một request `/connect/token` ẩn danh. Không có công tắc "tắt" toàn cục.

Lưu ý rằng https vẫn là bắt buộc trên cả hai URL metadata liên kết, bất kể danh sách này. Tài liệu đó mang các khóa và chứng chỉ mà mọi assertion phía upstream được kiểm tra dựa vào, và một mạng riêng không phải là một kênh an toàn.

> ⚠️ **Host đa tenant: hãy kiểm tra ai ghi URL metadata trước khi liệt kê bất cứ thứ gì.** Danh sách này giới hạn ở các đích do *bạn* cấu hình, và trong một bản triển khai đơn tenant thì người quản trị kết nối là bạn. Nếu bạn vận hành Authagonal cho người khác (một SaaS nơi admin của tenant tự cấu hình kết nối SAML/OIDC qua portal hoặc admin API), thì `MetadataLocation` do **khách hàng** cung cấp, và mọi mục bạn thêm vào đây đều có thể bị bất kỳ tenant nào chạm tới bằng cách trỏ một kết nối vào nó. Hãy để trống trên một host như vậy (mặc định), và nếu một tenant thực sự cần một IdP on-premises, hãy cho họ một đường egress kết thúc bên ngoài mạng của bạn thay vì mở một đường từ bên trong.

### Nếu egress của bạn yêu cầu HTTP proxy {#if-your-egress-requires-an-http-proxy}

Phép kiểm tra địa chỉ được gắn vào `SocketsHttpHandler.ConnectCallback`, và khi có proxy, .NET gọi callback đó với endpoint của **proxy** và không bao giờ với endpoint của đích, nên phép kiểm tra sẽ xem xét proxy, thấy nó hoàn toàn định tuyến được, và cho phép mọi thứ. Nó sẽ mở khi lỗi (fail open) đúng trong những mạng có khả năng dùng proxy nhất. Vì vậy các client được bảo vệ đặt `UseProxy = false`, và trong một mạng chỉ đi ra qua proxy, các lần truy xuất của chúng sẽ thất bại.

`Auth:AllowOutboundProxy` gửi các lần truy xuất do người vận hành cấu hình (metadata SAML, OIDC discovery, callback cấp phát) trở lại qua proxy. Với chúng, bạn giữ phép kiểm tra URL và mất phép kiểm tra địa chỉ: một hostname phân giải ra địa chỉ nội bộ sẽ không còn bị bắt. Nó **không** áp dụng cho lần truy xuất `jwks_uri` của client hay việc gửi đăng xuất back-channel: các đích đó do bên đăng ký chọn và có thể bị chạm tới từ các request ẩn danh, nên không có công tắc nào cho chúng. Một mạng buộc phải đi qua proxy cho các lần truy xuất đó cần một egress gateway có lọc SSRF đặt phía trước.

`UseAuthagonal()` ghi một cảnh báo lúc khởi động khi phát hiện `HTTPS_PROXY`, `HTTP_PROXY` hoặc `ALL_PROXY` được đặt, nêu tên những client nào bỏ qua proxy; nếu không, triệu chứng sẽ là "SSO ngừng hoạt động" mà không có gì chỉ ra nguyên nhân.

### Những gì không được bảo vệ {#what-is-not-guarded}

Các client gửi ra ngoài của BFF và việc gửi email. `AuthagonalBffOptions.Upstreams[].TargetBaseUrl` là cấu hình của chính bạn và ví dụ trong tài liệu của nó là một địa chỉ nội bộ, token client của BFF giao tiếp với authority mà bạn đã cấu hình, và proxy vốn đã từ chối mọi đích được ghép ra mà rời khỏi authority upstream đã cấu hình, nên bên gọi không thể điều hướng các request đó. `Resend` gửi POST tới một hằng số lúc biên dịch. Cả ba đều dùng proxy của môi trường như bình thường.

## Giới hạn tần suất {#rate-limiting}

Các giới hạn tần suất dựng sẵn bảo vệ những endpoint dễ bị lạm dụng:

| Endpoint | Giới hạn | Cửa sổ | Khóa theo |
|---|---|---|---|
| `POST /api/auth/login` | 30 (`Auth:MaxLoginAttemptsPerIp`) | 5 phút (`Auth:LoginWindowMinutes`) | Địa chỉ nguồn, và tính riêng theo email được gửi lên |
| `POST /api/auth/register` | 5 (`Auth:MaxRegistrationsPerIp`) | 1 giờ (`Auth:RegistrationWindowMinutes`) | IP của client |
| `POST /api/auth/forgot-password` | 3 (`Auth:MaxPasswordResetsPerEmail`) | 1 giờ (`Auth:PasswordResetWindowMinutes`) | Email đích |
| `POST /api/auth/forgot-password` | 15 (`Auth:MaxPasswordResetsPerIp`) | 1 giờ (`Auth:PasswordResetWindowMinutes`) | IP của client |
| `POST /connect/register` (khi được bật) | 10 | 1 giờ | IP của client |
| Các endpoint SCIM | 200 | 1 phút | Client SCIM |

Theo mặc định, các giới hạn được áp đặt **trong tiến trình, theo từng node** (phía sau điểm nối `IRateLimiter`), nên với N instance, mức trần hiệu lực là N lần giá trị đã cấu hình. Hãy coi chúng là lớp chặn dự phòng và áp đặt giới hạn toàn cục chính thức ở biên (WAF / ingress / CDN). Xem [Mở rộng quy mô](scaling#rate-limiting).

### Giới hạn trên toàn cụm (`Auth:DurableRateLimiting`) {#cluster-wide-limits-authdurableratelimiting}

Đặt `Auth:DurableRateLimiting` thành `true` để chuyển các bộ đếm vào store mà bản triển khai đang
chạy sẵn, để mọi replica dùng chung một ngân sách và mức trần không còn nhân lên theo số instance.

| | trong tiến trình (mặc định) | bền vững |
|---|---|---|
| Mức trần trên N replica | N lần giá trị đã cấu hình | giá trị đã cấu hình |
| Chi phí mỗi lần kiểm tra | không có | một lượt đi về store |
| Còn lại sau khi pod khởi động lại | không | có |
| Backend | bất kỳ | Azure Table, SQL, DynamoDB |

Đáng bật khi một ngân sách bảo vệ thứ có thể đoán được, trên hết là `user_code` của device flow, nơi
giới hạn số lần thử là thứ duy nhất đứng giữa kẻ tấn công và một mã cấp một phiên đang hoạt động, và một
ngân sách tăng theo số replica là sai về bản chất. Ít hữu ích hơn với các giới hạn về lưu lượng, nơi
biên dù sao vẫn là giới hạn chính thức.

Những chi tiết quan trọng trong production:

- **Nó không miễn phí.** Mọi lần kiểm tra giới hạn tần suất trở thành một lượt đi về store, kể cả trên các đường
  đăng nhập, token và SCIM. Một bản triển khai một node không được lợi gì (ở đó, theo từng node *chính là* trên toàn cụm) và nên
  để tắt.
- **Cửa sổ cố định, nên các đợt dồn dập có thể vắt qua ranh giới.** Một ngân sách N là "N mỗi cửa sổ, và tới 2N
  khi vắt qua ranh giới", và các ngân sách mặc định có đủ khoảng dư đó. Đây là điều cho phép bộ đếm là một lệnh
  tăng nguyên tử duy nhất trên mọi backend, đó là tính chất mà tính đúng đắn dựa vào.
- **Nó mở khi lỗi.** Nếu không truy cập được store, request được cho phép và một lỗi được ghi log: bộ
  giới hạn bảo vệ đường đăng nhập và không được trở thành một cách để đánh sập nó. Hãy giữ quy tắc ở biên.
- **Host sẽ không khởi động** nếu bạn đặt thiết lập này mà không có provider cung cấp `IRateLimitCounterStore`.
  Nó từ chối thay vì lặng lẽ quay về giới hạn theo từng node mà bạn vừa tắt.
- **Các dòng bộ đếm được thu dọn tự động**: DynamoDB bằng TTL sẵn có, SQL bằng `SqlExpiryReaper`, Azure
  Table bằng một lượt quét chỉ chạy trên leader (Table Storage không có TTL cũng không có phép tính phía máy chủ, nên đây cũng là
  backend mà một lần tăng tốn một lần đọc cộng một lần ghi có điều kiện).

## CORS {#cors}

CORS được cấu hình động, và **giới hạn theo đường dẫn**: mô tả một dòng trước đây ("các origin từ mọi
client đã đăng ký đều tự động được cho phép") mô tả nhiều hơn đáng kể so với những gì provider thực hiện.

- **Các origin do client đăng ký** (`AllowedCorsOrigins` trên một client) chỉ được chấp nhận dưới `/connect/` và
  `/.well-known/`. Chúng **không** mở `/api/auth/`, `/api/v1/` hay `/scim/`. Một client bị tắt không đóng góp
  gì, và một origin sai định dạng bị loại bỏ.
- **Credential không bao giờ được cho phép** dưới `/api/auth/`, `/api/v1/`, `/scim/`, `/consent` hay `/approvals`, với
  bất kỳ origin nào, dù do người vận hành cấu hình hay do client đăng ký. Một client trình duyệt gọi các đường đó với
  `credentials: 'include'` từ một origin khác sẽ thất bại bất kể cấu hình; hãy dùng một
  backend-for-frontend (xem gói `@authagonal/bff`) thay vì các lời gọi khác origin có kèm credential.
- Các chính sách đã phân giải được lưu đệm trong 60 phút.

Vì vậy một origin được thêm vào `AllowedCorsOrigins` của một client khiến `/connect/*` hoạt động và không khiến `/api/v1/*`
hoạt động. Đó là có chủ đích: các đường đó mang cookie phiên và bề mặt quản trị.

## HashiCorp Vault Transit {#hashicorp-vault-transit}

`VaultTransitClient` giao tiếp với secrets engine Transit của Vault: ký, xác minh, mã hóa, giải mã và HMAC có khóa.
Nó là khối xây dựng cho một `IFieldCipher` hoặc `IIndexTokenizer` dựa trên Vault, mà bạn tự đăng ký.

**Việc ký JWT không được ủy thác cho Vault.** `ProtocolKeyManager` luôn ký bằng khóa trong
`ISigningKeyStore`, và không có điểm nối nào thay khóa đó bằng một khóa Vault. Xem
[Khả năng mở rộng](extensibility) để biết điều đó sẽ đòi hỏi những gì.

Thành phần này được cấu hình bằng code khi host dưới dạng thư viện.

## Ví dụ đầy đủ {#full-example}

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
