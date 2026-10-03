---
layout: default
title: 本地化
locale: zh-Hans
---

# 本地化

登录界面开箱即带十一种语言区域：英语、简体中文（`zh-Hans`）、德语（`de`）、法语（`fr`）、西班牙语（`es`）、越南语（`vi`）、葡萄牙语（`pt`）、阿拉伯语（`ar`）、南非荷兰语（`af`）、印地语（`hi`）和日语（`ja`）。服务器 API 响应和服务器渲染的页面同样以这十一种语言提供本地化。本地化范围包括服务器 API 响应、登录界面以及本文档站点。

## 支持的语言 {#supported-languages}

| 代码 | 语言 | 登录界面 | 服务器 API |
|---|---|---|---|
| `en` | 英语（默认） | ✓ | ✓ |
| `zh-Hans` | 简体中文 | ✓ | ✓ |
| `de` | 德语 | ✓ | ✓ |
| `fr` | 法语 | ✓ | ✓ |
| `es` | 西班牙语 | ✓ | ✓ |
| `vi` | 越南语 | ✓ | ✓ |
| `pt` | 葡萄牙语 | ✓ | ✓ |
| `ar` | 阿拉伯语（从右到左） | ✓ | ✓ |
| `af` | 南非荷兰语 | ✓ | ✓ |
| `hi` | 印地语 | ✓ | ✓ |
| `ja` | 日语 | ✓ | ✓ |

## 服务器（API 响应） {#server-api-responses}

服务器使用 ASP.NET Core 内置的本地化机制，配合 `IStringLocalizer<T>` 和 `.resx` 资源文件。语言根据 `Accept-Language` HTTP 标头选择。

### 本地化的内容 {#what-is-localized}

- 密码校验错误消息
- 密码策略标签（`GET /api/auth/password-policy`）
- 密码重置流程消息（令牌错误、过期、成功）
- 异常处理中间件返回的通用错误描述
- 管理员用户管理消息（邮箱确认、验证等）
- 结束会话的确认消息
- 服务器渲染的页面（邮箱确认结果页、结束会话确认页和已注销页），包括 `<html lang>`，以及阿拉伯语的 `dir="rtl"`
- 由库发送的邮件（`EmailService`：验证邮件、密码重置邮件和“账户已存在”通知），使用收件人保存的语言区域（`AuthUser.Locale`），没有则使用请求的区域性，再没有则使用英语

### 不本地化的内容 {#what-is-not-localized}

- 机器可读的 `error` 代码（`"email_required"`、`"invalid_credentials"` 等），它们属于 API 契约，保持不变
- 令牌、授权和撤销端点上的 OAuth/OIDC 错误代码及面向开发者的错误描述
- 内部日志消息和异常消息

### 测试服务器本地化 {#testing-server-localization}

向任意已本地化的端点发送 `Accept-Language` 标头：

```bash
# English (default)
curl https://auth.example.com/api/auth/password-policy

# Simplified Chinese
curl -H "Accept-Language: zh-Hans" https://auth.example.com/api/auth/password-policy

# German
curl -H "Accept-Language: de" https://auth.example.com/api/auth/password-policy
```

### 资源文件 {#resource-files}

所有服务器翻译字符串都位于 `src/Authagonal.Server/Resources/` 下的 `.resx` 文件中：

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

## 登录界面 {#login-ui}

