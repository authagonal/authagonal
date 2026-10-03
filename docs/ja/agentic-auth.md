---
layout: default
title: エージェント認可
locale: ja
---

# エージェント認可

Authagonal は、ユーザーの権限を AI エージェント (あるいは人間以外の任意のワークロード) に安全に委任するための構成要素を提供します。登録済みエージェント、きめ細かな権限付与、複合委任トークン、ユーザーの常設同意、ジャストインタイム承認、ケイパビリティチケット、そして委任を把握した監査の仕組みです。ライブラリはプリミティブと不変条件を受け持ち、ホストアプリケーションがそれらを組み立てて製品にします (コネクターの実装、承認の UX、通知の配信、ビジネスポリシーはホスト側に残ります)。

## 不変条件 {#the-invariant}

委任されたトークンはすべて次の式に従います。

```
effective authority = admin ceiling ∩ user consent ∩ task request ∩ subject-token authority
```

下流のどこからもこれを広げることはできず、委任のホップを重ねるたびに再び積集合が取られるため、権限は狭まる一方です。積集合の計算は 1 か所 (`AuthoritySet.Intersect`) に実装され、あらゆる場所で使われます。

## エンティティ {#entities}

| エンティティ | 型 | 備考 |
|---|---|---|
| エージェント | 機密 `OAuthClient` 上の `AgentProfile` | プロファイルを登録することで、そのクライアントがエージェントになります。プロファイルを削除すると、クライアントは通常の OAuth クライアントに戻ります。 |
| 権限 | `AuthoritySet` / `AuthorityGrant` | RFC 9396 の `authorization_details` の形式です。コネクターの `type`、`actions`、`locations`、制約、アクションごとの `auto`/`ask`/`deny` ポリシーから成ります。 |
| 上限 | `AgentProfile.Ceiling` | そのエージェントを経由する委任が持てる権限の最大範囲です。管理者が管理します (`/api/v1/agents`)。 |
| 同意 (下限) | `PersistedGrant` 型 `agent_consent` | (ユーザー, エージェント) の組ごとに `/consent/agents` で管理します。上限との積集合を取った状態で保存され、発行のたびに再度積集合が取られます。 |
| 委任 | RFC 8693 トークン交換 | 複合アイデンティティです。`sub` = ユーザー、`act` = エージェント (ホップごとに入れ子)、`authorization_details` = 実効的な積集合。有効期間は短く、リフレッシュできません。 |
| 承認 | `PersistedGrant` 型 `approval` | `ask` ポリシーのアクションに対するジャストインタイムのゲートです。デバイスフローと同じポーリングの意味論を持ち、1 回限りで、リクエストの形に束縛されます。 |
| ケイパビリティチケット | `ICapabilityTicketService` | トークンに束縛された不透明な 1 回限りのハンドルです。BFF の ws-ticket を汎用化したもので、グラントストア上でアトミックに動作します。 |
| 監査 | `IAuthHook` | `OnDelegationMintedAsync`、`OnApprovalRequested/ResolvedAsync`、`OnAgentConsentChangedAsync`、`OnCapabilityTicketRedeemedAsync`、および発行前のゲートである `OnTokenIssuingAsync` です。 |

## エージェントの登録 {#registering-an-agent}

1. `urn:ietf:params:oauth:grant-type:token-exchange` (委任モード) と `client_credentials` (サービスモード) の一方または両方を許可する機密クライアントを作成します。
2. `PUT /api/v1/agents/{clientId}`:

```json
{
  "mode": "delegated",
  "ceiling": [
    {
      "type": "email",
      "actions": ["send", "read"],
      "action_policies": { "send": "ask" },
      "recipient_domains": ["@acme.com", "*.partners.acme.com"]
    },
    { "type": "calendar", "actions": ["read"] }
  ],
  "maxDelegationDepth": 0,
  "maxTokenLifetimeSeconds": 300,
  "highRiskDefault": "ask"
}
```

`mode` は `delegated`、`service`、`both` のいずれかです (更新時に `mode` を省略すると既存の値が維持されます)。`maxDelegationDepth` は 0 から 8 (既定値 0)、`maxTokenLifetimeSeconds` は 30 から 86400 (既定値 300)、`highRiskDefault` は `auto`、`ask`、`deny` のいずれかでなければならず、それ以外はすべて 400 になります。

