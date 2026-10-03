---
layout: default
title: OAuth-Scopes
locale: de
---

# OAuth-Scopes

Authagonal unterstützt sowohl **integrierte** OAuth/OIDC-Scopes als auch **benutzerdefinierte** Scopes, die zur Laufzeit verwaltet werden. Benutzerdefinierte Scopes werden dauerhaft gespeichert, über das Discovery-Dokument angekündigt und auf dem Zustimmungsbildschirm neben den integrierten angezeigt.

## Integrierte Scopes {#built-in-scopes}

Diese Scopes stehen immer zur Verfügung und müssen nicht registriert werden:

| Scope | Zweck |
|---|---|
| `openid` | Erforderlich, um einen OIDC-Ablauf zu starten. Stellt ein ID Token aus. |
| `profile` | Standard-Profil-Claims (name, family_name, given_name usw.) |
| `email` | E-Mail-Adresse und `email_verified`-Claims |
| `phone` | Claims `phone_number` und `phone_number_verified` (OIDC Core 5.4) |
| `roles` | Der Claim `roles`. Kein Standard-Scope von OIDC: Die Rollenzugehörigkeit ist ein Claim, dessen Offenlegung der Endbenutzer zustimmt |
| `groups` | Der Claim `groups` (SCIM-Gruppenzugehörigkeit). Kein Standard-Scope von OIDC, gesteuert wie `roles` |
| `offline_access` | Stellt zusätzlich zum Access Token ein Refresh Token aus |

Ein Client darf nur die Scopes anfordern, die in seinen eigenen `AllowedScopes` stehen. `/connect/authorize` lehnt einen Scope, der in dieser Liste fehlt, mit `invalid_scope` ab, statt ihn herauszufiltern. Wer also `roles` in den Request einer Anwendung aufnimmt, ohne ihn auch beim Client einzutragen, bricht jeden Login.

## Benutzerdefinierte Scopes {#custom-scopes}

Benutzerdefinierte Scopes werden über die Admin-API unter `/api/v1/scopes` verwaltet. Sie erfordern ein JWT Access Token mit dem Scope `authagonal-admin` (konfigurierbar über `AdminApi:Scope`).

### Scope-Modell {#scope-model}

```csharp
public sealed class Scope
{
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public bool Emphasize { get; set; }
    public string? Group { get; set; }
    public bool Required { get; set; }
    public bool ShowInDiscoveryDocument { get; set; } = true;
    public List<string> AllowedRoles { get; set; } = [];
    public List<string> UserClaims { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
```

