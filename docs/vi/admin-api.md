---
layout: default
title: Admin API
locale: vi
---

# Admin API

Các endpoint quản trị yêu cầu một JWT access token mang scope `authagonal-admin` (có thể cấu hình qua `AdminApi:Scope`).

Mọi endpoint đều nằm dưới `/api/v1/`.

## Khởi tạo admin token đầu tiên {#bootstrapping-the-first-admin-token}

Mọi endpoint `/api/v1/*` đều yêu cầu một bearer token mang scope quản trị, nhưng chính admin API (và [đăng ký client động](client-registration)) lại **từ chối tạo hoặc cập nhật bất kỳ client nào giữ scope đó** (`403 forbidden_scope`), nên một client được tạo lúc chạy không bao giờ có thể leo thang thành quản trị. Cách duy nhất để tạo admin token là một **client được nạp từ cấu hình**: các mục trong phần cấu hình `Clients:` được `ClientSeedService` upsert lúc khởi động, và cấu hình được tin cậy, cơ chế chặn scope bị cấm chỉ áp dụng cho các API lúc chạy.

Nạp một client `client_credentials` có scope quản trị trong `appsettings.json` (hoặc các biến môi trường / kho secret tương đương):

```json
{
  "Clients": [
    {
      "Id": "admin-cli",
      "Name": "Admin CLI",
      "ClientSecret": "a-long-random-secret",
      "GrantTypes": ["client_credentials"],
      "Scopes": ["authagonal-admin"]
    }
  ]
}
```

(`ClientSecret` được hash lúc khởi động; hãy cung cấp `SecretHashes` thay vào đó nếu bạn chỉ muốn giữ một giá trị đã hash sẵn trong cấu hình. `ClientId`/`ClientName`/`AllowedGrantTypes`/`AllowedScopes` được chấp nhận làm bí danh cho `Id`/`Name`/`GrantTypes`/`Scopes`.)

Sau đó đổi thông tin xác thực lấy token tại token endpoint tiêu chuẩn:

```bash
curl -X POST https://auth.example.com/connect/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=admin-cli" \
  -d "client_secret=a-long-random-secret" \
  -d "scope=authagonal-admin"
```

```json
{ "access_token": "eyJhbGci...", "token_type": "Bearer", "expires_in": 1800, "scope": "authagonal-admin" }
```

Grant `client_credentials` kiểm tra scope được yêu cầu với `AllowedScopes` của client; vì client được nạp sẵn giữ `authagonal-admin`, token được phát hành. Dùng nó dưới dạng `Authorization: Bearer {access_token}` trong mọi lời gọi quản trị:

```bash
curl https://auth.example.com/api/v1/clients -H "Authorization: Bearer eyJhbGci..."
```

Hãy giữ secret của client được nạp sẵn trong kho secret của bản triển khai; xoay vòng nó là một thay đổi cấu hình + khởi động lại.

## Người dùng {#users}

### Lấy người dùng {#get-user}

```
GET /api/v1/profile/{userId}
```

Trả về hồ sơ cùng những gì một bảng điều khiển hỗ trợ cần để chẩn đoán sự cố đăng nhập:
`emailConfirmed`, `isActive`, `lockoutEnd`, `accessFailedCount`, `roles`, các
`externalLogins` đã liên kết, và `hasPassword` (chỉ cho biết có hay không, không bao giờ trả hash). Trường cuối cùng đó là khác biệt
giữa "họ đã quên mật khẩu" và "họ chưa từng có mật khẩu, họ đăng nhập bằng SSO",
hai lời khuyên hoàn toàn trái ngược nhau.

Trả về thông tin chi tiết của người dùng, bao gồm các liên kết external login.

### Người dùng có tồn tại không {#user-exists}

```
GET /api/v1/profile/{userId}/exists
```

Trả `204` nếu người dùng tồn tại, ngược lại `404` (một phép thăm dò sự tồn tại rẻ, không có body).

### Đăng ký người dùng {#register-user}

