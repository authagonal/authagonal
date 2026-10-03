---
layout: default
title: Bắt đầu nhanh
locale: vi
---

# Bắt đầu nhanh

Chạy Authagonal trên máy cục bộ trong 5 phút.

## 1. Khởi động máy chủ {#1-start-the-server}

```bash
docker compose up
```

Lệnh này khởi động Authagonal tại `http://localhost:8080` cùng với Azurite làm kho lưu trữ.

> Tệp compose đặt `Auth__AllowInsecureHttp=true`, vì RFC 6749 §3.1/§3.2 yêu cầu TLS tại authorization endpoint và token endpoint, và nếu không có thiết lập này Authagonal sẽ từ chối các request plaintext tới `/connect/*`. Công tắc này chỉ dành cho máy tính cá nhân. Bất cứ thứ gì người khác có thể truy cập tới đều phải đặt sau một proxy kết thúc TLS có chuyển tiếp `X-Forwarded-Proto: https`, và gỡ bỏ công tắc này: xem [Cài đặt](installation).

## 2. Kiểm tra máy chủ đang chạy {#2-verify-its-running}

```bash
# Health check
curl http://localhost:8080/health

# OIDC discovery
curl http://localhost:8080/.well-known/openid-configuration

# Login page (returns the SPA)
curl http://localhost:8080/login
```

## 3. Đăng ký một client {#3-register-a-client}

Thêm một client vào `appsettings.json` (hoặc truyền qua biến môi trường):

```json
{
  "Clients": [
    {
      "ClientId": "my-web-app",
      "ClientName": "My Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["http://localhost:3000/callback"],
      "PostLogoutRedirectUris": ["http://localhost:3000"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["http://localhost:3000"],
      "RequirePkce": true,
      "RequireClientSecret": false
    }
  ]
}
```

Client được nạp sẵn khi khởi động, chạy lại ở mỗi lần triển khai cũng an toàn.

## 4. Bắt đầu một lượt đăng nhập {#4-initiate-a-login}

Chuyển hướng người dùng tới:

```
http://localhost:8080/connect/authorize
  ?client_id=my-web-app
  &redirect_uri=http://localhost:3000/callback
  &response_type=code
  &scope=openid profile email
  &state=random-state
  &code_challenge=...
  &code_challenge_method=S256
```

Người dùng thấy trang đăng nhập, xác thực, rồi được chuyển hướng trở lại kèm một authorization code.

> **Người dùng đầu tiên:** đăng ký một người dùng tại `http://localhost:8080/login/register`, hoặc tạo qua [Admin API](admin-api). Tự đăng ký sẽ gửi email xác minh, và khi chưa cấu hình bên gửi email nào (mặc định trên máy cục bộ) thì email đó bị loại bỏ, nên để thử nghiệm cục bộ hãy đặt `Auth__AutoConfirmEmailDomains__0=example.dev` (bất kỳ tên miền nào bạn dùng để đăng ký) để bỏ qua bước xác minh, hoặc cấu hình `Email:ResendApiKey` + `Email:SenderEmail`. Xem [Cấu hình → Email](configuration#email).

## 5. Đổi code lấy token {#5-exchange-the-code}

```bash
curl -X POST http://localhost:8080/connect/token \
  -d grant_type=authorization_code \
  -d code=THE_CODE \
  -d redirect_uri=http://localhost:3000/callback \
  -d client_id=my-web-app \
  -d code_verifier=THE_VERIFIER
```

Phản hồi:

```json
{
  "access_token": "eyJ...",
  "id_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 1800,
  "scope": "openid profile email"
}
```

`expires_in` là `AccessTokenLifetimeSeconds` của client (1800 với một client được nạp sẵn trừ khi bạn tự đặt). Ở đây không có `refresh_token`: client chỉ nhận được refresh token khi nó đặt `AllowOfflineAccess` và request có yêu cầu scope `offline_access`.

## Bản demo hoạt động {#working-demo}

Thư mục `demos/sample-app/` chứa một React SPA + API hoàn chỉnh hiện thực toàn bộ luồng OIDC ở trên. Xem [README của demos](https://github.com/authagonal/authagonal/tree/master/demos) để biết hướng dẫn.

## Bước tiếp theo {#next-steps}

- [Cấu hình](configuration), tài liệu tham chiếu đầy đủ cho mọi thiết lập
- [Khả năng mở rộng](extensibility), chạy dưới dạng thư viện, thêm hook tùy chỉnh
- [Thương hiệu](branding), tùy biến giao diện đăng nhập
- [SAML](saml), thêm các provider SAML SSO
- [Cấp phát](provisioning), cấp phát người dùng vào các ứng dụng downstream
