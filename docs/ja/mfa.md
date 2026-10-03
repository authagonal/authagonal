---
layout: default
title: 多要素認証
locale: ja
---

# 多要素認証 (MFA)

Authagonal は多要素認証をサポートしています。利用できる方式は 3 つあります。TOTP (認証アプリ)、WebAuthn/パスキー (ハードウェアキーと生体認証)、そして使い捨てのリカバリーコードです。パスキーは[パスワードレスログイン](#passwordless-passkey-login)にも使えます。

フェデレーションログイン (SAML/OIDC) も対象です。SAML や OIDC のアサーションが証明するのは第1要素であって、第2要素ではありません。MFA を登録済みのフェデレーションユーザーは、パスワードログインと同じローカルの MFA チャレンジを経由し、`Required` ポリシーの場合はセッションが発行される前に登録が強制されます。フェデレーションだけで完結するのは、MFA が登録されておらず、かつ必須でもない場合に限られます。接続ごとに `ChallengeMfaAfterLogin: false` でローカルのチャレンジをオプトアウトできます (後述)。

## サポートされる方式 {#supported-methods}

| 方式 | 説明 |
|---|---|
| **TOTP** | 時間ベースのワンタイムパスワード (RFC 6238) です。6 桁、30 秒ステップ、SHA-1 で、前後 1 ステップの時刻ずれを許容して検証されます。任意の認証アプリ (Google Authenticator、Authy、1Password など) で動作します。一度受け付けられたコードを、その有効期間内に再利用することはできません。 |
| **WebAuthn / パスキー** | FIDO2 ハードウェアセキュリティキー、プラットフォームの生体認証 (Touch ID、Windows Hello)、同期パスキーです。ユーザーは複数のパスキーを登録でき、パスキーでパスワードなしにサインインできます。 |
| **リカバリーコード** | 他の方式が使えないときのアカウント復旧用の、10 個の使い捨てバックアップコードです (32 文字のアルファベットから成る 10 文字で、`XXXXX-XXXXX` の形式で表示されます)。保存時はハッシュ化され、さらに暗号化されます。 |

## MFA ポリシー {#mfa-policy}

MFA の強制は、`appsettings.json` の `MfaPolicy` プロパティで**クライアントごと**に設定します。

| 値 | 動作 |
|---|---|
| `Disabled` (既定) | 登録を強制しません。すべてのクライアントが `Disabled` の場合、セルフサービスのセットアップ UI では MFA が表示されません |
| `Enabled` | MFA の登録を勧めますが、強制はしません |
| `Required` | MFA を持たないユーザーに登録を強制します |

MFA を登録済みのユーザーは、**クライアントのポリシーにかかわらず、ログイン時に必ずチャレンジを受けます**。MFA は要求元のクライアントではなくユーザーとそのセッションの属性なので、`Disabled` のクライアントを経由したリクエストを使って、登録済みユーザーの第2要素を省略することはできません。

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "MfaPolicy": "Enabled"
    },
    {
      "ClientId": "admin-portal",
      "MfaPolicy": "Required"
    }
  ]
}
```

既定値は `Disabled` なので、オプトインするまで既存のクライアントは影響を受けません。

### ユーザーごとの上書き {#per-user-override}

特定のユーザーについてクライアントのポリシーを上書きするには、`IAuthHook.ResolveMfaPolicyAsync` を実装します。

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    // Exempt service accounts
    if (email.EndsWith("@service.internal"))
        return Task.FromResult(MfaPolicy.Disabled);

    return Task.FromResult(clientPolicy);
}
```

解決されたポリシーが左右するのは登録 (勧めるか強制するか) です。すでに登録済みのユーザーをチャレンジから除外することはありません。登録済みのユーザーは常にチャレンジを受けます。

フックの詳しいドキュメントは[拡張性](extensibility)を参照してください。

## ログインフロー {#login-flow}

MFA を伴うログインフローは次のように動作します。

1. ユーザーがメールアドレスとパスワードを `POST /api/auth/login` に送信します
2. サーバーはパスワードを検証し、続いて実効的な MFA ポリシーを解決します
3. ポリシーとユーザーの登録状況に応じて次のようになります。

| ポリシー | ユーザーは MFA を持っているか | 結果 |
|---|---|---|
| 任意 | はい | `mfaRequired` を返します。ユーザーは検証を行う必要があります |
| `Disabled` / `Enabled` | いいえ | Cookie が設定され、ログインが完了します |
| `Required` | いいえ | `mfaSetupRequired` を返します。ユーザーは登録を行う必要があります |