```
POST /api/v1/profile/
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Tạo người dùng và gửi email xác minh. Trả `409 user_exists` nếu email đã được dùng.

Các trường tùy chọn chỉ dành cho admin: `userId` (id do bên gọi cung cấp, `409 user_id_in_use` khi trùng), `emailConfirmed` (tạo người dùng ở trạng thái đã xác minh, bỏ qua email xác minh), `companyName`, `organizationId`, `phone`, `locale`, và `customAttributes` (một map chuỗi được lưu trên người dùng và chuyển tiếp tới các đích cấp phát).

`skipProvisioning: true` tạo danh tính mà không chạy bước cấp phát. Nó dành cho một ứng dụng
của chính bạn mà BẢN THÂN nó là một đích cấp phát và đang dở dang việc thiết lập người dùng này: nó
gọi tới đây để tạo danh tính, không phải để bị gọi ngược lại về một người dùng mà nó đang tạo
dở. Không có cờ này, ứng dụng đó sẽ nhận lời gọi Try của chính nó cho một người dùng mới được dựng một nửa, chỉ mang những
thuộc tính còn sống sót qua vòng gọi, và nếu nó xử lý tiếp được thì cuối cùng sẽ cấp phát người dùng hai lần.

### Cập nhật người dùng {#update-user}

```
PUT /api/v1/profile/
Content-Type: application/json

{
  "userId": "user-id",
  "firstName": "Jane",
  "lastName": "Smith",
  "organizationId": "new-org-id"
}
```

`userId` là bắt buộc; mọi trường khác đều tùy chọn, chỉ các trường được cung cấp mới được cập nhật.

`isActive` vô hiệu hóa hoặc kích hoạt lại tài khoản. `emailConfirmed` (cũng chấp nhận dưới tên `emailVerified`)
đánh dấu địa chỉ là đã xác nhận mà không gửi thư xác minh, dùng khi quyền sở hữu địa chỉ đã được
xác lập bằng cách khác.

Thay đổi `organizationId`, hoặc vô hiệu hóa, sẽ kích hoạt:
- Xoay vòng SecurityStamp (vô hiệu hóa mọi phiên cookie trong vòng 30 phút)
- Thu hồi mọi refresh token

Một lệnh chặn chỉ có hiệu lực ở lần đăng nhập tiếp theo thì không phải là lệnh chặn, đó là lý do việc vô hiệu hóa thu hồi ngay
thay vì đợi hết hạn.

### Tìm kiếm người dùng {#search-users}

```
GET /api/v1/profile/search?q=jane&maxResults=20
```

Tìm kiếm theo tiền tố trên các chỉ mục email và tên. Trả về `{ "users": [ ... ] }`.

### Lấy người dùng theo email {#get-user-by-email}

```
GET /api/v1/profile/by-email?email=jane@example.com
```

Tra cứu chính xác, khác với tìm kiếm, vốn khớp theo tiền tố và có thể trả về nhiều người. Bên gọi
cần phân giải "địa chỉ này" thành "tài khoản này" muốn một câu trả lời hoặc không có gì. Trả `404` nếu không có người dùng như vậy.

### Liệt kê người dùng {#list-users}

```
GET /api/v1/profile?organizationId=&count=100&continuationToken=
```

Liệt kê thư mục theo phân trang bằng con trỏ; truyền lại `continuationToken` được trả về để lấy trang tiếp theo, và
dừng khi nó là null. Dùng con trỏ thay vì offset vì store phân trang theo token: offset sẽ
quét lại từ đầu ở mỗi trang.

### Những người dùng nào tồn tại {#which-users-exist}

```
POST /api/v1/profile/exists
Content-Type: application/json

{ "userIds": [ "a", "b", "c" ] }
```

Trả về tập con các id tồn tại, cùng `truncated: true` khi request vượt giới hạn 500 id, để
bên gọi được biết lô của mình đã bị cắt bớt thay vì âm thầm chỉ được trả lời về 500 trong số 600 id. Dùng để
đối chiếu một tập id với tập id của hệ thống khác.

### Trạng thái MFA của nhiều người dùng {#mfa-status-for-many-users}

```
POST /api/v1/profile/mfa-status
Content-Type: application/json

