---
layout: default
title: Cấp phát SCIM 2.0
nav_order: 13
locale: vi
---

# Cấp phát SCIM 2.0

Authagonal hỗ trợ SCIM 2.0 (System for Cross-domain Identity Management) để tự động cấp phát người dùng từ các identity provider doanh nghiệp như Microsoft Entra ID, Okta và OneLogin.

## Tổng quan {#overview}

SCIM là một giao thức cấp phát chiều vào: identity provider của bạn đẩy các thay đổi về người dùng và nhóm tới Authagonal. Nó bổ sung cho cơ chế cấp phát chiều ra TCC (Try-Confirm-Cancel) hiện có, vốn đẩy người dùng tới các ứng dụng downstream.

**Các thao tác được hỗ trợ:**
- CRUD người dùng (tạo, đọc, cập nhật, xóa bằng cách vô hiệu hóa mềm)
- CRUD nhóm kèm quản lý thành viên
- Lọc (toán tử `eq` và `co` trên `userName`, `externalId`, `displayName`)
- Phân trang: dựa trên con trỏ (`cursor`/`nextCursor`) cho cả người dùng lẫn nhóm; `startIndex` vẫn được chấp nhận trên nhóm cho các client hiện có nhưng không được công bố
- PATCH để cập nhật một phần (bao gồm vô hiệu hóa bằng `active=false`)
- Ánh xạ nhóm sang vai trò, được phân giải tại thời điểm phát hành token

**Không hỗ trợ:** thao tác hàng loạt, sắp xếp, ETag, quản lý mật khẩu qua SCIM.

Mọi tài nguyên đều thuộc phạm vi của SCIM client đã cấp phát chúng: một người dùng hoặc nhóm được tạo bởi client của một SCIM token thì vô hình (404) đối với mọi SCIM client khác.

## Tạo SCIM token {#generating-a-scim-token}

Các endpoint SCIM được xác thực bằng Bearer token tĩnh. Tạo token qua Admin API:

```http
POST /api/v1/scim/tokens
Authorization: Bearer {admin-token}
Content-Type: application/json

{
  "clientId": "your-client-id",
  "description": "Entra ID SCIM token",
  "expiresInDays": 365,
  "organizationId": "org_acme",
  "allowedEmailDomains": ["acme.example", "acme-eu.example"]
}
```

Phản hồi chứa token thô **một lần duy nhất**. Token được lưu dưới dạng hash SHA-256 và không thể khôi phục sau này, nên hãy lưu giữ nó an toàn:

```json
{
  "tokenId": "abc123",
  "clientId": "your-client-id",
  "token": "base64-encoded-token",
  "description": "Entra ID SCIM token",
  "createdAt": "2024-01-01T00:00:00Z",
  "expiresAt": "2025-01-01T00:00:00Z",
  "organizationId": "org_acme",
  "allowedEmailDomains": ["acme.example", "acme-eu.example"]
}
```

Bỏ `expiresInDays` (hoặc truyền `0`) để có token không hết hạn.

### Gắn tổ chức cho người dùng của một connector {#tagging-a-connectors-users-with-an-organization}

`organizationId` là tùy chọn. Khi được đặt, mọi người dùng được cấp phát qua token đó đều được ghi với
`OrganizationId` đó, giá trị này được phát ra dưới dạng claim `org_id` trên token của họ. SCIM không có cách nào để connector
cho biết nó đang đồng bộ cho khách hàng nào của bạn: SCIM lõi không định nghĩa thuộc tính tổ chức nào, và
enterprise extension chưa được hiện thực (xem *Hỗ trợ schema* bên dưới). Gắn giá trị này vào thông tin xác thực
giải quyết vấn đề mà không cần mỗi khách hàng một OAuth client.

Nếu bỏ trống, người dùng sẽ không được gắn tổ chức, đó cũng là cách mọi token hoạt động trước khi có tính năng này. Không có gì
được suy ra từ client id.

Hai quy tắc:

