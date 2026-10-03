---
layout: default
title: Sicherung und Wiederherstellung
locale: de
---

# Sicherung und Wiederherstellung

Authagonal stellt zwei CLI-Werkzeuge zum Sichern und Wiederherstellen von Daten in Azure Table Storage bereit. Beide sind .NET-Konsolenanwendungen im Verzeichnis `tools/` und beide sind schlanke Hüllen um das NuGet-Paket `Authagonal.Backup`. Hosts, die zeitgesteuerte, mandantenfähige oder nicht dateisystembasierte Sicherungen benötigen, können die Bibliothek direkt verwenden (siehe [Die Bibliothek verwenden](#using-the-library)).

## Sicherung {#backup}

```bash
dotnet run --project tools/Authagonal.Backup -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --output ./backups
```

### Optionen {#options}

| Option | Beschreibung |
|---|---|
| `--connection-string <conn>` | Connection String für Azure Table Storage (oder Umgebungsvariable `STORAGE_CONNECTION_STRING` setzen) |
| `--output <dir>` | Ausgabeverzeichnis (Standard: `./backups`) |
| `--incremental` | Nur Entitäten sichern, die seit der letzten Sicherung geändert wurden |
| `--tables <t1,t2,...>` | Kommagetrennte Liste von Tabellen (Standard: alle Authagonal-Tabellen) |
| `--prefix <prefix>` | Präfix für Tabellennamen (für mandantenfähigen Speicher) |
| `--gzip` | Sicherungsdateien mit gzip komprimieren (`.jsonl.gz`) |
| `--encryption-key <base64>` | 32 Byte langer AES-256-Schlüssel zur Schlüsselverschlüsselung. Verschlüsselt jede Datendatei. Bewahren Sie ihn **außerhalb** des Sicherungsziels auf. Wird auch aus `BACKUP_ENCRYPTION_KEY` gelesen (vorzuziehen; siehe unten). |
| `--manifest-key <base64>` | HMAC-Schlüssel mit mindestens 32 Byte. Signiert das Manifest, sodass die Wiederherstellung nachweisen kann, dass die aufgezeichneten Hashes nicht zusammen mit den Dateien umgeschrieben wurden. Bewahren Sie ihn **außerhalb** des Sicherungsziels auf. Wird auch aus `BACKUP_MANIFEST_KEY` gelesen (vorzuziehen; siehe unten). |
| `--dry-run` | Anzeigen, was gesichert würde, ohne zu schreiben |

### Ausgabeformat {#output-format}

Jede Sicherung legt ein Verzeichnis mit Zeitstempel an:

```
backups/
  20260329-120000/          (full backup)
    Users.jsonl
    Clients.jsonl
    Grants.jsonl
    ...
    _manifest.json
  20260329-180000-incr/     (incremental, compressed)
    Users.jsonl.gz
    _tombstones.jsonl.gz
    _manifest.json
```

Mit `--prefix` werden Sicherungen eine Ebene tiefer unter dem Präfix abgelegt: `backups/acmecorp/20260329-120000/`.
Das verhindert, dass zwei vollständige Sicherungen verschiedener Mandanten, die in derselben Sekunde im selben `--output`-Verzeichnis
landen, kollidieren. Die Sicherungs-ID selbst ist weiterhin ein bloßer Zeitstempel `yyyyMMdd-HHmmss[-incr]` mit
Sekundenauflösung und ohne Präfix. Ohne die Verschachtelung würden zwei Präfixe, die innerhalb derselben Sekunde gesichert werden,
also dieselbe ID und damit dasselbe Verzeichnis erhalten. Richten Sie `--input` auf das verschachtelte Verzeichnis, um daraus
wiederherzustellen (`--input backups/acmecorp/20260329-120000`); Läufe ohne Präfix sind nicht betroffen und behalten das oben gezeigte flache Layout.

Jede `.jsonl`-Datei enthält ein JSON-Objekt pro Zeile (eines pro Tabellenentität). Mit `--gzip` werden die Dateien als `.jsonl.gz` komprimiert. `_manifest.json` hält die Sicherungs-ID, den Zeitstempel, den Modus (`full` oder `incremental`), die Komprimierung, die Watermark für inkrementelle Sicherungen, die Anzahl der Entitäten pro Tabelle, die Anzahl der Tombstones, welche Tabellen (falls vorhanden) über das Änderungsprotokoll gelesen wurden (`ChangeLogTables`; null bedeutet vollständige Abdeckung durch einen Scan) sowie SHA-256-Hashes der Dateien zur Integritätsprüfung fest.

Inkrementelle Sicherungen schreiben zusätzlich eine Datei `_tombstones.jsonl(.gz)`, die Löschungen seit der Watermark festhält: eine Zeile pro gelöschter Zeile mit `Table`, `PartitionKey`, `RowKey` und `DeletedAt`. Die Wiederherstellung spielt diese ab, damit gelöschte Zeilen nicht wiederauferstehen (siehe [Tombstones abspielen](#tombstone-replay)).

Entitätswerte bleiben bei Hin- und Rückweg exakt erhalten: Jede gesicherte Zeile trägt eine Formatmarkierung `"@v"` und für jede Spalte, die JSON nicht eindeutig darstellen kann, eine explizite Annotation `"{column}@odata.type"` (`Edm.Guid`, `Edm.DateTime`, `Edm.Binary`, `Edm.Int64`, `Edm.Double`). Die Wiederherstellung schreibt daher die ursprünglichen Typen zurück, statt in Zeichenketten umgewandelte oder neu abgeleitete Werte.

### Integritätsprüfung {#integrity-verification}

Jedes Sicherungsmanifest enthält ein Dictionary `FileHashes`, das Dateinamen ihren SHA-256-Hashes zuordnet. Bei der Wiederherstellung wird jede Datei gegen ihren aufgezeichneten Hash geprüft (und zwar anhand desselben Lesevorgangs, aus dem die Entitäten übernommen werden, sodass genau die geprüften Bytes geschrieben werden), bevor auch nur ein Teil ihrer Daten eine Tabelle erreicht. Eine Datei, die die Prüfung nicht besteht, eine Datendatei, die im Manifest fehlt, oder eine im Manifest aufgeführte Datei, die im Speicher fehlt, bricht die Wiederherstellung jeweils ab. Sicherungen, die vor Einführung der Integritäts-Hashes geschrieben wurden (ohne `FileHashes`), lassen sich nicht prüfen und werden abgelehnt, sofern nicht `--allow-unverified` angegeben ist. Programmatisch lässt sich die Prüfung über `RestoreOptions.VerifyIntegrity` (Standard `true`) abschalten.

### Schlüssel per Umgebungsvariable übergeben, nicht auf der Befehlszeile {#pass-the-keys-by-environment-variable-not-on-the-command-line}

Beide Werkzeuge lesen `BACKUP_ENCRYPTION_KEY` und `BACKUP_MANIFEST_KEY`, und eine zeitgesteuerte Sicherung sollte diese verwenden.

Ein Flag wird Teil der Befehlszeile des Prozesses. In Kubernetes bedeutet das, dass die CronJob-Spezifikation den base64-kodierten
KEK und den HMAC-Schlüssel wörtlich enthält, sodass jeder mit `get`/`list` auf CronJobs oder Pods in diesem Namespace beide
mit `kubectl get cronjob -o yaml` lesen kann. Das ist ein weit größerer Kreis von Principals als die Inhaber des Secrets, und einer,
der routinemäßig schreibgeschützten Dashboards und CI-Dienstkonten gewährt wird. Dieselben Werte sind in
`/proc/<pid>/cmdline` für jeden Prozess auf dem Knoten sichtbar sowie in jeder Shell-Historie oder jedem CI-Log, in dem der Befehl
zusammengesetzt wurde. `--connection-string` hatte genau aus diesem Grund einen Weg über die Umgebung; die beiden Schlüssel, die
das Archiv schützen, hatten ihn nicht.

```yaml
env:
  - name: BACKUP_ENCRYPTION_KEY
    valueFrom: { secretKeyRef: { name: authagonal-backup, key: encryption-key } }
  - name: BACKUP_MANIFEST_KEY
    valueFrom: { secretKeyRef: { name: authagonal-backup, key: manifest-key } }
```

Sind beide gesetzt, hat das Flag weiterhin Vorrang; eine interaktive einmalige Wiederherstellung braucht also keine Änderung.

Hashes belegen, dass das Archiv zum Manifest passt, nicht aber, dass eines von beiden authentisch ist: Das Manifest liegt auf demselben Ziel wie die Daten, wer also `Clients.jsonl.gz` umschreiben kann, kann auch die Zeile umschreiben, die dessen Hash festhält. `--manifest-key` schließt diese Lücke: Die Sicherung bildet einen HMAC über das Manifest, die Wiederherstellung prüft ihn, und der Schlüssel liegt an einem Ort, den der Schreibprozess der Sicherung nicht erreichen kann. **Die Wiederherstellung schlägt sicher fehl**: Ohne `--manifest-key` verweigert sie den Vorgang, statt nur zu warnen, und `--allow-unauthenticated-manifest` ist die ausdrückliche Ausnahme für Archive, die vor Einführung der Manifest-Signatur geschrieben wurden.

### Inkrementelle Sicherungen {#incremental-backups}

Übergeben Sie `--incremental`, um nur Entitäten zu sichern, die seit der letzten erfolgreichen Sicherung geändert wurden. Das Werkzeug filtert über die eingebaute Eigenschaft `Timestamp` von Azure Table Storage und hält den Höchststand in einer Datei `.lastbackup` im Ausgabeverzeichnis fest.

Existiert keine Datei `.lastbackup`, führt der erste inkrementelle Lauf eine vollständige Sicherung durch.

Jeder inkrementelle `Timestamp`-Filter zieht vor dem Filtern eine kleine Sicherheitsmarge ab (`BackupDefaults.WatermarkSkewMargin`, 5 Minuten). Die Watermark stammt aus der Uhr des Aufrufers, während die Zeitstempel der Zeilen vom Speicherdienst gesetzt werden. Eine Änderung, die innerhalb der Uhrenabweichung festgeschrieben wird, würde sonst von diesem und jedem späteren Lauf übersehen. Das erneute Lesen der Marge kostet ein paar doppelte Zeilen pro Lauf, die durch die Upsert-Semantik der Wiederherstellung dedupliziert werden.

### Standardtabellen {#default-tables}

Das Sicherungswerkzeug umfasst standardmäßig alle Authagonal-Tabellen (`BackupDefaults.Tables`):

`Users`, `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `UserOrganizations`, `Clients`, `Grants`, `GrantsBySubject`, `GrantsByExpiry`, `SigningKeys`, `SsoDomains`, `SamlProviders`, `OidcProviders`, `UpstreamRefreshTokens`, `UserProvisions`, `MfaCredentials`, `MfaChallenges`, `MfaWebAuthnIndex`, `ScimTokens`, `ScimGroups`, `ScimGroupExternalIds`, `ScimGroupRoleMappings`, `Roles`, `UserRoles`, `Scopes`, `AgentProfiles`, `ProvisioningApps`, `Organizations`, `OrganizationSlugs`, `OrganizationMembers`, `UserMemberships`

`AgentProfiles`, `UserRoles` und `UpstreamRefreshTokens` gehören bewusst dazu: Ohne sie wäre ein wiederhergestelltes Deployment unbemerkt schwächer als das gesicherte (Agent-Clients verlieren ihre Obergrenze und ihre Zustimmungsprüfungen, Rollen sind definiert, aber niemand hat sie inne, Refresh Tokens von Upstream-Providern verschwinden).

Kurzlebige Tabellen (`SamlReplayCache`, `OidcStateStore`, `RevokedTokens`) sind standardmäßig ausgenommen, da ihre Einträge durch die Lebensdauer der Tokens begrenzt sind; nehmen Sie sie bei Bedarf ausdrücklich über `--tables` auf. Die Änderungsprotokoll-Tabelle `Tombstones` wird von der Sicherungs-Engine gesondert behandelt und sollte nicht aufgeführt werden.

### Signaturschlüssel sind standardmäßig ausgenommen {#signing-keys-are-excluded-by-default}

Die Tabelle `SigningKeys` steht in der Standardliste der Tabellen, wird aber **standardmäßig aus Sicherungen herausgefiltert** (`BackupOptions.IncludeSigningKeys`, Standard `false`; die CLI aktiviert es nie). Bei Hosts mit der lokalen (in der Tabelle gespeicherten) Schlüsselquelle enthält diese Tabelle den **privaten Schlüssel** für die JWT-Signatur, und ihn in eine Sicherungsdatei im Klartext zu schreiben, würde es jedem, der die Sicherung liest, ermöglichen, Tokens zu fälschen. Das gilt für **jeden** Host: Die JWT-Signatur wird nicht an Vault Transit delegiert, es gibt also keine Konfiguration, in der die Tabelle `SigningKeys` keinen privaten Schlüssel enthält.

> ⚠️ Aktivieren Sie `BackupOptions.IncludeSigningKeys` nur, wenn das Sicherungsziel selbst im Ruhezustand verschlüsselt und zugriffsbeschränkt ist. Dasselbe gilt für den Rest der Sicherung: Mit dem standardmäßigen **Klartext**-Secret-Provider enthalten Sicherungen auch Client-Secrets von Upstream-OIDC-Providern sowie TOTP-/MFA-Seeds im Klartext. Siehe [Konfiguration → Secret-Provider](configuration#secret-provider).

### `--tables` benennt Tabellen aus dem Sicherungsumfang {#--tables-names-tables-from-the-backup-set}

Nur Tabellen aus dem deklarierten Tabellenumfang (`BackupDefaults.Tables` oder `KnownTables` weiter unten) dürfen genannt werden. Eine Tabelle außerhalb davon wird von vornherein abgelehnt,
statt ein Archiv zu erzeugen, das die Wiederherstellung zurückweisen würde. Die Zulassungsliste der Wiederherstellung ist derselbe Umfang, ein Archiv,
das etwas anderes nennt, könnte also geschrieben, gehasht und signiert und danach nie wiederhergestellt werden. Kurzlebige Tabellen (Einträge
widerrufener Tokens, Zähler für Ratenbegrenzung) sind bewusst ausgenommen: Sie laufen von selbst ab, und veraltete Zeilen wiederherzustellen
bringt nichts.

## Wiederherstellung {#restore}

```bash
dotnet run --project tools/Authagonal.Restore -- \
  --connection-string "DefaultEndpointsProtocol=https;..." \
  --input ./backups/20260329-120000
```

### Optionen {#options-1}

| Option | Beschreibung |
|---|---|
| `--connection-string <conn>` | Connection String für Azure Table Storage (oder Umgebungsvariable `STORAGE_CONNECTION_STRING` setzen) |
| `--input <dir>` | Sicherungsverzeichnis, aus dem wiederhergestellt wird |
| `--mode <mode>` | Wiederherstellungsmodus: `upsert` (Standard), `merge` oder `clean` |
| `--tables <t1,t2,...>` | Kommagetrennte Liste der wiederherzustellenden Tabellen (Standard: alle `.jsonl`-/`.jsonl.gz`-Dateien der Sicherung) |
| `--prefix <prefix>` | Präfix für Tabellennamen (für mandantenfähigen Speicher) |
| `--clean-env <env>` | Mit `--mode clean` nur die Zeilen dieser Umgebung löschen (PartitionKey-Präfix `<env>|`) |
| `--allow-clean-from-incremental` | `--mode clean` gegen eine inkrementelle Sicherung erlauben |
| `--allow-clean-all-envs` | `--mode clean` ohne `--clean-env` erlauben, wodurch die gesamte Tabelle geleert wird |
| `--encryption-key <base64>` | Der 32 Byte lange Schlüssel zur Schlüsselverschlüsselung, mit dem die Sicherung geschrieben wurde. Für ein verschlüsseltes Archiv erforderlich. Wird auch aus `BACKUP_ENCRYPTION_KEY` gelesen. |
| `--manifest-key <base64>` | Der HMAC-Schlüssel, mit dem die Sicherung signiert wurde. **Erforderlich**, sofern nicht `--allow-unauthenticated-manifest` angegeben ist. Wird auch aus `BACKUP_MANIFEST_KEY` gelesen. |
| `--allow-unauthenticated-manifest` | Ohne `--manifest-key` wiederherstellen und Hashes akzeptieren, die Beschädigung erkennen, Manipulation aber nicht |
| `--allow-unverified` | Eine Sicherung wiederherstellen, deren Manifest überhaupt keine Datei-Hashes enthält |
| `--dry-run` | Anzeigen, was wiederhergestellt würde, ohne zu schreiben |

### Wiederherstellungsmodi {#restore-modes}

| Modus | Verhalten |
|---|---|
| `upsert` | Jede Entität einfügen oder ersetzen. Vorhandene Daten werden überschrieben. |
| `merge` | Einfügen oder zusammenführen. Vorhandene Eigenschaften, die nicht in der Sicherung stehen, bleiben erhalten. |
| `clean` | Vor der Wiederherstellung alle vorhandenen Daten jeder Tabelle löschen. |

Mit gzip komprimierte Sicherungsdateien (`.jsonl.gz`) werden automatisch erkannt und entpackt; zusätzliche Flags sind nicht nötig.

### Tombstones abspielen {#tombstone-replay}

Nach den Datendateien wendet die Wiederherstellung die Datei `_tombstones` der Sicherung an: Jeder aufgezeichnete Schlüssel wird aus den wiederhergestellten Tabellen gelöscht (`RestoreOptions.ApplyTombstones`, Standard `true`). Die Löschungen einer inkrementellen Sicherung gehören ebenso zu ihrem Zustand wie ihre Upserts; sie zu überspringen, würde beim Wiederherstellen einer Folge aus vollständiger und inkrementellen Sicherungen gelöschte Zeilen wiederauferstehen lassen, auch solche, die nach DSGVO gelöscht wurden. Vollständige Sicherungen haben keine Tombstone-Datei. Wenn Sie eine vollständige Sicherung gefolgt von inkrementellen wiederherstellen, wenden Sie diese von der ältesten an, damit eine spätere Neuanlage nach einer früheren Löschung landet. Der Hash der Tombstone-Datei wird wie bei den Datendateien gegen das Manifest geprüft.

### Exakte Typerhaltung {#exact-type-round-trip}

Zeilen, die mit der Formatmarkierung `"@v"` geschrieben wurden, tragen explizite EDM-Typannotationen, sodass die Wiederherstellung die exakten ursprünglichen Spaltentypen rekonstruiert (`Int64`, `Guid`, `Binary`, `DateTime`, `Double`); eine Zeichenkette ohne Annotation wird als Zeichenkette wiederhergestellt. Ältere Sicherungsdateien ohne die Markierung greifen auf eine formbasierte Ableitung zurück, die nur beibehalten wird, damit alte Sicherungen wiederherstellbar bleiben (die Ableitung kann Zeichenkettenspalten, die wie GUIDs oder Datumswerte aussehen, falsch typisieren).

### Exit-Codes {#exit-codes}

| Code | Bedeutung |
|---|---|
| `0` | Erfolg |
| `1` | Fehler (fehlende Argumente, ungültige Eingabe) |
| `2` | Teilweiser Erfolg (bei einigen Entitäten traten Fehler auf) |

### Ein Host mit eigenen Tabellen: `KnownTables` {#a-host-with-its-own-tables-knowntables}

`BackupOptions.KnownTables` und `RestoreOptions.KnownTables` (beide `string[]?`, null bedeutet `BackupDefaults.Tables`) deklarieren den Umfang der Tabellen, die ein Archiv Ihres Deployments legitimerweise nennen darf. Ein Host, der eigene Daten neben denen von Authagonal speichert und beides als ein Archiv sichert, setzt ihn; andernfalls wird jede Sicherung, die diese Tabellen nennt, von vornherein abgelehnt (`BackupService.cs:48`), und jede Wiederherstellung weist das Archiv zurück (`RestoreService.cs:17,167`).

- Der Host deklariert den Umfang im Voraus. Er wird nie aus dem Archiv abgeleitet, und genau darum geht es: Ein Archiv darf nicht bestimmen, welche Tabellen eine Wiederherstellung schreibt.
- Übergeben Sie beiden Optionen **denselben** Umfang. Eine mit einem größeren Umfang erstellte Sicherung lässt sich nur über eine Wiederherstellung einspielen, die denselben Umfang deklariert.

## Die Bibliothek verwenden {#using-the-library}

Das NuGet-Paket `Authagonal.Backup` stellt dieselben Operationen programmatisch bereit, für Hintergrunddienste oder eigene Orchestrierung:

| Typ | Zweck |
|---|---|
| `BackupService` | Führt eine vollständige oder inkrementelle Sicherung gegen einen `TableServiceClient` aus und schreibt in ein `IBackupTarget` |
| `RestoreService` | Prüft die Hashes und schreibt eine Sicherung zurück in Table Storage |
| `MergeService` | Führt eine vollständige Sicherung plus inkrementelle (samt ihrer Tombstones) als Datenstrom zu einer Sicht des aktuellen Zustands zusammen |
| `RollupService` | Fasst inkrementelle Sicherungen zu einer neuen vollständigen Sicherung zusammen und löscht optional die Eingaben |
| `BackupOptions` / `RestoreOptions` | Konfiguration pro Lauf |
| `BackupDefaults` | Standardliste der Tabellen und Voreinstellungen für das Änderungsprotokoll |
| `IBackupSource` / `IBackupTarget` | Speicherabstraktionen; `FileSystemBackupSource` / `FileSystemBackupTarget` sind die eingebauten Implementierungen. Implementieren Sie `IBackupTarget`, um in Blob Storage oder anderswohin zu schreiben. |

```csharp
var serviceClient = new TableServiceClient(connectionString);
var target = new FileSystemBackupTarget("./backups");
var options = new BackupOptions { Incremental = true, Gzip = true };
var manifest = await new BackupService(serviceClient, target, options).RunAsync(ct);
```

### Inkrementelle Sicherungen über das Änderungsprotokoll {#change-log-driven-incrementals}

Azure Table Storage indiziert nur `PartitionKey` und `RowKey`, sodass eine auf `Timestamp` gefilterte inkrementelle Sicherung dennoch jede Tabelle vollständig scannt. Um das zu vermeiden, zeichnen die Stores von Authagonal jede Änderung über den Erweiterungspunkt `IChangeWriter` (`Authagonal.Core`) in einem Änderungsprotokoll auf; für Azure implementiert ihn `TableChangeWriter` (`Authagonal.AzureProvider`). Es handelt sich um eine physische Tabelle, die weiterhin `Tombstones` heißt: PK = der logische Tabellenname, RK = `"{pk}|{rk}"`, eine Spalte `Op` mit `"U"` (Upsert) oder `"D"` (Löschung) und maßgebliche Spalten `OrigPK`/`OrigRK` (ein `|` im ursprünglichen PartitionKey macht das Aufteilen des zusammengesetzten RowKey mehrdeutig; der Sicherungsleser vertraut daher den Spalten und greift nur bei älteren Zeilen auf das Aufteilen zurück). Jeder Schlüssel hat genau eine Zeile (Upsert mit Ersetzen), sodass die letzte Operation innerhalb eines Sicherungsfensters gewinnt.

Bei aktiviertem Änderungsprotokoll zählt eine inkrementelle Sicherung die Einträge mit `Op = "U"` einer Tabelle seit der Watermark auf und liest jede aktuelle Zeile gezielt, statt die Tabelle zu scannen. Die Funktion ist **optional und standardmäßig deaktiviert**: Ist `BackupOptions.ChangeLoggedTables` null oder leer, bleibt jede Tabelle beim Scan, der Mechanismus wird also wirkungslos ausgeliefert, bis er bewusst umgeschaltet wird (ein Deployment kann so nicht stillschweigend Zeilen verpassen, die von Code ohne Protokollierung geändert wurden). Zwei Voreinstellungen:

| Voreinstellung | Inhalt |
|---|---|
| `BackupDefaults.ChangeLoggedTables` | Die Tabellen, deren Schreibvorgänge vollständig im Änderungsprotokoll erfasst werden: `UserEmails`, `UserFirstNames`, `UserLastNames`, `UserLogins`, `UserExternalIds`, `UserEmailDomains`, `UserEmailLocalPrefixes`, `UserOrganizations`, `ScimGroupRoleMappings`, `ProvisioningApps`, `Organizations`, `OrganizationSlugs`, `OrganizationMembers`, `UserMemberships` |
| `BackupDefaults.ChangeLoggedTablesWithUsers` | Derselbe Umfang plus `Users`. Schreibvorgänge zum Anmeldestatus von Benutzern werden bewusst nicht erfasst (heißer Pfad, geringer Nutzen), daher ist diese Voreinstellung **nur sicher, wenn Sie zusätzlich den unten beschriebenen absichernden vollständigen Scan ausführen** |

Die Eigenschaft `ChangeLogTables` des Manifests listet auf, welche Tabellen ein Lauf über das Änderungsprotokoll gelesen hat; null oder leer bedeutet, dass der Lauf vollständige Abdeckung durch einen Scan hatte (eine vollständige Sicherung, eine einfache inkrementelle Sicherung per Scan oder ein absichernder Scan).

### Absichernder vollständiger Scan {#full-scan-backstop}

Weil die Erfassung im Änderungsprotokoll Schreibvorgänge verpassen kann (Felder zum Anmeldestatus, Schreibvorgänge außerhalb der Stores, Pods, die während eines Deployments noch Code ohne Protokollierung ausführen), kombinieren Sie inkrementelle Sicherungen über das Änderungsprotokoll mit einem regelmäßigen vollständigen erneuten Scan. Setzen Sie `BackupOptions.WatermarkOverride` auf den Zeitstempel des letzten Scans mit vollständiger Abdeckung und lassen Sie `ChangeLoggedTables` für diesen Lauf ungesetzt: Die inkrementelle Sicherung filtert dann über `Timestamp` das gesamte Fenster seit diesem Scan und erfasst alles, was das Änderungsprotokoll nie aufgezeichnet hat. Ein täglicher absichernder Scan neben stündlichen inkrementellen Sicherungen über das Änderungsprotokoll ist ein sinnvoller Rhythmus. Löschungen sind die einzige Art von Änderung ohne Selbstheilung (ein Scan aktueller Zeilen kann eine Zeile, die nicht mehr existiert, nicht sehen); deshalb schreiben die Stores den Lösch-Tombstone **bevor** sie die Datenzeile löschen.

Alle inkrementellen Filter, einschließlich des absichernden Scans, ziehen `BackupDefaults.WatermarkSkewMargin` (5 Minuten) von der Watermark ab; Aufrufer, die das Änderungsprotokoll nach einer Sicherung bereinigen, müssen die Bereinigung um dieselbe Marge begrenzen, sonst löschen sie Zeilen, die der nächste Lauf noch benötigt.

### Rollups {#rollups}

`RollupService.RollupAsync` führt eine vollständige Sicherung und ihre inkrementellen Sicherungen zu einer neuen vollständigen Sicherung zusammen; `RollupAndCleanAsync` löscht anschließend zusätzlich die Eingaben. Der optionale Parameter `newBackupId` benennt das Ergebnis (null leitet eine ID aus dem Zeitstempel ab); ein gesondert aufbewahrter Snapshot (zum Beispiel ein wöchentliches Rollup) muss hier seine ID übergeben, da die ID-basierte Aufbewahrung physische Sicherungs-IDs auflistet, nicht Manifeste.

Beim Zusammenführen werden Tombstones unter Beachtung der zeitlichen Reihenfolge angewandt: Eine Löschung entfernt eine erfasste Zeile nur, wenn deren `Timestamp` nicht nach dem `DeletedAt` des Tombstones liegt. Ein Schlüssel, der früh im Fenster gelöscht und später neu angelegt wurde, hat sowohl einen Tombstone als auch eine aktuelle Erfassung, und die neu angelegte Zeile übersteht das Rollup. Ältere Tombstones ohne `DeletedAt` löschen bedingungslos.

## Docker {#docker}

Das Sicherungswerkzeug bringt ein Dockerfile mit (`tools/Authagonal.Backup/Dockerfile`), um es in CI oder ohne installiertes .NET SDK auszuführen:

```bash
docker build -f tools/Authagonal.Backup/Dockerfile -t authagonal-backup .

docker run --rm -v $(pwd)/backups:/backups \
  -e STORAGE_CONNECTION_STRING="..." \
  authagonal-backup --output /backups
```

Für das Wiederherstellungswerkzeug gibt es kein Image; führen Sie es mit dem .NET SDK aus (`dotnet run --project tools/Authagonal.Restore`).

## Sicherungen zeitlich planen {#scheduling-backups}

Für den produktiven Einsatz führen Sie das Sicherungswerkzeug zeitgesteuert aus (z. B. täglich vollständig + stündlich inkrementell):

```bash
# Daily full backup (compressed)
0 2 * * * authagonal-backup --connection-string "$CONN" --output /backups --gzip

# Hourly incremental (compressed)
0 * * * * authagonal-backup --connection-string "$CONN" --output /backups --incremental --gzip
```

Hosts, die die Bibliothek einbetten, führen typischerweise stündliche inkrementelle Sicherungen mit aktiviertem Änderungsprotokoll, einen täglichen absichernden vollständigen Scan und regelmäßige Rollups aus, um die Kette inkrementeller Sicherungen zu begrenzen.
