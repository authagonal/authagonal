---
layout: default
title: ローカライズ
locale: ja
---

# ローカライズ

ログイン UI は、標準で 11 のロケールを備えています。英語、簡体字中国語 (`zh-Hans`)、ドイツ語 (`de`)、フランス語 (`fr`)、スペイン語 (`es`)、ベトナム語 (`vi`)、ポルトガル語 (`pt`)、アラビア語 (`ar`)、アフリカーンス語 (`af`)、ヒンディー語 (`hi`)、日本語 (`ja`) です。サーバー API のレスポンスとサーバー側でレンダリングされるページも、同じく 11 言語すべてにローカライズされています。ローカライズの対象は、サーバー API のレスポンス、ログイン UI、そしてこのドキュメントサイトです。

## サポートされている言語 {#supported-languages}

| コード | 言語 | ログイン UI | サーバー API |
|---|---|---|---|
| `en` | 英語 (既定) | ✓ | ✓ |
| `zh-Hans` | 簡体字中国語 | ✓ | ✓ |
| `de` | ドイツ語 | ✓ | ✓ |
| `fr` | フランス語 | ✓ | ✓ |
| `es` | スペイン語 | ✓ | ✓ |
| `vi` | ベトナム語 | ✓ | ✓ |
| `pt` | ポルトガル語 | ✓ | ✓ |
| `ar` | アラビア語 (右から左) | ✓ | ✓ |
| `af` | アフリカーンス語 | ✓ | ✓ |
| `hi` | ヒンディー語 | ✓ | ✓ |
| `ja` | 日本語 | ✓ | ✓ |

## サーバー (API レスポンス) {#server-api-responses}

サーバーは、`IStringLocalizer<T>` と `.resx` リソースファイルによる ASP.NET Core 組み込みのローカライズ機能を使います。言語は HTTP ヘッダー `Accept-Language` から選択されます。

### ローカライズされるもの {#what-is-localized}

- パスワード検証のエラーメッセージ
- パスワードポリシーのラベル (`GET /api/auth/password-policy`)
- パスワードリセットフローのメッセージ (トークンのエラー、有効期限切れ、成功)
- 例外処理ミドルウェアが返す汎用的なエラーの説明
- 管理者によるユーザー管理のメッセージ (メールアドレスの確認、検証など)
- セッション終了の確認メッセージ
- サーバー側でレンダリングされるページ (メールアドレス確認の結果、セッション終了の確認ページとサインアウト済みページ)。アラビア語では `<html lang>` と `dir="rtl"` も含みます
- ライブラリが送信するメール (`EmailService`: 確認メール、パスワードリセット、「アカウントは既に存在します」という通知)。受信者の保存済みロケール (`AuthUser.Locale`)、なければリクエストのカルチャ、それもなければ英語で送信されます

### ローカライズされないもの {#what-is-not-localized}

- 機械可読の `error` コード (`"email_required"`、`"invalid_credentials"` など)。これらは API の契約であり、変わりません
- OAuth/OIDC のエラーコードと、トークン、認可、失効の各エンドポイントにおける開発者向けのエラーの説明
- 内部のログメッセージと例外メッセージ

### サーバーのローカライズをテストする {#testing-server-localization}

ローカライズされた任意のエンドポイントに `Accept-Language` ヘッダーを送ります。

```bash
# English (default)
curl https://auth.example.com/api/auth/password-policy

# Simplified Chinese
curl -H "Accept-Language: zh-Hans" https://auth.example.com/api/auth/password-policy

# German
curl -H "Accept-Language: de" https://auth.example.com/api/auth/password-policy
```

### リソースファイル {#resource-files}

サーバーの翻訳文字列はすべて、`src/Authagonal.Server/Resources/` 配下の `.resx` ファイルにあります。

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

## ログイン UI {#login-ui}