{ "userIds": [ "a", "b", "c" ] }
```

Trả về `{ "statuses": { "a": true, "b": false }, "truncated": false }`: `true` nghĩa là người dùng có ít nhất một thông tin xác thực MFA. Giới hạn 500 id; `truncated: true` cho biết request đã bị cắt bớt. Dùng cho huy hiệu "dùng MFA" trên giao diện thư mục.

### Đặt mật khẩu {#set-a-password}

```
POST /api/v1/profile/{userId}/set-password
Content-Type: application/json

{ "password": "N3w!Password" }
```

Đường hỗ trợ cho người bị khóa khỏi tài khoản mà địa chỉ email không còn tới được họ nữa. Tuân theo
chính sách mật khẩu. Thu hồi mọi refresh token và xoay vòng security stamp: một lần đổi mật khẩu
mà để các phiên cũ tiếp tục chạy thì chưa hề thay đổi ai có thể hành động với tư cách người đó.

### Mở khóa người dùng {#unlock-a-user}

```
POST /api/v1/profile/{userId}/unlock
```

Xóa trạng thái khóa và bộ đếm số lần thất bại, cho phép người đó vào lại ngay thay vì đợi tới khi
thời hạn khóa tình cờ hết.

### Xóa người dùng {#delete-user}

```
DELETE /api/v1/profile/{userId}
```

Xóa người dùng, thu hồi mọi grant, và thu hồi cấp phát khỏi mọi ứng dụng downstream (theo khả năng tốt nhất).

### Xác nhận email {#confirm-email}

```
POST /api/v1/profile/confirm-email?token={token}
```

### Gửi email xác minh {#send-verification-email}

```
POST /api/v1/profile/{userId}/send-verification-email
```

### Liên kết danh tính bên ngoài {#link-external-identity}

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### Hủy liên kết danh tính bên ngoài {#unlink-external-identity}

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

### Liệt kê tổ chức của một người dùng {#list-a-users-organizations}

```
GET /api/v1/profile/{userId}/organizations
```

Trả về `{ "items": [ { "organizationId", "slug", "name", "status", "roles" } ] }`: mọi tổ chức mà người dùng có tư cách thành viên, ở bất kỳ trạng thái nào. Một tư cách thành viên mà tổ chức không còn tồn tại sẽ bị bỏ qua.

### Mời người dùng {#invite-user}

```
POST /api/v1/profile/invite
Content-Type: application/json

