---
layout: default
title: SAML
locale: ja
---

# SAML 2.0 SP

Authagonal には、独自実装の SAML 2.0 サービスプロバイダーが含まれています。サードパーティの SAML ライブラリは使わず、`System.Security.Cryptography.Xml.SignedXml` (.NET の一部) の上に構築されています。

## 対応範囲 {#scope}

- **SP 起点の SSO** (ユーザーが Authagonal から開始し、IdP にリダイレクトされます)
- AuthnRequest の **HTTP-Redirect バインディング** (任意で署名可能。下記を参照)
- Response (ACS) の **HTTP-POST バインディング**
- 接続ごとの SP 鍵ペアで復号される**暗号化アサーション** (`EncryptedAssertion`)
- **シングルログアウト** (SP 起点と IdP 起点、Redirect と POST のバインディング)
- 主な対象は Azure AD / Entra ID ですが、準拠した IdP であればどれでも動作します (Okta、OneLogin、Ping、Google Workspace、ADFS、Shibboleth の属性名に対応しています)

### サポートされないもの {#not-supported}

- Artifact バインディング
- AES-GCM によるアサーションの暗号化 (.NET の `EncryptedXml` の制約です。IdP 側で AES-CBC を設定してください。下記を参照)

**IdP 起点のサインインは機能し、タイルを設定し直す必要もありません**が、ユーザーをサインインさせるのは要求されていないアサーションではありません。`InResponseTo` のない Response は破棄され、ACS はブラウザを `/saml/{connectionId}/login` にリダイレクトします。そこで、そのブラウザに束縛された新しい AuthnRequest が発行されます。ユーザーはすでに IdP で認証されているので IdP はすぐに応答し、この往復はユーザーには見えません。IdP の `RelayState` は戻り先の URL として引き継がれるため、ユーザーはタイルに設定されたディープリンクにそのままたどり着きます。

アサーションを破棄しなければならないのは、次の理由からです。要求されていないアサーションを受け付けると、その IdP にアカウントを持つ誰もが、任意のユーザーエージェントにセッションをサインインさせられるようになります (攻撃者が自分のアカウントのために正当に取得したアサーションは、§4.1.4.3 のすべての規則を満たします)。また、`InResponseTo` を取り除いた同じアサーションを再送できるのであれば、SP 起点の経路でリクエスト Cookie を必須にしても何の意味もありません。フローを最初からやり直すことで、そうしたものを一切受け付けずにタイルを機能させ続けられます。最終的にサインインするのは、IdP が*新しい*やり取りで指名した人です。

やり直しはブラウザごとに 1 回だけです。AuthnRequest に対してさらに要求されていない Response で応答する IdP は、再びリダイレクトされるのではなく `error=saml_unsolicited` で拒否されます。そのため、設定を誤った IdP がリダイレクトのループを引き起こすことはありません。

代わりに要求されていないアサーションをそのまま受け付けるには、接続に `allowUnsolicitedResponses: true` を設定します (**既定では無効**)。有効にすると、要求されていないレスポンスについてはリクエスト ID のチェックが省略されますが、アサーション ID の 1 回限りの使用は引き続き強制されます (「セキュリティ」を参照)。

## Azure AD のセットアップ {#azure-ad-setup}

### 1. SAML プロバイダーを作成する {#1-create-a-saml-provider}

**オプション A: 設定ファイル (静的な構成に推奨)**

