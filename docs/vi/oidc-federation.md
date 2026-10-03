---
layout: default
title: Liên kết OIDC
locale: vi
---

# Liên kết OIDC

Authagonal có thể liên kết việc xác thực tới các identity provider OIDC bên ngoài (Google, Apple, Azure AD, v.v.). Nhờ vậy có thể dùng các flow kiểu "Đăng nhập bằng Google" trong khi Authagonal vẫn là auth server trung tâm.

## Cách hoạt động {#how-it-works}

Có hai lối vào liên kết:

**Dựa trên tên miền (đăng nhập tương tác):**

1. Người dùng nhập email trên trang đăng nhập
2. SPA gọi `/api/auth/sso-check`; nếu tên miền email được gắn với một OIDC provider, SSO là bắt buộc
3. Người dùng bấm "Tiếp tục với SSO" và được chuyển hướng tới IdP bên ngoài (khi email là `login_hint` của một authorize request và tên miền của nó được định tuyến tới một kết nối, người dùng đi thẳng tới IdP, kèm `login_hint` được chuyển tiếp)
4. Sau khi xác thực, IdP chuyển hướng trở lại `/oidc/callback`
5. Authagonal xác thực id_token, liên kết người dùng (hoặc tạo mới nếu kết nối cho phép cấp phát JIT), và đặt cookie phiên

**Do RP gợi ý (`idp_hint`):**

Relying party phía downstream có thể định tuyến thẳng tới một IdP upstream cụ thể mà không cần qua bước email/tên miền SSO. Thêm `idp_hint={connectionId}` vào `/connect/authorize`:

```
/connect/authorize?client_id=my-rp&scope=openid+email&...&idp_hint=google
```

