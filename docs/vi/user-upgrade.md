---
layout: default
title: Nâng cấp người dùng
locale: vi
---

# Nâng cấp người dùng (nhận lại tài khoản không mật khẩu)

Một số tài khoản bắt đầu mà không có mật khẩu:

- một **khách** đã mở một liên kết chia sẻ và được tạo đúng lúc (just-in-time) bởi một lần đăng nhập liên kết,
- một người dùng được **cấp phát JIT** bởi một lần đăng nhập SSO hoặc một lời mời vào tổ chức,
- một người dùng thư mục được đẩy vào qua **SCIM**.

Mỗi tài khoản đó là một người dùng Authagonal thực sự (id ổn định, thường đã được cấp phát quyền truy cập phía downstream) chỉ
đơn giản là không có thông tin xác thực cục bộ. Lối vào *từng là* liên kết, đường link chia sẻ, hoặc lời mời.

**Nâng cấp** một người dùng như vậy cho phép họ đặt một mật khẩu bên thứ nhất và, thường là, đồng thời nâng cấp mối quan hệ của họ
với sản phẩm của bạn (khách → thành viên tiêu chuẩn, dùng thử → trả phí, "tạo tổ chức của bạn").
Authagonal cung cấp điều này như một flow chính thức, cần bật tường minh: người đó đăng ký lại bằng cùng email, chứng minh
họ kiểm soát hộp thư, và tài khoản **hiện có** của họ được nhận lại *tại chỗ* (cùng user id, nên mọi quyền truy cập
trước đó đều được giữ lại) trong khi ứng dụng của bạn chạy bất kỳ logic nâng cấp nào nó cần.

> Điều này cố ý không giống với "đặt lại mật khẩu". Việc đặt lại mật khẩu giả định một tài khoản đã có
> thông tin xác thực và gửi email một liên kết đặt lại. Việc nhận lại biến một tài khoản *không có thông tin xác thực* thành một tài khoản có thông tin xác thực và
> chạy lại việc cấp phát, để downstream của bạn có thể phản ứng với việc nâng cấp.

## Khi nào nên dùng {#when-to-use-it}

Hãy bật flow nhận lại khi một sản phẩm downstream coi "ai đó đăng ký bằng email của một danh tính
liên kết" là một đường nâng cấp hợp lệ, trường hợp điển hình là một khách đến từ liên kết chia sẻ quyết định tạo một
tài khoản thật. Nếu bản triển khai của bạn không có đường như vậy, hãy để nó tắt (mặc định): khi đó mọi email đã tồn tại
đều được coi là trùng lặp, và việc đăng ký trả về response thông thường không để lộ tài khoản có tồn tại hay không.

## 1. Bật tính năng {#1-enable-it}

Việc nhận lại được kiểm soát bởi một tùy chọn bật duy nhất trong mục cấu hình `Auth` (gắn với `AuthOptions`):

```json
{
  "Auth": {
    "AllowPasswordlessAccountClaim": true,
    "ClaimAllowedAttributeKeys": ["org_name", "plan"]
  }
}
```