`appsettings.json` に追加します。

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "acme-azure",
      "ConnectionName": "Acme Corp Azure AD",
      "EntityId": "https://auth.example.com/saml/acme-azure",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
      "AllowedDomains": ["acme.com"]
    }
  ]
}
```

プロバイダーは起動時に初期投入されます。新しい接続では `ConnectionId`、`EntityId`、`MetadataLocation` が必須です (これらがないと起動に失敗します)。SSO ドメインのマッピングは `AllowedDomains` から自動的に登録されます。ただし組織スコープの接続は例外で、そのドメインはその組織の中でのみ照合されます。新しく初期投入されたプロバイダーには SP 鍵ペアが付与されません (そのため、署名付きの AuthnRequest、暗号化アサーション、署名付きのログアウトメッセージは使えません)。これらの機能を使うには管理 API を使ってください。

初期投入では `OrganizationId`、`JitProvisioningEnabled` (既定値 `false`)、`ChallengeMfaAfterLogin` (既定値 `true`)、`ProvisioningAttributeParams`、`AllowUninvitedJit`、`AllowUnsolicitedResponses` も設定できます。初期投入は保存済みの接続を読み取ってマージするため、既存の接続は、初期投入にフィールドがない SP 鍵ペア、貼り付けたメタデータ、NameID 形式、`signAuthnRequests`、アイコンを維持します。上記の動作フラグは起動のたびに初期投入の内容で書き込まれるため、省略したフラグは既定値に戻ります。

`EntityId` は **SP 側のエンティティ ID** (IdP に登録する識別子) であり、IdP のエンティティ ID ではありません。

> **自社のプライベートネットワーク上にある IdP。** `MetadataLocation` は https でなければならず、既定では公開ルーティング可能なアドレスに解決される必要があります。メタデータドキュメントにはすべてのアサーションの検証に使う証明書が含まれており、Authagonal は取得するすべての URL について内部のターゲットを拒否するからです。オンプレミスの IdP とフェデレーションするには、それを [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard) に指定してください。IdP が https のメタデータエンドポイントをまったく公開していない場合は、代わりに管理 API でドキュメントを `MetadataXml` に貼り付けてください。

**オプション B: 管理 API (実行時の管理向け)**

```bash
curl -X POST https://auth.example.com/api/v1/saml/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Acme Corp Azure AD",
    "entityId": "https://auth.example.com/saml/acme-azure",
    "metadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
    "allowedDomains": ["acme.com"]
  }'
```

API が `connectionId` (GUID) を生成し、`Location` ヘッダーとレスポンスボディで返します。その他の任意フィールドは次のとおりです。`metadataXml` (貼り付けたメタデータ。下記を参照)、`nameIdFormat` (下記を参照)、`signAuthnRequests` (AuthnRequest の署名を強制します)、`iconUrl` (ログインボタンのアイコン)、`jitProvisioningEnabled` (未知のユーザーを初回ログイン時に自動作成します。**既定では無効**なので、設定するまで未知のユーザーは拒否されます)、`challengeMfaAfterLogin` (既定値 `true`。`false` にすると IdP 自身の MFA を信頼します)、`provisioningAttributeParams` と `allowUninvitedJit` ([セルフサービス SSO](self-service-sso) を参照)、`organizationId` (接続を 1 つの組織にスコープします。[セルフサービス SSO](self-service-sso#organisation-scoped-connections) を参照)、`allowUnsolicitedResponses` (フローをやり直す代わりに IdP 起点のアサーションをそのまま受け付けます。既定では無効。上記を参照)。API で作成した接続には、SP 鍵ペアも自動生成されます (下記の「SP 鍵ペア」を参照)。

接続は `/api/v1/saml/connections[/{connectionId}]` に対する `POST` / `GET` / `PUT` / `DELETE` で管理します。`PUT` は部分更新で、リクエストで送信されたフィールドだけが変更されます。

### 2. Azure AD を設定する {#2-configure-azure-ad}

1. Azure AD → Enterprise Applications → New Application → Create your own
2. Set up Single Sign-On → SAML
3. **Identifier (Entity ID):** `https://auth.example.com/saml/acme-azure`
4. **Reply URL (ACS):** `https://auth.example.com/saml/acme-azure/acs`
5. **Sign on URL:** `https://auth.example.com/saml/acme-azure/login`

### 3. SSO ドメインのルーティング {#3-sso-domain-routing}

`AllowedDomains` が (設定ファイルまたは作成 API で) 指定されている場合、SSO ドメインのマッピングは自動的に登録されます。ユーザーがログインページで `user@acme.com` を入力すると、SPA は SSO が必須であることを検出し、「SSOで続行」を表示します。1 つのドメインは 1 つの接続にしかマッピングできません。別の接続がすでに使っているドメインは API が拒否します。