{
  "email": "ada@acme.com",
  "firstName": "Ada",
  "lastName": "Lovelace",
  "locale": "en",
  "organizationId": "org_0123456789abcdef0123456789abcdef",
  "organizationRoles": ["Auditor"]
}
```

Chỉ `email` là bắt buộc. Tạo tài khoản (chưa xác nhận, chưa có mật khẩu dùng được) và gửi qua email một liên kết dùng một lần, có hiệu lực 7 ngày, tới `/accept-invite`. Với `organizationId` (id của tổ chức, không phải slug), lời mời là vào tổ chức đó: một tư cách thành viên `invited` được ghi với `organizationRoles`, chuyển sang `active` khi người được mời đặt mật khẩu. Email được soạn bằng `Accept-Language` đầu tiên trong yêu cầu của bạn mà thư viện có, nếu không thì bằng `locale`, và nếu không nữa thì bằng tiếng Anh. Xem [Tổ chức](organizations#inviting-people).

Trả về `200` với `{ "userId": "...", "email": "ada@acme.com" }` (địa chỉ viết thường). Lỗi là `{ "error", "error_description" }`:

| Trạng thái | `error` | Khi nào |
|---|---|---|
| 400 | `email_required` | thiếu `email` hoặc để trống |
| 400 | `invalid_email` | `email` không phải địa chỉ dùng được |
| 400 | `invalid_role` | một vai trò của tổ chức bắt đầu bằng `tenant:` hoặc `platform:` |
| 404 | `organization_not_found` | `organizationId` không trỏ tới tổ chức nào |
| 409 | `user_already_exists` | địa chỉ đã có tài khoản |

### Gửi lại lời mời {#resend-invite}

```
POST /api/v1/profile/{userId}/resend-invite
```

Tạo một liên kết mới và gửi đi. Trả về `200` với `{ "sent": true }`. `404 user_not_found` với người dùng không rõ, `400 already_accepted` sau khi địa chỉ đã được xác nhận (hãy dùng đặt lại mật khẩu), và `502 invite_email_failed` khi không thể gửi email.

## Quản lý MFA {#mfa-management}

### Lấy trạng thái MFA {#get-mfa-status}

```
GET /api/v1/profile/{userId}/mfa
```

Trả về trạng thái MFA và các phương thức đã đăng ký của một người dùng.

### Đặt lại toàn bộ MFA {#reset-all-mfa}

```
DELETE /api/v1/profile/{userId}/mfa
```

Gỡ mọi thông tin xác thực MFA và đặt `MfaEnabled=false`. Người dùng sẽ cần đăng ký lại nếu được yêu cầu.

### Gỡ một thông tin xác thực MFA cụ thể {#remove-specific-mfa-credential}

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

Gỡ một thông tin xác thực MFA cụ thể (ví dụ một ứng dụng xác thực bị mất). Nếu phương thức chính cuối cùng bị gỡ, MFA sẽ bị tắt.

## SSO Provider {#sso-providers}

### SAML Provider {#saml-providers}

```
POST   /api/v1/saml/connections                    # Create
GET    /api/v1/saml/connections/{connectionId}     # Get one
PUT    /api/v1/saml/connections/{connectionId}     # Update (partial: only supplied fields change)
DELETE /api/v1/saml/connections/{connectionId}     # Delete
```

Thao tác tạo yêu cầu `connectionName`, `entityId`, và **đúng một trong hai** `metadataLocation` (một URL metadata) hoặc `metadataXml` (metadata của IdP được dán vào, cho các IdP không có URL metadata, nó được kiểm tra cú pháp và thu gọn khi lưu). Tùy chọn: `nameIdFormat` (bỏ trống để dùng mặc định emailAddress, `"none"` để bỏ NameIDPolicy, khuyến nghị cho ADFS, hoặc một URN định dạng NameID), `signAuthnRequests`, `iconUrl`, `allowedDomains`, `disableJitProvisioning`, `organizationId`. Mỗi kết nối nhận một cặp khóa SP do máy chủ sinh ra; nó không bao giờ được API trả về. Xem [SAML](saml) để biết chi tiết.

`organizationId` giới hạn kết nối trong một [tổ chức](organizations): kết nối chỉ được đưa ra khi tổ chức đó được chọn, `allowedDomains` của nó chỉ được so khớp bên trong tổ chức đó (và *không* được ghi vào chỉ mục tên miền SSO toàn tenant), và mọi người đăng nhập qua kết nối đó đều trở thành thành viên của tổ chức. Bỏ trống hoặc `null` = một kết nối cấp tenant. Một tổ chức không tồn tại sẽ trả `400 unknown_organization`. Khi cập nhật, `null` (trường vắng mặt) giữ nguyên phạm vi, `""` đưa kết nối trở về cấp tenant, và cả hai chiều đều ghi lại chỉ mục tên miền tương ứng. Xem [Kết nối theo phạm vi tổ chức](self-service-sso#organisation-scoped-connections).

### OIDC Provider {#oidc-providers}

```
POST   /api/v1/oidc/connections                    # Create
GET    /api/v1/oidc/connections/{connectionId}     # Get one
DELETE /api/v1/oidc/connections/{connectionId}     # Delete
```

Thao tác tạo yêu cầu `connectionName`, `metadataLocation`, `clientId`, `clientSecret`, `redirectUrl`. Tùy chọn: `iconUrl`, `allowedDomains`, `passthroughParams`, `organizationId` (cùng ý nghĩa như trên kết nối SAML ở trên). Client secret được bảo vệ khi lưu trữ và không bao giờ được trả về. Xem [Liên kết OIDC](oidc-federation).

### Tên miền SSO {#sso-domains}

```
GET    /api/v1/sso/domains                 # List all
```

## Client {#clients}

Quản lý OAuth client lúc chạy. Mọi route đều yêu cầu policy `IdentityAdmin` (scope quản trị).

```
GET    /api/v1/clients              # List all clients
GET    /api/v1/clients/{clientId}   # Get one client
POST   /api/v1/clients              # Create a client
PUT    /api/v1/clients/{clientId}   # Update a client
DELETE /api/v1/clients/{clientId}   # Delete a client
```

### Tạo / cập nhật client {#create--update-client}

```
POST /api/v1/clients
Content-Type: application/json

