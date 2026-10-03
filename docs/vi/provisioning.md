---
layout: default
title: Cấp phát
locale: vi
---

# Cấp phát TCC

Authagonal cấp phát người dùng vào các ứng dụng downstream theo mẫu **Try-Confirm-Cancel (TCC)**. Điều này bảo đảm mọi ứng dụng đều đồng thuận trước khi người dùng có quyền truy cập, với khả năng quay lui sạch sẽ nếu có ứng dụng nào từ chối.

## Khi nào việc cấp phát chạy {#when-provisioning-runs}

Việc cấp phát chạy tự động mỗi khi người dùng được tạo, bất kể theo đường tạo nào:

| Endpoint | Điều kiện kích hoạt |
|---|---|
| `POST /api/v1/profile/` | Quản trị viên tạo người dùng |
| `POST /api/auth/register` | Tự đăng ký |
| SAML ACS (`POST /saml/{id}/acs`) | Lần đăng nhập SSO đầu tiên (người dùng mới) |
| Callback OIDC (`GET /oidc/callback`) | Lần đăng nhập SSO đầu tiên (người dùng mới) |
| SCIM (`POST /scim/v2/Users`) | Cấp phát từ identity provider |
| `GET /connect/authorize` | Lần authorize đầu tiên qua một client có `ProvisioningApps` |

Các tổ hợp ứng dụng/người dùng đã được cấp phát sẽ được bỏ qua (theo dõi trong bảng `UserProvisions`).

Các đường tạo người dùng cấp phát vào **mọi ứng dụng đã cấu hình**. Authorize endpoint chỉ cấp phát vào danh sách `ProvisioningApps` của client.

**Khi bị từ chối:** Nếu bất kỳ ứng dụng cấp phát nào từ chối người dùng ở pha Try (hoặc một callback thất bại), người dùng vừa được tạo sẽ bị xóa. Điều này ngăn tình trạng người dùng chỉ được tạo một nửa. Những gì bên gọi nhận được tùy thuộc vào đường đi:

| Đường đi | Response |
|---|---|
| Quản trị viên tạo (`POST /api/v1/profile/`), tự đăng ký | `422 Unprocessable Entity` kèm lý do từ chối |
| SAML ACS, callback OIDC | `400 Bad Request`, `{ "error": "provisioning_rejected", "message": "..." }` |
| Tạo qua SCIM | SCIM `400`, `scimType: invalidValue`, với một thông báo cố định (văn bản của ứng dụng downstream không được phản hồi lại cho identity provider) |
| Xác nhận việc nhận lại tài khoản không mật khẩu | `400 provisioning_rejected` dạng JSON, hoặc chuyển hướng tới `/login?error=provisioning_rejected&error_description=...` khi người dùng bấm trên trình duyệt (xem [Nâng cấp người dùng](user-upgrade)) |
| `GET /connect/authorize` | Chuyển hướng trở lại client với `error=access_denied` |

Request tạo của quản trị viên chấp nhận `skipProvisioning: true`, dành cho bên gọi bên thứ nhất mà chính nó là đích cấp phát và không muốn callback của mình bị gọi lại trong lúc đang thiết lập người dùng dở dang. Khi đó không có gì được cấp phát và không ứng dụng nào được gọi cho người dùng đó.

## Cấu hình {#configuration}

### 1. Định nghĩa các ứng dụng cấp phát {#1-define-provisioning-apps}