管理 API を使って実行時にドメインを管理することもできます。[管理 API](admin-api) を参照してください。

## 貼り付けたメタデータ XML {#pasted-metadata-xml}

メタデータの URL を公開していない IdP (Google Workspace) や、メタデータエンドポイントに SP から到達できない IdP (プライベートネットワーク上の ADFS) もあります。そうした場合は、代わりにメタデータドキュメントを貼り付けます。作成時または更新時に `metadataXml` を指定してください。`metadataLocation` と `metadataXml` のどちらか一方だけを指定する必要があり、更新時に一方を指定するともう一方は消去されます。

貼り付けたメタデータは保存時に検証され、SP が使うものだけを保持した正規の最小限の `EntityDescriptor` に**圧縮**されます (`SamlMetadataParser.Condense`)。保持されるのは、entityID、署名証明書、SSO エンドポイント、SLO エンドポイント (ある場合)、`WantAuthnRequestsSigned` フラグです。ベンダーのドキュメントは 100KB を超えることがあり (ADFS の `FederationMetadata.xml`)、Azure Table のプロパティの上限である 64KB を超えますが、SP が使う部分は数 KB です。解析できない貼り付けは 400 で拒否されます。ドキュメントには、署名証明書と `SingleSignOnService` を持つ `IDPSSODescriptor` が含まれている必要があります。

## NameID の形式 {#nameid-format}

`nameIdFormat` フィールドは、AuthnRequest で要求する `NameIDPolicy` の Format を制御します。

| 値 | 動作 |
|---|---|
| 省略 / null | `urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress` (従来からの既定値) |
| `"none"` | `NameIDPolicy` 要素を完全に省略します。ADFS で安全な設定です。ADFS は、要求された形式をクレームルールが出力しない場合にログイン全体を失敗させます (MSIS7070)。 |
| その他の値 | Format の URN としてそのまま送信されます (`urn:` で始まる必要があります) |

更新時に `""` を指定すると、既定の emailAddress に戻ります。SP メタデータは接続が要求する形式を公開します (`"none"` に設定した場合は `NameIDFormat` を省略します)。

## エンドポイント {#endpoints}

