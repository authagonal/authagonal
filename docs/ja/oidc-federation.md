---
layout: default
title: OIDC フェデレーション
locale: ja
---

# OIDC フェデレーション

Authagonal は、外部の OIDC アイデンティティプロバイダー (Google、Apple、Azure AD など) に認証をフェデレーションできます。これにより、Authagonal を中央の認証サーバーとしたまま、「Google でログイン」のようなフローを実現できます。

## 仕組み {#how-it-works}

フェデレーションへの入り口は 2 つあります。

**ドメインベース (対話型ログイン):**

1. ユーザーがログインページでメールアドレスを入力します
2. SPA が `/api/auth/sso-check` を呼び出します。メールドメインが OIDC プロバイダーに関連付けられていれば、SSO が必須になります
3. ユーザーが「SSOで続行」をクリックし、外部 IdP にリダイレクトされます (メールアドレスが認可リクエストの `login_hint` であり、そのドメインが接続にルーティングされている場合は、ユーザーは `login_hint` を転送された状態で IdP に直接送られます)
4. 認証後、IdP が `/oidc/callback` にリダイレクトして戻します
5. Authagonal が id_token を検証し、ユーザーを紐付け (接続が JIT プロビジョニングを許可していればユーザーを作成し)、セッション Cookie を設定します

**RP からのヒント (`idp_hint`):**

下流のリライングパーティーは、メールアドレス/SSO ドメインの段階を経ずに、特定の上流 IdP へ直接ルーティングできます。`/connect/authorize` に `idp_hint={connectionId}` を付加します。

```
/connect/authorize?client_id=my-rp&scope=openid+email&...&idp_hint=google
```

