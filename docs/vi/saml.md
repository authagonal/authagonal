---
layout: default
title: SAML
locale: vi
---

# SAML 2.0 SP

Authagonal có sẵn một bản triển khai SAML 2.0 Service Provider tự xây dựng. Không dùng thư viện SAML của bên thứ ba: được xây dựng trên `System.Security.Cryptography.Xml.SignedXml` (một phần của .NET).

## Phạm vi {#scope}

- **SSO do SP khởi tạo** (người dùng bắt đầu tại Authagonal, được chuyển hướng tới IdP)
- **Binding HTTP-Redirect** cho AuthnRequest (có thể ký tùy chọn, xem bên dưới)
- **Binding HTTP-POST** cho Response (ACS)
- **Assertion được mã hóa** (`EncryptedAssertion`) được giải mã bằng một cặp khóa SP riêng cho từng kết nối
- **Single Logout** (do SP khởi tạo và do IdP khởi tạo, binding Redirect và POST)
- Azure AD / Entra ID là đích chính, nhưng mọi IdP tuân thủ đều hoạt động (tên thuộc tính của Okta, OneLogin, Ping, Google Workspace, ADFS, Shibboleth đều được xử lý)

### Không được hỗ trợ {#not-supported}

- Binding Artifact
- Mã hóa assertion bằng AES-GCM (hạn chế của `EncryptedXml` trong .NET; hãy cấu hình AES-CBC tại IdP, xem bên dưới)

**Đăng nhập do IdP khởi tạo vẫn hoạt động, và ô ứng dụng không cần cấu hình lại**, nhưng assertion không được yêu cầu không phải là thứ đăng nhập người dùng. Một Response không có `InResponseTo` bị loại bỏ, và ACS chuyển hướng trình duyệt tới `/saml/{connectionId}/login`, nơi phát hành một AuthnRequest mới gắn với trình duyệt đó. Người dùng đã được xác thực tại IdP, nên IdP trả lời ngay và vòng đi về này không hiển thị với người dùng; `RelayState` của IdP được mang theo làm URL quay về, nên người dùng vẫn tới đúng deep link mà ô ứng dụng đã được cấu hình.

Assertion phải bị loại bỏ vì chấp nhận một assertion không được yêu cầu sẽ cho phép bất kỳ ai có tài khoản tại IdP đó đăng nhập một phiên vào bất kỳ user-agent nào (mọi quy tắc của §4.1.4.3 đều được thỏa mãn bởi một assertion mà kẻ tấn công lấy được một cách hợp lệ cho chính tài khoản của mình), và vì việc yêu cầu cookie của request trên đường do SP khởi tạo chẳng có giá trị gì khi chính assertion đó vẫn có thể phát lại sau khi bị gỡ `InResponseTo`. Khởi động lại flow giữ cho ô ứng dụng hoạt động mà không chấp nhận bất kỳ điều nào trong số đó: người được đăng nhập cuối cùng là người mà IdP nêu tên trong lượt trao đổi *mới*.

Việc khởi động lại chỉ diễn ra một lần cho mỗi trình duyệt. Một IdP trả lời AuthnRequest bằng một Response không được yêu cầu khác sẽ bị từ chối với `error=saml_unsolicited` thay vì bị chuyển hướng lần nữa, nên một IdP cấu hình sai không thể tạo ra vòng lặp chuyển hướng.

Nếu muốn chấp nhận nguyên trạng assertion không được yêu cầu, hãy đặt `allowUnsolicitedResponses: true` trên kết nối (**tắt theo mặc định**). Khi bật, bước kiểm tra request ID được bỏ qua với các response không được yêu cầu nhưng việc chỉ dùng một lần của assertion ID vẫn được áp đặt (xem phần Bảo mật).

## Thiết lập Azure AD {#azure-ad-setup}

### 1. Tạo một SAML Provider {#1-create-a-saml-provider}

**Cách A: Cấu hình (khuyến nghị cho các thiết lập tĩnh)**