| エンドポイント | 説明 |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...&loginHint=...` | SP 起点の SSO を開始します。AuthnRequest を組み立て (該当する場合は署名し)、IdP にリダイレクトします。`loginHint` は、それを尊重する IdP (Entra、Google) に `login_hint` として渡されます。 |
| `POST /saml/{connectionId}/acs` | アサーションコンシューマーサービスです。SAML Response を受け取って検証し、ユーザーを作成/サインインさせます。 |
| `GET /saml/{connectionId}/metadata` | IdP の設定に使う SP メタデータの XML です。 |
| `GET /saml/{connectionId}/logout?returnUrl=...` | SP 起点のシングルログアウトです。ローカルセッションを終了し、IdP が SLO をサポートしている場合は IdP に LogoutRequest を送信します。 |
| `GET/POST /saml/{connectionId}/slo` | シングルログアウトのエンドポイントです。IdP 起点の LogoutRequest (Redirect または POST バインディング) と、SP 起点の SLO の LogoutResponse を受け取ります。 |

ログイン後の戻り先の URL は、RelayState ではなく、サーバー側で保存された AuthnRequest に (リクエスト ID をキーとして) 保持されます。SAML の仕様は RelayState を 80 バイトに制限しており、それを切り詰める IdP もあるからです。RelayState が参照されるのは IdP 起点のフローだけです。

## SP 鍵ペアと暗号化アサーション {#sp-keypair--encrypted-assertions}

API で作成したすべての接続には、SP 鍵ペアが自動生成されます。自己署名の 2048 ビット RSA 証明書 (有効期間 10 年) で、PKCS#12 として保存され、ホストのシークレットプロバイダーで保存時に保護されます。これはサーバー専用で、API から返されることは決してありません。この鍵ペアによって次が可能になります。

- **署名付きの AuthnRequest** (Redirect バインディングでの `SigAlg`/`Signature` によるクエリ署名)。IdP のメタデータが `WantAuthnRequestsSigned` を宣言している場合は自動的に、接続が `signAuthnRequests: true` を設定している場合は常に、署名が有効になります。
- **暗号化アサーションの復号。** SP メタデータが暗号化証明書を公開していると、ADFS は既定でアサーションを暗号化し始めます。ACS は SP の秘密鍵でそれを復号し、復号したアサーションを平文のアサーションと同じ署名/条件の処理に通します。サポートされるのは、鍵転送では RSA-OAEP (SHA-1/SHA-256)、データ暗号化では AES-128/192/256-CBC と 3DES です。**RSA-1.5 の鍵転送は拒否されます** (PKCS#1 v1.5 のアンラップは Bleichenbacher/ROBOT のオラクルになります)。また **AES-GCM はサポートされません** (.NET の `EncryptedXml` の制約です)。IdP は RSA-OAEP と AES-CBC で設定してください。どちらの失敗も、意図的に同じ固定のメッセージ (「Could not decrypt the assertion.」) を返します。アルゴリズムや失敗した段階を明示することこそがオラクルを作り出すからです。そのため、診断はエラーからではなく IdP の設定から行ってください。
- **署名付きのログアウトメッセージ** (Redirect バインディングでの LogoutRequest/LogoutResponse)。

SP メタデータは証明書を `signing` と `encryption` の両方の `KeyDescriptor` として公開し、接続が署名を強制している場合は `AuthnRequestsSigned="true"` を設定します。

## シングルログアウト {#single-logout}

ACS は、ログアウトを IdP のセッションと関連付けられるよう、SAML のセッションを認証 Cookie に記録します (`saml_connection`、`saml_name_id`、`saml_name_id_format`、`saml_session_index` の各クレーム)。

- **SP 起点:** `GET /saml/{connectionId}/logout` は常に、まずローカルの Cookie セッションを終了します (ユーザーはログアウトを求めており、IdP の SLO はベストエフォートだからです)。ブラウザのセッションがこの接続から得られたもので、IdP のメタデータが `SingleLogoutService` を公開している場合は、LogoutRequest (NameID + SessionIndex。SP が鍵を持っていれば署名付き) を Redirect バインディングで送信します。IdP の LogoutResponse は `/slo` に戻り、ユーザーは保存された `returnUrl` にたどり着きます。SLO エンドポイントを持たない IdP (Google) の場合は、ローカルのサインアウトだけが行われます。
- **IdP 起点:** IdP が `/saml/{connectionId}/slo` に LogoutRequest を送信します (Redirect の GET または POST バインディング)。署名付きのリクエストは、IdP のメタデータの証明書で検証されます。**署名のない、または検証できない LogoutRequest は、どのセッションも参照する前に 400 で拒否されます**。セッションに限定したフォールバックはありません。サードパーティのページが*被害者の*ブラウザをここへナビゲートさせた場合、渡されるのは攻撃者ではなく被害者のセッションなので、フォールバックを現在のセッションに限定しても、ログアウトさせられる相手を制限することにはならないからです。いずれにせよ Profiles §4.4.3.1 は、Redirect または POST バインディングでの LogoutRequest に IdP が署名することを求めており、接続のメタデータはすでに証明書を提供しています。そのため、署名のないリクエストを拒否しても、準拠した IdP が困ることは何もありません。IdP が SLO エンドポイントを持っている場合は、署名付きの LogoutResponse が返されます。フロントチャネルのみです。メッセージはユーザーのブラウザに届くため、Cookie セッションを終了するとそのブラウザだけがログアウトされます。

## メタデータのキャッシュと証明書のロールオーバー {#metadata-caching--cert-rollover}

- `MetadataLocation` から取得した IdP のメタデータは、メモリ内に 60 分間キャッシュされます (`Cache:SamlMetadataCacheMinutes` で変更可能)。キーは接続 ID ではなくメタデータの URL なので、テナント間でキャッシュが混同されることはありません。
- 貼り付けたメタデータは内容 (XML のハッシュ) をキーとしてキャッシュされ、再取得されることはありません。
- **署名の失敗による再取得:** IdP の証明書のロールオーバー直後に署名の検証が失敗した場合、それはキャッシュされたメタデータが古いことを意味します。まさにその失敗が起きると、キャッシュのエントリが削除されてメタデータが 1 回再取得され、検証が再試行されます。メタデータの場所ごとに 5 分のクールダウンがあるため、不正なアサーションを使って IdP のメタデータエンドポイントに大量のリクエストを送らせることはできません。この仕組みがなければ、証明書のロールオーバーの後、キャッシュの TTL が切れるまでログインが失敗し続けます (URL から取得したメタデータのみが対象です。貼り付けたメタデータには再取得するものがありません)。

## Azure AD との互換性 {#azure-ad-compatibility}

| Azure AD の動作 | 処理 |
|---|---|
| アサーションのみに署名 (既定) | Assertion 要素の署名を検証します |
| レスポンスのみに署名 | Response 要素の署名を検証します |
| 両方に署名 | 両方の署名を検証します |
| SHA-256 (既定) | SHA-256 と SHA-1 をサポートします |
| NameID: emailAddress | メールアドレスを直接取り出します |
| NameID: persistent (不透明) | 属性のメールアドレスのクレームにフォールバックします |
| NameID: unspecified | 属性のメールアドレスのクレームにフォールバックします |
| NameID: transient | ログインのたびに変わるため、フェデレーションのキーとして使われることはありません。代わりに IdP の安定したオブジェクト ID 属性が使われます。それが表明されていない場合、ログインは対処方法を示すエラーで拒否されます (persistent または emailAddress の NameID を設定するか、オブジェクト ID 属性を表明してください)。 |

## 属性のマッピング {#attribute-mapping}

属性は、`Name` と `FriendlyName` の両方で大文字と小文字を区別せずにインデックス化されます (Okta と Shibboleth は OID の Name と人が読める FriendlyName を出力するので、どちらでも一致させることがベンダーごとのマッピングを機能させる鍵です)。各フィールドは別名のリストを順に試します。最初の別名は Microsoft のクレーム URI なので Entra/ADFS での動作は変わらず、残りは Okta、OneLogin、Ping、Google、Shibboleth が既定で出力する分かりやすい名前と OID の名前をカバーします。

| フィールド | 受け付ける属性名 |
|---|---|
| email | `.../claims/emailaddress`, `email`, `mail`, `emailaddress`, `urn:oid:0.9.2342.19200300.100.1.3` |
| firstName | `.../claims/givenname`, `givenName`, `given_name`, `firstName`, `first_name`, `urn:oid:2.5.4.42` |
| lastName | `.../claims/surname`, `sn`, `surname`, `lastName`, `last_name`, `familyName`, `family_name`, `urn:oid:2.5.4.4` |
| displayName | `http://schemas.microsoft.com/identity/claims/displayname`, `displayName`, `urn:oid:2.16.840.1.113730.3.1.241`, `cn`, `urn:oid:2.5.4.3` |
| objectId | `http://schemas.microsoft.com/identity/claims/objectidentifier`, `objectGUID`, `user.objectid` |
| groups | `.../claims/groups`, `groups`, `memberOf`, `.../claims/role`, `urn:oid:1.3.6.1.4.1.5923.1.5.1.1` |

