---
layout: default
title: セルフサービス SSO
locale: ja
---

# セルフサービス SSO のオンボーディング

顧客の IdP への[接続をフェデレーション](oidc-federation)したら、次の問題は**一度もログインしたことのない人が現れたらどうなるか**です。Authagonal は、そうした未知のユーザーに対して、最も厳格なものから最も開放的なものまで 3 つの方針 (未知のユーザーをすべて拒否する、招待のコンテキストを必須にする、許可されたドメインから自動でプロビジョニングする) と、*外部の* IdP が落とし穴にならないようにするための制御を提供します。最初の 2 つは方針 1 でまとめて扱い、3 つ目は方針 2 で扱います。このガイドは、望む方針を選んで組み込むためのものです。

いずれも接続ごとの設定です。`OidcProviders` と `SamlProviders` の初期投入セクション、保存済みの `OidcProviderConfig` / `SamlProviderConfig`、そして管理 API です。関係する設定項目は次のとおりです。

| 設定項目 | 効果 | プロトコル |
|---|---|---|
| `JitProvisioningEnabled` | そもそも未知のユーザーを作成してよいか。 | OIDC、SAML |
| `ProvisioningAttributeParams` | ユーザーを作成する前に、リクエストに*招待のコンテキスト*を必須にします。 | OIDC、SAML |
| `AllowUninvitedJit` | 招待が**ない**場合でもセルフサービスでの作成を許可します (接続のタグが付きます)。 | OIDC、SAML |
| `IsExternalConnection` | サードパーティの IdP であることを示し、ファーストパーティ専用のフラグが適用されないようにします。 | OIDC のみ |
| `InteractionPath` | フェデレーションの*前に*ログインアプリのページ (名前/利用規約) を表示します。 | OIDC のみ |

管理 API はこれらすべてを公開しているわけではないため、それぞれをどこで設定できるかが重要です。

- **設定の初期投入 (`OidcProviders`、`SamlProviders`):** 上記のうち、そのプロトコルに存在するすべての設定項目。初期投入された接続は起動のたびに設定から再適用されるため、初期投入された接続の `JitProvisioningEnabled` と `AllowUninvitedJit` は、最後に保存された値ではなく初期投入の値になります。
- **SAML の管理 API** (`POST` / `PUT /api/v1/saml/connections`): `JitProvisioningEnabled`、`ProvisioningAttributeParams`、`AllowUninvitedJit`。
- **OIDC の管理 API** (`POST /api/v1/oidc/connections`): `JitProvisioningEnabled` と `InteractionPath` (`/` で始まる必要があります)。OIDC では `ProvisioningAttributeParams`、`AllowUninvitedJit`、`IsExternalConnection` は初期投入でしか設定できず、OIDC 接続の更新ルートはありません。[管理 API](admin-api) と [OIDC フェデレーション](oidc-federation)を参照してください。

## 方針 1: 招待制 (招待されていないユーザーを拒否する) {#posture-1-invite-only-reject-the-uninvited}

これが既定です。`JitProvisioningEnabled: false` の場合、未知の SSO ユーザーは即座に拒否されます (`access_denied`、「contact your administrator」)。すべてのユーザーを管理者または SCIM があらかじめ作成しておく必要がある場合に適しています。

