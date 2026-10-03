---
layout: default
title: Multi-Faktor-Authentifizierung
locale: de
---

# Multi-Faktor-Authentifizierung (MFA)

Authagonal unterstützt Multi-Faktor-Authentifizierung. Drei Methoden stehen zur Verfügung: TOTP (Authenticator-Apps), WebAuthn/Passkeys (Hardware-Schlüssel und Biometrie) und Einmal-Wiederherstellungscodes. Passkeys lassen sich außerdem für die [passwortlose Anmeldung](#passwordless-passkey-login) verwenden.

Föderierte Anmeldungen (SAML/OIDC) sind ebenfalls abgedeckt: Eine SAML- oder OIDC-Assertion belegt den ersten Faktor, nicht den zweiten. Ein föderierter Benutzer mit eingerichteter MFA durchläuft dieselbe lokale MFA-Abfrage wie bei einer Anmeldung mit Passwort, und eine Richtlinie `Required` erzwingt die Einrichtung, bevor eine Sitzung ausgestellt wird. Nur wenn MFA weder eingerichtet noch verlangt ist, genügt die Föderation allein. Eine Verbindung kann sich mit `ChallengeMfaAfterLogin: false` von der lokalen Abfrage ausnehmen (siehe unten).

## Unterstützte Methoden {#supported-methods}

| Methode | Beschreibung |
|---|---|
| **TOTP** | Zeitbasierte Einmalpasswörter (RFC 6238): 6 Ziffern, 30-Sekunden-Intervall, SHA-1, geprüft mit einem Toleranzfenster von einem Intervall für Uhrabweichungen. Funktioniert mit jeder Authenticator-App (Google Authenticator, Authy, 1Password usw.). Ein bereits akzeptierter Code kann innerhalb seines Gültigkeitsfensters nicht erneut verwendet werden. |
| **WebAuthn / Passkeys** | FIDO2-Hardware-Sicherheitsschlüssel, Plattform-Biometrie (Touch ID, Windows Hello) und synchronisierte Passkeys. Benutzer können mehrere Passkeys registrieren, und Passkeys ermöglichen eine passwortlose Anmeldung. |
| **Wiederherstellungscodes** | 10 einmalig verwendbare Ersatzcodes (10 Zeichen aus einem Alphabet mit 32 Zeichen, angezeigt als `XXXXX-XXXXX`) zur Kontowiederherstellung, wenn keine andere Methode verfügbar ist. Sie werden gehasht und im Ruhezustand verschlüsselt gespeichert. |

## MFA-Richtlinie {#mfa-policy}

Die Durchsetzung von MFA wird **pro Client** über die Eigenschaft `MfaPolicy` in `appsettings.json` konfiguriert:

| Wert | Verhalten |
|---|---|
| `Disabled` (Standard) | Keine erzwungene Einrichtung; die Self-Service-Oberfläche zur Einrichtung blendet MFA aus, wenn jeder Client `Disabled` ist |
| `Enabled` | MFA-Einrichtung anbieten, aber nicht erzwingen |
| `Required` | Einrichtung für Benutzer ohne MFA erzwingen |

Ein Benutzer mit eingerichteter MFA wird **bei der Anmeldung immer abgefragt, unabhängig von der Richtlinie des Clients**. MFA ist eine Eigenschaft des Benutzers und seiner Sitzung, nicht des anfragenden Clients. Ein Request über einen Client mit `Disabled` kann daher nicht genutzt werden, um den zweiten Faktor eines Benutzers mit eingerichteter MFA zu umgehen.

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

Der Standard ist `Disabled`, sodass bestehende Clients unverändert bleiben, bis Sie sich dafür entscheiden.

### Überschreibung pro Benutzer {#per-user-override}

Implementieren Sie `IAuthHook.ResolveMfaPolicyAsync`, um die Client-Richtlinie für bestimmte Benutzer zu überschreiben:

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

Die ermittelte Richtlinie bestimmt die Einrichtung (ob sie angeboten oder erzwungen wird). Sie nimmt einen Benutzer, der MFA bereits eingerichtet hat, nicht von der Abfrage aus; solche Benutzer werden immer abgefragt.

Die vollständige Dokumentation der Hooks finden Sie unter [Erweiterbarkeit](extensibility).

## Ablauf der Anmeldung {#login-flow}

Die Anmeldung mit MFA läuft wie folgt ab:

1. Der Benutzer sendet E-Mail-Adresse und Passwort an `POST /api/auth/login`
2. Der Server prüft das Passwort und ermittelt dann die effektive MFA-Richtlinie
3. Abhängig von der Richtlinie und davon, ob der Benutzer MFA eingerichtet hat:

| Richtlinie | Benutzer hat MFA? | Ergebnis |
|---|---|---|
| Beliebig | Ja | Gibt `mfaRequired` zurück: Der Benutzer muss sich verifizieren |
| `Disabled` / `Enabled` | Nein | Cookie gesetzt, Anmeldung abgeschlossen |
| `Required` | Nein | Gibt `mfaSetupRequired` zurück: Der Benutzer muss MFA einrichten |

### MFA-Abfrage {#mfa-challenge}

Wird `mfaRequired` zurückgegeben, enthält die Login-Antwort eine `challengeId`, die verfügbaren `methods` des Benutzers und (wenn der Benutzer Passkeys hat) Assertion-Optionen unter `webAuthn`. Der Client leitet auf eine Seite für die MFA-Abfrage weiter, auf der sich der Benutzer mit einer seiner eingerichteten Methoden über `POST /api/auth/mfa/verify` verifiziert:

```json
{
  "challengeId": "...",
  "method": "totp",
  "code": "123456"
}
```

`method` ist `totp`, `recovery` oder `webauthn` (WebAuthn sendet statt eines `code` eine `assertion`).

Abfragen laufen nach 5 Minuten ab (konfigurierbar über `Auth:MfaChallengeExpiryMinutes`) und werden bei erfolgreicher Verifizierung verbraucht.

#### Kontingent für Wiederholungen {#retry-budget}

Ein falscher Code verbraucht die Abfrage nicht. Der Verify-Endpunkt prüft zuerst den Code und verbraucht die Abfrage nur bei Erfolg, sodass eine vertippte TOTP-Ziffer einfach mit derselben `challengeId` wiederholt werden kann. Fehlversuche geben `invalid_code` (bzw. `assertion_failed` bei WebAuthn) mit einer 401 zurück und erhöhen einen begrenzten Zähler an der Abfrage; der fünfte falsche Versuch verbraucht die Abfrage und gibt `too_many_attempts` zurück, sodass eine neue Anmeldung nötig ist. Das gilt für alle drei Methoden.

Das Kontingent pro Abfrage ist nur eine schnelle erste Hürde, nicht die eigentliche Sicherheitsgrenze. Daher gelten für `POST /api/auth/mfa/verify` zwei weitere Sperren:

- **Ratenbegrenzung pro Benutzer.** Mehr als 10 Verifizierungsversuche pro Minute für einen Benutzer ergeben `too_many_attempts` mit einer 429, unabhängig davon, welche `challengeId` verwendet wird.
- **Gemeinsame Kontosperre.** Jeder falsche Code zählt auch gegen denselben Fehlversuchszähler wie der Passwortschritt (`Auth:MaxFailedAttempts`, `Auth:LockoutDurationMinutes`). Greift die Sperre, wird die Abfrage verbraucht und die Antwort ist `locked_out` (423). Solange das Konto gesperrt ist, wird die Verifizierung mit `locked_out` abgelehnt, bevor der Code überhaupt geprüft wird.

Nur bestätigte Berechtigungsnachweise können eine Verifizierung erfüllen; eine begonnene, aber nie abgeschlossene Einrichtung zählt nicht als Faktor.

Eine fehlende, abgelaufene oder bereits verbrauchte Abfrage ergibt `invalid_challenge`.

### Föderierte Anmeldungen {#federated-logins}

Nach einer erfolgreichen SAML- oder OIDC-Assertion ermittelt der Server dieselbe effektive MFA-Richtlinie. Ein Benutzer mit eingerichteter MFA wird auf die gehostete Seite für die MFA-Abfrage weitergeleitet (mit einer `challengeId`), statt eine Sitzung zu erhalten; ein Benutzer ohne MFA unter einer Richtlinie `Required` wird auf die Seite zur MFA-Einrichtung weitergeleitet (mit einem `setupToken`). Die Sitzung wird erst als MFA-authentifiziert markiert, wenn die Verifizierung abgeschlossen ist.

Diese Abfrage gilt pro Verbindung: Eine SAML- oder OIDC-Verbindung, bei der `ChallengeMfaAfterLogin` auf `false` gesetzt ist, überspringt die lokale Abfrage für Benutzer, die über sie ankommen. Der Standard ist `true`.

### Erzwungene Einrichtung {#forced-enrollment}

Wird `mfaSetupRequired` zurückgegeben, enthält die Antwort ein `setupToken`. Dieses Token authentifiziert den Benutzer gegenüber den Endpunkten zur MFA-Einrichtung (über den Header `X-MFA-Setup-Token`), sodass er eine Methode einrichten kann, bevor er eine Cookie-Sitzung erhält. Setup-Tokens laufen nach 15 Minuten ab (konfigurierbar über `Auth:MfaSetupTokenExpiryMinutes`).

## MFA einrichten {#enrolling-mfa}

Benutzer richten MFA über die Self-Service-Endpunkte zur Einrichtung ein. Diese erfordern entweder eine authentifizierte Cookie-Sitzung oder ein Setup-Token.

### TOTP einrichten {#totp-setup}

1. Rufen Sie `POST /api/auth/mfa/totp/setup` auf; die Antwort enthält einen QR-Code (`data:image/png;base64,...`), einen `manualKey` (Base32 für die manuelle Eingabe) und ein Setup-Token
2. Der Benutzer scannt den QR-Code mit seiner Authenticator-App
3. Der Benutzer gibt den 6-stelligen Code zur Bestätigung ein: `POST /api/auth/mfa/totp/confirm`

Der Bestätigungsschritt wird wie die Verifizierung gedrosselt: Mehr als 10 Versuche pro Minute für einen Benutzer ergeben `too_many_attempts` (429), und mit einem Setup-Token verbraucht der fünfte falsche Code die Einrichtungsabfrage. Eine nicht bestätigte Einrichtung läuft nach 30 Minuten ab (`setup_expired`).

### WebAuthn / Passkey einrichten {#webauthn--passkey-setup}

1. Rufen Sie `POST /api/auth/mfa/webauthn/setup` auf; die Antwort enthält ein `setupToken` und `PublicKeyCredentialCreationOptions`
2. Der Client ruft `navigator.credentials.create()` mit diesen Optionen auf
3. Senden Sie die Attestation-Antwort an `POST /api/auth/mfa/webauthn/confirm`

Die Einrichtung eines Passkeys setzt zuerst einen bestätigten TOTP-Berechtigungsnachweis voraus (`totp_required_first`). Passkeys sind eine gerätegebundene Annehmlichkeit auf einem übertragbaren Basisfaktor, sodass jedes Konto einen geräteunabhängigen Faktor behält und eine Richtlinie `Required` nicht allein durch einen Passkey erfüllt werden kann.

Benutzer können mehrere Passkeys registrieren (einen pro Gerät). Eine bereits registrierte Credential-ID (für ein beliebiges Konto, auch das eigene des einrichtenden Benutzers) wird mit `credential_already_registered` (409) abgelehnt. Würde ein bereits eingerichteter Authenticator erneut eingerichtet, entstünde eine zweite Zeile für den Berechtigungsnachweis mit derselben Credential-ID: Ihr Signaturzähler würde neu beginnen, was die Klonerkennung schwächt, und das Löschen einer der beiden Zeilen würde den Nachschlageeintrag entfernen, auf den beide angewiesen sind. Der Nachschlageeintrag wird mit einem Schreibvorgang belegt, der nur einfügt, wenn noch nichts vorhanden ist, sodass zwei Registrierungen derselben Credential-ID nicht beide gelingen können. Benutzer, deren E-Mail-Domain per erzwungenem SSO an einen externen IdP geleitet wird, können keinen lokalen Passkey einrichten (`sso_managed`), da dieser den IdP und dessen Deprovisionierung umgehen würde.

### Host der Relying Party {#relying-party-host}

Die FIDO2-Relying-Party-ID und der Origin werden pro Request aus dem Host ermittelt, sodass jeder Hostname eines Mandanten eine eigene Relying Party ist. Setzen Sie `Auth:WebAuthnAllowedHosts` auf die Hostnamen, die Sie bedienen, damit ein Host außerhalb dieser Liste nicht als Relying Party auftreten kann. Eine leere Liste (der Standard) behält das bisherige Verhalten bei, statt bestehende Passkey-Benutzer bei einem Upgrade auszusperren, und wird bei der ersten Verwendung als Lücke protokolliert. Ein sicherer Dauerzustand ist sie nicht. Zusätzlich `AllowedHosts` in `appsettings.json` zu setzen, damit die Host-Filterung von ASP.NET Core unbekannte `Host`-Header ablehnt, bevor ein Handler läuft, ist die günstigere äußere Schicht.

Unabhängig von dieser Liste speichert jeder Berechtigungsnachweis die Relying Party, unter der er eingerichtet wurde, und wird überall sonst abgelehnt. Diesen Teil kann der Request nicht beeinflussen: Sonst leiten beide Zeremonien ihre Erwartungen aus genau dem `Host`-Header ab, den sie prüfen. Origin und `rpIdHash` würden also mit einem Wert verglichen, den der Aufrufer selbst geliefert hat, und bei einem Host im Übertragungsweg, der seinen eigenen `Host` weiterreicht, würde die Origin-Bindung, also genau die Eigenschaft, die einen Passkey phishing-resistent macht, diesen Host bestätigen, statt ihn abzuwehren. Berechtigungsnachweise, die eingerichtet wurden, bevor die RP-ID gespeichert wurde, tragen keine und funktionieren weiterhin; sie erhalten die Bindung, wenn sie neu eingerichtet werden.

### Wiederherstellungscodes {#recovery-codes}

Rufen Sie `POST /api/auth/mfa/recovery/generate` auf, um 10 Einmalcodes zu erzeugen. Zuvor muss mindestens eine bestätigte primäre Methode (TOTP oder WebAuthn) eingerichtet sein (`primary_method_required`), und der Aufruf erfordert eine echte authentifizierte Sitzung: Ein Setup-Token erhält `session_required` (403).

Jeder Code besteht aus 10 Zeichen aus einem Alphabet mit 32 Zeichen und wird als zwei Fünfergruppen angezeigt (`XXXXX-XXXXX`).

Eine Neuerzeugung ersetzt alle bestehenden Wiederherstellungscodes. Jeder Code kann nur einmal verwendet werden; ein eingelöster Code wird als verbraucht markiert und nicht mehr akzeptiert.

Codes werden nie im Klartext gespeichert: Jeder Code wird gehasht, und der Hash wird im Ruhezustand zusätzlich mit dem Secret-Provider des Mandanten verschlüsselt. Ein Abzug des Speichers liefert also Chiffretext statt eines Hashs, der sich offline per Brute Force angreifen ließe.

## Passwortlose Anmeldung mit Passkey {#passwordless-passkey-login}

Passkeys sind nicht nur ein zweiter Faktor: Ein Benutzer mit eingerichtetem Passkey kann sich ohne Passwort anmelden.

1. `POST /api/auth/mfa/passwordless/begin` gibt eine `challengeId` und Assertion-`options` für auffindbare Berechtigungsnachweise zurück, sodass der Authenticator jeden für die Website gespeicherten Passkey anbietet
2. Der Client ruft `navigator.credentials.get()` mit diesen Optionen auf
3. `POST /api/auth/mfa/passwordless/complete` mit `{ challengeId, assertion }`: Der Server ermittelt den Benutzer aus dem Passkey selbst und meldet ihn an

Die gehostete Login-Seite bindet dies über Conditional Mediation (Passkey-Autofill) in das E-Mail-Feld ein: Unterstützt der Browser dies, wird ein verfügbarer Passkey als Autofill-Vorschlag angeboten, ohne zusätzliche Oberfläche.

Ein Passkey ist eine phishing-resistente starke Authentifizierung, daher trägt die entstehende Sitzung die MFA-Markierung und wird nicht erneut abgefragt. Wird die E-Mail-Domain des Benutzers per erzwungenem SSO an einen externen IdP geleitet, wird die passwortlose Anmeldung mit einer 409 `sso_required` abgelehnt, die die SSO-Weiterleitungs-URL enthält, damit ein lokaler Passkey den IdP nicht umgehen kann.

## MFA verwalten {#managing-mfa}

### Self-Service für Benutzer {#user-self-service}

- `GET /api/auth/mfa/status`: eingerichtete Methoden anzeigen (meldet auch, ob irgendein Client MFA anbietet)
- `DELETE /api/auth/mfa/credentials/{id}`: einen bestimmten Berechtigungsnachweis entfernen

Das Entfernen eines Berechtigungsnachweises erfordert eine echte authentifizierte Sitzung; ein Setup-Token berechtigt nur zum Hinzufügen eines ersten Faktors und erhält hier `session_required`, sodass ein abgeflossenes Setup-Token die MFA eines Benutzers nicht abschwächen kann.

Wird die letzte primäre Methode entfernt, ist MFA für den Benutzer deaktiviert.

### Admin-API {#admin-api}

Administratoren können MFA für jeden Benutzer über die [Admin-API](admin-api) verwalten:

- `GET /api/v1/profile/{userId}/mfa`: MFA-Status eines Benutzers anzeigen
- `DELETE /api/v1/profile/{userId}/mfa`: die gesamte MFA zurücksetzen (für ausgesperrte Benutzer)
- `DELETE /api/v1/profile/{userId}/mfa/{id}`: einen bestimmten Berechtigungsnachweis entfernen

### Audit-Hooks {#audit-hooks}

Implementieren Sie `IAuthHook.OnMfaVerifiedAsync`, um MFA-Ereignisse zu protokollieren:

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

Der gesamte MFA-Lebenszyklus ist über Hooks erreichbar: `OnMfaVerifyFailedAsync` (ein fehlgeschlagener Verifizierungsversuch), `OnMfaEnrolledAsync` (eine Methode bestätigt), `OnMfaCredentialRemovedAsync` (ein Berechtigungsnachweis entfernt, mit einem Flag, ob dadurch MFA deaktiviert wurde) und `OnRecoveryCodesRegeneratedAsync`.

## Eigene Login-Oberfläche {#custom-login-ui}

Wenn Sie eine eigene Login-Oberfläche bauen, behandeln Sie diese Antworten von `POST /api/auth/login`:

1. **Normale Anmeldung**: `{ userId, email, name }` mit gesetztem Cookie. Leiten Sie auf `returnUrl` weiter.
2. **MFA erforderlich**: `{ mfaRequired: true, challengeId, methods, webAuthn? }`. Zeigen Sie das Formular für die MFA-Abfrage an.
3. **MFA-Einrichtung erforderlich**: `{ mfaSetupRequired: true, setupToken }`. Zeigen Sie den Ablauf zur MFA-Einrichtung an.

Bei Fehlern von `POST /api/auth/mfa/verify` gilt: `invalid_code` und `assertion_failed` können mit derselben `challengeId` wiederholt werden (bis das Kontingent an Versuchen erschöpft ist); `too_many_attempts` und `invalid_challenge` sind endgültig, schicken Sie den Benutzer also zurück zum Anmeldeformular.

Die vollständige Referenz der Endpunkte finden Sie unter [Auth-API](auth-api).
