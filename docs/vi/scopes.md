---
layout: default
title: OAuth Scope
locale: vi
---

# OAuth Scope

Authagonal hỗ trợ cả scope OAuth/OIDC **tích hợp sẵn** lẫn scope **tùy chỉnh** được quản lý lúc chạy. Scope tùy chỉnh được lưu bền vững, quảng bá qua tài liệu discovery, và hiển thị trên màn hình chấp thuận cùng với các scope tích hợp sẵn.

## Scope tích hợp sẵn {#built-in-scopes}

Các scope này luôn có sẵn và không cần đăng ký:

| Scope | Mục đích |
|---|---|
| `openid` | Bắt buộc để khởi tạo một flow OIDC. Cấp một ID token. |
| `profile` | Các claim hồ sơ tiêu chuẩn (name, family_name, given_name, v.v.) |
| `email` | Các claim địa chỉ email và `email_verified` |
| `phone` | Các claim `phone_number` và `phone_number_verified` (OIDC Core 5.4) |
| `roles` | Claim `roles`. Không phải scope tiêu chuẩn của OIDC: tư cách thành viên role là một claim mà người dùng cuối đồng ý tiết lộ |
| `groups` | Claim `groups` (tư cách thành viên nhóm SCIM). Không phải scope tiêu chuẩn của OIDC, được kiểm soát giống `roles` |
| `offline_access` | Cấp một refresh token cùng với access token |

Một client chỉ có thể yêu cầu các scope được liệt kê trong `AllowedScopes` của chính nó. `/connect/authorize` từ chối scope không có trong danh sách đó với `invalid_scope` thay vì lọc bỏ nó, nên thêm `roles` vào request của một ứng dụng mà không thêm nó vào client sẽ làm hỏng mọi lần đăng nhập.

## Scope tùy chỉnh {#custom-scopes}

Scope tùy chỉnh được quản lý qua admin API tại `/api/v1/scopes`. Chúng yêu cầu một JWT access token có scope `authagonal-admin` (cấu hình được qua `AdminApi:Scope`).

### Mô hình scope {#scope-model}

```csharp
public sealed class Scope
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }
    public string? Group { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> AllowedRoles { get; set; } = [];
    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

| Trường | Mô tả |
|---|---|
| `Name` | Định danh scope được gửi trong token request (ví dụ `billing.read`) |
| `DisplayName` | Tên dễ đọc hiển thị trên màn hình chấp thuận |
| `Description` | Mô tả dài hơn hiển thị trên màn hình chấp thuận |
| `Emphasize` | Nếu là `true`, màn hình chấp thuận làm nổi bật scope này như một scope nhạy cảm |
| `Group` | Tiêu đề trên màn hình chấp thuận để xếp scope này vào. Chỉ phục vụ trình bày: nó không bao giờ ảnh hưởng tới những gì được cấp |
| `Required` | Nếu là `true`, người dùng không thể bỏ chọn scope này khi chấp thuận |
| `ShowInDiscoveryDocument` | Nếu là `true`, scope xuất hiện trong `/.well-known/openid-configuration` dưới `scopes_supported` |
| `AllowedRoles` | Các role mà người dùng phải có để được cấp scope này. Để trống (mặc định) thì scope không bị giới hạn, xem [Scope giới hạn theo role](#role-gated-scopes) |
| `UserClaims` | Danh sách cho phép gồm tên các claim thuộc tính tùy chỉnh được phát hành lên token khi scope này được cấp. Các claim giao thức được dành riêng (như `org_id`) không bao giờ được phát hành theo cách này, nên một thuộc tính đã lưu không thể giả mạo chúng |

### Scope giới hạn theo role {#role-gated-scopes}

`AllowedScopes` của client trả lời câu hỏi *ứng dụng này có được yêu cầu scope này không*, một câu hỏi
được giải quyết trước khi có ai đăng nhập. `AllowedRoles` trả lời nửa còn lại: *người này có được nhận nó không*. Cả hai
lớp kiểm soát đều áp dụng, và không lớp nào thay thế được lớp kia.

```json
{
  "name": "staff-admin",
  "displayName": "Staff administration",
  "allowedRoles": ["staff", "super-admin"]
}
```

Người dùng không có role nào trong danh sách sẽ bị **loại scope khỏi grant**, chứ không bị từ chối: client
đã yêu cầu đầy đủ tập scope của nó và được thông báo, qua `scope` được phản hồi lại trong token response (RFC 6749
§3.3), rằng nó nhận được ít hơn. Đây là điều cho phép một ứng dụng phục vụ cả nhân viên lẫn mọi người khác: bề mặt
dành cho nhân viên là một scope trong số nhiều scope, và chỉ những người có quyền mới nhận được nó.

Một request mà *mọi* scope được yêu cầu đều bị loại sẽ thất bại với `access_denied`, vì không còn gì
để cấp token.

Lớp kiểm soát áp dụng ở mọi nơi token được mint cho một con người:

| Flow | Nơi chạy |
|---|---|
| Authorization code | Tại `/connect/authorize`, khi đã biết người dùng và **trước** bước chấp thuận, để màn hình không bao giờ đề xuất một quyền không thể cấp |
| Device code | Tại `/api/auth/device/approve`, điểm đầu tiên trong flow đó mà chủ thể được biết |
| Refresh | Ở mỗi lần xoay vòng, đối chiếu với các role vừa được phân giải lại. Đây là nơi việc thu hồi một role thực sự có hiệu lực, vì grant vẫn ghi lại những gì đã được phê duyệt lúc đăng nhập |
| Token exchange | Không được kiểm soát riêng: một lần exchange chỉ có thể thu hẹp scope trong phạm vi các scope của chính subject token, nên nó không bao giờ có thể với tới một scope mà chủ thể chưa được cấp |

Grant client-credentials không có chủ thể và cố ý không bị ảnh hưởng: thẩm quyền của một machine client
là bản đăng ký của nó.

Việc khởi tạo một scope từ cấu hình có thể thêm hoặc thay đổi `AllowedRoles` nhưng không thể xóa trống nó (như với
`UserClaims`, một trường bị bỏ trống sẽ giữ nguyên giá trị đã lưu). Để gỡ bỏ lớp kiểm soát, hãy `PUT` scope với
một mảng rỗng tường minh.

## Khởi tạo từ cấu hình {#seeding-from-configuration}

Scope có thể được khai báo trong mục cấu hình `Scopes`. Chúng được ghi vào scope store lúc khởi động, cùng với [việc khởi tạo client](configuration#clients).

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

| Trường | Mô tả |
|---|---|
| `Name` | Bắt buộc. Một mục không có tên sẽ bị bỏ qua kèm cảnh báo |
| `DisplayName`, `Description`, `UserClaims`, `ShowInDiscoveryDocument`, `Emphasize`, `Group`, `Required`, `AllowedRoles` | Như trong [mô hình scope](#scope-model) |

Việc nạp sẵn là upsert theo `Name`. Một trường bạn đặt sẽ được ưu tiên hơn giá trị đã lưu ở mỗi lần khởi động, nên một chỉnh sửa thực hiện qua admin API trên một trường cũng được nạp sẵn sẽ bị ghi đè ở lần khởi động kế tiếp. Một trường bạn bỏ trống sẽ giữ giá trị đã lưu (hoặc giá trị mặc định của mô hình với scope mới). Vì bỏ trống nghĩa là "giữ nguyên", cấu hình có thể thêm hoặc thay đổi `UserClaims` và `AllowedRoles` nhưng không thể làm rỗng chúng: hãy làm việc đó bằng `PUT /api/v1/scopes/{name}` với một mảng rỗng tường minh.

## Endpoint quản trị {#admin-endpoints}

### Liệt kê scope {#list-scopes}

```
GET /api/v1/scopes
```

Trả về `{ "scopes": [ ... ] }`.

### Lấy scope {#get-scope}

```
GET /api/v1/scopes/{name}
```

Trả về scope hoặc `404` nếu không tìm thấy.

### Tạo scope {#create-scope}

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing (read-only)",
  "description": "View invoices and payment history",
  "emphasize": false,
  "required": false,
  "showInDiscoveryDocument": true,
  "userClaims": ["billing_plan"]
}
```

