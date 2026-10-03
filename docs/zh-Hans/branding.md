---
layout: default
title: 品牌定制
locale: zh-Hans
---

# 定制登录界面的品牌

登录 SPA 可以在运行时通过 Web 根目录下提供的 `branding.json` 文件进行配置。无需重新构建，只需挂载你的配置和资源文件即可。

## 工作原理 {#how-it-works}

启动时，SPA 会获取 `/branding.json`。如果该文件不存在或无法访问，则使用默认值。（宿主服务器也可以把配置内联为一个 `<script type="application/json" id="authagonal-boot">` 启动载荷；存在该载荷时，SPA 会直接读取它，而不再发起请求。）该配置控制以下内容：

- 应用名称（显示在页眉和页面标题中）
- Logo 图片，以及可选的按明暗模式分别设置的背景“底块”
- 主色（按钮、链接、焦点环），以及可选的深色模式变体
- 按明暗模式分别设置的页面和卡片背景色
- “忘记密码”和注册链接是否显示
- 深色模式默认值（浅色 / 跟随操作系统 / 深色）
- 语言选择器选项
- “由 Authagonal 提供技术支持”页脚
- 用于更深入定制样式的自定义 CSS

## 组织名称 {#organisation-name}

在会为请求解析出组织的多租户宿主上（例如绑定到某个客户的自定义域名），`authagonal-boot` 载荷可以在 `branding` 和 `providers` 之外携带第三个成员：

```json
{
  "branding": { "appName": "Acme Corp", "...": "..." },
  "providers": [],
  "organization": { "id": "org_123", "slug": "widgets-inc", "name": "Widgets Inc" }
}
```

当请求没有解析出任何组织时（单租户部署，或该主机名没有绑定自定义域名的部署），`organization` 为 `null`（或该成员不存在）。本库自身不会为匿名的、认证前的请求解析组织（`OrganizationSelector` 需要一个已登录的 `AuthUser`）；拥有自己的认证前解析机制的宿主（Authagonal Cloud 按自定义域名绑定组织）会在组装启动载荷时设置 `organization`。

当 `organization.name` 存在且与 `branding.appName` 不同时（比较时忽略大小写并去除首尾空白，因此与租户同名的组织不会显示成“Acme / 正在登录 Acme”），登录卡片会在标题下方渲染一行副标题：“正在登录 {name}”（`data-testid="login-org-name"`，i18n 键 `login.signingInTo`）。它只由共享的 `AuthLayout` 页眉渲染一次，因此所有通过它挂载的路由（登录、注册、忘记/重置密码、MFA 质询和设置页面、设备页面、同意和智能体同意、授权以及账户页面）都会以相同方式显示它。当 `organization` 不存在、为 `null` 或其名称与 `branding.appName` 相同时，不会渲染任何内容，页眉保持正常间距。

## 配置 {#configuration}

将 `branding.json` 文件放在 `wwwroot/` 目录中（或将其挂载到 Docker 容器中）：

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

### 选项 {#options}

| 属性 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | 显示在页眉和浏览器标签页标题中 |
| `logoUrl` | `string \| null` | `null` | Logo 图片的 URL。设置后会替换文字页眉。 |
| `primaryColor` | `string` | `"#2563eb"` | 按钮、链接和焦点指示器使用的十六进制颜色 |
| `supportEmail` | `string \| null` | `null` | 支持联系邮箱（保留供将来使用） |
| `showForgotPassword` | `boolean` | `true` | 显示/隐藏登录页上的“忘记密码？”链接 |
| `showRegistration` | `boolean` | `false` | 显示/隐藏自助注册链接 |
| `customCssUrl` | `string \| null` | `null` | 在默认样式之后加载的自定义 CSS 文件的 URL |
| `welcomeTitle` | `LocalizedString` | `null` | 可选的问候语，渲染在认证页面的页眉下方（普通字符串或 `{ "en": "...", "de": "..." }`）。未设置时不渲染任何内容。 |
| `welcomeSubtitle` | `LocalizedString` | `null` | 位于 `welcomeTitle` 下方的可选文字行，格式相同。未设置时不渲染任何内容。 |
| `languages` | `array \| null` | `null` | 语言选择器选项（`[{ "code": "en", "label": "English" }, ...]`）。`null` 会显示所有随附的语言，趣味语言区域除外（参见[本地化](localization)）。 |
| `poweredBy` | `boolean` | `true` | 显示/隐藏认证页面上的“由 Authagonal 提供技术支持”页脚 |
| `darkMode` | `"off" \| "auto" \| "force"` | `"auto"` | 访客尚未选择主题时的默认主题：`"off"`（仅浅色）、`"auto"`（跟随操作系统偏好）、`"force"`（始终深色）。访客通过主题切换按钮所做的选择仍然优先。 |
| `lightBg` | `string \| null` | `null` | 浅色模式下的页面背景色 |
| `lightCardBg` | `string \| null` | `null` | 浅色模式下的卡片/表单背景色 |
| `darkBg` | `string \| null` | `null` | 深色模式下的页面背景色 |
| `darkCardBg` | `string \| null` | `null` | 深色模式下的卡片/表单背景色 |
| `darkPrimaryColor` | `string \| null` | `null` | 在深色模式下覆盖 `primaryColor` |
| `lightLogoBg` | `string \| null` | `null` | 浅色模式下的 Logo 底块背景（见下文） |
| `darkLogoBg` | `string \| null` | `null` | 深色模式下的 Logo 底块背景（见下文） |