Thêm vào `appsettings.json`:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "acme-azure",
      "ConnectionName": "Acme Corp Azure AD",
      "EntityId": "https://auth.example.com/saml/acme-azure",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
      "AllowedDomains": ["acme.com"]
    }
  ]
}
```

Các provider được nạp sẵn lúc khởi động. `ConnectionId`, `EntityId` và `MetadataLocation` là bắt buộc với một kết nối mới (thiếu chúng thì khởi động thất bại). Ánh xạ tên miền SSO được đăng ký tự động từ `AllowedDomains`, trừ với kết nối theo phạm vi tổ chức, mà tên miền chỉ được đối chiếu bên trong tổ chức của nó. Một provider mới được nạp sẵn sẽ không có cặp khóa SP (nên không có AuthnRequest được ký, assertion được mã hóa hay thông điệp đăng xuất được ký); hãy dùng Admin API cho các tính năng đó.

Phần nạp sẵn cũng có thể đặt `OrganizationId`, `JitProvisioningEnabled` (mặc định `false`), `ChallengeMfaAfterLogin` (mặc định `true`), `ProvisioningAttributeParams`, `AllowUninvitedJit` và `AllowUnsolicitedResponses`. Việc nạp sẵn đọc kết nối đã lưu rồi gộp vào, nên một kết nối đã tồn tại vẫn giữ cặp khóa SP, metadata đã dán, định dạng NameID, `signAuthnRequests` và biểu tượng, những thứ mà phần nạp sẵn không có trường tương ứng. Các cờ hành vi nêu trên được ghi từ phần khởi tạo ở mỗi lần khởi động, nên cờ nào bạn bỏ trống sẽ trở về giá trị mặc định.

`EntityId` là **entity ID của SP của bạn** (định danh bạn đăng ký tại IdP), không phải entity ID của IdP.

> **IdP nằm trong mạng riêng của bạn.** `MetadataLocation` phải là https và, theo mặc định, phải phân giải tới một địa chỉ định tuyến công khai được: tài liệu metadata mang các chứng chỉ dùng để xác thực mọi assertion, và Authagonal từ chối các đích nội bộ trên mọi URL mà nó tải. Để liên kết với một IdP on-premises, hãy khai báo nó trong [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard). Nếu IdP hoàn toàn không công bố endpoint metadata https nào, hãy dán tài liệu vào `MetadataXml` qua Admin API.

**Cách B: Admin API (để quản lý lúc chạy)**

```bash
curl -X POST https://auth.example.com/api/v1/saml/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Acme Corp Azure AD",
    "entityId": "https://auth.example.com/saml/acme-azure",
    "metadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
    "allowedDomains": ["acme.com"]
  }'
```

API tạo ra `connectionId` (một GUID) và trả nó trong header `Location` cùng body của response. Các trường tùy chọn bổ sung: `metadataXml` (metadata đã dán, xem bên dưới), `nameIdFormat` (xem bên dưới), `signAuthnRequests` (bắt buộc ký AuthnRequest), `iconUrl` (biểu tượng của nút đăng nhập), `jitProvisioningEnabled` (tự động tạo người dùng chưa biết ở lần đăng nhập đầu tiên; **tắt theo mặc định**, nên người dùng chưa biết bị từ chối cho tới khi bạn đặt nó), `challengeMfaAfterLogin` (mặc định `true`; `false` tin vào MFA của chính IdP), `provisioningAttributeParams` và `allowUninvitedJit` (xem [SSO tự phục vụ](self-service-sso)), `organizationId` (giới hạn kết nối trong một tổ chức, xem [SSO tự phục vụ](self-service-sso#organisation-scoped-connections)), `allowUnsolicitedResponses` (chấp nhận nguyên trạng một assertion do IdP khởi tạo thay vì khởi động lại flow; tắt theo mặc định, xem ở trên). Các kết nối tạo qua API cũng nhận một cặp khóa SP được tạo tự động (xem phần Cặp khóa SP bên dưới).

Kết nối được quản lý qua `POST` / `GET` / `PUT` / `DELETE` trên `/api/v1/saml/connections[/{connectionId}]`. `PUT` là cập nhật một phần: chỉ các trường được gửi lên mới bị thay đổi.

### 2. Cấu hình Azure AD {#2-configure-azure-ad}

1. Trong Azure AD → Enterprise Applications → New Application → Create your own
2. Thiết lập Single Sign-On → SAML
3. **Identifier (Entity ID):** `https://auth.example.com/saml/acme-azure`
4. **Reply URL (ACS):** `https://auth.example.com/saml/acme-azure/acs`
5. **Sign on URL:** `https://auth.example.com/saml/acme-azure/login`

### 3. Định tuyến tên miền SSO {#3-sso-domain-routing}

Khi `AllowedDomains` được chỉ định (trong cấu hình hoặc qua API tạo), ánh xạ tên miền SSO được đăng ký tự động. Khi người dùng nhập `user@acme.com` trên trang đăng nhập, SPA phát hiện SSO là bắt buộc và hiển thị "Tiếp tục với SSO". Một tên miền chỉ có thể được ánh xạ tới một kết nối; API từ chối tên miền đã được một kết nối khác nhận.