ログイン SPA は、クライアント側のローカライズに [react-i18next](https://react.i18next.com/) を使います。言語はブラウザーの `navigator.language` の設定から自動的に検出されます。

登録済みのロケールは、`login-app/src/i18n/index.ts` にある単一の `LANGUAGES` レジストリにまとめられています。このレジストリが i18next のリソース登録とすべての言語選択メニューの両方を決めるため、両者が食い違うことはありません。現在、登録済みのロケールはすべて既定の言語選択メニューに表示されます。`DEFAULT_LANGUAGES` は `LANGUAGES` とは別にエクスポートされており、将来ロケールを制限する場合に、呼び出し箇所に手を加えずに選択メニューから除外できるようになっていますが、現時点で除外されているものはありません。テナントも同じ方法で選択メニューを絞り込めます。`branding.json` の `languages` 配列は既定のリストを完全に置き換えます ([ブランディング](branding) を参照)。

有効な言語は `<html lang>` と `<html dir>` に反映されるため、右から左に書く言語 (`ar`) では認証カードが自動的に左右反転します。選択メニューでその場で言語を切り替えた場合も同様です。

### 言語の検出 {#language-detection}

検出の順序は次のとおりです。

1. **localStorage**: 以前の訪問で保存された設定
2. **クエリパラメーター**: `?lng=de` はブラウザーによる検出より優先されます
3. **ブラウザーの言語**: `navigator.language` (自動)
4. **フォールバック**: 英語 (`en`)

### 翻訳ファイル {#translation-files}

翻訳の JSON ファイルは、`login-app/src/i18n/` でアプリにバンドルされています。

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

### パスワードポリシーのラベル {#password-policy-labels}

パスワードリセットページは、`GET /api/auth/password-policy` が返す `rule` キーに基づいて、パスワード要件のチェックリストをクライアント側で翻訳します (認識できないルールについては、サーバーが提供する `label` にフォールバックします)。これにより、ブラウザーの `Accept-Language` ヘッダーが異なっていても、要件は UI で選択された言語で表示されます。登録ページは、`Accept-Language` に基づいてローカライズされた、サーバー提供の `label` の値を表示します。

### npm パッケージの利用者 {#npm-package-consumers}

`@authagonal/login` 経由でログインアプリを利用している場合、i18n インスタンスがエクスポートされています。

```typescript
import { i18n } from '@authagonal/login';

// Change language programmatically
i18n.changeLanguage('de');
```

## ドキュメント {#documentation}

ドキュメントサイトはディレクトリベースの方式を採用しています。英語のページはルートにあり、翻訳はロケールごとのサブディレクトリ (`/zh-Hans/`、`/de/`、`/fr/`、`/es/`、`/vi/`、`/pt/`、`/ja/`) にあります。サイドバーの言語切り替えドロップダウンで言語を切り替えられます。

## 新しい言語の追加 {#adding-a-new-language}

新しい言語 (例: イタリア語 `it`) のサポートを追加するには、次のようにします。

### 1. サーバー {#1-server}

英語の `.resx` ファイルをコピーして値を翻訳し、新しい `.resx` ファイルを作成します。

```
src/Authagonal.Server/Resources/SharedMessages.it.resx
```

`src/Authagonal.Server/Services/SupportedLocales.cs` の `SupportedLocales.All` に `"it"` を追加します。これは、リクエストのローカライズミドルウェアとサーバー側でレンダリングされるページの両方が参照する唯一のリストです。

```csharp
public static readonly string[] All = ["en", "zh-Hans", "de", "fr", "es", "vi", "pt", "ja", "ar", "af", "hi", "it"];
```

### 2. ログイン UI {#2-login-ui}

`en.json` をコピーして値を翻訳し、新しい翻訳 JSON ファイルを作成します。

```
login-app/src/i18n/it.json
```

`login-app/src/i18n/index.ts` の `LANGUAGES` 配列に登録します。この 1 つのエントリで i18next のリソースが登録され、すべての言語選択メニューに言語が追加されます。

```typescript
import it from './it.json';

// In the LANGUAGES array:
{ code: 'it', label: 'Italiano', resource: it },
```

### 3. ドキュメント {#3-documentation}

翻訳した Markdown ファイルを入れる新しいディレクトリを作成します。

```
docs/it/
  index.md
  installation.md
  quickstart.md
  ...
```

`docs/_config.yml` にロケールの既定値を追加します。

```yaml
defaults:
  - scope:
      path: "it"
    values:
      locale: "it"
```

`docs/_layouts/default.html` の言語切り替えに、その言語の選択肢を追加します。

## 新しい文字列の追加 {#adding-new-strings}

### サーバー {#server}

1. `SharedMessages.resx` にキーと英語の値を追加します
2. 各ロケールの `.resx` ファイルに翻訳した値を追加します
3. `IStringLocalizer<SharedMessages>` を使って文字列にアクセスします。

```csharp
// Inject via parameter
IStringLocalizer<SharedMessages> localizer

// Use with key
localizer["MyNewKey"].Value

// With format parameters
string.Format(localizer["MyNewKey"].Value, param1)
```

### ログイン UI {#login-ui-1}

1. `en.json` にキーと英語の値を追加します
2. 各ロケールの JSON ファイルに翻訳した値を追加します
3. コンポーネント内で `t()` 関数を使います。

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```
