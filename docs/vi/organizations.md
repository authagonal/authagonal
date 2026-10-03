---
layout: default
title: Tổ chức
nav_order: 14
locale: vi
---

# Tổ chức

Một tổ chức là một khách hàng bên trong tenant của bạn. Một bản triển khai có thể phục vụ nhiều tổ chức: mỗi tổ chức có danh tính riêng, thành viên riêng, và `org_id` riêng trên các token mà ứng dụng của bạn nhận được.

## Tổng quan {#overview}

Trước khi có tổ chức, bản ghi người dùng mang một chuỗi `OrganizationId` (do cấp phát TCC hoặc do việc gắn SCIM token ghi vào) và chuỗi đó được phát ra dưới dạng claim `org_id`. Không có nơi nào để nói tổ chức đó *là* gì, ai thuộc về nó, hay liệu có thể xác thực với tư cách tổ chức đó hay không.

Một `Organization` cho nó một bản ghi: một id mờ bất biến, một slug bất biến và duy nhất trong tenant, một tên hiển thị, một cờ bật/tắt, một túi metadata, và một phần ghi đè thương hiệu. Một `OrganizationMembership` ghi nhận ai thuộc về tổ chức, và chính nó mới là thứ thực sự cho phép phát hành token cho tổ chức đó.

**Tính năng này dùng để làm gì.** Một ISV có sản phẩm được triển khai riêng cho từng khách hàng (một instance ứng dụng, một cơ sở dữ liệu, được phân giải theo hostname) đăng ký một tenant và một tổ chức cho mỗi khách hàng. Ứng dụng của nó đọc `org_id` từ access token và từ chối mọi thứ không thuộc về instance mà nó đang phục vụ. Quyết định định tuyến mà trước đây ứng dụng tự đưa ra giờ do máy chủ ủy quyền đưa ra, và được chứng minh bằng một claim có chữ ký.

**Tenant vẫn là ranh giới cô lập.** Một khóa ký, một issuer, một user store. Tổ chức phân chia danh tính *bên trong* ranh giới đó; nó không tạo ra ranh giới thứ hai. Hai tổ chức trong cùng một tenant dùng chung một thư mục người dùng, và một người dùng có thể thuộc nhiều tổ chức.

**Chưa được hỗ trợ:**