リクエストが未認証の場合、Authagonal は元の `/authorize` の URL を `returnUrl` として保持したまま `/oidc/{connectionId}/login` にリダイレクトします。フェデレーションが完了すると、ユーザーはセッション Cookie を持った状態で `/authorize` に戻り、フローは通常どおり進みます。接続に `InteractionPath` が設定されている場合、ユーザーはまずそのログインアプリのページに送られます ([フェデレーションの前に情報を収集する](self-service-sso#collect-something-before-federating)を参照)。`ShowOnLogin: false` の接続はログインボタンとして表示されることはなく、この方法でしか到達できません。

## セットアップ {#setup}

### 1. OIDC プロバイダーを作成する {#1-create-an-oidc-provider}

**オプション A: 設定ファイル (静的な構成に推奨)**

`appsettings.json` に追加します。

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

プロバイダーは起動時に初期投入されます。`ConnectionId`、`MetadataLocation`、`ClientId`、`ClientSecret` は必須です (これらがないと起動に失敗します)。`RedirectUrl` は互換性のために受け付けられますが、無視されます。リダイレクト URI はブラウザがいるオリジン上にある必要があるため、リクエストごとに `{Issuer}/oidc/callback` として導出され、IdP に登録すべきなのはその URI です (異なる値が初期投入された場合は、無視された旨がログに出力されます)。`ClientSecret` は `ISecretProvider` によって保護されます (設定されていれば Key Vault、そうでなければ平文)。SSO ドメインのマッピングは `AllowedDomains` から自動的に登録されます。ただし組織スコープの接続は例外で、そのドメインはその組織の中でのみ照合されます。

初期投入では、下の表にあるすべての動作フラグも設定できます。**初期投入されたエントリは、起動のたびに保存済みの接続を置き換えます**。省略したフラグは既定値に戻るため、維持したいフラグはすべて設定に明記してください (省略しても残るのは `ConnectionName`、`IconUrl`、`OrganizationId` の値だけで、`CreatedAt` は保持されます)。

| フィールド | 既定値 | 効果 |
|---|---|---|
| `JitProvisioningEnabled` | `false` | 未知のフェデレーションユーザーを初回ログイン時に作成します。無効の場合、未知のユーザーは `access_denied` で拒否されます |
| `AllowUninvitedJit` | `false` | `ProvisioningAttributeParams` が宣言されている場合に、そのコンテキストなしで到着したユーザーもプロビジョニングします。[セルフサービス SSO](self-service-sso) を参照してください |
| `ProvisioningAttributeParams` | なし | 認可リクエストのクエリキーのうち、JIT でプロビジョニングされたユーザーにプロビジョニング属性として取り込むものです (`PassthroughParams` の内向きの対) |
| `PassthroughParams` | なし | 上流の認可 URL に転送されるクエリキーです。[パススルーのクエリパラメーター](#passthrough-query-parameters)を参照してください |
| `SessionExpClaim` | なし | [セッション有効期間の上限](#session-lifetime-cap)を参照してください |
| `ShowOnLogin` | `true` | `false` にすると「{provider}で続行」ボタンが非表示になり、接続には `idp_hint` を通じてのみ到達できます |
| `ChallengeMfaAfterLogin` | `true` | `false` にすると上流自身の MFA を信頼し、ローカルのチャレンジを省略します |
| `IsExternalConnection` | `false` | 顧客が所有するサードパーティの IdP であることを示します。`UseUpstreamSubjectAsUserId` と `AutoLinkExistingByEmail` は、設定されていても無効化されます |
| `UseUpstreamSubjectAsUserId` | `false` | JIT ユーザーのローカル ID を、新しい GUID ではなく上流の `sub` にします。ファーストパーティの接続専用です |
| `AutoLinkExistingByEmail` | `false` | `AllowedDomains` がそのドメインを含まない場合でも、メールアドレスで既存のローカルアカウントに紐付けます。ファーストパーティの接続専用です |
| `RevalidateOnRefresh` | `false` | [フェデレーションセッション](federated-sessions)を参照してください |
| `InteractionPath` | なし | `idp_hint` リクエストをフェデレーションする前に表示するログインアプリのパスです (`/` で始まる必要があります) |
| `OrganizationId` | なし | 接続を 1 つの組織にスコープします。[セルフサービス SSO](self-service-sso#organisation-scoped-connections) を参照してください |

> **自社のプライベートネットワーク上にある IdP。** `MetadataLocation` は https でなければならず、既定では公開ルーティング可能なアドレスに解決される必要があります。Authagonal は、取得するすべての URL について、URL の段階とソケットの段階の両方で内部のターゲットを拒否します。オンプレミスの IdP とフェデレーションするには、それを [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard) に指定してください。これにより、ディスカバリドキュメントが示す `token_endpoint`、`userinfo_endpoint`、`jwks_uri` を含む、やり取り全体が対象になります。それでも https は必須です。このドキュメントは上流のすべての `id_token` の検証に使う鍵を提供するものであり、プライベートネットワークは安全な通信路ではないからです。

**オプション B: 管理 API (実行時の管理向け)**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"],
    "jitProvisioningEnabled": true
  }'
```

作成時のボディは `connectionName`、`metadataLocation`、`clientId`、`clientSecret` (すべて必須) に加え、`iconUrl`、`redirectUrl` (無視されます。任意)、`organizationId`、`allowedDomains`、`passthroughParams`、`jitProvisioningEnabled` (既定値 `false`)、`challengeMfaAfterLogin` (既定値 `true`)、`interactionPath` を受け付けます。接続 ID はサーバーが生成し、`201` のボディで返されます (クライアントシークレットが返されることはありません)。`metadataLocation` は https でなければならず、作成時に送信先取得のガードでチェックされます。上の表にある他のフラグ (`SessionExpClaim`、`ShowOnLogin`、`IsExternalConnection`、`RevalidateOnRefresh` など) は作成ルートでは設定できません。設定ファイルから初期投入するか、ホスティングコードから `IOidcProviderStore` を通じて書き込んでください。OIDC 接続の更新ルートはありません。変更するには、削除して作成し直してください (または初期投入の設定を編集します)。`GET /api/v1/oidc/connections/{connectionId}` と `DELETE` で一通りが揃います。

### 2. SSO ドメインのルーティング {#2-sso-domain-routing}

`AllowedDomains` が (設定ファイルまたは作成 API で) 指定されている場合、SSO ドメインのマッピングは自動的に登録されます。ドメインのルーティングがなくても、`/oidc/{connectionId}/login` を使ってユーザーを OIDC ログインに誘導できます。

## エンドポイント {#endpoints}

| エンドポイント | 説明 |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...&loginHint=...` | OIDC ログインを開始します。PKCE + state + nonce を生成し、`returnUrl` から上流のスコープとパススルーのパラメーターを導出し、IdP の認可エンドポイントにリダイレクトします (`loginHint` がある場合は、`login_hint` として上流に送られます)。未知の接続には `404` を返します。 |
| `GET /oidc/callback` | IdP のコールバックを処理します。コードをトークンと交換し、id_token を検証し、プロトコル以外のすべてのクレームを `federated:*` として Cookie に取り込み、ユーザーを作成/サインインさせます。 |

## スコープとクレームの受け渡し {#scope-and-claim-flow-through}

下流の RP が `/connect/authorize` で要求したスコープのセットは、**標準の OIDC のセットに絞り込んだうえで**上流の IdP に転送されます。`openid`、`profile`、`email`、`address`、`phone` で、`openid` は常に含まれます。RP が要求したそれ以外のもの (独自の API スコープ、`offline_access` など) は上流を呼び出す前に破棄されます (唯一の例外は `RevalidateOnRefresh` を持つ接続で、上流のリフレッシュトークンを取得できるよう `offline_access` を再び加えます)。Google のような厳格な IdP は未知の値に対して `invalid_scope` を返しますし、上流はユーザーを識別できればよく、RP 自身のスコープが適用されるのは上流のトークンではなく Authagonal が発行するトークンだからです。上流の IdP がスコープに応じて id_token に載せたクレームはすべて Authagonal に戻り、`federated:<name>` クレームとして Cookie のチケットに保管され、次に `/connect/authorize` を通過する際に `OidcSubject.FederationClaims` に引き継がれます。そこから `ProtocolTokenService` が、`CustomAttributes` をゲートしているのと同じ `Scope.UserClaims` の許可リストに従って、それらを Authagonal が発行するトークンに再び出力します。キーが衝突した場合は、Authagonal 自身のユーザーストアの値が優先されます。これらのクレームは上流の IdP からそのまま届くものなので、上書きを許すと、顧客が管理する IdP が自社ユーザーについてスコープで公開される任意のクレームを言い換え、このサーバーの記録より優先させることができてしまうからです。保存済みの対応する値がない上流のクレームは、そのまま引き継がれます。

結果として、保持するクレームを接続ごとに許可リストで指定する必要はありません。上流が id_token に載せたプロトコル以外のクレームはすべて取り込まれます。そのうちどれが下流のトークンに届くかは下流のスコープの `UserClaims` で制御され、そこにクレームを宣言すれば値が引き継がれます。

`FederationClaims` は `CustomAttributes` とは別にリフレッシュのローテーションをまたいで保持されます。そのため、セッションごとのフェデレーションのコンテキスト (たとえば最初の認可時に取り込んだ共有リンクのトークン) はそのまま残り、ユーザーごとの属性は引き続きユーザーストアから最新の値が読み直されます。

## パススルーのクエリパラメーター {#passthrough-query-parameters}

`OidcProviderConfig.PassthroughParams` は、元の `/authorize` リクエストから上流 IdP の認可 URL へ引き継がれるクエリキーの、接続ごとの許可リストです。標準のセット (`scope`、`state`、`nonce`、PKCE) は常に転送されます。これは、上流が認証に必要とする一度限りの資格情報など、RP が指定する追加の値のためのものです (例: 共有リンク型 IdP 向けの `link_token`)。

キーが許可リストにある場合、Authagonal は元の `/authorize` のクエリ (`returnUrl` で運ばれます) からその値を取り出し、上流の URL に付加します。許可リストにないものは、警告なしに破棄されます。

## セッション有効期間の上限 {#session-lifetime-cap}

`OidcProviderConfig.SessionExpClaim` は、任意で指定する id_token クレームの名前で、その値 (Unix 秒) がローカルセッションの有効期間の上限になります。指定されていると、上流の値が Cookie のチケット上の `session_max_exp` として引き継がれ、発行される認可コードにも含まれます。アクセス / ID / リフレッシュトークンは、ローテーションで発行されるものも含め、どのトークンも上流のセッションより長く存続しないように制限されます。上流の IdP が、Authagonal の既定よりも短いセッションの期限を強制している場合に役立ちます。

## セキュリティ機能 {#security-features}

- **PKCE**: すべての認可リクエストで S256 の code_challenge を使います
- **nonce の検証**: nonce は state とともに保存され、id_token に存在し、かつ一致する必要があります
- **state の検証**: 1 回限り (`IOidcStateStore` によってアトミックに消費され、有効期限付きで永続化されます) **かつブラウザに束縛**されています。ログイン時に `/oidc` にスコープされた `SameSite=Lax` Cookie が設定され、コールバック時の `state` と一致する必要があります。そのため、攻撃者が自分で開始したフェデレーションフローのコールバック URL を被害者に送り付けて完了させることはできません (ログイン CSRF)
- **id_token の署名検証**: 鍵は IdP の JWKS エンドポイントから取得され、発行者、オーディエンス、有効期間が検証されます
- **userinfo へのフォールバック**: id_token にメールアドレスが含まれていない場合は、userinfo エンドポイントを試します。userinfo の `sub` は id_token の `sub` と一致する必要があり (OIDC Core 5.3.2)、一致しない場合はレスポンスが無視されます
- **安定したアイデンティティの紐付け**: 再訪したユーザーは、プロバイダー + `sub` で特定され、メールアドレスだけで特定されることは決してありません。フェデレーションのアイデンティティを**既存の**ローカルアカウントにメールアドレスで紐付けるには、接続の `AllowedDomains` がそのメールアドレスのドメインを含んでいる (その IdP がドメインを所有していることを管理者が明示的に保証している) か、ファーストパーティの接続で `AutoLinkExistingByEmail` が設定されている必要があり、そのドメインが別の接続にルーティングされている場合は拒否されます。すでに別の接続のフェデレーションアイデンティティに紐付いているアカウントを引き継ぐのは、この接続がそのドメインの権威である場合だけで、その場合は古い紐付けが削除されます。上流が表明した `email_verified` だけでは、既存のアカウントを乗っ取るのに*十分ではありません*
- **ドメインの強制**: `AllowedDomains` が設定されている場合、接続はそれらのドメイン内のアイデンティティしか表明できません (それ以外は `access_denied`)
- **JIT はオプトイン**: 接続が `JitProvisioningEnabled` を設定していない限り、未知のユーザーは `access_denied` で拒否されます。JIT が適用される場合でも、`email_verified` を表明しない上流はアカウントを作成できず、メールドメインが別の接続にルーティングされている接続も同様に作成できません
- **オープンリダイレクトのガード**: `returnUrl` は同一サイトの相対パスでなければなりません。プロトコル相対 (`//`) やバックスラッシュの形式は拒否されます
- **既定ではローカルの MFA も適用される**: フェデレーションが証明するのは第1要素だけです。MFA を登録済みのユーザー (またはクライアントのポリシーが MFA を必須とするユーザー) は、コールバック後にそのままサインインするのではなく、ローカルの MFA のチャレンジ/セットアップページを経由します。セッションに MFA のマーカーが付くのは、その後だけです。`ChallengeMfaAfterLogin: false` の接続ではこれが省略され、フェデレーションだけで MFA 認証済みとしてユーザーがサインインします
- **メタデータは限定的に信頼される**: ディスカバリドキュメントは https でなければならず、その URL はドキュメントが示す発行者に束縛されます。また上流の id_token は、非対称の署名アルゴリズム (RS/PS/ES の 256、384、512) の場合にのみ受け付けられます
- **組織への束縛**: 組織スコープの接続を通じてサインインしたユーザーはその組織のメンバーになり、セッションにはその `org_id` が含まれます

## Azure AD 固有の事項 {#azure-ad-specifics}

Azure AD は、メールアドレスを `emails` クレームの JSON 配列として返すことがあります (特に B2C の場合)。Authagonal は `email` クレームと `emails` 配列 (JSON 配列または単一の文字列) の両方を確認することで、これに対応しています。

## サポートされるプロバイダー {#supported-providers}

次をサポートする、OIDC に準拠した任意のプロバイダー:
- 認可コードフロー
- PKCE (S256)
- ディスカバリドキュメント (`.well-known/openid-configuration`)

動作確認済み:
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C

## 関連ガイド {#related-guides}

- [セルフサービス SSO](self-service-sso): JIT プロビジョニングの方針 (招待制かセルフサービスか)、接続の信頼レベル、フェデレーション前の中間ページ。
- [フェデレーションセッション](federated-sessions): `RevalidateOnRefresh` によって、上流での取り消しをローカルセッションに反映させます。
- [ユーザーのアップグレード](user-upgrade): フェデレーション / ゲストのアカウントが、ファーストパーティのパスワードを取得できるようにします。
