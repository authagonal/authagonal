---
layout: default
title: Migration
locale: de
---

# Migration von Duende IdentityServer

Das Paket `Authagonal.Migration` führt eine einmalige Migration von Duende IdentityServer + SQL
Server in die Speicher von Authagonal durch. Dieselbe Engine steht auf zwei Wegen zur Verfügung:

- **Gehosteter Runner** (empfohlen): ein Hintergrunddienst in Ihrem Authagonal-Host, der die
  Migration bei der Bereitstellung einmal ausführt, abhängig von der Cluster-Leader-Rolle und ohne den Start zu blockieren.
- **CLI**: `tools/Authagonal.Migration.Cli`, für lokale bzw. Offline-Läufe gegen ein Table-Storage-Ziel.

SqlClient ist nur in diesem Paket enthalten, sodass Hosts, die nicht migrieren, es nie mitbekommen.

## Gehosteter Runner {#hosted-runner}

Fügen Sie ihn nach `AddAuthagonal` hinzu (er hängt von den Speichern, dem Secret-Provider und der Cluster-Leader-Rolle ab):

```csharp
builder.Services.AddAuthagonal(builder.Configuration, c => c.UseAzureStorage(blob, table));
builder.Services.AddAuthagonalDuendeMigration(builder.Configuration);

var app = builder.Build();
app.MapAuthagonalEndpoints();
app.MapAuthagonalDuendeMigration();   // GET /admin/migration/status
```

Der zweite `Map`-Aufruf ist erforderlich und eigenständig: Dieses Paket referenziert `Authagonal.Server`,
daher kann `MapAuthagonalEndpoints` es nicht erreichen. Ohne ihn antwortet `GET /admin/migration/status` mit 404,
was sich nicht von einer Ablehnung durch die Richtlinie `IdentityAdmin` unterscheiden lässt, und der Lauf protokolliert
beim Start eine entsprechende Warnung.

Konfiguriert wird über den Abschnitt `Migration`:

```json
{
  "Migration": {
    "Enabled": true,
    "DryRun": false,
    "Version": "1",
    "UsersMode": "CreateOnly",
    "MigrateClients": true,
    "MigrateRefreshTokens": false,
    "LeaseWaitMinutes": 10,
    "StartupDelaySeconds": 30,
    "Source": { "ConnectionString": "Server=...;Database=Identity;..." }
  }
}
```

Der Runner:

1. Wartet `StartupDelaySeconds` (die Seed-Dienste werden zuerst fertig; der Start wird nie blockiert).
2. Überspringt den Lauf, wenn für `Version` bereits eine Markierung `Completed` ohne `DryRun` existiert.
3. Wartet bis zu `LeaseWaitMinutes`, um Cluster-Leader zu werden (nur ein Pod führt die Migration aus).
4. Schreibt eine Markierung `Started`, führt die Engine aus und schreibt dann eine Markierung `Completed`/`Failed` mit dem Bericht.

Verliert der Runner während des Laufs die Leader-Rolle, wird die Engine abgebrochen; der neue Leader führt sie erneut aus.
Das ist sicher, weil jeder Durchlauf idempotent ist. Den Fortschritt prüfen Sie unter `GET /admin/migration/status` (geschützt durch die Richtlinie `IdentityAdmin`).

## CLI {#cli}

```bash
docker run authagonal-migration \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  --DryRun true --UsersMode CreateOnly
```

(Kein Trennzeichen `--` nach dem Image-Namen.) Oder aus dem Quellcode:

```bash
dotnet run --project tools/Authagonal.Migration.Cli -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  --DryRun true
```

## Was migriert wird {#what-gets-migrated}

