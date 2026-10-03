---
layout: default
title: ユーザーのアップグレード
locale: ja
---

# ユーザーのアップグレード (パスワードのないアカウントの引き継ぎ)

パスワードを持たない状態で始まるアカウントがあります。

- 共有リンクを開き、フェデレーションのログインによってジャストインタイムで作成された**ゲスト**
- SSO のサインインや組織への招待によって **JIT プロビジョニング**されたユーザー
- **SCIM** で送り込まれたディレクトリのユーザー

いずれも実在する Authagonal のユーザー (安定した ID を持ち、通常は下流でのアクセスもすでにプロビジョニングされています) であり、ローカルの資格情報を持っていないだけです。そのユーザーの入り口は、フェデレーション、リンク、または招待*でした*。

そうしたユーザーを**アップグレード**すると、ユーザーはファーストパーティのパスワードを設定できるようになり、通常は同時に製品との関係も格上げされます (ゲスト → 一般メンバー、試用 → 有料、「組織を作成する」)。Authagonal はこれを、正式なオプトインのフローとして提供しています。本人が同じメールアドレスで登録し直し、その受信トレイを管理していることを証明すると、その**既存の**アカウントが*その場で*引き継がれます (ユーザー ID は変わらないため、それまでのアクセスはすべて維持されます)。その間に、アプリは必要なアップグレードの処理を実行します。

> これは意図的に「パスワードのリセット」とは別のものにしています。パスワードのリセットは資格情報を持つアカウントを前提とし、リセットリンクをメールで送ります。引き継ぎは*資格情報のない*アカウントを資格情報のあるアカウントに変え、プロビジョニングを再実行するため、下流は格上げに対応できます。

## 使う場面 {#when-to-use-it}

下流の製品が「フェデレーションアイデンティティのメールアドレスで誰かが登録すること」を正当なアップグレードの経路として扱う場合に、引き継ぎのフローを有効にします。典型的なのは、共有リンクのゲストが本物のアカウントを作成しようと決めた場合です。デプロイにそのような経路がない場合は、無効のまま (既定) にしてください。その場合、既存のメールアドレスはすべて重複として扱われ、登録は通常の列挙に中立なレスポンスを返します。

## 1. 有効にする {#1-enable-it}

引き継ぎは、`Auth` 設定セクション (`AuthOptions` にバインドされます) の 1 つのオプトインのオプションで制御されます。

```json
{
  "Auth": {
    "AllowPasswordlessAccountClaim": true,
    "ClaimAllowedAttributeKeys": ["org_name", "plan"]
  }
}
```

