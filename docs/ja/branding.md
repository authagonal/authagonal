---
layout: default
title: ブランディング
locale: ja
---

# ログイン UI のブランディング

ログイン SPA は、Web ルートから配信される `branding.json` ファイルによって実行時に設定できます。再ビルドは不要で、設定ファイルとアセットをマウントするだけです。

## 仕組み {#how-it-works}

起動時に SPA は `/branding.json` を取得します。ファイルが存在しないか到達できない場合は、既定値が使われます。(ホストサーバーが設定を `<script type="application/json" id="authagonal-boot">` のブートペイロードとしてインラインで埋め込むこともできます。それがある場合、SPA は取得せずにそちらを読み込みます。) 設定で制御できるものは次のとおりです。

- アプリケーション名 (ヘッダーとページタイトルに表示)
- ロゴ画像と、モードごとに任意で付けられる背景の「チップ」
- プライマリカラー (ボタン、リンク、フォーカスリング) と、任意のダークモード用バリアント
- モードごとのページとカードの背景色
- パスワードを忘れた場合のリンクと登録リンクの表示/非表示
- ダークモードの既定値 (ライト / OS に従う / ダーク)
- 言語セレクターの選択肢
- 「Powered by Authagonal」フッター
- より踏み込んだスタイル調整のためのカスタム CSS

## 組織名 {#organisation-name}

リクエストに対して組織を解決するマルチテナントのホスト (たとえば 1 社の顧客に固定されたカスタムドメイン) では、`authagonal-boot` ペイロードに `branding` と `providers` に加えて 3 つ目のメンバーを含めることができます。

```json
{
  "branding": { "appName": "Acme Corp", "...": "..." },
  "providers": [],
  "organization": { "id": "org_123", "slug": "widgets-inc", "name": "Widgets Inc" }
}
```

リクエストがどの組織にも解決されなかった場合 (シングルテナントのデプロイや、このホストにカスタムドメインの固定がないデプロイ)、`organization` は `null` になります (またはメンバー自体が存在しません)。このライブラリ自体は、匿名の認証前リクエストに対して組織を解決しません (`OrganizationSelector` にはサインイン済みの `AuthUser` が必要です)。独自の認証前の解決手段を持つホスト (Authagonal Cloud はカスタムドメインごとに 1 つの組織を固定します) は、ブートペイロードを組み立てる際に `organization` を設定します。

`organization.name` が存在し、かつ `branding.appName` と異なる場合 (大文字小文字を区別せず、前後の空白を除いて比較するため、テナントと同じ名前の組織で「Acme / Acmeにサインインしています」のような表示にはなりません)、ログインカードは見出しの下に「{name}にサインインしています」というサブタイトルを表示します (`data-testid="login-org-name"`、i18n キー `login.signingInTo`)。これは共有の `AuthLayout` ヘッダーで 1 回だけ描画されるため、それを通じてマウントされるすべてのルート (サインイン、登録、パスワードを忘れた場合/リセット、MFA のチャレンジとセットアップのページ、デバイスページ、同意とエージェント同意、グラント、アカウント) で同じように表示されます。`organization` が存在しない、`null` である、またはその名前が `branding.appName` と一致する場合は何も描画されず、ヘッダーは通常の余白を保ちます。

## 設定 {#configuration}

`branding.json` ファイルを `wwwroot/` ディレクトリに置きます (または Docker コンテナにマウントします)。

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

### オプション {#options}

| プロパティ | 型 | 既定値 | 説明 |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | ヘッダーとブラウザのタブのタイトルに表示されます |
| `logoUrl` | `string \| null` | `null` | ロゴ画像の URL です。設定すると、テキストのヘッダーを置き換えます。 |
| `primaryColor` | `string` | `"#2563eb"` | ボタン、リンク、フォーカスインジケーターの 16 進カラーです |
| `supportEmail` | `string \| null` | `null` | サポート窓口のメールアドレスです (将来の利用のために予約) |
| `showForgotPassword` | `boolean` | `true` | ログインページの「パスワードをお忘れですか？」リンクの表示/非表示 |
| `showRegistration` | `boolean` | `false` | セルフサービス登録リンクの表示/非表示 |
| `customCssUrl` | `string \| null` | `null` | 既定のスタイルの後に読み込まれるカスタム CSS ファイルの URL です |
| `welcomeTitle` | `LocalizedString` | `null` | 認証ページのヘッダーの下に表示する任意の挨拶文です (プレーンな文字列または `{ "en": "...", "de": "..." }`)。未設定の場合は何も表示されません。 |
| `welcomeSubtitle` | `LocalizedString` | `null` | `welcomeTitle` の下に表示する任意の 1 行で、形式は同じです。未設定の場合は何も表示されません。 |
| `languages` | `array \| null` | `null` | 言語セレクターの選択肢です (`[{ "code": "en", "label": "English" }, ...]`)。`null` の場合、ノベルティロケールを除く同梱のすべての言語が表示されます ([ローカライズ](localization)を参照)。 |
| `poweredBy` | `boolean` | `true` | 認証ページの「Powered by Authagonal」フッターの表示/非表示 |
| `darkMode` | `"off" \| "auto" \| "force"` | `"auto"` | 訪問者がテーマを選んでいない場合の既定のテーマです。`"off"` (ライトのみ)、`"auto"` (OS の設定に従う)、`"force"` (常にダーク)。訪問者のテーマ切り替えが常に優先されます。 |
| `lightBg` | `string \| null` | `null` | ライトモードでのページの背景色 |
| `lightCardBg` | `string \| null` | `null` | ライトモードでのカード/フォームの背景色 |
| `darkBg` | `string \| null` | `null` | ダークモードでのページの背景色 |
| `darkCardBg` | `string \| null` | `null` | ダークモードでのカード/フォームの背景色 |
| `darkPrimaryColor` | `string \| null` | `null` | ダークモードで `primaryColor` を上書きします |
| `lightLogoBg` | `string \| null` | `null` | ライトモードでのロゴチップの背景 (後述) |
| `darkLogoBg` | `string \| null` | `null` | ダークモードでのロゴチップの背景 (後述) |