- **Chỉ khi tạo.** Một lượt đồng bộ sau đó qua một token được gắn tổ chức khác sẽ không gắn lại tổ chức cho tài khoản đang tồn tại.
- **Nó đi trước bước cấp phát.** Phản hồi `/try` của TCC chỉ điền tổ chức khi trường đó vẫn còn trống
  (xem [Cấp phát](provisioning.md)), nên ràng buộc tường minh trên thông tin xác thực sẽ được ưu tiên, và payload `/try`
  mang giá trị đã ràng buộc để ứng dụng downstream có thể biết lượt đồng bộ đến từ khách hàng nào.

Khi `organizationId` chỉ tới một [tổ chức](organizations) đang tồn tại, thao tác tạo cũng ghi một bản ghi thành viên `active` của tổ chức đó (không có vai trò) và ghi kiểm toán `scim.organization_member_added`, để người dùng sau đó không bị cổng kiểm tra thành viên của tổ chức từ chối cấp token. Một id không chỉ tới tổ chức nào sẽ chỉ là một nhãn `org_id` trần, giống những gì token được tạo trước khi có tổ chức làm. Giống như nhãn, bản ghi thành viên chỉ được ghi khi tạo.

> **Gắn tổ chức không phải là cô lập.** Quyền sở hữu được áp dụng theo từng **client**, không theo từng token. Hai token được cấp
> cho cùng một client là một danh tính với hai secret, và mỗi token đều có thể đọc, đổi tên, vô hiệu hóa và
> xóa những gì token kia đã tạo. Điều đó không sao khi một bên nắm giữ tất cả. Nếu các connector không tin cậy
> lẫn nhau, mỗi bên giữ token riêng, hãy cấp cho mỗi connector một client riêng.

### Giới hạn những danh tính mà một connector được phép tạo {#bounding-which-identities-a-connector-may-create}

`allowedEmailDomains` là cơ chế kiểm soát duy nhất đối với việc thông tin xác thực SCIM có thể cấp phát **những** người dùng nào. Hãy đặt nó.

Bỏ trống sẽ tạo ra một token không bị giới hạn, và không giới hạn còn rộng hơn bạn tưởng. Người dùng được tạo qua SCIM
được ghi với `EmailConfirmed = true` (địa chỉ được coi là đã chứng minh kể từ thời điểm đó), nên một
connector không bị giới hạn có thể tạo `ceo@some-other-company.example` như một tài khoản đã được xác minh sẵn. Khi chủ sở hữu
thật sau này đăng nhập qua liên kết, một bản ghi chưa có external login nào sẽ được nhận làm của họ thay vì
bị từ chối, nên lần đăng nhập của họ gắn vào tài khoản đó; và vì `ScimProvisionedByClientId` vẫn ghi tên
connector đã tạo ra nó, connector đó giữ toàn quyền sở hữu đối tượng: nó có thể đọc hồ sơ, đổi
`userName`, vô hiệu hóa tài khoản (việc này thu hồi mọi grant), hoặc xóa nó, việc này xóa sạch passkey và
tư cách thành viên nhóm của người dùng, đồng thời đánh dấu tombstone cho bản ghi khiến connector hợp lệ của tên miền đó nhận 404 ở mọi
thao tác.

Một token bỏ trống trường này sẽ ghi một cảnh báo lúc tạo, kèm id của token.

Hãy cung cấp tên miền trần (`acme.example`, không phải `@acme.example`, và không phải một địa chỉ). Giá trị không bao giờ có thể khớp sẽ
bị từ chối thay vì được lưu, vì một giới hạn không cho phép gì trông giống hệt một connector bị cấu hình sai.

Người vận hành cũng có thể đặt giới hạn trong cấu hình:

```json
{
  "Scim": {
    "Clients": {
      "your-client-id": { "AllowedEmailDomains": ["acme.example"] }
    }
  }
}
```

Hai nguồn được **lấy giao**, và danh sách rỗng từ bất kỳ nguồn nào có nghĩa là "nguồn này không đặt giới hạn". Vì vậy cả hai
cùng rỗng là không giới hạn; chỉ một trong hai có giá trị thì tự nó được áp dụng; còn khi cả hai cùng được đặt, chỉ những tên miền có mặt trong cả hai mới
được phép: việc tạo token có thể thu hẹp giới hạn mà người vận hành đã cấu hình nhưng không bao giờ mở rộng được nó.