(`.../claims/...` は、完全な `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/...` または `http://schemas.microsoft.com/ws/2008/06/identity/claims/...` の URI の省略形です。)

メールアドレスを解決する優先順位: 明示的なメールアドレスの属性 (いずれかの別名) → 形式が emailAddress の場合の NameID → `@` を含む場合の `name` クレーム → 拒否 (メールアドレスは必須です)。

**グループは複数の値を持ちます:** 最初の 1 つだけでなく、すべての `AttributeValue` 要素 (グループのメンバーシップごとに 1 つ) が取り込まれます。

## JIT プロビジョニング {#jit-provisioning}

JIT プロビジョニングは**既定では無効**です。`jitProvisioningEnabled: true` の接続は、未知のユーザーを初回ログイン時に自動作成し (メールアドレスと姓名はアサーションから取得し、メールアドレスは確認済みとします)、安定したフェデレーションアイデンティティ (`saml:{connectionId}` + NameID、または transient の NameID の場合はオブジェクト ID) によってユーザーを接続に紐付けます。これが有効でない場合、未知のユーザーは拒否されます。`provisioningAttributeParams` を宣言した接続では、`allowUninvitedJit` が設定されていない限り、さらにログイン時にその招待のコンテキストが必要です。[セルフサービス SSO](self-service-sso) を参照してください。再訪したユーザーはまずフェデレーションの紐付けで照合され、メールアドレスだけで照合されることは決してありません。既存のローカルアカウントがメールアドレスで紐付けられるのは、接続の `AllowedDomains` がそのメールアドレスのドメインを含んでいる (その IdP がドメインを所有していることを管理者が明示的に表明している) 場合だけです。これにより、不正な IdP によるアカウントの乗っ取りを防ぎます。