色の値は 16 進カラー (`#rgb`、`#rrggbb`、`#rrggbbaa`) か、`rgb()`/`rgba()`/`hsl()`/`hsla()` 式でなければならず、それ以外は無視されます。モードごとの色は、同梱のスタイルの後に `<style id="branding-theme-vars">` ルールとして挿入されます。`light*` の値は `:root:where(:not(.dark))` に置かれるためダークモードでは決して適用されず、ダークの値は `.dark` に、`primaryColor` は両モードの基本色なので `:root` に置かれます。`:where()` は詳細度を加えないため、`customCssUrl` で引き続きこれらすべてを上書きできます。

### ロゴの背景チップ {#logo-background-chip}

ロゴが白または透過のアートワークの場合、ライトのカードの上で見えなくなることがあります。`lightLogoBg` と `darkLogoBg` の一方または両方を設定すると、ロゴがその背景色を持つ余白付きの角丸「チップ」の中に描画されます。

```json
{
  "logoUrl": "/branding/logo.svg",
  "lightLogoBg": "#1c1e22",
  "darkLogoBg": "#1c1e22"
}
```

チップ (`--auth-logo-bg` CSS 変数で制御される `data-auth="logo-chip"` ラッパー) に余白と背景が付くのは、ロゴの背景が設定されている場合だけです。そのため、設定していないテナントでは、これまでどおりロゴがカードにそのまま表示されます。2 つのフィールドは互いに独立しています。`lightLogoBg` だけを設定すれば、ライトモードではロゴをチップに入れ、ダークモードではそのまま表示できます。

## Docker の例 {#docker-example}

ブランディング用のファイルをコンテナにマウントします。

```bash
docker run -p 8080:8080 \
  -v ./my-branding/branding.json:/app/wwwroot/branding.json \
  -v ./my-branding/logo.svg:/app/wwwroot/branding/logo.svg \
  -v ./my-branding/custom.css:/app/wwwroot/branding/custom.css \
  -e Storage__ConnectionString="..." \
  -e Issuer="https://auth.example.com" \
  authagonal
```

docker-compose の場合:

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

## カスタム CSS {#custom-css}

`customCssUrl` オプションは既定のスタイルの後に追加のスタイルシートを読み込むため、独自のルールが優先されます。フォントの変更、余白の調整、特定の要素のスタイル変更に便利です。URL は同一オリジンでなければなりません (`/branding/custom.css` のような相対 URL は問題ありません)。クロスオリジンのスタイルシートは警告なしにスキップされます。

### CSS カスタムプロパティ {#css-custom-properties}

ログイン UI は、きめ細かな制御のためにいくつかの CSS カスタムプロパティを公開しています。

| プロパティ | 既定値 | 説明 |
|---|---|---|
| `--brand-primary` | `#2563eb` | ボタン、リンク、フォーカスリングのプライマリカラー |
| `--auth-bg` | `#f3f4f6` | ページの背景色 |
| `--auth-card-bg` | `#ffffff` | カード/フォームの背景色 |
| `--auth-logo-bg` | `transparent` | ロゴチップの背景 (チップの余白は、ロゴの背景が設定されている場合にのみ表示されます) |
| `--auth-radius` | `0.5rem` | 認証カードの角の丸み |
| `--auth-font` | *(継承。システムフォントスタック)* | 認証カードのフォントファミリー |
| `--auth-heading` | `#111827` | 見出しのテキスト色 |

ここにある色の変数は設定フィールド (`primaryColor`、`lightBg`/`darkBg`、`lightCardBg`/`darkCardBg`、`lightLogoBg`/`darkLogoBg`) に直接対応しているため、単純な色の変更には設定を使い、カスタム CSS はそれ以外のことに使ってください。

カスタム CSS で上書きします。

