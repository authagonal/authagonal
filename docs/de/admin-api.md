---
layout: default
title: Admin-API
locale: de
---

# Admin-API

Admin-Endpunkte erfordern ein JWT-Access-Token mit dem Scope `authagonal-admin` (konfigurierbar über `AdminApi:Scope`).

Alle Endpunkte liegen unter `/api/v1/`.

## Das erste Admin-Token beschaffen {#bootstrapping-the-first-admin-token}

Jeder Endpunkt unter `/api/v1/*` erfordert ein Bearer Token mit dem Admin-Scope, doch die Admin-API selbst (ebenso wie die [dynamische Client-Registrierung](client-registration)) **verweigert das Anlegen oder Aktualisieren jedes Clients, der diesen Scope hält** (`403 forbidden_scope`). Ein zur Laufzeit angelegter Client kann sich also niemals zum Admin hochstufen. Der einzige Weg, ein Admin-Token auszustellen, ist ein **per Konfiguration angelegter Client**: Einträge im Konfigurationsabschnitt `Clients:` werden beim Start von `ClientSeedService` per Upsert geschrieben. Der Konfiguration wird vertraut; die Sperre für verbotene Scopes gilt nur für die Laufzeit-APIs.

Legen Sie in `appsettings.json` (oder über die entsprechenden Umgebungsvariablen bzw. den Secret Store) einen `client_credentials`-Client mit dem Admin-Scope an:

```json
{
  "Clients": [
    {
      "Id": "admin-cli",
      "Name": "Admin CLI",
      "ClientSecret": "a-long-random-secret",
      "GrantTypes": ["client_credentials"],
      "Scopes": ["authagonal-admin"]
    }
  ]
}
```

(`ClientSecret` wird beim Start gehasht; geben Sie stattdessen `SecretHashes` an, wenn Sie in der Konfiguration nur einen vorab gehashten Wert halten möchten. `ClientId`/`ClientName`/`AllowedGrantTypes`/`AllowedScopes` werden als Aliasse für `Id`/`Name`/`GrantTypes`/`Scopes` akzeptiert.)

Tauschen Sie die Anmeldedaten anschließend am regulären Token-Endpunkt gegen ein Token:

```bash
curl -X POST https://auth.example.com/connect/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=client_credentials" \
  -d "client_id=admin-cli" \
  -d "client_secret=a-long-random-secret" \
  -d "scope=authagonal-admin"
```

```json
{ "access_token": "eyJhbGci...", "token_type": "Bearer", "expires_in": 1800, "scope": "authagonal-admin" }
```

Der Grant `client_credentials` prüft den angeforderten Scope gegen die `AllowedScopes` des Clients; da der angelegte Client `authagonal-admin` hält, wird das Token ausgestellt. Verwenden Sie es bei jedem Admin-Aufruf als `Authorization: Bearer {access_token}`:

```bash
curl https://auth.example.com/api/v1/clients -H "Authorization: Bearer eyJhbGci..."
```

Bewahren Sie das Secret des angelegten Clients im Secret Store Ihres Deployments auf; es zu rotieren bedeutet eine Konfigurationsänderung plus Neustart.

## Benutzer {#users}

### Benutzer abrufen {#get-user}

```
GET /api/v1/profile/{userId}
```

Liefert das Profil sowie das, was eine Support-Konsole zur Diagnose eines Anmeldeproblems braucht:
`emailConfirmed`, `isActive`, `lockoutEnd`, `accessFailedCount`, `roles`, die verknüpften
`externalLogins` und `hasPassword` (nur ob eines vorhanden ist, niemals den Hash). Letzteres macht den Unterschied
zwischen „Die Person hat ihr Passwort vergessen“ und „Die Person hatte nie eines, sie meldet sich per SSO an“,
und das sind gegensätzliche Ratschläge.

Liefert die Benutzerdetails einschließlich der Verknüpfungen zu externen Anmeldungen.

### Existiert der Benutzer? {#user-exists}

```
GET /api/v1/profile/{userId}/exists
```

Liefert `204`, wenn der Benutzer existiert, andernfalls `404` (eine günstige Existenzprüfung ohne Body).

