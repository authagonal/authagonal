---
layout: default
title: 拡張性
locale: ja
---

# 拡張性

Authagonal は、サービスの実装を完全に制御できる形で、独自の ASP.NET Core プロジェクトにライブラリとしてホストできます。

## 拡張メソッド {#extension-methods}

3 つのメソッドで、Authagonal を任意の ASP.NET Core アプリに組み込みます。

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

### マルチテナントのホスティング {#multi-tenant-hosting}

マルチテナントのデプロイメントでは、代わりに `AddAuthagonalCore()` を使用します。これはエンドポイント、ミドルウェア、コアサービスを登録しますが、ストレージとバックグラウンドサービスは登録しません。それらはテナントごとに自分で提供します。署名鍵の管理には既定で `Authagonal.Protocol` の `ProtocolKeyManager` シングルトンが使われ、`AddAuthagonalCore()` の前に独自の `IKeyManager` を登録したホストでは、その登録が維持されます。

```csharp
builder.Services.AddScoped<ITenantContext, MyTenantContext>();
builder.Services.AddScoped<IKeyManager, MyPerTenantKeyManager>();
builder.Services.AddAuthagonalCore(builder.Configuration);
```

`IKeyManager` とストアのインターフェイス (`IClientStore`、`IScimTokenStore` など) はリクエスト時に `HttpContext.RequestServices` から解決されるため、スコープ付きの登録はテナントごとの分離のために正しく機能します。

### `Authagonal.Protocol` だけを組み込む {#embedding-authagonalprotocol-alone}

OIDC プロトコルの機能だけが必要なホスト (独自の認証、独自のパイプラインを持ち、`/connect/*` エンドポイントをそのまま追加したいホスト) は、`Authagonal.Server` を一切使わずに `AddAuthagonalProtocol()` と `MapAuthagonalProtocolEndpoints()` を呼び出します。

この形でも、`/connect/authorize`、`/connect/token`、`/connect/userinfo`、`/connect/par` は、RFC 6749 §3.1/§3.2 に従って平文の http を拒否します。このパッケージは自分が所有していないパイプラインにマップされるため、この要件はミドルウェアとしてではなく、エンドポイントのフィルターとして適用されます。そのため、パイプラインをどのように構成しても、またすべての機能をまとめてマップしても 1 つずつマップしても、この要件は維持されます。アップグレードの前に知っておくべき影響が 2 つあります。

- **TLS を終端するプロキシの背後では、プロキシを宣言したうえで `UseForwardedHeaders` を呼び出してください。** フィルターはルーティングの後にスキームを読み取るため、転送された `X-Forwarded-Proto: https` があれば要件を満たします。このミドルウェアがないと、ホストには平文に見えます。これは Cookie に `Secure` が付いておらず、生成される絶対 URL も間違っていることを意味するので、回避するのではなく修正する価値があります。登録するときは `KnownProxies` / `KnownNetworks` を設定してください。ASP.NET Core は空の信頼セットを「すべての呼び出し元が信頼されたプロキシである」と解釈するため、ホストに到達できる誰にでもスキームの決定を委ねてしまいます。拒否のレスポンス本文が適用されていない `X-Forwarded-Proto` に言及している場合、求められているのはこのミドルウェアです。
- **プロトコルの機能を本当に http で提供するホストはオプトインを設定します。** サーバーと同じ方法です。

```csharp
builder.Services.AddAuthagonalProtocol(o =>
{
    o.AuthenticationScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    o.AllowInsecureHttp = builder.Environment.IsDevelopment();   // never in production
});
```

ディスカバリーと JWKS には意図的に制限をかけていません。これらは公開メタデータであり、それを読み取れないクライアントは、そもそも https が必要だと知ることもできないからです。

`AddAuthagonal()` (完全なサーバー) を使う場合は、これを個別に設定する必要はありません。`Auth:AllowInsecureHttp` が自動的にプロトコルのオプションに反映されるため、1 つのスイッチで全体を制御できます。

## サービスのオーバーライド {#overriding-services}

