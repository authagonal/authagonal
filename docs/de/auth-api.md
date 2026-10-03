---
layout: default
title: Auth-API
locale: de
---

# Auth-API

Diese Endpunkte treiben die Login-SPA an. Sie verwenden Cookie-Authentifizierung (`SameSite=Lax`, `HttpOnly`).

Wenn Sie eine eigene Login-UI bauen, sind das die Endpunkte, gegen die Sie implementieren müssen.

## Endpunkte {#endpoints}

### Anmelden {#login}

```
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**Erfolg (200):** Setzt ein Auth-Cookie und liefert:

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe",
  "mfaAvailable": false
}
```

`mfaAvailable` ist `true`, wenn die `MfaPolicy` des Clients `Enabled` ist, der Benutzer MFA aber noch nicht eingerichtet hat (die UI kann die Einrichtung anbieten); in diesem Fall wird zusätzlich ein Feld `clientId` mitgeliefert.

**MFA erforderlich (200):** Hat der Benutzer MFA eingerichtet, wird er **immer** zur MFA aufgefordert, unabhängig von der `MfaPolicy` des anfragenden Clients (MFA ist eine Eigenschaft des Benutzers bzw. der Sitzung, nicht des Clients):

```json
{
  "mfaRequired": true,
  "challengeId": "a1b2c3...",
  "methods": ["totp", "webauthn", "recoverycode"],
  "webAuthn": { /* PublicKeyCredentialRequestOptions */ }
}
```

Der Client sollte auf eine Seite für die MFA-Abfrage weiterleiten und `POST /api/auth/mfa/verify` aufrufen.

**MFA-Einrichtung erforderlich (200):** Wenn die `MfaPolicy` `Required` ist und der Benutzer keine MFA eingerichtet hat:

```json
{
  "mfaSetupRequired": true,
  "setupToken": "abc123..."
}
```

Der Client sollte auf eine Seite zur MFA-Einrichtung weiterleiten. Das Setup-Token authentifiziert den Benutzer über den Header `X-MFA-Setup-Token` gegenüber den Endpunkten zur MFA-Einrichtung.

**Fehlerantworten:**

| `error` | Status | Beschreibung |
|---|---|---|
| `invalid_credentials` | 401 | Falsche E-Mail-Adresse oder falsches Passwort. Bei unbekannten E-Mail-Adressen bewusst identisch (Schutz vor Aufzählung). |
| `locked_out` | 423 | Zu viele fehlgeschlagene Versuche. `retryAfter` (Sekunden) ist enthalten. |
| `account_disabled` | 403 | Das Konto ist deaktiviert (wird nur nach einem korrekten Passwort gemeldet) |
| `email_not_confirmed` | 403 | Die E-Mail-Adresse ist noch nicht verifiziert (wird nur nach einem korrekten Passwort gemeldet) |
| `sso_required` | 409 | Die Domain erfordert SSO. `redirectUrl` verweist auf die SSO-Anmeldung. |
| `captcha_failed` | 400 | Die Turnstile-Verifizierung ist fehlgeschlagen (nur wenn Turnstile konfiguriert ist; Anfragen benötigen dann ein Feld `turnstileToken`) |
| `email_required` | 400 | Das E-Mail-Feld ist leer |
| `password_required` | 400 | Das Passwortfeld ist leer |

### Registrieren {#register}

```
POST /api/auth/register
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Legt ein neues Benutzerkonto an und sendet eine Verifizierungs-E-Mail. Liefert `201 { "success": true, "userId": "..." }`. Optionale Felder: `locale` (BCP-47-Tag, das am Benutzer gespeichert wird) und `customAttributes` (eine String-Map).

Die Registrierung ist bewusst **neutral gegenüber Aufzählung**: Ist die E-Mail-Adresse bereits registriert, ist die Antwort dasselbe neutrale `201` (mit einer Wegwerf-`userId`), und der tatsächliche Inhaber erhält stattdessen per E-Mail einen Hinweis zur Anmeldung bzw. zum Zurücksetzen. Die Registrierung ist außerdem pro IP ratenbegrenzt, bei Überschreitung kommt `429 rate_limited` (Zeitfenster und Obergrenze konfigurierbar über `Auth:MaxRegistrationsPerIp` / `Auth:RegistrationWindowMinutes`).

### E-Mail-Adresse bestätigen {#confirm-email}

```
GET  /api/auth/confirm-email?token={token}
POST /api/auth/confirm-email?token={token}
```

Bestätigt die E-Mail-Adresse des Benutzers mit dem Token aus der Verifizierungs-E-Mail. `GET` ist der anklickbare Link in der E-Mail; er leitet auf `/login?email_confirmed=1` weiter (zuzüglich eines Parameters `continue_client`, wenn die Registrierung aus einem OAuth-Ablauf stammt). `POST` ist der programmatische Weg und liefert JSON (das Token kann auch in einem JSON-Body als `{ "token": "..." }` übergeben werden); die Antwort enthält einen optionalen `appLink` (Ziel für „Weiter zur App“).

### Provider {#providers}

```
GET /api/auth/providers
```

Liefert die Liste der konfigurierten externen Identity Provider (zum Rendern von SSO-Schaltflächen):

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "type": "oidc", "iconUrl": null, "loginUrl": "/oidc/google/login" }
  ],
  "turnstileSiteKey": null
}
```