### Benutzer registrieren {#register-user}

```
POST /api/v1/profile/
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Legt einen Benutzer an und sendet eine Bestätigungs-E-Mail. Liefert `409 user_exists`, wenn die E-Mail-Adresse bereits vergeben ist.

Optionale, nur Administratoren vorbehaltene Felder: `userId` (vom Aufrufer vorgegebene ID, `409 user_id_in_use` bei einer Kollision), `emailConfirmed` (den Benutzer bereits bestätigt anlegen, ohne Bestätigungs-E-Mail), `companyName`, `organizationId`, `phone`, `locale` und `customAttributes` (eine String-Map, die am Benutzer gespeichert und an Provisionierungsziele weitergereicht wird).

`skipProvisioning: true` legt die Identität an, ohne die Provisionierung auszuführen. Das ist für eine Anwendung
aus erster Hand gedacht, die SELBST ein Provisionierungsziel ist und bereits mitten in der Einrichtung dieses Benutzers steckt:
Sie ruft hier auf, um die Identität ausstellen zu lassen, nicht um zu einem Benutzer zurückgerufen zu werden, den sie gerade
anlegt. Ohne diese Option erhält die Anwendung ihren eigenen Try-Aufruf für einen halb angelegten Benutzer, der nur die
Attribute trägt, die den Hin- und Rückweg überstanden haben, und provisioniert den Benutzer, falls sie sich davon erholt, am Ende doppelt.

### Benutzer aktualisieren {#update-user}

```
PUT /api/v1/profile/
Content-Type: application/json

{
  "userId": "user-id",
  "firstName": "Jane",
  "lastName": "Smith",
  "organizationId": "new-org-id"
}
```

`userId` ist erforderlich; jedes andere Feld ist optional, und nur übergebene Felder werden aktualisiert.

`isActive` deaktiviert oder reaktiviert das Konto. `emailConfirmed` (auch als `emailVerified`
akzeptiert) markiert die Adresse als bestätigt, ohne eine Bestätigungsmail zu senden, für den Fall, dass der Besitz
auf anderem Weg nachgewiesen wurde.

Eine Änderung von `organizationId` oder eine Deaktivierung löst Folgendes aus:
- Rotation des SecurityStamp (macht alle Cookie-Sitzungen innerhalb von 30 Minuten ungültig)
- Widerruf aller Refresh Tokens

Eine Sperre, die erst bei der nächsten Anmeldung wirkt, ist keine Sperre; deshalb widerruft eine Deaktivierung, statt
auf den Ablauf zu warten.

### Benutzer suchen {#search-users}

```
GET /api/v1/profile/search?q=jane&maxResults=20
```

Präfixsuche über die Indizes für E-Mail-Adresse und Namen. Liefert `{ "users": [ ... ] }`.

### Benutzer per E-Mail abrufen {#get-user-by-email}

```
GET /api/v1/profile/by-email?email=jane@example.com
```

Exakte Abfrage, im Unterschied zur Suche, die ein Präfixabgleich ist und mehrere Personen liefern kann. Ein Aufrufer,
der „diese Adresse“ zu „diesem Konto“ auflöst, will genau eine Antwort oder keine. `404`, wenn es keinen solchen Benutzer gibt.

### Benutzer auflisten {#list-users}

```
GET /api/v1/profile?organizationId=&count=100&continuationToken=
```

Cursorbasiert paginierte Verzeichnisauflistung; übergeben Sie das zurückgegebene `continuationToken` für die nächste Seite zurück und
hören Sie auf, wenn es null ist. Cursor statt Offsets, weil der Store per Token paginiert: Ein Offset würde bei jeder Seite
erneut von vorn scannen.

### Welche Benutzer existieren {#which-users-exist}

```
POST /api/v1/profile/exists
Content-Type: application/json

{ "userIds": [ "a", "b", "c" ] }
```

Liefert die Teilmenge, die existiert, sowie `truncated: true`, wenn die Anfrage die Obergrenze von 500 IDs überschritten hat. So erfährt
ein Aufrufer, dass sein Stapel gekürzt wurde, statt stillschweigend eine Antwort zu 500 von 600 zu erhalten. Gedacht zum
Abgleich einer ID-Menge mit der eines anderen Systems.

### MFA-Status für viele Benutzer {#mfa-status-for-many-users}

```
POST /api/v1/profile/mfa-status
Content-Type: application/json