Bạn cũng có thể quản lý tên miền lúc chạy qua Admin API; xem [Admin API](admin-api).

## Metadata XML được dán {#pasted-metadata-xml}

Một số IdP không công bố URL metadata (Google Workspace), hoặc endpoint metadata của chúng không truy cập được từ SP (ADFS trong mạng riêng). Với những trường hợp đó, hãy dán tài liệu metadata: cung cấp `metadataXml` khi tạo/cập nhật. Phải cung cấp đúng một trong hai `metadataLocation` hoặc `metadataXml`; cung cấp một trong hai khi cập nhật sẽ xóa cái còn lại.

Metadata được dán sẽ được kiểm tra lúc lưu và **rút gọn** (`SamlMetadataParser.Condense`) thành một `EntityDescriptor` tối giản chuẩn tắc chỉ chứa đúng những gì SP sử dụng: entityID, các chứng chỉ ký, SSO endpoint, SLO endpoint nếu có, và cờ `WantAuthnRequestsSigned`. Tài liệu của nhà cung cấp có thể vượt 100KB (`FederationMetadata.xml` của ADFS), quá giới hạn 64KB cho mỗi thuộc tính của Azure Table, trong khi phần SP sử dụng chỉ vài KB. Nội dung dán không phân tích được bị từ chối với 400; tài liệu phải chứa một `IDPSSODescriptor` có chứng chỉ ký và một `SingleSignOnService`.

## Định dạng NameID {#nameid-format}

Trường `nameIdFormat` kiểm soát Format của `NameIDPolicy` được yêu cầu trong AuthnRequest:

| Giá trị | Hành vi |
|---|---|
| bỏ trống / null | `urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress` (mặc định từ trước tới nay) |
| `"none"` | Lược bỏ hoàn toàn phần tử `NameIDPolicy`. Thiết lập an toàn cho ADFS: ADFS làm thất bại toàn bộ lần đăng nhập (MSIS7070) khi các quy tắc claim của nó không phát ra định dạng được yêu cầu. |
| bất kỳ giá trị nào khác | Được gửi nguyên văn làm URN của Format (phải bắt đầu bằng `urn:`) |

Khi cập nhật, `""` đặt lại về mặc định emailAddress. Metadata của SP quảng bá định dạng mà kết nối yêu cầu (và lược bỏ `NameIDFormat` khi được đặt là `"none"`).

## Endpoint {#endpoints}