Verbindungen mit konfigurierten `AllowedDomains` werden **ausgeschlossen**: Diese werden statt über eine Schaltfläche über die E-Mail-Adresse zuerst via `/api/auth/sso-check` erreicht. `turnstileSiteKey` ist gesetzt, wenn Cloudflare Turnstile konfiguriert ist (die Login-UI muss dann bei Anmelde-, Registrierungs- und Passwortanfragen ein `turnstileToken` senden).

### Abmelden {#logout}

```
POST /api/auth/logout
```

Beendet die Sitzung des Aufrufers auf dieselbe Weise wie `/connect/endsession`: Back-Channel-Logout-Tokens werden an die Relying Parties gesendet, die eine URI registriert haben, die für diese Sitzung ausgestellten Grants werden widerrufen, und das Auth-Cookie wird gelöscht. Erfordert Cookie-Authentifizierung und eine Anfrage vom selben Origin. Liefert `200`:

```json
{
  "success": true,
  "frontchannel_logout_uris": ["https://myapp.example.com/oidc/frontchannel"]
}
```

`frontchannel_logout_uris` listet die Front-Channel-Logout-URLs auf, die der Aufrufer laden sollte (in versteckten iframes), um die Abmeldung im Browser abzuschließen; die Liste ist leer, wenn kein Client eine registriert hat. Siehe [Front-Channel-Logout](front-channel-logout).

### Passwort vergessen {#forgot-password}

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

Liefert immer `200` (Schutz vor Aufzählung). Existiert der Benutzer, wird eine E-Mail zum Zurücksetzen gesendet.

### Passwort zurücksetzen {#reset-password}

```
POST /api/auth/reset-password
Content-Type: application/json

{
  "token": "base64-encoded-token",
  "newPassword": "NewSecurePass1!"
}
```

| `error` | Beschreibung |
|---|---|
| `weak_password` | Erfüllt die Anforderungen an die Passwortstärke nicht |
| `invalid_token` | Das Token ist fehlerhaft |
| `token_expired` | Das Token ist abgelaufen (standardmäßig 60 Minuten gültig, konfigurierbar über `Auth:PasswordResetExpiryMinutes`) |

### Sitzung {#session}

```
GET /api/auth/session
```

Liefert Informationen zur aktuellen Sitzung, wenn der Aufrufer authentifiziert ist:

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

Liefert `401`, wenn er nicht authentifiziert ist.

### Apps {#apps}

```
GET /api/auth/apps
```

Liefert die Anwendungslinks des Mandanten für den Starter „Zurück zur App“ auf der Kontoseite: aktivierte Clients, die eine Home-URI haben (`initiateLoginUri` hat Vorrang vor `clientUri`). Jeder Eintrag ist `{ clientId, clientName, homeUri, logoUri, isDefault }`; genau eine App ist als Standard markiert (der gekennzeichnete Client oder der einzige Client mit einer Home-URI). Erfordert Cookie-Authentifizierung.

### Profil (Self-Service) {#profile-self-service}

```
GET   /api/auth/profile
PATCH /api/auth/profile
```

Der authentifizierte Benutzer liest bzw. aktualisiert seine eigenen, nicht sensiblen Profilfelder: `firstName`, `lastName`, `companyName`, `phone`, `locale`. Felder mit null bleiben unverändert; E-Mail-Adresse, Passwort, Rollen, Aktivstatus und Organisation sind hier **nicht** bearbeitbar. Beide liefern das Profil `{ email, emailConfirmed, firstName, lastName, companyName, phone, locale }`.

### Sitzungen (Self-Service) {#sessions-self-service}

```
GET    /api/auth/sessions
DELETE /api/auth/sessions/{sessionId}
POST   /api/auth/sessions/revoke-others
```

