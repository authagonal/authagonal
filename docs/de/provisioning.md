---
layout: default
title: Provisionierung
locale: de
---

# TCC-Provisionierung

Authagonal provisioniert Benutzer in nachgelagerte Anwendungen nach dem Muster **Try-Confirm-Cancel (TCC)**. So stimmen alle Apps zu, bevor ein Benutzer Zugriff erhält, und lehnt eine App ab, wird sauber zurückgerollt.

## Wann die Provisionierung läuft {#when-provisioning-runs}

Die Provisionierung läuft automatisch, sobald ein Benutzer angelegt wird, unabhängig vom Weg, auf dem er angelegt wird:

| Endpunkt | Auslöser |
|---|---|
| `POST /api/v1/profile/` | Anlegen eines Benutzers durch einen Administrator |
| `POST /api/auth/register` | Selbstregistrierung |
| SAML ACS (`POST /saml/{id}/acs`) | Erster SSO-Login (neuer Benutzer) |
| OIDC-Callback (`GET /oidc/callback`) | Erster SSO-Login (neuer Benutzer) |
| SCIM (`POST /scim/v2/Users`) | Provisionierung durch den Identity Provider |
| `GET /connect/authorize` | Erste Autorisierung über einen Client mit `ProvisioningApps` |

Bereits provisionierte Kombinationen aus App und Benutzer werden übersprungen (nachverfolgt in der Tabelle `UserProvisions`).

Die Pfade zum Anlegen von Benutzern provisionieren in **jede konfigurierte App**. Der Authorize-Endpunkt provisioniert nur in die Apps der Liste `ProvisioningApps` des Clients.

**Bei Ablehnung:** Lehnt irgendeine Provisionierungs-App den Benutzer in der Try-Phase ab (oder schlägt ein Callback fehl), wird der neu angelegte Benutzer gelöscht. Das verhindert halb angelegte Benutzer. Was der Aufrufer sieht, hängt vom Pfad ab:

| Pfad | Antwort |
|---|---|
| Anlegen durch einen Administrator (`POST /api/v1/profile/`), Selbstregistrierung | `422 Unprocessable Entity` mit dem Ablehnungsgrund |
| SAML ACS, OIDC-Callback | `400 Bad Request`, `{ "error": "provisioning_rejected", "message": "..." }` |
| Anlegen per SCIM | SCIM-`400`, `scimType: invalidValue`, mit einer festen Meldung (der Text der nachgelagerten App wird nicht an den Identity Provider zurückgegeben) |
| Bestätigung der Übernahme eines passwortlosen Kontos | `400 provisioning_rejected` als JSON bzw. bei einem Klick im Browser eine Weiterleitung auf `/login?error=provisioning_rejected&error_description=...` (siehe [Upgrade eines Benutzers](user-upgrade)) |
| `GET /connect/authorize` | Weiterleitung zurück zum Client mit `error=access_denied` |

Der Request zum Anlegen durch einen Administrator akzeptiert `skipProvisioning: true`, gedacht für einen eigenen Aufrufer, der selbst das Ziel der Provisionierung ist und nicht möchte, dass sein eigener Callback erneut aufgerufen wird, während er den Benutzer gerade einrichtet. Für diesen Benutzer wird dann nichts provisioniert, und es werden keine Apps aufgerufen.

## Konfiguration {#configuration}

### 1. Provisionierungs-Apps definieren {#1-define-provisioning-apps}