### MFA チャレンジ {#mfa-challenge}

`mfaRequired` が返された場合、ログインレスポンスには `challengeId`、ユーザーが利用できる `methods`、そして (ユーザーがパスキーを持つ場合は) `webAuthn` のアサーションオプションが含まれます。クライアントは MFA チャレンジページにリダイレクトし、ユーザーはそこで登録済みの方式のいずれかを使い、`POST /api/auth/mfa/verify` で検証を行います。

```json
{
  "challengeId": "...",
  "method": "totp",
  "code": "123456"
}
```

`method` は `totp`、`recovery`、`webauthn` のいずれかです (WebAuthn では `code` の代わりに `assertion` を送ります)。

チャレンジは 5 分で失効し (`Auth:MfaChallengeExpiryMinutes` で設定可能)、検証に成功すると消費されます。

#### 再試行の上限 {#retry-budget}

コードを間違えても、チャレンジは消費されません。検証エンドポイントはまずコードを確認し、成功した場合にのみチャレンジを消費するため、TOTP の数字を打ち間違えても、同じ `challengeId` に対して再試行するだけで済みます。失敗した試行は 401 とともに `invalid_code` (WebAuthn の場合は `assertion_failed`) を返し、チャレンジ上の上限付きカウンターを増やします。5 回目の誤りでチャレンジは消費されて `too_many_attempts` が返され、改めてログインし直す必要があります。これは 3 つの方式すべてに適用されます。

チャレンジごとの試行回数の上限は早めに打ち切るための仕組みであって、セキュリティ上の歯止めではありません。そのため `POST /api/auth/mfa/verify` には、さらに 2 つのゲートが適用されます。

- **ユーザーごとのレート制限。** 1 人のユーザーについて 1 分間に 10 回を超える検証の試行は、どの `challengeId` を使っていても、429 とともに `too_many_attempts` を返します。
- **共有のアカウントロックアウト。** 失敗したコードはすべて、パスワードの段階と同じ失敗回数カウンター (`Auth:MaxFailedAttempts`、`Auth:LockoutDurationMinutes`) にも計上されます。これが上限に達すると、チャレンジは消費され、レスポンスは `locked_out` (423) になります。アカウントがロックされている間、検証はコードを確認する前に `locked_out` で拒否されます。

検証を満たせるのは確認済みの資格情報だけです。開始したものの完了しなかった登録は、要素として数えられません。

チャレンジが存在しない、失効している、またはすでに消費されている場合は、`invalid_challenge` が返されます。

### フェデレーションログイン {#federated-logins}

SAML または OIDC のアサーションが成功した後、サーバーは同じ実効的な MFA ポリシーを解決します。MFA を登録済みのユーザーは、セッションを受け取る代わりに、ホストされた MFA チャレンジページ (`challengeId` 付き) にリダイレクトされます。`Required` ポリシーの下で MFA を持たないユーザーは、MFA のセットアップページ (`setupToken` 付き) にリダイレクトされます。セッションが MFA 認証済みとしてマークされるのは、検証が完了してからです。

このチャレンジは接続ごとに制御できます。`ChallengeMfaAfterLogin` が `false` に設定された SAML または OIDC 接続では、その接続を経由してきたユーザーに対してローカルのチャレンジが省略されます。既定値は `true` です。

### 登録の強制 {#forced-enrollment}

`mfaSetupRequired` が返された場合、レスポンスには `setupToken` が含まれます。このトークンは MFA のセットアップエンドポイントに対してユーザーを認証するため (`X-MFA-Setup-Token` ヘッダーを使用)、ユーザーは Cookie セッションを得る前に方式を登録できます。セットアップトークンは 15 分で失効します (`Auth:MfaSetupTokenExpiryMinutes` で設定可能)。

## MFA の登録 {#enrolling-mfa}

ユーザーは、セルフサービスのセットアップエンドポイントを通じて MFA を登録します。これらのエンドポイントには、認証済みの Cookie セッションかセットアップトークンのどちらかが必要です。

### TOTP のセットアップ {#totp-setup}