| Feld | Beschreibung |
|---|---|
| `Name` | Der Bezeichner des Scopes, der in Token-Requests gesendet wird (z. B. `billing.read`) |
| `DisplayName` | Lesbarer Name, der auf dem Zustimmungsbildschirm angezeigt wird |
| `Description` | Ausführlichere Beschreibung, die auf dem Zustimmungsbildschirm angezeigt wird |
| `Emphasize` | Bei `true` hebt der Zustimmungsbildschirm diesen Scope als sensibel hervor |
| `Group` | Überschrift auf dem Zustimmungsbildschirm, unter der dieser Scope einsortiert wird. Rein für die Darstellung: Sie beeinflusst nie, was gewährt wird |
| `Required` | Bei `true` kann der Benutzer diesen Scope bei der Zustimmung nicht abwählen |
| `ShowInDiscoveryDocument` | Bei `true` erscheint der Scope in `/.well-known/openid-configuration` unter `scopes_supported` |
| `AllowedRoles` | Rollen, die ein Benutzer haben muss, damit ihm dieser Scope gewährt wird. Leer (der Standard) bedeutet keine Einschränkung, siehe [Rollengebundene Scopes](#role-gated-scopes) |
| `UserClaims` | Allowlist der Claim-Namen benutzerdefinierter Attribute, die in Tokens aufgenommen werden, wenn dieser Scope gewährt wird. Reservierte Protokoll-Claims (etwa `org_id`) werden auf diesem Weg nie ausgegeben, sodass ein gespeichertes Attribut sie nicht fälschen kann |

### Rollengebundene Scopes {#role-gated-scopes}

Die `AllowedScopes` eines Clients beantworten die Frage *darf diese Anwendung diesen Scope anfordern*, die bereits
entschieden ist, bevor sich überhaupt jemand angemeldet hat. `AllowedRoles` beantwortet die andere Hälfte: *darf
diese Person ihn haben*. Beide Prüfungen greifen, und keine ersetzt die andere.

```json
{
  "name": "staff-admin",
  "displayName": "Staff administration",
  "allowedRoles": ["staff", "super-admin"]
}
```

Hat ein Benutzer keine der aufgeführten Rollen, wird der Scope **aus dem Grant entfernt**, nicht abgelehnt: Der
Client hat seinen vollständigen Satz angefordert und erfährt über den in der Token-Antwort zurückgegebenen `scope`
(RFC 6749 §3.3), dass er weniger erhalten hat. Genau das erlaubt es einer einzigen Anwendung, sowohl Mitarbeitende
als auch alle anderen zu bedienen: Der Mitarbeiterbereich ist ein Scope unter mehreren, und nur die dazu
berechtigten Personen erhalten ihn.

Ein Request, bei dem *jeder* angeforderte Scope entfernt wird, schlägt mit `access_denied` fehl, weil nichts übrig
bleibt, wofür ein Token ausgestellt werden könnte.

Die Prüfung greift überall, wo ein Token für einen Menschen ausgestellt wird:

| Ablauf | Wo sie greift |
|---|---|
| Authorization Code | An `/connect/authorize`, sobald der Benutzer bekannt ist, und **vor** der Zustimmung, damit der Bildschirm nie eine Berechtigung anbietet, die nicht gewährt werden kann |
| Device Code | An `/api/auth/device/approve`, der ersten Stelle in diesem Ablauf, an der das Subjekt bekannt ist |
| Refresh | Bei jeder Rotation, gegen frisch ermittelte Rollen. Hier wird der Entzug einer Rolle tatsächlich wirksam, da der Grant weiterhin festhält, was beim Login genehmigt wurde |
| Token Exchange | Nicht gesondert geprüft: Ein Austausch darf den Scope nur innerhalb der eigenen Scopes des Subject Tokens einschränken und kann deshalb nie einen Scope erreichen, der dem Subjekt nicht gewährt wurde |

Client-Credentials-Grants haben kein Subjekt und bleiben bewusst unberührt: Die Befugnis eines Maschinen-Clients
ist seine Registrierung.

Ein Scope aus der Seed-Konfiguration kann `AllowedRoles` hinzufügen oder ändern, aber nicht leeren
(wie bei `UserClaims` behält ein weggelassenes Feld den gespeicherten Wert). Um eine Rollenbindung zu entfernen,
senden Sie den Scope per `PUT` mit einem explizit leeren Array.

## Anlegen per Seed-Konfiguration {#seeding-from-configuration}

Scopes können im Konfigurationsabschnitt `Scopes` deklariert werden. Sie werden beim Start in den Scope-Speicher geschrieben, zusammen mit der [Seed-Konfiguration der Clients](configuration#clients).

```json
{
  "Scopes": [
    {
      "Name": "billing.read",
      "DisplayName": "Billing (read-only)",
      "Description": "View invoices and payment history",
      "UserClaims": ["billing_plan"],
      "ShowInDiscoveryDocument": true,
      "Emphasize": false,
      "Group": "Billing",
      "Required": false,
      "AllowedRoles": ["finance"]
    }
  ]
}
```

| Feld | Beschreibung |
|---|---|
| `Name` | Pflichtfeld. Ein Eintrag ohne Namen wird mit einer Warnung übersprungen |
| `DisplayName`, `Description`, `UserClaims`, `ShowInDiscoveryDocument`, `Emphasize`, `Group`, `Required`, `AllowedRoles` | Wie im [Scope-Modell](#scope-model) |

Das Anlegen per Seed-Konfiguration ist ein Upsert anhand von `Name`. Ein gesetztes Feld setzt sich bei jedem Start gegen den gespeicherten Wert durch; eine Änderung über die Admin-API an einem Feld, das auch die Seed-Konfiguration setzt, wird also beim nächsten Start überschrieben. Ein weggelassenes Feld behält den gespeicherten Wert (bzw. den Standardwert des Modells bei einem neuen Scope). Weil Weglassen "beibehalten" bedeutet, kann die Konfiguration `UserClaims` und `AllowedRoles` hinzufügen oder ändern, aber nicht leeren: Dafür verwenden Sie `PUT /api/v1/scopes/{name}` mit einem explizit leeren Array.

## Admin-Endpunkte {#admin-endpoints}

### Scopes auflisten {#list-scopes}

```
GET /api/v1/scopes
```

Gibt `{ "scopes": [ ... ] }` zurück.

### Scope abrufen {#get-scope}

```
GET /api/v1/scopes/{name}
```

Gibt den Scope zurück oder `404`, wenn er nicht gefunden wird.

### Scope anlegen {#create-scope}

```
POST /api/v1/scopes
Content-Type: application/json

{
  "name": "billing.read",
  "displayName": "Billing (read-only)",
  "description": "View invoices and payment history",
  "emphasize": false,
  "required": false,
  "showInDiscoveryDocument": true,
  "userClaims": ["billing_plan"]
}
```

Gibt `201 Created` mit dem Scope zurück. Gibt `400` (`invalid_request`) zurück, wenn `name` fehlt oder Leerzeichen enthält, und `409` (`scope_exists`), wenn bereits ein Scope mit demselben Namen existiert.

### Scope aktualisieren {#update-scope}

```
PUT /api/v1/scopes/{name}
Content-Type: application/json

{
  "displayName": "Billing (read)",
  "description": "View invoices",
  "emphasize": true
}
```

Nur die mitgesendeten Felder werden aktualisiert; weggelassene Felder behalten ihren aktuellen Wert.

### Scope löschen {#delete-scope}

```
DELETE /api/v1/scopes/{name}
```

Gibt `204 No Content` zurück (`404`, wenn der Scope nicht existiert). Bereits ausgestellte Tokens, die diesen Scope enthalten, bleiben bis zu ihrem Ablauf gültig; widerrufen Sie sie bei Bedarf explizit über `/connect/revocation`.

## Discovery-Dokument {#discovery-document}

Scopes mit `ShowInDiscoveryDocument = true` erscheinen unter `scopes_supported` in `/.well-known/openid-configuration`. Die sieben integrierten Scopes werden immer angekündigt.

```json
{
  "scopes_supported": ["openid", "profile", "email", "phone", "roles", "groups", "offline_access", "billing.read"]
}
```

## Zustimmungsbildschirm {#consent-screen}

Fordert ein Client einen Scope an, der nicht in seiner Liste der zustimmungsfreien Scopes steht, führt die Zustimmungsseite jeden angeforderten Scope mit seinem `DisplayName` auf (ersatzweise mit `Name`), darunter die `Description`. Scopes mit `Emphasize = true` werden optisch gesondert dargestellt. `Required`-Scopes können nicht abgewählt werden.

Den Ablauf aus Benutzersicht beschreibt [OAuth-Zustimmungsbildschirm](index#key-features).

## Dynamische Client-Registrierung {#dynamic-client-registration}

Clients, die über die [dynamische Client-Registrierung](client-registration) registriert werden, dürfen nur die integrierten OIDC-Scopes (`openid`, `profile`, `email`, `phone`, `offline_access`) deklarieren sowie jeden Scope, der in `Auth:DynamicClientRegistrationScopes` genannt ist. Dass ein Scope im Speicher existiert, berechtigt einen selbst registrierten Client noch nicht, ihn zu deklarieren, und rollengebundene Scopes (solche mit `AllowedRoles`) sind nie registrierbar. Alles andere wird mit `invalid_scope` abgelehnt.
