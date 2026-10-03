---
layout: default
title: フロントチャネルログアウト
locale: ja
---

# フロントチャネルログアウト

Authagonal は **OpenID Connect Front-Channel Logout 1.0** を実装しています。これはブラウザ主導のログアウトの仕組みで、[バックチャネルログアウト](index#key-features)を補完するものです。バックチャネルログアウトがサーバー間の POST であるのに対し、フロントチャネルログアウトは各リライングパーティーのログアウト URL を非表示の iframe に読み込み、各アプリのブラウザセッション (Cookie、ローカルストレージ) をユーザーのブラウザ内部から消去します。

## 使い分け {#when-to-use-which}

| 観点 | バックチャネル | フロントチャネル |
|---|---|---|
| サーバー側のセッション | ✅ | ❌ |
| ブラウザの Cookie / ローカルストレージ | ❌ | ✅ |
| ユーザーのブラウザがオフラインでも機能する | ✅ | ❌ |
| ネットワークエラーに耐える (再試行) | ✅ | ❌ (ベストエフォートで 1 回のみ) |

ほとんどのアプリでは、**両方**を設定することが有益です。バックチャネルはサーバーへの通知を保証し、フロントチャネルはブラウザを消去します。

## クライアントの設定 {#client-configuration}

`OAuthClient` レコードにフロントチャネルログアウト URI を追加します。

```json
{
  "clientId": "myapp",
  "frontChannelLogoutUri": "https://myapp.example.com/oidc/frontchannel",
  "frontChannelLogoutSessionRequired": true
}
```

| フィールド | 説明 |
|---|---|
| `FrontChannelLogoutUri` | ブラウザから見える、クライアントのログアウトエンドポイント |
| `FrontChannelLogoutSessionRequired` | `true` (既定) の場合、URL は `iss` と `sid` のクエリパラメーター付きで呼び出されるため、クライアントはログアウトを特定のセッションと関連付けられます |

## 仕組み {#how-it-works}

ブラウザが `/connect/endsession` (GET または POST) にアクセスすると、次のように処理されます。

1. **確認 (CSRF ガード)。** ブラウザにサインイン済みのセッションがあり、リクエストにそのセッションと `sub` が一致する `id_token_hint` が含まれていない場合、サーバーはユーザーをログアウトさせる代わりに、まず確認ボタン付きの「サインアウトしますか?」ページを表示します。ボタンは、そのセッションに束縛された短命 (15 分) のトークンを付けて POST で送り返します。これにより、サードパーティのページがエンドポイントへナビゲートするだけでユーザーのセッションを終了させることを防ぎます (セッション Cookie は `SameSite=Lax` なので、クロスサイトのトップレベル GET にも付随します)。一致する `id_token_hint` があれば、確認の代わりになります。
2. サーバーは、ユーザーが現在グラントを持っているすべてのクライアントを見つけます。
3. 送信先 URL のチェックを通過する `FrontChannelLogoutUri` を持つ各クライアントについて (リクエストを行うのはユーザー自身のブラウザなのでループバックは許可されますが、プライベート範囲やリンクローカルのアドレスは許可されません)、サーバーは URL を組み立てます。`FrontChannelLogoutSessionRequired` が `true` の場合は、`iss=<issuer>` (およびセッションに ID がある場合は `sid=<session_id>`) を付加します。
4. サーバーは、そのセッションで発行されたグラントを取り消し、認可サーバーの Cookie からユーザーをサインアウトさせ、バックグラウンドでバックチャネルログアウトの通知を開始します。フロントチャネルの URL が 1 つ以上組み立てられた場合は、それぞれについて非表示の `<iframe>` を含む HTML ページを返します。
   ```html
   <iframe src="https://myapp.example.com/oidc/frontchannel?iss=https%3A%2F%2Fauth.example.com&sid=abc123" style="display:none"></iframe>
   ```
   このページには、`frame-src` をそれらの URL のオリジンに限定した `Content-Security-Policy` が付いており、スクリプトは含まれません。
5. ログアウト後の遷移先は、iframe が関わるかどうかにかかわらず同じ方法で決定されます。`post_logout_redirect_uri` が尊重されるのは、リクエストがクライアントを特定でき (`id_token_hint` のオーディエンス、または `client_id` パラメーターによって)、かつその URI がそのクライアントの登録済み `PostLogoutRedirectUris` に含まれている場合だけです (`state` パラメーターが指定されていれば付加されます)。iframe がある場合、ページは 2 秒待ってから (`meta refresh`) リダイレクトし、有効な遷移先がなければ「サインアウトしました」というメッセージを表示します。フロントチャネルの URL がない場合、サーバーはすぐにリダイレクト (`302`) し、有効な遷移先がなければ JSON の `message` 付きで `200` を返します。

`id_token_hint` が受け付けられるのは、このサーバーが署名した (ES256、`typ: JWT`) 単一のオーディエンスを持つ ID トークンである場合だけです。有効期限切れのトークンは受け付けられます。アクセストークンやログアウトトークンは、ヒントとしては拒否されます。`client_id` と `id_token_hint` の両方が送信され、それぞれが異なるクライアントを指している場合、リクエストは `400 invalid_request` で失敗します。

JSON エンドポイント `POST /api/auth/logout` (ログインアプリのサインアウトボタンが使用) も、同じ取り消しと通知の手順を実行します。こちらは iframe を描画せず、呼び出し元が読み込むための URL を `frontchannel_logout_uris` で返します ([認証 API](auth-api#logout) を参照)。

## クライアント側のログアウトハンドラー {#client-side-logout-handler}

各リライングパーティーは、`FrontChannelLogoutUri` が指す URL を実装する必要があります。最小限のハンドラーは次のとおりです。

```http
GET /oidc/frontchannel?iss=https://auth.example.com&sid=abc123
```

1. `iss` が想定する認可サーバーと一致することを検証します。
2. `sid` が指定されている場合は、セッション Cookie のセッション ID と一致することを確認します。
3. ローカルセッション (Cookie、サーバー側のセッション、SPA のストレージ) を消去します。
4. `200 OK` と空のボディ (または小さなページ) で応答します。このレスポンスがユーザーの目に触れることはありません。

```csharp
app.MapGet("/oidc/frontchannel", (HttpContext ctx) =>
{
    var iss = ctx.Request.Query["iss"].ToString();
    var sid = ctx.Request.Query["sid"].ToString();
    // Validate iss/sid, then clear local session
    ctx.SignOutAsync();
    return Results.Ok();
});
```

## ディスカバリドキュメント {#discovery-document}

フロントチャネルログアウトは `/.well-known/openid-configuration` で公開されます。

```json
{
  "frontchannel_logout_supported": true,
  "frontchannel_logout_session_supported": true
}
```

## 動的クライアント登録 {#dynamic-client-registration}

[動的クライアント登録](client-registration)で登録されるクライアントは、次を含めることができます。

```json
{
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

登録時、外部アドレスでないログアウト URI は拒否されます (ループバック、リンクローカル、プライベート範囲、および `.localhost`/`.local`/`.internal` の名前は `invalid_client_metadata` で拒否されます)。

## 制限事項 {#limitations}

- **ベストエフォート**: iframe は 1 回だけ読み込まれます。ネットワークエラーやブラウザ拡張機能によってブロックされても、再試行はありません。信頼性のためにバックチャネルログアウトと組み合わせてください。
- **サードパーティ Cookie**: 一部のブラウザは、クロスサイトの iframe 内の Cookie を既定でブロックします。RP がファーストパーティ Cookie に依存している場合は、ログアウトハンドラーが Cookie の送信を前提としていないことを確認してください。
- **タイムアウト**: ページはリダイレクトする前に約 2 秒待ちます。RP のログアウトハンドラーが重い処理を行う場合、時間内に完了しないことがあります。

## 関連項目 {#related}

- [動的クライアント登録](client-registration): 登録リクエストにおけるフロントチャネルのパラメーター
- [OAuth スコープ](scopes): スコープを考慮した同意は、ログアウトフローを補完します