| Endpoint | Mô tả |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...&loginHint=...` | Bắt đầu SSO do SP khởi tạo. Dựng một AuthnRequest (được ký khi áp dụng) và chuyển hướng tới IdP. `loginHint` được truyền dưới dạng `login_hint` cho các IdP có hỗ trợ (Entra, Google). |
| `POST /saml/{connectionId}/acs` | Assertion Consumer Service. Nhận SAML Response, xác thực nó, tạo/đăng nhập người dùng. |
| `GET /saml/{connectionId}/metadata` | Metadata XML của SP dùng để cấu hình IdP. |
| `GET /saml/{connectionId}/logout?returnUrl=...` | Single Logout do SP khởi tạo. Kết thúc phiên cục bộ, rồi gửi một LogoutRequest tới IdP khi IdP hỗ trợ SLO. |
| `GET/POST /saml/{connectionId}/slo` | Endpoint Single Logout. Nhận các LogoutRequest do IdP khởi tạo (binding Redirect hoặc POST) và chặng LogoutResponse của SLO do SP khởi tạo. |

URL quay về sau đăng nhập được mang ở phía server trên AuthnRequest đã lưu (theo khóa request ID), không phải trong RelayState: đặc tả SAML giới hạn RelayState ở 80 byte và một số IdP cắt cụt nó. RelayState chỉ được xem xét với các flow do IdP khởi tạo.

## Cặp khóa SP và assertion được mã hóa {#sp-keypair--encrypted-assertions}

Mọi kết nối tạo qua API đều nhận một cặp khóa SP được tạo tự động: một chứng chỉ RSA 2048-bit tự ký (hiệu lực 10 năm), được lưu dưới dạng PKCS#12 và được bảo vệ khi lưu trữ bởi secret provider của host. Nó chỉ nằm ở server và không bao giờ được API trả về. Cặp khóa này cho phép:

- **AuthnRequest được ký** (ký query `SigAlg`/`Signature` của binding redirect). Việc ký tự động bật khi metadata của IdP khai báo `WantAuthnRequestsSigned`, hoặc luôn bật khi kết nối đặt `signAuthnRequests: true`.
- **Giải mã assertion được mã hóa.** Khi metadata của SP quảng bá một chứng chỉ mã hóa, ADFS bắt đầu mã hóa assertion theo mặc định; ACS giải mã chúng bằng khóa riêng của SP và đưa assertion đã giải mã qua cùng quy trình kiểm tra chữ ký/điều kiện như một assertion văn bản thuần. Được hỗ trợ: vận chuyển khóa RSA-OAEP (SHA-1/SHA-256); mã hóa dữ liệu AES-128/192/256-CBC và 3DES. **Vận chuyển khóa RSA-1.5 bị từ chối** (việc mở gói PKCS#1 v1.5 là một oracle Bleichenbacher/ROBOT) và **AES-GCM không được hỗ trợ** (hạn chế của `EncryptedXml` trong .NET). Hãy cấu hình IdP dùng RSA-OAEP và AES-CBC. Cả hai lỗi đều trả về cùng một thông báo cố định ("Could not decrypt the assertion."), một cách có chủ ý: nêu tên thuật toán hay giai đoạn bị lỗi chính là điều tạo ra oracle, nên hãy chẩn đoán từ cấu hình của IdP thay vì từ thông báo lỗi.
- **Thông điệp đăng xuất được ký** (LogoutRequest/LogoutResponse trên binding redirect).

Metadata của SP công bố chứng chỉ dưới dạng cả `KeyDescriptor` `signing` lẫn `encryption`, và đặt `AuthnRequestsSigned="true"` khi kết nối bắt buộc ký.

## Single Logout {#single-logout}

ACS ghi phiên SAML lên cookie xác thực (các claim `saml_connection`, `saml_name_id`, `saml_name_id_format`, `saml_session_index`) để việc đăng xuất có thể gắn ngược lại với phiên tại IdP.

- **Do SP khởi tạo:** `GET /saml/{connectionId}/logout` luôn kết thúc phiên cookie cục bộ trước (người dùng đã yêu cầu đăng xuất; SLO tại IdP chỉ là cố gắng hết mức). Nếu phiên của trình duyệt đến từ kết nối này và metadata của IdP quảng bá một `SingleLogoutService`, một LogoutRequest (NameID + SessionIndex, được ký khi SP có khóa) được gửi qua binding redirect; LogoutResponse của IdP quay về `/slo`, nơi đưa người dùng tới `returnUrl` đã lưu. Các IdP không có SLO endpoint (Google) chỉ được đăng xuất cục bộ.
- **Do IdP khởi tạo:** IdP gửi một LogoutRequest tới `/saml/{connectionId}/slo` (binding Redirect GET hoặc POST). Request được ký sẽ được xác thực với các chứng chỉ trong metadata của IdP. **Một LogoutRequest không được ký hoặc không xác minh được bị từ chối với 400** trước khi bất kỳ phiên nào được xem xét. Không có phương án dự phòng theo phạm vi phiên: một trang bên thứ ba điều hướng trình duyệt *của nạn nhân* tới đây sẽ cung cấp phiên của nạn nhân, không phải của kẻ tấn công, nên việc giới hạn phương án dự phòng trong phiên hiện tại cũng không hạn chế được ai có thể bị đăng xuất. Dù sao Profiles §4.4.3.1 cũng yêu cầu IdP ký LogoutRequest trên binding Redirect hoặc POST, và metadata của kết nối đã cung cấp sẵn các chứng chỉ, nên việc từ chối một request không được ký không gây thiệt hại gì cho IdP tuân thủ. Một LogoutResponse được ký sẽ được trả về khi IdP có SLO endpoint. Chỉ qua front-channel: thông điệp đến trong trình duyệt của người dùng, nên việc kết thúc phiên cookie đăng xuất đúng trình duyệt đó.

## Cache metadata và chuyển đổi chứng chỉ {#metadata-caching--cert-rollover}

- Metadata của IdP tải từ `MetadataLocation` được cache trong bộ nhớ 60 phút (cấu hình được qua `Cache:SamlMetadataCacheMinutes`), theo khóa là URL metadata (không phải ID kết nối, nên không thể xảy ra nhầm lẫn cache giữa các tenant).
- Metadata được dán được cache theo nội dung (hash của XML) và không bao giờ được tải lại.
- **Tải lại khi chữ ký thất bại:** một lỗi xác thực chữ ký ngay sau khi IdP chuyển đổi chứng chỉ có nghĩa là metadata trong cache đã cũ. Khi gặp đúng lỗi đó, mục cache bị loại bỏ và metadata được tải lại một lần, rồi việc xác thực được thử lại, với thời gian chờ 5 phút cho mỗi vị trí metadata để một assertion rác không thể bị dùng để dội request vào endpoint metadata của IdP. Không có cơ chế này, một lần chuyển đổi chứng chỉ sẽ làm đăng nhập thất bại cho tới khi TTL của cache hết hạn. (Chỉ áp dụng cho metadata tải từ URL; metadata được dán không có gì để tải lại.)

## Khả năng tương thích với Azure AD {#azure-ad-compatibility}

| Hành vi của Azure AD | Cách xử lý |
|---|---|
| Chỉ ký assertion (mặc định) | Xác thực chữ ký trên phần tử Assertion |
| Chỉ ký response | Xác thực chữ ký trên phần tử Response |
| Ký cả hai | Xác thực cả hai chữ ký |
| SHA-256 (mặc định) | Hỗ trợ SHA-256 và SHA-1 |
| NameID: emailAddress | Lấy email trực tiếp |
| NameID: persistent (mờ) | Dự phòng bằng claim email từ các thuộc tính |
| NameID: unspecified | Dự phòng bằng claim email từ các thuộc tính |
| NameID: transient | Thay đổi ở mỗi lần đăng nhập, nên không bao giờ được dùng làm khóa liên kết. Thuộc tính object-id ổn định của IdP được dùng thay thế; nếu không có thuộc tính nào như vậy được khẳng định, lần đăng nhập bị từ chối kèm một lỗi có hướng dẫn khắc phục (cấu hình NameID dạng persistent hoặc emailAddress, hoặc khẳng định một thuộc tính object-id). |

## Ánh xạ thuộc tính {#attribute-mapping}

Các thuộc tính được lập chỉ mục không phân biệt hoa thường theo cả `Name` lẫn `FriendlyName` (Okta và Shibboleth phát ra Name dạng OID kèm FriendlyName dễ đọc; việc khớp với một trong hai là điều giúp ánh xạ của các nhà cung cấp hoạt động). Mỗi trường thử một danh sách bí danh theo thứ tự; bí danh đầu tiên là claim URI của Microsoft, nên hành vi với Entra/ADFS không thay đổi, và phần còn lại bao gồm các tên dễ đọc và tên OID mà Okta, OneLogin, Ping, Google và Shibboleth phát ra theo mặc định:

| Trường | Tên thuộc tính được chấp nhận |
|---|---|
| email | `.../claims/emailaddress`, `email`, `mail`, `emailaddress`, `urn:oid:0.9.2342.19200300.100.1.3` |
| firstName | `.../claims/givenname`, `givenName`, `given_name`, `firstName`, `first_name`, `urn:oid:2.5.4.42` |
| lastName | `.../claims/surname`, `sn`, `surname`, `lastName`, `last_name`, `familyName`, `family_name`, `urn:oid:2.5.4.4` |
| displayName | `http://schemas.microsoft.com/identity/claims/displayname`, `displayName`, `urn:oid:2.16.840.1.113730.3.1.241`, `cn`, `urn:oid:2.5.4.3` |
| objectId | `http://schemas.microsoft.com/identity/claims/objectidentifier`, `objectGUID`, `user.objectid` |
| groups | `.../claims/groups`, `groups`, `memberOf`, `.../claims/role`, `urn:oid:1.3.6.1.4.1.5923.1.5.1.1` |