独自の実装は、`AddAuthagonal()` を呼び出す**前に**登録してください。Authagonal は内部で `TryAdd` を使っているため、あなたの登録が優先されます。

```csharp
// Custom implementations, registered first so they won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

`IAuthHook` は特別で、複数の登録からなるパイプラインです。フックはいくつでも (任意の有効期間で、`AddScoped` も含めて) 登録でき、すべてが登録順に実行されます。何もしない `NullAuthHook` は、`AddAuthagonal()` / `AddAuthagonalCore()` が実行される時点でフックが 1 つも登録されていない場合にのみ追加されます。そのため、フックは常に先に登録してください。

### 拡張点 {#extensibility-points}

| インターフェイス | 既定 | 目的 |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (何もしない。フックが登録されていない場合にのみ追加) | 認証イベントのライフサイクルフック。監査ログ、独自の検証、Webhook など。複数のフックを登録でき、すべてが順に実行されます |
| `IEmailService` | `NullEmailService` (何もしない)、または `Email:ResendApiKey` が構成されている場合は組み込みの Resend 送信機能 | 確認、パスワードのリセット、アカウントが既に存在することの通知のためのメール配信 |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` (スコープ付き) | 下流アプリへのユーザーのプロビジョニング |
| `ISecretProvider` | `PlaintextSecretProvider`、または `SecretProvider:VaultUri` が構成されている場合は組み込みの `KeyVaultSecretProvider` | 復号可能なシークレットの保存 (Key Vault、AWS Secrets Manager、Vault Transit など) |
| `ITenantContext` | `DefaultTenantContext` (`IConfiguration` から読み取る) | マルチテナントのデプロイメントにおけるテナントの解決 |
| `IKeyManager` | `ProtocolKeyManager` (シングルトン、`Authagonal.Protocol` 由来) | 署名鍵の管理。テナントごとに鍵を分離するにはオーバーライドします |
| `IProvisioningAppProvider` | `ConfigProvisioningAppProvider` (スコープ付き) | 利用可能なプロビジョニング先アプリの解決。動的またはテナントごとのアプリ解決にはオーバーライドします |
| `IAuditLogger` | `NullAuditLogger` (何もしない) | 構成の変更とセキュリティ上重要なイベントの監査証跡 |
| `IClientCredentialsClaimsTransformer` | `NullClientCredentialsClaimsTransformer` (シングルトン、`Authagonal.Protocol` 由来) | `client_credentials` による発行で呼び出し側が渡したコンテキストを検証し、クレームをトークンに強制的に付与するか、発行を拒否します |
| `ITokenExchangeSubjectTransformer` | `NullTokenExchangeSubjectTransformer` (シングルトン、`Authagonal.Protocol` 由来) | RFC 8693 のトークン交換におけるサブジェクトのマッピング。[エージェント認証](agentic-auth) を参照 |
| `ITurnstileKeyProvider` | `OptionsTurnstileKeyProvider` (スコープ付き、`TurnstileOptions` を読み取る) | このリクエストに適用する Turnstile のサイトキーとシークレット |
| `IInteractiveCorsOriginPolicy` | `DenyInteractiveCorsOriginPolicy` (シングルトン、すべてのオリジンを拒否) | `/api/auth/*` への資格情報付きのクロスオリジン呼び出しを許可するオリジン |

さらに 3 つの拡張点が、DI ではなく**ストアのレベル**にあります。`IFieldCipher`、`IIndexTokenizer`、`IChangeWriter` (いずれも `Authagonal.Core.Services`) です。ストレージプロバイダーはこれらを省略可能なコンストラクターパラメーターとして受け取ります。後述のそれぞれのセクションを参照してください。

## IAuthHook {#iauthhook}

`IAuthHook` インターフェイスは、認証のライフサイクルへのフックを提供します。クリティカルパス上のメソッド (認証、ユーザーの作成、トークンの発行) は、例外をスローして操作を中止できます。新しいメソッドは事後の通知です。複数の `IAuthHook` 実装を登録でき、すべてが登録順に実行されます。

