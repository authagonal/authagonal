---
layout: default
title: Bản địa hóa
locale: vi
---

# Bản địa hóa

Giao diện đăng nhập có sẵn mười một locale: tiếng Anh, tiếng Trung giản thể (`zh-Hans`), tiếng Đức (`de`), tiếng Pháp (`fr`), tiếng Tây Ban Nha (`es`), tiếng Việt (`vi`), tiếng Bồ Đào Nha (`pt`), tiếng Ả Rập (`ar`), tiếng Afrikaans (`af`), tiếng Hindi (`hi`) và tiếng Nhật (`ja`). Phản hồi API của máy chủ và các trang do máy chủ render cũng được bản địa hóa đủ cả mười một ngôn ngữ. Bản địa hóa bao gồm phản hồi API của máy chủ, giao diện đăng nhập, và trang tài liệu này.

## Ngôn ngữ được hỗ trợ {#supported-languages}

| Mã | Ngôn ngữ | Giao diện đăng nhập | API máy chủ |
|---|---|---|---|
| `en` | Tiếng Anh (mặc định) | ✓ | ✓ |
| `zh-Hans` | Tiếng Trung giản thể | ✓ | ✓ |
| `de` | Tiếng Đức | ✓ | ✓ |
| `fr` | Tiếng Pháp | ✓ | ✓ |
| `es` | Tiếng Tây Ban Nha | ✓ | ✓ |
| `vi` | Tiếng Việt | ✓ | ✓ |
| `pt` | Tiếng Bồ Đào Nha | ✓ | ✓ |
| `ar` | Tiếng Ả Rập (viết từ phải sang trái) | ✓ | ✓ |
| `af` | Tiếng Afrikaans | ✓ | ✓ |
| `hi` | Tiếng Hindi | ✓ | ✓ |
| `ja` | Tiếng Nhật | ✓ | ✓ |

## Máy chủ (phản hồi API) {#server-api-responses}

Máy chủ dùng cơ chế bản địa hóa dựng sẵn của ASP.NET Core với `IStringLocalizer<T>` và các tệp tài nguyên `.resx`. Ngôn ngữ được chọn từ HTTP header `Accept-Language`.

### Những gì được bản địa hóa {#what-is-localized}

- Thông báo lỗi kiểm tra mật khẩu
- Nhãn chính sách mật khẩu (`GET /api/auth/password-policy`)
- Thông báo trong luồng đặt lại mật khẩu (lỗi token, hết hạn, thành công)
- Mô tả lỗi chung từ middleware xử lý ngoại lệ
- Thông báo quản lý người dùng của admin (xác nhận email, xác minh, v.v.)
- Thông báo xác nhận kết thúc phiên
- Các trang do máy chủ render (kết quả xác nhận email, trang xác nhận kết thúc phiên và trang đã đăng xuất), bao gồm `<html lang>` và `dir="rtl"` cho tiếng Ả Rập
- Email do thư viện gửi (`EmailService`: xác minh, đặt lại mật khẩu, và thông báo "tài khoản đã tồn tại"), theo locale đã lưu của người nhận (`AuthUser.Locale`), nếu không có thì theo culture của request, nếu không nữa thì tiếng Anh

### Những gì KHÔNG được bản địa hóa {#what-is-not-localized}

- Mã `error` dành cho máy đọc (`"email_required"`, `"invalid_credentials"`, v.v.), đây là hợp đồng API và giữ nguyên
- Mã lỗi OAuth/OIDC và mô tả lỗi dành cho lập trình viên trên các endpoint token, authorize và revocation
- Thông điệp log nội bộ và thông điệp ngoại lệ

### Kiểm thử bản địa hóa phía máy chủ {#testing-server-localization}

Gửi header `Accept-Language` tới bất kỳ endpoint nào đã được bản địa hóa:

```bash
# English (default)
curl https://auth.example.com/api/auth/password-policy

# Simplified Chinese
curl -H "Accept-Language: zh-Hans" https://auth.example.com/api/auth/password-policy

# German
curl -H "Accept-Language: de" https://auth.example.com/api/auth/password-policy
```

### Tệp tài nguyên {#resource-files}

Mọi chuỗi dịch của máy chủ nằm trong các tệp `.resx` dưới `src/Authagonal.Server/Resources/`:

```
Resources/
  SharedMessages.cs          # Marker class
  SharedMessages.resx        # English (default)
  SharedMessages.zh-Hans.resx
  SharedMessages.de.resx
  SharedMessages.fr.resx
  SharedMessages.es.resx
  SharedMessages.vi.resx
  SharedMessages.pt.resx
  SharedMessages.ja.resx
  SharedMessages.ar.resx
  SharedMessages.af.resx
  SharedMessages.hi.resx
```

## Giao diện đăng nhập {#login-ui}