登录 SPA 使用 [react-i18next](https://react.i18next.com/) 进行客户端本地化。语言根据浏览器的 `navigator.language` 设置自动检测。

已注册的语言区域集中在 `login-app/src/i18n/index.ts` 中的一个 `LANGUAGES` 注册表里，它同时驱动 i18next 资源注册和所有语言选择器，因此两者不会出现不一致。目前每个已注册的语言区域都会出现在默认选择器中。`DEFAULT_LANGUAGES` 与 `LANGUAGES` 分开导出，这样将来如有受限的语言区域，可以在不改动调用点的情况下将其从选择器中排除，但目前没有排除任何语言。租户也可以用同样的方式缩小选择器的范围：`branding.json` 中的 `languages` 数组会完全替换默认列表（参见[品牌定制](branding)）。

当前语言会同步到 `<html lang>` 和 `<html dir>` 上，因此从右到左的语言（`ar`）会自动翻转身份验证卡片，通过选择器就地切换语言时也是如此。

### 语言检测 {#language-detection}

检测顺序如下：

1. **localStorage**：上次访问时保存的偏好
2. **查询参数**：`?lng=de` 会覆盖浏览器检测结果
3. **浏览器语言**：`navigator.language`（自动）
4. **兜底**：英语（`en`）

### 翻译文件 {#translation-files}

翻译 JSON 文件随应用一起打包，位于 `login-app/src/i18n/`：

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

### 密码策略标签 {#password-policy-labels}

重置密码页面会根据 `GET /api/auth/password-policy` 返回的 `rule` 键，在客户端翻译其密码要求清单（遇到无法识别的规则时，退回使用服务器提供的 `label`）。这确保了要求清单跟随界面中所选的语言，即使浏览器的 `Accept-Language` 标头与之不同。注册页面显示的是服务器提供的 `label` 值，这些值依据 `Accept-Language` 进行本地化。

### npm 包使用者 {#npm-package-consumers}

如果你通过 `@authagonal/login` 使用登录应用，i18n 实例是导出的：

```typescript
import { i18n } from '@authagonal/login';

// Change language programmatically
i18n.changeLanguage('de');
```

## 文档 {#documentation}

文档站点采用基于目录的方式。英文页面位于根目录，翻译位于各语言区域的子目录中（`/zh-Hans/`、`/de/`、`/fr/`、`/es/`、`/vi/`、`/pt/`、`/ja/`）。侧边栏中的语言切换下拉菜单可用于在语言之间切换。

## 添加新语言 {#adding-a-new-language}

要添加对一种新语言的支持（例如意大利语 `it`）：

### 1. 服务器 {#1-server}

复制英文 `.resx` 文件并翻译其中的值，创建新的 `.resx` 文件：

```
src/Authagonal.Server/Resources/SharedMessages.it.resx
```

将 `"it"` 添加到 `src/Authagonal.Server/Services/SupportedLocales.cs` 中的 `SupportedLocales.All`，请求本地化中间件和服务器渲染的页面读取的都是这一个列表：

```csharp
public static readonly string[] All = ["en", "zh-Hans", "de", "fr", "es", "vi", "pt", "ja", "ar", "af", "hi", "it"];
```

### 2. 登录界面 {#2-login-ui}

复制 `en.json` 并翻译其中的值，创建新的翻译 JSON 文件：

```
login-app/src/i18n/it.json
```

在 `login-app/src/i18n/index.ts` 的 `LANGUAGES` 数组中注册它。这一个条目既注册了 i18next 资源，也把该语言加入所有选择器：

```typescript
import it from './it.json';

// In the LANGUAGES array:
{ code: 'it', label: 'Italiano', resource: it },
```

### 3. 文档 {#3-documentation}

创建一个新目录，放入翻译后的 markdown 文件：

```
docs/it/
  index.md
  installation.md
  quickstart.md
  ...
```

在 `docs/_config.yml` 中添加语言区域默认值：

```yaml
defaults:
  - scope:
      path: "it"
    values:
      locale: "it"
```

在 `docs/_layouts/default.html` 的切换器中添加该语言选项。

## 添加新字符串 {#adding-new-strings}

### 服务器 {#server}

1. 在 `SharedMessages.resx` 中添加键和英文值
2. 在每个语言区域的 `.resx` 文件中添加翻译后的值
3. 使用 `IStringLocalizer<SharedMessages>` 访问该字符串：

```csharp
// Inject via parameter
IStringLocalizer<SharedMessages> localizer

// Use with key
localizer["MyNewKey"].Value

// With format parameters
string.Format(localizer["MyNewKey"].Value, param1)
```

### 登录界面 {#login-ui-1}

1. 在 `en.json` 中添加键和英文值
2. 在每个语言区域的 JSON 文件中添加翻译后的值
3. 在组件中使用 `t()` 函数：

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```
