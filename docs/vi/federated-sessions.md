---
layout: default
title: Phiên liên kết (federated)
locale: vi
---

# Giữ phiên liên kết đồng bộ với upstream

Khi người dùng đăng nhập qua một [IdP bên ngoài](oidc-federation), Authagonal cấp phiên và token *của riêng nó*.
Theo mặc định, phiên cục bộ đó sau đó tồn tại độc lập: nếu khách hàng vô hiệu hóa người dùng trong directory của
họ, hoặc liên kết chia sẻ của khách bị thu hồi ở upstream, phiên Authagonal cục bộ vẫn tiếp tục hoạt động cho tới
khi cookie của nó hết hạn.

Để việc offboarding và thu hồi có hiệu lực nhanh chóng, hãy bật **`RevalidateOnRefresh`**. Khi đó, ở mỗi lần refresh
token cục bộ, Authagonal sẽ đổi refresh token upstream tại IdP, và nếu upstream cho biết thông tin xác thực không còn
nữa, lần refresh cục bộ bị từ chối và phiên sẽ ngừng nhận token mới trong vòng một thời hạn access token.

`RevalidateOnRefresh` là thiết lập chỉ dành cho **kết nối OIDC**. SAML không có refresh token để đổi, nên kết nối SAML
không thể xác minh lại; thay vào đó, hãy giới hạn các phiên đó bằng thời hạn phiên do chính assertion quy định (xem
[SAML](saml)).

## Bật tính năng {#enable-it}