Được áp dụng như nhau trên tạo, `PUT` và `PATCH`, nên việc đổi tên không thể chuyển một tài khoản sang tên miền mà thông tin xác thực
không được phép cấp phát.

### Liệt kê token {#listing-tokens}

```http
GET /api/v1/scim/tokens?clientId=your-client-id
Authorization: Bearer {admin-token}
```

### Thu hồi token {#revoking-a-token}

```http
DELETE /api/v1/scim/tokens/{tokenId}?clientId=your-client-id
Authorization: Bearer {admin-token}
```

## Cấu hình identity provider {#configuring-your-identity-provider}

### Tenant URL {#tenant-url}

```
https://your-authagonal-instance/scim/v2
```

### Xác thực {#authentication}

Dùng **OAuth Bearer Token** với token đã tạo ở trên.

### Microsoft Entra ID {#microsoft-entra-id}

1. Trong Azure portal, vào **Enterprise Applications** > ứng dụng của bạn > **Provisioning**
2. Đặt Provisioning Mode thành **Automatic**
3. Nhập Tenant URL: `https://your-instance/scim/v2`
4. Nhập Secret Token: token thô từ bước tạo token
5. Nhấn **Test Connection** để kiểm tra
6. Cấu hình ánh xạ thuộc tính (xem bên dưới)

### Okta {#okta}

1. Trong Okta admin console, vào **Applications** > ứng dụng của bạn > **Provisioning**
2. Bật **SCIM connector**
3. Đặt Base URL: `https://your-instance/scim/v2`
4. Đặt Authentication Mode: **HTTP Header**
5. Nhập Bearer token

### OneLogin {#onelogin}

1. Trong trang quản trị OneLogin, vào **Applications** > ứng dụng của bạn > **Provisioning**
2. Bật cấp phát
3. Đặt SCIM Base URL: `https://your-instance/scim/v2`
4. Đặt SCIM Bearer Token

## Các endpoint SCIM {#scim-endpoints}

| Phương thức | Đường dẫn | Mô tả |
|--------|------|-------------|
| GET | `/scim/v2/Users` | Liệt kê/lọc người dùng |
| GET | `/scim/v2/Users/{id}` | Lấy một người dùng |
| POST | `/scim/v2/Users` | Tạo người dùng |
| PUT | `/scim/v2/Users/{id}` | Thay thế người dùng |
| PATCH | `/scim/v2/Users/{id}` | Cập nhật một phần |
| DELETE | `/scim/v2/Users/{id}` | Tombstone (vô hiệu hóa; GET sau đó trả 404) |
| GET | `/scim/v2/Groups` | Liệt kê/lọc nhóm |
| GET | `/scim/v2/Groups/{id}` | Lấy một nhóm |
| POST | `/scim/v2/Groups` | Tạo nhóm |
| PUT | `/scim/v2/Groups/{id}` | Thay thế nhóm |
| PATCH | `/scim/v2/Groups/{id}` | Thêm/xóa thành viên |
| DELETE | `/scim/v2/Groups/{id}` | Xóa nhóm |
| GET | `/scim/v2/ServiceProviderConfig` | Khả năng hỗ trợ |
| GET | `/scim/v2/Schemas` | Định nghĩa schema |
| GET | `/scim/v2/ResourceTypes` | Loại tài nguyên |

Mọi endpoint cũng được ánh xạ không kèm đoạn `/v2` (ví dụ `/scim/Users`) cho các identity provider tự nối thêm đường dẫn của riêng chúng. Các endpoint discovery (`ServiceProviderConfig`, `Schemas`, `ResourceTypes`, cùng các base URL trần `/scim/` và `/scim/v2/`, vốn trả về ServiceProviderConfig) là ẩn danh; mọi endpoint còn lại đều yêu cầu SCIM Bearer token.

Các endpoint người dùng và nhóm bị giới hạn tốc độ ở 200 request mỗi phút cho mỗi SCIM client; request vượt mức nhận lỗi SCIM với status `429`.

## Ánh xạ thuộc tính {#attribute-mapping}

### Thuộc tính người dùng {#user-attributes}

