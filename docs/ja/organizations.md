---
layout: default
title: 組織
nav_order: 14
locale: ja
---

# 組織

組織とは、テナントの中にいる 1 つの顧客のことです。1 つのデプロイメントで多数の顧客にサービスを提供できます。各組織は独自の ID と独自のメンバーを持ち、アプリケーションが受け取るトークンには組織ごとの `org_id` が載ります。

## 概要 {#overview}

組織の機能ができる前は、ユーザーのレコードが `OrganizationId` という文字列 (TCC プロビジョニングまたは SCIM トークンへの結び付けによって書き込まれる) を持っており、その文字列が `org_id` クレームとして出力されていました。その組織が*何であるか*、誰が所属しているか、そもそもその組織として認証できるのかどうかを表す場所はどこにもありませんでした。

`Organization` は組織にレコードを与えます。変更不可の不透明な ID、変更不可でテナント内で一意のスラッグ、表示名、有効フラグ、メタデータの入れ物、そしてブランディングの上書きです。`OrganizationMembership` は誰が所属しているかを記録するもので、その組織のトークンの発行を実際に認可するのはこれです。

**何のためのものか。** 製品を顧客ごとにデプロイしている (アプリのインスタンスが 1 つ、データベースが 1 つで、ホスト名で解決する) ISV は、1 つのテナントと、顧客ごとに 1 つの組織を登録します。そのアプリケーションはアクセストークンから `org_id` を読み取り、自分がサービスを提供しているインスタンスでないものはすべて拒否します。以前はアプリケーションが自分で行っていたルーティングの判断を、今では認可サーバーが行い、署名されたクレームによって証明します。

**分離の境界は引き続きテナントです。** 署名鍵、発行者、ユーザーストアはそれぞれ 1 つです。組織はその境界の*内側で* ID を区分するものであり、2 つ目の境界を作るものではありません。1 つのテナント内の 2 つの組織はユーザーディレクトリを共有し、1 人のユーザーが複数の組織に所属することもできます。

**まだサポートされていないもの:**