{ "userIds": [ "a", "b", "c" ] }
```

Liefert `{ "statuses": { "a": true, "b": false }, "truncated": false }`: `true` bedeutet, dass der Benutzer mindestens einen MFA-Berechtigungsnachweis hat. Begrenzt auf 500 IDs; `truncated: true` zeigt an, dass die Anfrage gekürzt wurde. Gedacht für Kennzeichnungen wie „nutzt MFA“ in einer Verzeichnisansicht.

### Ein Passwort setzen {#set-a-password}

```
POST /api/v1/profile/{userId}/set-password
Content-Type: application/json

{ "password": "N3w!Password" }
```

Der Support-Weg für jemanden, der aus einem Konto ausgesperrt ist, dessen Adresse ihn nicht mehr erreicht. Unterliegt
der Passwortrichtlinie. Widerruft jedes Refresh Token und rotiert den Security Stamp: Eine Passwortänderung, die die alten
Sitzungen weiterlaufen lässt, hat nicht geändert, wer als diese Person handeln kann.

### Einen Benutzer entsperren {#unlock-a-user}

```
POST /api/v1/profile/{userId}/unlock
```

Hebt die Sperre auf und setzt den Zähler fehlgeschlagener Versuche zurück, sodass die Person jetzt wieder hineinkommt und nicht erst,
wenn die Sperre irgendwann abläuft.

### Benutzer löschen {#delete-user}

```
DELETE /api/v1/profile/{userId}
```

Löscht den Benutzer, widerruft alle Grants und deprovisioniert ihn aus allen nachgelagerten Anwendungen (nach bestem Bemühen).

### E-Mail-Adresse bestätigen {#confirm-email}

```
POST /api/v1/profile/confirm-email?token={token}
```

### Bestätigungs-E-Mail senden {#send-verification-email}

```
POST /api/v1/profile/{userId}/send-verification-email
```

### Externe Identität verknüpfen {#link-external-identity}

```
POST /api/v1/profile/{userId}/identities
Content-Type: application/json