| Quelle (SQL Server) | Ziel | Hinweise |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Benutzer + E-Mail-/Namensindizes | IDs werden unverändert übernommen. Zusammenführung der Claims: `given_name`→FirstName, `family_name`→LastName, `company`→CompanyName, `org_id`→OrganizationId (auch die xmlsoap-Varianten); E-Mail-Claims werden verworfen; alles Übrige → benutzerdefinierte Attribute. Fehlende Passwort-Hashes (Benutzer nur mit externem SSO) sind kein Problem. BCrypt- und ASP.NET-Identity-V3-Hashes werden unverändert geprüft und bei der nächsten Anmeldung auf natives PBKDF2 umgestellt. |
| `AspNetUserLogins` | UserLogins | `409 Conflict` = überspringen (idempotent) |
| `AspNetRoles` + `AspNetUserRoles` | Rollen + Rollenzuordnungen der Benutzer | Eine Zuordnung von Rollen-ID zu Name löst die Zuweisungen der Benutzer auf |
| `ApiScopes` + `IdentityResources` | Scopes | Bereits vorhandene (per Seed-Konfiguration angelegte) Namen werden übersprungen; Scope-Claims werden kopiert |
| Duende `Clients` + untergeordnete Tabellen | Clients | Secrets werden nach Digest-Länge als `SHA256$`/`SHA512$` gekennzeichnet (andere werden mit einer Warnung verworfen); abgelaufene Secrets werden übersprungen; per Seed-Konfiguration angelegte Clients haben Vorrang (übersprungen) |
| Duende `ApiResources` | (abgeflacht) | Audiences → von der Migration erzeugte Clients; Ressourcen-Claims → von der Migration erzeugte Scopes |
| `SamlProviderConfigurations` | SamlProviders + SsoDomains | Die CSV `AllowedDomains` wird in SSO-Domaineinträge aufgeteilt |
| `OidcProviderConfigurations` | OidcProviders + SsoDomains | Dieselbe Aufteilung der Domains |
| `AspNetUserTokens` (`AuthenticatorKey`, `RecoveryCodes`) | MfaCredentials | TOTP-Secret base32→geschützt (`duende-totp`); Wiederherstellungscodes gehasht (`duende-rc-{n}`); Benutzer wird übersprungen, wenn bereits MFA vorhanden ist |
| Duende `PersistedGrants` (Refresh Tokens) | Grants | **Mit einem unveränderten Duende nicht möglich**, siehe unten. Erfordert `MigrateRefreshTokens` *und* `SourceGrantKeysAreUnhashed`; andernfalls mit einer Warnung übersprungen, und die Benutzer melden sich neu an. |

## Optionen {#options}

| Option | Standard | Beschreibung |
|---|---|---|
| `Enabled` | `false` | Hauptschalter für den gehosteten Runner |
| `DryRun` | `false` | Durchläuft die Quelle und erstellt den vollständigen Prüfbericht (Zeichensatz und Länge der IDs, doppelte E-Mail-Adressen, Bestand an Tabellen und Spalten, Zählungen pro Durchlauf), ohne zu schreiben |
| `Version` | `"1"` | Markierung des Laufs. Erhöhen Sie sie, um einen Delta-Durchlauf erneut auszuführen. Nur eine Markierung `Completed` ohne `DryRun` verhindert einen erneuten Lauf |
| `UsersMode` | `CreateOnly` | `CreateOnly` überspringt vorhandene Benutzer; `Upsert` überschreibt sie. **Nach der Umstellung nie `Upsert` verwenden**, es überschreibt neu gehashte Passwörter und neu eingerichtete MFA |
| `MigrateClients` | `true` | OAuth-Clients migrieren. Per Seed-Konfiguration angelegte Clients haben immer Vorrang, vorhandene Clients werden übersprungen |
| `MigrateRefreshTokens` | `false` | Aktive Refresh Tokens einbeziehen. Erfordert `SourceGrantKeysAreUnhashed` |
| `SourceGrantKeysAreUnhashed` | `false` | Sichert zu, dass `PersistedGrants.Key` in der Quelle die Handles unverändert enthält. Trifft nur auf einen Fork mit eigenem Grant-Speicher zu |
| `Source:ConnectionString` | *(keiner)* | Verbindung zum SQL Server mit der Duende-Quelle |
| `MaxDegreeOfParallelism` | `32` | Begrenzte Schreibparallelität für die Durchläufe mit hohem Volumen (Benutzer, externe Logins, MFA, Refresh Tokens). Senken Sie den Wert für kleine oder zur Drosselung neigende Konten; `1` ist vollständig sequenziell |
| `LeaseWaitMinutes` | `10` | Gehosteter Runner: Nach dieser Zeit wird das Warten auf die Cluster-Leader-Rolle aufgegeben; ein späterer Neustart versucht es erneut |
| `StartupDelaySeconds` | `30` | Gehosteter Runner: Verzögerung vor dem Start, damit die Seed-Dienste fertig werden und der Start nicht blockiert wird |