{
  "clientId": "my-app",
  "clientName": "My Application",
  "allowedGrantTypes": ["authorization_code"],
  "redirectUris": ["https://app.example.com/callback"],
  "allowedScopes": ["openid", "profile", "email"]
}
```

`POST` trả `409` nếu client đã tồn tại. `PUT` cập nhật một client hiện có (`404` nếu không tìm thấy); khi cập nhật, chỉ các scope mới được thêm vào mới bị kiểm tra leo thang quyền.

Lưu ý:

- **Hash của secret không bao giờ được trả về.** `clientSecretHashes` bị loại khỏi mọi phản hồi (list, get, create, update). Khi cập nhật, bỏ trống `clientSecretHashes` sẽ giữ nguyên secret đã lưu; cung cấp hash mới sẽ xoay vòng nó.
- **Không thể cấp scope quản trị cho một client.** Yêu cầu `AdminApi:Scope` (mặc định `authagonal-admin`) trong `allowedScopes` trả `403 forbidden_scope`, không client nào được giữ scope quản trị, nếu không một client `client_credentials` có thể tạo admin token vô thời hạn.
- Thêm các scope mà bên gọi không được phép cấp sẽ trả `403`.

## Scope {#scopes}

Quản lý OAuth scope tùy chỉnh lúc chạy. Xem [OAuth Scope](scopes) để biết đầy đủ mô hình scope.

```
GET    /api/v1/scopes           # List all scopes
GET    /api/v1/scopes/{name}    # Get one scope
POST   /api/v1/scopes           # Create a scope
PUT    /api/v1/scopes/{name}    # Update a scope (only supplied fields change)
DELETE /api/v1/scopes/{name}    # Delete a scope
```

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing, read-only",
  "description": "View invoices and payment history",
  "userClaims": ["billing_plan"]
}
```

Trả `201` khi tạo (`409` nếu scope đã tồn tại), JSON của scope khi get/update, và `204` khi xóa.

## Ứng dụng cấp phát {#provisioning-apps}

Quản lý các đích cấp phát downstream lúc chạy. Mọi route đều yêu cầu policy `IdentityAdmin`.

```
GET    /api/v1/provisioning/apps               # List apps (also returns the configured limit)
POST   /api/v1/provisioning/apps               # Create an app
PUT    /api/v1/provisioning/apps/{appId}       # Update an app
DELETE /api/v1/provisioning/apps/{appId}       # Delete an app
POST   /api/v1/provisioning/apps/{appId}/test  # Send a test /try call to the app's callback
```

### Tạo / cập nhật ứng dụng cấp phát {#create--update-provisioning-app}

```
POST /api/v1/provisioning/apps
Content-Type: application/json

{
  "name": "Backend",
  "callbackUrl": "https://api.example.com/provisioning",
  "apiKey": "secret-api-key",
  "tryTimeoutSeconds": 30
}
```

- `name` và `callbackUrl` là bắt buộc; `callbackUrl` phải là một URL `http(s)` tuyệt đối.
- `tryTimeoutSeconds` bị kẹp trong khoảng 5–300.
- **API key không bao giờ được trả về.** Phản hồi chỉ ra `hasApiKey` (một giá trị boolean) thay vì chính khóa. Khi cập nhật, bỏ trống `apiKey` giữ nguyên nó, chuỗi rỗng xóa nó, và một giá trị sẽ thay thế nó.
- Việc tạo chịu một hạn mức có thể cấu hình cho mỗi bản triển khai (`IProvisioningAppQuota`); vượt hạn mức sẽ trả `400 provisioning_app_limit`. Phản hồi danh sách bao gồm `limit` hiện tại.

### Kiểm thử ứng dụng cấp phát {#test-a-provisioning-app}

```
POST /api/v1/provisioning/apps/{appId}/test
```

Gửi một `POST {callbackUrl}/try` giả lập với payload mẫu (kèm API key của ứng dụng dưới dạng bearer token nếu có đặt) và trả về `{ success, statusCode, body }` để bạn có thể kiểm tra kết nối từ giao diện quản trị.

## Vai trò {#roles}

### Liệt kê vai trò {#list-roles}

```
GET /api/v1/roles
```