| Thuộc tính SCIM | Trường Authagonal |
|---------------|------------------|
| `userName` | `Email` |
| `name.givenName` | `FirstName` |
| `name.familyName` | `LastName` |
| `displayName` | `FirstName LastName` |
| `emails[type eq "work"].value` | `Email` |
| `active` | `IsActive` |
| `externalId` | `ExternalId` |
| `preferredLanguage` (dự phòng bằng `locale`) | `Locale` |

### Thuộc tính nhóm {#group-attributes}

| Thuộc tính SCIM | Trường Authagonal |
|---------------|------------------|
| `displayName` | `DisplayName` |
| `externalId` | `ExternalId` |
| `members` | `MemberUserIds` |

### Hỗ trợ schema {#schema-support}

Chỉ `User` và `Group` của SCIM 2.0 lõi (RFC 7643). Các bảng ở trên là toàn bộ tập được hỗ trợ.

**Enterprise user extension chưa được hiện thực**, nên `employeeNumber`, `costCenter`, `organization`,
`division`, `department` và `manager` được chấp nhận và bỏ qua thay vì được lưu, như nhau trên tạo, thay thế và
PATCH. Entra và Okta ánh xạ một số thuộc tính này trong ánh xạ thuộc tính mặc định của chúng, nên một connector nguyên bản
không cần gỡ chúng ra. (Trước 0.27.0, một PATCH mang một trong các thuộc tính này bị từ chối TOÀN BỘ với
`400 invalidPath`, khiến mọi lượt đồng bộ tăng dần thất bại trong khi thao tác tạo vẫn thành công, và có thể khiến một
lệnh thu hồi cấp phát `active: false` bị kẹt lại vì một thuộc tính không liên quan.)

Sự nới lỏng này rất hẹp: một đường dẫn LÕI viết sai như `name.givenNam` vẫn trả `400`, và
các thuộc tính chỉ đọc (`id`, `meta`, `groups`) vẫn bị từ chối theo `mutability`.

Lưu ý rằng thuộc tính enterprise `organization` **không** trở thành `org_id` của người dùng. Giá trị đó được
khẳng định bởi chính identity provider của khách hàng, trong khi ràng buộc trên thông tin xác thực ở trên do
người vận hành đặt; hãy dùng `organizationId` trên token thay vào đó.

## Chi tiết hành vi {#behavior-details}

### Tạo người dùng {#user-creation}
- Người dùng được cấp phát qua SCIM được tạo với `EmailConfirmed = true` (chỉ SSO, không có mật khẩu).
- Trường `ScimProvisionedByClientId` ghi nhận SCIM client nào đã tạo người dùng.
- Nếu client có cấu hình `ProvisioningApps`, cấp phát TCC được kích hoạt tự động. Nếu bước cấp phát từ chối người dùng, thao tác tạo qua SCIM được hoàn tác và phản hồi là SCIM `400` với `scimType: invalidValue` cùng một thông điệp cố định (văn bản riêng của ứng dụng downstream cố ý không được trả lại cho SCIM client).
- Tạo một người dùng có `userName` hoặc `externalId` đã tồn tại sẽ trả xung đột SCIM `409`. Thay đổi email qua PUT hoặc PATCH cũng được kiểm tra xung đột theo cùng cách.

### Vô hiệu hóa người dùng {#user-deactivation}
- `DELETE /scim/v2/Users/{id}` **đánh dấu tombstone** cho tài nguyên: nó vô hiệu hóa người dùng, giữ lại bản ghi cục bộ, và ghi `ScimDeletedAt`. Một `GET /scim/v2/Users/{id}` sau đó trả **404**, đúng như RFC 7644 §3.6 yêu cầu ("the service provider MUST return a 404 for all operations associated with the previously deleted resource"). Đừng xác nhận việc thu hồi cấp phát bằng cách đọc lại tài nguyên và chờ `active: false`. Lượt đọc trả 404, và đó là thành công.
- Bản ghi được giữ lại thay vì xóa hẳn để một người được tuyển lại có thể được tạo lại: tombstone giải phóng `userName`/`externalId` mà tài nguyên mới cần, trong khi tài khoản cục bộ, lịch sử kiểm toán và tư cách thành viên nhóm của nó vẫn còn.
- `PATCH` với `active = false` cũng vô hiệu hóa người dùng.
- Người dùng bị vô hiệu hóa không thể đăng nhập bằng mật khẩu, SAML hay OIDC.
- Mọi grant (refresh token, phiên) đều bị thu hồi khi vô hiệu hóa.
- Việc thu hồi cấp phát ở các ứng dụng downstream chỉ được kích hoạt bởi `DELETE`; vô hiệu hóa bằng `PATCH` thu hồi grant nhưng không động tới các ứng dụng downstream.