## セッションの有効期間 {#session-lifetime}

アサーションの `AuthnStatement` に `SessionNotOnOrAfter` が含まれている場合、それは IdP 自身が確立したばかりのセッションの上限であり、Authagonal はそれを尊重します。ログイン Cookie はその時刻までに失効し (30 日以内である場合)、同じ上限が `session_max_exp` としてセッションに引き継がれ、そこから発行されるすべてのアクセス、ID、リフレッシュトークンを制限します。`SessionNotOnOrAfter` のないアサーションは、追加の上限を課しません。SAML には上流のリフレッシュトークンがないため、サインイン後に IdP がセッションを制限する方法はこれだけです。OIDC 接続については[フェデレーションセッション](federated-sessions)を参照してください。

## セキュリティ {#security}

- **リプレイの防止:** SP 起点のフローでは、`InResponseTo` が保存されたリクエスト ID (1 回限り) と照合されます。これとは別に、受け付けたすべてのアサーションの ID が保存され、1 回限りの使用が強制されます。これは IdP 起点のレスポンスや `InResponseTo` が取り除かれたレスポンスにも適用されます (アサーション ID は署名されたアサーションの内部にあるため、署名を壊さずに変更することはできません)。
- **時刻のずれ:** NotBefore/NotOnOrAfter に 5 分の許容幅があります
- **アサーションの経過時間の上限:** 自身の `IssueInstant` から 1 時間 (と時刻のずれの許容幅) を超えて提示されたアサーションは、`NotOnOrAfter` の内容にかかわらず拒否されます。また、未来の `IssueInstant` も拒否されます
- **発行者、宛先、オーディエンス:** Response と Assertion の `Issuer` は接続の IdP のエンティティ ID と一致する必要があり、署名付きの Response はこの ACS の URL と一致する `Destination` を含む必要があり、オーディエンスはこの接続の SP のエンティティ ID である必要があります
- **IdP の証明書の有効期間:** 固定された IdP の署名証明書が、自身の `NotBefore`/`NotAfter` の期間 (5 分の許容幅) の外にある場合は、アサーションでも Redirect バインディングのログアウト署名でも同様に使われません。そのため、ロールオーバーの後はメタデータを更新してください
- **ラッピング攻撃の防止:** 署名の Reference URI は、署名された要素の ID と一致する必要があります
- **オープンリダイレクトの防止:** ログイン後の戻り先の URL は、ルート相対パスでなければなりません (`/` で始まり、`//` やバックスラッシュを含まないこと。ブラウザは `\` を `/` として扱うためです)
- **ドメインの保証:** `AllowedDomains` が設定されている場合、それらのドメイン以外のメールアドレスのアサーションは拒否されます。そのため、ある接続が別の接続のドメインやローカルユーザーのメールアドレスを表明することはできません
- **MFA:** フェデレーションが証明するのは第1要素だけです。ユーザーの実効ポリシーが MFA を必須としている場合、ログインは完全に認証されたセッションを発行する代わりに、ローカルの MFA のチャレンジ/セットアップを経由します。ただし、接続が `challengeMfaAfterLogin: false` を設定している場合は除きます。