颜色值必须是十六进制颜色（`#rgb`、`#rrggbb`、`#rrggbbaa`）或 `rgb()`/`rgba()`/`hsl()`/`hsla()` 表达式；其他值都会被忽略。按模式设置的颜色会作为一条 `<style id="branding-theme-vars">` 规则注入到内置样式之后：`light*` 值作用于 `:root:where(:not(.dark))`，因此永远不会在深色模式下生效；深色值作用于 `.dark`；`primaryColor` 作用于 `:root`，因为它是两种模式的基础颜色。`:where()` 不增加优先级，因此 `customCssUrl` 仍然可以覆盖所有这些值。

### Logo 背景底块 {#logo-background-chip}

如果你的 Logo 使用白色或透明的图案，它在浅色卡片上可能会看不见。设置 `lightLogoBg` 和/或 `darkLogoBg`，即可把 Logo 渲染在一个带内边距、圆角并使用该背景色的“底块”中：

```json
{
  "logoUrl": "/branding/logo.svg",
  "lightLogoBg": "#1c1e22",
  "darkLogoBg": "#1c1e22"
}
```

底块（一个由 `--auth-logo-bg` CSS 变量驱动的 `data-auth="logo-chip"` 包装元素）只有在配置了 Logo 背景时才会获得内边距和背景，因此没有设置它的租户看到的 Logo 仍然与以前完全一样，紧贴在卡片上。这两个字段彼此独立：只设置 `lightLogoBg`，Logo 就只在浅色模式下显示底块，在深色模式下保持原样。

## Docker 示例 {#docker-example}

将你的品牌文件挂载到容器中：

```bash
docker run -p 8080:8080 \
  -v ./my-branding/branding.json:/app/wwwroot/branding.json \
  -v ./my-branding/logo.svg:/app/wwwroot/branding/logo.svg \
  -v ./my-branding/custom.css:/app/wwwroot/branding/custom.css \
  -e Storage__ConnectionString="..." \
  -e Issuer="https://auth.example.com" \
  authagonal
```

或者使用 docker-compose：

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

## 自定义 CSS {#custom-css}

`customCssUrl` 选项会在默认样式之后加载一个额外的样式表，因此你的规则优先生效。适合用来更换字体、调整间距或重新设计特定元素的样式。该 URL 必须是同源的（`/branding/custom.css` 之类的相对 URL 没有问题）；跨源样式表会被静默跳过。

### CSS 自定义属性 {#css-custom-properties}

登录界面公开了若干 CSS 自定义属性，用于精细控制：

| 属性 | 默认值 | 说明 |
|---|---|---|
| `--brand-primary` | `#2563eb` | 按钮、链接、焦点环的主色 |
| `--auth-bg` | `#f3f4f6` | 页面背景色 |
| `--auth-card-bg` | `#ffffff` | 卡片/表单背景色 |
| `--auth-logo-bg` | `transparent` | Logo 底块背景（只有配置了 Logo 背景时才会出现底块内边距） |
| `--auth-radius` | `0.5rem` | 认证卡片的圆角半径 |
| `--auth-font` | *（继承；系统字体栈）* | 认证卡片的字体 |
| `--auth-heading` | `#111827` | 标题文字颜色 |

这里的颜色变量直接对应配置字段（`primaryColor`、`lightBg`/`darkBg`、`lightCardBg`/`darkCardBg`、`lightLogoBg`/`darkLogoBg`），因此简单的颜色修改请优先使用配置，其他一切再交给自定义 CSS。

在你的自定义 CSS 中覆盖它们：