## Idempotenz und Delta-Durchläufe {#idempotency--delta-sweeps}

Jeder Durchlauf ist idempotent (Überspringen, wenn vorhanden, deterministische MFA-IDs), daher kann die Migration gefahrlos erneut ausgeführt werden.
Führen Sie sie einige Tage vor der Umstellung aus und erhöhen Sie dann kurz vor der Umstellung `Version` für einen abschließenden Delta-Durchlauf, der die
seitdem registrierten Benutzer übernimmt. Vorhandene Datensätze werden übersprungen (oder unter `Upsert` aktualisiert), nie dupliziert.

## Was NICHT migriert wird {#what-is-not-migrated}

- **Aktive Refresh Tokens bei einem unveränderten Duende.** Der `DefaultGrantStore` von Duende speichert nie ein
  Handle eines Refresh Tokens: `PersistedGrants.Key` enthält `base64(SHA-256(handle + ":" + grantType))`, und
  das vorgelegte Handle wird beim Nachschlagen erneut gehasht. Das Handle lässt sich daher aus der
  Quelldatenbank nicht wiederherstellen, und migrierte Zeilen wären dauerhaft nicht einlösbar. Das ist schlimmer, als nicht zu
  migrieren, weil der Bericht sie als erzeugt zählt und der Fehler erst bei der ersten
  Token-Erneuerung nach der Umstellung sichtbar wird. Planen Sie die Umstellung mit einer erneuten Anmeldung ein oder betreiben Sie während
  des Übergangszeitraums eine Zwischenschicht, die aus beiden Systemen liest. `SourceGrantKeysAreUnhashed` existiert nur für einen Fork, dessen Grant-Speicher
  Handles unverändert speichert, und ein solcher Fork ist auch dafür verantwortlich, `PersistedGrants.Data` aus
  der `RefreshToken`-Struktur von Duende in `RefreshTokenData` zu übersetzen.
- **SCIM-Tokens und -Gruppen**, **Benutzer-Provisionierungen**: kein Gegenstück in Duende; sie beginnen leer.
- **Signaturschlüssel**: nicht automatisiert. Damit bestehende Tokens über die Umstellung hinweg gültig bleiben, exportieren Sie den RSA-
  Signaturschlüssel aus Duende und importieren ihn kurz vor der Umstellung in die Tabelle `SigningKeys`.

## Strategie für die Umstellung {#cutover-strategy}

1. Unsichtbar bereitstellen (`Enabled=false`).
2. `Enabled=true, DryRun=true` → Neustart → den Bericht unter `/admin/migration/status` prüfen.
3. `DryRun=false` → Neustart → prüfen, dass die Markierung `Completed` ist, und Anmeldungen stichprobenartig testen.
4. `Version` für den abschließenden Delta-Durchlauf erhöhen, dann Clients und BFFs auf Authagonal umstellen. **Rechnen Sie mit einer
   erzwungenen erneuten Anmeldung**, siehe oben.
5. Überwachen; Rollback = zurück auf die unveränderte Duende-Bereitstellung umstellen.

## NDJSON-Benutzerimport {#ndjson-user-import}

Eine zweite, unabhängige Importquelle im selben Paket `Authagonal.Migration`: eine flache NDJSON-Datei
(ein JSON-Objekt pro Zeile) statt einer Live-Datenbankverbindung, und nur Benutzer, keine Clients, Rollen,
Scopes oder Föderationskonfiguration. Gedacht für die Migration der eigenen Benutzertabelle einer Altanwendung (ein selbst gebauter
ASP.NET-Identity-Speicher, eine nach bcrypt exportierte Rails/Devise-Tabelle, eine Node-App mit scrypt, ...), damit Benutzer
sich weiter mit ihrem alten Passwort anmelden können, während es bei ihrer nächsten erfolgreichen Anmeldung transparent auf natives PBKDF2 neu gehasht wird.
Das ist derselbe Weg des verzögerten Neu-Hashens, auf den sich der Duende-Import oben stützt.