### Lấy vai trò {#get-role}

```
GET /api/v1/roles/{roleId}
```

### Tạo vai trò {#create-role}

```
POST /api/v1/roles
Content-Type: application/json

{
  "name": "admin",
  "description": "Administrator role"
}
```

### Cập nhật vai trò {#update-role}

```
PUT /api/v1/roles/{roleId}
Content-Type: application/json

{
  "name": "admin",
  "description": "Updated description"
}
```

### Xóa vai trò {#delete-role}

```
DELETE /api/v1/roles/{roleId}
```

### Gán vai trò cho người dùng {#assign-role-to-user}

```
POST /api/v1/roles/assign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

Việc gán dựa trên **tên vai trò**, không phải id vai trò. Trả về danh sách vai trò đã cập nhật của người dùng.

### Bỏ gán vai trò khỏi người dùng {#unassign-role-from-user}

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

### Lấy vai trò của người dùng {#get-users-roles}

```
GET /api/v1/roles/user/{userId}
```

### Người dùng có một vai trò {#users-in-a-role}

```
GET /api/v1/roles/{roleName}/users?maxResults=200
```

Chiều ngược lại của endpoint trên (ai đang giữ vai trò này), được trả lời từ một chỉ mục thành viên vai trò thay vì
đọc từng người dùng. Trả về `{ "roleName": "...", "members": [ { "userId", "email", "firstName",
"lastName", "roles" } ] }`; mỗi thành viên mang toàn bộ tập vai trò của họ, vì một bảng điều khiển liệt kê một
vai trò hầu như luôn muốn hiển thị các thành viên của nó còn có những vai trò gì khác.

Trả `404 role_not_found` cho vai trò không tồn tại, thay vì một danh sách rỗng: "không ai giữ vai trò này"
và "bạn gõ sai tên vai trò" là hai vấn đề khác nhau. Trả `501 not_supported` nếu store được cấu hình
không đánh chỉ mục thành viên vai trò, cũng vì lý do đó: một danh sách thành viên rỗng sẽ bị hiểu thành
"không ai quản trị thứ này".

Các tài khoản được ghi trước khi có chỉ mục sẽ vô hình với nó cho tới khi được đánh chỉ mục lại
(`IUserStore.ReindexUserAsync`, phương thức này upsert tư cách thành viên của người dùng mà không xóa bất kỳ mục nào).

## Tổ chức {#organizations}

Tổ chức, thành viên và miền email của chúng (xem [Tổ chức](organizations) để biết ý nghĩa). Lỗi có dạng `{ "error": "<code>", "error_description": "..." }` với trạng thái `400` (đầu vào không hợp lệ), `404` (không tìm thấy) hoặc `409` (xung đột).

Danh sách được phân trang bằng `?cursor={cursor}&limit={limit}`: `limit` bị giới hạn trong 1..200 (mặc định 50) và phản hồi là `{ "items": [...], "nextCursor": "..." }`, với `nextCursor` là null ở trang cuối. Hãy gửi lại `nextCursor` nguyên vẹn; một cursor mà danh sách không phát ra là `400 invalid_cursor`.

Một tổ chức có dạng `{ "id", "slug", "name", "brandingJson", "metadata", "enabled", "requireMembershipForTokens", "allowAutoMembership", "createdAt", "updatedAt", "domains" }`.

### Liệt kê tổ chức {#list-organizations}

```
GET /api/v1/organizations?cursor={cursor}&limit={limit}
```

`200`, sắp xếp theo id. `400 invalid_cursor`.

### Tạo tổ chức {#create-organization}

```
POST /api/v1/organizations
Content-Type: application/json