- **Không có bộ chọn tổ chức.** Một người dùng thuộc nhiều tổ chức, trên một request không nêu tổ chức nào, sẽ nhận `account_selection_required`, một lỗi mà relying party có thể xử lý bằng cách thử lại kèm một tham số. Không có màn hình được host nào yêu cầu họ chọn.
- **Không có quản trị tổ chức được ủy quyền.** Không có quyền nào cho phép quản trị viên của chính khách hàng quản lý thành viên của họ.
- **Không có cô lập SCIM theo phạm vi tổ chức.** Một SCIM token gắn với một tổ chức (`ScimToken.OrganizationId`) gắn nhãn cho những người dùng mà nó tạo ra và, khi id đó trỏ tới một tổ chức có thật, biến họ thành thành viên đang hoạt động (xem [Tư cách thành viên từ một SCIM token](#membership-from-a-scim-token)). Các phép kiểm tra quyền sở hữu vẫn dựa trên OAuth client, không dựa trên tổ chức, nên việc gắn quyết định việc gắn nhãn chứ không quyết định quyền truy cập.
- **Không có luồng mời trong thư viện này.** Không có endpoint mời và không có email mời. Host tự ghi một tư cách thành viên `invited`; các cách để nó trở thành `active` nằm ở mục [Lời mời](#invitations).
- **Không có nhóm theo phạm vi tổ chức.** Claim `groups` và tư cách thành viên nhóm SCIM vẫn áp dụng trên toàn tenant; chỉ vai trò mới theo phạm vi tổ chức.
- **Không có sự kiện webhook cho tổ chức, và không có audit theo phạm vi tổ chức.** `IAuthHook` không có sự kiện vòng đời tổ chức (tạo, cấp hoặc thu hồi tư cách thành viên), các payload hook hiện có không mang `organizationId`, và audit log không có cột hay chỉ mục nào cho tổ chức.
- **Các scope giới hạn theo vai trò được lọc theo vai trò của tenant tại authorize.** `Scope.AllowedRoles` được áp dụng trên `/connect/authorize` dựa vào các vai trò được gán trực tiếp cho tài khoản, trước khi tổ chức được phân giải, nên một scope mà `AllowedRoles` chỉ được thỏa mãn bởi một vai trò theo phạm vi tổ chức sẽ bị loại bỏ tại authorize, hoặc bị từ chối với `access_denied` nếu không còn scope được yêu cầu nào. Khi refresh, cùng phép kiểm tra đó chạy với các vai trò của chủ thể đã phân giải, vốn có bao gồm vai trò của tổ chức. Cho tới khi hai bên thống nhất, hãy giới hạn scope theo vai trò của tenant.
- **Không có thương hiệu tổ chức trong thư viện này.** `Organization.BrandingJson` được lưu để host trộn đè lên thương hiệu của tenant; không có gì trong thư viện này đọc nó. Ứng dụng đăng nhập hiển thị tên tổ chức ("Đang đăng nhập vào {name}") khi boot payload của host mang một `organization` (`{ id, slug, name }`); bản thân thư viện không phân giải tổ chức nào trước khi xác thực, ngoại trừ qua tham số `organization`, một giới hạn client chỉ có một mục, một kết nối theo phạm vi tổ chức, hoặc `ITenantContext.OrganizationId` của host.
- **Không có REST API quản trị tổ chức.** `IOrganizationStore` và `IOrganizationMembershipStore` là bề mặt có sẵn; host nào muốn có endpoint thì tự xây dựng. Host liệt kê tổ chức hoặc thành viên nên dùng `ListPageAsync` / `ListByOrganizationPageAsync` (bên dưới).

## Tạo một tổ chức {#creating-an-organization}

Tổ chức được lưu qua `IOrganizationStore`. Cần có một bản cài đặt lưu trữ bền vững trước khi có thể tạo bất kỳ tổ chức nào. Mặc định dựng sẵn là rỗng và chỉ đọc, và từ chối thao tác ghi bằng một thông báo nêu tên đăng ký còn thiếu. Điều đó là có chủ đích: bản ghi tổ chức quyết định việc phát hành token, và một dictionary cục bộ trong tiến trình sẽ tiếp tục phát token trên mọi node chưa thấy lệnh thu hồi.

```csharp
await organizationStore.UpsertAsync(new Organization
{
    Id = "org_7f3a",              // opaque, immutable, emitted as org_id
    Slug = "international-sos",   // tenant-unique, immutable, emitted as org_slug
    DisplayName = "International SOS",
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Slug` phải khớp `^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$`: từ 1 đến 64 ký tự gồm chữ thường, chữ số và dấu gạch nối ở giữa, không có dấu gạch nối ở đầu hoặc cuối. Phải là chữ thường vì tham số `organization` được chuyển thành chữ thường trước khi tra cứu slug, nên một slug có chữ hoa sẽ là một giá trị mà không request nào phân giải được.

`Slug` phải là duy nhất trong tenant, và **id và slug dùng chung một không gian tên**: store từ chối một lần upsert có slug đã thuộc về một tổ chức khác, và cũng từ chối lần upsert có slug trùng với id của một tổ chức khác, hoặc có id trùng với slug của một tổ chức khác. Hai bản ghi cùng trả lời cho một giá trị sẽ khiến tham số `organization` chỉ một tổ chức trong khi mọi id đã lưu lại chỉ một tổ chức khác.

`Id` phải khớp `^[A-Za-z0-9._~-]{1,200}$`: cùng dạng với tham số `organization`, để mọi id luôn có thể được gửi dưới dạng tham số đó. Một id nằm ngoài dạng này là id mà không request nào chọn được, và một client bị giới hạn vào id đó sẽ từ chối mọi request.

`Id` **nên** chứa ít nhất một ký tự mà slug không được phép có: một chữ hoa, `.`, `_` hoặc `~`. Id và slug dùng chung một không gian tra cứu, và một giá trị toàn chữ thường được phân giải theo slug trước, nên một id có dạng giống slug là id mà sau này có thể bị từ chối khi tạo vì ai đó đã chiếm slug đó, còn một id chứa ký tự không hợp lệ cho slug thì không bao giờ gặp chuyện này. Dạng khuyến nghị cho id mới là một giá trị mờ có tiền tố `org_` (`org_7f3a9c`): `_` không hợp lệ cho slug, nên chỉ riêng tiền tố đã bảo đảm điều đó. Không có quy ước nào bị bắt buộc, và các giá trị đang có trong trường này là tùy ý: chúng đến từ phản hồi TCC `/try` của một ứng dụng downstream (`TccProvisioningOrchestrator`) hoặc từ việc người vận hành gắn SCIM token (`ScimToken.OrganizationId`, được `ScimUserEndpoints` gán lên người dùng mới).

Cả `Id` và `Slug` trên thực tế đều bất biến. Các relying party so sánh chúng với instance mà mình đang phục vụ và sẽ ghi cứng chúng, nên thay đổi một trong hai là một sự cố ngừng hoạt động không kèm thông báo lỗi nào. `DisplayName` có thể thay đổi tự do và là thứ được hiển thị trên màn hình.

## Cấp tư cách thành viên {#granting-membership}

```csharp
await membershipStore.UpsertAsync(new OrganizationMembership
{
    OrganizationId = "org_7f3a",
    UserId = user.Id,
    Status = MembershipStatus.Active,
    JoinedAt = DateTimeOffset.UtcNow,
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Status` là `invited`, `active` hoặc `suspended`. **Chỉ `active` mới cho phép phát hành token.** Tạm ngưng thay vì xóa sẽ giữ lại hồ sơ ai đã mời ai.

### Lời mời {#invitations}

Một bản ghi `invited` mang `InvitedByUserId`, `InvitedAt`, và các vai trò được đề nghị cho người được mời. Thư viện không bao giờ gửi lời mời; nó nâng bản ghi lên `active` (giữ nguyên vai trò, người mời và thời điểm mời, và gán `JoinedAt`) ở hai chỗ:

- **Đăng nhập qua một kết nối SAML hoặc OIDC theo phạm vi tổ chức.** Việc IdP của chính tổ chức đó xác nhận cho người đó đồng nghĩa với chấp nhận lời mời (`FederatedOrganizationBinding`, 0.30.2). Nếu không có điều này, một người được mời chỉ luôn đăng nhập qua SSO sẽ bị phép kiểm tra tư cách thành viên từ chối cấp token.
- **Tư cách thành viên tự động** (bên dưới), khi người dùng đủ điều kiện.

Một bản ghi `suspended` không bao giờ được nâng cấp bởi cả hai đường này và không bao giờ bị thay đổi bởi một lần đăng nhập.

### Tên miền đã xác minh và tư cách thành viên tự động {#verified-domains-and-automatic-membership}

`Organization.Domains` chứa các tên miền email mà tổ chức đã khai báo sở hữu, mỗi tên miền là một `OrganizationDomain { Domain, VerificationToken, CreatedAt, VerifiedAt }`. `Domain` được lưu ở dạng chữ thường, đã cắt khoảng trắng, không có dấu chấm ở cuối. Thư viện lưu lời khai báo và đọc `VerifiedAt`; việc chứng minh quyền kiểm soát (thường là một bản ghi DNS TXT mang `VerificationToken`) là việc của host, và host gán `VerifiedAt` khi việc này thành công.

`Organization.AllowAutoMembership` (mặc định tắt) cho phép người dùng tham gia mà không cần lời mời. Khi tổ chức được chọn **một cách tường minh** (một refresh grant được mang theo, tham số `organization`, một kết nối theo phạm vi tổ chức, hoặc một client bị giới hạn vào đúng tổ chức đó) và người dùng chưa có tư cách thành viên đang hoạt động, họ được biến thành thành viên nếu **tất cả** các điều kiện sau đều đúng:

- tổ chức đang được bật và `AllowAutoMembership` đang bật,
- `AuthUser.EmailConfirmed` là true,
- phần email sau ký tự `@` cuối cùng, chuyển thành chữ thường, bằng **chính xác** một tên miền có `VerifiedAt` được đặt. Một `acme.com` đã xác minh không chấp nhận `user@eu.acme.com`.

Nếu chưa có bản ghi, một bản ghi `active` không có vai trò nào sẽ được tạo; bản ghi `invited` được nâng cấp; bản ghi `suspended` không bao giờ bị động tới. Cơ chế này áp dụng tại authorize và ở mọi lần refresh, nên khi cờ đang bật, việc xóa bản ghi của một thành viên đủ điều kiện không ngăn được họ (họ sẽ tham gia lại ở token tiếp theo); hãy tạm ngưng họ thay vào đó. Một tổ chức chỉ được thừa hưởng từ `AuthUser.OrganizationId` không bao giờ tự động kết nạp. Mỗi lần tự động kết nạp được ghi log ở mức Information.

### Tư cách thành viên từ một SCIM token {#membership-from-a-scim-token}

Một SCIM token được tạo với `organizationId` sẽ gán giá trị đó làm `org_id` cho mọi người dùng mà nó tạo ra. Khi id đó trỏ tới một tổ chức đang tồn tại, thao tác tạo cũng ghi một tư cách thành viên `active` không có vai trò nào và ghi audit `scim.organization_member_added`. Một id không trỏ tới tổ chức nào vẫn chỉ là một nhãn đơn thuần. Chỉ khi tạo: một lần đồng bộ sau đó không gắn nhãn lại hay thêm thành viên. Xem [SCIM](scim#tagging-a-connectors-users-with-an-organization).

### Liệt kê và xóa {#listing-and-deleting}

`IOrganizationStore.ListPageAsync(cursor, limit)` và `IOrganizationMembershipStore.ListByOrganizationPageAsync(organizationId, cursor, limit)` trả về một trang (`Items` và một `NextCursor` mờ, là null ở trang cuối). `limit` bị kẹp trong khoảng 1..200 và một cursor sai định dạng sẽ ném `ArgumentException`. Cursor dựa trên keyset, nên một bản ghi được thêm hoặc xóa giữa các lần đọc không bao giờ làm lệch hay lặp lại một trang. Cả hai đều có bản cài đặt mặc định dựa trên các phép liệt kê không phân trang, nên một store tùy chỉnh vẫn biên dịch được; các store Azure Table ghi đè chúng bằng một truy vấn khoảng phía máy chủ.

Xóa một người dùng qua `DELETE /api/v1/profile/{userId}` của admin, `DELETE /scim/v2/Users/{id}` của SCIM hoặc đường thu hồi lại của SCIM cũng xóa mọi tư cách thành viên mà người đó nắm giữ (`AccountArtefactPurge.PurgeAsync` với một `IOrganizationMembershipStore`; overload ba store không xóa tư cách thành viên nào). Host có đường xóa riêng cũng phải truyền membership store vào, nếu không các tổ chức sẽ tiếp tục liệt kê thành viên đã bị xóa.

## Vai trò theo phạm vi tổ chức {#organization-scoped-roles}

`OrganizationMembership.Roles` chứa các vai trò mà người dùng có **bên trong** tổ chức đó. Tên vai trò lấy từ danh mục vai trò hiện có của tenant: một ISV khai báo "Auditor" một lần, và mọi khách hàng cấp vai trò đó cho người của chính họ.

```csharp
membership.Roles = ["Auditor", "Site Manager"];
```

Chúng được hợp vào claim `roles` cùng với các vai trò được gán trực tiếp cho người dùng và các vai trò được cấp qua tư cách thành viên nhóm SCIM, dưới cùng phép kiểm tra scope `roles`. Resource server không cần biết một vai trò được cấp trên toàn tenant hay theo từng tổ chức, nhưng **phải** đọc `org_id` cùng với `roles`, vì cùng một tên vai trò giờ mang nghĩa "trong tổ chức này".

Bốn quy tắc giới hạn cơ chế này:

- **Chỉ một tổ chức được chọn tường minh mới đóng góp vai trò**: tổ chức được nêu qua tham số `organization` hoặc qua một giới hạn client chỉ có một mục. Một tổ chức được thừa hưởng từ `AuthUser.OrganizationId` không đóng góp vai trò nào, cùng một sự bất đối xứng như ở phép kiểm tra tư cách thành viên.
- **Chỉ tư cách thành viên `active` mới đóng góp.** Một thành viên được mời nhưng chưa chấp nhận, hoặc đã bị tạm ngưng, không cấp gì cả, đúng như việc họ không cho phép điều gì.
- **Vai trò không bao giờ vượt qua ranh giới tổ chức.** Chúng được đọc từ bản ghi thành viên có khóa là tổ chức đã chọn, nên một vai trò nắm giữ ở tổ chức này không thể lọt vào token được phát hành cho tổ chức khác.
- **Các tiền tố dành riêng bị loại bỏ.** Một vai trò bắt đầu bằng `tenant:` hoặc `platform:` bị loại khi hợp và được ghi log ở mức Warning. Bản ghi thành viên là dữ liệu thuộc phạm vi khách hàng, nên nếu một tư cách thành viên có thể cấp `tenant:admin` thì "có thể quản lý tổ chức của mình" sẽ biến thành "có thể quản trị tenant". Vai trò được gán trực tiếp và ánh xạ nhóm→vai trò của SCIM không bị ảnh hưởng: chúng được người vận hành ghi qua một bề mặt quản trị đã xác thực, và đó là thẩm quyền mà một bản ghi thành viên không có.

Vai trò được đọc lại từ bản ghi thành viên ở mỗi lần xoay vòng refresh, nên việc thay đổi chúng sẽ đến được một phiên đang hoạt động ở lần refresh tiếp theo.

Vai trò toàn tenant được **hợp với** vai trò của tổ chức chứ không bị thay thế: `tenant:admin` là thẩm quyền portal và vẫn còn sau khi chọn một tổ chức.

## Chọn tổ chức trên một authorization request {#selecting-an-organization-on-an-authorization-request}

Gửi `organization` kèm slug hoặc id của tổ chức:

```http
GET /connect/authorize
  ?client_id=mobiom-web
  &response_type=code
  &redirect_uri=https://audit.example.com/callback
  &scope=openid%20profile
  &organization=international-sos
  &code_challenge=...&code_challenge_method=S256
```

Giá trị phải khớp `^[A-Za-z0-9._~-]{1,200}$` (tập ký tự unreserved của RFC 3986); mọi giá trị khác là `invalid_request`. Cách nó được phân giải phụ thuộc vào chữ hoa chữ thường:

- **Có bất kỳ ký tự chữ hoa nào → chỉ được phân giải như một id, khớp chính xác.** Slug chỉ gồm chữ thường, nên giá trị như vậy không thể là slug. Chuyển nó thành chữ thường rồi vẫn hỏi chỉ mục slug sẽ là hỏi "có slug của tổ chức nào là dạng chữ thường của id này không?", và nếu có, bên gọi đang nêu một id sẽ được trao cho một khách hàng khác.
- **Toàn chữ thường → slug trước, rồi tới id.** Nó có thể là một trong hai, và slug là thứ relying party thường gửi. Không mơ hồ vì store không cho phép một id và một slug có chung giá trị.

`org_slug` và `org_id` được chấp nhận làm bí danh: cả hai đều đang được dùng ở các nhà cung cấp khác, và lặng lẽ bỏ qua cái mà máy chủ này không chọn còn tệ hơn chấp nhận cả hai. Gửi hai tham số trỏ tới các tổ chức *khác nhau* sẽ bị từ chối với `invalid_request`: request mang hai nghĩa, và dù máy chủ chọn cái nào, relying party cũng đã được báo là cái kia. Lặp lại bất kỳ tham số nào trong ba tham số này cũng bị từ chối vì cùng lý do như với `redirect_uri`.

Tham số này được giữ nguyên qua vòng đi về giao diện đăng nhập, vì toàn bộ URL authorize được truyền dưới dạng `returnUrl`. Nó cũng hoạt động qua [Pushed Authorization Requests](par) mà không cần làm thêm gì: endpoint PAR lưu mọi trường được gửi tới, và `/connect/authorize` đọc payload đã đẩy thay vì query.

### Thứ tự ưu tiên {#precedence}

Tổ chức được phân giải theo thứ tự sau:

1. **Khi refresh, tổ chức mà grant được phát hành cho.**
2. **Tổ chức mà một [kết nối SSO theo phạm vi tổ chức](self-service-sso#organisation-scoped-connections) đã xác thực phiên này cho.** Nguồn duy nhất ở đây đã được *chứng minh* thay vì do bên gọi khẳng định: người dùng đã đăng nhập tại một IdP thuộc về đúng một tổ chức. Một request nêu tổ chức khác sẽ bị từ chối với `access_denied` thay vì lặng lẽ được phát hành cho tổ chức kia.
3. **Tham số `organization`.**
4. **`OAuthClient.RestrictedToOrganizationIds`, khi nó chứa đúng một mục.** Một ứng dụng dành riêng cho từng khách hàng nêu tổ chức của mình một lần, lúc đăng ký, và relying party của nó không bao giờ gửi tham số nào. Đây là dạng mà hầu hết các sản phẩm một-instance-mỗi-khách-hàng mong muốn.
5. **`AuthUser.OrganizationId`**: tổ chức được lưu trên chính tài khoản.

Các quy tắc 1-4 là các lựa chọn *tường minh* và phải thỏa mãn điều kiện thành viên. Quy tắc 5 thì không: bản thân bản ghi tài khoản đã là lời khẳng định thuộc về tổ chức, và đòi hỏi thêm một lời khẳng định thứ hai sẽ khóa ngoài mọi người dùng đã có từ trước ngay khi tổ chức tương ứng được tạo.

Trước khi có ai xác thực (home-realm discovery, danh sách provider trên trang đăng nhập, `/sso-check`), chưa có người dùng và chưa có grant, nên chỉ các quy tắc 3, 4 và sau đó `ITenantContext.OrganizationId` được phân giải. Xem [Kết nối theo phạm vi tổ chức](self-service-sso#organisation-scoped-connections).

## Giới hạn một client vào một tổ chức {#restricting-a-client-to-an-organization}

```csharp
client.RestrictedToOrganizationIds = ["org_7f3a"];
```

Mỗi mục phải khớp dạng id tổ chức `^[A-Za-z0-9._~-]{1,200}$`; admin API trả `400 invalid_request` cho một mục rỗng hoặc sai định dạng, vì một giới hạn liệt kê id mà không tham số `organization` nào gửi được sẽ không khớp gì cả, và một giới hạn không khớp gì cả sẽ từ chối mọi request. Một danh sách `null` được chuẩn hóa thành rỗng.

Rỗng (điều mà mọi client hiện có đều đang có) nghĩa là không giới hạn. Một request có tổ chức không nằm trong danh sách sẽ bị từ chối với `access_denied`. Một danh sách một mục cũng đồng thời chọn tổ chức, theo quy tắc 4 ở trên. Một danh sách nhiều mục thì giới hạn nhưng không chọn: request vẫn phải nêu một tổ chức, nếu không sẽ bị từ chối với `account_selection_required`.

## Các claim {#the-claims}

Trên cả ID token lẫn access token:

| Claim | Giá trị | Scope |
|---|---|---|
| `org_id` | `Organization.Id` | không có; luôn có mặt khi chủ thể có tổ chức |
| `org_slug` | `Organization.Slug` | không có; luôn có mặt khi tổ chức là một bản ghi thật |
| `org_name` | `Organization.DisplayName` | `profile` |

**`org_id` và `org_slug` cố ý không bị giới hạn theo scope.** Chúng là ngữ cảnh ủy quyền, không phải dữ liệu hồ sơ: chúng cho biết token được phép hành động cho khách hàng nào, đó là điều đầu tiên mà một resource server phục vụ nhiều khách hàng kiểm tra, trước khi nó quyết định có quan tâm tới tên hay không, và thường trên một token không hề yêu cầu profile. Khi bị giới hạn bởi `profile`, một client chỉ dùng API yêu cầu riêng `openid` sẽ nhận được token không có tổ chức nào, điều này được hiểu là "không thuộc về ai": resource server hoặc từ chối một bên gọi hợp lệ, hoặc coi token là không có phạm vi và phục vụ dữ liệu của mọi khách hàng từ nó. Lỗi thứ hai diễn ra âm thầm, và đó mới là lỗi quan trọng.

Phát hành chúng không giới hạn không để lộ điều gì mà client chưa xác lập: nó đã chọn tổ chức, hoặc nó bị giới hạn vào một tổ chức. `org_name` giữ giới hạn `profile` vì đó là dữ liệu trình bày, và không có gì nên dựa vào nó để ủy quyền.

Một tài khoản không có tổ chức sẽ không phát ra claim nào trong ba claim này, nên một token trước đây không mang claim tổ chức nào thì giờ vẫn không mang.

Cả ba đều là claim dành riêng: không danh sách `UserClaims` của scope nào và không thuộc tính người dùng tùy chỉnh nào có thể tạo ra hay ghi đè chúng. Điều đó quan trọng nhất với `org_slug`, vốn là khóa ổn định mà relying party so sánh với instance khách hàng đang phục vụ. Một giá trị tự khẳng định sẽ chính là câu trả lời cho phép so sánh đó.

Một tài khoản mang id tổ chức không phân giải được tới bản ghi nào chỉ phát ra mỗi `org_id`. Việc thiếu `org_slug` nghĩa là "không có slug", không bao giờ là "bị giữ lại".

**Kiểm tra `org_id` trong ứng dụng của bạn:**

```csharp
var orgId = User.FindFirst("org_id")?.Value;
if (!string.Equals(orgId, ThisInstanceOrganizationId, StringComparison.Ordinal))
    return Results.Forbid();
```

## Userinfo, introspection và token exchange {#userinfo-introspection-and-token-exchange}

**`/connect/userinfo`** trả về `org_id`, `org_slug`, `org_name` và `roles` từ **token được xuất trình**, không phải từ bản ghi người dùng. `org_id` và `org_slug` được trả về bất cứ khi nào token mang chúng, không bị giới hạn theo scope, vì cùng lý do chúng không bị giới hạn trên chính token; `org_name` cần `profile`. Đó là nguồn duy nhất có thể đúng khi một người dùng có thể thuộc nhiều tổ chức: tài khoản mang một giá trị mặc định, còn token nêu tổ chức mà grant thực sự được phát hành cho. Các trường hồ sơ (`email`, `name`, `phone_number`) vẫn lấy theo thời gian thực: đó là thông tin hiện tại của chủ thể, đúng là thứ userinfo dùng để cung cấp.

Vì vậy gắn nhãn lại một tài khoản không làm thay đổi những gì userinfo nói về một token đã được phát hành, và một người dùng đăng nhập vào tổ chức B không bao giờ được báo `org_id` A bởi chính máy chủ đã đặt B vào ID token của họ.

**`/connect/introspect`** bao gồm `org_id` và `org_slug` khi token mang chúng. Một resource server tự kiểm tra JWT sẽ đọc chúng từ token; một resource server dùng introspection thay vào đó giờ cũng nhận được cùng câu trả lời.

**Token exchange RFC 8693** chuyển `org_id`, `org_slug` và `org_name` từ subject token sang token được trao đổi, và áp dụng `RestrictedToOrganizationIds` của client **thực hiện trao đổi** lên chúng: một client được đăng ký để phục vụ một khách hàng không thể trao đổi token của khách hàng khác, và không thể trao đổi một token không mang tổ chức nào. Vì `org_id` không bị giới hạn theo scope, phép kiểm tra đó cũng hoạt động với một token resource server được phát hành mà không có scope `profile`: theo cách giới hạn cũ, token như vậy trông như không thuộc về ai, và một client bị giới hạn bị từ chối chính lưu lượng của nó. Một lần từ chối là `invalid_target`, khớp với các lần từ chối theo chính sách target khác trên đường này. Một lần trao đổi là một phép chiếu của một phiên đã có, và một phép chiếu đánh rơi tổ chức mà nó đang hành động cho sẽ trở thành không xác định được thuộc về ai chứ không hẹp hơn. `ITokenExchangeSubjectTransformer` của host vẫn có thể cố ý gắn lần trao đổi sang một tổ chức khác (đó là mục đích của các lần trao đổi gắn ngữ cảnh), nhưng nó phải nói rõ điều đó.

## Refresh {#refresh}

Tổ chức mà một grant được phát hành cho được mang qua mọi lần xoay vòng refresh, và được kiểm tra lại ở mỗi lần. Vì vậy ba điều sau có hiệu lực ở lần xoay vòng tiếp theo thay vì phải đợi hết thời gian sống của refresh:

- thu hồi hoặc tạm ngưng một tư cách thành viên,
- tắt một tổ chức (`Enabled = false`),
- thu hẹp `RestrictedToOrganizationIds` của một client.

**Mỗi điều này từ chối lần refresh; không điều nào thu hồi grant.** Refresh token được xuất trình không bị tiêu thụ và họ token vẫn nguyên vẹn, nên chuỗi vẫn có thể bị từ chối chừng nào điều kiện còn đúng và tiếp tục ngay khi điều kiện không còn đúng nữa: khôi phục tư cách thành viên, hoặc bật lại một tổ chức, sẽ đưa phiên trở lại mà không cần đăng nhập lại. Grant vẫn hết hạn theo thời gian sống tuyệt đối của chính nó. Đây là cùng một dạng như với người dùng bị vô hiệu hóa, có các lần refresh bị từ chối chừng nào `IsActive` còn là false.

Để thực sự kết thúc một phiên, hãy thu hồi grant: `POST /connect/revocation` với refresh token, hoặc `GrantRevocation` ở phía host. Tắt một tổ chức là một cổng chặn, không phải một lần thu hồi.

Một grant chỉ thừa hưởng tổ chức của tài khoản thì được suy ra lại ở mỗi lần xoay vòng, nên việc gắn nhãn lại một tài khoản vẫn có hiệu lực.

**Chuyển tổ chức là một authorization request mới**, không phải một lần refresh. Gửi lại `/connect/authorize` với một `organization` khác; phiên hiện có được dùng lại, nên không cần đăng nhập lần thứ hai, và một grant mới bắt đầu. Đừng mong endpoint refresh thay đổi tổ chức: nó không có user agent và không có bước chấp thuận, và grant ghi lại các scope đã được chấp thuận cho tổ chức mà nó được phát hành cho.

## Tắt phép kiểm tra tư cách thành viên {#turning-the-membership-gate-off}

```csharp
organization.RequireMembershipForTokens = false;
```

Mặc định bật. Hãy tắt nó cho một bản triển khai dùng tổ chức cho thương hiệu và định tuyến thay vì cho quyền truy cập: khi đó bất kỳ ai nêu được tên tổ chức đều được phát hành token cho tổ chức đó. Một tổ chức mà tư cách thành viên chỉ mang tính tham khảo thì không phải là một ranh giới; hãy đưa ra lựa chọn này một cách có cân nhắc.

## Từ chối phát hành từ một hook của host {#refusing-an-issuance-from-a-host-hook}

`IAuthHook.OnTokenIssuingAsync` được gọi ngay trước khi các grant `authorization_code`, `refresh_token` và `device_code` phát hành bất cứ thứ gì, kèm chủ thể đã phân giải:

```csharp
public Task OnTokenIssuingAsync(TokenIssuanceContext context, CancellationToken ct = default)
{
    if (IsOffboarded(context.SubjectId, context.ClientId))
        throw new InvalidOperationException("This account is being offboarded.");
    return Task.CompletedTask;
}
```

Ném ngoại lệ sẽ từ chối việc phát hành với `access_denied` và thông điệp của ngoại lệ làm `error_description`; ném một `ProtocolTokenException` thay vào đó cho phép nêu lỗi OAuth của riêng bạn. Trên đường refresh, phép kiểm tra chạy **trước** khi xoay vòng, nên một lần từ chối để refresh token được xuất trình không bị tiêu thụ và họ token vẫn nguyên vẹn: "chưa phải lúc này" không có nghĩa là "kết thúc phiên này".

Đây là một thành viên interface mặc định, nên một `IAuthHook` hiện có mà không ghi đè nó sẽ không bị ảnh hưởng. Hai lần phát hành cho agent (`client_credentials` và token exchange, mỗi cái kèm một agent profile) gọi nó đúng như trước đây.

## Các trường hợp từ chối {#refusals}

| Điều kiện | Lỗi |
|---|---|
| Hai bộ chọn nêu các tổ chức khác nhau | `invalid_request` |
| Bất kỳ bộ chọn nào bị lặp lại | `invalid_request` (trả trực tiếp, không phản chiếu về `redirect_uri`) |
| Tổ chức được nêu không tồn tại | `access_denied` |
| Tổ chức đang bị tắt | `access_denied` |
| Client không được phép cho tổ chức này | `access_denied` |
| Người dùng không phải thành viên đang hoạt động (lựa chọn tường minh) | `access_denied` |
| Client phục vụ nhiều tổ chức, request không nêu tổ chức nào | `account_selection_required` |

## Device flow {#device-flow}

Device grant không có authorization request để mang tham số, nên nó rơi xuống giới hạn client rồi tới giá trị mặc định của tài khoản. Một device client cần được ghim vào một tổ chức nên được đăng ký với `RestrictedToOrganizationIds` chỉ có một mục.

## Nâng cấp một bản triển khai hiện có {#upgrading-an-existing-deployment}

Không có gì thay đổi cho tới khi một tổ chức tồn tại. Khi chưa có bản ghi nào:

- không request nào có thể chọn một tổ chức,
- không có phép kiểm tra tư cách thành viên nào được kích hoạt,
- một tài khoản mang `OrganizationId` cũ vẫn tiếp tục phát ra `org_id` từ bản ghi người dùng, đúng như trước đây,
- token cho một người dùng không có tổ chức không mang claim nào trong ba claim.

Khi bạn tạo tổ chức, hãy cấp tư cách thành viên **trước khi** trỏ một client hoặc một relying party tới tổ chức đó: một lựa chọn tường minh đòi hỏi tư cách thành viên đang hoạt động, và một khách hàng có người dùng mang bản ghi nhưng không có tư cách thành viên sẽ bị từ chối.