1. `POST /api/auth/mfa/totp/setup` を呼び出すと、QR コード (`data:image/png;base64,...`)、`manualKey` (手入力用の Base32)、セットアップトークンが返されます
2. ユーザーが認証アプリで QR コードを読み取ります
3. ユーザーが 6 桁のコードを入力して確定します: `POST /api/auth/mfa/totp/confirm`

確定の段階にも検証と同様の制限があります。1 人のユーザーについて 1 分間に 10 回を超える試行は `too_many_attempts` (429) を返し、セットアップトークンを使っている場合は 5 回目の誤ったコードでセットアップのチャレンジが消費されます。確定されていない登録は 30 分で失効します (`setup_expired`)。

### WebAuthn / パスキーのセットアップ {#webauthn--passkey-setup}

1. `POST /api/auth/mfa/webauthn/setup` を呼び出すと、`setupToken` と `PublicKeyCredentialCreationOptions` が返されます
2. クライアントがそのオプションで `navigator.credentials.create()` を呼び出します
3. アテステーションのレスポンスを `POST /api/auth/mfa/webauthn/confirm` に送信します

パスキーを登録するには、先に確認済みの TOTP 資格情報が必要です (`totp_required_first`)。パスキーは、持ち運び可能な基本の要素の上に重ねる、デバイスごとの利便性のための手段です。そのため、どのアカウントもデバイスに依存しない要素を保持し、`Required` ポリシーをパスキーだけで満たすことはできません。

ユーザーは複数のパスキー (デバイスごとに 1 つ) を登録できます。すでに登録されている資格情報 ID (登録しようとしているユーザー自身のものを含む、任意のアカウントのもの) は、`credential_already_registered` (409) で拒否されます。登録済みの認証器を再登録すると、1 つの資格情報 ID を共有する 2 つ目の資格情報の行が作られてしまいます。その署名カウンターは最初からやり直しになってクローン検出が弱まり、どちらかの行を削除すると両方が依存する検索用エントリが消えてしまいます。検索用エントリは「存在しない場合のみ挿入」の書き込みで確保されるため、同じ資格情報 ID の 2 つの登録が両方とも成功することはありません。強制 SSO によってメールドメインが外部 IdP にルーティングされているユーザーは、ローカルのパスキーを登録できません (`sso_managed`)。登録できてしまうと、IdP とそのプロビジョニング解除を迂回できてしまうためです。

### リライングパーティーのホスト {#relying-party-host}

FIDO2 のリライングパーティー ID とオリジンは、リクエストごとにホストから解決されるため、テナントのホスト名ごとにそれぞれ独立したリライングパーティーになります。`Auth:WebAuthnAllowedHosts` に配信するホスト名を設定すると、そのリストにないホストはリライングパーティーとして振る舞えなくなります。空のリスト (既定) は、アップグレード時に既存のパスキーユーザーを締め出さないよう従来の動作を維持し、初回使用時に不備としてログに記録されます。安心してそのままにしておける状態ではありません。さらに `appsettings.json` で `AllowedHosts` も設定し、どのハンドラーが実行されるよりも前に ASP.NET Core のホストフィルタリングで認識できない `Host` ヘッダーを拒否させれば、より低コストな外側の防御層になります。

このリストとは独立して、すべての資格情報は登録時のリライングパーティーを記録し、それ以外の場所では拒否されます。ここはリクエストからは影響を与えられない部分です。そうでなければ、どちらのセレモニーも、検証の対象であるのと同じ `Host` ヘッダーから期待値を組み立ててしまいます。つまりオリジンと `rpIdHash` は呼び出し元が提供した値と比較されることになり、通信経路上にいて自分の `Host` を転送するホストに対しては、パスキーをフィッシング耐性のあるものにしている性質であるオリジンの束縛が、そのホストを阻止するどころか正当なものとして検証してしまいます。RP ID が記録されるようになる前に登録された資格情報は RP ID を持たないまま機能し続け、再登録したときに束縛が付きます。

### リカバリーコード {#recovery-codes}

`POST /api/auth/mfa/recovery/generate` を呼び出すと、10 個の使い捨てコードが生成されます。先に確認済みの主要な方式 (TOTP または WebAuthn) が少なくとも 1 つ登録されている必要があり (`primary_method_required`)、呼び出しには実際の認証済みセッションが必要です。セットアップトークンでは `session_required` (403) になります。

各コードは 32 文字のアルファベットから成る 10 文字で、5 文字ずつの 2 グループ (`XXXXX-XXXXX`) で表示されます。