### Lọc {#filtering}
Hỗ trợ đầy đủ ngữ pháp bộ lọc của RFC 7644 §3.4.2.2.

**Toán tử:** `eq`, `ne`, `co`, `sw`, `ew`, `gt`, `ge`, `lt`, `le`, và `pr` (tồn tại).
**Logic:** `and`, `or`, `not (...)`, có nhóm bằng dấu ngoặc. `and` có độ ưu tiên cao hơn `or`.
**Đường dẫn:** thuộc tính con (`name.givenName`), thuộc tính đa trị (`emails.value`), value path (`emails[type eq "work"].value`) và tên có tiền tố URN (`urn:ietf:params:scim:schemas:core:2.0:User:userName`).

```
userName eq "user@example.com"
userName sw "sales-" and active eq true
emails[type eq "work"].value co "@acme.com"
not (title pr)
meta.lastModified gt "2026-01-01T00:00:00Z"
```

Ngữ nghĩa tuân theo RFC: so sánh chuỗi không phân biệt hoa thường, một thuộc tính đa trị khớp khi bất kỳ phần tử nào khớp, và một thuộc tính vắng mặt khiến mọi phép so sánh đều sai trừ `ne`. Đầu vào không phải bộ lọc SCIM hợp lệ bị từ chối với `400` và `scimType: invalidFilter`, kèm tên vấn đề.

**Hiệu năng.** `userName eq` và `externalId eq` (các lượt tra cứu mà Entra và Okta gửi trước mỗi lần tạo hoặc cập nhật) được phân giải bằng tra cứu điểm có chỉ mục thay vì quét danh sách, nên chúng luôn nhanh bất kể số lượng người dùng. Mọi bộ lọc khác được đánh giá trong khi phân trang qua người dùng của client, có giới hạn: PII của người dùng được mã hóa khi lưu trữ và chỉ tìm kiếm được qua blind index, nên các điều kiện phong phú hơn không thể đẩy xuống tầng lưu trữ. Với phân trang bằng con trỏ, `totalResults` bị **bỏ qua** khi còn `nextCursor`, và là tổng chính xác khi `nextCursor` không còn. Xem phần Phân trang.

### Phân trang {#pagination}
Danh sách người dùng dùng **phân trang bằng con trỏ**. Mỗi trang của `GET /scim/v2/Users` trả về một thuộc tính `nextCursor` trong phản hồi danh sách; truyền lại nó dưới dạng `?cursor=` để lấy trang tiếp theo. Khi không còn `nextCursor`, danh sách đã đầy đủ. Kích thước trang được điều khiển bởi `count` (mặc định 100, tối đa 200).

Yêu cầu `startIndex` lớn hơn 1 trên endpoint Users sẽ trả lỗi `400` hướng bạn sang phân trang bằng con trỏ; không hỗ trợ phân trang theo offset quá trang đầu tiên. `totalResults` bị **bỏ hoàn toàn** khi còn `nextCursor`, và chỉ mang tổng chính xác ở trang cuối cùng. Nó cố ý không báo kích thước của trang được trả về: một client đồng bộ từng đọc `totalResults`, thấy nó bằng số tài nguyên vừa nhận, rồi kết luận rằng nó đã có toàn bộ thư mục, và thế là âm thầm đọc thiếu dữ liệu của tenant. Hãy điều khiển vòng lặp bằng `nextCursor`, không bao giờ bằng `totalResults`, và coi `totalResults` vắng mặt là "chưa biết", không phải bằng không.

**Danh sách nhóm cũng được phân trang bằng con trỏ.** `GET /scim/v2/Groups` trả về `nextCursor` ở cả dạng có lọc
lẫn không lọc; hãy theo nó theo cùng cách. `startIndex` vẫn được chấp nhận trên Groups cho các client đã dùng
nó, nhưng **không được công bố** trong `ServiceProviderConfig` và không nên dựa vào: `pagination.index` là một
khẳng định về provider, không phải về một collection, và `/Users` không hỗ trợ nó, nên giá trị duy nhất
đúng ở mọi nơi là `false`. Hãy dùng con trỏ, vốn hoạt động trên cả hai.