(`.../claims/...` là dạng viết tắt của URI đầy đủ `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/...` hoặc `http://schemas.microsoft.com/ws/2008/06/identity/claims/...`.)

Thứ tự ưu tiên khi xác định email: thuộc tính email tường minh (bất kỳ bí danh nào) → NameID khi định dạng của nó là emailAddress → claim `name` nếu nó chứa `@` → từ chối (email là bắt buộc).

**Nhóm có nhiều giá trị:** mọi phần tử `AttributeValue` đều được ghi nhận (mỗi phần tử cho một nhóm thành viên), không chỉ phần tử đầu tiên.

## Cấp phát JIT {#jit-provisioning}

Cấp phát JIT **tắt theo mặc định**. Một kết nối có `jitProvisioningEnabled: true` tự động tạo người dùng chưa biết ở lần đăng nhập đầu tiên (email, tên/họ từ assertion, email được đánh dấu đã xác nhận) và liên kết họ với kết nối theo danh tính liên kết ổn định của họ (`saml:{connectionId}` + NameID, hoặc object-id với NameID dạng transient). Không có cờ này, người dùng chưa biết bị từ chối. Một kết nối khai báo `provisioningAttributeParams` còn đòi hỏi ngữ cảnh lời mời đó trong lần đăng nhập, trừ khi `allowUninvitedJit` được đặt; xem [SSO tự phục vụ](self-service-sso). Người dùng quay lại được khớp trước hết theo liên kết danh tính, không bao giờ chỉ theo email; một tài khoản cục bộ đã tồn tại chỉ được gắn theo email khi `AllowedDomains` của kết nối bao gồm tên miền của email đó (tuyên bố rõ ràng của quản trị viên rằng IdP này sở hữu tên miền), ngăn chặn việc chiếm đoạt tài khoản qua một IdP giả mạo.

