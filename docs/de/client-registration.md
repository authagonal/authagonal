---
layout: default
title: Dynamische Client-Registrierung
locale: de
---

# Dynamische Client-Registrierung

Authagonal implementiert die **OAuth 2.0 Dynamic Client Registration** ([RFC 7591](https://datatracker.ietf.org/doc/html/rfc7591)), mit der sich Client-Anwendungen zur Laufzeit selbst registrieren können, ohne dass ein Administrator eingreifen muss.

## Den Endpunkt aktivieren {#enabling-the-endpoint}

Die dynamische Registrierung ist **standardmäßig deaktiviert**. Aktivieren Sie sie in der Konfiguration:

```json
{
  "Auth": {
    "DynamicClientRegistrationEnabled": true
  }
}
```

Oder setzen Sie `Auth__DynamicClientRegistrationEnabled=true` als Umgebungsvariable. Ein mandantenfähiger Host kann die Einstellung pro Mandant über `ITenantContext.DynamicClientRegistrationEnabled` überschreiben: Die Angabe des Mandanten hat Vorrang, und bei `null` gilt die hostweite Option.

Ist die Registrierung aktiviert, kündigt das Discovery-Dokument den Endpunkt an:

```
GET /.well-known/openid-configuration
```
```json
{
  "registration_endpoint": "https://auth.example.com/connect/register"
}
```

## Einen Client registrieren {#registering-a-client}

```
POST /connect/register
Content-Type: application/json

{
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "token_endpoint_auth_method": "client_secret_basic",
  "scope": "openid profile email offline_access",
  "audiences": ["https://api.myapp.example.com"],
  "allowed_cors_origins": ["https://myapp.example.com"],
  "backchannel_logout_uri": "https://myapp.example.com/oidc/backchannel",
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

### Antwort {#response}

```
HTTP/1.1 201 Created
Content-Type: application/json

{
  "client_id": "a1b2c3d4e5f6...",
  "client_secret": "xkCd2_base64url...",
  "client_id_issued_at": 1745000000,
  "client_secret_expires_at": 0,
  "client_name": "My App",
  "redirect_uris": ["https://myapp.example.com/callback"],
  "post_logout_redirect_uris": ["https://myapp.example.com/"],
  "grant_types": ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "scope": "openid profile email offline_access",
  "token_endpoint_auth_method": "client_secret_basic"
}
```

Das `client_secret` wird **einmalig** zurückgegeben und lässt sich später nicht mehr abrufen. Bewahren Sie es sicher auf. Die Antwort wird mit `Cache-Control: no-store` gesendet. `client_id` besteht aus 32 hexadezimalen Kleinbuchstaben, und `client_secret_expires_at` ist immer `0` (Secrets laufen nicht ab). Öffentliche Clients (`none`) und `private_key_jwt`-Clients erhalten in der Antwort kein `client_secret`. Die Antwort enthält nur die gezeigten Felder: `audiences`, `jwks`, `jwks_uri`, `allowed_cors_origins` und die Logout-Felder werden gespeichert, aber nicht zurückgegeben.

## Request-Parameter {#request-parameters}

| Parameter | Pflicht | Hinweise |
|---|---|---|
| `client_name` | nein | Wird er weggelassen, gilt die generierte `client_id` als Name |
| `redirect_uris` | bedingt | Pflicht, wenn `grant_types` den Wert `authorization_code` enthält. Müssen absolute URIs sein; die Schemata `javascript:`/`data:`/`vbscript:`/`file:` werden abgelehnt (native benutzerdefinierte Schemata für mobile Deep Links sind erlaubt). Ein Fragment wird abgelehnt (RFC 6749 §3.1.2), und unverschlüsseltes `http` wird nur für Loopback-Hosts akzeptiert (RFC 8252 §7.3). Höchstens 20 Einträge mit jeweils höchstens 2048 Zeichen. |
| `post_logout_redirect_uris` | nein | Gültige Weiterleitungsziele nach dem Logout. Dieselben Grenzen von 20 Einträgen / 2048 Zeichen wie bei `redirect_uris`. |
| `grant_types` | nein | Standard ist `["authorization_code"]`. **Registrierbar sind nur `authorization_code` und `refresh_token`**: `client_credentials`, `implicit`, Device und jeder andere Grant-Typ werden mit `invalid_client_metadata` abgelehnt, sodass eine offene Registrierung nie einen Machine-to-Machine-Client erzeugen kann. `refresh_token` wird automatisch ergänzt, wenn `offline_access` angefordert wird. |
| `token_endpoint_auth_method` | nein | `client_secret_basic` (Standard), `client_secret_post`, `private_key_jwt` oder `none` für öffentliche Clients. Jeder andere Wert wird mit `invalid_client_metadata` abgelehnt. |
| `jwks` / `jwks_uri` | bei `private_key_jwt` | Die öffentlichen Schlüssel des Clients. Für `private_key_jwt` ist eines der beiden Pflicht (sonst `invalid_client_metadata`); `jwks_uri` muss die Prüfung ausgehender URLs bestehen (eine externe Adresse). Ein `private_key_jwt`-Client erhält kein Secret. |
| `scope` | nein | Durch Leerzeichen getrennte Scopes. Registrierbar sind nur die fünf integrierten OIDC-Scopes (`openid`, `profile`, `email`, `phone`, `offline_access`) sowie alles, was `Auth:DynamicClientRegistrationScopes` nennt: Die bloße Existenz im Scope-Speicher reicht **nicht** aus (siehe [Scopes](scopes)). Rollengebundene Scopes und der administrative Scope (`AdminApi:Scope`, Standard `authagonal-admin`) können nie registriert werden. |
| `audiences` | nein | JWT-`aud`-Werte, die Access Tokens hinzugefügt werden. Höchstens 20 Einträge mit höchstens 512 Zeichen, jeweils eine absolute URI ohne Fragment; ein ungültiger Wert ergibt `invalid_client_metadata`. |
| `allowed_cors_origins` | nein | Jeder Eintrag muss ein gültiger Origin sein (sonst `invalid_client_metadata`), der Wert wird aber **nicht so gespeichert, wie er gesendet wurde**: Die erlaubten Origins des Clients werden aus den Origins seiner eigenen `https`-`redirect_uris` abgeleitet. Ein Registrant erreicht also nur Origins, für die er bereits eine Redirect-URI nachgewiesen hat. |
| `backchannel_logout_uri` | nein | Aktiviert den [Back-Channel Logout](index#key-features) |
| `frontchannel_logout_uri` | nein | Aktiviert den [Front-Channel Logout](front-channel-logout) |
| `frontchannel_logout_session_required` | nein | Standard ist `true`; bei `true` trägt die Logout-URL die Parameter `iss` und `sid` |

## Standardwerte und Invarianten {#defaults--invariants}

- **PKCE erforderlich**: `RequirePkce` ist für dynamisch registrierte Clients immer `true`.
- **Zustimmung erforderlich**: `RequireConsent` ist immer `true`, sodass ein Benutzer bei einem selbst registrierten Client den Zustimmungsbildschirm sieht, selbst wenn ein statisch per Seed-Konfiguration angelegter Client ihn überspringen würde.
- **Öffentliche Clients**: `token_endpoint_auth_method: "none"` erzeugt einen Client ohne Secret. PKCE bleibt trotzdem Pflicht.
- **Offline-Zugriff**: Die Anforderung des Scopes `offline_access` ergänzt `grant_types` implizit um `refresh_token`.

## Fehlerantworten {#error-responses}

| HTTP | `error` | Ursache |
|---|---|---|
| `400` | `invalid_redirect_uri` | Eine der `redirect_uris` ist keine gültige absolute URI, verwendet ein Script-, Data- oder File-Pseudoschema, trägt ein Fragment, ist unverschlüsseltes `http` zu einem Nicht-Loopback-Host oder ist (in einer der beiden URI-Listen) länger als 2048 Zeichen |
| `400` | `invalid_client_metadata` | Ein nicht registrierbarer Grant-Typ wurde angefordert, `redirect_uris` fehlt bei einem Grant-Typ, der sie erfordert, `token_endpoint_auth_method` wird nicht unterstützt, `private_key_jwt` hat kein `jwks`/`jwks_uri` (oder eine unsichere `jwks_uri`), `audiences` ist ungültig, ein Eintrag in `allowed_cors_origins` ist kein Origin, oder eine Logout-URI ist keine externe Adresse |
| `400` | `invalid_scope` | Ein angeforderter Scope ist weder integriert noch registriert |
| `400` | `invalid_client_metadata` | Mehr als 20 `redirect_uris` / `post_logout_redirect_uris` |
| `403` | `invalid_scope` | Ein angeforderter Scope ist nicht registrierbar: nicht in `Auth:DynamicClientRegistrationScopes` oder rollengebunden |
| `403` | `invalid_scope` | Der administrative Scope wurde angefordert; er kann nie über die Registrierung gewährt werden |
| `403` | `invalid_scope` | Ein registrierter `IClientScopeGuard` hat einen angeforderten Scope abgelehnt (ihm wird der anonyme Aufrufer übergeben) |
| `403` | `not_supported` | Die dynamische Client-Registrierung ist nicht aktiviert |
| `429` | `rate_limited` | Zu viele Registrierungen von dieser IP (10 pro Stunde) |

## Sicherheitsaspekte {#security-considerations}

Der Registrierungsendpunkt ist **nicht authentifiziert**, aber konstruktionsbedingt eingeschränkt:

- **Ratenbegrenzt**: 10 Registrierungen pro Quelladresse innerhalb einer gleitenden Stunde (`429 rate_limited`), damit der Client-Speicher nicht geflutet werden kann. Maßgeblich ist die Adresse, die der Aufrufer nicht selbst wählen kann (dem weitergeleiteten Wert wird nicht blind vertraut).
- **Grant-Typen eingeschränkt**: nur `authorization_code` + `refresh_token`; ein registrierter Client erfordert immer einen vom Benutzer vermittelten Ablauf und kann nie als Machine-to-Machine-Client auftreten.
- **Scopes per Allowlist, nicht geerbt**: Ein Registrant darf die fünf integrierten OIDC-Scopes deklarieren und sonst nichts, es sei denn, ein Betreiber führt einen Scope in `Auth:DynamicClientRegistrationScopes` auf. Existenz im Scope-Speicher ist keine Berechtigung: Ein Scope existiert, weil irgendein Client ihn braucht, nicht weil jeder anonyme Registrant ihn beanspruchen darf.
- **Admin-Scope reserviert**: Der Scope `authagonal-admin` (oder der in `AdminApi:Scope` gesetzte Wert) wird abgelehnt, sodass die Registrierung nie einen Client hervorbringen kann, der die [Admin-API](admin-api) erreicht.
- **Logout-URIs validiert**: `backchannel_logout_uri` und `frontchannel_logout_uri` werden vom Server aufgerufen und müssen deshalb externe http(s)-Endpunkte sein: Loopback, RFC1918, Link-Local (einschließlich der Metadatenadresse der Cloud) und Hosts unter `.internal`/`.local` werden abgelehnt.
- **Begrenzte Datensätze**: höchstens 20 Redirect-URIs mit jeweils höchstens 2048 Zeichen, sodass eine einzelne Registrierung den Client-Speicher nicht aufblähen kann.
- **CORS-Origins abgeleitet, nicht übernommen**: Die gespeicherten Origins stammen aus den eigenen `https`-Redirect-URIs des Clients, nie aus dem Request-Body.
- **PKCE immer erforderlich** und **Zustimmung immer erforderlich** bei registrierten Clients.

Was **nicht** eingeschränkt wird, sofern der Registrant es nicht ausdrücklich angibt, ist die Audience. RFC 7591 sieht dafür kein Feld vor, daher lässt eine gewöhnliche Registrierung `audiences` (eine Authagonal-Erweiterung) ganz weg: Der Client wurde nie gefragt, seine Liste ist "nicht gesetzt", und er darf am Autorisierungsendpunkt jede absolute URI als `resource` nennen und ein Token erhalten, das diesen Wert als `aud` trägt. Das ist beabsichtigt (die MCP-Autorisierungsspezifikation verlangt von Clients, den MCP-Server als Ressource zu nennen, und ein MCP-Client ist ein DCR-Client), und es macht den Resource Server dafür verantwortlich, anhand von `scope` zu autorisieren statt anhand von `iss` + `aud` + `sub`. Wer `audiences` **mitsendet**, auch als leere Liste, gibt damit eine Antwort und legt den Client darauf fest: Eine nicht leere Liste ist die Allowlist für `resource`, und ein explizites `[]` bedeutet, dass der Client überhaupt keine Ressource nennen darf. Token Exchange ist die Ausnahme: Dort führt ein nicht gesetztes `Audiences` zur sofortigen Ablehnung, sodass ein registrierter Client ein ausgetauschtes Token nirgendwohin richten kann. Siehe [Audiences und Resource Indicators](configuration#audiences-and-resource-indicators-rfc-8707).

Für strengere Zugangskontrollen (Initial Access Tokens, mTLS, Software Statements) schalten Sie eigene Middleware oder einen `IAuthHook` vor den Endpunkt. Erwägen Sie, die dynamische Registrierung ganz zu deaktivieren und Clients über die Admin-API zu verwalten, wenn in Ihrer Umgebung keine Selbstregistrierung benötigt wird.