Listet die eigenen SSO-Sitzungen des authentifizierten Benutzers auf und beendet sie. Dafür sind serverseitige Sitzungen nötig, die Opt-in sind: Rufen Sie `AddAuthagonalServerSideSessions(configuration)` nach `AddAuthagonal` auf (Azure Table Storage, liest `Storage:ConnectionString` oder `Storage:TableServiceUri`), oder registrieren Sie Ihr eigenes `ITicketStore` und `IUserSessionRegistry`. Ohne Registry liefert `GET` eine leere Liste, `revoke-others` liefert `{ "revoked": 0 }`, und `DELETE` liefert `404 not_supported`. Die Routen `DELETE` und `POST` erfordern eine Anfrage vom selben Origin.

`GET` liefert die Sitzungen, die mit der jüngsten Aktivität zuerst:

```json
{
  "sessions": [
    {
      "sessionId": "...",
      "current": true,
      "createdAt": "2026-10-01T02:11:40+00:00",
      "lastSeenAt": "2026-10-04T05:30:12+00:00",
      "expiresAt": "2026-10-08T02:11:40+00:00",
      "ip": "203.0.113.7",
      "userAgent": "Mozilla/5.0 ..."
    }
  ]
}
```

`DELETE` beendet eine Sitzung und liefert `{ "revoked": 1 }` oder `404 session_not_found`. `POST /revoke-others` beendet jede Sitzung außer der des Aufrufers und liefert `{ "revoked": <count> }`. Beide benachrichtigen außerdem die Relying Parties jeder beendeten Sitzung (Back-Channel- und Front-Channel-Logout) und widerrufen die daran gebundenen Grants, sodass auf diesem Gerät gehaltene Refresh Tokens nicht mehr funktionieren. Die Kontoseite der Login-UI zeigt diese Liste an, wenn eine Registry registriert ist.

### SSO-Prüfung {#sso-check}

```
GET /api/auth/sso-check?email=user@acme.com
```

Prüft, ob die Domain der E-Mail-Adresse SSO erfordert:

```json
{
  "ssoRequired": true,
  "providerType": "saml",
  "connectionId": "acme-azure",
  "redirectUrl": "/saml/acme-azure/login"
}
```

Wenn SSO nicht erforderlich ist:

```json
{
  "ssoRequired": false
}
```

### Passwortrichtlinie {#password-policy}

```
GET /api/auth/password-policy
```

Liefert die Passwortanforderungen des Servers (konfiguriert über `PasswordPolicy` in den Einstellungen):

```json
{
  "rules": [
    { "rule": "minLength", "value": 8, "label": "At least 8 characters" },
    { "rule": "uppercase", "value": null, "label": "Uppercase letter" },
    { "rule": "lowercase", "value": null, "label": "Lowercase letter" },
    { "rule": "digit", "value": null, "label": "Number" },
    { "rule": "specialChar", "value": null, "label": "Special character" }
  ]
}
```

Die Standard-Login-UI ruft diesen Endpunkt auf der Seite zum Zurücksetzen des Passworts ab, um die Anforderungen dynamisch anzuzeigen.

## Standardanforderungen an Passwörter {#default-password-requirements}

Mit der Standardkonfiguration müssen Passwörter alle folgenden Anforderungen erfüllen:

- Mindestens 8 Zeichen
- Mindestens ein Großbuchstabe
- Mindestens ein Kleinbuchstabe
- Mindestens eine Ziffer
- Mindestens ein nicht alphanumerisches Zeichen
- Mindestens 2 verschiedene Zeichen

Diese lassen sich über den Konfigurationsabschnitt `PasswordPolicy` anpassen, siehe [Konfiguration](configuration).

## MFA-Endpunkte {#mfa-endpoints}

### MFA-Verifizierung {#mfa-verify}

```
POST /api/auth/mfa/verify
Content-Type: application/json

{
  "challengeId": "a1b2c3...",
  "method": "totp",
  "code": "123456"
}
```

Verifiziert eine MFA-Abfrage. Bei Erfolg wird das Auth-Cookie gesetzt, und die Benutzerinformationen werden zurückgegeben.

**Methoden:**

| `method` | Erforderliche Felder | Beschreibung |
|---|---|---|
| `totp` | `code` (6 Ziffern) | Zeitbasiertes Einmalpasswort aus einer Authenticator-App |
| `webauthn` | `assertion` (JSON-String) | WebAuthn-Assertion-Antwort von `navigator.credentials.get()` |
| `recovery` | `code` (`XXXX-XXXX`) | Einmaliger Wiederherstellungscode (wird bei Verwendung verbraucht) |