Trong `appsettings.json`:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-bearer-token",
      "TryTimeoutSeconds": 60
    }
  }
}
```

`TryTimeoutSeconds` là tùy chọn (mặc định 60). Hãy tăng nó khi ứng dụng downstream thực hiện công việc thực sự trong pha Try. Confirm, Cancel và Deprovision luôn dùng một thời gian chờ cố định ngắn (10 giây) và không điều chỉnh được; chúng lúc nào cũng nên nhẹ.

Mục cấu hình `ProvisioningApps` chỉ được đọc khi không có `IProvisioningAppStore` nào được đăng ký. Các provider Azure Table, AWS và SQL đều đăng ký một store như vậy, và khi đó thư viện phân giải ứng dụng từ store (xem [Tùy biến cách phân giải ứng dụng](#custom-app-resolution)), nên với một provider lưu trữ bền vững, hãy định nghĩa ứng dụng qua admin API thay vì trong `appsettings.json`.

### 2. Gán ứng dụng cho client {#2-assign-apps-to-clients}

Mỗi client khai báo những ứng dụng mà người dùng của nó phải được cấp phát vào, qua trường `provisioningApps` trên bản ghi client. Hãy đặt trường này qua admin API của client (cấu hình nạp sẵn `Clients` không mang trường này). Việc tạo client gắn toàn bộ bản ghi, còn `PUT /api/v1/clients/{clientId}` gộp các trường bạn gửi lên client đã lưu, nên một request chỉ mang `provisioningApps` sẽ giữ nguyên phần còn lại của client:

```
PUT /api/v1/clients/web-app
{
  "provisioningApps": ["my-backend"]
}
```

Khi người dùng authorize qua `web-app`, họ được cấp phát vào `my-backend` nếu chưa được cấp phát trước đó.

## Giao thức TCC {#tcc-protocol}

Authagonal thực hiện ba loại lời gọi HTTP tới endpoint cấp phát của bạn. Tất cả đều dùng `POST` với body JSON và `Authorization: Bearer {ApiKey}`.

### Pha 1: Try {#phase-1-try}

**Request:** `POST {CallbackUrl}/try`

```json
{
  "transactionId": "a1b2c3d4...",
  "userId": "user-id",
  "email": "user@example.com",
  "firstName": "Jane",
  "lastName": "Doe",
  "organizationId": "org-id-or-null",
  "customAttributes": { "key": "value" }
}
```

Các trường null (bao gồm `customAttributes` khi người dùng không có thuộc tính nào) bị lược khỏi payload.

**Response mong đợi:**

| Status | Body | Ý nghĩa |
|---|---|---|
| `200` | `{ "approved": true }` | Người dùng có thể được cấp phát. Ứng dụng tạo một bản ghi **đang chờ**. |
| `200` | `{ "approved": false, "reason": "..." }` | Người dùng bị từ chối. Không có bản ghi nào được tạo. |
| `2xx` | Body rỗng hoặc không phân tích được | Được coi là phê duyệt. |
| Khác 2xx | Bất kỳ | Được coi là thất bại. |

Hãy trả về một giá trị `approved` tường minh. Một response có body không đọc được dưới dạng JSON sẽ được coi là phê duyệt, nên một endpoint cấu hình sai trả `200` kèm một trang HTML sẽ chấp thuận mọi người dùng.

`transactionId` định danh lần thử cấp phát này. Ứng dụng của bạn nên lưu nó cùng với bản ghi đang chờ.

Một response phê duyệt cũng có thể trả về `organizationId`, `customAttributes` và `emailVerified`. Authagonal gộp chúng vào người dùng: `organizationId` chỉ được áp dụng nếu người dùng chưa có (các ứng dụng sau trong cùng giao dịch sẽ thấy giá trị đã gán trước đó), các mục `customAttributes` được gộp theo từng khóa, và `emailVerified: true` đánh dấu email của người dùng là đã xác nhận (dùng khi ứng dụng downstream đã xác minh địa chỉ; khi đó việc tự đăng ký sẽ bỏ qua email xác minh). Cả `organizationId` lẫn các thuộc tính đều được đưa lên token (claim `org_id`; thuộc tính tùy chỉnh qua cấu hình `UserClaims` của scope). Các giá trị đã gộp được lưu vào người dùng khi mọi ứng dụng đều đã confirm.

### Pha 2: Confirm {#phase-2-confirm}

Chỉ được gọi nếu **mọi** ứng dụng đều trả về `approved: true` ở pha try.

**Request:** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Response mong đợi:** `2xx` (body bất kỳ). Ứng dụng của bạn nâng bản ghi đang chờ thành đã xác nhận. Một response khác 2xx hoặc quá thời gian chờ (10 giây) được tính là confirm thất bại.

### Pha 3: Cancel {#phase-3-cancel}

Được gọi nếu try của **bất kỳ** ứng dụng nào bị từ chối hoặc thất bại, để dọn dẹp các ứng dụng đã thành công ở pha try.

**Request:** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Response mong đợi:** `200` (body bất kỳ). Ứng dụng của bạn xóa bản ghi đang chờ.

Cancel chỉ là cố gắng hết mức: nếu nó thất bại, Authagonal ghi log lỗi và tiếp tục. Ứng dụng của bạn nên **dọn các bản ghi chưa được xác nhận sau một TTL** (ví dụ 1 giờ) như một lưới an toàn.

## Sơ đồ luồng {#flow-diagram}

```
Authorize Endpoint
    │
    ├─ User authenticated ✓
    ├─ Client requires apps: [A, B]
    ├─ User already provisioned into: [A]
    ├─ Need to provision: [B]
    │
    ├─ TRY B ──────────► App B: create pending record
    │   └─ approved: true
    │
    ├─ CONFIRM B ──────► App B: promote to confirmed
    │   └─ 200 OK
    │
    ├─ Store provision record (userId, "B")
    ├─ Issue authorization code
    └─ Redirect to client
