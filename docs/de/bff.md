---
layout: default
title: Backend-for-Frontend (BFF)
locale: de
---

# Backend-for-Frontend (BFF)

Eine Browser-SPA, die ein Access oder Refresh Token in einem für JavaScript erreichbaren Speicher hält, setzt beide einem XSS-Angriff aus. Das BFF ist ein **vertraulicher OIDC-Client, den Sie in Ihrem eigenen Backend betreiben**. Es führt den Authorization-Code-Ablauf mit PKCE serverseitig aus, hält die Tokens in einer serverseitigen Sitzung und gibt dem Browser nichts außer einem httpOnly-Sitzungscookie. Aufrufe der SPA an Ihre APIs laufen über den Proxy des BFF, der beim Weiterleiten das Access Token der Sitzung anhängt.

Es wird zweimal ausgeliefert, mit demselben Protokoll:

| Paket | Für | Quelle |
|---|---|---|
| `Authagonal.Bff` (NuGet) | ASP.NET-Core-Hosts | `src/Authagonal.Bff/` |
| `@authagonal/bff` (npm) | Express und Next.js (App Router) | `bff-lib/` |

Das BFF ist ein gewöhnlicher vertraulicher Client des Auth-Hosts: Es nutzt die OIDC-Discovery sowie die Endpunkte für Authorize, Token, Revocation und End Sitzung. Vom Auth-Host braucht es also nur einen registrierten Client.

## 1. Einen BFF-Client registrieren {#1-register-a-bff-client}

Der Client muss **vertraulich** sein (er hat ein Secret), PKCE verlangen und `offline_access` erlauben, wenn Sie serverseitige Erneuerung wünschen. Registrieren Sie:

- die Redirect-URI `https://app.example.com/bff/callback`
- die Post-Logout-Redirect-URI `https://app.example.com/` (und `https://app.example.com/bff/logout-callback`, wenn Sie beim Logout `returnUrl` verwenden, siehe [Logout](#logout))

Für ein subjektweites "überall abmelden" per [Back-Channel Logout](index#key-features) registrieren Sie den Client mit `BackChannelLogoutSessionRequired = false`. Das BFF akzeptiert Logout Tokens, die `sid` oder nur `sub` tragen.

## 2. Einbinden (.NET) {#2-wire-it-up-net}

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

`UseForwardedHeaders` ist wichtig: Hinter einem Proxy, der TLS terminiert, sieht das BFF einfaches http. Ohne diesen Aufruf wird das `__Host-`-Sitzungscookie ohne `Secure` geschrieben, und Browser verwerfen es. Wie Sie den Proxy deklarieren, beschreibt [Installation](installation#production-security-checklist).

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

Für Next.js verwenden Sie `createBffRoute` aus `@authagonal/bff/next` in `app/bff/[...bff]/route.ts`. Beides ist in `bff-lib/README.md` beschrieben.

## Endpunkte {#endpoints}

Eingehängt unter `BasePath` (Standard `/bff`).

| Route | Zweck |
|---|---|
| `GET /bff/login?returnUrl=/` | Startet den Login: setzt ein Korrelations-Cookie pro Login und leitet mit PKCE (`S256`), `state` und `nonce` an `/connect/authorize` weiter. |
| `GET /bff/callback` | Die OIDC-Redirect-URI (`CallbackPath`). Tauscht den Code ein und legt die Sitzung an. |
| `GET /bff/user` | `{ isAuthenticated, claims, sessionExpiresAt }`. Erfordert den Anti-Forgery-Header. Immer `Cache-Control: no-store`. |
| `GET\|POST /bff/logout` | Beendet die Sitzung lokal und beim Auth-Host. Ein `POST` erfordert den Anti-Forgery-Header; ein `GET` ist eine einfache Navigation. |
| `GET /bff/logout-callback` | Zielseite für den End-Session-Roundtrip, wenn dem Logout eine `returnUrl` übergeben wurde. |
| `POST /bff/backchannel-logout` | Empfänger des OIDC-Back-Channel-Logouts von Server zu Server. Authentifiziert durch das signierte Logout Token, daher ohne CSRF-Header. |
| `GET /bff/ws-ticket` | Optional (`WsTicketsEnabled`), nur .NET. Siehe [WebSocket-Authentifizierung](websocket-auth). |
| `GET /bff/token?resource=...` | Optional (`TokenEndpointEnabled`), nur .NET. Siehe [Ausgetauschte Tokens für einen anderen Origin](#exchanged-tokens-for-another-origin). |
| `ANY /bff/api/**` | Der Proxy, der Tokens einfügt. Wird nur eingehängt, wenn `Upstreams` nicht leer ist. |

`claims` in `/bff/user` ist eine flache String-Map der Claims des id_token, ohne die Protokollbestandteile (`iss`, `aud`, `exp`, `iat`, `nbf`, `nonce`, `at_hash`, `c_hash`, `s_hash`, `azp`, `jti`, `sid`, `auth_time`, `acr`, `amr`, `typ`). Array-Claims wie `roles` und `groups` werden durch Leerzeichen verbunden. Die Claims werden aus jedem erneuerten id_token neu gelesen; eine nach dem Login vergebene Rolle erreicht die SPA also bei der nächsten Erneuerung und nicht erst beim nächsten Login.

## Aus dem Browser {#from-the-browser}

Jeder Aufruf, der keine Navigation ist, trägt einen statischen Header, der zusammen mit `SameSite=Lax` vor CSRF schützt. Jeder Wert wird akzeptiert; geprüft wird nur, ob der Header vorhanden ist.

```js
const me = await fetch('/bff/user', { headers: { 'X-Authagonal-Bff': '1' } }).then(r => r.json());
if (!me.isAuthenticated) location.href = '/bff/login?returnUrl=' + encodeURIComponent(location.pathname);
```

An- und abgemeldet wird per **Navigation** (`location.href = '/bff/login'`), nicht per `fetch`. Der Name des Headers steht in `AntiForgeryHeader`.

## Der Proxy {#the-proxy}

Konfigurieren Sie Upstreams; die SPA ruft dann `/bff/api/<prefix>/...` auf:

```csharp
o.Upstreams.Add(new BffUpstream
{
    Prefix = "/orders",
    TargetBaseUrl = "https://api.internal.example.com",
});
```

Der Proxy verlangt den Anti-Forgery-Header und eine aktive Sitzung, erneuert das Access Token, wenn es innerhalb von `RefreshThresholdSeconds` abläuft, leitet den Request mit `Authorization: Bearer` weiter und streamt die Antwort zurück. Das Sitzungscookie wird nie weitergeleitet. Eingehende Header `X-Forwarded-*`, `Forwarded` und `X-Real-IP` werden entfernt und aus dem eigenen Zustand des BFF neu gesetzt, sodass ein Skript in der SPA keine Client-IP und kein Schema vorgeben kann. Weiterleitungen des Upstreams werden an den Browser durchgereicht, statt ihnen zu folgen.

Pro Upstream (`BffUpstream`):

| Eigenschaft | Bedeutung |
|---|---|
| `Prefix` | Pfad nach `/bff/api`, der diesen Upstream auswählt. |
| `TargetBaseUrl` | Wohin passende Requests weitergeleitet werden. |
| `StripPrefix` | Entfernt das passende Präfix, bevor der Pfad an das Ziel angehängt wird. So kann ein BFF auf mehrere Backends verteilen, die sich einen Pfad-Namensraum teilen. |
| `RequiredAuthority` | Paare der Form `"type:action"`. Der Proxy prüft die `authorization_details` nach RFC 9396 im ausgehenden Token und gibt 403 zurück, sofern nicht jedes Paar erlaubt ist. Siehe [Agentic Auth](agentic-auth). |
| `AuthorityLocation` | Die `locations`-Wurzel, unter der dieser Upstream bekannt ist, wenn sie von `TargetBaseUrl` abweicht. |
| `StrictAuthority` | Lehnt den Aufruf ab, wenn ein Grant eine Einschränkung trägt, die der Proxy nicht auswerten kann, statt ihn durchzureichen. |

Verwandte Optionen: `AllowAnonymousProxyRequests` leitet einen Request ohne Sitzung (oder mit einer nicht erneuerbaren) ohne `Authorization`-Header weiter, statt mit 401 zu antworten; gedacht für APIs, die selbst entscheiden. Eine durch `RequiredAuthority` geschützte Route ist nie anonym. `ExchangeRoutes` bindet Proxy-Routen an einen [Austausch nach RFC 8693](agentic-auth), sodass der Upstream statt des primären Tokens der Sitzung ein eingeschränktes, kontextgebundenes Token erhält: Jede Route hat ein `PathPattern` mit genau einem Platzhalter (die einzige unterstützte Einschränkung ist `:guid`), das erfasste Segment wird als Austauschparameter gesendet, und ein abgelehnter Austausch ergibt eine 403. Eine unbekannte Einschränkung führt schon beim Start zu einem Fehler, statt stillschweigend das weiter reichende Token weiterzuleiten.

## Logout {#logout}

`/bff/logout` widerruft das Refresh Token der Sitzung (nach bestem Bemühen), entfernt die Sitzung, löscht das Cookie und leitet mit dem `id_token_hint` der Sitzung an den End-Session-Endpunkt des Auth-Hosts weiter. Ohne Sitzung gibt es beim Auth-Host nichts zu beenden, daher wird direkt an `PostLogoutRedirectUri` weitergeleitet. Mit einer `returnUrl` leitet der Auth-Host zurück an `/bff/logout-callback`, das das Ziel erneut gegen `ReturnUrlAllowlist` prüft und dorthin weiterleitet. Registrieren Sie diesen Callback als Post-Logout-Redirect-URI des Clients.

Der Back-Channel Logout entfernt Sitzungen serverseitig: anhand von `sid`, wenn das Logout Token eines enthält, andernfalls jede Sitzung des `sub`. Das Entfernen ist auf den Mandanten beschränkt, dessen Issuer das Token signiert hat, weil `sub` nur innerhalb eines Issuers eindeutig ist. Das Logout Token muss `iat` tragen und aktuell sein.

## Optionsreferenz (.NET) {#options-reference-net}

| Option | Standard | Hinweise |
|---|---|---|
| `Authority`, `ClientId`, `ClientSecret` | Pflicht | Nicht erforderlich, wenn `TenantQueryParam` gesetzt ist. |
| `Scope` | `openid profile offline_access` | `offline_access` ermöglicht die Erneuerung. |
| `BasePath` | `/bff` | |
| `CallbackPath` | `/bff/callback` | Muss der registrierten Redirect-URI entsprechen. |
| `CookieName` | `__Host-agbff` | Das Präfix `__Host-` erzwingt Secure, `Path=/` und keine Domain und erfordert daher https. Für lokale Entwicklung über http überschreiben. |
| `SessionLifetime` | 8 Stunden | Absolute Obergrenze, unabhängig von Erneuerungen. |
| `PersistentCookie` | `false` | Bei true erhält das Cookie ein `Max-Age`, das durch `SessionLifetime` begrenzt ist, und übersteht einen Neustart des Browsers ("angemeldet bleiben"). Der Back-Channel Logout beendet die Sitzung trotzdem. |
| `CorrelationLifetime` | 30 Minuten | Wie lange ein Login zwischen `/bff/login` und dem Callback dauern darf. Deckt Registrierung, Bestätigungs-E-Mail und Anmeldung ab. |
| `RefreshThresholdSeconds` | 60 | |
| `ReturnUrlAllowlist` | leer | Origins, auf die eine nicht relative `returnUrl` zeigen darf. Relative Pfade sind immer erlaubt; alles andere wird zu `/`. |
| `LoginPassthroughParams` | leer | Namen von Query-Parametern, die von `/bff/login` an `/connect/authorize` übernommen werden (zum Beispiel `idp_hint`). Die Standardparameter haben immer Vorrang. |
| `AntiForgeryHeader` | `X-Authagonal-Bff` | |
| `PostLogoutRedirectUri` | keiner | |
| `WsTicketsEnabled`, `WsTicketLifetime`, `TicketExchangeParams` | aus, 30 s, leer | Siehe [WebSocket-Authentifizierung](websocket-auth). |
| `TokenEndpointEnabled`, `TokenEndpointResources`, `TokenEndpointExchangeParams` | aus, leer, leer | Aktivieren ohne Ressourcen führt beim Start zu einem Fehler. |
| `Upstreams`, `ExchangeRoutes`, `AllowAnonymousProxyRequests` | leer, leer, `false` | Siehe [Der Proxy](#the-proxy). |
| `TenantQueryParam` | keiner | Mandantenfähiger Modus, siehe unten. |

Von `SessionMode` ist nur `Store` implementiert; `Stateless` ist reserviert und führt beim Start zu einem Fehler.

Das Node-Paket nimmt die camelCase-Entsprechungen von `authority`, `clientId`, `clientSecret`, `scope`, `basePath`, `callbackPath`, `cookieName`, `refreshThresholdSeconds`, `returnUrlAllowlist`, `postLogoutRedirectUri`, `antiForgeryHeader`, `sessionLifetimeSeconds`, `upstreams` und `tenantQueryParam` entgegen, dazu `cookieSecret`, `sessionStore`, `cookieProtector`, `tenantResolver` und `clientIp`. Die Endpunkte für WebSocket-Tickets und Tokens hat es nicht.

## Sitzungen und der Betrieb mehrerer Instanzen {#sessions-and-running-more-than-one-instance}

Sitzungen werden über `IBffSessionStore` gespeichert. Standard ist `IDistributedCache`, der im Arbeitsspeicher liegt, sofern Sie nicht **vor** `AddAuthagonalBff` einen echten Cache (zum Beispiel Redis) registrieren.

Ein gemeinsamer Cache allein reicht nicht. Die Absicherung, dass nur ein Erneuerungsvorgang gleichzeitig läuft, gilt pro Prozess, während die Sitzung und ihr rotierendes Refresh Token im gemeinsamen Cache liegen. Zwei Replikate können dieselbe Sitzung lesen, beide feststellen, dass sie erneuert werden muss, und beide dasselbe Refresh Token einlösen. Der Auth-Host wertet die zweite Einlösung als Replay eines gestohlenen Tokens und widerruft die gesamte Grant-Familie, wodurch der Benutzer überall abgemeldet wird. Stellen Sie auf einem von zwei Wegen eine Sperre über Replikate hinweg bereit:

- **Registrieren Sie einen `ILeaseProvider`** (beliebiges Backend). Die Provider für Azure, AWS und SQL liefern einen über `AddAuthagonalClustering` mit. Siehe [Skalierung](scaling).
- **Implementieren Sie `IBffRefreshLockStore` in Ihrem Sitzungsspeicher** (`TryAcquireRefreshLockAsync(sessionId, ttl)` und `ReleaseRefreshLockAsync`). Das ist ein bedingter Schreibvorgang mit TTL, zum Beispiel `SET NX PX` bei Redis. Der Standardspeicher kann das nicht anbieten, weil `IDistributedCache` kein Setzen-falls-nicht-vorhanden kennt. Der Node-Sitzungsspeicher bietet die Entsprechungen `acquireRefreshLock` / `releaseRefreshLock`.

Fehlt beides, verlässt sich die Bereitstellung auf `Auth:RefreshTokenReuseGraceSeconds` des Auth-Hosts, das im Server-Host standardmäßig 0 ist (strikt). Das BFF protokolliert beim Start eine Warnung, wenn der Sitzungsspeicher geteilt aussieht und keine Sperre vorhanden ist.

Ein eigener `IBffSessionStore` muss das Argument `tenantKey` in `RemoveBySidAsync` und `RemoveBySubjectAsync` berücksichtigen. Die weiteren Erweiterungspunkte sind `ICookieProtector` (Standard: ASP.NET Data Protection) und `ITokenClient`.

## Viele Mandanten aus einem BFF {#many-tenants-from-one-bff}

Setzen Sie `TenantQueryParam` (zum Beispiel `"slug"`) und registrieren Sie einen `IBffTenantResolver`. `/bff/login?slug=acme` ermittelt die `BffTenantConfig` des Mandanten (Authority, Client-ID, Secret, Scope), der Schlüssel wird in der Sitzung abgelegt, damit spätere Requests ihn erneut auflösen, und der Back-Channel Logout ermittelt den Mandanten anhand des `iss` im Token über `ResolveByIssuerAsync`. Ist `TenantQueryParam` nicht gesetzt, arbeitet das BFF mit nur einem Mandanten und verwendet die statischen Optionen.

## Ausgetauschte Tokens für einen anderen Origin {#exchanged-tokens-for-another-origin}

Das Cookie-Modell kann keinen Resource Server auf einem anderen Origin erreichen (etwa eine iframe-App, die die SPA einbettet). `TokenEndpointEnabled` fügt `GET /bff/token?resource=<audience>` hinzu, das `{ accessToken, expiresInSeconds }` für ein **ausgetauschtes** Token nach RFC 8693 zurückgibt: adressiert an genau eine Ressource aus `TokenEndpointResources` (alles andere ergibt eine 400 `resource_not_allowed`), gebunden an etwaige Werte aus `TokenEndpointExchangeParams` in der Query und kurzlebig. Der Browser erhält nie das primäre Token der Sitzung und sollte das ausgetauschte nur im Arbeitsspeicher halten. Der Client des Mandanten benötigt den Token-Exchange-Grant und muss die Ressourcen als seine Audiences deklarieren. Ein abgelehnter Austausch ergibt eine 403.