JIT を使いたいものの、招待がある場合*だけ*に限りたい場合は、JIT を有効にした**うえで** `ProvisioningAttributeParams` を宣言します。これは、招待のコンテキストを運ぶ許可リスト上の `/authorize` のクエリパラメーター (例: `acceptKind`、`acceptToken`) を指定するものです。未知のユーザーがプロビジョニングされるのは、それらのパラメーターのうち少なくとも 1 つが実際に値付きで届いた場合だけです。招待のない単なる SSO ログインは `access_denied` (「This login requires an invitation」) で拒否されるため、紛れ込んだログインによって新しいアカウントや組織が勝手にセルフプロビジョニングされることはありません。パラメーターは、ユーザーが戻っていく先の `/authorize` の URL (SAML の場合は `RelayState`) のクエリから読み取られます。OIDC では、コールバックのリクエスト自身のクエリにもフォールバックします。

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "ConnectionName": "Acme (Entra)",
      "MetadataLocation": "https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration",
      "ClientId": "…", "ClientSecret": "…",
      "AllowedDomains": ["acme.com"],
      "JitProvisioningEnabled": true,
      "ProvisioningAttributeParams": ["acceptKind", "acceptToken"]
    }
  ]
}
```

設定すべき `RedirectUrl` はありません。コールバックの `redirect_uri` はリクエストごとに `{issuer}/oidc/callback` として導出されるため、その URI を上流の IdP に登録してください。初期投入された `RedirectUrl` は無視されます。

取り込まれたパラメーターは JIT ユーザーの `CustomAttributes` に入り、[プロビジョニングの `Try` ハンドラー](provisioning)に届きます。*値*を実際に判定するのはこのハンドラーです (例: 「この招待トークンはこのメールアドレスと一致するか」)。Authagonal は許可リストにあるキーを取り込み、それが有効かどうかはプロビジョナーが判断します。`Try` が `approved: false` を返した場合、作成されたばかりのユーザーは削除され、ブラウザは `400 provisioning_rejected` を受け取ります。

## 方針 2: セルフサービス (許可されたドメインのユーザーを自動でプロビジョニングする) {#posture-2-self-service-auto-provision-an-allowed-domain-user}

「顧客の従業員なら誰でもログインするだけでアカウントを得られる」ようにするには、`AllowUninvitedJit: true` を設定します。これで、**許可されたドメイン**からの未知のユーザーは招待のコンテキストがなくてもプロビジョニングされ、Authagonal はそのユーザーにどの接続を通じて来たかのタグを付けます。これによりプロビジョナーは、新しいテナントを立ち上げるのではなく、正しいテナントにユーザーを配置できます。ドメインのチェックが適用されるのは `AllowedDomains` が空でない場合だけです。ドメインを列挙していない接続は IdP が表明するどのドメインでも受け付けるため、すべてのセルフサービスの接続でドメインを列挙してください。

```json
{
  "ConnectionId": "acme-entra",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true,
  "ProvisioningAttributeParams": ["acceptKind", "acceptToken"],
  "AllowUninvitedJit": true
}
```

タグは `federated_connection` というカスタム属性として届きます。その値は接続の (`ConnectionId` ではなく) `ConnectionName` で、招待のコンテキストなしでユーザーが作成された場合にのみ書き込まれます。招待されたユーザーには、代わりに取り込まれたパラメーターが付きます。`Try` ハンドラーはこれによって処理を分岐します。

```javascript
app.post('/provisioning/try', async (req, res) => {
  const { userId, email, customAttributes } = req.body;

  if (customAttributes?.acceptToken) {
    // Invited: validate the invite and add them to that org.
    const org = await validateInvite(customAttributes.acceptToken, email);
    if (!org) return res.json({ approved: false, reason: 'Invalid invite' });
    stage(userId, { orgId: org.id, role: customAttributes.acceptKind ?? 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  if (customAttributes?.federated_connection) {
    // Self-service: no invite, but they came through a known enterprise connection.
    const org = await orgForConnection(customAttributes.federated_connection);
    stage(userId, { orgId: org.id, role: 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  return res.json({ approved: false, reason: 'No invite and no known connection' });
});
```

`AllowUninvitedJit` は接続ごとのオプトインです。`ProvisioningAttributeParams` を宣言していても、これを設定して**いない**接続は招待制のままです。

どの方針を選んだ場合でも、未知のユーザーが作成される前に、さらに 2 つのチェックが行われます。

- **ドメインが別の接続のものであってはなりません。** SSO ドメインのインデックスがユーザーのメールドメインを別の接続にルーティングしている場合、ログインは `access_denied` (「This email domain is managed by a different identity provider」) で拒否されます。
- **OIDC のみ: 上流がメールアドレスを確認済みである必要があります。** 上流が `email_verified` を true と報告しない場合 (id_token から読み取るか、メールアドレスが userinfo のレスポンスから得られた場合はそこから読み取ります)、ログインは `access_denied` で拒否されます。SAML のアサーションにはそのようなフラグがないため、SAML では代わりに `AllowedDomains` に依存します。

`federated_connection` は予約された属性名です。トークンに出力されることは決してなく、OIDC の上流の id_token にある同名のクレームは破棄され、匿名のセルフサービス登録でこれを設定することもできません。そのため、アカウントがどの接続を通じて来たかを表明できるのは SSO のコールバックだけです。

## 外部 IdP が落とし穴にならないようにする {#keep-external-idps-from-becoming-foot-guns}

OIDC 接続のフラグのいくつかは、**自社で**管理する接続では安全ですが、任意のサードパーティの IdP では危険です。

- **`UseUpstreamSubjectAsUserId`**: 上流がローカルのユーザー ID を決めます。自社の共有リンクのプロバイダーであれば ID がそろったままになりますが、顧客の IdP では*顧客が*自社のユーザー ID を選べてしまいます。
- **`AutoLinkExistingByEmail`**: ドメインの所有のチェックを省略して、フェデレーションのログインを既存のローカルアカウントにメールアドレスで紐付けます。受信トレイが確認済みのファーストパーティの接続なら問題ありませんが、外部の IdP ではアカウントを乗っ取るための手段になります。

サードパーティの接続を**外部**としてマークすると、これらのフラグは設定されていても無効化されます。

```json
{
  "ConnectionId": "acme-entra",
  "IsExternalConnection": true,
  "UseUpstreamSubjectAsUserId": false,
  "AutoLinkExistingByEmail": false
}
```

`IsExternalConnection` の既定値は `false` (ファーストパーティ) なので、既存の接続は影響を受けません。他者の IdP を指すすべての OIDC 接続でこれを設定してください。そうすれば、後で設定を誤っても、その IdP にローカルのアイデンティティの制御を渡してしまうことはありません。SAML 接続にはこれらのフラグがないため、無効化するものはありません (フェデレーションアイデンティティを既存のアカウントに紐付けるには、これに加えて、接続の `AllowedDomains` がそのメールアドレスのドメインを保証している必要があります。[OIDC フェデレーション: セキュリティ](oidc-federation)を参照してください)。

## フェデレーションの前に情報を収集する {#collect-something-before-federating}

ユーザーを IdP に送る**前に**ページを表示する必要がある場合があります。ゲストの表示名、利用規約のチェックボックス、プランの選択などです。`InteractionPath` (OIDC 接続のみ) は、最初に表示するログインアプリのルートを指定します。

```json
{ "ConnectionId": "guest-link", "InteractionPath": "/guest" }
```

未認証の `idp_hint={ConnectionId}` リクエストが `/connect/authorize` に届くと、Authagonal は IdP に直接ではなく `{LoginAppUrl}{InteractionPath}?returnUrl=<authorize url>&connection={id}` にリダイレクトします (`LoginAppUrl` の既定値は `/login` で、パスは `/` で始まる必要があります)。[組織](#organisation-scoped-connections)の唯一の接続またはドメインが一致した接続に自動でチャレンジする場合や、`prompt=login` が `idp_hint` を通じて再認証を強制する場合にも、同じリダイレクトが行われます。ページは必要な情報を収集し、その値を `returnUrl` のクエリ (`PassthroughParams` / `ProvisioningAttributeParams` がそこから読み取ります) に付加し、自身で `/oidc/{id}/login` に進みます。操作が不要だと判断したページは、すぐに先へ進めます。

## 組織スコープの接続 {#organisation-scoped-connections}

ここまでの説明はすべて**テナントレベル**の接続についてのものです。テナント全体で共有され、その `AllowedDomains` が、テナントが提供するすべてのログイン画面についてメールドメインを主張する接続です。テナントごとに 1 つの顧客とフェデレーションする場合は、これが適切な形です。しかし、1 つのテナントが多数の顧客の[組織](organizations)に対応し、それぞれが独自の IdP を持ち込む場合には不適切な形です。2 つの顧客が両方とも `contoso.com` を主張することはできませんし、ある顧客の「Contoso Entraで続行」ボタンが別の顧客のログイン画面に表示されるべきではありません。

接続に `OrganizationId` を設定すると、その接続は代わりにその組織に属します (管理 API で、作成時に、また SAML の場合は更新時にも設定できます。存在しない組織は `400 unknown_organization` になります)。

```json
{
  "ConnectionId": "acme-entra",
  "OrganizationId": "org_7f3a9c",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true
}
```

変わるのは 3 点で、それ以外は何も変わりません。

**その組織が選択された場合にのみ提示されます。** 組織スコープの接続がテナント自身のログイン画面に表示されることは決してなく、どの組織にも解決されなかったリクエストからは、たとえその `login_hint` が接続のドメインと完全に一致していても、到達されることはありません。

**そのドメインはその組織の中でのみ照合されます。** 組織スコープの接続は、意図的にテナント全体の SSO ドメインのインデックスに*書き込まれません*。そのため、1 つのドメインをテナントレベルで 1 回、組織ごとに 1 回ずつ主張できます。1 つの組織の中での 2 回目の主張は、両方のプロトコルをまたいで引き続き `domain_claimed` で拒否されるため、1 つのアドレスが組織の 2 つの IdP にルーティングされることはありません。接続を組織に移すとそのインデックスの行は削除され、元に戻すと (SAML の更新エンドポイントに `"organizationId": ""` を送信します) 再び登録されます。OIDC 接続には更新ルートがないため、そのスコープは作成時か `OidcProviders` の初期投入で設定します。

**それを通じてサインインした人は全員、その組織のメンバーになります。** SAML の ACS と OIDC のコールバックは、接続の組織を `org_id` として付与し、アカウント自身の `AuthUser.OrganizationId` (これはこのサインインについての表明ではなく、下流のプロビジョニングの成果物です) を上書きします。また、ユーザーがメンバーシップを持っていない場合は有効なメンバーシップを作成します。**招待中**のメンバーシップは受け入れられ、`active` になってロール、招待者、招待日時を維持します。組織自身の IdP がその人物を保証したからです。それ以外の既存のメンバーシップには手を付けません。`suspended` の行は停止されたままなので、再びサインインしても、管理者が取り消したアクセスを復活させることはできません。

### サインインの前に、リクエストがどの組織のためのものかを決める {#which-organization-a-request-is-for-before-anyone-signs-in}

ホームレルムディスカバリはユーザーが存在する前にこれに答える必要があるため、[認証後のセレクター](organizations#precedence)とは別に (ただし同じ順序で) 解決します。

1. リクエストの **`organization` パラメーター** (スラッグまたは ID)。
2. **`OAuthClient.RestrictedToOrganizationIds`** が、ちょうど 1 つのエントリを持つ場合。2 つ以上の場合は選択になりません。クライアントは複数の組織に対応しており、リクエストはどれも指定していないからです。
3. **`ITenantContext.OrganizationId`**: リクエストごとに組織を固定するホスト (たとえば組織ごとのカスタムドメイン) の場合です。シングルテナントのデプロイではすべて `null` です。

組織は存在して有効である必要があり、クライアントの制限がそれを許可している必要があります。それ以外の場合は*組織なし*に解決され、リクエストはこれまでどおりテナント全体の経路で進みます。特に、クライアントの制限によって除外されている組織を指定したパラメーターは、ここでは拒否されません。拒否は認証後にすでに行われており (`access_denied`)、それをログイン画面の前に移すと、未認証の呼び出し元が見分けられるリクエストが変わってしまうからです。

### `/connect/authorize` がそれをどう扱うか {#what-connectauthorize-does-with-it}

組織が解決された場合、テナント全体のどの規則よりも前に、次の処理が行われます。

- **その組織の**接続の 1 つを指定した `idp_hint` は、その接続に直接進みます。これには SAML も含まれ、テナント全体のヒントの経路 (OIDC のみ) では SAML に到達できません。それ以外を指定したヒントは次の処理に進みます。
- **接続がちょうど 1 つで、矛盾する `login_hint` がない場合** → その接続に直接進みます。ドメインを列挙していない接続は組織全体を主張します。ドメインを列挙している接続も、ヒントのアドレスのドメインがそれに含まれていない場合を除き、自動でチャレンジされます。
- **接続が複数ある場合** → ヒントのメールドメインによってその中から選びます。
- **一致しない場合** → テナント全体の `login_hint` とログインカードの動作になり、何も変わりません。

失敗してクエリに `error=` を付けて戻ってきたフェデレーションは、再びフェデレーションするのではなく、そのエラーをリライングパーティーに返します。そのため、自動のチャレンジがループすることはありません。

### ログインアプリから見えるもの {#what-the-login-app-sees}

`/api/auth/providers` と `/api/auth/sso-check` はどちらも `organization` クエリパラメーターを受け取り (なければ `ITenantContext.OrganizationId` にフォールバックします)、同じ規則で解決します。組織がある場合:

- `providers` は、まず**その組織の**ボタンとなる接続を、次にテナント自身の接続を一覧表示します。接続がボタンになるのは、`AllowedDomains` を列挙していない場合だけです (OIDC の場合は、さらに `ShowOnLogin` が有効である必要があります)。ドメインでルーティングされる接続には、メールアドレスを先に入力して `sso-check` を通じて到達します。組織が解決されない場合、組織スコープの接続は一覧から完全に除外され、他の組織の接続が一覧に表示されることは決してありません。
- 組織の接続がちょうど 1 つの場合、`providers` に **`autoChallenge`** が加わります。これは、アプリがカードを省略して直接進むべき接続についての完全なプロバイダーのレコード (`connectionId`、`name`、`type`、`loginUrl`、`iconUrl`) です。ID だけでなくレコード全体を含むのは、その接続がドメインでルーティングされるものや非表示のものであって、`providers` に含まれない可能性があるからです。それ以外の場合、このフィールドは省略されます。また、これは**参考情報**です。`/connect/authorize` 自身が同じ自動のチャレンジを行うため、これを無視するアプリでも同じ IdP に到達します。
- `sso-check` は、テナント全体のインデックスよりも**前に**組織の接続のドメインを照合し、組織がそのアドレスについて何も主張していない場合はテナント全体のインデックスに進みます。ドメインを列挙していない唯一の接続は、すべてのアドレスを主張します。

### トークン発行時 {#at-token-issuance}

組織スコープの接続を通じて確立されたセッションは、リフレッシュで引き継がれた値の次に優先度の高い取得元として、その組織を保持します (`organization` パラメーターやクライアントの制限よりも優先されます)。それが*証明された*唯一のものだからです。ユーザーは、まさにその組織に属する IdP で認証しています。別の組織を指定したリクエストは、黙ってその別の組織で発行されるのではなく、`access_denied` で拒否されます。組織の `RequireMembershipForTokens` は引き続き適用されます。コールバックがメンバーシップを作成するのはそのためです。

## 関連項目 {#related}

- [組織](organizations): レコード、メンバーシップ、クレーム、選択の規則。
- [OIDC フェデレーション](oidc-federation): 接続のセットアップとセキュリティモデル。
- [TCC プロビジョニング](provisioning): これらのフローが呼び出す `Try` ハンドラー。
- [フェデレーションセッションを上流と同期させる](federated-sessions): 上流がセッションを取り消したときに、ローカルセッションも取り消します。