- **組織選択画面はありません。** 複数の組織に所属するユーザーが、どの組織も指定していないリクエストを送ると、`account_selection_required` を受け取ります。これはリライングパーティーがパラメーターを付けて再試行することで対処できるエラーです。ユーザーに選択を求めるホスト型の画面はありません。
- **組織の管理の委任はありません。** 顧客自身の管理者が自分たちのメンバーを管理できるようにする権限はありません。
- **組織スコープの SCIM の分離はありません。** 組織に結び付けられた SCIM トークン (`ScimToken.OrganizationId`) は、それが作成するユーザーにタグを付け、ID が実在する組織を指している場合はそのユーザーを有効なメンバーにします ([SCIM トークンによるメンバーシップ](#membership-from-a-scim-token) を参照)。所有権のチェックは引き続き組織ではなく OAuth クライアントをキーとしているため、結び付けが決めるのはタグ付けであり、アクセスではありません。
- **組織スコープのグループはありません。** `groups` クレームと SCIM グループのメンバーシップはテナント全体のままです。組織スコープになるのはロールだけです。
- **組織の Webhook イベントも、組織スコープの監査もありません。** `IAuthHook` には組織のライフサイクルのイベント (作成、メンバーシップの付与や取り消し) がなく、既存のフックのペイロードは `organizationId` を含まず、監査ログには組織の列もインデックスもありません。
- **ロールで制限されたスコープは、認可時にテナントのロールに対してフィルタリングされます。** `Scope.AllowedRoles` は `/connect/authorize` で、組織が解決される前に、アカウントに直接割り当てられたロールに対して適用されます。そのため、`AllowedRoles` が組織スコープのロールでしか満たされないスコープは認可時に除外され、要求したスコープが 1 つも残らなければ `access_denied` で拒否されます。リフレッシュ時には同じ制限が、解決されたサブジェクトのロール (組織のロールを含みます) に対して実行されます。この 2 つが一致するまでは、スコープの制限はテナントのロールに基づいて行ってください。
- **このライブラリには組織のブランディングはありません。** `Organization.BrandingJson` は、ホストがテナントのブランディングに重ねてマージするために保存されます。このライブラリはそれを招待メールの 1 か所だけで読み取ります([ブランディング](branding#organisation-branding-in-invitation-emails))。ホストのブートペイロードが `organization` (`{ id, slug, name }`) を含む場合、ログインアプリは組織の名前 (「{name}にサインインしています」) を表示します。ライブラリ自体が認証前に組織を解決するのは、`organization` パラメーター、エントリが 1 つだけのクライアント制限、組織スコープの接続、またはホストの `ITenantContext.OrganizationId` による場合だけです。

## 組織の作成 {#creating-an-organization}

組織は `IOrganizationStore` を通じて保存されます。組織を作成するには、その前に永続的な実装が必要です。組み込みの既定実装は空で読み取り専用であり、書き込みは、欠けている登録を示すメッセージとともに拒否されます。これは意図的なものです。組織のレコードはトークンの発行を左右するものであり、プロセスローカルのディクショナリでは、失効を把握していないすべてのノードがトークンを発行し続けてしまうからです。

```csharp
await organizationStore.UpsertAsync(new Organization
{
    Id = "org_7f3a",              // opaque, immutable, emitted as org_id
    Slug = "international-sos",   // tenant-unique, immutable, emitted as org_slug
    DisplayName = "International SOS",
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Slug` は `^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$` に一致しなければなりません。小文字の英字、数字、および途中のハイフンからなる 1～64 文字で、先頭と末尾にはハイフンを置けません。小文字なのは、`organization` パラメーターがスラッグの検索の前に小文字化されるためです。大文字を含むスラッグは、どのリクエストからも決して解決できない値になってしまいます。

`Slug` はテナント内で一意でなければならず、**ID とスラッグは 1 つの名前空間を共有します**。ストアは、スラッグが既に別の組織に使われているアップサートを拒否し、同様に、スラッグが別の組織の ID と等しいもの、ID が別の組織のスラッグと等しいものも拒否します。1 つの値に 2 つのレコードが該当すると、`organization` パラメーターはある組織を意味するのに、保存されているすべての ID は別の組織を意味する、ということになってしまいます。

`Id` は `^[A-Za-z0-9._~-]{1,200}$` に一致しなければなりません。これは `organization` パラメーターと同じ形式なので、どの ID も必ずパラメーターとして送信できます。この形式から外れる ID はどのリクエストからも選択できず、そのような ID に制限されたクライアントはすべてのリクエストを拒否することになります。

`Id` には、スラッグに使えない文字 (大文字、`.`、`_`、`~`) を少なくとも 1 つ含める**ことを推奨します**。ID とスラッグは 1 つの検索用の名前空間を共有しており、すべて小文字の値はスラッグとして先に解決されます。そのため、それ自体がスラッグの形をした ID は、後で誰かがそのスラッグを取ったせいで作成時に拒否される可能性がありますが、スラッグに使えない文字を含む ID ではそれは決して起きません。新しい ID に推奨する形式は、`org_` プレフィックス付きの不透明な値 (`org_7f3a9c`) です。`_` はスラッグに使えないので、プレフィックスだけでこれが保証されます。規約は強制されておらず、このフィールドに既に入っている値は任意です。それらは下流アプリの TCC `/try` レスポンス (`TccProvisioningOrchestrator`) や、オペレーターによる SCIM トークンへの結び付け (`ScimToken.OrganizationId`。`ScimUserEndpoints` が新しいユーザーに記録します) から来たものです。

`Id` と `Slug` はどちらも事実上変更できません。リライングパーティーはこれらを自分がサービスを提供しているインスタンスと比較し、ハードコードするので、どちらかを変更すると、エラーメッセージのない障害になります。`DisplayName` は自由に変更でき、画面に表示されるのはこちらです。

## メンバーシップの付与 {#granting-membership}

```csharp
await membershipStore.UpsertAsync(new OrganizationMembership
{
    OrganizationId = "org_7f3a",
    UserId = user.Id,
    Status = MembershipStatus.Active,
    JoinedAt = DateTimeOffset.UtcNow,
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Status` は `invited`、`active`、`suspended` のいずれかです。**トークンの発行を認可するのは `active` だけです。** 削除ではなく停止にすれば、誰が誰を招待したかの記録が残ります。

### 招待 {#invitations}

`invited` 行には `InvitedByUserId`、`InvitedAt`、被招待者に提示されたロールが入ります。管理 API は組織に誰かを招待するときにこの行を書き込みます([ユーザーを招待する](#inviting-people))。ホストが自前で書き込むこともできます。ライブラリは次の 3 つの場合に、この行を `active` に昇格させます(ロール、招待者、招待日時は保持し、`JoinedAt` を設定します)。

- **招待を受け入れたとき。** 招待リンクからパスワードを設定するとメールアドレスが確認され、ライブラリのアクティベーションフックがそのユーザーの `invited` メンバーシップをすべて `active` に変更し、`organization.member.joined` を書き込みます。
- **組織スコープの SAML または OIDC 接続によるサインイン。** 組織自身の IdP がその人を保証することで、招待が受諾されます (`FederatedOrganizationBinding`、0.30.2)。これがなければ、SSO でしかサインインしない招待者は、メンバーシップによる制限でトークンを拒否されてしまいます。
- **自動メンバーシップ** (後述)。ユーザーが条件を満たす場合です。

`suspended` 行は、これらのどの経路でも昇格されることはなく、サインインによって変更されることもありません。

### 検証済みドメインと自動メンバーシップ {#verified-domains-and-automatic-membership}

`Organization.Domains` は組織が申請したメールドメインを保持します。各要素は `OrganizationDomain { Domain, VerificationToken, CreatedAt, VerifiedAt }` です。`Domain` は小文字で、空白と末尾のドットなしで保存されます。管理 API は申請されたドメインを保存し、`VerificationToken` を含む DNS TXT レコードで管理権限を証明し、`VerifiedAt` を設定します([ドメインの検証](#domain-verification))。独自の検証を持つホストは、自分で `VerifiedAt` を設定できます。

`Organization.AllowAutoMembership` (既定では無効) を使うと、ユーザーは招待なしで参加できます。組織が**明示的に**選択され (引き継がれたリフレッシュグラント、`organization` パラメーター、組織スコープの接続、またはまさにその組織だけに制限されたクライアント)、ユーザーが有効なメンバーシップを持っていない場合、次の条件が**すべて**満たされればユーザーはメンバーになります。

- 組織が有効で、`AllowAutoMembership` がオンになっている
- `AuthUser.EmailConfirmed` が true である
- メールアドレスの最後の `@` より後の部分を小文字にしたものが、`VerifiedAt` が設定されたドメインと**完全に**一致する。検証済みの `acme.com` は `user@eu.acme.com` を受け入れません。

行が存在しなければロールなしの `active` として作成され、`invited` の行は昇格され、`suspended` の行には決して手を触れません。これは認可時とすべてのリフレッシュ時に適用されます。そのため、フラグがオンの間は、条件を満たすメンバーの行を削除しても締め出すことはできません (次のトークンで再び参加します)。代わりに停止してください。`AuthUser.OrganizationId` から引き継いだだけの組織に自動参加することはありません。自動参加はそれぞれ Information レベルでログに記録されます。

### SCIM トークンによるメンバーシップ {#membership-from-a-scim-token}

`organizationId` 付きで発行された SCIM トークンは、その値を作成する各ユーザーの `org_id` として刻印します。`POST /api/v1/scim/tokens` は、SSO 接続と同様に、どの組織も指さない `organizationId` を `400 unknown_organization` で拒否し(形式が不正なものは `400 invalid_request`)、管理 API で発行したトークンが、どの組織にも載っていないタグを持つことはありません。ID が組織を指す場合、作成時にロールなしの `active` メンバーシップも書き込まれ、`scim.organization_member_added` が監査に記録されます。すでに何も指さない ID を持つトークンは、単なるタグのままです。作成時のみで、その後の同期でタグの付け直しやメンバーの追加は行われません。[SCIM](scim#tagging-a-connectors-users-with-an-organization) を参照してください。

### 一覧表示と削除 {#listing-and-deleting}

`IOrganizationStore.ListPageAsync(cursor, limit)` と `IOrganizationMembershipStore.ListByOrganizationPageAsync(organizationId, cursor, limit)` は 1 ページ分 (`Items` と不透明な `NextCursor`。最後のページでは null) を返します。`limit` は 1..200 の範囲に収められ、不正な形式のカーソルは `ArgumentException` をスローします。カーソルはキーセットに基づくため、読み取りの合間に行が追加・削除されても、ページがずれたり重複したりすることはありません。どちらにもページングなしの一覧に基づく既定の実装があるため、独自のストアもそのままコンパイルできます。Azure Table のストアは、サーバー側の範囲クエリでこれらをオーバーライドしています。

管理用の `DELETE /api/v1/profile/{userId}`、SCIM の `DELETE /scim/v2/Users/{id}`、または SCIM の再利用の経路でユーザーを削除すると、そのユーザーが持つすべてのメンバーシップも削除されます (`IOrganizationMembershipStore` を伴う `AccountArtefactPurge.PurgeAsync`。ストアを 3 つ取るオーバーロードはメンバーシップを何も消去しません)。独自の削除経路を持つホストもメンバーシップのストアを渡さなければなりません。そうしないと、組織は削除されたメンバーを一覧に表示し続けます。

## HTTP で組織を管理する {#administering-over-http}

管理 API(`AdminApi:Enabled`、管理スコープ)は、組織、メンバーシップ、ドメイン、招待を管理します。[管理 API](admin-api#organizations) のページに、すべてのリクエスト、成功ステータス、エラーコードがあります。概要は次のとおりです。

| ルート | 目的 |
|---|---|
| `GET /api/v1/organizations`、`POST /api/v1/organizations` | 一覧(ページ分割)と作成 |
| `GET /api/v1/organizations/{idOrSlug}`、`PUT /api/v1/organizations/{id}`、`DELETE /api/v1/organizations/{id}` | 取得、更新、削除(メンバーも一緒に削除) |
| `GET /api/v1/organizations/{id}/members`、`POST /api/v1/organizations/{id}/members`、`PUT /api/v1/organizations/{id}/members/{userId}`、`DELETE /api/v1/organizations/{id}/members/{userId}` | 一覧、追加、ステータスとロールの変更、削除 |
| `POST /api/v1/organizations/{id}/domains`、`POST /api/v1/organizations/{id}/domains/{domain}/verify`、`DELETE /api/v1/organizations/{id}/domains/{domain}` | メールドメインの申請、証明、解放 |
| `POST /api/v1/organizations/backfill` | 従来の `OrganizationId` の値を組織に変換 |
| `GET /api/v1/profile/{userId}/organizations` | ユーザーが所属する組織 |
| `POST /api/v1/profile/invite`、`POST /api/v1/profile/{userId}/resend-invite` | メールで招待、招待の再送 |

失敗時は `{ "error": "<code>", "error_description": "..." }` を、`400`(入力が不正)、`404`(見つからない)、`409`(競合)、または `502`(`invite_email_failed`)で返します。

## ドメインの検証 {#domain-verification}

`POST /api/v1/organizations/{id}/domains` に `{ "domain": "acme.com" }` を送ると、未検証のままドメインを申請し、公開すべきレコードを添えて `201` を返します。`recordName`(`_authagonal-org.acme.com`)と `recordValue`(`authagonal-org-verify=<token>`)です。トークンは 256 ビットの乱数で、申請ごとに 1 つです。その名前に、その値とまったく同じ内容の TXT レコードを公開してから、`POST /api/v1/organizations/{id}/domains/acme.com/verify` を呼び出します。

一致すると `VerifiedAt` が設定され、`organization.domain_verified` が書き込まれます。失敗は `409 verification_failed` で、`verified: false` を伴う `200` になることはなく、その説明には公開すべきレコードの名前と値、および見つかったものが記されます。検証済みのドメインを検証すると、変更なしで返されます。ドメインは、検証済みかどうかにかかわらず、最大で 1 つの組織にしか属せません(`domain_taken`)。ドメインを削除すると、今後の自動参加は止まりますが、誰も除外されません。

DNS の読み取りは `ITxtResolver`(`Authagonal.Core.Services`)を経由します。`ResolveTxtAsync(name, ct)` は、ある名前に公開された TXT の値を返し、何もなければ空のリストを返し、クエリが失敗したときは例外を投げます(`verification_failed` として報告され、例外のテキストは返されません)。既定は DnsClient で実際の DNS を読み取り、1 つのレコードの文字列を 1 つの値に連結します。`TryAdd` で登録されるため、`AddAuthagonal()` より前に登録したリゾルバー(社内のもの、DNS over HTTPS など)が優先されます。

## バックフィル {#backfill}

組織が導入される前、`AuthUser.OrganizationId` は自由テキストでした。`POST /api/v1/organizations/backfill` は、その文字列が完全に一致するユーザーをまとめ、異なる値ごとに組織を 1 つ作成し、各ユーザーに `active`(ロールなし)のメンバーシップを作ります。スラッグは値を小文字にし、それ以外の文字の連続をハイフン 1 つにまとめたものです(衝突した場合は `-2`、`-3` と続きます)。表示名は値そのもので、組織にはメタデータキー `legacyOrganizationId` の印が付きます。本文は `{ "dryRun": true }` です。**ドライランが既定**で、本文がない場合も同様であり、何も書き込みません。

再実行しても冪等です。ある値ですでに印が付いた組織は、名前が変更されていても再利用され、すでにメンバーシップを持つユーザーは `usersSkipped` に数えられます。応答には `dryRun`、`organizationsCreated`、`membershipsCreated`、`usersSkipped`、`organizations`(最初の 50 件。それぞれ `legacyValue`、`slug`、`users` を持つ)、`truncated` が含まれます。1 回の実行で走査するのは最大 50,000 ユーザーで、`truncated: true` は再実行が必要という意味です。実際の実行では `organization.backfill` が書き込まれます。

## ユーザーを招待する {#inviting-people}

`POST /api/v1/profile/invite` は `email`(必須)、`firstName`、`lastName`、`locale`、`organizationId`、`organizationRoles` を受け付けます。アカウントを即座に作成し(未確認で、使えるパスワードはありません)、リンクをメールで送ります。応答は `200` で `{ "userId", "email" }` です。`organizationId` を指定すると、ロール、`InvitedByUserId`(呼び出した管理者)、`InvitedAt` を持つ `invited` メンバーシップも書き込み、メールには組織名とそのブランディングが使われます([ブランディング](branding#organisation-branding-in-invitation-emails))。組織に関するすべての確認はアカウントが作られる前に行われるため、拒否された招待が孤立したユーザーを残すことはありません。招待できるのは新しいアカウントだけです。既存のアドレスは `409 user_already_exists` となり、既存のユーザーは `POST /api/v1/organizations/{id}/members` で追加します。

リンクには、有効期間 7 日の 1 回限りの `password_reset` グラントが含まれ、既存の `POST /api/auth/reset-password` が引き換えます。引き換え用の別のエンドポイントはありません。パスワードを設定するとアドレスが確認され、`IAuthHook.OnEmailConfirmedAsync` が発火し、ライブラリ自身のアクティベーションフックがそのユーザーの `invited` メンバーシップをすべて `active` に変更し(ロール、招待者、招待日時は保持し、`JoinedAt` を設定)、新しいメンバーを操作者として `organization.member.joined` を書き込みます。`suspended` または `active` の行には触れず、ストアの障害はログに記録されるだけで確認を失敗させることはなく、フックは冪等です。ホストがどのようなフックを登録していても、ライブラリが `OnEmailConfirmedAsync` を発火する場所ならどこでも実行されます。

`POST /api/v1/profile/{userId}/resend-invite` は新しいリンクを生成して送信します。`404 user_not_found`、アドレスが確認済みの場合は `400 already_accepted`(代わりにパスワードリセットを使用)、送信が例外を投げた場合は `502 invite_email_failed` です。最初の招待はより寛容で、送信に失敗してもログに記録されるだけで、ユーザーとリンクは存在します(招待は再送できるため)。再送された招待は、ユーザーの `invited` メンバーシップがちょうど 1 つのときに組織名を含みます。

メールは `IEmailService.SendUserInviteEmailAsync(toEmail, tenantName, acceptUrl, locale, ct, organization)` を経由します。これは既定の実装を持つインターフェースメンバーで、その本体は承認リンク付きのパスワードリセットメールを送信するため、独自の `IEmailService` を持つホストでも機能するリンクが届きます。独自の招待を送るにはオーバーライドしてください。組み込みの送信者は、ライブラリが対応している場合は招待する管理者の言語(認識できる最初の `Accept-Language`、なければリクエストの `locale`)で、なければ被招待者の言語で、それもなければ英語で招待を作成します。

`IInviteLinkBuilder`(`Authagonal.Core.Services`)は、リンクの宛先を決めます。`Task<string> BuildAcceptUrlAsync(string token, Organization? organization, CancellationToken ct = default)` です。既定は `{issuer}/accept-invite?p=<token>` を返し、組織への招待では `&organization=<slug>` が付き、どちらもエスケープされます。ページを別の場所(組織独自のドメインなど)で提供するには、`AddAuthagonal()` より前に独自の実装を登録してください(`TryAdd` で追加されます)。招待と再送は、すべてのリンクをこれを通じて生成します。

## 招待の承認ページ {#the-accept-invite-page}

ログインアプリは `/accept-invite` を、`/login` のベースパスの外側、ホストのルートで提供し、スタンドアロンサーバーの SPA フォールバックがそれを配信します。トークンを `p` から読み取り、リンクに組織が含まれていればその名前を表示し、新しいパスワードを 2 回入力させ、パスワードポリシーで検証して `/api/auth/reset-password` に送信します。トークンがない、または不明もしくは期限切れのトークンを持つリンクでは、「無効または期限切れ」というメッセージが表示され、フォームは表示されません。サーバーが拒否したパスワード(`weak_password`)では、サーバーのメッセージが表示されます。文言は 11 言語すべてに用意されています。npm パッケージをベースにしたホストは、`@authagonal/login` から `AcceptInvite` をインポートして `/accept-invite` にマウントします。

## コードからサービスを呼び出す {#calling-the-services-from-code}

エンドポイントは薄く作られています。`OrganizationOperations` と `UserInviteOperations`(`Authagonal.Server.Services`、スコープ付きで `TryAdd` により登録)がすべてのルール、エラーコード、監査行を担うため、ホスト独自の画面(ポータルのルートやツール)からこれらを呼び出して、同じ行を書き込めます。`OrganizationOperations` には `ListPageAsync`、`GetAsync`、`CreateAsync`、`UpdateAsync`、`DeleteAsync`、`ListMembersPageAsync`、`AddMemberAsync`、`UpdateMemberAsync`、`RemoveMemberAsync`、`AddDomainAsync`、`VerifyDomainAsync`、`RemoveDomainAsync`、`ListUserOrganizationsAsync`、`BackfillAsync` があり、`UserInviteOperations` には `InviteAsync` と `ResendInviteAsync` があります。監査の操作者は文字列として受け取り、HTTP の型は一切使いません。誰がこれらを呼び出せるか(誰が組織に招待できるか、招待をどのくらいの頻度で再送できるか)は、ホストのポリシーです。

それぞれ `OperationResult<T>` を返します。`Outcome`(`Ok`、`Invalid`、`NotFound`、`Conflict`、`Failed` のいずれか)、`Value`、`Error`(コード)、`Message`、`Fields`、`Succeeded` です。管理 API は結果を `400`、`404`、`409`、`502` に対応付けます。一覧は `ResultPage<T>`(`Items` と `NextCursor`)を返します。`ListUserOrganizationsAsync` と `BackfillAsync` は値をそのまま返します。

## 監査アクション {#audit-actions}

すべての書き込みは `IAuditLogger` を通り、エンティティ種別は `organization`(最後の 2 つは `user`)、操作者は呼び出した管理者の subject です。

| アクション | 書き込まれるとき | 詳細 |
|---|---|---|
| `organization.created` | 組織が作成されたとき | スラッグ |
| `organization.updated` | 組織が更新されたとき | スラッグ |
| `organization.deleted` | 組織が削除されたとき | スラッグ |
| `organization.member.added` | メンバーが追加されたとき | `user=<userId>` |
| `organization.member.updated` | メンバーのステータスまたはロールが変更されたとき | `user=<userId>` |
| `organization.member.removed` | メンバーが削除されたとき | `user=<userId>` |
| `organization.member.invited` | 招待によって `invited` メンバーシップが作られたとき | `user=<userId>` |
| `organization.member.joined` | メール確認時に `invited` メンバーシップが `active` になったとき(操作者はそのユーザー) | `user=<userId>` |
| `organization.domain_added` | ドメインが申請されたとき | `domain=<domain>` |
| `organization.domain_verified` | ドメインが証明されたとき | `domain=<domain>` |
| `organization.domain_removed` | ドメインが解放されたとき | `domain=<domain>` |
| `organization.backfill` | ドライランではないバックフィル | `organizationsCreated=<n> membershipsCreated=<n>` |
| `user.invited` | 誰かが招待されたとき | ローカル部をマスクしたアドレス(`***@acme.com`) |
| `user.invite_resent` | 招待が再送されたとき | 同上 |

## 組織スコープのロール {#organization-scoped-roles}

`OrganizationMembership.Roles` は、ユーザーがその組織の**中で**持つロールを保持します。名前はテナントの既存のロールカタログから取ります。ISV が「Auditor」を一度宣言すれば、各顧客はそれを自分たちの人員に付与します。

```csharp
membership.Roles = ["Auditor", "Site Manager"];
```

これらは、同じ `roles` スコープによる制限のもとで、ユーザーに直接割り当てられたロールや SCIM グループのメンバーシップによって付与されたロールと合わせて、`roles` クレームに統合されます。リソースサーバーは、ロールがテナント全体で付与されたのか組織ごとに付与されたのかを知る必要はありませんが、`roles` と合わせて `org_id` を読み取る必要は**あります**。同じロール名が、今では「この組織において」という意味を持つからです。

これには 4 つの制約があります。

- **ロールに寄与するのは、明示的に選択された組織だけです。** `organization` パラメーターまたはエントリが 1 つだけのクライアント制限で指定された組織です。`AuthUser.OrganizationId` から引き継がれた組織は何も寄与しません。メンバーシップによる制限にあるのと同じ非対称性です。
- **寄与するのは `active` のメンバーシップだけです。** 招待されたもののまだ受諾していないメンバーや停止されたメンバーは、何も認可しないのとまったく同じく、何も付与しません。
- **ロールが組織をまたぐことはありません。** ロールは選択された組織をキーとするメンバーシップの行から読み取られるため、ある組織で持つロールが別の組織向けに発行されたトークンに届くことはありません。
- **予約済みのプレフィックスは取り除かれます。** `tenant:` または `platform:` で始まるロールは統合の際に除外され、Warning レベルでログに記録されます。メンバーシップの行は顧客のスコープのデータなので、メンバーシップが `tenant:admin` を付与できると、「自分の組織を管理してよい」が「テナントを管理してよい」に変わってしまいます。直接割り当てられたロールと、SCIM グループからロールへのマッピングは影響を受けません。これらは認証された管理画面を通じてオペレーターが書き込むものであり、それこそがメンバーシップの行が持たない権限だからです。

ロールはリフレッシュのローテーションのたびにメンバーシップの行から読み直されるため、ロールの変更は、有効なセッションにも次のリフレッシュの時点で反映されます。

テナント全体のロールは、組織のロールに置き換えられるのではなく、組織のロールと**統合されます**。`tenant:admin` はポータルの権限であり、組織を選択しても残ります。

## 認可リクエストでの組織の選択 {#selecting-an-organization-on-an-authorization-request}

組織のスラッグまたは ID を指定して `organization` を送信します。

```http
GET /connect/authorize
  ?client_id=mobiom-web
  &response_type=code
  &redirect_uri=https://audit.example.com/callback
  &scope=openid%20profile
  &organization=international-sos
  &code_challenge=...&code_challenge_method=S256
```

値は `^[A-Za-z0-9._~-]{1,200}$` (RFC 3986 の非予約文字の集合) に一致しなければならず、それ以外は `invalid_request` になります。どのように解決されるかは、大文字小文字によって決まります。

- **大文字を 1 文字でも含む → ID としてのみ、完全一致で解決されます。** スラッグは小文字のみなので、そのような値はスラッグではあり得ません。それでも小文字に変換してスラッグのインデックスに問い合わせると、「この ID を小文字にしたものを自分のスラッグとする組織はあるか?」と尋ねることになり、もしあれば、ID を指定した呼び出し側に別の顧客が渡されてしまいます。
- **すべて小文字 → まずスラッグ、次に ID。** どちらの可能性もあり、リライングパーティーが通常送るのはスラッグです。ストアは ID とスラッグが同じ値を共有することを許さないので、曖昧さはありません。

`org_slug` と `org_id` も別名として受け付けます。どちらも他のプロバイダーで実際に使われており、このサーバーが選ばなかったほうを黙って無視するよりは、両方を受け付けるほうがよいからです。*異なる*組織を指す 2 つを送ると、`invalid_request` で拒否されます。そのリクエストは 2 つの意味を持っており、サーバーがどちらを選んでも、リライングパーティーにはもう一方を伝えたことになってしまうからです。3 つのいずれかを繰り返して指定すると、`redirect_uri` と同じ理由で拒否されます。

認可 URL 全体が `returnUrl` として運ばれるため、このパラメーターはログイン UI を往復しても失われません。また、[プッシュ型認可リクエスト](par) でも追加の作業なしに機能します。PAR エンドポイントは渡されたすべてのフィールドを保存し、`/connect/authorize` はクエリの代わりにプッシュされたペイロードを読み取るからです。

### 優先順位 {#precedence}

組織は次の順序で解決されます。

1. **リフレッシュでは、グラントの発行対象となった組織。**
2. **[組織スコープの SSO 接続](self-service-sso#organisation-scoped-connections) がこのセッションを認証した組織。** この中で、呼び出し側が主張したものではなく*証明された*唯一のソースです。ユーザーは、ただ 1 つの組織に属する IdP でサインインしたからです。別の組織を指定したリクエストは、黙ってその組織のトークンを発行されるのではなく、`access_denied` で拒否されます。
3. **`organization` パラメーター。**
4. **エントリを 1 つだけ持つ場合の `OAuthClient.RestrictedToOrganizationIds`。** 顧客ごとのアプリケーションは登録時に一度だけ自分の組織を指定し、そのリライングパーティーはパラメーターをまったく送りません。顧客ごとに 1 インスタンスの製品の多くが求めるのはこの形です。
5. **`AuthUser.OrganizationId`**: アカウント自身に保存されている組織。

ルール 1～4 は*明示的な*選択であり、メンバーシップの条件を満たす必要があります。ルール 5 はそうではありません。アカウントのレコード自体が所属の主張であり、2 つ目の主張を要求すると、対応する組織が作成された瞬間に既存のユーザー全員が締め出されてしまうからです。

誰も認証していない段階 (ホームレルムの判定、ログインページのプロバイダー一覧、`/sso-check`) ではユーザーもグラントも存在しないため、ルール 3、ルール 4、そして `ITenantContext.OrganizationId` だけで解決されます。[組織スコープの接続](self-service-sso#organisation-scoped-connections) を参照してください。

## クライアントを組織に制限する {#restricting-a-client-to-an-organization}

```csharp
client.RestrictedToOrganizationIds = ["org_7f3a"];
```

各エントリは組織 ID の形式 `^[A-Za-z0-9._~-]{1,200}$` に一致しなければなりません。空または不正な形式のエントリに対して、管理 API は `400 invalid_request` を返します。どの `organization` パラメーターでも送信できない ID を列挙した制限は何にも一致せず、何にも一致しない制限はすべてのリクエストを拒否するからです。`null` のリストは空に正規化されます。

空 (既存のすべてのクライアントがこれです) は制限なしを意味します。組織がリストにないリクエストは `access_denied` で拒否されます。エントリが 1 つのリストは、前述のルール 4 により選択も兼ねます。複数のエントリを持つリストは制限はしますが選択はしません。リクエストはそのうちの 1 つを指定しなければならず、指定しなければ `account_selection_required` で拒否されます。

## クレーム {#the-claims}

ID トークンとアクセストークンの両方に、次のクレームが含まれます。

| クレーム | 値 | スコープ |
|---|---|---|
| `org_id` | `Organization.Id` | なし。サブジェクトが組織を持つ場合は常に存在 |
| `org_slug` | `Organization.Slug` | なし。組織が実在するレコードである場合は常に存在 |
| `org_name` | `Organization.DisplayName` | `profile` |

**`org_id` と `org_slug` は、意図的にスコープで制限していません。** これらはプロファイルのデータではなく認可のコンテキストです。トークンがどの顧客のために動作してよいかを示すものであり、複数の顧客を扱うリソースサーバーが最初に確認する情報です。それは名前が必要かどうかを判断する前であり、多くの場合、プロファイルをまったく要求していないトークンに対して行われます。`profile` による制限のもとでは、`openid` だけを要求した API 専用のクライアントは組織を含まないトークンを受け取り、それは「どこにも属さない」と読めてしまいました。リソースサーバーは正当な呼び出し側を拒否するか、トークンをスコープなしとして扱い、それを使ってすべての顧客のデータを提供してしまいます。2 つ目の失敗はエラーとして表に出ず、問題になるのはこちらです。

これらを制限なしで出しても、クライアントが既に確定させていない情報は何も開示されません。クライアントはその組織を選んだか、1 つの組織に制限されているかのどちらかだからです。`org_name` は表示用の情報であり、それに基づいて認可を行うべきではないので、`profile` による制限を維持しています。

組織を持たないアカウントでは 3 つのいずれも出力されないため、以前に組織のクレームを含んでいなかったトークンは、今も何も含みません。

3 つはいずれも予約されています。どのスコープの `UserClaims` リストも、どのカスタムユーザー属性も、これらを生成したり上書きしたりすることはできません。これが最も重要なのは `org_slug` です。これは、リライングパーティーが自分がサービスを提供している顧客のインスタンスと比較する安定したキーだからです。自己申告できてしまえば、その比較の答えを申告者が決めることになります。

どのレコードにも解決されない組織 ID を持つアカウントは、`org_id` だけを出力します。`org_slug` がないことは「スラッグが存在しない」ことを意味し、「伏せられている」ことを意味することはありません。

**アプリケーションで `org_id` を検証してください:**

```csharp
var orgId = User.FindFirst("org_id")?.Value;
if (!string.Equals(orgId, ThisInstanceOrganizationId, StringComparison.Ordinal))
    return Results.Forbid();
```

## UserInfo、イントロスペクション、トークン交換 {#userinfo-introspection-and-token-exchange}

**`/connect/userinfo`** は、`org_id`、`org_slug`、`org_name`、`roles` を、ユーザーのレコードではなく**提示されたトークン**に基づいて返します。`org_id` と `org_slug` は、トークン自体で制限していないのと同じ理由により、トークンが含んでいればスコープの制限なしに常に返されます。`org_name` には `profile` が必要です。ユーザーが複数の組織に所属し得る以上、正しい答えを出せるソースはこれしかありません。アカウントが持つのは 1 つの既定値ですが、トークンはグラントが実際に発行された対象の組織を示しているからです。プロファイルのフィールド (`email`、`name`、`phone_number`) は引き続き最新の値です。それらはサブジェクトの現在の詳細であり、UserInfo の本来の目的はそれを返すことだからです。

そのため、アカウントのタグを付け替えても、既に発行されたトークンについて UserInfo が返す内容は変わりません。また、組織 B にサインインしたユーザーが、ID トークンに B を入れたのと同じサーバーから `org_id` A を告げられることは決してありません。

**`/connect/introspect`** は、トークンが含んでいれば `org_id` と `org_slug` を含めます。JWT を自分で検証するリソースサーバーはトークンからこれらを読み取り、代わりにイントロスペクションを行うリソースサーバーも今では同じ答えを得られます。

**RFC 8693 のトークン交換**は、サブジェクトトークンの `org_id`、`org_slug`、`org_name` を交換後のトークンに引き継ぎ、**交換を行う**クライアントの `RestrictedToOrganizationIds` をそれらに対して適用します。1 つの顧客にサービスを提供するために登録されたクライアントは、別の顧客のトークンを交換できず、組織をまったく含まないトークンも交換できません。`org_id` はスコープで制限されていないため、このチェックは `profile` スコープなしで発行されたリソースサーバー用のトークンでも機能します。以前の制限のもとでは、そのようなトークンは帰属のないものに見え、制限されたクライアントは自分自身のトラフィックを拒否されていました。拒否は `invalid_target` で、この経路での他のターゲットポリシーによる拒否と同じです。交換は既存のセッションを投影したものであり、自分が代理として動作している組織を落とした投影は、範囲が狭まったものではなく、帰属のないものになってしまいます。ホストの `ITokenExchangeSubjectTransformer` は、交換を意図的に別の組織に結び付け直すこともできます (コンテキストに結び付いた交換はそのためのものです) が、その場合は明示的にそうする必要があります。

## リフレッシュ {#refresh}

グラントの発行対象となった組織は、リフレッシュのローテーションのたびに引き継がれ、そのたびに再チェックされます。そのため、次の 3 つは、リフレッシュの有効期間が尽きるのを待たずに、次のローテーションで反映されます。

- メンバーシップの取り消しまたは停止
- 組織の無効化 (`Enabled = false`)
- クライアントの `RestrictedToOrganizationIds` の縮小

**いずれもリフレッシュを拒否しますが、グラントを失効させるものはありません。** 提示されたリフレッシュトークンは消費されず、ファミリーもそのまま残ります。そのため、条件が続く間はチェーンを拒否し続けられ、条件が解消された瞬間に再開します。メンバーシップを復元したり組織を再び有効にしたりすれば、新たなサインインなしにセッションが戻ります。グラントは自身の絶対的な有効期間に従って期限切れになります。これは無効化されたユーザーと同じ形で、そのユーザーのリフレッシュは `IsActive` が false の間は拒否されます。

実際にセッションを終わらせるには、グラントを失効させてください。リフレッシュトークンを指定した `POST /connect/revocation`、またはホスト側の `GrantRevocation` を使います。組織の無効化は制限であり、失効ではありません。

アカウントの組織を引き継いだだけのグラントは、代わりにローテーションのたびに組織が導き直されるため、アカウントのタグの付け替えは引き続き反映されます。

**組織の切り替えは、リフレッシュではなく新しい認可リクエストです。** 別の `organization` を指定して再び `/connect/authorize` を送信してください。既存のセッションが再利用されるので 2 度目のサインインは不要で、新しいグラントが始まります。リフレッシュエンドポイントで組織が変わることは期待しないでください。リフレッシュエンドポイントにはユーザーエージェントも同意もなく、グラントには、発行対象の組織について承認されたスコープが記録されているからです。

## メンバーシップによる制限をオフにする {#turning-the-membership-gate-off}

```csharp
organization.RequireMembershipForTokens = false;
```

既定ではオンです。アクセスのためではなく、ブランディングやルーティングのために組織を使うデプロイメントではオフにしてください。その場合、組織を指定できる人なら誰にでも、その組織のトークンが発行されます。メンバーシップが参考程度でしかない組織は境界ではありません。この選択は熟慮のうえで行ってください。

## ホストのフックから発行を拒否する {#refusing-an-issuance-from-a-host-hook}

`IAuthHook.OnTokenIssuingAsync` は、`authorization_code`、`refresh_token`、`device_code` の各グラントが何かを発行する直前に、解決されたサブジェクトとともに呼び出されます。

```csharp
public Task OnTokenIssuingAsync(TokenIssuanceContext context, CancellationToken ct = default)
{
    if (IsOffboarded(context.SubjectId, context.ClientId))
        throw new InvalidOperationException("This account is being offboarded.");
    return Task.CompletedTask;
}
```

例外をスローすると、発行は `access_denied` で拒否され、例外のメッセージが `error_description` になります。代わりに `ProtocolTokenException` をスローすると、独自の OAuth エラーを指定できます。リフレッシュの経路では、この制限はローテーションの**前**に実行されるため、拒否しても提示されたリフレッシュトークンは消費されず、ファミリーもそのまま残ります。「今は駄目」は「このセッションを終わらせる」ではありません。

これは既定のインターフェイスメンバーなので、オーバーライドしていない既存の `IAuthHook` は影響を受けません。2 つのエージェント向けの発行 (`client_credentials` とトークン交換で、いずれもエージェントプロファイルを伴うもの) では、以前とまったく同じように呼び出されます。

## 拒否 {#refusals}

| 条件 | エラー |
|---|---|
| 2 つのセレクターが異なる組織を指している | `invalid_request` |
| いずれかのセレクターが繰り返されている | `invalid_request` (`redirect_uri` には返さず、直接通知される) |
| 指定された組織が存在しない | `access_denied` |
| 組織が無効になっている | `access_denied` |
| クライアントがこの組織に対して許可されていない | `access_denied` |
| ユーザーが有効なメンバーではない (明示的な選択) | `access_denied` |
| クライアントが複数の組織にサービスを提供しており、リクエストがどれも指定していない | `account_selection_required` |

## デバイスフロー {#device-flow}

デバイスグラントにはパラメーターを運ぶ認可リクエストがないため、クライアントの制限、次にアカウントの既定値へとフォールスルーします。1 つの組織に固定する必要のあるデバイスクライアントは、エントリが 1 つだけの `RestrictedToOrganizationIds` で登録してください。

## 既存のデプロイメントのアップグレード {#upgrading-an-existing-deployment}

組織が存在するまでは何も変わりません。レコードがなければ、次のようになります。

- どのリクエストも組織を選択できません。
- メンバーシップによる制限は働きません。
- 旧来の `OrganizationId` を持つアカウントは、これまでとまったく同じく、ユーザーのレコードから `org_id` を出力し続けます。
- 組織を持たないユーザーのトークンには、3 つのクレームのいずれも含まれません。

組織を作成したら、クライアントやリライングパーティーをその組織に向ける**前に**メンバーシップを付与してください。明示的な選択には有効なメンバーシップが必要であり、ユーザーのレコードはあってもメンバーシップのない顧客は拒否されます。
