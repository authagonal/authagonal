---
layout: default
title: ホーム
locale: ja
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

.NET 向けの OAuth 2.0 / OpenID Connect / SAML 2.0 認証サーバーです。ストレージはプラグイン方式で、自前の PostgreSQL または SQLite、Azure Table Storage、AWS (DynamoDB / S3 / Secrets Manager) を利用できます。

単一の自己完結型デプロイメントです。サーバーとログイン UI は 1 つの Docker イメージとして提供され、SPA は API と同じオリジンから配信されます。そのため、Cookie 認証、リダイレクト、CSP はいずれもクロスオリジンの複雑さなしに動作します。

> **マネージドサービスをお望みですか?** [Authagonal Cloud](https://authagonal.io) がこれらすべてを代わりに運用します。マルチテナント対応で、すべてのプランですべての機能が使え、SSO の接続ごとの料金もかかりません。→ [authagonal.io](https://authagonal.io)

## 主な機能 {#key-features}

- **OIDC プロバイダー**: authorization_code + PKCE、client_credentials、refresh_token、device_code の各グラントに対応し、ワンタイムのローテーションを行います
- **SAML 2.0 SP**: Azure AD を完全にサポートする独自実装 (レスポンス、アサーション、またはその両方への署名)。接続ごとの SP キーペアによる署名付き AuthnRequest と `EncryptedAssertion` の復号、そしてシングルログアウト (SP 起点と IdP 起点の両方) に対応します
- **動的 OIDC フェデレーション**: Google、Apple、Azure AD、その他 OIDC 準拠の任意の IdP に接続できます
- **多要素認証**: TOTP、WebAuthn/パスキー、リカバリーコード。クライアントごとのポリシー (`Disabled` / `Enabled` / `Required`) を `IAuthHook` でユーザーごとに上書きでき、フェデレーションログインにも適用されます
- **SCIM 2.0 プロビジョニング**: Entra ID、Okta、OneLogin からのユーザー/グループのインバウンドプロビジョニング。カーソルによるページング付き一覧と、ブラインドインデックスに基づく `eq` フィルターに対応します
- **OAuth 同意画面**: クライアントごとの同意。スコープに応じた再確認とグラント管理を備えます
- **デバイス認可グラント**: 入力手段の限られたデバイス (スマート TV、CLI、IoT) 向けの RFC 8628 フロー
- **トークンイントロスペクション**: リソースサーバーがトークンの有効性を確認するための RFC 7662
- **トークン署名**: ES256 のみです。アクセストークンには RFC 9068 の `typ: at+jwt` が付くため、リソースサーバーは id_token やログアウトトークンと区別できます。ただし **RFC 9068 への準拠は主張していません**。§2.1 はサポートするアルゴリズムに RS256 を含めることを求めていますが、本サーバーは RS256 を発行も受理もしません。アルゴリズムを 1 つに絞っているのは意図的な方針です。受け入れるアルゴリズムが 1 つ増えるたびに、検証側が誤ったアルゴリズムを使うよう誘導される経路が 1 つ増えるからです。
- **バックチャネルログアウト**: リライングパーティーへの OIDC Back-Channel Logout 1.0 通知
- **サーバーサイドセッション** *(オプトイン)*: `AddAuthagonalServerSideSessions` は SSO チケットをストレージに保持するため、認証 Cookie には不透明な ID だけが載ります。さらに、セルフサービスの `GET /api/auth/sessions` による一覧表示とデバイスごとの失効が有効になります ([認証 API](auth-api#sessions-self-service))
- **Backend-for-Frontend**: `Authagonal.Bff` (.NET) と `@authagonal/bff` (Node)。コンフィデンシャルクライアントとして動作する BFF により、SPA がトークンを保持することはありません ([BFF](bff))
- **GDPR セルフサービス** *(Authagonal Cloud)*: ホストされたアカウントページからのデータエクスポートとアカウント削除の予約。ログインアプリはそのための UI を同梱していますが、UI が呼び出すエンドポイント (`GET /api/v1/account/export`、`POST /api/v1/account/erasure`) は Cloud の認証ホストが提供するものであり、本ライブラリの提供範囲には**含まれません**。セルフホスト環境では、これらを実装するか、アカウントページから 2 つのボタンを外す必要があります。`MapFallbackToFile` は未実装のルートに 200 と SPA 自身の HTML で応答するため、未実装のエクスポートは、ダウンロードしてしまうのではなく、未実装であると判別する必要があります。
- **TCC プロビジョニング**: 認可時に下流アプリへ Try-Confirm-Cancel 方式でプロビジョニングします
- **ブランド設定可能なログイン UI**: JSON ファイル、ロゴ、色、CSS カスタムプロパティで実行時に設定でき、再ビルドは不要です。11 言語にローカライズされています
- **認証フック**: 監査ログ、独自の検証、Webhook のための `IAuthHook` 拡張機構
- **PII 暗号化の拡張点**: 保存時のフィールドレベル暗号化と、鍵付きブラインドインデックス (HMAC) による検索のための `IFieldCipher` / `IIndexTokenizer` 拡張点。リカバリーコードは `ISecretProvider` で暗号化されます
- **HashiCorp Vault Transit クライアント**: Vault の Transit エンジンに対する署名/検証、暗号化/復号、鍵付き HMAC。`IFieldCipher` や `IIndexTokenizer` の構築に使えます。JWT のリモート署名は組み込まれていません。トークン署名鍵は常に `ISigningKeyStore` にあるものです。
- **組み込み可能なライブラリ**: `AddAuthagonal()` / `UseAuthagonal()` で自分のプロジェクト内にホストし、サービスを独自に上書きできます
- **Native AOT 対応**: IL トリミングとソース生成による JSON シリアライズで高速に起動します
- **プラガブルなストレージ**: セルフホストの PostgreSQL または SQLite (クラウドアカウント不要)、あるいは低コストでサーバーレスと相性の良いバックエンドとして Azure Table Storage / AWS (DynamoDB / S3 / Secrets Manager)
- **バックアップとリストア**: 増分バックアップ (変更ログ駆動で、フルスキャンによる補完付き)、整合性検証、トゥームストーンによる削除の追跡
- **管理 API**: ユーザーの CRUD、SAML/OIDC プロバイダー管理、SSO ドメインルーティング、ユーザーになり代わるトークンの発行 (インパーソネーション)

## よくある統合 {#common-integrations}

チームが最もよく構築するフローのためのタスク別ガイドです。

- **[ユーザーのアップグレード](user-upgrade)**: パスワードのないアカウントの引き継ぎによって、ゲスト / SSO / 招待のアカウントを資格情報付きのアカウントに変え、確認時にゲストから通常メンバーへの昇格を実行します。
- **[セルフサービス SSO](self-service-sso)**: エンタープライズ接続の JIT プロビジョニング。招待制とセルフサービスによるオンボーディングの違い、外部 IdP が思わぬ落とし穴にならないようにする方法、フェデレーション前の中間画面について説明します。
- **[フェデレーションセッション](federated-sessions)**: 上流の IdP がセッションを失効させたときに、ローカルのセッションも失効させます (`RevalidateOnRefresh`)。
- **[Backend-for-Frontend (BFF)](bff)**: トークンをブラウザーに置かないようにします。バックエンド上の OIDC コンフィデンシャルクライアントに、httpOnly のセッション Cookie とトークンを注入する API プロキシを組み合わせたもので、.NET と Node で提供されます。
- **[WebSocket 認証](websocket-auth)**: トークンを露出させずに、BFF を通してブラウザーの WebSocket を認証します。
- **[エージェント認証](agentic-auth)**: ユーザーの権限を AI エージェントに委任します。登録済みエージェント、RFC 9396 によるきめ細かな権限、複合委任トークン (RFC 8693 `act`)、継続的な同意、ジャストインタイムの承認、ケイパビリティチケットを扱います。
- **[組織](organizations)**: 1 つのテナントで多数の顧客にサービスを提供します。`Organization` とメンバーシップのレコード、`organization` 認可パラメーター、トークン上の `org_id` / `org_slug` / `org_name`、組織スコープのロール、そして非メンバーの拒否を扱います。

## アーキテクチャ {#architecture}

```
Client App                    Authagonal                         IdP (Azure AD, etc.)
    │                             │                                    │
    ├─ GET /connect/authorize ──► │                                    │
    │                             ├─ 302 → /login (SPA)                │
    │                             │   ├─ SSO check                     │
    │                             │   └─ SAML/OIDC redirect ─────────► │
    │                             │                                    │
    │                             │ ◄── SAML Response / OIDC callback ─┤
    │                             │   └─ Create user + cookie          │
    │                             │                                    │
    │                             ├─ TCC provisioning (try/confirm)    │
    │                             ├─ Issue authorization code          │
    │ ◄─ 302 ?code=...&state=... ┤                                    │
    │                             │                                    │
    ├─ POST /connect/token ─────► │                                    │
    │ ◄─ { access_token, ... } ──┤                                    │
```

まずは [インストール](installation) ガイドから始めるか、[クイックスタート](quickstart) に進んでください。Authagonal を自分のプロジェクト内でホストするには [拡張性](extensibility) を参照してください。データ管理については [バックアップとリストア](backup-restore) を参照してください。変更履歴の全体は [変更履歴](https://github.com/authagonal/authagonal/blob/master/CHANGELOG.md) にあります。