Theo từng kết nối (được nạp sẵn từ cấu hình như bên dưới, hoặc đặt khi tạo kết nối qua
[Admin API](admin-api)), và upstream phải thực sự cấp refresh token:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "MetadataLocation": "https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration",
      "ClientId": "...", "ClientSecret": "...",
      "AllowedDomains": ["acme.com"],
      "RevalidateOnRefresh": true
    }
  ]
}
```

Toàn bộ thiết lập chỉ có vậy. Khi cờ được bật, Authagonal thêm `offline_access` vào scope mà nó yêu cầu từ upstream
(nếu request phía downstream chưa mang scope này), cất giữ refresh token mà upstream trả về, và đổi nó theo kiểu
server-to-server ở mỗi lần refresh cục bộ. Refresh token upstream **không bao giờ** được phát cho client. Nó được lưu
mã hóa trong một store bền vững theo từng phiên, được tạo lúc đăng nhập với thời hạn cố định bảy ngày (giới hạn phiên
tuyệt đối), và chỉ được dùng để xác minh lại.

Upstream phải hợp tác: nếu bản đăng ký ứng dụng của nó không bao giờ nhận được `offline_access` (ví dụ chấp thuận chưa
được cấp), sẽ không có refresh token nào trả về và không có gì để đổi. Authagonal ghi một cảnh báo
(`RevalidateOnRefresh is enabled for connection ... but no upstream refresh token is held`) ở mỗi lần refresh trong
trạng thái đó, và upstream **không** được kiểm tra lại.

## Điều gì xảy ra khi refresh {#what-happens-on-refresh}

1. RP refresh một token Authagonal như thường lệ (`grant_type=refresh_token` tại `/connect/token`).
2. Authagonal đổi refresh token upstream tại token endpoint của IdP:
   - **Thành công** → lần refresh cục bộ tiếp tục; nếu upstream đã xoay vòng token, token mới được lưu và
     dùng chung cho mọi grant của các RP trong phiên.
   - **`invalid_grant`** → thông tin xác thực liên kết không còn nữa (người dùng bị vô hiệu hóa, phiên bị thu hồi,
     token hết hạn). Lần refresh cục bộ bị **từ chối**: RP nhận `invalid_grant` từ `/connect/token`, và token
     upstream đã lưu bị xóa. Việc từ chối chỉ làm thất bại request đó; nó không thu hồi grant Authagonal,
     nên refresh token của RP vẫn chưa bị tiêu thụ và tiếp tục bị từ chối chừng nào upstream vẫn ở trạng thái thu hồi.
   - **Bất kỳ 4xx nào khác** (ví dụ `invalid_client` do secret đã bị xoay vòng/cấu hình sai, hoặc 429), một 5xx,
     một body lỗi không phân tích được, một lỗi truyền tải, hoặc một kết nối không tải được (đã bị xóa, lỗi discovery
     hoặc lỗi secret) → được coi là **tạm thời**: phiên được giữ lại để lỗi của người vận hành không khiến
     mọi người dùng liên kết bị đăng xuất hàng loạt. Hãy sửa cấu hình; không có gì bị mất. Phiên vẫn bị giới hạn bởi
     giới hạn phiên tuyệt đối.

Vì chỉ có **một** token upstream cho mỗi phiên trình duyệt (theo khóa người dùng + kết nối + phiên), một RP thứ hai
mà người dùng mở sẽ đọc và xoay vòng *chính* token đó: nhờ vậy lần refresh của một ứng dụng không thể khiến ứng dụng
khác giữ một bản sao đã chết.

## Không cần triển khai gì {#nothing-to-implement}

Ở đây không có interface nào cần viết. Store bền vững
(`IUpstreamRefreshTokenStore`) được các storage provider Azure, AWS và SQL tự động đăng ký, và việc đổi token
diễn ra bên trong. Bạn chỉ cần bật `RevalidateOnRefresh` trên những kết nối mà upstream sở hữu một thông tin xác
thực có thể thu hồi.

Token đã lưu bị xóa khi phiên kết thúc theo bất kỳ đường nào chạm tới nó: đăng xuất, thu hồi một phiên
từ trang tài khoản, "đăng xuất ở mọi nơi", và đợt quét hết hạn. Nếu host không đăng ký store nào, bản sao
được mang trên cookie phiên sẽ là phương án dự phòng.

Mọi phiên liên kết qua OIDC cũng ghi lại kết nối nào sở hữu nó (claim `upstream_connection_id`),
bất kể kết nối đó có xác minh lại hay không. Đó chỉ là việc ghi chép: không có gì được đổi cho một kết nối
không bật cờ.

> **Phạm vi:** tính năng này hoạt động ở bất cứ nơi nào store được đăng ký (các provider Azure Table, DynamoDB và
> SQL). Hãy bật nó trên các kết nối tới một upstream **đáng tin cậy**, đặc biệt là upstream xoay vòng refresh token
> theo kiểu dùng một lần (Entra, Auth0), và kết hợp với `IsExternalConnection` cho các IdP bên thứ ba (xem
> [SSO tự phục vụ](self-service-sso)).

## Bổ trợ: giới hạn phiên cứng {#complementary-a-hard-session-cap}

`RevalidateOnRefresh` giữ cho phiên phản ánh đúng việc thu hồi ở upstream. Nếu thay vào đó (hoặc thêm vào đó) bạn
muốn phiên cục bộ không bao giờ *tồn tại lâu hơn* phiên mà upstream đã khẳng định, hãy đặt `SessionExpClaim` thành
tên của một claim trong id_token mang thời điểm hết hạn (giây Unix). Authagonal giới hạn phiên cục bộ và mọi token
được mint từ phiên đó (bao gồm cả các lần xoay vòng refresh, và cả một grant device-code được phê duyệt qua phiên đó)
trong mốc này. Xem [Liên kết OIDC → Giới hạn thời hạn phiên](oidc-federation).

Device flow đáng được nêu tên vì cho tới gần đây nó vẫn là ngoại lệ: bản ghi device-code không có chỗ nào để mang
mốc giới hạn của phiên đã phê duyệt, nên một thiết bị được phê duyệt qua phiên liên kết vẫn tiếp tục mint token trong
toàn bộ thời hạn refresh tuyệt đối của client sau khi phiên đó đã kết thúc, và `RevalidateOnRefresh` cũng không bao
giờ hỏi lại upstream cho nó. Giờ đây cả hai đều tuân theo phiên đã phê duyệt. Một thiết bị được phê duyệt qua một phiên
*không liên kết* thì không có mốc nào để kế thừa, cũng là kết quả mà flow authorize đem lại.