制約のメンバーは JSON の形によって型が決まります。文字列/文字列配列 → 許可リスト (集合の積で合成。エントリは完全一致、`*.host` ワイルドカード、`@suffix` の一致に対応)、数値 → 上限値 (最小値で合成)、真偽値 → ゲート (AND で合成)。解釈できないメンバーはそのまま保持され、評価時にはフェイルクローズします。`GET /api/v1/agents/{clientId}/effective-grant?subjectId=…` は、管理 UI 向けに上限 ∩ 同意をプレビューします。

## ユーザーの同意 (下限) {#user-consent-the-floor}

- `GET /consent/agents/{clientId}/info`: コネクターカタログに照らして上限を表示用に展開したものです (表示名、アクションの説明、高リスクフラグのために `IConnectorCatalog` を登録します。その型はディスカバリで `authorization_details_types_supported` として公開されます)。
- `POST /consent/agents` `{ "clientId": …, "authority": […] }`: 下限を付与します (`authority` を省略すると上限全体に同意します)。ユーザーはポリシーを厳しくすること (`auto` → `ask`) はできますが、緩めたり広げたりすることはできません。ストアが現在の上限との積集合を事前に取るためです。
- `GET /consent/agents` / `DELETE /consent/agents/{clientId}`: 一覧表示と取り消しです。取り消すと次回以降の発行が止まります。発行済みの委任はリフレッシュできないため、それぞれの (短い) 有効期間内に失効します。同意がなければ交換は `invalid_grant` / `consent_required` で失敗し、上限だけでは何も付与されません。

## 委任の発行 {#minting-a-delegation}

エージェントは自分自身として認証し、ユーザーのトークンを交換します。

```
POST /connect/token
grant_type=urn:ietf:params:oauth:grant-type:token-exchange
client_id=agent&client_secret=…            (or private_key_jwt, below)
subject_token={user access token}
subject_token_type=urn:ietf:params:oauth:token-type:access_token
authorization_details=[{"type":"email","actions":["read"]}]   (the task slice; omit = everything grantable)
```

発行時には次の順に検査が行われます。エージェントのモード、常設同意、サブ委任の深さ (`act` チェーンにすでに含まれる各アクターが、もう 1 ホップ分の `maxDelegationDepth` の余地を持っている必要があります)、積集合、明示的なリクエストに対する拒否 (`invalid_target`。エージェントが持っていない権限を持っていると誤認してはならないため)、ask ゲート、そして有効期間の制限 (クライアントの有効期間 ∩ サブジェクトトークンの残り期間 ∩ `maxTokenLifetimeSeconds`) です。トークンには `act` (RFC 8693、ホップごとに入れ子) と `authorization_details` (RFC 9396) が含まれ、レスポンスには付与された details がそのまま返され、イントロスペクションは両方を出力します。委任済みのトークンをさらに交換すると、サブジェクトトークン自身のクレームが積集合に加わるため、権限は自動的に絞り込まれます。

エージェントプロファイルを**持たない**クライアントは、従来の交換動作をそのまま維持します。唯一の違いは、`authorization_details` リクエストパラメーターが交換後のトークンを絞り込むようになった (広げることは決してない) 点です。

## 承認 (ask ゲート) {#approvals-ask-gate}

実効的なスライスに `ask` アクションが含まれる場合、交換は保留されます。

```json
{ "error": "authorization_pending", "approval_id": "…", "interval": 5 }
```

ホストには `IAuthHook.OnApprovalRequestedAsync` を通じて通知されます (メール、プッシュ、チャットでの配信はホスト側の担当です)。ユーザーは `GET /approvals`、`POST /approvals/{id}` `{ "decision": "approve" | "deny" }` で承認または拒否を決め、その間エージェントは同一のリクエストに `approval_id` を加えて再試行します。用語は終始デバイスフローのもの (`slow_down`、`access_denied`、`expired_token`) を使います。承認は 1 回限り (アトミックに消費) で、`ApprovalLifetimeSeconds` (既定値 300) を過ぎると失効し、リクエストの正確な形*および現在のポリシーの状態*に束縛されます。保留からポーリングまでの間に管理者が上限を編集した場合、古い権限で発行するのではなく、その承認が無効になります。消費された承認による発行では、その `ask` アクションは `auto` に解決されます (確認済みで回答済みのため)。