SPA đăng nhập dùng [react-i18next](https://react.i18next.com/) để bản địa hóa phía client. Ngôn ngữ được tự động phát hiện từ thiết lập `navigator.language` của trình duyệt.

Các locale đã đăng ký nằm trong một registry `LANGUAGES` duy nhất ở `login-app/src/i18n/index.ts`, registry này điều khiển cả việc đăng ký resource của i18next lẫn mọi bộ chọn ngôn ngữ, nên hai thứ không thể lệch nhau. Hiện mọi locale đã đăng ký đều xuất hiện trong bộ chọn mặc định. `DEFAULT_LANGUAGES` được export tách riêng khỏi `LANGUAGES` để sau này một locale bị hạn chế có thể bị loại khỏi bộ chọn mà không phải sửa các nơi gọi, nhưng hiện chưa có locale nào bị loại. Tenant cũng có thể thu hẹp bộ chọn theo cùng cách: một mảng `languages` trong `branding.json` thay thế hoàn toàn danh sách mặc định (xem [Thương hiệu](branding)).

Ngôn ngữ đang dùng được phản chiếu lên `<html lang>` và `<html dir>`, nên các ngôn ngữ viết từ phải sang trái (`ar`) tự động lật thẻ xác thực, kể cả khi ngôn ngữ được chuyển ngay tại chỗ qua bộ chọn.

### Phát hiện ngôn ngữ {#language-detection}

Thứ tự phát hiện là:

1. **localStorage**: lựa chọn đã lưu từ lần truy cập trước
2. **Tham số truy vấn**: `?lng=de` ghi đè việc phát hiện theo trình duyệt
3. **Ngôn ngữ trình duyệt**: `navigator.language` (tự động)
4. **Mặc định dự phòng**: tiếng Anh (`en`)

### Tệp bản dịch {#translation-files}

Các tệp JSON bản dịch được đóng gói cùng ứng dụng tại `login-app/src/i18n/`:

```
i18n/
  index.ts        # i18n initialization + the LANGUAGES registry
  en.json         # English
  zh-Hans.json    # Simplified Chinese
  de.json         # German
  fr.json         # French
  es.json         # Spanish
  vi.json         # Vietnamese
  pt.json         # Portuguese
  ar.json         # Arabic
  af.json         # Afrikaans
  hi.json         # Hindi
  ja.json         # Japanese
```

### Nhãn chính sách mật khẩu {#password-policy-labels}

Trang đặt lại mật khẩu dịch danh sách yêu cầu mật khẩu ngay phía client dựa trên khóa `rule` do `GET /api/auth/password-policy` trả về (dùng `label` do máy chủ cung cấp cho các rule không nhận ra). Nhờ đó các yêu cầu luôn theo ngôn ngữ được chọn trong giao diện, kể cả khi header `Accept-Language` của trình duyệt khác. Trang đăng ký hiển thị các giá trị `label` do máy chủ cung cấp, vốn được bản địa hóa theo `Accept-Language`.

### Dùng qua gói npm {#npm-package-consumers}

Nếu bạn dùng ứng dụng đăng nhập qua `@authagonal/login`, instance i18n được export:

```typescript
import { i18n } from '@authagonal/login';

// Change language programmatically
i18n.changeLanguage('de');
```

## Tài liệu {#documentation}

Trang tài liệu dùng cách tổ chức theo thư mục. Các trang tiếng Anh nằm ở thư mục gốc, còn bản dịch nằm trong các thư mục con theo locale (`/zh-Hans/`, `/de/`, `/fr/`, `/es/`, `/vi/`, `/pt/`, `/ja/`). Một danh sách thả xuống chuyển ngôn ngữ trong thanh bên cho phép chuyển giữa các ngôn ngữ.

## Thêm một ngôn ngữ mới {#adding-a-new-language}

Để hỗ trợ một ngôn ngữ mới (ví dụ tiếng Ý `it`):

### 1. Máy chủ {#1-server}

Tạo một tệp `.resx` mới bằng cách sao chép tệp tiếng Anh và dịch các giá trị:

```
src/Authagonal.Server/Resources/SharedMessages.it.resx
```

Thêm `"it"` vào `SupportedLocales.All` trong `src/Authagonal.Server/Services/SupportedLocales.cs`, danh sách duy nhất mà cả middleware bản địa hóa request lẫn các trang do máy chủ render cùng đọc:

```csharp
public static readonly string[] All = ["en", "zh-Hans", "de", "fr", "es", "vi", "pt", "ja", "ar", "af", "hi", "it"];
```

### 2. Giao diện đăng nhập {#2-login-ui}

Tạo một tệp JSON bản dịch mới bằng cách sao chép `en.json` và dịch các giá trị:

```
login-app/src/i18n/it.json
```

Đăng ký nó trong mảng `LANGUAGES` ở `login-app/src/i18n/index.ts`. Chỉ một mục đó vừa đăng ký resource của i18next vừa thêm ngôn ngữ vào mọi bộ chọn:

```typescript
import it from './it.json';

// In the LANGUAGES array:
{ code: 'it', label: 'Italiano', resource: it },
```

### 3. Tài liệu {#3-documentation}

Tạo một thư mục mới chứa các tệp markdown đã dịch:

```
docs/it/
  index.md
  installation.md
  quickstart.md
  ...
```

Thêm một giá trị locale mặc định trong `docs/_config.yml`:

```yaml
defaults:
  - scope:
      path: "it"
    values:
      locale: "it"
```

Thêm tùy chọn ngôn ngữ vào bộ chuyển ngôn ngữ trong `docs/_layouts/default.html`.

## Thêm chuỗi mới {#adding-new-strings}

### Máy chủ {#server}

1. Thêm khóa và giá trị tiếng Anh vào `SharedMessages.resx`
2. Thêm giá trị đã dịch vào tệp `.resx` của từng locale
3. Dùng `IStringLocalizer<SharedMessages>` để truy cập chuỗi:

```csharp
// Inject via parameter
IStringLocalizer<SharedMessages> localizer

// Use with key
localizer["MyNewKey"].Value

// With format parameters
string.Format(localizer["MyNewKey"].Value, param1)
```

### Giao diện đăng nhập {#login-ui-1}

1. Thêm khóa và giá trị tiếng Anh vào `en.json`
2. Thêm giá trị đã dịch vào tệp JSON của từng locale
3. Dùng hàm `t()` trong các component:

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```
