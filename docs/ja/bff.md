---
layout: default
title: Backend-for-Frontend (BFF)
locale: ja
---

# Backend-for-Frontend (BFF)

アクセストークンやリフレッシュトークンを JavaScript から到達できるストレージに保持するブラウザ SPA は、その両方を XSS にさらします。BFF は**自社のバックエンドでホストする機密 OIDC クライアント**です。認可コード + PKCE フローをサーバー側で実行し、トークンをサーバー側のセッションに保持し、ブラウザには httpOnly のセッション Cookie 以外何も渡しません。SPA から自社 API への呼び出しは BFF のプロキシを経由し、プロキシが送出時にセッションのアクセストークンを付与します。

同じプロトコルを話す 2 つの実装が提供されています。

| パッケージ | 対象 | ソース |
|---|---|---|
| `Authagonal.Bff` (NuGet) | ASP.NET Core ホスト | `src/Authagonal.Bff/` |
| `@authagonal/bff` (npm) | Express と Next.js (App Router) | `bff-lib/` |

BFF は認証ホストのごく普通の機密クライアントです。OIDC ディスカバリと、認可・トークン・取り消し・セッション終了の各エンドポイントを使うため、認証ホスト側に必要なのは登録済みのクライアントだけです。

## 1. BFF クライアントを登録する {#1-register-a-bff-client}

クライアントは**機密** (シークレットを持つ) で、PKCE を必須とし、サーバー側でリフレッシュを行いたい場合は `offline_access` を許可されている必要があります。次を登録します。