## Thời hạn phiên {#session-lifetime}

Nếu `AuthnStatement` của assertion mang `SessionNotOnOrAfter`, đó là giới hạn trên do chính IdP đặt cho phiên mà nó vừa thiết lập, và Authagonal tuân thủ nó. Cookie đăng nhập hết hạn không muộn hơn thời điểm đó (khi nó nằm trong vòng 30 ngày), và cùng giới hạn đó được mang trên phiên dưới dạng `session_max_exp`, kẹp mọi access token, ID token và refresh token được cấp từ phiên. Một assertion không có `SessionNotOnOrAfter` không áp đặt thêm giới hạn nào. SAML không có refresh token upstream, nên đây là cách duy nhất để IdP giới hạn một phiên sau khi đăng nhập; với kết nối OIDC, xem [Phiên liên kết](federated-sessions).

## Bảo mật {#security}

- **Chống phát lại:** với các flow do SP khởi tạo, `InResponseTo` được kiểm tra với một request ID đã lưu (chỉ dùng một lần). Độc lập với điều đó, ID của mọi assertion được chấp nhận đều được lưu và áp đặt chỉ dùng một lần, điều này cũng bao trùm các response do IdP khởi tạo và các response đã bị gỡ `InResponseTo` (assertion ID nằm bên trong assertion được ký, nên không thể thay đổi mà không làm hỏng chữ ký).
- **Độ lệch đồng hồ:** dung sai 5 phút cho NotBefore/NotOnOrAfter
- **Giới hạn tuổi của assertion:** một assertion được trình ra muộn hơn một giờ (cộng độ lệch) so với `IssueInstant` của chính nó bị từ chối bất kể `NotOnOrAfter` nói gì, và một `IssueInstant` ở tương lai cũng bị từ chối
- **Issuer, destination và audience:** `Issuer` của Response và Assertion phải bằng entity ID của IdP trong kết nối, một Response được ký phải mang `Destination` khớp với URL ACS này, và audience phải là entity ID của SP trong kết nối này
- **Hiệu lực chứng chỉ của IdP:** một chứng chỉ ký của IdP đã được ghim mà nằm ngoài khoảng `NotBefore`/`NotAfter` của chính nó (độ lệch 5 phút) sẽ bị bỏ qua, với cả assertion lẫn chữ ký đăng xuất của binding Redirect, nên hãy làm mới metadata sau một lần chuyển đổi chứng chỉ
- **Chống tấn công wrapping:** Reference URI của chữ ký phải khớp với ID của phần tử được ký
- **Chống open redirect:** URL quay về sau đăng nhập phải là một đường dẫn tương đối từ gốc (bắt đầu bằng `/`, không có `//`, không có dấu gạch chéo ngược, vì trình duyệt coi `\` là `/`)
- **Bảo đảm tên miền:** khi `AllowedDomains` được cấu hình, assertion cho email nằm ngoài các tên miền đó bị từ chối, nên một kết nối không thể khẳng định tên miền của kết nối khác hay email của một người dùng cục bộ
- **MFA:** liên kết chỉ chứng minh yếu tố thứ nhất. Nếu chính sách hiệu lực của người dùng yêu cầu MFA, lần đăng nhập đi qua thử thách/thiết lập MFA cục bộ thay vì cấp một phiên đã xác thực đầy đủ, trừ khi kết nối đặt `challengeMfaAfterLogin: false`.