**Semantik von Wiederholungen:** Ein falscher Code verbraucht die Abfrage **nicht**; der Code wird zuerst geprüft, und die Abfrage wird erst bei Erfolg verbraucht, sodass der Benutzer es nach einer vertippten Ziffer mit derselben `challengeId` erneut versuchen kann (`401 invalid_code` / `assertion_failed`). Jede Abfrage toleriert **5 Fehlversuche**; der 5. Fehlschlag verbraucht sie und liefert `401 too_many_attempts`, was eine neue Anmeldung erzwingt (damit ist ein Brute-Force-Angriff auf TOTP auf 5 Versuche pro Abfrage begrenzt). Abfragen laufen außerdem ab (standardmäßig nach 5 Minuten, `Auth:MfaChallengeExpiryMinutes`); eine abgelaufene, unbekannte oder bereits verbrauchte `challengeId` liefert `invalid_challenge`. TOTP-Codes sind zusätzlich gegen Wiederverwendung geschützt: Ein Code aus einem bereits genutzten Zeitschritt wird abgelehnt.

### MFA-Status {#mfa-status}

```
GET /api/auth/mfa/status
```

Liefert die eingerichteten MFA-Methoden des Benutzers. Erfordert Cookie-Authentifizierung oder den Header `X-MFA-Setup-Token`.

