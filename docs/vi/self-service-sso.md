---
layout: default
title: SSO tự phục vụ
locale: vi
---

# Onboarding SSO tự phục vụ

Khi bạn đã [liên kết một kết nối](oidc-federation) tới IdP của khách hàng, câu hỏi tiếp theo
là: **điều gì xảy ra khi một người chưa từng đăng nhập xuất hiện?** Authagonal cung cấp cho bạn
ba chế độ đối với người dùng chưa biết đó, từ chặt chẽ nhất tới cởi mở nhất (từ chối mọi người dùng chưa biết, yêu cầu ngữ cảnh
lời mời, hoặc tự động cấp phát từ một tên miền được phép), cùng các cơ chế kiểm soát để ngăn một IdP *bên ngoài* trở thành
một cái bẫy tự gây hại. Hai chế độ đầu được trình bày chung trong Chế độ 1, chế độ thứ ba trong Chế độ 2. Hướng dẫn này nói về việc chọn và nối dây chế độ bạn muốn.

Tất cả đều là cấu hình theo từng kết nối: các mục nạp sẵn `OidcProviders` và `SamlProviders`, các bản ghi đã lưu
`OidcProviderConfig` / `SamlProviderConfig`, và admin API. Các nút điều chỉnh liên quan:

| Nút điều chỉnh | Tác dụng | Giao thức |
|---|---|---|
| `JitProvisioningEnabled` | Có được phép tạo người dùng chưa biết hay không? | OIDC, SAML |
| `ProvisioningAttributeParams` | Yêu cầu *ngữ cảnh lời mời* trên request trước khi tạo người dùng. | OIDC, SAML |
| `AllowUninvitedJit` | Cho phép tự tạo tài khoản mà **không** cần lời mời (được gắn thẻ theo kết nối). | OIDC, SAML |
| `IsExternalConnection` | Đánh dấu một IdP bên thứ ba để các cờ chỉ dành cho bên thứ nhất không thể áp dụng. | Chỉ OIDC |
| `InteractionPath` | Hiển thị một trang của ứng dụng đăng nhập (tên/điều khoản) *trước khi* liên kết. | Chỉ OIDC |

Nơi có thể đặt từng nút là điều quan trọng, vì admin API không cung cấp tất cả chúng:

- **Cấu hình nạp sẵn (`OidcProviders`, `SamlProviders`):** mọi nút ở trên tồn tại cho giao thức đó. Các kết nối
  được nạp sẵn sẽ được áp dụng lại từ cấu hình ở mỗi lần khởi động, nên với một kết nối được nạp sẵn,
  `JitProvisioningEnabled` và `AllowUninvitedJit` đến từ phần nạp sẵn, không phải từ giá trị được lưu gần nhất.
- **SAML admin API** (`POST` / `PUT /api/v1/saml/connections`): `JitProvisioningEnabled`,
  `ProvisioningAttributeParams` và `AllowUninvitedJit`.
- **OIDC admin API** (`POST /api/v1/oidc/connections`): `JitProvisioningEnabled` và `InteractionPath`
  (phải bắt đầu bằng `/`). `ProvisioningAttributeParams`, `AllowUninvitedJit` và `IsExternalConnection`
  chỉ đặt được qua phần khởi tạo với OIDC, và không có route cập nhật cho kết nối OIDC. Xem
  [Admin API](admin-api) và [Liên kết OIDC](oidc-federation).

## Chế độ 1: chỉ theo lời mời (từ chối người không được mời) {#posture-1-invite-only-reject-the-uninvited}

Đây là mặc định. Với `JitProvisioningEnabled: false`, một người dùng SSO chưa biết bị từ chối thẳng
(`access_denied`, "contact your administrator"), đó là điều bạn muốn khi mọi người dùng phải được quản trị viên
hoặc SCIM tạo trước.