- **`AllowPasswordlessAccountClaim`** (既定値 `false`): フローを有効にします。
- **`ClaimAllowedAttributeKeys`** (既定値は空で、予約されていないすべてのキーを許可): 引き継ぎがアカウントに持ち込めるカスタム属性のキーの許可リストです ([アップグレードのコンテキストを渡す](#4-pass-upgrade-context-safely)を参照)。引き継ぎで任意の属性を注入できないよう、プロビジョナーが想定するキーを列挙してください。名前に反して、同じリストは通常のセルフサービス登録の `customAttributes` も絞り込みます。

フラグが無効の場合、既存のメールアドレスは重複です。有効の場合、既存の**資格情報のない**アカウント (`PasswordHash` がないもの) を引き継げるようになります。すでにパスワードを持つアカウントには**決して**手を付けません。登録し直しで本物の資格情報を上書きすることはできません。

## 2. 引き継ぎの全体の流れ {#2-the-claim-end-to-end}

ユーザーは、引き継ぎたいアカウントのメールアドレスで、通常の登録エンドポイントを呼び出します。

```bash
# 1. The user re-registers with the SAME email as their guest/SSO/invite account.
curl -X POST https://auth.example.com/api/auth/register \
  -H "Content-Type: application/json" \
  -d '{
    "email": "grace@acme.com",
    "password": "a-strong-passphrase",
    "firstName": "Grace",
    "lastName": "Hopper",
    "customAttributes": { "org_name": "Acme Inc" }
  }'
# → 201 Created (enumeration-neutral: the same response a brand-new signup returns)
```

レスポンスは `{ "success": true, "userId": "..." }` を含む `201` です。引き継ぎの経路では `userId` は本物のアカウント ID ではなく使い捨ての値なので、レスポンスから引き継ぎとまったく新しい登録を見分けることはできません。

この時点では、まだ何も有効になっていません。サーバーはパスワードとプロフィール/属性を**仮置き**し、新しい確認リンクをメールで送ります。ユーザーはそれを開きます。

```
GET https://auth.example.com/api/auth/confirm-email?token=<from the email>
```

この `GET` は、ワンクリックの確認ページを表示するだけです (そのため、URL を取得するメールスキャナーやリンクのプリフェッチャーがトークンを消費することはありません)。そのページで**ボタンを押す**と `POST /api/auth/confirm-email` が送信され、これによって仮置きされた資格情報が昇格し、アップグレードが実行されます。同じ `POST` は、トークンをクエリパラメーターや JSON ボディ (`{ "token": "..." }`) としても受け付けます。JSON の呼び出し元は `{ "message": "Email confirmed successfully.", "appLink": ... }` を受け取り、ページからのフォームの送信は `/login?email_confirmed=1` にリダイレクトされます。その後、ユーザーは新しいパスワードで通常どおりサインインします。

### サーバーが行うこと {#what-the-server-does}

1. **登録**: アカウントが存在しパスワードを持たないため、リクエストは引き継ぎとして扱われます。選ばれたパスワードはハッシュ化されて `PendingPasswordHash` に入り (効力はなく、どの認証経路もこれを読みません)、姓名と許可リストに含まれる `customAttributes` は `PendingClaimJson` に仮置きされます。同時にアカウントのセキュリティスタンプがローテーションされ、すでに受信トレイにある確認リンクはすべて無効になります。それ以外にアカウントが変更されることは**ありません**。アカウントのメールアドレスが元のフローですでに確認済みであっても、確認メールが送信されます。以前の証明は*別の*人物のものであり、引き継ぎには独自の証明が必要だからです。リンクには、そのために仮置きされた資格情報のダイジェスト `pc=` が含まれます。
2. **確認**: 確認が所有の証明です。サーバーはリンクが*現在*仮置きされている資格情報に束縛されていることを確認し、仮置きされたプロフィール/属性を適用し、**`ReprovisionAsync`** (次のセクションを参照) を実行してから、`PendingPasswordHash` を `PasswordHash` に昇格させ、再びセキュリティスタンプをローテーションします。プロビジョニングがアップグレードを拒否した場合、仮置きされた資格情報とプロフィールは破棄され、アカウントはパスワードのない、再び引き継ぎ可能な状態のままになるため、中途半端な状態が残ることはありません。

1 回目の引き継ぎが確認される前に 2 回目の引き継ぎが送信されると、仮置きされた資格情報が置き換えられ、最初のリンクは機能しなくなります。そのリンクで確認すると `claim_superseded` が返されます (JSON の `400`、または確認ページからの `/login?error=claim_superseded` へのリダイレクト)。管理者の「確認メールを送信」の操作によるものなど、`pc=` ダイジェストのないリンクも、資格情報が仮置きされている間は同じように失敗します。どちらの場合も、ユーザーは登録し直して新しいリンクを要求します。

ユーザー ID は決して変わらないため、ゲストとしてのプロジェクトへのアクセス、SCIM の紐付け、グループのメンバーシップなど、すべてがアップグレード後も維持されます。

## 3. 下流でアップグレードを行う {#3-do-the-upgrade-downstream}

引き継ぎの確認は `ReprovisionAsync` を呼び出します。これは通常のプロビジョニングとは異なり、**ユーザーがすでにプロビジョニングされているアプリに対しても**、[TCC の Try/Confirm/Cancel](provisioning) のサイクルを再実行します。それこそが要点です。アプリはこのユーザーをすでに*ゲスト*としてプロビジョニングしているため、通常のプロビジョニングではそのユーザーは省略されます。再プロビジョニングでは、今度は登録のコンテキストを伴った 2 回目の Try が届くため、そのユーザーを格上げできます。

プロビジョニングの `Try` ハンドラーは、その `userId` のレコードをすでに持っているかどうかで「初回のプロビジョニング」と「アップグレード」を区別し、引き継ぎが運んできたコンテキスト (ここでは `org_name`) に応じて処理します。

```javascript
// POST {CallbackUrl}/try
app.post('/provisioning/try', async (req, res) => {
  const { transactionId, userId, email, customAttributes } = req.body;
  const existing = await db.members.findByAuthId(userId);

  if (!existing) {
    // First time we've seen this user: a plain new signup.
    stagePending(transactionId, { userId, email, role: 'member' });
    return res.json({ approved: true });
  }

  if (existing.kind === 'guest') {
    // UPGRADE: the guest is claiming a real account. Create their org from the signup context,
    // and stage the promotion (applied in /confirm). Reject to abort the whole claim if it can't proceed.
    const orgName = customAttributes?.org_name;
    if (!orgName) return res.json({ approved: false, reason: 'Organization name is required' });

    stagePending(transactionId, { userId, upgradeTo: 'standard', orgName });
    // Return org_id so Authagonal stamps it on the user's tokens (org_id claim).
    const orgId = deterministicOrgId(userId);
    return res.json({ approved: true, organizationId: orgId });
  }

  // Already a full member: nothing to do, but approve so the claim completes.
  res.json({ approved: true });
});

// POST {CallbackUrl}/confirm: all apps approved; commit the promotion.
app.post('/provisioning/confirm', async (req, res) => {
  const p = takePending(req.body.transactionId);
  if (p?.upgradeTo === 'standard') {
    await db.orgs.create({ id: deterministicOrgId(p.userId), name: p.orgName, ownerAuthId: p.userId });
    await db.members.promote(p.userId, { kind: 'standard' });
  }
  res.sendStatus(200);
});

// POST {CallbackUrl}/cancel: the claim failed elsewhere; drop the staged promotion.
app.post('/provisioning/cancel', (req, res) => { takePending(req.body.transactionId); res.sendStatus(200); });
```

いずれかのアプリからの `approved: false` (またはコールバックの失敗) があると、確認は `400 provisioning_rejected` で失敗し (API の呼び出し元には JSON ボディ、確認ページからは `/login?error=provisioning_rejected&error_description=...` へのリダイレクト)、アカウントはアップグレードされず、パスワードのない、引き継ぎ可能な状態のままになります。引き継ぎではない登録をプロビジョニングのアプリが拒否した場合は `422` になります。[TCC プロビジョニング](provisioning)を参照してください。承認したレスポンスに含まれる `organizationId` (または追加の `customAttributes`) は、ユーザーにマージされ、そのトークンに載ります。

## 4. アップグレードのコンテキストを安全に渡す {#4-pass-upgrade-context-safely}

登録の呼び出しに含まれる `customAttributes` は、引き継ぎが登録のコンテキスト (組織名、プラン、紹介元) をプロビジョナーに運ぶための手段です。これらは**仮置き**され、確認リンクのクリック時にのみ適用され、`ClaimAllowedAttributeKeys` で絞り込まれます。この許可リストは厳しく保ってください。フェデレーションユーザーのメールアドレスを*知っている*だけの人物が、本来の所有者のトークンに載る属性を注入するのを防ぐ境界がこれだからです。空の許可リストは予約されていないすべてのキーを許可し (信頼できるファーストパーティのフローには便利です)、値を設定した許可リストは列挙されていないものをすべて破棄します。

許可リストの内容にかかわらず、絞り込みでは常に次の制限が適用され、それに反するものは警告なしに破棄されます (登録自体は成功します)。

- 属性は最大 32 個で、キーは最大 64 文字、値は最大 1024 文字です。
- 次の予約されたキーは、`ClaimAllowedAttributeKeys` に列挙されていても決して受け付けられません: `federated_connection`、`org_id`、`roles`、`groups`、`sub`、`iss`、`aud`、`scope`、`client_id`、`sid`、`acr`、`amr`、`email`、`email_verified`。

同じ絞り込みは、通常の (引き継ぎではない) セルフサービス登録でも実行されます。

## セキュリティ上の特性 {#security-properties}

- **メールアドレスを知っているだけでは不十分です。** 引き継ぎが完了するのは、アカウント自身の受信トレイが確認リンクを受け取り、それを確認した場合だけです。アドレスを知っているだけの攻撃者がそのメールを受け取ることはありません。
- **仮置きされる資格情報は一度に 1 つだけです。** リンクはそのために仮置きされた資格情報に束縛されているため、後の引き継ぎを以前のリンクで昇格させることはできません (`claim_superseded`)。
- **本物の資格情報が上書きされることは決してありません。** 引き継げるのは `PasswordHash` のないアカウントだけです。資格情報を持つアカウントに対する引き継ぎには、通常の列挙に中立な重複のレスポンスが返されます。
- **確認されるまで何も有効になりません。** 確認されるまでは、仮置きされたパスワードで認証することはできず、仮置きされたプロフィール/属性も適用されません。アップグレードが拒否されると、すべてが元に戻ります。
- **属性の注入は `ClaimAllowedAttributeKeys` によって制限されます。**

## 関連項目 {#related}

- [TCC プロビジョニング](provisioning): ハンドラーが実装する Try/Confirm/Cancel の契約です。
- [セルフサービス SSO](self-service-sso): そもそもパスワードのないアカウントを作成する JIT のフローです。
