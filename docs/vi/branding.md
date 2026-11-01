---
layout: default
title: Thương hiệu
locale: vi
---

# Tùy biến thương hiệu cho giao diện đăng nhập

SPA đăng nhập có thể được cấu hình lúc chạy thông qua tệp `branding.json` được phục vụ từ thư mục gốc web. Không cần build lại, chỉ cần mount cấu hình và tài nguyên của bạn vào.

## Cách hoạt động {#how-it-works}

Khi khởi động, SPA tải `/branding.json`. Nếu tệp không tồn tại hoặc không truy cập được, các giá trị mặc định sẽ được dùng. (Server host cũng có thể nhúng trực tiếp cấu hình dưới dạng một boot payload `<script type="application/json" id="authagonal-boot">`; khi có payload này, SPA đọc nó thay vì tải tệp.) Cấu hình này điều khiển:

- Tên ứng dụng (hiển thị ở phần đầu trang và tiêu đề trang)
- Ảnh logo, kèm tùy chọn một "chip" nền riêng cho từng chế độ
- Màu chủ đạo (nút, liên kết, viền focus), kèm tùy chọn một biến thể cho chế độ tối
- Màu nền trang và nền thẻ, theo từng chế độ
- Việc hiển thị liên kết quên mật khẩu và đăng ký
- Chế độ tối mặc định (sáng / theo hệ điều hành / tối)
- Các tùy chọn của bộ chọn ngôn ngữ
- Chân trang "Được cung cấp bởi Authagonal"
- CSS tùy chỉnh để tạo kiểu sâu hơn

## Tên tổ chức {#organisation-name}

Trên một host multi-tenant có phân giải tổ chức cho request (ví dụ một tên miền tùy chỉnh được gắn cố định
với một khách hàng), payload `authagonal-boot` có thể mang thành phần thứ ba bên cạnh `branding` và
`providers`:

```json
{
  "branding": { "appName": "Acme Corp", "...": "..." },
  "providers": [],
  "organization": { "id": "org_123", "slug": "widgets-inc", "name": "Widgets Inc" }
}
```