```csharp
public interface IAuthHook
{
    // Core lifecycle: implement these
    Task OnUserAuthenticatedAsync(string userId, string email, string method,
        string? clientId = null, CancellationToken ct = default);
    Task OnUserCreatedAsync(string userId, string email, string createdVia,
        CancellationToken ct = default);
    Task OnLoginFailedAsync(string email, string reason,
        CancellationToken ct = default);
    Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType,
        CancellationToken ct = default);
    Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email,
        MfaPolicy clientPolicy, string clientId, CancellationToken ct = default);
    Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default);
    Task OnUserUpdatedAsync(string userId, string email, string updatedVia,
        CancellationToken ct = default);
    Task OnUserDeletedAsync(string userId, string email, string deletedVia,
        CancellationToken ct = default);

    // Additive notifications: default no-op implementations, so existing
    // hooks keep compiling as the interface grows
    Task OnMfaVerifyFailedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnEmailConfirmedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaEnrolledAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaCredentialRemovedAsync(string userId, string email, string mfaMethod,
        bool mfaDisabled, CancellationToken ct = default) => Task.CompletedTask;
    Task OnRecoveryCodesRegeneratedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnPasswordChangedAsync(string userId, string email, string changedVia,
        CancellationToken ct = default) => Task.CompletedTask;

    // Token gate and agentic / consent notifications (also default no-ops)
    Task OnTokenIssuingAsync(TokenIssuanceContext context,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnDelegationMintedAsync(DelegationAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnApprovalRequestedAsync(ApprovalAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnApprovalResolvedAsync(ApprovalAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnAgentConsentChangedAsync(string subjectId, string clientId, string change,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnConsentRevokedAsync(string subjectId, string clientId, int grantsRemoved,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnCapabilityTicketRedeemedAsync(string ticketId, string? subjectId, string clientId,
        CancellationToken ct = default) => Task.CompletedTask;
}
```

### パラメーター {#parameters}

