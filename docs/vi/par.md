---
layout: default
title: Pushed Authorization Requests
locale: vi
---

# Pushed Authorization Requests (PAR)

[RFC 9126](https://www.rfc-editor.org/rfc/rfc9126) cho phép client POST các tham số authorize request thẳng tới server bằng cơ chế xác thực client tiêu chuẩn và nhận về một `request_uri` mờ, ngắn hạn để đưa cho trình duyệt. Sau đó trình duyệt truy cập `/connect/authorize?request_uri=...&client_id=...` thay vì mang mọi tham số trên URL.

Lý do nên dùng:

- Các tham số authorize không bao giờ xuất hiện trong lịch sử trình duyệt, log server hay header `Referer`.
- Server xác thực client ngay lúc push, nên các tham số được kiểm tra toàn vẹn trước khi có bất kỳ lần chuyển hướng nào.
- Các tập tham số dài (request `claims` lớn, flow nhiều resource) không vượt giới hạn độ dài URL.

## Endpoint {#endpoint}

```
POST /connect/par
Content-Type: application/x-www-form-urlencoded
```

Xác thực giống như `/connect/token`: HTTP Basic với `client_id`/`client_secret`, hoặc thông tin xác thực mã hóa dạng form. Confidential client bắt buộc phải xác thực; public client post mà không cần secret. Lỗi xác thực client trả về `401` (theo RFC 9126, khác với token endpoint, nơi chỉ `invalid_client` là 401).

Body dạng form mang cùng các tham số vốn thường nằm trên `/connect/authorize` (`response_type`, `redirect_uri`, `scope`, `state`, `code_challenge`, `code_challenge_method`, `nonce`, `resource`, v.v.). Bản thân `request_uri` bị từ chối, vì việc xâu chuỗi PAR bị cấm theo §2.1 của đặc tả. Nếu body mang `client_id`, nó phải khớp với client đã xác thực. Giống token endpoint, route này từ chối request `http` văn bản thuần trừ khi `AuthagonalProtocolOptions.AllowInsecureHttp` được đặt.

Request được kiểm tra ngay lúc push, theo cùng cách mà `/connect/authorize` sẽ kiểm tra nó (`redirect_uri` đã đăng ký, scope được phép, PKCE, các giá trị `prompt`, v.v.). Một request không hợp lệ bị từ chối ngay với `400 invalid_request` và không có `request_uri` nào được cấp, nên lỗi lộ ra với client thay vì với người dùng cuối giữa chừng flow. `authorization_details` bị từ chối với `invalid_authorization_details` (rich authorization request thuộc về token endpoint, không phải ở đây).

### Giới hạn {#limits}

- Body bị giới hạn ở 32 KB, với tối đa 64 trường form, tên dài 256 ký tự và 8 KB cho mỗi giá trị. Bất cứ thứ gì lớn hơn bị từ chối với `413 invalid_request`.
- Request bị giới hạn tần suất ở mức 60 mỗi phút cho mỗi client và địa chỉ nguồn, và tổng cộng 300 mỗi phút cho mỗi client, trả về `429 temporarily_unavailable`.

### Response {#response}

```
HTTP/1.1 201 Created
```
```json
{
  "request_uri": "urn:ietf:params:oauth:request_uri:abc123...",
  "expires_in": 90
}
```

`request_uri` chỉ dùng một lần. Nó bị xóa khỏi store khi authorization code được cấp cho nó. Nếu không bao giờ được dùng, nó hết hạn sau 90 giây.

### Bước authorize {#authorization-step}

```
GET /connect/authorize?client_id=my-rp&request_uri=urn:ietf:params:oauth:request_uri:abc123...
```

Khi có `request_uri`, mọi tham số khác được lấy từ payload đã push, mọi thứ khác trên URL bị bỏ qua (ngoài `client_id`, vốn phải khớp với client đã push payload, và tham số `error` mà một vòng liên kết thất bại nối thêm vào). Một `request_uri` không xác định, đã hết hạn, đã bị tiêu thụ hoặc do một client khác push sẽ bị từ chối với `invalid_request`. Chỉ các URN mờ do chính PAR endpoint của server này cấp mới được chấp nhận: mọi giá trị `request_uri` khác bị từ chối với `request_uri_not_supported`, và tham số `request` của RFC 9101 bị từ chối với `request_not_supported`.

Các giá trị `prompt` và `max_age` đã push được tuân thủ. Một PAR request mang `prompt=login` (hoặc một `max_age` mà phiên đã vượt quá) chỉ được thỏa mãn bởi một phiên có `auth_time` bằng hoặc sau thời điểm request được push, nên một phiên có sẵn từ trước sẽ bị đăng xuất và xác thực lại một lần, và lượt quay về từ đăng nhập sẽ cấp code thay vì lặp vô hạn.

## Bắt buộc PAR theo từng client {#requiring-par-per-client}

Đặt `RequirePushedAuthorizationRequests = true` trên một client để từ chối các request `/connect/authorize` thông thường từ client đó. Mọi lần authorize không qua PAR đều trả về `invalid_request` với mô tả "This client requires requests to be pushed via /connect/par".

```csharp
new OAuthClient
{
    ClientId = "high-risk-rp",
    RequirePushedAuthorizationRequests = true,
    // ...
}
```

Đây là tư thế được khuyến nghị cho các client xử lý scope nhạy cảm; kết hợp với PKCE, nó loại bỏ thanh địa chỉ khỏi bề mặt tấn công.

## Thời hạn và lưu trữ {#lifetime-and-storage}

`expires_in` trả về từ lần push là 90 giây, và khoảng thời gian đó bao trùm chặng từ lần push tới request `/connect/authorize` đầu tiên. Khi bản ghi được lấy ra lần đầu, nó được gia hạn (một lần) tới một hạn chót tuyệt đối là 15 phút kể từ lần push, để người dùng có thể hoàn tất đăng nhập, MFA và chấp thuận. Các giá trị 90 giây và 15 phút là hằng số, không phải cấu hình. Payload đã push được lưu qua cùng `IGrantStore` với authorization code và refresh token, nên chúng tự động kế thừa chiến lược lưu bền vững và nhân bản của host.

## Discovery {#discovery}

PAR endpoint tự quảng bá trong `.well-known/openid-configuration` như sau:

```json
{
  "pushed_authorization_request_endpoint": "https://auth.example.com/connect/par"
}
```