- **`AllowPasswordlessAccountClaim`** (mặc định `false`): bật flow.
- **`ClaimAllowedAttributeKeys`** (mặc định rỗng = cho phép mọi khóa không được dành riêng): một danh sách cho phép gồm các
  khóa thuộc tính tùy chỉnh mà một lần nhận lại có thể mang vào tài khoản (xem
  [Truyền ngữ cảnh nâng cấp](#4-pass-upgrade-context-safely)). Hãy liệt kê các khóa mà trình cấp phát của bạn mong đợi để một
  lần nhận lại không thể chèn các thuộc tính tùy ý. Dù có tên như vậy, cùng danh sách này cũng lọc
  `customAttributes` của một lần đăng ký tự phục vụ thông thường.

Khi cờ tắt, một email đã tồn tại là trùng lặp. Khi cờ bật, một tài khoản **không có thông tin xác thực** đã tồn tại
(không có `PasswordHash`) có thể được nhận lại; một tài khoản đã có mật khẩu thì **không bao giờ** bị đụng tới: việc
đăng ký lại không thể ghi đè một thông tin xác thực thật.

## 2. Việc nhận lại, từ đầu đến cuối {#2-the-claim-end-to-end}

Người dùng gọi endpoint đăng ký thông thường với email của tài khoản họ muốn nhận lại:

```bash
# 1. The user re-registers with the SAME email as their guest/SSO/invite account.
curl -X POST https://auth.example.com/api/auth/register \
  -H "Content-Type: application/json" \
  -d '{
    "email": "grace@acme.com",
    "password": "a-strong-passphrase",
    "firstName": "Grace",
    "lastName": "Hopper",
    "customAttributes": { "org_name": "Acme Inc" }
  }'
# → 201 Created (enumeration-neutral: the same response a brand-new signup returns)
```

Response là `201` với `{ "success": true, "userId": "..." }`. Trên đường nhận lại, `userId` là một
giá trị dùng một lần, không phải id tài khoản thật, nên không thể dùng response để phân biệt một lần nhận lại với một lần đăng ký
hoàn toàn mới.

Chưa có gì có hiệu lực. Máy chủ **đặt tạm** mật khẩu cùng hồ sơ/thuộc tính và gửi email một liên kết xác minh
mới. Người dùng mở nó:

```
GET https://auth.example.com/api/auth/confirm-email?token=<from the email>
```

`GET` đó chỉ hiển thị một trang xác nhận một cú nhấp (để các trình quét thư và trình tải trước liên kết truy cập
URL không tiêu thụ token). **Việc nhấn nút** trên trang đó gửi
`POST /api/auth/confirm-email` và chính nó mới nâng thông tin xác thực đã đặt tạm lên và chạy việc nâng cấp. Cùng
`POST` đó cũng chấp nhận token dưới dạng tham số query hoặc JSON body (`{ "token": "..." }`); một bên gọi JSON
nhận `{ "message": "Email confirmed successfully.", "appLink": ... }`, còn form post từ trang
chuyển hướng tới `/login?email_confirmed=1`. Sau đó, người dùng đăng nhập bình thường bằng mật khẩu mới.

### Máy chủ làm gì {#what-the-server-does}

1. **Đăng ký**: vì tài khoản tồn tại và không có mật khẩu, request được coi là một lần nhận lại. Mật khẩu
   được chọn được băm vào `PendingPasswordHash` (bất hoạt, không đường xác thực nào đọc nó), và tên, họ
   cùng các `customAttributes` nằm trong danh sách cho phép được đặt tạm trong `PendingClaimJson`. Security stamp của tài khoản
   được xoay vòng ngay lúc đó, làm mất hiệu lực mọi liên kết xác minh đang nằm sẵn trong một
   hộp thư. Ngoài ra tài khoản **không** bị thay đổi gì. Một email xác minh được gửi dù
   email của tài khoản đã được xác nhận bởi flow ban đầu của nó: bằng chứng trước đó thuộc về một chủ thể *khác*,
   và việc nhận lại cần bằng chứng riêng. Liên kết mang một digest `pc=` của thông tin xác thực được đặt tạm cho nó.
2. **Xác nhận**: việc xác nhận là bằng chứng sở hữu. Máy chủ kiểm tra liên kết được gắn với thông tin xác thực
   *hiện đang* được đặt tạm, áp dụng hồ sơ/thuộc tính đã đặt tạm, chạy **`ReprovisionAsync`** (xem
   mục tiếp theo), rồi nâng `PendingPasswordHash` thành `PasswordHash` và xoay vòng security stamp
   một lần nữa. Nếu việc cấp phát từ chối việc nâng cấp, thông tin xác thực và hồ sơ đã đặt tạm bị loại bỏ và
   tài khoản vẫn không mật khẩu và vẫn có thể nhận lại, nên không có gì dở dang được lưu lại.

Nếu một lần nhận lại thứ hai được gửi trước khi lần đầu được xác nhận, nó thay thế thông tin xác thực đã đặt tạm và
liên kết đầu tiên ngừng hoạt động: xác nhận nó sẽ trả về `claim_superseded` (JSON `400`, hoặc chuyển hướng tới
`/login?error=claim_superseded` từ trang xác nhận). Một liên kết không có digest `pc=`, chẳng hạn liên kết từ
hành động "send verification email" của quản trị viên, cũng thất bại theo cách này khi đang có một thông tin xác thực được đặt tạm. Trong cả hai trường hợp,
người dùng yêu cầu một liên kết mới bằng cách đăng ký lại.

User id không bao giờ thay đổi, nên quyền truy cập dự án của khách, liên kết SCIM, tư cách thành viên nhóm, tất cả đều được giữ lại
qua việc nâng cấp.

## 3. Thực hiện nâng cấp ở downstream {#3-do-the-upgrade-downstream}

Việc xác nhận nhận lại gọi `ReprovisionAsync`, vốn khác với việc cấp phát thông thường ở chỗ chạy lại chu trình
[TCC Try/Confirm/Cancel](provisioning) **ngay cả với các ứng dụng mà người dùng đã được
cấp phát vào**. Đó chính là mục đích: ứng dụng của bạn đã cấp phát người dùng này dưới dạng *khách*, nên một
lần cấp phát thông thường sẽ bỏ qua họ; việc cấp phát lại cho bạn một lần Try thứ hai, giờ mang theo ngữ cảnh đăng ký, để
bạn có thể nâng cấp họ.

Trình xử lý `Try` cấp phát phân biệt "cấp phát lần đầu" với "nâng cấp" dựa trên việc nó đã có
bản ghi cho `userId` đó hay chưa, và phản ứng với ngữ cảnh mà lần nhận lại mang theo (ở đây là `org_name`):

```javascript
// POST {CallbackUrl}/try
app.post('/provisioning/try', async (req, res) => {
  const { transactionId, userId, email, customAttributes } = req.body;
  const existing = await db.members.findByAuthId(userId);

  if (!existing) {
    // First time we've seen this user: a plain new signup.
    stagePending(transactionId, { userId, email, role: 'member' });
    return res.json({ approved: true });
  }

  if (existing.kind === 'guest') {
    // UPGRADE: the guest is claiming a real account. Create their org from the signup context,
    // and stage the promotion (applied in /confirm). Reject to abort the whole claim if it can't proceed.
    const orgName = customAttributes?.org_name;
    if (!orgName) return res.json({ approved: false, reason: 'Organization name is required' });

    stagePending(transactionId, { userId, upgradeTo: 'standard', orgName });
    // Return org_id so Authagonal stamps it on the user's tokens (org_id claim).
    const orgId = deterministicOrgId(userId);
    return res.json({ approved: true, organizationId: orgId });
  }

  // Already a full member: nothing to do, but approve so the claim completes.
  res.json({ approved: true });
});

// POST {CallbackUrl}/confirm: all apps approved; commit the promotion.
app.post('/provisioning/confirm', async (req, res) => {
  const p = takePending(req.body.transactionId);
  if (p?.upgradeTo === 'standard') {
    await db.orgs.create({ id: deterministicOrgId(p.userId), name: p.orgName, ownerAuthId: p.userId });
    await db.members.promote(p.userId, { kind: 'standard' });
  }
  res.sendStatus(200);
});

// POST {CallbackUrl}/cancel: the claim failed elsewhere; drop the staged promotion.
app.post('/provisioning/cancel', (req, res) => { takePending(req.body.transactionId); res.sendStatus(200); });
```

Một `approved: false` từ bất kỳ ứng dụng nào (hoặc một callback thất bại) khiến việc xác nhận thất bại với `400 provisioning_rejected`
(một JSON body cho bên gọi API, hoặc chuyển hướng tới `/login?error=provisioning_rejected&error_description=...` từ
trang xác nhận) và để tài khoản ở trạng thái chưa nâng cấp, vẫn không mật khẩu và vẫn có thể nhận lại. Một
lần đăng ký không phải nhận lại mà bị một ứng dụng cấp phát từ chối sẽ trả về `422`; xem [Cấp phát TCC](provisioning).
Một `organizationId` (hoặc `customAttributes` bổ sung) trong response phê duyệt được hợp nhất vào người dùng và
đi theo các token của họ.

## 4. Truyền ngữ cảnh nâng cấp một cách an toàn {#4-pass-upgrade-context-safely}

`customAttributes` trên lời gọi đăng ký là cách lần nhận lại mang ngữ cảnh đăng ký (tên tổ chức, gói,
giới thiệu) tới trình cấp phát của bạn. Chúng được **đặt tạm**, chỉ được áp dụng khi nhấp vào liên kết xác minh, và được lọc bởi
`ClaimAllowedAttributeKeys`. Hãy giữ danh sách cho phép đó chặt chẽ: nó là ranh giới ngăn một người chỉ đơn thuần
*biết* email của một người dùng liên kết chèn các thuộc tính sẽ đi theo token của chủ sở hữu thật. Một
danh sách cho phép rỗng cho phép mọi khóa không được dành riêng (tiện cho các flow bên thứ nhất đáng tin cậy); một danh sách có nội dung
loại bỏ mọi thứ không được liệt kê.

Bất kể danh sách cho phép nói gì, bộ lọc luôn áp dụng các giới hạn sau, và âm thầm loại bỏ mọi thứ
vi phạm chúng (việc đăng ký vẫn thành công):

- tối đa 32 thuộc tính, với khóa dài tối đa 64 ký tự và giá trị dài tối đa 1024 ký tự;
- các khóa dành riêng sau không bao giờ được chấp nhận, kể cả khi được liệt kê trong `ClaimAllowedAttributeKeys`: `federated_connection`,
  `org_id`, `roles`, `groups`, `sub`, `iss`, `aud`, `scope`, `client_id`, `sid`, `acr`, `amr`, `email`,
  `email_verified`.

Cùng bộ lọc đó chạy trên việc đăng ký tự phục vụ thông thường (không phải nhận lại).

## Các đặc tính bảo mật {#security-properties}

- **Biết email là không đủ.** Việc nhận lại chỉ hoàn tất khi chính hộp thư của tài khoản nhận và
  xác nhận liên kết xác minh. Kẻ tấn công biết địa chỉ không bao giờ nhận được email đó.
- **Mỗi lúc chỉ một thông tin xác thực được đặt tạm.** Liên kết được gắn với thông tin xác thực được đặt tạm cho nó, nên một lần nhận lại về sau
  không thể được nâng lên bằng một liên kết cũ hơn (`claim_superseded`).
- **Một thông tin xác thực thật không bao giờ bị ghi đè.** Chỉ tài khoản không có `PasswordHash` mới có thể nhận lại; một lần nhận lại
  nhắm vào một tài khoản đã có thông tin xác thực sẽ nhận response trùng lặp thông thường, không để lộ tài khoản có tồn tại hay không.
- **Không có gì có hiệu lực cho tới khi được xác nhận.** Mật khẩu đã đặt tạm không thể dùng để xác thực, và
  hồ sơ/thuộc tính đã đặt tạm không được áp dụng, cho tới khi xác nhận. Một lần nâng cấp bị từ chối sẽ quay lui mọi thứ.
- **Việc chèn thuộc tính bị giới hạn** bởi `ClaimAllowedAttributeKeys`.

## Liên quan {#related}

- [Cấp phát TCC](provisioning): hợp đồng Try/Confirm/Cancel mà trình xử lý của bạn triển khai.
- [SSO tự phục vụ](self-service-sso): các flow JIT tạo ra những tài khoản không mật khẩu ngay từ đầu.