```css
:root {
  --brand-primary: #059669;
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

ログイン UI は Tailwind CSS を使っています。カスタム CSS では、標準の HTML 要素と Tailwind のユーティリティクラスを対象にできます。エクスポートされている UI コンポーネント (`Button`、`Input`、`Card`、`Alert` など) は内部で Tailwind を使っています。

## ダークモード {#dark-mode}

ログイン SPA には、ライト、ダーク、**システム**のテーマが用意されています。テーマ切り替えはレイアウト内に常に表示されます。ユーザーの選択は `localStorage` の `auth-theme` キーに保存されます。

### 仕組み {#how-it-works-1}

- **既定値**: 訪問者がテーマを選ぶまでは、ブランディングオプションの `darkMode` が既定値を決めます。`"off"` (ライト)、`"auto"` (システム。既定)、`"force"` (ダーク) です。訪問者が切り替えを使うと、その選択が常に優先されます。
- **検出**: テーマが「システム」の場合、SPA は `window.matchMedia('(prefers-color-scheme: dark)')` を監視し、OS の設定が変わると自動的にテーマを適用し直します。
- **適用**: SPA は `<html>` の `.dark` クラスを切り替えます。Tailwind のダークバリアント (`&:where(.dark, .dark *)`) により、各コンポーネントにコンパイルされたダーク用のスタイルが有効になります。
- **保存**: 明示的に選ばれた「ライト」/「ダーク」/「システム」は `localStorage` に保存されます。

### CSS 変数 {#css-variables}

ライトの値は `:root` で宣言され、ダークモードの上書きは `.dark` に限定されているため、`customCssUrl` でテナントのブランディングが指定された場合は常にそちらが優先されます。

| 変数 | ライト | ダーク |
|---|---|---|
| `--auth-bg` | `#f3f4f6` (または `lightBg`) | `#030712` (または `darkBg`) |
| `--auth-card-bg` | `#ffffff` (または `lightCardBg`) | `#111827` (または `darkCardBg`) |
| `--auth-heading` | `#111827` | `#f9fafb` |
| `--auth-logo-bg` | `transparent` (または `lightLogoBg`) | `transparent` (または `darkLogoBg`) |
| `--brand-primary` | `#2563eb` (または `primaryColor`) | ライトの値 (または `darkPrimaryColor`) |

### 無効化と上書き {#disabling-or-overriding}

テナントのブランディングが常に優先されます。単一のテーマに固定するには、`customCssUrl` で独自の値を設定します。

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

テーマ切り替えを完全に取り除くには、npm パッケージを使う方法で `AuthLayout` をインポートして切り替えなしで描画するか、SPA をフォークしてください。

### データ属性 {#data-attributes}

ログインフォームのすべての要素には、CSS での指定やテスト自動化のための `data-auth` 属性が付いています。

| 属性 | 要素 |
|---|---|
| `data-auth="page"` | ページ全体のラッパー |
| `data-auth="header"` | ヘッダー部分 |
| `data-auth="logo-chip"` | ロゴ画像を囲むラッパー (ロゴの背景が設定されている場合にのみ余白が付きます) |
| `data-auth="logo"` | ロゴ画像 |
| `data-auth="app-name"` | アプリ名の見出し |
| `data-auth="welcome-title"` / `data-auth="welcome-subtitle"` | 任意の `welcomeTitle` / `welcomeSubtitle` の行 (設定されている場合にのみ存在) |
| `data-auth="content"` | メインコンテンツ領域 |
| `data-auth="languages"` | 言語セレクター |
| `data-auth="language-trigger"` | 言語セレクターを開くボタン |
| `data-auth="theme-toggle"` | ライト/システム/ダークのテーマ切り替え |
| `data-auth="powered-by"` | 「Powered by Authagonal」フッター |
| `data-auth="login-form"`、`"email-field"`、`"password-field"`、`"submit-button"` | サインインフォームとその構成要素 (ログインページのみ) |

カスタム CSS でこれらを対象にします。

```css
[data-auth="header"] {
  background: linear-gradient(135deg, #667eea, #764ba2);
}
```

### 例: 背景とフォントのカスタマイズ {#example-custom-background-and-font}

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}
```

## カスタマイズの段階 {#customization-tiers}

| レベル | 行うこと | 更新方法 |
|---|---|---|
| **設定のみ** | `branding.json` とロゴをマウントする | シームレスです。Docker イメージを更新し、マウントはそのまま維持します |
| **設定 + CSS** | スタイルを上書きする `customCssUrl` を追加する | 同上です。CSS クラスは安定しています |
| **npm パッケージ** | `npm install @authagonal/login` を実行し、`branding.json` をカスタマイズして `wwwroot/` にビルドする | 更新可能です。`npm update` で新しいバージョンを取り込みます |
| **SPA をフォーク** | `login-app/` をクローンし、ソースを変更して独自にビルドする | UI は自分で所有し、サーバーの更新とは独立します |
| **独自に作成** | 認証 API に対して完全に独自のフロントエンドを構築する | 完全に制御できます。契約については [認証 API](auth-api) を参照してください |

カスタムブランディング (緑のテーマ、「Acme Corp」) の動作例は `demos/custom-server/` を参照してください。