Danh sách nhóm có lọc quét theo các cửa sổ có giới hạn thay vì nạp toàn bộ tenant, nên nó có thể trả về
một trang rỗng trong khi phía sau vẫn còn kết quả khớp. Khi đó nó trả về `nextCursor` và **bỏ**
`totalResults`: một trang rỗng kèm con trỏ nghĩa là "tiếp tục", còn một trang rỗng không có con trỏ nghĩa là tập
đã lọc thực sự rỗng. Đừng coi trang rỗng đầu tiên là điểm kết thúc của collection.

`count=0` trả về `totalResults` mà không có tài nguyên nào (RFC 7644 §3.4.2.4) trên cả hai collection, và `count`
âm bị từ chối với `400` thay vì bị kẹp giá trị.

### Thành viên nhóm qua PATCH {#group-membership-via-patch}
`PATCH /scim/v2/Groups/{id}` chấp nhận các dạng thay đổi thành viên mà các identity provider lớn thực sự gửi:

- **Thêm thành viên:** `op: "add"` với `path: "members"` và một mảng value gồm các đối tượng `{ "value": "user-id" }`. Phần tử trùng lặp bị bỏ qua.
- **Thay thế thành viên:** `op: "replace"` với `path: "members"` thay thế toàn bộ danh sách thành viên bằng mảng được cung cấp.
- **Xóa một thành viên cụ thể (mảng value):** `op: "remove"` với `path: "members"` và một mảng value gồm id các thành viên cần xóa (dạng mà Entra ID gửi).
- **Xóa một thành viên cụ thể (bộ lọc trong path):** `op: "remove"` với `path: 'members[value eq "user-id"]'`, id nằm trong bộ lọc của path và không có value (dạng mà Okta gửi khi thu hồi cấp phát).
- **Xóa mọi thành viên:** `op: "remove"` với `path: "members"` và không có value sẽ làm trống nhóm.

### Ánh xạ nhóm sang vai trò {#group-to-role-mapping}
Tư cách thành viên trong một nhóm SCIM có thể cấp vai trò ứng dụng. Mỗi ánh xạ là một hàng cho mỗi cặp (nhóm, vai trò), và một nhóm có thể cấp nhiều vai trò. Chúng được phân giải tại **thời điểm phát hành token**: vai trò hiệu lực của người dùng là các vai trò được gán trực tiếp cộng với vai trò của mọi nhóm đã ánh xạ mà người đó thuộc về, nên việc thêm hoặc xóa thành viên nhóm có hiệu lực ở token kế tiếp mà không cần chạm vào bản ghi người dùng. Kho ánh xạ rỗng thì không có tác dụng gì.

Các ánh xạ được lưu bền vững qua `IScimGroupRoleMappingStore` (được hiện thực bởi storage provider Azure và AWS; nếu không thì một bản mặc định trong bộ nhớ được đăng ký) và được quản lý bởi giao diện quản trị của ứng dụng chủ, không phải qua chính SCIM API.

Tùy chọn thêm, một client bật `IncludeGroupsInTokens` cũng nhận tên hiển thị các nhóm SCIM của người dùng dưới dạng claim `groups` trong token được phát hành.

## Hạn chế đã biết {#known-limitations}

- **Không có thao tác hàng loạt:** người dùng và nhóm phải được cấp phát từng cái một.
- **Không sắp xếp:** danh sách người dùng trả về theo thứ tự lưu trữ khi phân trang bằng con trỏ; danh sách nhóm được sắp theo ngày tạo.
- **Không quản lý mật khẩu:** người dùng được cấp phát qua SCIM chỉ xác thực qua SSO.
- **Tombstone, không xóa hẳn:** `DELETE` vô hiệu hóa và đánh dấu tombstone cho tài nguyên (một `GET` sau đó trả 404, theo RFC 7644 §3.6) thay vì xóa vĩnh viễn bản ghi người dùng cục bộ. Để xóa hẳn, hãy dùng admin API.