```

### Khi thất bại {#on-failure}

```
    ├─ TRY A ──────────► App A: create pending record
    │   └─ approved: true
    │
    ├─ TRY B ──────────► App B: rejects
    │   └─ approved: false, reason: "No license available"
    │
    ├─ CANCEL A ───────► App A: delete pending record
    │
    └─ Redirect with error=access_denied
```

### Khi confirm thất bại một phần {#on-partial-confirm-failure}

Nếu một lần confirm thất bại, Authagonal quay lui toàn bộ giao dịch:

1. Các ứng dụng chưa được confirm nhận `POST {CallbackUrl}/cancel`.
2. Các ứng dụng đã confirm **trong giao dịch này** được bù trừ bằng `DELETE {CallbackUrl}/users/{userId}` (cùng lời gọi với [thu hồi cấp phát](#deprovisioning)), và bản ghi cấp phát của chúng bị xóa. Các ứng dụng mà người dùng đã được cấp phát vào từ một giao dịch trước đó được giữ nguyên.
3. Một lỗi cấp phát được phát ra, và đường gọi sẽ xóa người dùng vừa tạo (hoặc, với authorize endpoint, trả về một lỗi).

Bản ghi cấp phát chỉ được lưu sau khi mọi lần confirm đều thành công, nên một lần thử lại sẽ thử lại tất cả các ứng dụng. Việc bù trừ chỉ là cố gắng hết mức: một `DELETE` thất bại được ghi log và tài khoản trong ứng dụng có thể cần được xóa thủ công.

## Tùy biến cách phân giải ứng dụng {#custom-app-resolution}

Thư viện tự chọn nguồn ứng dụng cho bạn:

- Khi một `IProvisioningAppStore` được đăng ký, điều mà các provider Azure Table, AWS và SQL đều làm, ứng dụng được lấy từ store (`StoreProvisioningAppProvider`) và được quản lý qua admin API bên dưới.
- Nếu không, chúng được đọc từ mục cấu hình `ProvisioningApps` (`ConfigProvisioningAppProvider`).

Đăng ký `IProvisioningAppProvider` của riêng bạn trước `AddAuthagonal` để phân giải ứng dụng theo cách khác, ví dụ theo từng tenant; giá trị mặc định của thư viện chỉ được thêm nếu chưa có cái nào được đăng ký:

```csharp
builder.Services.AddSingleton<IProvisioningAppProvider, MyAppProvider>();
builder.Services.AddAuthagonal(builder.Configuration);
```

Provider trả về danh sách ứng dụng và callback URL của chúng. `TccProvisioningOrchestrator` gọi Try/Confirm/Cancel trên từng ứng dụng.

> **Theo mặc định, `CallbackUrl` phải định tuyến công khai được.** Authagonal kiểm tra nó khi được ghi và kiểm tra lại ở mỗi request mà nó thực hiện, từ chối các đích loopback, RFC1918, link-local và `.internal`/`.local` (callback cấp phát là một URL do server tải). Một ứng dụng cấp phát chạy bên trong mạng riêng của bạn là cách triển khai được hỗ trợ: hãy khai báo nó trong [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard).

### Admin API {#admin-api}

Các ứng dụng nằm trong store được quản lý tại `/api/v1/provisioning/apps` (policy `IdentityAdmin`; mọi thay đổi đều được ghi audit):

| Route | Hành vi |
|---|---|
| `GET /` | `{ "apps": [{ "appId", "name", "callbackUrl", "hasApiKey", "tryTimeoutSeconds" }], "limit": n }`. API key không bao giờ được trả về, chỉ có `hasApiKey`. `limit` là hạn mức ứng dụng, null khi không có hạn mức. |
| `POST /` | Tạo mới. `name` và `callbackUrl` là bắt buộc; `apiKey` và `tryTimeoutSeconds` là tùy chọn. Một `appId` dài 12 ký tự được tạo ra. Vượt hạn mức thì trả `400 provisioning_app_limit`. |
| `PUT /{appId}` | Thay thế `name`, `callbackUrl` và `tryTimeoutSeconds` (cả `name` lẫn `callbackUrl` lại là bắt buộc). `apiKey` bị bỏ trống hoặc null thì khóa được giữ nguyên; một chuỗi rỗng sẽ xóa nó. `404 app_not_found` với ứng dụng không xác định. |
| `DELETE /{appId}` | `{ "removed": true }`. |
| `POST /{appId}/test` | Gửi một lời gọi Try với người dùng thử nghiệm cố định (`test-user`, `test@example.com`) tới ứng dụng, với thời gian chờ 10 giây. Trả về `{ "success", "statusCode", "body" }` (body bị cắt còn 1000 ký tự). Lỗi kết nối trả về `success: false, statusCode: 0` thay vì một status lỗi. |

`callbackUrl` phải là một URL `http` hoặc `https` tuyệt đối trên một host bên ngoài, như mô tả ở trên. `tryTimeoutSeconds` bị kẹp trong khoảng 5 tới 300 giây. `appId` là giá trị mà client liệt kê trong `provisioningApps`.

## Thu hồi cấp phát {#deprovisioning}

Khi người dùng bị xóa qua admin API (`DELETE /api/v1/profile/{userId}`) hoặc bị thu hồi cấp phát qua SCIM (`DELETE /scim/v2/Users/{id}`, một thao tác xóa mềm làm vô hiệu hóa người dùng), Authagonal gọi `DELETE {CallbackUrl}/users/{userId}` trên từng ứng dụng mà người dùng đã được cấp phát vào, với thời gian chờ 10 giây, và xóa bản ghi cấp phát. Việc này chỉ là cố gắng hết mức: lỗi được ghi log nhưng không chặn việc xóa. Ứng dụng không còn được cấu hình sẽ bị bỏ qua kèm một cảnh báo.

`ReprovisionAsync` trên `IProvisioningOrchestrator` chạy lại Try và Confirm cho mọi ứng dụng ngay cả khi người dùng đã được cấp phát. Thư viện dùng nó khi một tài khoản không mật khẩu được nhận lại (xem [Nâng cấp người dùng](user-upgrade)); một lần đăng nhập lại thông thường thì không bao giờ.

## Triển khai các endpoint upstream {#implementing-the-upstream-endpoints}

### Ví dụ tối giản (Node.js/Express) {#minimal-example-nodejsexpress}

```javascript
const pending = new Map(); // transactionId → user data

app.post('/provisioning/try', (req, res) => {
  const { transactionId, userId, email } = req.body;

  // Your business logic: can this user be provisioned?
  if (!isAllowed(email)) {
    return res.json({ approved: false, reason: 'Domain not allowed' });
  }

  // Store pending record with TTL
  pending.set(transactionId, { userId, email, createdAt: Date.now() });

  res.json({ approved: true });
});

app.post('/provisioning/confirm', (req, res) => {
  const { transactionId } = req.body;
  const data = pending.get(transactionId);

  if (data) {
    createUser(data); // Promote to real record
    pending.delete(transactionId);
  }

  res.sendStatus(200);
});

app.post('/provisioning/cancel', (req, res) => {
  pending.delete(req.body.transactionId);
  res.sendStatus(200);
});

// Cleanup unconfirmed records older than 1 hour
setInterval(() => {
  const cutoff = Date.now() - 3600000;
  for (const [id, data] of pending) {
    if (data.createdAt < cutoff) pending.delete(id);
  }
}, 600000);
```