Trả về `201 Created` kèm scope. Trả về `400` (`invalid_request`) nếu thiếu `name` hoặc nó chứa khoảng trắng, và `409` (`scope_exists`) nếu đã tồn tại một scope cùng tên.

### Cập nhật scope {#update-scope}

```
PUT /api/v1/scopes/{name}
Content-Type: application/json

{
  "displayName": "Billing (read)",
  "description": "View invoices",
  "emphasize": true
}
```

Chỉ các trường được cung cấp mới được cập nhật; các trường bị bỏ trống giữ nguyên giá trị hiện tại.

### Xóa scope {#delete-scope}

```
DELETE /api/v1/scopes/{name}
```

Trả về `204 No Content` (`404` nếu scope không tồn tại). Các token đã được cấp có chứa scope này vẫn còn hiệu lực cho tới khi hết hạn; hãy thu hồi chúng một cách tường minh qua `/connect/revocation` nếu cần.

## Tài liệu discovery {#discovery-document}

Scope có `ShowInDiscoveryDocument = true` xuất hiện dưới `scopes_supported` trong `/.well-known/openid-configuration`. Bảy scope tích hợp sẵn luôn được quảng bá.

```json
{
  "scopes_supported": ["openid", "profile", "email", "phone", "roles", "groups", "offline_access", "billing.read"]
}
```

## Màn hình chấp thuận {#consent-screen}

Khi một client yêu cầu một scope không nằm trong danh sách bỏ qua chấp thuận của nó, trang chấp thuận liệt kê từng scope được yêu cầu theo `DisplayName` (dự phòng bằng `Name`) với `Description` bên dưới. Scope có `Emphasize = true` được hiển thị với kiểu trình bày riêng biệt. Scope `Required` không thể bị bỏ chọn.

Xem [Màn hình chấp thuận OAuth](index#key-features) để biết flow phía người dùng.

## Đăng ký client động {#dynamic-client-registration}

Client được đăng ký qua [Đăng ký client động](client-registration) chỉ có thể khai báo các scope OIDC tích hợp sẵn (`openid`, `profile`, `email`, `phone`, `offline_access`) cùng với bất kỳ scope nào được nêu trong `Auth:DynamicClientRegistrationScopes`. Việc một scope chỉ đơn giản tồn tại trong store không phải là sự cho phép để một client tự đăng ký khai báo nó, và các scope giới hạn theo role (những scope có `AllowedRoles`) không bao giờ đăng ký được. Mọi thứ khác bị từ chối với `invalid_scope`.