`organization` là `null` (hoặc không có thành phần này) khi request không phân giải được tổ chức nào:
một triển khai single-tenant, hoặc một triển khai không gắn tổ chức nào với tên miền tùy chỉnh của host này. Bản thân thư viện phân giải tổ chức cho một request ẩn danh, trước khi xác thực, theo thứ tự nêu trong [Tổ chức](organizations#organizations-on-the-login-page): trước hết là ghim host có thẩm quyền, rồi tổ chức của request ủy quyền, rồi một giới hạn client chỉ có một mục. Host có cơ chế phân giải trước xác thực riêng (Authagonal Cloud gắn một tổ chức cho mỗi tên miền tùy chỉnh) có thể thay vào đó đặt `organization` khi lắp ráp boot payload bằng `LoginBootPayloadBuilder`.

Khi có `organization.name` và giá trị này khác `branding.appName` (không phân biệt hoa thường, đã cắt khoảng trắng,
để một tổ chức trùng tên với tenant không tạo ra "Acme / Đang đăng nhập vào Acme"),
thẻ đăng nhập hiển thị một dòng phụ dưới tiêu đề: "Đang đăng nhập vào {name}"
(`data-testid="login-org-name"`, khóa i18n `login.signingInTo`). Dòng này được hiển thị một lần, bởi phần đầu
`AuthLayout` dùng chung, nên mọi route được gắn qua nó (đăng nhập, đăng ký, quên/đặt lại
mật khẩu, các trang thử thách và thiết lập MFA, trang thiết bị, chấp thuận và chấp thuận cho agent, grant,
và tài khoản) đều hiển thị giống hệt nhau. Không có gì được hiển thị, và phần đầu giữ khoảng cách bình thường, khi
`organization` vắng mặt, là `null`, hoặc tên của nó trùng với `branding.appName`.

## Thương hiệu tổ chức {#organisation-branding}

Khi request được phân giải tới một tổ chức, `BrandingJson` của tổ chức đó được trộn theo từng trường đè lên thương hiệu của tenant, và kết quả là thành viên `branding` của boot payload. Trường null hoặc không có sẽ kế thừa giá trị của tenant, khóa không xác định bị bỏ qua, giá trị sai kiểu giữ giá trị của tenant cho khóa đó, `languages` thay thế danh sách của tenant, và một tài liệu sai định dạng cho ra thương hiệu của tenant nguyên vẹn. Tổ chức không thể đặt `showRegistration`. Các quy tắc nằm trong [Tổ chức](organizations#organization-branding) (`OrganizationBranding.Merge`).

Máy chủ độc lập dựng boot payload cho mỗi request tới trang đăng nhập bằng `LoginBootPayloadBuilder`, và `MapAuthagonalLoginFallback()` nhúng nó dưới dạng `<script type="application/json" id="authagonal-boot">` ngay trước `</head>`. `branding.json` được đọc lại ở mỗi request, nên chỉnh sửa tệp đã mount có hiệu lực ngay; tệp không có hoặc sai định dạng được coi là `{}` (kèm cảnh báo nếu sai định dạng). Nếu có gì thất bại, `index.html` thuần được phục vụ và SPA lấy `/branding.json` như trước.

## Thương hiệu của tổ chức trong email lời mời {#organisation-branding-in-invitation-emails}

`Organization.BrandingJson` là một đối tượng JSON thuộc về host. Ngoài trang đăng nhập ([ở trên](#organisation-branding)), thư viện đọc nó trong email lời mời (`IEmailService.SendUserInviteEmailAsync`, xem [Tổ chức](organizations#inviting-people)). Với lời mời vào một tổ chức, bộ gửi tích hợp lấy từ đó `appName`, `logoUrl` và `primaryColor`, từng trường một, đặt lên trên các giá trị mặc định của chính email: `Authagonal`, không có logo và `#2563eb` (các giá trị mặc định của ứng dụng đăng nhập). Một trường vắng mặt, rỗng hoặc không phải chuỗi được coi là chưa đặt; logo phải là URL `https` tuyệt đối và màu phải là một màu CSS đơn giản (thập lục phân, `rgb()`/`hsl()` hoặc từ khóa), nếu không cũng được coi là chưa đặt. JSON sai định dạng bị bỏ qua: một lời mời không bao giờ bị từ chối vì thương hiệu của tổ chức.

Tiêu đề là tiêu đề lời mời đã bản địa hóa cùng tên ứng dụng, phần thân nêu tên tổ chức và mọi giá trị đều được mã hóa HTML. Một lời mời đơn giản (không có tổ chức) dùng các giá trị mặc định và nêu tên người gửi (`Email:SenderName`, nếu không thì `Authagonal`).

Host có `IEmailService` riêng không bị ảnh hưởng. `SendUserInviteEmailAsync` mặc định của interface gửi email đặt lại mật khẩu kèm liên kết chấp nhận; hãy ghi đè để gửi lời mời mang thương hiệu của riêng bạn. Trang mà liên kết mở ra, `/accept-invite`, là một phần của ứng dụng đăng nhập và lấy `branding.json` của tenant như mọi trang khác, với thương hiệu của tổ chức mà tham số `organization` nêu tên được trộn đè lên ([ở trên](#organisation-branding)).

## Cấu hình {#configuration}

Đặt tệp `branding.json` vào thư mục `wwwroot/` (hoặc mount nó vào container Docker):

```json
{
  "appName": "Acme Corp",
  "logoUrl": "/branding/logo.svg",
  "primaryColor": "#1a56db",
  "darkPrimaryColor": "#3b82f6",
  "darkMode": "auto",
  "supportEmail": "help@acme.com",
  "showForgotPassword": true,
  "customCssUrl": "/branding/custom.css"
}
```

### Tùy chọn {#options}

| Thuộc tính | Kiểu | Mặc định | Mô tả |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | Hiển thị ở phần đầu trang và tiêu đề tab trình duyệt |
| `logoUrl` | `string \| null` | `null` | URL tới ảnh logo. Khi được đặt, nó thay thế phần đầu dạng chữ. |
| `primaryColor` | `string` | `"#2563eb"` | Màu hex cho nút, liên kết và chỉ báo focus |
| `supportEmail` | `string \| null` | `null` | Email liên hệ hỗ trợ (dành cho sử dụng trong tương lai) |
| `showForgotPassword` | `boolean` | `true` | Hiện/ẩn liên kết "Quên mật khẩu?" trên trang đăng nhập |
| `showRegistration` | `boolean` | `false` | Hiện/ẩn liên kết tự đăng ký |
| `customCssUrl` | `string \| null` | `null` | URL tới tệp CSS tùy chỉnh được tải sau các style mặc định |
| `welcomeTitle` | `LocalizedString` | `null` | Lời chào tùy chọn hiển thị dưới phần đầu trên các trang xác thực (chuỗi thường hoặc `{ "en": "...", "de": "..." }`). Không hiển thị gì khi không đặt. |
| `welcomeSubtitle` | `LocalizedString` | `null` | Dòng tùy chọn dưới `welcomeTitle`, cùng định dạng. Không hiển thị gì khi không đặt. |
| `languages` | `array \| null` | `null` | Các tùy chọn của bộ chọn ngôn ngữ (`[{ "code": "en", "label": "English" }, ...]`). `null` hiển thị mọi ngôn ngữ được phát hành trừ các locale vui (xem [Bản địa hóa](localization)). |
| `poweredBy` | `boolean` | `true` | Hiện/ẩn chân trang "Được cung cấp bởi Authagonal" trên các trang xác thực |
| `darkMode` | `"off" \| "auto" \| "force"` | `"auto"` | Giao diện mặc định khi khách truy cập chưa chọn: `"off"` (chỉ sáng), `"auto"` (theo thiết lập của hệ điều hành), `"force"` (luôn tối). Nút chuyển giao diện của khách truy cập vẫn được ưu tiên. |
| `lightBg` | `string \| null` | `null` | Màu nền trang ở chế độ sáng |
| `lightCardBg` | `string \| null` | `null` | Màu nền thẻ/biểu mẫu ở chế độ sáng |
| `darkBg` | `string \| null` | `null` | Màu nền trang ở chế độ tối |
| `darkCardBg` | `string \| null` | `null` | Màu nền thẻ/biểu mẫu ở chế độ tối |
| `darkPrimaryColor` | `string \| null` | `null` | Ghi đè `primaryColor` ở chế độ tối |
| `lightLogoBg` | `string \| null` | `null` | Nền chip logo ở chế độ sáng (xem bên dưới) |
| `darkLogoBg` | `string \| null` | `null` | Nền chip logo ở chế độ tối (xem bên dưới) |

Giá trị màu phải là màu hex (`#rgb`, `#rrggbb`, `#rrggbbaa`) hoặc một biểu thức `rgb()`/`rgba()`/`hsl()`/`hsla()`; mọi giá trị khác đều bị bỏ qua. Các màu theo từng chế độ được chèn vào dưới dạng một quy tắc `<style id="branding-theme-vars">` sau các style đi kèm: các giá trị `light*` tại `:root:where(:not(.dark))`, nên chúng không bao giờ áp dụng ở chế độ tối; các giá trị tối tại `.dark`; và `primaryColor` tại `:root`, vì đó là màu cơ sở cho cả hai chế độ. `:where()` không thêm độ ưu tiên (specificity), nên `customCssUrl` vẫn ghi đè được tất cả.

### Chip nền cho logo {#logo-background-chip}

Nếu logo của bạn có hình màu trắng hoặc trong suốt, nó có thể biến mất trên nền thẻ sáng. Hãy đặt `lightLogoBg` và/hoặc `darkLogoBg` để hiển thị logo bên trong một "chip" bo góc, có khoảng đệm, với màu nền đó:

```json
{
  "logoUrl": "/branding/logo.svg",
  "lightLogoBg": "#1c1e22",
  "darkLogoBg": "#1c1e22"
}
```

Chip (một phần tử bao `data-auth="logo-chip"` được điều khiển bởi biến CSS `--auth-logo-bg`) chỉ có khoảng đệm và màu nền khi nền logo được cấu hình, nên các tenant không đặt giá trị này vẫn thấy logo nằm sát trên thẻ y như trước. Hai trường này độc lập với nhau: chỉ đặt `lightLogoBg` để đặt logo trong chip ở chế độ sáng và để trống ở chế độ tối.

## Ví dụ Docker {#docker-example}

Mount các tệp thương hiệu vào container:

```bash
docker run -p 8080:8080 \
  -v ./my-branding/branding.json:/app/wwwroot/branding.json \
  -v ./my-branding/logo.svg:/app/wwwroot/branding/logo.svg \
  -v ./my-branding/custom.css:/app/wwwroot/branding/custom.css \
  -e Storage__ConnectionString="..." \
  -e Issuer="https://auth.example.com" \
  authagonal
```

Hoặc với docker-compose:

```yaml
services:
  authagonal:
    build: .
    ports:
      - "8080:8080"
    volumes:
      - ./my-branding/branding.json:/app/wwwroot/branding.json
      - ./my-branding/assets:/app/wwwroot/branding
    environment:
      - Storage__ConnectionString=...
      - Issuer=https://auth.example.com
```

## CSS tùy chỉnh {#custom-css}

Tùy chọn `customCssUrl` tải thêm một stylesheet sau các style mặc định, nên các quy tắc của bạn được ưu tiên. Hữu ích để đổi phông chữ, chỉnh khoảng cách, hoặc tạo lại kiểu cho các phần tử cụ thể. URL phải cùng origin (URL tương đối như `/branding/custom.css` là được); stylesheet khác origin sẽ bị âm thầm bỏ qua.

### Thuộc tính tùy chỉnh CSS {#css-custom-properties}

Giao diện đăng nhập cung cấp một số thuộc tính tùy chỉnh CSS để điều khiển chi tiết:

| Thuộc tính | Mặc định | Mô tả |
|---|---|---|
| `--brand-primary` | `#2563eb` | Màu chủ đạo cho nút, liên kết, viền focus |
| `--auth-bg` | `#f3f4f6` | Màu nền trang |
| `--auth-card-bg` | `#ffffff` | Màu nền thẻ/biểu mẫu |
| `--auth-logo-bg` | `transparent` | Nền chip logo (khoảng đệm của chip chỉ xuất hiện khi nền logo được cấu hình) |
| `--auth-radius` | `0.5rem` | Bán kính bo góc cho thẻ xác thực |
| `--auth-font` | *(kế thừa; bộ phông hệ thống)* | Họ phông chữ cho thẻ xác thực |
| `--auth-heading` | `#111827` | Màu chữ tiêu đề |

Các biến màu ở đây ánh xạ trực tiếp tới các trường cấu hình (`primaryColor`, `lightBg`/`darkBg`, `lightCardBg`/`darkCardBg`, `lightLogoBg`/`darkLogoBg`), nên hãy ưu tiên dùng cấu hình cho các thay đổi màu đơn giản và để dành CSS tùy chỉnh cho mọi thứ khác.

Ghi đè chúng trong CSS tùy chỉnh của bạn:

```css
:root {
  --brand-primary: #059669;
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

Giao diện đăng nhập dùng Tailwind CSS. CSS tùy chỉnh có thể nhắm vào các phần tử HTML chuẩn và các lớp tiện ích của Tailwind. Các component giao diện được export (`Button`, `Input`, `Card`, `Alert`, v.v.) dùng Tailwind bên trong.

## Chế độ tối {#dark-mode}

SPA đăng nhập đi kèm các giao diện sáng, tối và **hệ thống**. Nút chuyển giao diện luôn hiển thị trong bố cục. Lựa chọn của người dùng được lưu vào `localStorage` dưới khóa `auth-theme`.

### Cách hoạt động {#how-it-works-1}

- **Mặc định**: cho đến khi khách truy cập chọn giao diện, tùy chọn thương hiệu `darkMode` quyết định giá trị mặc định: `"off"` (sáng), `"auto"` (hệ thống, mặc định), hoặc `"force"` (tối). Khi khách truy cập đã dùng nút chuyển, lựa chọn của họ luôn được ưu tiên.
- **Phát hiện**: khi giao diện là "hệ thống", SPA theo dõi `window.matchMedia('(prefers-color-scheme: dark)')` và tự động áp dụng lại giao diện khi thiết lập của hệ điều hành thay đổi.
- **Áp dụng**: SPA bật/tắt lớp `.dark` trên `<html>`. Biến thể dark của Tailwind (`&:where(.dark, .dark *)`) kích hoạt các style tối đã được biên dịch vào mọi component.
- **Lưu trữ**: các lựa chọn tường minh "sáng" / "tối" / "hệ thống" được lưu trong `localStorage`.

### Biến CSS {#css-variables}

Các giá trị sáng được khai báo tại `:root`; các giá trị ghi đè cho chế độ tối có phạm vi trong `.dark`, nên thương hiệu của tenant trong `customCssUrl` luôn được ưu tiên khi được cung cấp.

| Biến | Sáng | Tối |
|---|---|---|
| `--auth-bg` | `#f3f4f6` (hoặc `lightBg`) | `#030712` (hoặc `darkBg`) |
| `--auth-card-bg` | `#ffffff` (hoặc `lightCardBg`) | `#111827` (hoặc `darkCardBg`) |
| `--auth-heading` | `#111827` | `#f9fafb` |
| `--auth-logo-bg` | `transparent` (hoặc `lightLogoBg`) | `transparent` (hoặc `darkLogoBg`) |
| `--brand-primary` | `#2563eb` (hoặc `primaryColor`) | giá trị của chế độ sáng (hoặc `darkPrimaryColor`) |

### Tắt hoặc ghi đè {#disabling-or-overriding}

Thương hiệu của tenant luôn được ưu tiên. Để ép dùng một giao diện duy nhất, hãy đặt giá trị của riêng bạn trong `customCssUrl`:

```css
/* Force dark palette regardless of user choice */
:root {
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
.dark {
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

Để bỏ hẳn nút chuyển giao diện, hãy dùng cách tích hợp qua gói npm, import `AuthLayout` và render mà không có nút chuyển, hoặc fork SPA.

### Thuộc tính dữ liệu {#data-attributes}

Mọi phần tử của biểu mẫu đăng nhập đều có thuộc tính `data-auth` để nhắm CSS và tự động hóa kiểm thử:

| Thuộc tính | Phần tử |
|---|---|
| `data-auth="page"` | Phần tử bao ngoài của trang |
| `data-auth="header"` | Phần đầu trang |
| `data-auth="logo-chip"` | Phần tử bao quanh ảnh logo (chỉ có khoảng đệm khi đặt nền logo) |
| `data-auth="logo"` | Ảnh logo |
| `data-auth="app-name"` | Tiêu đề tên ứng dụng |
| `data-auth="welcome-title"` / `data-auth="welcome-subtitle"` | Các dòng tùy chọn `welcomeTitle` / `welcomeSubtitle` (chỉ có khi được đặt) |
| `data-auth="content"` | Vùng nội dung chính |
| `data-auth="languages"` | Bộ chọn ngôn ngữ |
| `data-auth="language-trigger"` | Nút mở bộ chọn ngôn ngữ |
| `data-auth="theme-toggle"` | Nút chuyển giao diện sáng/hệ thống/tối |
| `data-auth="powered-by"` | Chân trang "Được cung cấp bởi Authagonal" |
| `data-auth="login-form"`, `"email-field"`, `"password-field"`, `"submit-button"` | Biểu mẫu đăng nhập và các phần của nó (chỉ trên trang đăng nhập) |

Nhắm vào chúng trong CSS tùy chỉnh của bạn:

```css
[data-auth="header"] {
  background: linear-gradient(135deg, #667eea, #764ba2);
}
```

### Ví dụ: nền và phông chữ tùy chỉnh {#example-custom-background-and-font}

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}
```

## Các mức tùy biến {#customization-tiers}

| Mức | Việc bạn làm | Cách cập nhật |
|---|---|---|
| **Chỉ cấu hình** | Mount `branding.json` + logo | Liền mạch: cập nhật image Docker, giữ nguyên các mount |
| **Cấu hình + CSS** | Thêm `customCssUrl` với các style ghi đè | Tương tự, các lớp CSS ổn định |
| **Gói npm** | `npm install @authagonal/login`, tùy chỉnh `branding.json`, build vào `wwwroot/` | Cập nhật được: `npm update` kéo phiên bản mới |
| **Fork SPA** | Clone `login-app/`, sửa mã nguồn, tự build | Bạn sở hữu giao diện, cập nhật phía server độc lập |
| **Tự viết** | Xây dựng một frontend hoàn toàn tùy chỉnh dựa trên auth API | Toàn quyền kiểm soát, xem [Auth API](auth-api) để biết hợp đồng |

Xem `demos/custom-server/` để có một ví dụ hoạt động với thương hiệu tùy chỉnh (giao diện xanh lá, "Acme Corp").