{
  "provider": "saml:acme-azure",
  "providerKey": "external-user-id",
  "displayName": "Acme Corp Azure AD"
}
```

### Verknüpfung einer externen Identität aufheben {#unlink-external-identity}

```
DELETE /api/v1/profile/{userId}/identities/{provider}/{externalUserId}
```

## MFA-Verwaltung {#mfa-management}

### MFA-Status abrufen {#get-mfa-status}

```
GET /api/v1/profile/{userId}/mfa
```

Liefert den MFA-Status und die eingerichteten Methoden eines Benutzers.

### Gesamte MFA zurücksetzen {#reset-all-mfa}

```
DELETE /api/v1/profile/{userId}/mfa
```

Entfernt alle MFA-Berechtigungsnachweise und setzt `MfaEnabled=false`. Der Benutzer muss MFA, falls erforderlich, erneut einrichten.

### Einen bestimmten MFA-Berechtigungsnachweis entfernen {#remove-specific-mfa-credential}

```
DELETE /api/v1/profile/{userId}/mfa/{credentialId}
```

Entfernt einen bestimmten MFA-Berechtigungsnachweis (z. B. einen verlorenen Authenticator). Wird die letzte primäre Methode entfernt, wird MFA deaktiviert.

## SSO-Provider {#sso-providers}

### SAML-Provider {#saml-providers}

```
POST   /api/v1/saml/connections                    # Create
GET    /api/v1/saml/connections/{connectionId}     # Get one
PUT    /api/v1/saml/connections/{connectionId}     # Update (partial: only supplied fields change)
DELETE /api/v1/saml/connections/{connectionId}     # Delete
```

Das Anlegen erfordert `connectionName`, `entityId` und **genau eines von** `metadataLocation` (einer Metadaten-URL) oder `metadataXml` (eingefügten IdP-Metadaten für IdPs ohne Metadaten-URL; sie werden beim Speichern per Parsing validiert und verdichtet). Optional: `nameIdFormat` (weglassen für den Standard emailAddress, `"none"`, um NameIDPolicy wegzulassen, empfohlen für ADFS, oder eine URN eines NameID-Formats), `signAuthnRequests`, `iconUrl`, `allowedDomains`, `disableJitProvisioning`, `organizationId`. Jede Verbindung erhält ein vom Server erzeugtes SP-Schlüsselpaar; es wird von der API nie zurückgegeben. Einzelheiten unter [SAML](saml).

`organizationId` beschränkt die Verbindung auf eine [Organisation](organizations): Sie wird nur angeboten, wenn diese Organisation ausgewählt ist, ihre `allowedDomains` werden nur innerhalb dieser Organisation abgeglichen (und *nicht* in den mandantenweiten SSO-Domain-Index geschrieben), und jeder, der sich darüber anmeldet, wird Mitglied dieser Organisation. Weggelassen oder `null` = eine Verbindung auf Mandantenebene. Eine nicht existierende Organisation ergibt `400 unknown_organization`. Beim Aktualisieren lässt `null` (fehlendes Feld) die Zuordnung unverändert, `""` setzt die Verbindung auf die Mandantenebene zurück, und beide Richtungen schreiben den Domain-Index entsprechend um. Siehe [Organisationsbezogene Verbindungen](self-service-sso#organisation-scoped-connections).

### OIDC-Provider {#oidc-providers}

```
POST   /api/v1/oidc/connections                    # Create
GET    /api/v1/oidc/connections/{connectionId}     # Get one
DELETE /api/v1/oidc/connections/{connectionId}     # Delete
```

Das Anlegen erfordert `connectionName`, `metadataLocation`, `clientId`, `clientSecret`, `redirectUrl`. Optional: `iconUrl`, `allowedDomains`, `passthroughParams`, `organizationId` (gleiche Bedeutung wie bei einer SAML-Verbindung, siehe oben). Das Client-Secret wird im Ruhezustand geschützt und nie zurückgegeben. Siehe [OIDC-Föderation](oidc-federation).

### SSO-Domains {#sso-domains}

```
GET    /api/v1/sso/domains                 # List all
```

## Clients {#clients}

Verwalten Sie OAuth-Clients zur Laufzeit. Alle Routen erfordern die Richtlinie `IdentityAdmin` (den Admin-Scope).

```
GET    /api/v1/clients              # List all clients
GET    /api/v1/clients/{clientId}   # Get one client
POST   /api/v1/clients              # Create a client
PUT    /api/v1/clients/{clientId}   # Update a client
DELETE /api/v1/clients/{clientId}   # Delete a client
```

### Client anlegen / aktualisieren {#create--update-client}

```
POST /api/v1/clients
Content-Type: application/json

{
  "clientId": "my-app",
  "clientName": "My Application",
  "allowedGrantTypes": ["authorization_code"],
  "redirectUris": ["https://app.example.com/callback"],
  "allowedScopes": ["openid", "profile", "email"]
}
```

`POST` liefert `409`, wenn der Client bereits existiert. `PUT` aktualisiert einen bestehenden Client (`404`, wenn er nicht gefunden wird); beim Aktualisieren werden nur neu hinzugefügte Scopes auf eine Rechteausweitung geprüft.

Hinweise:

- **Secret-Hashes werden nie zurückgegeben.** `clientSecretHashes` wird aus jeder Antwort entfernt (Auflisten, Abrufen, Anlegen, Aktualisieren). Wird `clientSecretHashes` beim Aktualisieren weggelassen, bleibt das gespeicherte Secret erhalten; neue Hashes rotieren es.
- **Der Admin-Scope kann keinem Client gewährt werden.** Wird `AdminApi:Scope` (Standard `authagonal-admin`) in `allowedScopes` angefordert, ergibt das `403 forbidden_scope`. Kein Client darf den Admin-Scope halten, sonst könnte ein `client_credentials`-Client unbegrenzt Admin-Tokens ausstellen.
- Das Hinzufügen von Scopes, die der Aufrufer nicht vergeben darf, ergibt `403`.

## Scopes {#scopes}

Verwalten Sie eigene OAuth-Scopes zur Laufzeit. Das vollständige Scope-Modell beschreibt [OAuth-Scopes](scopes).

```
GET    /api/v1/scopes           # List all scopes
GET    /api/v1/scopes/{name}    # Get one scope
POST   /api/v1/scopes           # Create a scope
PUT    /api/v1/scopes/{name}    # Update a scope (only supplied fields change)
DELETE /api/v1/scopes/{name}    # Delete a scope
```

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing, read-only",
  "description": "View invoices and payment history",
  "userClaims": ["billing_plan"]
}
```