```json
{
  "enabled": true,
  "offered": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

`offered` ist `false`, wenn die `MfaPolicy` jedes Clients `Disabled` ist; MFA ist für den Mandanten dann ausgeschaltet, und die Einrichtungs-UI kann sich ausblenden. Einträge für Wiederherstellungscodes tragen zusätzlich `isConsumed`.

### TOTP-Einrichtung {#totp-setup}

```
POST /api/auth/mfa/totp/setup
→ { "setupToken": "...", "qrCodeDataUri": "data:image/png;base64,...", "manualKey": "BASE32..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
→ { "success": true }
```

### Einrichtung von WebAuthn / Passkeys {#webauthn--passkey-setup}

```
POST /api/auth/mfa/webauthn/setup
→ { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
→ { "success": true, "credentialId": "..." }
```

Die Einrichtung eines Passkeys erfordert **zuerst einen bestätigten TOTP-Berechtigungsnachweis** (`400 totp_required_first`). Passkeys sind eine gerätebezogene Komfortfunktion, die auf einem portablen Basisfaktor aufsetzt; ein Konto kann daher nie nur noch einen Passkey haben und an ein Gerät gebunden sein. Benutzer, deren E-Mail-Domain über SSO geleitet wird, können keinen lokalen Passkey einrichten (`400 sso_managed`), da dieser den IdP des Mandanten umgehen würde. Eine Credential-ID, die bereits für **irgendein** Konto registriert ist, auch für das des einrichtenden Benutzers selbst, wird mit `409 credential_already_registered` abgelehnt, denn ein Duplikat würde den Signaturzähler dieses Berechtigungsnachweises neu starten und einen Suchindexeintrag zwischen zwei Zeilen teilen.

### Wiederherstellungscodes {#recovery-codes}

```
POST /api/auth/mfa/recovery/generate
→ { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

Erzeugt 10 einmalige Wiederherstellungscodes. Erfordert, dass mindestens eine primäre Methode (TOTP oder WebAuthn) eingerichtet ist. Eine erneute Erzeugung ersetzt alle bestehenden Wiederherstellungscodes.

### MFA-Berechtigungsnachweis entfernen {#remove-mfa-credential}

```
DELETE /api/auth/mfa/credentials/{credentialId}
→ { "success": true }
```

Entfernt einen bestimmten MFA-Berechtigungsnachweis. Wird die letzte primäre Methode entfernt, wird MFA für den Benutzer deaktiviert. Erfordert eine echte Cookie-Sitzung; ein Setup-Token wird mit `403 session_required` abgelehnt (Setup-Tokens dienen nur dazu, einen ersten Faktor hinzuzufügen, niemals dazu, MFA abzuschwächen).

### Passwortlose Anmeldung per Passkey {#passwordless-passkey-login}

```
POST /api/auth/mfa/passwordless/begin
→ { "challengeId": "...", "options": { /* PublicKeyCredentialRequestOptions */ } }

POST /api/auth/mfa/passwordless/complete
{ "challengeId": "...", "assertion": "..." }
→ { "userId": "...", "email": "...", "name": "..." }
```

Anmeldung mit auffindbaren Berechtigungsnachweisen (residente Passkeys) ohne vorherigen Benutzerkontext: `begin` stellt eine Assertion-Abfrage mit leerer `allowCredentials`-Liste aus, und `complete` ermittelt den Benutzer **anhand** des gewählten Passkeys, prüft die Assertion und meldet ihn an (die Sitzung trägt die MFA-Markierung, denn ein Passkey ist eine phishingresistente starke Authentifizierung). Weil vor dem Ablauf kein Benutzer identifiziert wurde, macht WebAuthn §7.2 Schritt 6 das User Handle des Authenticators hier verpflichtend: Eine Assertion ohne User Handle wird mit `401 user_handle_required` abgelehnt, und eine, die ein anderes Konto als den Besitzer des Berechtigungsnachweises nennt, mit `401 credential_not_found`. Wird die E-Mail-Domain des ermittelten Benutzers über SSO geleitet, wird die Anmeldung mit `409 sso_required` + `redirectUrl` abgelehnt, damit ein lokaler Passkey einen erzwungenen IdP nicht umgehen kann.

## Geräteautorisierung (RFC 8628) {#device-authorization-rfc-8628}

### Gerätecode anfordern {#request-device-code}

```
POST /connect/deviceauthorization
Content-Type: application/x-www-form-urlencoded

client_id=my-cli&scope=openid+profile
```

Liefert einen Gerätecode, einen Benutzercode und eine Verifizierungs-URI:

```json
{
  "device_code": "abc123...",
  "user_code": "ABCD-EFGH",
  "verification_uri": "https://auth.example.com/device",
  "verification_uri_complete": "https://auth.example.com/device?user_code=ABCD-EFGH",
  "expires_in": 300,
  "interval": 5
}
```

`expires_in` stammt aus dem `DeviceCodeLifetimeSeconds` des Clients (Standard 300). Das Gerät zeigt dem Benutzer die `verification_uri` und den `user_code` an und fragt dann den Token-Endpunkt mit dem `device_code` ab, nicht häufiger als im Abstand von `interval` Sekunden, sonst antwortet der Token-Endpunkt mit `slow_down` (RFC 8628 §3.5). Solange der Benutzer noch nicht zugestimmt hat, liefert der Token-Endpunkt `authorization_pending`. Der Benutzer ruft die Verifizierungs-URI auf, meldet sich an und gibt den Benutzercode ein, um zuzustimmen.

### Die Anfrage vor der Zustimmung anzeigen {#show-the-request-before-approving}

```
GET /api/auth/device/info?user_code=ABCD-EFGH
```

Erfordert Cookie-Authentifizierung. Beschreibt, was der Code gewähren würde, sodass der Zustimmungsbildschirm dem Benutzer vor der Zustimmung zeigen kann, welche Anwendung anfragt (ein von einem Angreifer gestarteter Device Flow, dem bei einer undurchsichtigen Abfrage zugestimmt wird, ist das Muster der erschlichenen Zustimmung, vor dem RFC 8628 §5.4 warnt):

```json
{
  "clientId": "my-cli",
  "clientName": "My CLI",
  "clientUri": "https://example.com",
  "logoUri": null,
  "scopes": ["openid", "profile"]
}
```

`scopes` ist das, was tatsächlich gewährt würde, nach der benutzerbezogenen Rollenprüfung für rollenbeschränkte Scopes, nicht die rohe Anfrage. Fehler: `401 not_authenticated`, `400 user_code_required`, `400 invalid_user_code` (unbekannt, verbraucht oder abgelaufen), `400 expired`. Der Endpunkt teilt sich das Kontingent der Ratenbegrenzung mit der Zustimmung (siehe unten).

### Gerät zulassen {#approve-device}

```
POST /api/auth/device/approve
Content-Type: application/x-www-form-urlencoded

user_code=ABCD-EFGH&scopes=openid+profile
```

Erfordert Cookie-Authentifizierung und eine Anfrage vom selben Origin. `scopes` ist optional (durch Leerzeichen getrennt): Es kann das, worauf der Benutzer Anspruch hat, nur einschränken, nie erweitern, und wird es weggelassen, wird alles gewährt, worauf Anspruch besteht. Lässt den Gerätecode für den aktuellen Benutzer zu und liefert `200 { "approved": true }`. Das Gerät kann den Gerätecode dann am Token-Endpunkt mit dem Grant-Typ `urn:ietf:params:oauth:grant-type:device_code` gegen Tokens eintauschen.

Der übermittelte Code wird vor der Suche gemäß RFC 8628 §6.1 normalisiert: Er wird in Großbuchstaben umgewandelt, und jedes Zeichen außerhalb des 31 Zeichen umfassenden Code-Alphabets wird verworfen. `ABCD-EFGH`, `abcd-efgh`, `ABCDEFGH`, `ABCD EFGH` und eine Kopie, bei der der Bindestrich zu einem Geviertstrich geworden ist, sind alle derselbe Code. Der Bindestrich dient nur dazu, den Code leichter vorlesen zu können.

| Status | `error` | Bedeutung |
|---|---|---|
| 400 | `user_code_required`, `invalid_user_code`, `expired` | Wie bei `info` |
| 400 | `invalid_scope` | `scopes` wurde angegeben, enthält aber keinen Scope, auf den der Benutzer Anspruch hat |
| 403 | `access_denied` | Der Benutzer hat auf keinen der angeforderten Scopes Anspruch (`Scope.AllowedRoles`) |
| 403 | `mfa_enrolment_required` | Die wirksame MFA-Richtlinie des Clients ist `Required`, und der Benutzer hat keinen zweiten Faktor; richten Sie ihn ein und stimmen Sie dann erneut zu |

Die Eingabe ist auf zehn Versuche pro Minute und Subjekt begrenzt (RFC 8628 §5.1), gemeinsam für `info`, `approve` und `deny`; der elfte liefert `429`. Dieser Zähler gilt mit dem standardmäßigen prozessinternen Ratenbegrenzer pro Knoten; ein Deployment mit mehreren Replikaten sollte die Begrenzung daher zusätzlich am Edge durchsetzen.

### Gerät ablehnen {#deny-device}

```
POST /api/auth/device/deny
Content-Type: application/x-www-form-urlencoded

user_code=ABCD-EFGH
```

Erfordert Cookie-Authentifizierung und eine Anfrage vom selben Origin. Hält die Ablehnung des Benutzers fest und liefert `200 { "success": true }`. Die nächste Abfrage des Token-Endpunkts durch das Gerät erhält bis zum Ablauf des Codes `access_denied` (RFC 8628 §3.5) statt `authorization_pending`. Dieselben Fehler und dasselbe Kontingent der Ratenbegrenzung wie bei `info`.

## Token-Introspection (RFC 7662) {#token-introspection-rfc-7662}

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

token=eyJhbGci...
```

Oder mit formularkodierten Anmeldedaten:

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded

token=eyJhbGci...&client_id=my-app&client_secret=secret
```

Liefert die Metadaten des Tokens:

```json
{
  "active": true,
  "sub": "user-id",
  "client_id": "my-app",
  "scope": "openid profile",
  "iss": "https://auth.example.com",
  "exp": 1234567890,
  "iat": 1234567890,
  "token_type": "Bearer"
}
```

Inaktive oder ungültige Tokens liefern `{ "active": false }`. Unterstützt werden sowohl JWT-Access-Tokens als auch opake Refresh Tokens.

## Endpunkte für die Zustimmung {#consent-endpoints}

### Informationen zur Zustimmung {#consent-info}

```
GET /consent/info?client_id=my-app
```

Erfordert Cookie-Authentifizierung. Liefert die Details des Clients und die angeforderten Scopes für die Zustimmungsseite. Die Scopes werden nicht aus dem Query-String übernommen: Sie sind das Angebot, das der Autorisierungsendpunkt für diesen Benutzer und Client festgehalten hat (nach der Filterung nach Rollenberechtigung), sodass ein manipulierter Link den Namen eines vertrauenswürdigen Clients nicht über eine vom Aufrufer gewählte Berechtigungsliste setzen kann.

```json
{
  "clientId": "my-app",
  "clientName": "My Application",
  "description": null,
  "clientUri": null,
  "logoUri": null,
  "scopes": ["openid", "profile", "email"],
  "scopeDetails": [
    { "name": "openid", "displayName": null, "description": null, "emphasize": false, "required": false, "group": null },
    { "name": "profile", "displayName": null, "description": null, "emphasize": false, "required": false, "group": null },
    { "name": "email", "displayName": null, "description": null, "emphasize": false, "required": false, "group": null }
  ]
}
```

`scopeDetails` verläuft parallel zu `scopes` (gleiche Reihenfolge, ein Eintrag pro Scope), sodass eine Login-App, die nur `scopes` liest, weiter funktioniert. Jeder Eintrag trägt die für diesen Scope registrierte Darstellung:

| Feld | Bedeutung |
|---|---|
| `name` | Der Name des Scopes, wie in `scopes`. |
| `displayName` | Der registrierte Anzeigename oder `null`, wenn der Scope nicht registriert ist. |
| `description` | Die registrierte Beschreibung oder `null`. |
| `emphasize` | `true`, wenn der Scope als folgenreich registriert ist, sodass der Bildschirm die Aufmerksamkeit darauf lenken darf. Standardmäßig `false`. |
| `required` | `true`, wenn der Scope als nicht ablehnbar registriert ist: Der Bildschirm zeigt ihn angehakt und gesperrt. Standardmäßig `false`. |
| `group` | Die Überschrift, unter der der Scope einzuordnen ist, oder `null`, um ihn für sich allein anzuzeigen. |

Ein nicht registrierter Scope ergibt `null` für `displayName`, `description` und `group` sowie `false` für die beiden Flags, und die Login-App greift auf ihre eigenen Formulierungen zurück. Wie Sie die Formulierungen registrieren, steht unter [Scopes](scopes).

Fehler:

| Status | Body | Wann |
|---|---|---|
| `401` | keiner | Kein angemeldeter Benutzer. |
| `404` | `{ "error": "client_not_found" }` | Unbekannte `client_id`. |
| `400` | `{ "error": "no_pending_consent_request" }` | Für diesen Benutzer und Client gibt es kein gültiges Zustimmungsangebot (es wurde keines festgehalten, oder es ist abgelaufen). |

### Zustimmung übermitteln {#submit-consent}

```
POST /consent
Content-Type: application/json

{
  "clientId": "my-app",
  "decision": "allow",
  "scopes": ["openid", "profile", "email"],
  "returnUrl": "/connect/authorize?..."
}
```

Hält die Zustimmungsentscheidung des Benutzers fest (erfordert Cookie-Authentifizierung) und liefert `{ "redirect": "..." }`, wohin die SPA navigieren soll. Bei einer Zustimmung werden die gewährten Scopes gespeichert (gefiltert auf die `AllowedScopes` des Clients, sodass ein manipulierter Body keine Scopes festhalten kann, die der Client nicht anfordern könnte), und die Weiterleitung führt zurück in den Autorisierungsablauf. Bei `"decision": "deny"` führt die Weiterleitung zur `redirect_uri` des Clients mit dem Fehler `access_denied`.

### Grants auflisten {#list-grants}

```
GET /consent/grants
```

Liefert alle Anwendungen, die der Benutzer autorisiert hat:

```json
[
  {
    "clientId": "my-app",
    "clientName": "My Application",
    "scopes": ["openid", "profile", "email"],
    "consentedAt": "2026-04-09T12:00:00Z"
  }
]
```

### Grant widerrufen {#revoke-grant}

```
DELETE /consent/grants/{clientId}
```

Widerruft die Zustimmung für eine bestimmte Anwendung. Der Benutzer wird bei seiner nächsten Anmeldung erneut um Zustimmung gebeten.

## Discovery und Signaturschlüssel (JWKS) {#discovery-and-signing-keys-jwks}

Beide sind öffentlich und anonym zugänglich. Ein Resource Server verwendet sie, um die Tokens zu validieren, die dieser Server ausstellt.

```
GET /.well-known/openid-configuration
GET /.well-known/oauth-authorization-server
GET /.well-known/openid-configuration/jwks
```

- Die beiden Metadatenpfade liefern dasselbe Discovery-Dokument; dessen `jwks_uri` ist `{issuer}/.well-known/openid-configuration/jwks`.
- Das JWKS listet jeden nicht abgelaufenen Signaturschlüssel auf (`kty`, `use`, `kid`, `alg` sowie `crv`/`x`/`y` für die EC-Schlüssel). Bei der Rotation wird der nächste Schlüssel Tage im Voraus veröffentlicht, sodass einer zwischengespeicherten Kopie nie der Schlüssel fehlt, mit dem ein Token signiert wurde.
- Antworten tragen `Cache-Control: public, max-age=3600`.
- Signiert wird ausschließlich mit ES256; die Discovery gibt `id_token_signing_alg_values_supported: ["ES256"]` an.
- Der Issuer stammt aus `ITenantContext`, die Schlüssel aus `IKeyManager`; ein mandantenfähiger Host mit einem Key Manager pro Mandant liefert daher Schlüssel pro Mandant aus.

## Verhalten des Autorisierungsendpunkts {#authorization-endpoint-behaviour}

`GET /connect/authorize` ist der Einstiegspunkt für den Authorization Code Flow. Zwei Verhaltensweisen sind für jeden wichtig, der dagegen einen Client oder eine Login-UI baut.

### Issuer in der Antwort (RFC 9207) {#issuer-in-the-response-rfc-9207}

Jede Weiterleitung zurück an die `redirect_uri` des Clients trägt einen Query-Parameter `iss` mit dem Issuer, sowohl bei Erfolg (neben `code` und `state`) als auch bei einem Fehler (neben `error`, `error_description` und `state`). Dasselbe gilt für die Fehlerweiterleitung, wenn ein Benutzer die Zustimmung unter `/consent` verweigert. Das Discovery-Dokument gibt dies mit `authorization_response_iss_parameter_supported: true` an. Ein Client, der mit mehreren Autorisierungsservern spricht, sollte `iss` mit dem Issuer vergleichen, bei dem er den Ablauf begonnen hat; genau das vereitelt den Mix-up-Angriff. Clients, die den Parameter ignorieren, sind nicht betroffen. Fehler, die auftreten, bevor eine vertrauenswürdige `redirect_uri` bekannt ist (unbekannte `client_id`, eine nicht registrierte Redirect-URI), werden als JSON-Fehlerbody statt als Weiterleitung zurückgegeben; dort gibt es also kein `iss`.

### `prompt` und `max_age` {#prompt-and-max_age}

| Anfrage | Verhalten |
|---|---|
| `prompt=login` | Eine bestehende Sitzung wird abgemeldet, und der Benutzer wird zur erneuten Authentifizierung zu `/login` geschickt. Der `prompt` wird aus der `returnUrl` entfernt, damit die neue Anmeldung nicht in einer Schleife eine erneute Authentifizierung erzwingt. Bei einer [übertragenen Anfrage](par) reist der Prompt in der gespeicherten Nutzlast mit, und die Schleife wird aufgelöst, indem verlangt wird, dass die `auth_time` der Sitzung zum Zeitpunkt der Übertragung der Anfrage oder danach liegt |
| `prompt=select_account` | Wird wie `prompt=login` behandelt: Der Server hält eine Sitzung pro Browser, die Kontoauswahl ist also der Anmeldebildschirm |
| `prompt=create` | Ein nicht authentifizierter Benutzer wird statt zum Anmeldeformular zu `/login/register` geschickt. Eine bestehende Sitzung fährt einfach fort |
| `prompt=consent` | Der Zustimmungsbildschirm wird auch dann angezeigt, wenn ein gespeicherter Grant die Anfrage erfüllen würde, einmal pro Anfrage (die Markierung für die erfüllte Zustimmung ist nur einmal verwendbar) |
| `prompt=none` | Es wird nie eine UI angezeigt. Der Server antwortet mit einer Weiterleitung, die `login_required` (keine Sitzung), `interaction_required` (MFA-Step-up oder -Einrichtung nötig) oder `consent_required` (Zustimmung nötig) trägt |
| `max_age=N` | Ist die `auth_time` der Sitzung älter als `N` Sekunden oder fehlt sie, wird der Benutzer genau wie bei `prompt=login` erneut authentifiziert. `max_age=0` authentifiziert immer erneut |

`prompt=none` in Kombination mit einem anderen Wert wird mit `invalid_request` abgelehnt, ebenso jeder Wert außer `none`, `login`, `consent`, `select_account` und `create`. Der einbettbare `Authagonal.Protocol`-Host berücksichtigt `prompt=login`, `select_account`, `none` und `max_age` auf dieselbe Weise, hat aber keine Zustimmungsoberfläche und antwortet daher auf `prompt=consent` mit `consent_required`.

## Eine eigene Login-UI bauen {#building-a-custom-login-ui}

Die Standard-SPA (`login-app/`) ist eine Implementierung dieser API. So bauen Sie Ihre eigene:

1. Stellen Sie Ihre UI unter den Pfaden `/login`, `/forgot-password`, `/reset-password`, `/consent`, `/device` bereit
2. Der Autorisierungsendpunkt leitet nicht authentifizierte Benutzer auf `/login?returnUrl={encoded-authorize-url}` weiter
3. Leiten Sie den Benutzer nach erfolgreicher Anmeldung (Cookie gesetzt) zur `returnUrl` weiter
4. Links zum Zurücksetzen des Passworts verwenden `{Issuer}/login/reset-password?p={token}` (die Login-SPA ist unter `/login` eingehängt)

Ihre UI muss vom **selben Origin** wie die API ausgeliefert werden, weil:
- die Cookie-Authentifizierung `SameSite=Lax` + `HttpOnly` verwendet
- der Autorisierungsendpunkt auf `/login` (relativ) weiterleitet
- Links zum Zurücksetzen `{Issuer}/login/reset-password` verwenden