In `appsettings.json`:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-bearer-token",
      "TryTimeoutSeconds": 60
    }
  }
}
```

`TryTimeoutSeconds` ist optional (Standard 60). Erhöhen Sie den Wert, wenn die nachgelagerte App während Try echte Arbeit leistet. Confirm, Cancel und Deprovision verwenden immer ein kurzes festes Timeout (10 Sekunden), das sich nicht einstellen lässt; diese Aufrufe sollten immer günstig sein.

Der Konfigurationsabschnitt `ProvisioningApps` wird nur gelesen, wenn kein `IProvisioningAppStore` registriert ist. Die Provider für Azure Table, AWS und SQL registrieren jeweils einen, und die Bibliothek ermittelt die Apps dann stattdessen aus dem Speicher (siehe [Eigene Ermittlung der Apps](#custom-app-resolution)). Mit einem persistenten Provider definieren Sie Apps daher über die Admin-API statt in `appsettings.json`.

### 2. Apps Clients zuordnen {#2-assign-apps-to-clients}

Jeder Client legt über das Feld `provisioningApps` seines Client-Datensatzes fest, in welche Apps seine Benutzer provisioniert werden müssen. Setzen Sie es über die Client-Admin-API (die Seed-Konfiguration `Clients` enthält dieses Feld nicht). Beim Anlegen eines Clients wird der gesamte Datensatz gebunden, und `PUT /api/v1/clients/{clientId}` führt die gesendeten Felder mit dem gespeicherten Client zusammen. Ein Request, der nur `provisioningApps` enthält, lässt den Rest des Clients also unverändert:

```
PUT /api/v1/clients/web-app
{
  "provisioningApps": ["my-backend"]
}
```

Autorisiert sich ein Benutzer über `web-app`, wird er in `my-backend` provisioniert, sofern das noch nicht geschehen ist.

## TCC-Protokoll {#tcc-protocol}

Authagonal ruft Ihren Provisionierungs-Endpunkt mit drei Arten von HTTP-Aufrufen auf. Alle verwenden `POST` mit JSON-Body und `Authorization: Bearer {ApiKey}`.

### Phase 1: Try {#phase-1-try}

**Request:** `POST {CallbackUrl}/try`

```json
{
  "transactionId": "a1b2c3d4...",
  "userId": "user-id",
  "email": "user@example.com",
  "firstName": "Jane",
  "lastName": "Doe",
  "organizationId": "org-id-or-null",
  "customAttributes": { "key": "value" }
}
```

Felder mit dem Wert null (auch `customAttributes`, wenn der Benutzer keine hat) werden im Payload weggelassen.

**Erwartete Antworten:**

| Status | Body | Bedeutung |
|---|---|---|
| `200` | `{ "approved": true }` | Der Benutzer kann provisioniert werden. Die App legt einen **ausstehenden** Datensatz an. |
| `200` | `{ "approved": false, "reason": "..." }` | Der Benutzer wird abgelehnt. Es wird kein Datensatz angelegt. |
| `2xx` | Leerer oder nicht auswertbarer Body | Wird als Zustimmung behandelt. |
| Nicht 2xx | Beliebig | Wird als Fehlschlag behandelt. |

Geben Sie einen expliziten `approved`-Wert zurück. Eine Antwort, deren Body sich nicht als JSON lesen lässt, gilt als Zustimmung; ein falsch konfigurierter Endpunkt, der mit `200` und einer HTML-Seite antwortet, stimmt also jedem Benutzer zu.

Die `transactionId` identifiziert diesen Provisionierungsversuch. Ihre App sollte sie zusammen mit dem ausstehenden Datensatz speichern.

Eine zustimmende Antwort kann außerdem `organizationId`, `customAttributes` und `emailVerified` zurückgeben. Authagonal führt sie mit dem Benutzer zusammen: `organizationId` wird nur übernommen, wenn der Benutzer noch keine hat (spätere Apps in derselben Transaktion sehen die frühere Zuordnung), Einträge in `customAttributes` werden Schlüssel für Schlüssel zusammengeführt, und `emailVerified: true` markiert die E-Mail-Adresse des Benutzers als bestätigt (verwenden Sie das, wenn die nachgelagerte App die Adresse bereits verifiziert hat; die Selbstregistrierung überspringt dann die Bestätigungs-E-Mail). Sowohl `organizationId` als auch die Attribute gelangen in die Tokens (Claim `org_id`; benutzerdefinierte Attribute über die Scope-Konfiguration `UserClaims`). Die zusammengeführten Werte werden am Benutzer gespeichert, sobald jede App bestätigt hat.

### Phase 2: Confirm {#phase-2-confirm}

Wird nur aufgerufen, wenn **alle** Apps in der Try-Phase `approved: true` zurückgegeben haben.

**Request:** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Erwartete Antwort:** `2xx` (beliebiger Body). Ihre App macht aus dem ausstehenden Datensatz einen bestätigten. Eine Antwort außerhalb von 2xx oder ein Timeout (10 Sekunden) gilt als fehlgeschlagenes Confirm.

### Phase 3: Cancel {#phase-3-cancel}

Wird aufgerufen, wenn das Try **irgendeiner** App abgelehnt wurde oder fehlgeschlagen ist, um bei den Apps aufzuräumen, deren Try erfolgreich war.

**Request:** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Erwartete Antwort:** `200` (beliebiger Body). Ihre App löscht den ausstehenden Datensatz.

Cancel erfolgt nach bestem Bemühen: Schlägt es fehl, protokolliert Authagonal den Fehler und macht weiter. Ihre App sollte zur Absicherung **unbestätigte Datensätze nach einer TTL aufräumen** (z. B. nach 1 Stunde).

## Ablaufdiagramm {#flow-diagram}

```
Authorize Endpoint
    │
    ├─ User authenticated ✓
    ├─ Client requires apps: [A, B]
    ├─ User already provisioned into: [A]
    ├─ Need to provision: [B]
    │
    ├─ TRY B ──────────► App B: create pending record
    │   └─ approved: true
    │
    ├─ CONFIRM B ──────► App B: promote to confirmed
    │   └─ 200 OK
    │
    ├─ Store provision record (userId, "B")
    ├─ Issue authorization code
    └─ Redirect to client