サービスモード (`client_credentials`) にはループ内にユーザーがいません。上限だけが適用され、`ask` は `deny` に格下げされます。

## リソース側での強制 {#resource-side-enforcement}

- 任意のリソースサーバーで `AuthorityEvaluator.Permits(user, type, action, context, location, strict)` を使います (context のキーは制約名と照合されるので、導出できるものを渡してください。例: メール送信時の `recipient_domains`)。クレームを持たないトークンは無制限として評価されます (旧来の互換性のため)。壊れたクレームはすべて拒否として評価されます。
  - `location` は、操作の対象となる RFC 9396 の `locations` の値です。location を指定した付与は、それらの location でのみ有効です。付与された location は**ルート**として扱われるため、`https://api.example.com/orders` は `/orders/17` を含みますが、`/orders-admin` は含みません。
  - `strict: true` を指定すると、呼び出し元がある制約に対する context を渡さなかった場合、その制約を飛ばすのではなく拒否します。サポートするすべてのキーを列挙できる場面では必ずこれを使ってください。`AuthoritySet.UncheckedConstraints(type, context)` が、渡さなかったキーを示します。
- BFF のチョークポイント: `BffUpstream.RequiredAuthority = ["email:send"]` を指定すると、プロキシは転送前に送出するベアラートークンを検査し、失敗時は 403 を返します。匿名での通過はありません。プロキシが提示する location は、リクエストが実際に到達する上流です (権限が内部アドレスではなく公開識別子に対して発行されている場合は、`AuthorityLocation` がルートを上書きします)。`StrictAuthority` を指定すると、プロキシは評価できない制約を上流に任せるのではなく拒否します。

## ケイパビリティチケット {#capability-tickets}

`ICapabilityTicketService` (既定は `GrantStoreCapabilityTicketService`。`AddAuthagonalCore` が `TryAdd` で登録するため、`AddAuthagonal` でも利用できます) は、トークンに束縛された不透明な 1 回限りのハンドルを発行します。ハンドルはグラントストアの条件付き削除によってアトミックに引き換えられるため、通常のキャッシュの「取得してから削除」とは異なり、永続的で、複数のポッドにまたがってもリプレイに対して安全です。BFF の ws-ticket は既存の分散キャッシュの契約 (`WsTicketKey` / `TryRedeemWsTicketAsync`) を維持します。その引き換え側は通常、Redis だけを共有する別のホストだからです。同じホストに同居するブローカーでは、ケイパビリティチケットサービスを優先して使ってください。

## private_key_jwt {#private_key_jwt}

エージェントはワークロードであり、共有シークレットはチェーンの中で最も弱い部分です。`OAuthClient.JwksJson` (インラインの JWKS) または `JwksUri` (取得して約 10 分キャッシュ) を設定し、RFC 7523 のクライアントアサーション (`client_assertion_type=…:jwt-bearer`) で認証してください。強制される内容は、登録済み JWKS に対する署名の検証、`iss` = `sub` = `client_id`、audience = 発行者またはトークンエンドポイント、上限付きの `exp` (10 分以下)、そして 1 回限りの `jti` (`IRevokedTokenStore` 上のリプレイキャッシュ) です。アサーションが提示されている場合、シークレットによる認証にフォールバックすることは決してありません。

## 互換性 {#compatibility}

- エージェントプロファイルがなければ、どのフローの動作も変わりません。新しいテーブルと列はすべて null 許容の既定値を持ち、両方のストレージプロバイダーで自動的にプロビジョニングされます (`AgentProfiles` テーブル、クライアントの `JwksJson`/`JwksUri`。同意、承認、チケットは既存のグラントテーブルに格納されます)。
- 新しい `IAuthHook` メンバーは既定のインターフェイスメソッドなので、既存のフックはそのままコンパイルできます。
- `ITokenExchangeSubjectTransformer` は引き続きすべての交換で実行され、拒否したり context クレームを束縛したりできます。ただし委任を広げること (出力は再度積集合が取られます) や、`act` チェーン (予約済みクレーム) に触れることはできません。