```css
:root {
  --brand-primary: #059669;
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

登录界面使用 Tailwind CSS。自定义 CSS 可以针对标准 HTML 元素和 Tailwind 工具类。导出的界面组件（`Button`、`Input`、`Card`、`Alert` 等）内部也使用 Tailwind。

## 深色模式 {#dark-mode}

登录 SPA 内置浅色、深色和**跟随系统**三种主题。主题切换按钮始终显示在布局中。用户的选择会以 `auth-theme` 键持久化到 `localStorage` 中。

### 工作原理 {#how-it-works-1}

- **默认值**：在访客选择主题之前，由 `darkMode` 品牌选项设置默认值：`"off"`（浅色）、`"auto"`（跟随系统，默认）或 `"force"`（深色）。访客一旦使用了切换按钮，其选择始终优先。
- **检测**：当主题为“跟随系统”时，SPA 会监听 `window.matchMedia('(prefers-color-scheme: dark)')`，并在操作系统偏好变化时自动重新应用主题。
- **应用**：SPA 在 `<html>` 上切换 `.dark` 类。Tailwind 的 dark 变体（`&:where(.dark, .dark *)`）会激活编译进每个组件的深色样式。
- **持久化**：明确选择的“浅色”/“深色”/“跟随系统”会存储在 `localStorage` 中。

### CSS 变量 {#css-variables}

浅色值声明在 `:root` 上；深色模式的覆盖值作用于 `.dark`，因此只要提供了 `customCssUrl` 中的租户品牌样式，它就始终优先。

| 变量 | 浅色 | 深色 |
|---|---|---|
| `--auth-bg` | `#f3f4f6`（或 `lightBg`） | `#030712`（或 `darkBg`） |
| `--auth-card-bg` | `#ffffff`（或 `lightCardBg`） | `#111827`（或 `darkCardBg`） |
| `--auth-heading` | `#111827` | `#f9fafb` |
| `--auth-logo-bg` | `transparent`（或 `lightLogoBg`） | `transparent`（或 `darkLogoBg`） |
| `--brand-primary` | `#2563eb`（或 `primaryColor`） | 浅色值（或 `darkPrimaryColor`） |

### 禁用或覆盖 {#disabling-or-overriding}

租户品牌样式始终优先。如需强制使用单一主题，请在 `customCssUrl` 中设置你自己的值：

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

如需完全移除主题切换按钮，可以走 npm 包路线，导入 `AuthLayout` 并在不带切换按钮的情况下渲染，或者 fork 该 SPA。

### 数据属性 {#data-attributes}

所有登录表单元素都带有 `data-auth` 属性，便于 CSS 定位和测试自动化：

| 属性 | 元素 |
|---|---|
| `data-auth="page"` | 页面主包装元素 |
| `data-auth="header"` | 页眉区域 |
| `data-auth="logo-chip"` | 包裹 Logo 图片的元素（仅在设置了 Logo 背景时才有内边距） |
| `data-auth="logo"` | Logo 图片 |
| `data-auth="app-name"` | 应用名称标题 |
| `data-auth="welcome-title"` / `data-auth="welcome-subtitle"` | 可选的 `welcomeTitle` / `welcomeSubtitle` 文字行（仅在设置后出现） |
| `data-auth="content"` | 主内容区域 |
| `data-auth="languages"` | 语言选择器 |
| `data-auth="language-trigger"` | 语言选择器的触发按钮 |
| `data-auth="theme-toggle"` | 浅色/跟随系统/深色主题切换按钮 |
| `data-auth="powered-by"` | “由 Authagonal 提供技术支持”页脚 |
| `data-auth="login-form"`、`"email-field"`、`"password-field"`、`"submit-button"` | 登录表单及其组成部分（仅登录页面） |

在你的自定义 CSS 中针对它们设置样式：

```css
[data-auth="header"] {
  background: linear-gradient(135deg, #667eea, #764ba2);
}
```

### 示例：自定义背景和字体 {#example-custom-background-and-font}

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}
```

## 定制层级 {#customization-tiers}

| 层级 | 你需要做什么 | 更新方式 |
|---|---|---|
| **仅配置** | 挂载 `branding.json` + Logo | 无缝更新：更新 Docker 镜像，保留你的挂载 |
| **配置 + CSS** | 添加带样式覆盖的 `customCssUrl` | 同上，CSS 类保持稳定 |
| **npm 包** | `npm install @authagonal/login`，定制 `branding.json`，构建到 `wwwroot/` 中 | 可更新，`npm update` 会拉取新版本 |
| **Fork SPA** | 克隆 `login-app/`，修改源码，自行构建 | 界面归你所有，服务器更新与之相互独立 |
| **完全自行编写** | 基于认证 API 构建完全自定义的前端 | 完全掌控，契约参见[认证 API](auth-api) |

带有自定义品牌（绿色主题，“Acme Corp”）的可运行示例请参见 `demos/custom-server/`。