```

### Bei einem Fehlschlag {#on-failure}

```
    ├─ TRY A ──────────► App A: create pending record
    │   └─ approved: true
    │
    ├─ TRY B ──────────► App B: rejects
    │   └─ approved: false, reason: "No license available"
    │
    ├─ CANCEL A ───────► App A: delete pending record
    │
    └─ Redirect with error=access_denied
```

### Bei einem teilweise fehlgeschlagenen Confirm {#on-partial-confirm-failure}

Schlägt ein Confirm fehl, rollt Authagonal die gesamte Transaktion zurück:

1. Apps, die noch nicht bestätigt wurden, erhalten `POST {CallbackUrl}/cancel`.
2. Apps, die **in dieser Transaktion** bereits bestätigt haben, werden mit `DELETE {CallbackUrl}/users/{userId}` kompensiert (derselbe Aufruf wie beim [Deprovisionieren](#deprovisioning)), und ihre Provisionierungsdatensätze werden entfernt. Apps, in die der Benutzer durch eine frühere Transaktion provisioniert wurde, bleiben unberührt.
3. Es wird ein Provisionierungsfehler ausgelöst, und der aufrufende Pfad löscht den neu angelegten Benutzer (bzw. antwortet beim Authorize-Endpunkt mit einem Fehler).

Provisionierungsdatensätze werden erst gespeichert, wenn jedes Confirm erfolgreich war; ein erneuter Versuch spricht also wieder alle Apps an. Die Kompensation erfolgt nach bestem Bemühen: Ein fehlgeschlagenes `DELETE` wird protokolliert, und das Konto in der App muss gegebenenfalls von Hand entfernt werden.

## Eigene Ermittlung der Apps {#custom-app-resolution}

Die Bibliothek wählt die Quelle der Apps für Sie:

- Ist ein `IProvisioningAppStore` registriert, was die Provider für Azure Table, AWS und SQL alle tun, stammen die Apps aus dem Speicher (`StoreProvisioningAppProvider`) und werden über die unten beschriebene Admin-API verwaltet.
- Andernfalls werden sie aus dem Konfigurationsabschnitt `ProvisioningApps` gelesen (`ConfigProvisioningAppProvider`).

Registrieren Sie vor `AddAuthagonal` einen eigenen `IProvisioningAppProvider`, um Apps auf andere Weise zu ermitteln, zum Beispiel pro Mandant; der Standard der Bibliothek wird nur hinzugefügt, wenn keiner registriert ist:

```csharp
builder.Services.AddSingleton<IProvisioningAppProvider, MyAppProvider>();
builder.Services.AddAuthagonal(builder.Configuration);
```

Der Provider liefert eine Liste von Apps samt ihrer Callback-URLs. Der `TccProvisioningOrchestrator` ruft für jede davon Try/Confirm/Cancel auf.

> **`CallbackUrl` muss standardmäßig öffentlich routbar sein.** Authagonal validiert die URL beim Schreiben und erneut bei jedem Request, den es stellt, und lehnt Ziele unter Loopback, RFC1918, Link-Local sowie `.internal`/`.local` ab (ein Provisionierungs-Callback ist eine vom Server abgerufene URL). Eine Provisionierungs-App, die in Ihrem eigenen Netz läuft, ist eine unterstützte Bereitstellungsform: Tragen Sie sie in [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard) ein.

### Admin-API {#admin-api}

Die im Speicher gehaltenen Apps werden unter `/api/v1/provisioning/apps` verwaltet (Richtlinie `IdentityAdmin`; jede Änderung wird im Audit-Log erfasst):

| Route | Verhalten |
|---|---|
| `GET /` | `{ "apps": [{ "appId", "name", "callbackUrl", "hasApiKey", "tryTimeoutSeconds" }], "limit": n }`. Der API-Schlüssel wird nie zurückgegeben, nur `hasApiKey`. `limit` ist das App-Kontingent, null, wenn es keines gibt. |
| `POST /` | Anlegen. `name` und `callbackUrl` sind Pflicht; `apiKey` und `tryTimeoutSeconds` sind optional. Es wird eine `appId` mit 12 Zeichen erzeugt. Bei überschrittenem Kontingent `400 provisioning_app_limit`. |
| `PUT /{appId}` | Ersetzt `name`, `callbackUrl` und `tryTimeoutSeconds` (`name` und `callbackUrl` sind wieder Pflicht). Ein weggelassenes oder auf null gesetztes `apiKey` lässt den Schlüssel unverändert; eine leere Zeichenfolge löscht ihn. `404 app_not_found` bei einer unbekannten App. |
| `DELETE /{appId}` | `{ "removed": true }`. |
| `POST /{appId}/test` | Sendet ein Try mit einem festen Testbenutzer (`test-user`, `test@example.com`) an die App, mit einem Timeout von 10 Sekunden. Gibt `{ "success", "statusCode", "body" }` zurück (Body auf 1000 Zeichen gekürzt). Verbindungsfehler liefern `success: false, statusCode: 0` statt eines Fehlerstatus. |

`callbackUrl` muss wie oben beschrieben eine absolute `http`- oder `https`-URL auf einem externen Host sein. `tryTimeoutSeconds` wird auf 5 bis 300 Sekunden begrenzt. Die `appId` ist der Wert, den ein Client in `provisioningApps` aufführt.

## Deprovisionierung {#deprovisioning}

Wird ein Benutzer über die Admin-API gelöscht (`DELETE /api/v1/profile/{userId}`) oder per SCIM deprovisioniert (`DELETE /scim/v2/Users/{id}`, ein Soft Delete, der den Benutzer deaktiviert), ruft Authagonal bei jeder App, in die der Benutzer provisioniert wurde, `DELETE {CallbackUrl}/users/{userId}` mit einem Timeout von 10 Sekunden auf und entfernt den Provisionierungsdatensatz. Das erfolgt nach bestem Bemühen: Fehlschläge werden protokolliert, blockieren das Löschen aber nicht. Eine App, die nicht mehr konfiguriert ist, wird mit einer Warnung übersprungen.

`ReprovisionAsync` an `IProvisioningOrchestrator` führt Try und Confirm für jede App erneut aus, auch dort, wo der Benutzer bereits provisioniert ist. Die Bibliothek verwendet es, wenn ein passwortloses Konto übernommen wird (siehe [Upgrade eines Benutzers](user-upgrade)); ein einfacher erneuter Login tut das nie.

## Die Upstream-Endpunkte implementieren {#implementing-the-upstream-endpoints}

### Minimalbeispiel (Node.js/Express) {#minimal-example-nodejsexpress}

```javascript
const pending = new Map(); // transactionId → user data

app.post('/provisioning/try', (req, res) => {
  const { transactionId, userId, email } = req.body;

  // Your business logic: can this user be provisioned?
  if (!isAllowed(email)) {
    return res.json({ approved: false, reason: 'Domain not allowed' });
  }

  // Store pending record with TTL
  pending.set(transactionId, { userId, email, createdAt: Date.now() });

  res.json({ approved: true });
});

app.post('/provisioning/confirm', (req, res) => {
  const { transactionId } = req.body;
  const data = pending.get(transactionId);

  if (data) {
    createUser(data); // Promote to real record
    pending.delete(transactionId);
  }

  res.sendStatus(200);
});

app.post('/provisioning/cancel', (req, res) => {
  pending.delete(req.body.transactionId);
  res.sendStatus(200);
});

// Cleanup unconfirmed records older than 1 hour
setInterval(() => {
  const cutoff = Date.now() - 3600000;
  for (const [id, data] of pending) {
    if (data.createdAt < cutoff) pending.delete(id);
  }
}, 600000);
```