### Datensatzschema {#record-schema}

Ein JSON-Objekt pro Zeile. `email` ist das einzige Pflichtfeld; alle anderen Felder sind optional. **Unbekannte
Felder auf oberster Ebene lassen die Zeile fehlschlagen** (standardmäßig strikt), sofern nicht `--AllowUnknownFields true` übergeben wird.

| Feld | Typ | Hinweise |
|---|---|---|
| `email` | string | Pflicht. Muss eine plausible E-Mail-Adresse sein. Schlüssel für Duplikate ohne Beachtung der Groß- und Kleinschreibung. |
| `username` | string | Keine eigene Spalte in `AuthUser`, wird in `CustomAttributes["username"]` gespeichert. |
| `givenName` | string | → `AuthUser.FirstName` |
| `familyName` | string | → `AuthUser.LastName` |
| `displayName` | string | Keine eigene Spalte, wird in `CustomAttributes["displayName"]` gespeichert. |
| `emailVerified` | bool | → `AuthUser.EmailConfirmed`. Fehlt es, gilt `false`. |
| `passwordHash` | string | → `AuthUser.PasswordHash`, wird **unverändert** gespeichert. Jedes Format, das `PasswordHasher` bei der Anmeldung erkennt (bcrypt `$2a$`/`$2b$`/`$2x$`/`$2y$`, ASP.NET Identity V3, scrypt `$s2$`), wird unverändert geprüft und von dort auf natives PBKDF2 umgestellt. Über "nicht leer" hinaus wird nichts geprüft; ein fehlerhafter Hash lässt sich bei der Anmeldung einfach nicht verifizieren, genau wie außerhalb einer Migration. Lassen Sie es bei Benutzern nur mit SSO oder ohne Passwort weg. |
| `roles` | string[] | → `AuthUser.Roles` |
| `organizationId` | string | → `AuthUser.OrganizationId` |
| `attributes` | object (string→string) | Wird in `AuthUser.CustomAttributes` zusammengeführt |
| `phoneNumber` | string | → `AuthUser.Phone` |
| `disabled` | bool | → `AuthUser.IsActive = !disabled`. Fehlt es, ist der Benutzer aktiv. |
| `createdAt` | string (ISO 8601) | → `AuthUser.CreatedAt`. Fehlt es, gilt der Importzeitpunkt. |
| `externalId` | string | → `AuthUser.ExternalId`, dasselbe Feld, das der Duende-Import mit der Benutzer-ID der Quelldatenbank belegt. |

Beispieldatei (5 Zeilen):

```ndjson
{"email":"ada.lovelace@legacy.example.com","givenName":"Ada","familyName":"Lovelace","passwordHash":"$2b$12$KIXQ8N6Qe0m6b6b6b6b6bOQe0m6b6b6b6b6b6b6b6b6b6b6b6b6b6","roles":["admin"],"organizationId":"org-legacy-1","externalId":"42"}
{"email":"bob@legacy.example.com","emailVerified":true,"attributes":{"dept":"eng"},"createdAt":"2019-03-04T00:00:00Z"}
{"email":"carol@legacy.example.com","disabled":true,"phoneNumber":"+61400000000"}
{"email":"dave@legacy.example.com","username":"dave1998","displayName":"Dave K."}
{"email":"erin@legacy.example.com"}
```

### CLI {#cli-1}

```bash
dotnet run --project tools/Authagonal.Migration.Cli -- import-ndjson-users \
    --Input ./users.ndjson \
    --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
    --DryRun true \
    --OnDuplicate skip \
    --BatchSize 500 \
    --AllowUnknownFields false \
    --ContinueOnError false \
    --AllowPlaintextPii true
```