Liefert beim Anlegen `201` (`409`, wenn der Scope bereits existiert), beim Abrufen und Aktualisieren das JSON des Scopes und beim Löschen `204`.

## Provisionierungs-Apps {#provisioning-apps}

Verwalten Sie nachgelagerte Provisionierungsziele zur Laufzeit. Alle Routen erfordern die Richtlinie `IdentityAdmin`.

```
GET    /api/v1/provisioning/apps               # List apps (also returns the configured limit)
POST   /api/v1/provisioning/apps               # Create an app
PUT    /api/v1/provisioning/apps/{appId}       # Update an app
DELETE /api/v1/provisioning/apps/{appId}       # Delete an app
POST   /api/v1/provisioning/apps/{appId}/test  # Send a test /try call to the app's callback
```

### Provisionierungs-App anlegen / aktualisieren {#create--update-provisioning-app}

```
POST /api/v1/provisioning/apps
Content-Type: application/json

{
  "name": "Backend",
  "callbackUrl": "https://api.example.com/provisioning",
  "apiKey": "secret-api-key",
  "tryTimeoutSeconds": 30
}
```

- `name` und `callbackUrl` sind erforderlich; `callbackUrl` muss eine absolute `http(s)`-URL sein.
- `tryTimeoutSeconds` wird auf den Bereich 5–300 begrenzt.
- **Der API-Schlüssel wird nie zurückgegeben.** Antworten enthalten statt des Schlüssels `hasApiKey` (einen booleschen Wert). Beim Aktualisieren lässt ein weggelassenes `apiKey` ihn unverändert, eine leere Zeichenkette löscht ihn, und ein Wert ersetzt ihn.
- Das Anlegen unterliegt einem konfigurierbaren Kontingent pro Deployment (`IProvisioningAppQuota`); bei Überschreitung wird `400 provisioning_app_limit` geliefert. Die Antwort der Auflistung enthält das aktuelle `limit`.

### Eine Provisionierungs-App testen {#test-a-provisioning-app}

```
POST /api/v1/provisioning/apps/{appId}/test
```

Sendet ein synthetisches `POST {callbackUrl}/try` mit einer Beispielnutzlast (und, falls gesetzt, dem API-Schlüssel der Anwendung als Bearer Token) und liefert `{ success, statusCode, body }`, damit Sie die Verbindung über die Admin-Oberfläche prüfen können.

## Rollen {#roles}

### Rollen auflisten {#list-roles}

```
GET /api/v1/roles
```

### Rolle abrufen {#get-role}

```
GET /api/v1/roles/{roleId}
```

### Rolle anlegen {#create-role}

```
POST /api/v1/roles
Content-Type: application/json

{
  "name": "admin",
  "description": "Administrator role"
}
```

### Rolle aktualisieren {#update-role}

```
PUT /api/v1/roles/{roleId}
Content-Type: application/json

{
  "name": "admin",
  "description": "Updated description"
}
```

### Rolle löschen {#delete-role}

```
DELETE /api/v1/roles/{roleId}
```

### Einem Benutzer eine Rolle zuweisen {#assign-role-to-user}

```
POST /api/v1/roles/assign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

Die Zuweisung erfolgt über den **Rollennamen**, nicht über die Rollen-ID. Liefert die aktualisierte Rollenliste des Benutzers.

### Einem Benutzer eine Rolle entziehen {#unassign-role-from-user}

```
POST /api/v1/roles/unassign
Content-Type: application/json