| メソッド | 補足と `method` / `via` の値 |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`、`"passkey"`、`"saml"`、`"oidc"` |
| `OnUserCreatedAsync` | `"admin"`、`"saml"`、`"oidc"` |
| `OnUserUpdatedAsync` | `"admin"`、`"self"` (ホストは独自の値を渡すこともできます。例: SCIM 由来) |
| `OnUserDeletedAsync` | `"admin"`。通知のみで、レコードは既に読み取れない可能性があります |
| `OnLoginFailedAsync` | `"user_not_found"`、`"invalid_password"` など |
| `OnTokenIssuedAsync` | グラントタイプ: `"authorization_code"`、`"refresh_token"`、`"client_credentials"` |
| `ResolveMfaPolicyAsync` | パスワードの検証後に呼び出され、ユーザーの実効 MFA ポリシーを返します。既定: `clientPolicy` をそのまま返します。 |
| `OnMfaVerifiedAsync` | `"totp"`、`"webauthn"`、`"recovery"` |
| `OnMfaVerifyFailedAsync` | `OnMfaVerifiedAsync` と同じ方式。有効な第1要素の資格情報の後にのみ発生するため、これが集中して発生することは MFA の回避の試みを強く示すシグナルになります (パスワードの段階である `OnLoginFailedAsync` とは別です) |
| `OnEmailConfirmedAsync` | ユーザーが確認リンクからメールアドレスを確認しました。既に保存済みです |
| `OnMfaEnrolledAsync` | `"totp"`、`"webauthn"`。資格情報は既に有効です |
| `OnMfaCredentialRemovedAsync` | `"totp"`、`"webauthn"`、`"recoverycode"`。削除によって主要な要素がなくなった場合、`mfaDisabled` は true です |
| `OnRecoveryCodesRegeneratedAsync` | 以前のリカバリーコードのセットは無効になります |
| `OnPasswordChangedAsync` | 例: `"reset"`。変更は保存済みで、既存のセッションは無効化されています |
| `OnTokenIssuingAsync` | `OnTokenIssuedAsync` とは異なり、発行前のゲートです。`authorization_code`、`refresh_token`、`device_code` と、2 つのエージェント向けの発行 (委任されたトークン交換、およびエージェントプロファイルを持つクライアントの `client_credentials`) で発生します。拒否するには例外をスローします。通常の例外はそのメッセージを含む `access_denied` になり、`ProtocolTokenException` をスローすると独自の OAuth エラーを指定できます。リフレッシュではローテーションの前に実行されるため、拒否しても提示されたリフレッシュトークンは引き続き使えます。コンテキストには `ClientId`、`SubjectId`、`GrantType`、`Scopes`、`RequestedAuthorityJson` と、リクエストが組織を選択した場合は `OrganizationId` / `OrganizationSlug` が含まれます |
| `OnDelegationMintedAsync` | トークン交換によって委任された (複合 ID の) トークンが発行されました。通知のみ |
| `OnApprovalRequestedAsync` | 委任された交換が確認を求めるポリシーのアクションで保留され、承認待ちのリクエストが作成されました |
| `OnApprovalResolvedAsync` | 承認待ちのリクエストがユーザーによって承認または拒否されました |
| `OnAgentConsentChangedAsync` | `change` は `"granted"` または `"revoked"` です (エージェントへの継続的な同意) |
| `OnConsentRevokedAsync` | ユーザーが認可済みのアプリを取り消しました。同意と、そのクライアントのセッションに結び付いたグラントは既に削除されています。`grantsRemoved` は削除された件数です (0 は削除なし) |
| `OnCapabilityTicketRedeemedAsync` | ケイパビリティチケットが、それに結び付いたトークンと引き換えられました |

### 例: 監査ロガー {#example-audit-logger}

```csharp
public sealed class AuditAuthHook(ILogger<AuditAuthHook> logger) : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Login: {Email} via {Method}", email, method);
        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email,
        string createdVia, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] User created: {Email} via {Via}", email, createdVia);
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct)
    {
        logger.LogWarning("[AUDIT] Login failed: {Email} ({Reason})", email, reason);
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId,
        string grantType, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Token issued: {ClientId} ({GrantType})",
            clientId, grantType);
        return Task.CompletedTask;
    }

    // ... remaining required methods return Task.CompletedTask
}
```

### 例: ドメインの制限 {#example-domain-restriction}

```csharp
public sealed class DomainRestrictionHook : IAuthHook
{
    private static readonly HashSet<string> BlockedDomains = ["competitor.com"];

    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        var domain = email.Split('@').Last();
        if (BlockedDomains.Contains(domain))
            throw new InvalidOperationException($"Domain {domain} is not allowed");