コードを再生成すると、既存のリカバリーコードはすべて置き換えられます。各コードは 1 回しか使えず、使われたコードは消費済みとしてマークされ、以後は受け付けられません。

コードが平文で保存されることはありません。各コードはハッシュ化され、そのハッシュはさらにテナントのシークレットプロバイダーで暗号化して保存されます。そのため、ストレージのダンプから得られるのは、オフラインで総当たりできるハッシュではなく暗号文です。

## パスワードレスのパスキーログイン {#passwordless-passkey-login}

パスキーは第2要素であるだけではありません。パスキーを登録済みのユーザーは、パスワードなしでサインインできます。

1. `POST /api/auth/mfa/passwordless/begin` は、検出可能な資格情報向けの `challengeId` とアサーションの `options` を返すため、認証器はそのサイト用に保存されている任意のパスキーを提示します
2. クライアントがそのオプションで `navigator.credentials.get()` を呼び出します
3. `{ challengeId, assertion }` を付けて `POST /api/auth/mfa/passwordless/complete` を呼び出します。サーバーはパスキー自体からユーザーを特定し、サインインさせます

ホストされたログインページは、条件付きメディエーション (パスキーの自動入力) によってこれをメールアドレス欄に組み込んでいます。ブラウザが対応していれば、利用可能なパスキーが追加の UI なしに自動入力の候補として提示されます。

パスキーはフィッシング耐性のある強力な認証なので、結果として得られるセッションには MFA のマーカーが付き、再度チャレンジされることはありません。強制 SSO によってユーザーのメールドメインが外部 IdP にルーティングされている場合、パスワードレスログインは SSO のリダイレクト URL を含む 409 `sso_required` レスポンスで拒否されるため、ローカルのパスキーで IdP を迂回することはできません。

## MFA の管理 {#managing-mfa}

### ユーザーのセルフサービス {#user-self-service}

- `GET /api/auth/mfa/status`: 登録済みの方式を表示します (いずれかのクライアントが MFA を提供しているかどうかも報告します)
- `DELETE /api/auth/mfa/credentials/{id}`: 特定の資格情報を削除します

資格情報の削除には、実際の認証済みセッションが必要です。セットアップトークンが認可するのは最初の要素の追加だけで、ここでは `session_required` になります。そのため、漏洩したセットアップトークンでユーザーの MFA を弱めることはできません。

最後の主要な方式が削除されると、そのユーザーの MFA は無効になります。

### 管理 API {#admin-api}

管理者は [管理 API](admin-api) を使って任意のユーザーの MFA を管理できます。

- `GET /api/v1/profile/{userId}/mfa`: ユーザーの MFA の状態を表示します
- `DELETE /api/v1/profile/{userId}/mfa`: すべての MFA をリセットします (ロックアウトされたユーザー向け)
- `DELETE /api/v1/profile/{userId}/mfa/{id}`: 特定の資格情報を削除します

### 監査フック {#audit-hooks}

MFA のイベントをログに記録するには、`IAuthHook.OnMfaVerifiedAsync` を実装します。

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

MFA のライフサイクル全体にフックできます。`OnMfaVerifyFailedAsync` (検証の試行の失敗)、`OnMfaEnrolledAsync` (方式の確定)、`OnMfaCredentialRemovedAsync` (資格情報の削除。それによって MFA が無効になったかどうかを示すフラグ付き)、`OnRecoveryCodesRegeneratedAsync` です。

## カスタムログイン UI {#custom-login-ui}

独自のログイン UI を構築する場合は、`POST /api/auth/login` からの次のレスポンスを処理してください。

1. **通常のログイン**: Cookie が設定された `{ userId, email, name }`。`returnUrl` にリダイレクトします。
2. **MFA が必要**: `{ mfaRequired: true, challengeId, methods, webAuthn? }`。MFA チャレンジのフォームを表示します。
3. **MFA のセットアップが必要**: `{ mfaSetupRequired: true, setupToken }`。MFA の登録フローを表示します。

`POST /api/auth/mfa/verify` のエラーを処理する際、`invalid_code` と `assertion_failed` は同じ `challengeId` に対して (試行回数の上限まで) 再試行できます。`too_many_attempts` と `invalid_challenge` は終端的なエラーなので、ユーザーをサインインフォームに戻してください。

エンドポイントの完全なリファレンスは [認証 API](auth-api) を参照してください。