{
  "userId": "user-id",
  "roleName": "admin"
}
```

### Rollen eines Benutzers abrufen {#get-users-roles}

```
GET /api/v1/roles/user/{userId}
```

### Benutzer einer Rolle {#users-in-a-role}

```
GET /api/v1/roles/{roleName}/users?maxResults=200
```

Die Umkehrung des Obigen (wer diese Rolle innehat), beantwortet aus einem Index der Rollenmitgliedschaften statt
durch Lesen jedes Benutzers. Liefert `{ "roleName": "...", "members": [ { "userId", "email", "firstName",
"lastName", "roles" } ] }`; jedes Mitglied trägt seinen vollständigen Rollensatz, weil eine Konsole, die eine
Rolle auflistet, fast immer auch zeigen will, was ihre Mitglieder sonst noch haben.

`404 role_not_found` für eine Rolle, die nicht existiert, statt einer leeren Liste: „Niemand hat diese Rolle inne“
und „Sie haben den Rollennamen falsch geschrieben“ sind unterschiedliche Probleme. `501 not_supported`, wenn der konfigurierte
Store die Rollenmitgliedschaft nicht indiziert, aus demselben Grund: Eine leere Mitgliederliste würde sich lesen wie
„niemand administriert das“.

Konten, die vor Einführung des Index geschrieben wurden, sind für ihn unsichtbar, bis sie neu indiziert werden
(`IUserStore.ReindexUserAsync`, das die Mitgliedschaften eines Benutzers per Upsert schreibt, ohne welche zu entfernen).

## SCIM-Tokens {#scim-tokens}

### Token erzeugen {#generate-token}

```
POST /api/v1/scim/tokens
Content-Type: application/json

{
  "clientId": "client-id",
  "description": "Entra provisioning",
  "expiresInDays": 365
}
```

`description` und `expiresInDays` sind optional (lassen Sie `expiresInDays` für ein Token ohne Ablaufdatum weg). Liefert das Rohtoken ein einziges Mal. Bewahren Sie es sicher auf; es lässt sich nicht erneut abrufen.

### Tokens auflisten {#list-tokens}

```
GET /api/v1/scim/tokens?clientId=client-id
```

Liefert die Metadaten der Tokens (ID, Erstellungsdatum) ohne den Rohwert des Tokens.

### Token widerrufen {#revoke-token}

```
DELETE /api/v1/scim/tokens/{tokenId}?clientId=client-id
```

## Tokens {#tokens}

### Benutzer impersonieren {#impersonate-user}

```
POST /api/v1/token?clientId=client-id&userId=user-id&scopes=openid%20profile
```

Stellt im Namen eines Benutzers Tokens aus (Access Token, Refresh Token und, wenn `openid` angefordert wird, ID-Token), ohne dessen Anmeldedaten zu benötigen. Nützlich für Tests und Support. Die Parameter werden als Query-Strings übergeben.

| Query-Parameter | Erforderlich | Beschreibung |
|---|---|---|
| `clientId` | Ja | Der Client, für den die Tokens ausgestellt werden. Die Lebensdauer der Tokens ergibt sich aus der Konfiguration dieses Clients. |
| `userId` | Ja | Der zu impersonierende Benutzer. |
| `scopes` | Nein | **Durch Leerzeichen getrennte** Liste von Scopes (die Leerzeichen URL-kodieren). Ohne Angabe gelten die `AllowedScopes` des Clients. |

Einschränkungen:

- Die Scopes sind auf die `AllowedScopes` des Clients beschränkt; die Anforderung eines Scopes, den der Client selbst nicht anfordern könnte, ergibt `400 invalid_scope`.
- Der Admin-Scope (`AdminApi:Scope`, Standard `authagonal-admin`) kann über diesen Endpunkt **nicht** ausgestellt werden; seine Anforderung ergibt `403 forbidden_scope`. Das verhindert, dass ein (möglicherweise zeitlich begrenztes) Admin-Token ein langlebiges Admin-Access- oder Refresh-Token ausstellt.

Die Antwort ist eine reguläre Token-Antwort mit `access_token`, `refresh_token`, optional `id_token`, `expires_in` und dem gewährten `scope` (durch Leerzeichen getrennt).