        return Task.CompletedTask;
    }

    // ... other methods return Task.CompletedTask
}
```

## IClientCredentialsClaimsTransformer {#iclientcredentialsclaimstransformer}

`client_credentials` のトークンにはサブジェクトがないため、トークン交換の拡張点からは扱えません。この拡張点は、ユーザーなしで、トークンが動作するコンテキスト (組織、テナント) を示す必要があるファーストパーティのサービスの呼び出し元のためのものです。クライアント、そのスコープ、および RFC 8707 のリソースが検証された後、トークンが発行される前に実行されます。

```csharp
public interface IClientCredentialsClaimsTransformer
{
    Task<ClientCredentialsClaimsResult> TransformAsync(
        OAuthClient client,
        IReadOnlyList<string> grantedScopes,
        IReadOnlyDictionary<string, string> extraParameters,
        CancellationToken ct = default);
}
```

- `extraParameters` は、トークンリクエストのプロトコル以外のフォームパラメーター (単一値で、最初のものが優先) を保持します。例えば、呼び出し側が送った `organization_id` です。
- `ClientCredentialsClaimsResult.Allow(claims)` を返すと `claims` がトークンに強制的に付与され (null または空の場合は変更なし)、`ClientCredentialsClaimsResult.Reject(error, description)` を返すと、その OAuth エラーで発行が拒否されます。
- 予約済みのプロトコルのクレーム名は、発行時に引き続きブロックされます。
- 呼び出し側が渡した結び付けは、自分自身の信頼できる情報源に照らして検証してください。チェックせずにトークンにコピーしてはいけません。
- 既定の `NullClientCredentialsClaimsTransformer` は `TryAddSingleton` で登録されるため、置き換えるには独自のものを先に登録してください。

## ITurnstileKeyProvider {#iturnstilekeyprovider}

Turnstile の 2 つのキーは 1 つのオブジェクトから取得されるため、ブラウザーが表示するウィジェットと、サーバーが検証に使うシークレットが食い違うことは決してありません。既定の `OptionsTurnstileKeyProvider` は `TurnstileOptions` から `SiteKey` と `SecretKey` を読み取り、1 つのドメインを提供するホストに適しています。顧客が用意したドメインを提供するホストでは Cloudflare がウィジェットのホスト名の数を制限するため、要求元のホストに割り当てられたウィジェットのキーのペアを返す、独自のスコープ付きの実装を登録します。

```csharp
public interface ITurnstileKeyProvider
{
    string? SiteKey { get; }     // null when disabled
    string? SecretKey { get; }   // null or empty disables enforcement
}
```

`TryAddScoped` で登録されるため、`AddAuthagonal` の前に行った登録が優先されます。

## IInteractiveCorsOriginPolicy {#iinteractivecorsoriginpolicy}

対話型の認証 API (`/api/auth/*`) は、同じオリジンから提供されるログインアプリによって操作されるため、既定では資格情報付きのクロスオリジン呼び出しを拒否します。テナントが別のオリジンで独自のログイン画面を構築できるようにするホストは、これを実装して特定のオリジンを保証します。

```csharp
public interface IInteractiveCorsOriginPolicy
{
    ValueTask<bool> IsAllowedAsync(HttpContext context, string origin, string path);
}
```

- リクエストごと、オリジンごとに参照されます。呼び出された時点で、テナントの解決は既に完了しています。
- true を返すと、そのオリジンは、サインインしている人のアカウント、セッション、プロファイル、MFA セットアップの各エンドポイントからの認証済みのレスポンスを読み取れるようになります。ホストが管理しているか検証済みのオリジンについてのみ true を返し、リクエストから取得したオリジンについては決して true を返さないでください。
- 既定 (`DenyInteractiveCorsOriginPolicy`、`TryAddSingleton`) はすべてのオリジンに対して false を返します。

## ISecretProvider {#isecretprovider}

`ISecretProvider` (`Authagonal.Core.Services` 内) は、SSO のクライアントシークレット、SMTP のパスワード、TOTP のシードなど、保存されるシークレットのための復号可能な暗号化の拡張点です。`ProtectAsync` は平文を、ストアが保存する参照に変換し、`ResolveAsync` は参照を平文に戻します。既定の `PlaintextSecretProvider` は値をそのまま保存します (参照が値そのものです)。

```csharp
public interface ISecretProvider
{
    Task<string> ResolveAsync(string secretReference, CancellationToken ct = default);
    Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default);
}
```

`SecretProvider:VaultUri` を設定すると、組み込みの `KeyVaultSecretProvider` (`DefaultAzureCredential` を介した Azure Key Vault) が自動的に組み込まれます。それ以外の場合は、`AddAuthagonal()` の前に独自の実装を登録してください。

## PII フィールドの暗号化: IFieldCipher {#pii-field-encryption-ifieldcipher}

`IFieldCipher` は、ユーザーの個々の PII フィールドの値 (電話番号、会社名、カスタム属性、プロファイル行のメールアドレスと氏名) を保存時に暗号化します。これはストアのレベルの拡張点です。ストレージプロバイダーはこれを省略可能なコンストラクターパラメーターとして受け取り (例: `TableUserStore`)、渡されない場合は値をそのまま通す `NullFieldCipher` が適用されます。そのため暗号化は完全にオプトインであり、構成していないホストは引き続き平文で保存します。

```csharp
public interface IFieldCipher
{
    Task<string> ProtectAsync(string plaintext, CancellationToken ct = default);
    Task<string> ResolveAsync(string stored, CancellationToken ct = default);

    // Batch variants have default loop implementations; override for backends
    // with a one-round-trip batch primitive (e.g. Vault Transit)
    Task<IReadOnlyList<string>> ProtectManyAsync(IReadOnlyList<string> plaintexts,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResolveManyAsync(IReadOnlyList<string> stored,
        CancellationToken ct = default);
}
```

契約上の重要な点が 2 つあります。`ProtectAsync` は自己記述的な暗号文のトークン (例: Vault Transit の `vault:v{n}:...`) を返さなければならず、`ResolveAsync` は自分の暗号文と認識できない値をそのまま返さなければなりません。このそのまま返すというルールによって、暗号化を既存の行に段階的に展開できます。移行されていない行を読み取ると従来の平文が返され、次の書き込みでそれが保護し直されます。

## ブラインドインデックスによる検索: IIndexTokenizer {#blind-index-search-iindextokenizer}

`IIndexTokenizer` は、暗号化されたフィールドを検索可能に保ちます。正規化された平文の値を、決定的で、テーブルのキーとして安全なブラインドインデックスのトークンに変換します。通常は、キーがデータベースの外にある鍵付き HMAC です。決定的であるため、等価検索は引き続き機能します (「email = x」が「token = HMAC(x)」になります)。一方、データベースのダンプからは、トークンを再計算することも逆算することもできません。鍵付き HMAC は順序と範囲スキャンを失わせるため、前方一致検索は、値の各プレフィックスを個別にトークン化することで、その上に実現されています。

> **それでもダンプから分かること。** 「再計算も逆算もできない」が当てはまるのは個々のトークンであって、
> インデックス全体ではありません。3 つの痕跡が残り、これに依存する前に知っておく価値があります。
>
>   *(修正済み。)* ~~**構造。** プレフィックスのインデックスはプレフィックスごとに 1 行を書き込むため、レコードの行数は
>   インデックス対象のフィールドの長さと等しくなります。~~ 現在は、インデックス対象のすべての値が固定数の行を書き込み、
>   どのクエリからも生成されず、ダンプからは本物のプレフィックスと区別できないダミーで埋められます。
> - **等価性と頻度。** トークンは構造上決定的であり、それこそが検索を機能させています。そのため、
>   ダンプからは、どのレコードが同じ値を共有しているか、各値がどれくらい一般的かが分かります。ドメインのインデックスは
>   ユーザー全体を勤務先ごとに分類するため、アドレスを復元しなくても個人を特定できることがよくあります。
> - **選択平文。** ストアを読み取ることができ、*かつ*値をインデックスに登録させることができる攻撃者
>   (アカウントを登録する、SCIM でプロビジョニングされる) は、候補の値を送信してそのトークンを探すことができます。
>   オラクルとなるのは暗号ではなく書き込みの経路であるため、キーがどこにあっても、推測可能な値 (一般的なドメイン、
>   一般的な名) は復元されます。
>
> トークン化は、それが想定している状況、つまりダンプだけを持っている誰かがアドレスを読み取ろうとする状況を防ぎます。
> 残る 2 つの痕跡は、いずれにせよ登録のオラクルが明かしてしまうものと同じです。それらが許容できない場合は、HMAC が
> それらを防いでいると思い込むのではなく、プレフィックスとドメインのインデックスのテーブルを構成しないでください
> (完全一致の検索にはどちらの痕跡もありません)。

```csharp
public interface IIndexTokenizer
{
    Task<string> TokenizeAsync(string value, CancellationToken ct = default);
    Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values,
        CancellationToken ct = default);
}
```

`IFieldCipher` と同様に、これは値をそのまま通す既定実装 (`NullIndexTokenizer`) を持つ、ストアの省略可能なコンストラクターパラメーターです。そのため、オプトインするまで、インデックスの行は平文をキーとしたままです。返されるトークンは、Azure Table の PartitionKey/RowKey の値として安全でなければなりません (`/ \ # ?` や制御文字を含まないこと)。

## 変更ログの記録: IChangeWriter {#change-log-capture-ichangewriter}

`IChangeWriter` (0.6.0 で `ITombstoneWriter` から名前が変わりました) は、変更されたすべての行のキーを専用の変更ログのテーブルに記録します。これにより、増分バックアップは、稼働中のテーブルのインデックスのない `Timestamp` 列をスキャンせずに、変更内容を見つけられます。削除はすべてのテーブルについて記録されます (稼働中の行のスキャンでは、既に存在しない行は見えないからです)。アップサートは、バックアップがスキャンの代わりにログから読み取るテーブルについて記録されます。組み込みの実装は、`TableChangeWriter` (Azure Table Storage)、`DynamoChangeWriter` (DynamoDB)、`SqlChangeWriter` (PostgreSQL / SQLite) です。

```csharp
public interface IChangeWriter
{
    // Deletes
    Task WriteAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);

    // Upserts
    Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteUpsertBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);
}
```

実装する側と呼び出す側のための順序の契約: データの行を削除する**前に**、削除のトゥームストーンを書き込んでください。逆の順序でクラッシュすると、その削除は以降のすべてのバックアップから失われます。削除は、再スキャンで自己修復できない唯一の種類の変更だからです。逆方向のクラッシュは安全です。後でそのキーに書き込みがあれば新しいタイムスタンプが付け直され、マージとリストアはトゥームストーンより後に書き込まれた行を保持します。

## 独自のエンドポイント {#custom-endpoints}

Authagonal のエンドポイントと並べて、独自のエンドポイントを追加できます。

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## HashiCorp Vault Transit との統合 {#hashicorp-vault-transit-integration}

> **JWT の署名は Vault に委任されません。** このセクションには以前、それを有効にするように見える DI のコード例が
> 載っていました。`VaultTransitCryptoProvider` を登録しても、**トークンの署名には何の効果もありません**。
> `ProtocolKeyManager` は `ProtocolSigningKeyOps.BuildSigningCredentials` を呼び出し、これは `ISigningKeyStore` 内の
> 鍵素材から `ECDsaSecurityKey` を構築するもので、それを `VaultTransitSecurityKey` に置き換えるものは何もありません。
> 以前のコード例に従ったホストでは、ES256 のトークンが JWKS で検証できたため、Vault が署名していると結論づけるのも
> 無理はありませんでした。しかし実際には、秘密鍵は初回起動時にローカルで生成され、プライマリのデータストアに、
> `IFieldCipher` がたまたま登録されていない限り平文で保存されていました。そのストアへの読み取りアクセスは、
> 発行者への完全ななりすましを意味します。署名鍵が決して HSM の外に出ないことを求めるコンプライアンス要件がある場合、
> これはそれを満たしません。
>
> 現在、サーバーは `VaultTransitCryptoProvider` が登録されているのを検出すると起動時にエラーをログに記録するため、
> この誤解が気付かれないまま残ることはありません。
>
> これを実現するには、DI の登録以上のものが必要です。`ISigningKeyStore` はローカルの鍵素材を持たない鍵
> (秘密のスカラー値ではなく Transit の鍵の*名前*) を表現できなければならず、`BuildSigningCredentials` には
> `VaultTransitSecurityKey` を返すための拡張点が必要で、`BuildJwksAsync` は Vault から読み戻した公開鍵を公開しなければならず、
> ローテーションと事前公開はローカルで生成するのではなく、Transit の鍵のバージョンを作成して昇格させなければなりません。
> `VaultTransitClient`、`VaultTransitSecurityKey`、`VaultTransitSignatureProvider`、`VaultTransitCryptoProvider` は、
> 機能する部品であるため残してあります。欠けているのはそれらをつなぐ部分です。

現在 `VaultTransitClient` が**実際に**役立つのは、暗号化と HMAC の拡張点です。保存時の PII のための Vault を使った
`IFieldCipher`、または鍵付きのブラインドインデックスのための `IIndexTokenizer` として使えます。

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("Vault", client =>
{
    client.BaseAddress = new Uri("https://vault.example.com");
    client.DefaultRequestHeaders.Add("X-Vault-Token", "hvs.xxx");
});

builder.Services.AddSingleton<VaultTransitClient>();

// Your own adapters over the client. These are the seams Authagonal actually consumes.
builder.Services.AddSingleton<IFieldCipher, MyVaultFieldCipher>();
builder.Services.AddSingleton<IIndexTokenizer, MyVaultIndexTokenizer>();

builder.Services.AddAuthagonal(builder.Configuration);
```

`IFieldCipher` を登録すると、`PlaintextSigningKeyWarning` も表示されなくなります。署名鍵のストアは、その鍵素材を同じ拡張点を通して
扱うからです。これが、元の主張に最も近い形として現在利用できるものです。秘密鍵は依然としてローカルに存在しますが、平文ではありません。

`VaultTransitClient` は次の操作を提供します。

| メソッド | 説明 |
|---|---|
| `SignAsync(keyName, data)` | Vault Transit の鍵を使ってデータに署名します |
| `VerifyAsync(keyName, data, signature)` | Transit の検証エンドポイントを通じて、JWS 形式の署名を検証します |
| `EncryptAsync` / `DecryptAsync` (+ `EncryptBatchAsync` / `DecryptBatchAsync`) | `aes256-gcm96` の鍵による共通鍵暗号化。そのまま保存する `vault:v{n}:...` のトークンを返します |
| `HmacAsync` / `HmacBatchAsync` | `hmac` の鍵による鍵付き HMAC (ブラインドインデックスのトークン) |
| `CreateKeyAsync(keyName, type)` | 新しい Transit の鍵を作成します (既定: `ecdsa-p256`) |
| `EnsureKeyTypeAsync(keyName, type)` | 目的の種類の鍵が存在することを冪等に保証します (種類が一致しない場合は再作成します。Transit の鍵は種類をその場で変更できません) |
| `RotateKeyAsync(keyName)` | 鍵を新しいバージョンにローテーションします |
| `DeleteKeyAsync(keyName)` | 鍵を削除します (先に `deletion_allowed` を有効にします) |
| `ReadKeyAsync(keyName)` | 鍵のメタデータ、バージョン、公開鍵を読み取ります |
| `KeyExistsAsync(keyName)` | 鍵が存在するかどうかを確認します |

`VaultTransitCryptoProvider` は .NET の `JsonWebTokenHandler` と統合され、JWT の署名で透過的に Vault を使うようにします。低レベルの統合は `VaultTransitSecurityKey` と `VaultTransitSignatureProvider` が担います。

## メール {#email}

組み込みの Resend 送信機能は、`Email:ResendApiKey` が構成されると自動的に有効になります (`Email:SenderEmail` も設定してください)。`IEmailService` が 1 つもない場合、メールは `NullEmailService` によって破棄されます。メールアドレスの確認を求めるログインの制限は既定でオンになっているため、自己登録したユーザーは決してログインできなくなります。その状態では、`UseAuthagonal()` が起動時に目立つ警告をログに記録します。

別のプロバイダーを使うには、`AddAuthagonal()` の前に独自の `IEmailService` を登録してください。

```csharp
public sealed class SmtpEmailService(SmtpClient smtp) : IEmailService
{
    public async Task SendVerificationEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Verify your email", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }

    public async Task SendPasswordResetEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Reset your password", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }
}
```

`IEmailService` は `SendAccountExistsEmailAsync` も宣言しています (既に登録されているメールアドレスで誰かが登録しようとしたときに送信され、登録のレスポンスをアカウントの列挙に対して中立に保ちます)。何もしない既定の実装があるため、既存の実装もそのままコンパイルできます。

## 関連項目 {#see-also}

- [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server): 完全な動作例
- [demos/sample-app/](https://github.com/authagonal/authagonal/tree/master/demos/sample-app): クライアントアプリの例