Gleiches Ziel (Azure Table Storage) und gleiche Sperre für PII im Klartext wie bei der Duende-CLI oben: Diese Quelle schreibt
`AuthUser`-Zeilen direkt in Table Storage, ohne einen im Host registrierten `IFieldCipher`/`IIndexTokenizer`. Daher
verweigert sie die Ausführung, solange `--AllowPlaintextPii true` nicht bestätigt, dass im Ziel keines von beiden konfiguriert ist (alternativ
binden Sie `NdjsonUserImportEngine` in den DI-Container des Hosts selbst ein, wo diese Erweiterungspunkte aufgelöst werden).
Anders als bei der Duende-CLI gibt es keine Sperre `--AllowPlaintextSecrets`: Diese Quelle schreibt nie TOTP-Secrets für MFA
oder OAuth-Client-Secrets, sondern nur Profilfelder der Benutzer und einen unverändert gespeicherten Passwort-Hash.

### Optionen {#options-1}

| Option | Standard | Beschreibung |
|---|---|---|
| `--Input` | *(Pflicht)* | Pfad zur NDJSON-Datei |
| `--Target:ConnectionString` | *(Pflicht)* | Verbindungszeichenfolge für Azure Table Storage |
| `--DryRun` | `false` | Jede Zeile parsen und prüfen, Duplikate gegen das Ziel auflösen und den vollständigen Bericht erstellen, ohne etwas zu schreiben |
| `--OnDuplicate` | `skip` | Umgang mit einer Zeile, deren E-Mail-Adresse (ohne Beachtung der Groß- und Kleinschreibung) bereits einem vorhandenen Benutzer entspricht: `skip` (unverändert lassen, idempotent), `update` (die vorhandenen Felder der Zeile in den bestehenden Benutzer übernehmen) oder `fail` (den Lauf sofort abbrechen) |
| `--BatchSize` | `500` | Anzahl der Zeilen zwischen zwei Fortschrittsmeldungen im Log. Kein Mechanismus zum gebündelten Schreiben: `IUserStore` hat keine Massen-API, daher ist jeder Import bzw. jede Aktualisierung weiterhin ein einzelner Aufruf des Speichers |
| `--AllowUnknownFields` | `false` | JSON-Eigenschaften auf oberster Ebene außerhalb des obigen Schemas akzeptieren und ignorieren, statt die Zeile fehlschlagen zu lassen |
| `--ContinueOnError` | `false` | Mit 0 beenden, auch wenn eine oder mehrere Zeilen beim Parsen oder Prüfen fehlgeschlagen sind. Gilt nicht für `--OnDuplicate fail`, das den Lauf unabhängig von diesem Flag immer abbricht |

### Ausgabe der Zusammenfassung und Exit-Codes {#summary-output--exit-codes}

Der Bericht wird als JSON ausgegeben: `TotalLines`, `Imported`, `Updated`, `Skipped`, `Failed` und die ersten
20 `Failures` (`LineNumber` + `Reason`). Leere Zeilen werden nirgends gezählt. Exit-Codes:

- `0`: Erfolg (oder `--ContinueOnError true` mit einer oder mehreren fehlgeschlagenen Zeilen)
- `1`: Eine oder mehrere Zeilen sind beim Parsen oder Prüfen fehlgeschlagen, und `--ContinueOnError` war nicht gesetzt
- `2`: Der Lauf wurde abgebrochen: `--OnDuplicate fail` ist auf eine vorhandene E-Mail-Adresse gestoßen, oder eine Pflichtoption fehlte

### Idempotenz {#idempotency}

Mit dem Standard `--OnDuplicate skip` bewirkt ein erneuter Lauf mit unveränderter Datei beim zweiten Mal nichts:
Jede Zeile, deren E-Mail-Adresse bereits existiert, wird als übersprungen gezählt, und nichts wird geschrieben. Auch `update` kann gefahrlos
erneut ausgeführt werden (es wendet immer dieselben Felder erneut an); `fail` ist für einen einmaligen Import gedacht, der nie
stillschweigend mit vorhandenen Konten kollidieren darf.

### Was NICHT importiert wird {#what-is-not-imported}

- **Rollen, Scopes, OAuth-Clients, Föderationskonfiguration.** Diese Quelle umfasst nur Benutzer: Wenn Sie auch diese Daten benötigen,
  sehen Sie sich den Duende-Import oben an.
- **MFA-Berechtigungsnachweise, externe Logins.** Nicht Teil des Schemas; fügen Sie sie nach dem Import über die üblichen Abläufe zur
  MFA-Einrichtung bzw. für SSO hinzu.