- リダイレクト URI `https://app.example.com/bff/callback`
- ログアウト後のリダイレクト URI `https://app.example.com/` (ログアウト時に `returnUrl` を使う場合は `https://app.example.com/bff/logout-callback` も。[ログアウト](#logout)を参照)

[バックチャネルログアウト](index#key-features)によるサブジェクト全体の「すべての場所からログアウト」を行うには、クライアントを `BackChannelLogoutSessionRequired = false` で登録してください。BFF は `sid` を含むログアウトトークンも、`sub` だけを含むログアウトトークンも受け付けます。

## 2. 組み込む (.NET) {#2-wire-it-up-net}

```csharp
builder.Services.AddAuthagonalBff(o =>
{
    o.Authority    = "https://auth.example.com";
    o.ClientId     = builder.Configuration["Bff:ClientId"]!;
    o.ClientSecret = builder.Configuration["Bff:ClientSecret"]!;
    o.Scope        = ["openid", "profile", "email", "offline_access"];
    o.PostLogoutRedirectUri = "https://app.example.com/";
});

var app = builder.Build();
app.UseForwardedHeaders();   // required behind a reverse proxy or ingress
app.MapAuthagonalBff();
app.MapFallbackToFile("index.html");
app.Run();
```

`UseForwardedHeaders` は重要です。TLS を終端するプロキシの背後では BFF からは平文の http に見えるため、これがないと `__Host-` セッション Cookie が `Secure` なしで書き込まれ、ブラウザに破棄されます。プロキシの宣言方法は[インストール](installation#production-security-checklist)を参照してください。

### Node (Express) {#node-express}

```ts
import { authagonalBff } from '@authagonal/bff/express';

app.set('trust proxy', 1);
app.use(authagonalBff({
  authority: 'https://auth.example.com',
  clientId: process.env.BFF_CLIENT_ID!,
  clientSecret: process.env.BFF_CLIENT_SECRET!,
  scope: ['openid', 'profile', 'email', 'offline_access'],
  cookieSecret: process.env.BFF_COOKIE_SECRET!,   // encrypts the session cookie
  postLogoutRedirectUri: 'https://app.example.com/',
}));
```

Next.js では、`app/bff/[...bff]/route.ts` で `@authagonal/bff/next` の `createBffRoute` を使います。どちらについても `bff-lib/README.md` を参照してください。

## エンドポイント {#endpoints}

`BasePath` (既定値 `/bff`) の下にマウントされます。

| ルート | 用途 |
|---|---|
| `GET /bff/login?returnUrl=/` | ログインを開始します。ログインごとの相関 Cookie を設定し、PKCE (`S256`)、`state`、`nonce` を付けて `/connect/authorize` にリダイレクトします。 |
| `GET /bff/callback` | OIDC のリダイレクト URI (`CallbackPath`) です。コードを交換してセッションを作成します。 |
| `GET /bff/user` | `{ isAuthenticated, claims, sessionExpiresAt }` を返します。CSRF 対策ヘッダーが必要です。常に `Cache-Control: no-store` です。 |
| `GET\|POST /bff/logout` | セッションをローカルと認証ホストの両方で終了します。`POST` には CSRF 対策ヘッダーが必要です。`GET` は通常のナビゲーションです。 |
| `GET /bff/logout-callback` | ログアウトに `returnUrl` が指定された場合の、セッション終了の往復後に戻ってくる着地点です。 |
| `POST /bff/backchannel-logout` | OIDC バックチャネルログアウトをサーバー間で受け取るエンドポイントです。署名付きログアウトトークンで認証されるため、CSRF ヘッダーは受け取りません。 |
| `GET /bff/ws-ticket` | オプトイン (`WsTicketsEnabled`)、.NET のみ。[WebSocket 認証](websocket-auth)を参照してください。 |
| `GET /bff/token?resource=...` | オプトイン (`TokenEndpointEnabled`)、.NET のみ。[別オリジン向けの交換済みトークン](#exchanged-tokens-for-another-origin)を参照してください。 |
| `ANY /bff/api/**` | トークンを付与するプロキシです。`Upstreams` が空でない場合にのみマップされます。 |

`/bff/user` の `claims` は、id_token のクレームからプロトコル上の仕組みに関わるもの (`iss`、`aud`、`exp`、`iat`、`nbf`、`nonce`、`at_hash`、`c_hash`、`s_hash`、`azp`、`jti`、`sid`、`auth_time`、`acr`、`amr`、`typ`) を除いた、フラットな文字列マップです。`roles` や `groups` などの配列クレームはスペース区切りで連結されます。クレームはリフレッシュされた id_token のたびに読み直されるため、ログイン後に付与されたロールは、次回のログインではなく次回のリフレッシュで SPA に届きます。

## ブラウザから {#from-the-browser}

ナビゲーション以外のすべての呼び出しには固定のヘッダーを付けます。これは `SameSite=Lax` と併せて CSRF を防ぎます。値は何でも受け付けられ、存在だけが確認されます。

```js
const me = await fetch('/bff/user', { headers: { 'X-Authagonal-Bff': '1' } }).then(r => r.json());
if (!me.isAuthenticated) location.href = '/bff/login?returnUrl=' + encodeURIComponent(location.pathname);
```

ログインとログアウトは `fetch` ではなく**ナビゲーション** (`location.href = '/bff/login'`) で行います。ヘッダー名は `AntiForgeryHeader` で設定します。

## プロキシ {#the-proxy}

上流を設定すると、SPA は `/bff/api/<prefix>/...` を呼び出します。

```csharp
o.Upstreams.Add(new BffUpstream
{
    Prefix = "/orders",
    TargetBaseUrl = "https://api.internal.example.com",
});
```

プロキシは CSRF 対策ヘッダーと有効なセッションを必要とし、アクセストークンの有効期限まで `RefreshThresholdSeconds` 以内であればリフレッシュし、`Authorization: Bearer` を付けてリクエストを転送し、レスポンスをストリーミングで返します。セッション Cookie が転送されることはありません。受信した `X-Forwarded-*`、`Forwarded`、`X-Real-IP` ヘッダーは取り除かれ、BFF 自身の状態から改めて設定されるため、SPA 内のスクリプトがクライアント IP やスキームを偽って申告することはできません。上流からのリダイレクトは、追従せずにブラウザへ中継されます。

上流ごとの設定 (`BffUpstream`):

| プロパティ | 意味 |
|---|---|
| `Prefix` | この上流を選択する、`/bff/api` 以降のパスです。 |
| `TargetBaseUrl` | 一致したリクエストの転送先です。 |
| `StripPrefix` | ターゲットに付加する前に、一致したプレフィックスを取り除きます。パスの名前空間を共有する複数のバックエンドに 1 つの BFF から振り分けられます。 |
| `RequiredAuthority` | `"type:action"` の組です。プロキシは送出するトークンの RFC 9396 `authorization_details` を検査し、すべての組が許可されていなければ 403 を返します。[エージェント認可](agentic-auth)を参照してください。 |
| `AuthorityLocation` | この上流が `TargetBaseUrl` と異なる名前で知られている場合の、`locations` のルートです。 |
| `StrictAuthority` | プロキシが評価できない制約を付与が含む場合、そのまま通すのではなく呼び出しを拒否します。 |

関連オプション: `AllowAnonymousProxyRequests` を指定すると、セッションのない (またはリフレッシュできない) リクエストに 401 を返す代わりに、`Authorization` ヘッダーなしで転送します。自分で判断する API 向けです。`RequiredAuthority` でゲートされたルートが匿名になることはありません。`ExchangeRoutes` はプロキシのルートを [RFC 8693 の交換](agentic-auth)に結び付けます。これにより、上流はセッションの主トークンではなく、スコープが絞り込まれコンテキストに束縛されたトークンを受け取ります。各ルートにはプレースホルダーをちょうど 1 つ含む `PathPattern` があり (サポートされる制約は `:guid` のみ)、取り出されたセグメントが交換パラメーターとして送られ、交換が拒否されると 403 になります。未知の制約は、より広いトークンを黙って転送するのではなく、起動時に失敗します。

## ログアウト {#logout}

`/bff/logout` は、セッションのリフレッシュトークンを (ベストエフォートで) 取り消し、セッションを削除し、Cookie を消去し、セッションの `id_token_hint` を付けて認証ホストのセッション終了エンドポイントにリダイレクトします。セッションがない場合は認証ホストで終了すべきものがないため、`PostLogoutRedirectUri` に直接リダイレクトします。`returnUrl` がある場合、認証ホストは `/bff/logout-callback` にリダイレクトし、そこでターゲットを `ReturnUrlAllowlist` に照らして再検証してからリダイレクトします。このコールバックを、クライアントのログアウト後のリダイレクト URI として登録してください。

バックチャネルログアウトはサーバー側でセッションを削除します。ログアウトトークンに `sid` があればそれによって、なければその `sub` のすべてのセッションを削除します。`sub` は 1 つの発行者の中でしか一意でないため、削除はトークンに署名した発行者のテナントに限定されます。ログアウトトークンは `iat` を含み、かつ新しいものでなければなりません。

## オプションリファレンス (.NET) {#options-reference-net}

| オプション | 既定値 | 備考 |
|---|---|---|
| `Authority`、`ClientId`、`ClientSecret` | 必須 | `TenantQueryParam` が設定されている場合は不要です。 |
| `Scope` | `openid profile offline_access` | `offline_access` でリフレッシュが有効になります。 |
| `BasePath` | `/bff` | |
| `CallbackPath` | `/bff/callback` | 登録済みのリダイレクト URI と一致する必要があります。 |
| `CookieName` | `__Host-agbff` | `__Host-` プレフィックスにより Secure、`Path=/`、Domain なしが強制されるため、https が必要です。ローカルの http 開発では上書きしてください。 |
| `SessionLifetime` | 8 時間 | リフレッシュの有無にかかわらない絶対的な上限です。 |
| `PersistentCookie` | `false` | true の場合、Cookie には `SessionLifetime` を上限とする `Max-Age` が付き、ブラウザを再起動しても残ります (「サインインしたままにする」)。バックチャネルログアウトは引き続きセッションを終了させます。 |
| `CorrelationLifetime` | 30 分 | `/bff/login` からコールバックまでにログインがかけられる時間です。サインアップ、確認メール、サインインまでを含みます。 |
| `RefreshThresholdSeconds` | 60 | |
| `ReturnUrlAllowlist` | 空 | 相対でない `returnUrl` が指せるオリジンです。相対パスは常に許可され、それ以外はすべて `/` になります。 |
| `LoginPassthroughParams` | 空 | `/bff/login` から `/connect/authorize` にコピーされるクエリパラメーター名です (例: `idp_hint`)。標準パラメーターが常に優先されます。 |
| `AntiForgeryHeader` | `X-Authagonal-Bff` | |
| `PostLogoutRedirectUri` | なし | |
| `WsTicketsEnabled`、`WsTicketLifetime`、`TicketExchangeParams` | 無効、30 秒、空 | [WebSocket 認証](websocket-auth)を参照してください。 |
| `TokenEndpointEnabled`、`TokenEndpointResources`、`TokenEndpointExchangeParams` | 無効、空、空 | リソースを指定せずに有効にすると、起動時に失敗します。 |
| `Upstreams`、`ExchangeRoutes`、`AllowAnonymousProxyRequests` | 空、空、`false` | [プロキシ](#the-proxy)を参照してください。 |
| `TenantQueryParam` | なし | マルチテナントモードです。後述します。 |

`SessionMode` は `Store` のみ実装されています。`Stateless` は予約済みで、起動時に失敗します。

Node パッケージは、`authority`、`clientId`、`clientSecret`、`scope`、`basePath`、`callbackPath`、`cookieName`、`refreshThresholdSeconds`、`returnUrlAllowlist`、`postLogoutRedirectUri`、`antiForgeryHeader`、`sessionLifetimeSeconds`、`upstreams`、`tenantQueryParam` という camelCase の対応オプションに加え、`cookieSecret`、`sessionStore`、`cookieProtector`、`tenantResolver`、`clientIp` を受け取ります。WebSocket チケットとトークンのエンドポイントはありません。

## セッションと複数インスタンスでの実行 {#sessions-and-running-more-than-one-instance}

セッションは `IBffSessionStore` を通じて保存されます。既定は `IDistributedCache` で、`AddAuthagonalBff` より**前に**実際のキャッシュ (Redis など) を登録しない限りインメモリです。

共有キャッシュだけでは十分ではありません。リフレッシュの単一実行化はプロセス単位ですが、セッションとそのローテーションするリフレッシュトークンは共有キャッシュにあります。2 つのレプリカが同じセッションを読み、両方ともリフレッシュが必要だと判断し、両方が同じリフレッシュトークンを引き換えることがありえます。認証ホストは 2 回目の引き換えを盗まれたトークンのリプレイとみなし、グラントファミリー全体を取り消すため、ユーザーはすべての場所でサインアウトされます。レプリカをまたぐロックを、次の 2 つの方法のいずれかで提供してください。

- **`ILeaseProvider` を登録する** (バックエンドは任意)。Azure、AWS、SQL の各プロバイダーは `AddAuthagonalClustering` を通じてこれを提供します。[スケーリング](scaling)を参照してください。
- **セッションストアに `IBffRefreshLockStore` を実装する** (`TryAcquireRefreshLockAsync(sessionId, ttl)` と `ReleaseRefreshLockAsync`)。これは TTL 付きの条件付き書き込みで、たとえば Redis の `SET NX PX` です。`IDistributedCache` には「存在しない場合のみ設定」がないため、既定のストアではこれを提供できません。Node のセッションストアには同等の `acquireRefreshLock` / `releaseRefreshLock` があります。

どちらもない場合、デプロイは認証ホストの `Auth:RefreshTokenReuseGraceSeconds` に頼ることになり、Server ホストではその既定値は 0 (厳格) です。セッションストアが共有されているように見えるのにロックがない場合、BFF は起動時に警告をログに出力します。

独自の `IBffSessionStore` は、`RemoveBySidAsync` と `RemoveBySubjectAsync` の `tenantKey` 引数を尊重しなければなりません。その他の拡張点は `ICookieProtector` (既定: ASP.NET Data Protection) と `ITokenClient` です。

## 1 つの BFF で多数のテナントを扱う {#many-tenants-from-one-bff}

`TenantQueryParam` (例: `"slug"`) を設定し、`IBffTenantResolver` を登録します。`/bff/login?slug=acme` はテナントの `BffTenantConfig` (authority、クライアント ID、シークレット、スコープ) を解決し、そのキーがセッションに保存されるため後続のリクエストで再解決でき、バックチャネルログアウトは `ResolveByIssuerAsync` を通じてトークンの `iss` からテナントを解決します。`TenantQueryParam` が未設定なら BFF はシングルテナントで、静的なオプションが使われます。

## 別オリジン向けの交換済みトークン {#exchanged-tokens-for-another-origin}

Cookie モデルでは、別オリジンにあるリソースサーバー (たとえば SPA が埋め込む iframe アプリ) には届きません。`TokenEndpointEnabled` を有効にすると `GET /bff/token?resource=<audience>` が追加されます。これは RFC 8693 で**交換された**トークンについて `{ accessToken, expiresInSeconds }` を返します。そのトークンは `TokenEndpointResources` のうちの 1 つのリソースを宛先とし (それ以外は 400 `resource_not_allowed`)、クエリ上の `TokenEndpointExchangeParams` の値に束縛され、有効期間は短くなります。ブラウザがセッションの主トークンを受け取ることはなく、交換済みトークンはメモリ内にのみ保持すべきです。テナントのクライアントにはトークン交換グラントが必要で、そのリソースをオーディエンスとして宣言しなければなりません。交換が拒否されると 403 になります。