{
  "slug": "acme",
  "name": "Acme Corp",
  "brandingJson": "{\"appName\":\"Acme\",\"primaryColor\":\"#1a56db\"}",
  "enabled": true,
  "requireMembershipForTokens": true,
  "allowAutoMembership": false,
  "metadata": { "plan": "enterprise" }
}
```

`slug` và `name` là bắt buộc; phần còn lại là tùy chọn và giữ giá trị mặc định của tổ chức nếu bỏ qua. `brandingJson` là một chuỗi chứa đối tượng JSON. Trả về `201` cùng header `Location` và tổ chức; `id` của nó là `org_` theo sau là 32 chữ số thập lục phân.

| Trạng thái | `error` | Khi nào |
|---|---|---|
| 400 | `slug_required` | thiếu `slug` hoặc để trống |
| 400 | `slug_invalid` | `slug` không phải 1 đến 64 chữ thường, chữ số và dấu gạch ngang ở giữa |
| 400 | `name_required` | thiếu `name` hoặc để trống |
| 400 | `invalid_branding_json` | `brandingJson` không phải đối tượng JSON |
| 409 | `slug_taken` | tổ chức khác đã có giá trị này làm slug hoặc id, kể cả khi tạo đồng thời |

### Lấy một tổ chức {#get-organization}

```
GET /api/v1/organizations/{idOrSlug}
```

`200`, tìm theo id trước rồi theo slug. `404 organization_not_found`.

### Cập nhật tổ chức {#update-organization}

```
PUT /api/v1/organizations/{id}
Content-Type: application/json

{ "name": "Acme Corporation", "enabled": true }
```

Mọi trường đều tùy chọn và chỉ thay đổi chính nó: `name`, `brandingJson`, `enabled`, `requireMembershipForTokens`, `allowAutoMembership` và `metadata` (được thay thế hoàn toàn; `{}` làm rỗng nó). `slug` chỉ được chấp nhận để từ chối khi khác giá trị hiện có. Trả về `200` cùng tổ chức. `404 organization_not_found`, `400 slug_immutable`, `400 invalid_branding_json`.

### Xóa tổ chức {#delete-organization}

```
DELETE /api/v1/organizations/{id}
```

Xóa mọi tư cách thành viên rồi xóa tổ chức. `204`; `404 organization_not_found`.

### Liệt kê thành viên {#list-members}

```
GET /api/v1/organizations/{id}/members?cursor={cursor}&limit={limit}
```

Một trang, sắp xếp theo id người dùng, gồm `{ "organizationId", "userId", "email", "name", "status", "roles", "createdAt", "updatedAt" }`; `email` và `name` lấy từ bản ghi người dùng và là null khi bản ghi không còn. `404 organization_not_found`, `400 invalid_cursor`.

### Thêm thành viên {#add-member}

```
POST /api/v1/organizations/{id}/members
Content-Type: application/json

{ "userId": "user-id", "status": "active", "roles": ["Auditor"] }
```

Chỉ định một người dùng đã có bằng `userId` (được kiểm tra trước) hoặc `email`. `status` là `invited`, `active` (mặc định) hoặc `suspended`; `roles` là tùy chọn. Trả về `201` cùng header `Location` và tư cách thành viên.

| Trạng thái | `error` | Khi nào |
|---|---|---|
| 404 | `organization_not_found` | không có tổ chức đó |
| 404 | `user_not_found` | cả `userId` lẫn `email` đều không trỏ tới người dùng nào |
| 409 | `membership_exists` | người dùng đã là thành viên |
| 400 | `invalid_role` | một vai trò bắt đầu bằng `tenant:` hoặc `platform:` |
| 400 | `invalid_status` | `status` không phải một trong ba giá trị |

### Cập nhật thành viên {#update-member}

```
PUT /api/v1/organizations/{id}/members/{userId}
Content-Type: application/json

{ "status": "suspended", "roles": [] }
```

Cả hai trường đều tùy chọn. Trả về `200` cùng tư cách thành viên. `404 membership_not_found`, `400 invalid_status`, `400 invalid_role`.

### Gỡ thành viên {#remove-member}

```
DELETE /api/v1/organizations/{id}/members/{userId}
```

`204`; `404 membership_not_found`.

### Thêm miền {#add-domain}

```
POST /api/v1/organizations/{id}/domains
Content-Type: application/json