Nếu bạn muốn JIT nhưng *chỉ* khi có lời mời, hãy bật JIT **và** khai báo
`ProvisioningAttributeParams`. Những tham số này nêu tên các tham số query `/authorize` nằm trong danh sách cho phép, mang ngữ cảnh
lời mời (ví dụ `acceptKind`, `acceptToken`). Người dùng chưa biết chỉ được cấp phát khi ít nhất một trong các
tham số đó thực sự được gửi kèm giá trị; một lần đăng nhập SSO trơn không có lời mời bị từ chối với `access_denied`
("This login requires an invitation"), nên một lần đăng nhập lạc không thể âm thầm tự cấp phát một tài khoản/tổ chức mới.
Các tham số được đọc từ query của URL `/authorize` mà người dùng đang quay về (`RelayState` với
SAML); OIDC còn dự phòng bằng chính query của request callback.

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "ConnectionName": "Acme (Entra)",
      "MetadataLocation": "https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration",
      "ClientId": "…", "ClientSecret": "…",
      "AllowedDomains": ["acme.com"],
      "JitProvisioningEnabled": true,
      "ProvisioningAttributeParams": ["acceptKind", "acceptToken"]
    }
  ]
}
```

Không có `RedirectUrl` nào cần đặt: `redirect_uri` của callback được suy ra theo từng request là
`{issuer}/oidc/callback`, nên hãy đăng ký URI đó với IdP upstream. Một `RedirectUrl` được nạp sẵn sẽ bị bỏ qua.

Các tham số được ghi lại nằm trên `CustomAttributes` của người dùng JIT và tới được
[trình xử lý `Try` cấp phát](provisioning), vốn là lớp kiểm soát thực sự đối với các *giá trị* (ví dụ
"invite token này có khớp với email này không?"). Authagonal ghi lại các khóa nằm trong danh sách cho phép; trình cấp phát của bạn quyết định
chúng có hợp lệ không. Nếu `Try` trả lời `approved: false`, người dùng vừa được tạo sẽ bị xóa và trình duyệt nhận
`400 provisioning_rejected`.

## Chế độ 2: tự phục vụ (tự động cấp phát người dùng thuộc tên miền được phép) {#posture-2-self-service-auto-provision-an-allowed-domain-user}

Với kịch bản "bất kỳ nhân viên nào của khách hàng cũng chỉ cần đăng nhập là có tài khoản", hãy đặt `AllowUninvitedJit: true`. Khi đó
một người dùng chưa biết từ một **tên miền được phép** sẽ được cấp phát ngay cả khi không có ngữ cảnh lời mời, và Authagonal gắn thẻ họ
bằng kết nối mà họ đi qua để trình cấp phát của bạn có thể đặt họ vào đúng tenant thay vì
tạo ra một tenant mới. Việc kiểm tra tên miền chỉ áp dụng khi `AllowedDomains` không rỗng: một kết nối
không liệt kê tên miền nào sẽ chấp nhận bất kỳ tên miền nào mà IdP của nó khẳng định, nên hãy liệt kê chúng trên mọi kết nối tự phục vụ.

```json
{
  "ConnectionId": "acme-entra",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true,
  "ProvisioningAttributeParams": ["acceptKind", "acceptToken"],
  "AllowUninvitedJit": true
}
```

Thẻ này đến dưới dạng thuộc tính tùy chỉnh `federated_connection`. Giá trị của nó là `ConnectionName` của kết nối
(không phải `ConnectionId`), và nó chỉ được ghi khi người dùng được tạo mà không có ngữ cảnh lời mời, nên một
người dùng được mời sẽ mang các tham số đã ghi lại thay vào đó. Trình xử lý `Try` của bạn rẽ nhánh dựa trên nó:

```javascript
app.post('/provisioning/try', async (req, res) => {
  const { userId, email, customAttributes } = req.body;

  if (customAttributes?.acceptToken) {
    // Invited: validate the invite and add them to that org.
    const org = await validateInvite(customAttributes.acceptToken, email);
    if (!org) return res.json({ approved: false, reason: 'Invalid invite' });
    stage(userId, { orgId: org.id, role: customAttributes.acceptKind ?? 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  if (customAttributes?.federated_connection) {
    // Self-service: no invite, but they came through a known enterprise connection.
    const org = await orgForConnection(customAttributes.federated_connection);
    stage(userId, { orgId: org.id, role: 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  return res.json({ approved: false, reason: 'No invite and no known connection' });
});
```

`AllowUninvitedJit` là tùy chọn bật theo từng kết nối: một kết nối khai báo `ProvisioningAttributeParams` nhưng
**không** đặt nó thì vẫn chỉ theo lời mời.

Hai bước kiểm tra nữa chạy trước khi bất kỳ người dùng chưa biết nào được tạo, bất kể bạn chọn chế độ nào:

- **Tên miền không được thuộc về một kết nối khác.** Nếu chỉ mục tên miền SSO định tuyến tên miền email của người dùng
  tới một kết nối khác, lần đăng nhập bị từ chối với `access_denied` ("This email domain is
  managed by a different identity provider").
- **Chỉ OIDC: upstream phải đã xác minh email.** Khi upstream không báo `email_verified` là true (đọc từ id_token, hoặc từ
  response userinfo khi email đến từ đó), lần đăng nhập bị
  từ chối với `access_denied`. Assertion SAML không có cờ như vậy, nên SAML dựa vào `AllowedDomains`
  thay thế.

`federated_connection` là một tên thuộc tính được dành riêng. Nó không bao giờ được phát ra trên token, claim cùng tên trong id_token
của một upstream OIDC bị loại bỏ, và việc tự đăng ký ẩn danh không thể đặt nó, nên chỉ các callback SSO
mới có thể khẳng định một tài khoản đã đi qua kết nối nào.

## Ngăn IdP bên ngoài trở thành cái bẫy tự gây hại {#keep-external-idps-from-becoming-foot-guns}

Một vài cờ của kết nối OIDC là an toàn trên một kết nối do **bạn** kiểm soát nhưng nguy hiểm trên một IdP bên thứ ba
tùy ý:

- **`UseUpstreamSubjectAsUserId`**: upstream chọn id người dùng cục bộ. Trên provider liên kết chia sẻ của chính bạn,
  điều đó giữ cho các id khớp nhau; trên IdP của khách hàng, nó để *họ* chọn id người dùng của bạn.
- **`AutoLinkExistingByEmail`**: gắn một lần đăng nhập liên kết vào một tài khoản cục bộ đã tồn tại theo email,
  bỏ qua bước kiểm tra quyền sở hữu tên miền. Với hộp thư đã được xác minh và kết nối bên thứ nhất thì ổn; trên một IdP bên ngoài, đó là
  một đòn bẩy để chiếm đoạt tài khoản.

Hãy đánh dấu các kết nối bên thứ ba là **bên ngoài** và những cờ đó sẽ bị vô hiệu hóa ngay cả khi được đặt:

```json
{
  "ConnectionId": "acme-entra",
  "IsExternalConnection": true,
  "UseUpstreamSubjectAsUserId": false,
  "AutoLinkExistingByEmail": false
}
```

`IsExternalConnection` mặc định là `false` (bên thứ nhất) nên các kết nối hiện có không bị ảnh hưởng. Hãy đặt nó trên
mọi kết nối OIDC trỏ tới IdP của người khác; khi đó một lỗi cấu hình về sau không thể trao cho IdP đó
quyền kiểm soát danh tính cục bộ. Kết nối SAML không có cờ nào trong số này, nên ở đó không có gì cần vô hiệu hóa.
(Việc gắn một danh tính liên kết vào một tài khoản đã tồn tại vẫn còn đòi hỏi thêm rằng
`AllowedDomains` của kết nối bảo đảm cho tên miền của email: xem
[Liên kết OIDC: Bảo mật](oidc-federation).)

## Thu thập thông tin trước khi liên kết {#collect-something-before-federating}

Đôi khi bạn cần hiển thị cho người dùng một trang **trước khi** chuyển họ tới IdP: tên hiển thị của khách, một
ô đánh dấu đồng ý điều khoản, một bộ chọn gói. `InteractionPath` (chỉ với kết nối OIDC) nêu tên một route của ứng dụng đăng nhập để hiển thị
trước:

```json
{ "ConnectionId": "guest-link", "InteractionPath": "/guest" }
```

Khi một request `idp_hint={ConnectionId}` chưa xác thực tới `/connect/authorize`, Authagonal chuyển hướng tới
`{LoginAppUrl}{InteractionPath}?returnUrl=<authorize url>&connection={id}` thay vì đi thẳng tới IdP
(`LoginAppUrl` mặc định là `/login`, và đường dẫn phải bắt đầu bằng `/`). Cùng việc chuyển hướng đó xảy ra khi
kết nối duy nhất hoặc kết nối khớp tên miền của một [tổ chức](#organisation-scoped-connections) được tự động challenge,
và khi `prompt=login` buộc xác thực lại thông qua một `idp_hint`. Trang của bạn thu thập những gì nó cần,
nối các giá trị vào query của `returnUrl` (nơi `PassthroughParams` /
`ProvisioningAttributeParams` đọc chúng), rồi tự tiếp tục tới `/oidc/{id}/login`. Một trang
xác định rằng không cần tương tác có thể tiếp tục ngay lập tức.

## Kết nối theo phạm vi tổ chức {#organisation-scoped-connections}

Mọi thứ ở trên mô tả một kết nối **cấp tenant**: kết nối mà toàn bộ tenant dùng chung, có
`AllowedDomains` nhận một tên miền email cho mọi màn hình đăng nhập mà tenant phục vụ. Đó là hình thức
phù hợp khi bạn liên kết tới một khách hàng cho mỗi tenant. Nó là hình thức sai khi một tenant phục vụ nhiều
[tổ chức](organizations) khách hàng và mỗi tổ chức mang IdP riêng: hai khách hàng không thể cùng nhận
`contoso.com`, và nút "Tiếp tục với Contoso Entra" của một khách hàng không có lý do gì xuất hiện trên
màn hình đăng nhập của khách hàng khác.

Đặt `OrganizationId` trên một kết nối và nó sẽ thuộc về tổ chức đó (khi tạo và, với SAML,
khi cập nhật, qua admin API; một tổ chức không tồn tại trả về `400 unknown_organization`):

```json
{
  "ConnectionId": "acme-entra",
  "OrganizationId": "org_7f3a9c",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true
}
```

Có ba điều thay đổi, và không có gì khác thay đổi.

**Nó chỉ được đề xuất khi tổ chức đó được chọn.** Một kết nối theo phạm vi tổ chức không bao giờ xuất hiện trên
màn hình đăng nhập của chính tenant và không bao giờ được tới bởi một request không phân giải ra tổ chức nào,
kể cả request có `login_hint` khớp chính xác với các tên miền của nó.

**Tên miền của nó chỉ được đối chiếu bên trong tổ chức đó.** Một kết nối theo phạm vi tổ chức cố ý
*không* được ghi vào chỉ mục tên miền SSO toàn tenant, nên một tên miền có thể được nhận một lần ở cấp tenant và
một lần cho mỗi tổ chức. Lần nhận thứ hai bên trong cùng một tổ chức vẫn bị từ chối với `domain_claimed`,
trên cả hai giao thức, để một địa chỉ không thể định tuyến tới hai IdP của cùng một tổ chức. Chuyển một kết nối
vào một tổ chức sẽ xóa các dòng chỉ mục của nó; chuyển nó trở lại (gửi `"organizationId": ""` tới endpoint cập nhật
SAML) sẽ đăng ký lại chúng. Kết nối OIDC không có route cập nhật, nên phạm vi của chúng được đặt lúc tạo hoặc
trong phần khởi tạo `OidcProviders`.

**Mọi người đăng nhập qua nó đều là thành viên của nó.** SAML ACS và callback OIDC đóng dấu
tổ chức của kết nối làm `org_id`, ghi đè `AuthUser.OrganizationId` của chính tài khoản (vốn
là một sản phẩm phụ của việc cấp phát phía downstream chứ không phải một khẳng định về lần đăng nhập này), và tạo một
tư cách thành viên đang hoạt động nếu người dùng chưa có. Một tư cách thành viên **đang được mời** sẽ được chấp nhận: nó trở thành `active` và
giữ nguyên các role, người mời và thời điểm mời, vì IdP của chính tổ chức giờ đã bảo đảm cho
người đó. Mọi tư cách thành viên hiện có khác được giữ nguyên: một dòng `suspended` vẫn bị đình chỉ, nên việc đăng nhập
lại không thể khôi phục quyền truy cập mà quản trị viên đã thu hồi.

### Request dành cho tổ chức nào, trước khi có ai đăng nhập {#which-organization-a-request-is-for-before-anyone-signs-in}

Home-realm discovery phải trả lời câu hỏi này trước khi có người dùng, nên nó được phân giải tách biệt với (nhưng theo
cùng thứ tự như) [bộ chọn sau xác thực](organizations#precedence):

1. **Tham số `organization`** trên request (một slug hoặc một id).
2. **`OAuthClient.RestrictedToOrganizationIds`**, khi nó chứa đúng một mục. Hai mục trở lên không phải là một
   lựa chọn: client phục vụ nhiều tổ chức và request không nêu tổ chức nào.
3. **`ITenantContext.OrganizationId`**: một host ghim một tổ chức cho mỗi request, ví dụ một tên miền tùy chỉnh
   theo từng tổ chức. `null` trong mọi bản triển khai single-tenant.

Tổ chức phải tồn tại và đang được bật, và giới hạn của client phải cho phép nó. Bất cứ điều gì khác
sẽ phân giải thành *không có tổ chức nào* và request tiếp tục trên đường toàn tenant y như trước. Đặc biệt,
một tham số nêu tên một tổ chức mà client bị giới hạn không được dùng sẽ không bị từ chối ở đây:
việc từ chối đã tồn tại sau bước xác thực (`access_denied`), và chuyển nó lên trước màn hình
đăng nhập sẽ thay đổi những request nào mà một bên gọi chưa xác thực có thể phân biệt được.

### `/connect/authorize` làm gì với nó {#what-connectauthorize-does-with-it}

Khi một tổ chức đã được phân giải, và trước mọi quy tắc toàn tenant:

- `idp_hint` nêu tên một trong các kết nối **của nó** sẽ đi thẳng tới kết nối đó. Điều này bao gồm cả SAML, vốn là thứ
  mà đường hint toàn tenant (chỉ OIDC) không với tới được. Một hint nêu tên bất cứ thứ gì khác sẽ đi tiếp xuống bước sau.
- **Đúng một kết nối và không có `login_hint` mâu thuẫn** → đi thẳng tới đó. Một kết nối không liệt kê
  tên miền nào sẽ nhận toàn bộ tổ chức; một kết nối có liệt kê tên miền vẫn được tự động challenge trừ khi
  tên miền của địa chỉ trong hint không nằm trong số đó.
- **Nhiều kết nối** → tên miền email trong hint chọn giữa chúng.
- **Không khớp** → hành vi `login_hint` và thẻ đăng nhập toàn tenant, không thay đổi.

Một lần liên kết thất bại và quay về với `error=` trên query sẽ trả lỗi đó cho relying
party thay vì liên kết lại, nên việc tự động challenge không thể lặp vô hạn.

### Ứng dụng đăng nhập nhìn thấy gì {#what-the-login-app-sees}

`/api/auth/providers` và `/api/auth/sso-check` đều nhận một tham số query `organization` (và dự phòng
bằng `ITenantContext.OrganizationId`), được phân giải theo cùng các quy tắc. Khi có một tổ chức:

- `providers` liệt kê các kết nối dạng nút của **tổ chức đó** trước, rồi tới các kết nối của chính tenant. Một kết nối
  chỉ là một nút khi nó không liệt kê `AllowedDomains` nào (và, với OIDC, có `ShowOnLogin` được bật); các kết nối
  định tuyến theo tên miền được tới bằng cách nhập email trước qua `sso-check`. Kết nối theo phạm vi tổ chức bị loại hoàn toàn khỏi danh sách
  khi không phân giải được tổ chức nào, và kết nối của các tổ chức khác không bao giờ được liệt kê.
- `providers` có thêm **`autoChallenge`** khi tổ chức có đúng một kết nối: một bản ghi
  provider đầy đủ (`connectionId`, `name`, `type`, `loginUrl`, `iconUrl`) cho kết nối mà ứng dụng
  nên đi thẳng tới, bỏ qua thẻ đăng nhập. Nó mang toàn bộ bản ghi chứ không chỉ một id trơn vì
  kết nối đó có thể được định tuyến theo tên miền hoặc bị ẩn và vì vậy không có mặt trong `providers`. Trường này
  bị lược bỏ trong các trường hợp khác, và nó chỉ mang tính **gợi ý**: `/connect/authorize` tự thực hiện cùng việc tự động challenge đó,
  nên một ứng dụng bỏ qua nó vẫn tới được cùng IdP.
- `sso-check` đối chiếu tên miền của các kết nối thuộc tổ chức **trước** chỉ mục toàn tenant, và
  rơi xuống chỉ mục đó khi tổ chức không nhận gì cho địa chỉ đó. Một kết nối duy nhất không liệt kê
  tên miền nào sẽ nhận mọi địa chỉ.

### Khi cấp token {#at-token-issuance}

Một phiên được thiết lập qua một kết nối theo phạm vi tổ chức mang tổ chức của nó làm nguồn có độ ưu tiên
cao nhất sau giá trị được mang theo từ một lần refresh (cao hơn tham số `organization` và cao hơn giới hạn của
client) vì đó là nguồn duy nhất đã được *chứng minh*: người dùng đã xác thực tại một IdP thuộc về
đúng tổ chức đó. Một request nêu tên một tổ chức khác bị từ chối với `access_denied` thay vì
lặng lẽ được cấp token cho tổ chức kia. `RequireMembershipForTokens` của tổ chức vẫn được áp dụng, đó là
lý do callback tạo tư cách thành viên.

## Liên quan {#related}

- [Tổ chức](organizations): các bản ghi, tư cách thành viên, claim và quy tắc lựa chọn.
- [Liên kết OIDC](oidc-federation): thiết lập kết nối và mô hình bảo mật.
- [Cấp phát TCC](provisioning): trình xử lý `Try` mà các flow này gọi tới.
- [Giữ phiên liên kết đồng bộ](federated-sessions): thu hồi phiên cục bộ khi upstream thu hồi.