Khi request chưa được xác thực, Authagonal chuyển hướng tới `/oidc/{connectionId}/login` với URL `/authorize` ban đầu được giữ lại làm `returnUrl`. Sau khi liên kết hoàn tất, người dùng quay về `/authorize` với một cookie phiên và flow tiếp tục bình thường. Nếu kết nối đặt `InteractionPath`, người dùng trước tiên được đưa tới trang đó của ứng dụng đăng nhập (xem [Thu thập thông tin trước khi liên kết](self-service-sso#collect-something-before-federating)). Một kết nối có `ShowOnLogin: false` không bao giờ được hiển thị thành nút đăng nhập và chỉ có thể tới được bằng cách này.

## Thiết lập {#setup}

### 1. Tạo một OIDC Provider {#1-create-an-oidc-provider}

**Cách A, Cấu hình (khuyến nghị cho các thiết lập tĩnh):**

Thêm vào `appsettings.json`:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

Các provider được nạp sẵn lúc khởi động. `ConnectionId`, `MetadataLocation`, `ClientId` và `ClientSecret` là bắt buộc (thiếu chúng thì khởi động thất bại). `RedirectUrl` được chấp nhận để tương thích và bị bỏ qua: redirect URI được suy ra theo từng request là `{Issuer}/oidc/callback`, vì nó phải nằm trên origin mà trình duyệt đang ở, và đó là URI cần đăng ký với IdP (một giá trị nạp sẵn khác sẽ được ghi log là bị bỏ qua). `ClientSecret` được bảo vệ qua `ISecretProvider` (Key Vault khi được cấu hình, nếu không thì văn bản thuần). Ánh xạ tên miền SSO được đăng ký tự động từ `AllowedDomains`, trừ với kết nối theo phạm vi tổ chức, mà tên miền chỉ được đối chiếu bên trong tổ chức của nó.

Phần nạp sẵn cũng có thể đặt mọi cờ hành vi trong bảng dưới đây. **Một mục nạp sẵn sẽ thay thế kết nối đã lưu ở mỗi lần khởi động**: cờ nào bạn bỏ trống sẽ trở về giá trị mặc định, nên hãy ghi rõ trong cấu hình mọi cờ bạn muốn giữ (`ConnectionName`, `IconUrl` và `OrganizationId` là những giá trị duy nhất còn lại khi bị bỏ trống, và `CreatedAt` được giữ nguyên).

| Trường | Mặc định | Tác dụng |
|---|---|---|
| `JitProvisioningEnabled` | `false` | Tạo người dùng liên kết chưa biết ở lần đăng nhập đầu tiên. Tắt nghĩa là người dùng chưa biết bị từ chối với `access_denied` |
| `AllowUninvitedJit` | `false` | Khi có khai báo `ProvisioningAttributeParams`, cũng cấp phát cả người dùng đến mà không mang ngữ cảnh đó. Xem [SSO tự phục vụ](self-service-sso) |
| `ProvisioningAttributeParams` | không có | Các khóa query của authorize request được ghi lên người dùng được cấp phát JIT dưới dạng thuộc tính cấp phát (chiều vào tương ứng với `PassthroughParams`) |
| `PassthroughParams` | không có | Các khóa query được chuyển tiếp lên authorize URL upstream, xem [Tham số query chuyển tiếp](#passthrough-query-parameters) |
| `SessionExpClaim` | không có | Xem [Giới hạn thời hạn phiên](#session-lifetime-cap) |
| `ShowOnLogin` | `true` | `false` ẩn nút "Tiếp tục với"; kết nối chỉ tới được qua `idp_hint` |
| `ChallengeMfaAfterLogin` | `true` | `false` tin vào MFA của chính upstream và bỏ qua thử thách cục bộ |
| `IsExternalConnection` | `false` | Đánh dấu một IdP bên thứ ba do khách hàng sở hữu. Nó vô hiệu hóa `UseUpstreamSubjectAsUserId` và `AutoLinkExistingByEmail` ngay cả khi chúng được đặt |
| `UseUpstreamSubjectAsUserId` | `false` | Id cục bộ của người dùng JIT là `sub` của upstream thay vì một GUID mới. Chỉ dành cho kết nối bên thứ nhất |
| `AutoLinkExistingByEmail` | `false` | Liên kết với một tài khoản cục bộ đã tồn tại theo email ngay cả khi `AllowedDomains` không bao gồm tên miền đó. Chỉ dành cho kết nối bên thứ nhất |
| `RevalidateOnRefresh` | `false` | Xem [Phiên liên kết](federated-sessions) |
| `InteractionPath` | không có | Đường dẫn trong ứng dụng đăng nhập được hiển thị trước khi liên kết một request `idp_hint` (phải bắt đầu bằng `/`) |
| `OrganizationId` | không có | Giới hạn kết nối trong một tổ chức, xem [SSO tự phục vụ](self-service-sso#organisation-scoped-connections) |

> **IdP nằm trong mạng riêng của bạn.** `MetadataLocation` phải là https và, theo mặc định, phải phân giải tới một địa chỉ định tuyến công khai được: Authagonal từ chối các đích nội bộ trên mọi URL mà nó tải, cả ở mức URL lẫn ở mức socket. Để liên kết với một IdP on-premises, hãy khai báo nó trong [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard). Điều đó bao trùm toàn bộ quá trình trao đổi, gồm cả `token_endpoint`, `userinfo_endpoint` và `jwks_uri` mà tài liệu discovery nêu ra. Vẫn bắt buộc https: tài liệu này cung cấp các khóa dùng để xác thực mọi `id_token` từ upstream, và một mạng riêng không phải là một kênh bảo mật.

**Cách B, Admin API (để quản lý lúc chạy):**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"],
    "jitProvisioningEnabled": true
  }'
```

Body khi tạo chấp nhận `connectionName`, `metadataLocation`, `clientId` và `clientSecret` (đều bắt buộc), cùng với `iconUrl`, `redirectUrl` (bị bỏ qua, tùy chọn), `organizationId`, `allowedDomains`, `passthroughParams`, `jitProvisioningEnabled` (mặc định `false`), `challengeMfaAfterLogin` (mặc định `true`) và `interactionPath`. Id kết nối do server tạo ra và được trả về trong body `201` (client secret không bao giờ được trả về). `metadataLocation` phải là https và được kiểm tra với cơ chế chặn tải ra ngoài ngay lúc tạo. Các cờ khác trong bảng trên (`SessionExpClaim`, `ShowOnLogin`, `IsExternalConnection`, `RevalidateOnRefresh` và phần còn lại) không thể đặt qua route tạo: hãy nạp sẵn chúng từ cấu hình hoặc ghi chúng qua `IOidcProviderStore` từ mã của host. Không có route cập nhật cho kết nối OIDC; để thay đổi, hãy xóa rồi tạo lại (hoặc sửa cấu hình nạp sẵn). `GET /api/v1/oidc/connections/{connectionId}` và `DELETE` hoàn thiện bộ route.

### 2. Định tuyến tên miền SSO {#2-sso-domain-routing}

Khi `AllowedDomains` được chỉ định (trong cấu hình hoặc qua API tạo), ánh xạ tên miền SSO được đăng ký tự động. Không có định tuyến tên miền, người dùng vẫn có thể được đưa tới đăng nhập OIDC qua `/oidc/{connectionId}/login`.

## Endpoint {#endpoints}

| Endpoint | Mô tả |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...&loginHint=...` | Bắt đầu đăng nhập OIDC. Tạo PKCE + state + nonce, suy ra scope upstream và các tham số chuyển tiếp từ `returnUrl`, chuyển hướng tới authorization endpoint của IdP (`loginHint`, nếu có, được gửi lên upstream dưới dạng `login_hint`). `404` với kết nối không xác định. |
| `GET /oidc/callback` | Xử lý callback từ IdP. Đổi code lấy token, xác thực id_token, ghi mọi claim không thuộc giao thức lên cookie dưới dạng `federated:*`, tạo/đăng nhập người dùng. |

## Luồng scope và claim đi qua {#scope-and-claim-flow-through}

Tập scope mà RP downstream yêu cầu tại `/connect/authorize` được chuyển tiếp tới IdP upstream, **được lọc còn tập OIDC tiêu chuẩn**: `openid`, `profile`, `email`, `address`, `phone`, trong đó `openid` luôn có mặt. Mọi thứ khác RP yêu cầu (scope API tùy chỉnh, `offline_access`, …) bị loại bỏ trước lời gọi upstream (ngoại lệ duy nhất là kết nối có `RevalidateOnRefresh`, vốn thêm lại `offline_access` để có thể lấy refresh token upstream): một IdP nghiêm ngặt như Google trả về `invalid_scope` với các giá trị không xác định, và upstream chỉ cần nhận diện người dùng; các scope riêng của RP được áp dụng trên token do Authagonal cấp, không phải token upstream. Bất kỳ claim nào mà IdP upstream đưa lên id_token theo scope sẽ quay về Authagonal, được cất trên ticket của cookie dưới dạng claim `federated:<name>`, và đi tiếp vào `OidcSubject.FederationClaims` ở lần đi qua `/connect/authorize` kế tiếp. Từ đó `ProtocolTokenService` phát lại chúng trên token do Authagonal cấp, được kiểm soát bởi cùng danh sách cho phép `Scope.UserClaims` vốn kiểm soát `CustomAttributes`. Khi trùng khóa, giá trị trong user store của chính Authagonal được ưu tiên: các claim này đến nguyên văn từ IdP upstream, nên nếu để chúng ghi đè thì một IdP do khách hàng kiểm soát có thể khẳng định lại bất kỳ claim nào được scope phát hành về chính người dùng của họ và lấn át bản ghi của server này. Một claim upstream không có bản tương ứng được lưu vẫn đi qua bình thường.

Kết quả thực tế: không cần danh sách cho phép claim theo từng kết nối. Mọi claim không thuộc giao thức mà upstream đặt lên id_token đều được ghi lại; claim nào trong số đó tới được token downstream do `UserClaims` của scope downstream quyết định, hãy khai báo claim ở đó và giá trị sẽ đi qua.

`FederationClaims` tồn tại qua các lần xoay vòng refresh tách biệt với `CustomAttributes`, nên ngữ cảnh liên kết theo từng phiên (ví dụ token của liên kết chia sẻ được ghi lại ở lần authorize ban đầu) vẫn nguyên vẹn trong khi các thuộc tính theo từng người dùng vẫn được đọc mới từ user store.

## Tham số query chuyển tiếp {#passthrough-query-parameters}

`OidcProviderConfig.PassthroughParams` là danh sách cho phép theo từng kết nối gồm các khóa query được chuyển từ request `/authorize` ban đầu sang authorize URL của IdP upstream. Tập tiêu chuẩn (`scope`, `state`, `nonce`, PKCE) luôn được chuyển tiếp; danh sách này dành cho các giá trị bổ sung do RP chỉ định, như một thông tin xác thực dùng một lần mà upstream cần để xác thực (ví dụ `link_token` cho các IdP liên kết chia sẻ).

Khi một khóa nằm trong danh sách cho phép, Authagonal lấy giá trị của nó từ query `/authorize` ban đầu (được mang qua `returnUrl`) và nối nó vào URL upstream. Mọi thứ không có trong danh sách cho phép bị loại bỏ một cách im lặng.

## Giới hạn thời hạn phiên {#session-lifetime-cap}

`OidcProviderConfig.SessionExpClaim` là tên tùy chọn của một claim trong id_token (giây Unix) mà giá trị của nó giới hạn thời hạn phiên cục bộ. Khi có mặt, giá trị upstream được mang theo dưới dạng `session_max_exp` trên ticket của cookie và vào authorization code được cấp; access / id / refresh token bị kẹp lại để không token nào, kể cả những token được mint từ các lần xoay vòng, tồn tại lâu hơn phiên upstream. Hữu ích khi IdP upstream áp đặt giới hạn phiên ngắn hơn mức Authagonal dùng theo mặc định.

## Tính năng bảo mật {#security-features}

- **PKCE**: code_challenge với S256 trên mọi authorization request
- **Xác thực nonce**: nonce được lưu cùng state, phải có mặt trong id_token và khớp
- **Xác thực state**: chỉ dùng một lần (được tiêu thụ nguyên tử qua `IOidcStateStore`, lưu bền vững kèm thời hạn) **và gắn với trình duyệt**: một cookie `SameSite=Lax` có phạm vi `/oidc` được đặt lúc đăng nhập và phải khớp với `state` trên callback, nên kẻ tấn công không thể hoàn tất một flow liên kết do chúng khởi tạo rồi đưa URL callback cho nạn nhân (login CSRF)
- **Xác thực chữ ký id_token**: khóa được tải từ JWKS endpoint của IdP; issuer, audience và thời hạn đều được xác thực
- **Phương án dự phòng userinfo**: nếu id_token không chứa email, userinfo endpoint sẽ được thử. `sub` của userinfo phải khớp với `sub` của id_token (OIDC Core 5.3.2), nếu không response bị bỏ qua
- **Liên kết danh tính ổn định**: người dùng quay lại được xác định theo provider + `sub`, không bao giờ chỉ theo email. Việc gắn một danh tính liên kết vào một tài khoản cục bộ **đã tồn tại từ trước** theo email đòi hỏi `AllowedDomains` của kết nối bao gồm tên miền của email đó (sự bảo đảm rõ ràng của quản trị viên rằng IdP sở hữu tên miền đó) hoặc `AutoLinkExistingByEmail` trên một kết nối bên thứ nhất, và bị từ chối khi tên miền được định tuyến tới một kết nối khác. Một tài khoản đã gắn với danh tính liên kết của một kết nối khác chỉ được tiếp nhận khi kết nối này là bên có thẩm quyền đối với tên miền, và khi đó liên kết cũ bị gỡ bỏ. Một `email_verified` do upstream khẳng định là *không* đủ để chiếm một tài khoản đã tồn tại
- **Áp đặt tên miền**: khi `AllowedDomains` được đặt, kết nối chỉ có thể khẳng định danh tính nằm trong các tên miền đó (nếu không thì `access_denied`)
- **JIT là tùy chọn bật**: trừ khi kết nối đặt `JitProvisioningEnabled`, người dùng chưa biết bị từ chối với `access_denied`. Khi JIT có áp dụng, một upstream không khẳng định `email_verified` không thể tạo tài khoản, và một kết nối có tên miền email được định tuyến tới kết nối khác cũng vậy
- **Chặn open redirect**: `returnUrl` phải là một đường dẫn tương đối cùng site; dạng protocol-relative (`//`) và dạng dấu gạch chéo ngược bị từ chối
- **MFA cục bộ vẫn áp dụng theo mặc định**: liên kết chỉ chứng minh yếu tố thứ nhất. Người dùng đã đăng ký MFA (hoặc có chính sách client yêu cầu MFA) được chuyển qua các trang thử thách/thiết lập MFA cục bộ sau callback thay vì được đăng nhập ngay; chỉ khi đó phiên mới mang dấu MFA. Một kết nối có `ChallengeMfaAfterLogin: false` bỏ qua bước này và đăng nhập người dùng như đã xác thực MFA chỉ dựa vào việc liên kết
- **Metadata chỉ được tin cậy trong phạm vi hẹp**: tài liệu discovery phải là https và URL của nó phải gắn với issuer mà nó nêu ra, và id_token từ upstream chỉ được chấp nhận với các thuật toán chữ ký bất đối xứng (RS/PS/ES 256, 384, 512)
- **Gắn với tổ chức**: người dùng đăng nhập qua một kết nối theo phạm vi tổ chức được đưa vào làm thành viên của tổ chức đó và phiên mang `org_id` của tổ chức

## Đặc thù của Azure AD {#azure-ad-specifics}

Azure AD đôi khi trả email dưới dạng một mảng JSON trong claim `emails` (đặc biệt với B2C). Authagonal xử lý trường hợp này bằng cách kiểm tra cả claim `email` lẫn mảng `emails` (một mảng JSON hoặc một chuỗi đơn).

## Các provider được hỗ trợ {#supported-providers}

Mọi provider tuân thủ OIDC có hỗ trợ:
- Flow Authorization Code
- PKCE (S256)
- Tài liệu discovery (`.well-known/openid-configuration`)

Đã được kiểm thử với:
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C

## Hướng dẫn liên quan {#related-guides}

- [SSO tự phục vụ](self-service-sso): các chế độ cấp phát JIT (chỉ theo lời mời so với tự phục vụ), cấp độ tin cậy của kết nối, và các trang chen giữa trước khi liên kết.
- [Phiên liên kết](federated-sessions): truyền việc thu hồi ở upstream xuống phiên cục bộ với `RevalidateOnRefresh`.
- [Nâng cấp người dùng](user-upgrade): cho phép một tài khoản liên kết / tài khoản khách nhận một mật khẩu bên thứ nhất.