{ "domain": "acme.com" }
```

Đăng ký miền, chưa xác minh. Miền được chuyển thành chữ thường và cắt khoảng trắng trước, và phải là một tên đơn giản như `acme.com`. Trả về `201` cùng header `Location` và `{ "domain", "verified", "verifiedAt", "createdAt", "recordName", "recordValue" }`; hãy công bố một bản ghi TXT tại `recordName` với giá trị `recordValue` (xem [Xác minh miền](organizations#domain-verification)).

| Trạng thái | `error` | Khi nào |
|---|---|---|
| 404 | `organization_not_found` | không có tổ chức đó |
| 400 | `domain_invalid` | không phải tên miền đơn giản |
| 409 | `domain_exists` | tổ chức này đã đăng ký nó |
| 409 | `domain_taken` | tổ chức khác đã đăng ký, dù đã xác minh hay chưa |

### Xác minh miền {#verify-domain}

```
POST /api/v1/organizations/{id}/domains/{domain}/verify
```

Truy vấn bản ghi TXT. Trả về `200` cùng miền (`verified: true`), kể cả khi nó đã được xác minh. `404 organization_not_found`, `404 domain_not_found`, `409 domain_taken`, và `409 verification_failed` khi bản ghi thiếu hoặc sai; `error_description` của nó nêu tên và giá trị cần công bố cùng những gì đã tìm thấy.

### Gỡ miền {#remove-domain}

```
DELETE /api/v1/organizations/{id}/domains/{domain}
```

`204`. Thành viên không bị đụng tới. `404 organization_not_found`, `404 domain_not_found`.

### Tạo tổ chức bằng backfill {#backfill-organizations}

```
POST /api/v1/organizations/backfill
Content-Type: application/json

{ "dryRun": true }
```

Tạo tổ chức và tư cách thành viên từ `OrganizationId` cũ của người dùng. `dryRun` mặc định là true, có hay không có thân, và khi đó không ghi gì. Trả về `200` với `{ "dryRun", "organizationsCreated", "membershipsCreated", "usersSkipped", "organizations": [ { "legacyValue", "slug", "users" } ], "truncated" }`. Xem [Backfill](organizations#backfill).

## SCIM token {#scim-tokens}

### Tạo token {#generate-token}

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id",
  "description": "Entra provisioning",
  "expiresInDays": 365
}
```

`description` và `expiresInDays` là tùy chọn (bỏ `expiresInDays` để có token không hết hạn). Trả về token thô một lần duy nhất. Hãy lưu giữ nó an toàn, không thể lấy lại token này.

`organizationId` là tùy chọn và gắn nhãn cho mọi người dùng mà token tạo ra (xem [Tổ chức](organizations#membership-from-a-scim-token)). Nó phải trỏ tới một tổ chức đã có: nếu không kết quả là `400 unknown_organization`, và giá trị sai định dạng cho `400 invalid_request`.

### Liệt kê token {#list-tokens}

```
GET /api/v1/scim/tokens?clientId=client-id
```

Trả về metadata của token (ID, ngày tạo) mà không kèm giá trị token thô.

### Thu hồi token {#revoke-token}

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## Token {#tokens}

### Mạo danh người dùng {#impersonate-user}

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid%20profile
```

Phát hành token (access, refresh, và id token khi có yêu cầu `openid`) thay mặt một người dùng mà không cần thông tin xác thực của họ. Hữu ích cho kiểm thử và hỗ trợ. Tham số được truyền dưới dạng query string.

| Tham số truy vấn | Bắt buộc | Mô tả |
|---|---|---|
| `clientId` | Có | Client mà token được phát hành cho. Thời gian sống của token lấy từ cấu hình của client này. |
| `userId` | Có | Người dùng cần mạo danh. |
| `scopes` | Không | Danh sách scope **phân cách bằng dấu cách** (mã hóa URL cho dấu cách). Mặc định là `AllowedScopes` của client khi bỏ trống. |

Hạn chế:

- Scope bị giới hạn trong `AllowedScopes` của client, yêu cầu bất kỳ scope nào mà chính client không thể yêu cầu sẽ trả `400 invalid_scope`.
- Scope quản trị (`AdminApi:Scope`, mặc định `authagonal-admin`) **không thể** được phát hành qua endpoint này; yêu cầu nó sẽ trả `403 forbidden_scope`. Điều này ngăn một admin token (có thể có thời hạn) tạo ra admin access/refresh token sống lâu.

Phản hồi là một token response tiêu chuẩn với `access_token`, `refresh_token`, `id_token` tùy chọn, `expires_in`, và `scope` đã được cấp (phân cách bằng dấu cách).
