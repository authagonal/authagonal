---
layout: default
title: SCIM-2.0-Provisionierung
nav_order: 13
locale: de
---

# SCIM-2.0-Provisionierung

Authagonal unterstützt SCIM 2.0 (System for Cross-domain Identity Management) für die automatisierte Benutzerprovisionierung aus Enterprise-Identitätsprovidern wie Microsoft Entra ID, Okta und OneLogin.

## Überblick {#overview}

SCIM ist ein Protokoll für die eingehende Provisionierung: Ihr Identitätsprovider überträgt Änderungen an Benutzern und Gruppen an Authagonal. Das ergänzt die bestehende ausgehende TCC-Provisionierung (Try-Confirm-Cancel), die Benutzer in nachgelagerte Anwendungen überträgt.

**Unterstützte Operationen:**
- Benutzer-CRUD (Anlegen, Lesen, Aktualisieren, Löschen per weicher Deaktivierung)
- Gruppen-CRUD mit Mitgliederverwaltung
- Filterung (Operatoren `eq` und `co` auf `userName`, `externalId`, `displayName`)
- Paginierung: cursorbasiert (`cursor`/`nextCursor`) für Benutzer und Gruppen; `startIndex` wird bei Gruppen für bestehende Clients weiterhin akzeptiert, aber nicht ausgewiesen
- PATCH für Teilaktualisierungen (einschließlich Deaktivierung per `active=false`)
- Zuordnung von Gruppen zu Rollen, aufgelöst bei der Token-Ausstellung

**Nicht unterstützt:** Bulk-Operationen, Sortierung, ETags, Passwortverwaltung über SCIM.

Alle Ressourcen sind dem SCIM-Client zugeordnet, der sie provisioniert hat: Ein Benutzer oder eine Gruppe, die über den Client eines SCIM-Tokens angelegt wurde, ist für jeden anderen SCIM-Client unsichtbar (404).

## Ein SCIM-Token erzeugen {#generating-a-scim-token}

SCIM-Endpunkte werden mit statischen Bearer Tokens authentifiziert. Erzeugen Sie Tokens über die Admin-API:

```http
POST /api/v1/scim/tokens
Authorization: Bearer {admin-token}
Content-Type: application/json

{
  "clientId": "your-client-id",
  "description": "Entra ID SCIM token",
  "expiresInDays": 365,
  "organizationId": "org_acme",
  "allowedEmailDomains": ["acme.example", "acme-eu.example"]
}
```

Die Antwort enthält das Token im Rohformat **ein einziges Mal**. Es wird als SHA-256-Hash gespeichert und lässt sich später nicht wiederherstellen; bewahren Sie es daher sicher auf:

```json
{
  "tokenId": "abc123",
  "clientId": "your-client-id",
  "token": "base64-encoded-token",
  "description": "Entra ID SCIM token",
  "createdAt": "2024-01-01T00:00:00Z",
  "expiresAt": "2025-01-01T00:00:00Z",
  "organizationId": "org_acme",
  "allowedEmailDomains": ["acme.example", "acme-eu.example"]
}
```

Lassen Sie `expiresInDays` weg (oder übergeben Sie `0`), um ein Token ohne Ablaufdatum zu erhalten.

### Die Benutzer eines Connectors mit einer Organisation kennzeichnen {#tagging-a-connectors-users-with-an-organization}

`organizationId` ist optional. Ist es gesetzt, wird jeder über dieses Token provisionierte Benutzer mit dieser
`OrganizationId` geschrieben, die in seinen Tokens als Claim `org_id` ausgegeben wird. SCIM bietet einem Connector
keine Möglichkeit mitzuteilen, welchen Ihrer Kunden er synchronisiert: Das SCIM-Kernschema definiert kein
Organisationsattribut, und die Enterprise-Erweiterung ist nicht implementiert (siehe *Schema-Unterstützung* weiter unten).
Die Bindung an den Berechtigungsnachweis beantwortet diese Frage, ohne einen eigenen OAuth-Client pro Kunde zu benötigen.

Lassen Sie es weg, bleiben die Benutzer ohne Kennzeichnung; so verhielt sich jedes Token, bevor es diese Option gab. Aus der
Client-ID wird niemals etwas abgeleitet.

Zwei Regeln:

- **Nur beim Anlegen.** Eine spätere Synchronisierung über ein anders gekennzeichnetes Token kennzeichnet ein bestehendes Konto nicht neu.
- **Die Kennzeichnung geht der Provisionierung voraus.** Eine TCC-Antwort auf `/try` füllt nur eine noch leere Organisation
  (siehe [Provisionierung](provisioning.md)). Eine explizite Bindung am Berechtigungsnachweis hat daher Vorrang, und die Nutzlast
  von `/try` enthält den gebundenen Wert, sodass eine nachgelagerte Anwendung erkennen kann, von welchem Kunden die Synchronisierung stammt.

Wenn `organizationId` eine existierende [Organisation](organizations) benennt, schreibt das Anlegen zusätzlich eine `active`-Mitgliedschaft in dieser Organisation (ohne Rollen) und protokolliert `scim.organization_member_added`. So wird dem Benutzer anschließend nicht von der Mitgliedschaftsprüfung der Organisation ein Token verweigert. Eine ID, die keine Organisation benennt, bleibt eine bloße `org_id`-Kennzeichnung; so verhalten sich Tokens, die vor der Einführung von Organisationen ausgestellt wurden. Wie die Kennzeichnung wird auch die Mitgliedschaft nur beim Anlegen geschrieben.

> **Kennzeichnung ist keine Isolation.** Die Eigentümerschaft wird pro **Client** durchgesetzt, nicht pro Token. Zwei Tokens,
> die für denselben Client ausgestellt wurden, sind eine Identität mit zwei Secrets, und jedes kann lesen, umbenennen, deaktivieren
> und löschen, was das andere angelegt hat. Das ist unproblematisch, solange eine Partei alle hält. Halten einander nicht
> vertrauende Connectors jeweils ein eigenes, geben Sie jedem einen eigenen Client.

### Begrenzen, welche Identitäten ein Connector anlegen darf {#bounding-which-identities-a-connector-may-create}

`allowedEmailDomains` ist die einzige Steuerung darüber, **welche** Benutzer ein SCIM-Berechtigungsnachweis provisionieren kann. Setzen Sie es.

Wenn Sie es weglassen, entsteht ein uneingeschränktes Token, und uneingeschränkt reicht weiter, als es klingt. Ein per SCIM angelegter
Benutzer wird mit `EmailConfirmed = true` geschrieben (die Adresse gilt ab diesem Moment als nachgewiesen). Ein uneingeschränkter
Connector kann also `ceo@some-other-company.example` als vorab verifiziertes Konto anlegen. Meldet sich der tatsächliche Inhaber
später über Föderation an, wird ein Datensatz ohne bestehende externe Anmeldungen übernommen statt abgelehnt, sodass seine Anmeldung an
dieses Konto gebunden wird. Und weil `ScimProvisionedByClientId` weiterhin den Connector benennt, der es angelegt hat, behält dieser
Connector die volle Eigentümerschaft am Objekt: Er kann das Profil lesen, den `userName` umbenennen, das Konto deaktivieren (was jeden
Grant widerruft) oder es löschen. Das Löschen entfernt die Passkeys und Gruppenmitgliedschaften des Benutzers und markiert die Zeile
mit einem Tombstone, sodass der legitime Connector für diese Domain bei jeder Operation 404 erhält.

Für ein Token ohne dieses Feld wird beim Ausstellen eine Warnung mit der Token-ID protokolliert.

Geben Sie reine Domains an (`acme.example`, nicht `@acme.example` und keine Adresse). Ein Wert, der niemals passen kann, wird abgelehnt
statt gespeichert, denn eine Begrenzung, die nichts zulässt, sieht genauso aus wie ein falsch konfigurierter Connector.

Betreiber können eine Begrenzung auch in der Konfiguration setzen:

```json
{
  "Scim": {
    "Clients": {
      "your-client-id": { "AllowedEmailDomains": ["acme.example"] }
    }
  }
}
```

Beide werden **geschnitten**, und eine leere Liste aus einer der beiden Quellen bedeutet „keine Begrenzung aus dieser Quelle“. Sind
beide leer, ist das Token also uneingeschränkt; ist nur eine gesetzt, gilt sie für sich allein; und sind beide gesetzt, sind nur
Domains zulässig, die in beiden stehen: Das Ausstellen eines Tokens kann die konfigurierte Begrenzung eines Betreibers einengen, aber nie erweitern.

Die Begrenzung wird beim Anlegen, bei `PUT` und bei `PATCH` gleichermaßen durchgesetzt, sodass eine Umbenennung ein Konto nicht in eine
Domain verschieben kann, die der Berechtigungsnachweis nicht provisionieren darf.

### Tokens auflisten {#listing-tokens}

```http
GET /api/v1/scim/tokens?clientId=your-client-id
Authorization: Bearer {admin-token}
```

### Ein Token widerrufen {#revoking-a-token}

```http
DELETE /api/v1/scim/tokens/{tokenId}?clientId=your-client-id
Authorization: Bearer {admin-token}
```

## Ihren Identitätsprovider konfigurieren {#configuring-your-identity-provider}

### Mandanten-URL {#tenant-url}

```
https://your-authagonal-instance/scim/v2
```

### Authentifizierung {#authentication}

Verwenden Sie **OAuth Bearer Token** mit dem oben erzeugten Token.

### Microsoft Entra ID {#microsoft-entra-id}

1. Gehen Sie im Azure-Portal zu **Enterprise Applications** > Ihre App > **Provisioning**
2. Setzen Sie Provisioning Mode auf **Automatic**
3. Tragen Sie als Tenant URL `https://your-instance/scim/v2` ein
4. Tragen Sie als Secret Token das Rohtoken aus dem Erzeugungsschritt ein
5. Klicken Sie zur Überprüfung auf **Test Connection**
6. Konfigurieren Sie die Attributzuordnungen (siehe unten)

### Okta {#okta}

1. Gehen Sie in der Okta-Admin-Konsole zu **Applications** > Ihre App > **Provisioning**
2. Aktivieren Sie den **SCIM connector**
3. Setzen Sie die Base URL auf `https://your-instance/scim/v2`
4. Setzen Sie den Authentication Mode auf **HTTP Header**
5. Tragen Sie das Bearer Token ein

### OneLogin {#onelogin}

1. Gehen Sie in der OneLogin-Administration zu **Applications** > Ihre App > **Provisioning**
2. Aktivieren Sie die Provisionierung
3. Setzen Sie die SCIM Base URL auf `https://your-instance/scim/v2`
4. Setzen Sie das SCIM Bearer Token

## SCIM-Endpunkte {#scim-endpoints}

| Methode | Pfad | Beschreibung |
|--------|------|-------------|
| GET | `/scim/v2/Users` | Benutzer auflisten/filtern |
| GET | `/scim/v2/Users/{id}` | Einen Benutzer abrufen |
| POST | `/scim/v2/Users` | Einen Benutzer anlegen |
| PUT | `/scim/v2/Users/{id}` | Einen Benutzer ersetzen |
| PATCH | `/scim/v2/Users/{id}` | Teilaktualisierung |
| DELETE | `/scim/v2/Users/{id}` | Tombstone (deaktiviert; ein späteres GET liefert 404) |
| GET | `/scim/v2/Groups` | Gruppen auflisten/filtern |
| GET | `/scim/v2/Groups/{id}` | Eine Gruppe abrufen |
| POST | `/scim/v2/Groups` | Eine Gruppe anlegen |
| PUT | `/scim/v2/Groups/{id}` | Eine Gruppe ersetzen |
| PATCH | `/scim/v2/Groups/{id}` | Mitglieder hinzufügen/entfernen |
| DELETE | `/scim/v2/Groups/{id}` | Eine Gruppe löschen |
| GET | `/scim/v2/ServiceProviderConfig` | Fähigkeiten |
| GET | `/scim/v2/Schemas` | Schemadefinitionen |
| GET | `/scim/v2/ResourceTypes` | Ressourcentypen |

Jeder Endpunkt ist zusätzlich ohne das Segment `/v2` erreichbar (z. B. `/scim/Users`), für Identitätsprovider, die ihren eigenen Pfad anhängen. Die Discovery-Endpunkte (`ServiceProviderConfig`, `Schemas`, `ResourceTypes` sowie die bloßen Basis-URLs `/scim/` und `/scim/v2/`, die die ServiceProviderConfig zurückgeben) sind anonym zugänglich; alles andere erfordert ein SCIM-Bearer-Token.

Benutzer- und Gruppenendpunkte sind auf 200 Anfragen pro Minute und SCIM-Client begrenzt; darüber hinausgehende Anfragen erhalten einen SCIM-Fehler mit Status `429`.

## Attributzuordnung {#attribute-mapping}

### Benutzerattribute {#user-attributes}

| SCIM-Attribut | Authagonal-Feld |
|---------------|------------------|
| `userName` | `Email` |
| `name.givenName` | `FirstName` |
| `name.familyName` | `LastName` |
| `displayName` | `FirstName LastName` |
| `emails[type eq "work"].value` | `Email` |
| `active` | `IsActive` |
| `externalId` | `ExternalId` |
| `preferredLanguage` (ersatzweise `locale`) | `Locale` |

### Gruppenattribute {#group-attributes}

| SCIM-Attribut | Authagonal-Feld |
|---------------|------------------|
| `displayName` | `DisplayName` |
| `externalId` | `ExternalId` |
| `members` | `MemberUserIds` |

### Schema-Unterstützung {#schema-support}

Nur die SCIM-2.0-Kernschemata `User` und `Group` (RFC 7643). Die Tabellen oben sind der vollständige unterstützte Umfang.

Die **Enterprise-Benutzererweiterung ist nicht implementiert**. `employeeNumber`, `costCenter`, `organization`,
`division`, `department` und `manager` werden daher angenommen und ignoriert statt gespeichert, und zwar beim Anlegen, Ersetzen und
bei PATCH gleichermaßen. Entra und Okta ordnen mehrere davon in ihren Standard-Attributzuordnungen zu, ein unveränderter Connector
muss sie also nicht entfernen. (Vor 0.27.0 wurde ein PATCH, der eines davon enthielt, GANZ mit
`400 invalidPath` abgelehnt. Dadurch schlug jede inkrementelle Synchronisierung fehl, während das Anlegen gelang, und eine
Deprovisionierung per `active: false` konnte an einem unbeteiligten Attribut hängen bleiben.)

Die Lockerung ist eng gefasst: Ein falsch geschriebener KERN-Pfad wie `name.givenNam` liefert weiterhin `400`, und
schreibgeschützte Attribute (`id`, `meta`, `groups`) behalten ihre Ablehnung wegen `mutability`.

Beachten Sie, dass das Enterprise-Attribut `organization` **nicht** zur `org_id` des Benutzers wird. Dieser Wert wird vom eigenen
Identitätsprovider des Kunden behauptet, während die oben beschriebene Bindung am Berechtigungsnachweis vom Betreiber gesetzt wird;
verwenden Sie stattdessen `organizationId` am Token.

## Details zum Verhalten {#behavior-details}

### Anlegen von Benutzern {#user-creation}
- Per SCIM provisionierte Benutzer werden mit `EmailConfirmed = true` angelegt (nur SSO, kein Passwort).
- Das Feld `ScimProvisionedByClientId` hält fest, welcher SCIM-Client den Benutzer angelegt hat.
- Sind für den Client `ProvisioningApps` konfiguriert, wird die TCC-Provisionierung automatisch ausgelöst. Lehnt die Provisionierung den Benutzer ab, wird das Anlegen per SCIM zurückgerollt, und die Antwort ist ein SCIM-`400` mit `scimType: invalidValue` und einer festen Meldung (der eigene Text der nachgelagerten Anwendung wird dem SCIM-Client bewusst nicht weitergegeben).
- Das Anlegen eines Benutzers, dessen `userName` oder `externalId` bereits existiert, liefert einen SCIM-Konflikt `409`. Änderungen der E-Mail-Adresse per PUT oder PATCH werden auf dieselbe Weise auf Konflikte geprüft.

### Deaktivieren von Benutzern {#user-deactivation}
- `DELETE /scim/v2/Users/{id}` versieht die Ressource mit einem **Tombstone**: Es deaktiviert den Benutzer, behält den lokalen Datensatz und setzt `ScimDeletedAt`. Ein anschließendes `GET /scim/v2/Users/{id}` liefert **404**, wie RFC 7644 §3.6 es verlangt („the service provider MUST return a 404 for all operations associated with the previously deleted resource“). Bestätigen Sie eine Deprovisionierung nicht, indem Sie die Ressource zurücklesen und `active: false` erwarten. Das Lesen liefert 404, und genau das ist der Erfolg.
- Der Datensatz wird aufbewahrt statt gelöscht, damit eine Wiedereinstellung neu angelegt werden kann: Der Tombstone gibt den `userName`/die `externalId` frei, die eine neue Ressource benötigt, während das lokale Konto, sein Audit-Verlauf und seine Gruppenmitgliedschaften erhalten bleiben.
- Auch `PATCH` mit `active = false` deaktiviert den Benutzer.
- Deaktivierte Benutzer können sich weder per Passwort noch per SAML oder OIDC anmelden.
- Bei der Deaktivierung werden alle Grants (Refresh Tokens, Sitzungen) widerrufen.
- Die Deprovisionierung nachgelagerter Anwendungen wird nur durch `DELETE` ausgelöst; eine Deaktivierung per `PATCH` widerruft Grants, lässt nachgelagerte Anwendungen aber unberührt.

### Filterung {#filtering}
Die vollständige Filtergrammatik nach RFC 7644 §3.4.2.2 wird unterstützt.

**Operatoren:** `eq`, `ne`, `co`, `sw`, `ew`, `gt`, `ge`, `lt`, `le` und `pr` (Vorhandensein).
**Logisch:** `and`, `or`, `not (...)` mit Gruppierung durch Klammern. `and` bindet stärker als `or`.
**Pfade:** Unterattribute (`name.givenName`), mehrwertige Attribute (`emails.value`), Wertpfade (`emails[type eq "work"].value`) und Namen mit URN-Präfix (`urn:ietf:params:scim:schemas:core:2.0:User:userName`).

```
userName eq "user@example.com"
userName sw "sales-" and active eq true
emails[type eq "work"].value co "@acme.com"
not (title pr)
meta.lastModified gt "2026-01-01T00:00:00Z"
```

Die Semantik folgt dem RFC: Zeichenkettenvergleiche unterscheiden nicht zwischen Groß- und Kleinschreibung, ein mehrwertiges Attribut passt, wenn irgendein Element passt, und ein fehlendes Attribut macht jeden Vergleich außer `ne` falsch. Eingaben, die kein gültiger SCIM-Filter sind, werden mit `400` und `scimType: invalidFilter` abgelehnt, wobei das Problem benannt wird.

**Leistung.** `userName eq` und `externalId eq` (die Abfragen, die Entra und Okta vor jedem Anlegen oder Aktualisieren stellen) werden über indizierte Punktabfragen statt über einen Scan der Auflistung aufgelöst und bleiben daher bei jeder Benutzerzahl schnell. Jeder andere Filter wird beim Durchblättern der Benutzer des Clients ausgewertet, und zwar begrenzt: Personenbezogene Benutzerdaten sind im Ruhezustand verschlüsselt und nur über Blind Indexes durchsuchbar, sodass sich komplexere Prädikate nicht an den Speicher delegieren lassen. Bei Cursor-Paginierung wird `totalResults` **weggelassen**, solange `nextCursor` vorhanden ist, und ist die exakte Gesamtzahl, sobald `nextCursor` fehlt. Siehe Paginierung.

### Paginierung {#pagination}
Benutzerauflistungen verwenden **Cursor-Paginierung**. Jede Seite von `GET /scim/v2/Users` liefert in der Listenantwort eine Eigenschaft `nextCursor`; übergeben Sie sie als `?cursor=` zurück, um die nächste Seite abzurufen. Fehlt `nextCursor`, ist die Auflistung vollständig. Die Seitengröße wird über `count` gesteuert (Standard 100, maximal 200).

Ein `startIndex` größer als 1 am Users-Endpunkt liefert einen `400`-Fehler mit dem Hinweis auf Cursor-Paginierung; Offset-Paginierung über die erste Seite hinaus wird nicht angeboten. `totalResults` wird **vollständig weggelassen**, solange `nextCursor` vorhanden ist, und enthält die exakte Gesamtzahl nur auf der letzten Seite. Es meldet bewusst nicht die Größe der zurückgegebenen Seite: Ein synchronisierender Client, der `totalResults` las, feststellte, dass es der Zahl der gerade erhaltenen Ressourcen entsprach, und daraus schloss, das gesamte Verzeichnis zu haben, las den Mandanten stillschweigend unvollständig. Steuern Sie die Schleife über `nextCursor`, niemals über `totalResults`, und behandeln Sie ein fehlendes `totalResults` als „noch nicht bekannt“, nicht als null.

**Auch Gruppenauflistungen sind cursorbasiert paginiert.** `GET /scim/v2/Groups` liefert sowohl in der gefilterten als auch in der
ungefilterten Form einen `nextCursor`; folgen Sie ihm auf dieselbe Weise. `startIndex` wird bei Groups für Clients, die es bereits verwenden,
weiterhin akzeptiert, aber in `ServiceProviderConfig` **nicht ausgewiesen**, und Sie sollten sich nicht darauf verlassen: `pagination.index` ist eine
Aussage über den Provider, nicht über eine einzelne Collection, und `/Users` unterstützt es nicht. Der einzige Wert, der überall zutrifft, ist
daher `false`. Verwenden Sie Cursor; sie funktionieren bei beiden.

Eine gefilterte Gruppenauflistung scannt in begrenzten Fenstern, statt den gesamten Mandanten zu materialisieren, und kann daher eine
leere Seite liefern, obwohl weiter hinten noch Treffer existieren. In diesem Fall liefert sie einen `nextCursor` und **lässt**
`totalResults` **weg**: Eine leere Seite mit Cursor bedeutet „weitermachen“, eine leere Seite ohne Cursor bedeutet, dass die
gefilterte Menge tatsächlich leer ist. Behandeln Sie die erste leere Seite nicht als Ende der Collection.

`count=0` liefert bei beiden Collections `totalResults` ohne Ressourcen (RFC 7644 §3.4.2.4), und ein negativer
`count` wird mit `400` abgelehnt statt begrenzt.

### Gruppenmitgliedschaft per PATCH {#group-membership-via-patch}
`PATCH /scim/v2/Groups/{id}` akzeptiert die Formen für Mitgliedschaftsänderungen, die die großen Identitätsprovider tatsächlich senden:

- **Mitglieder hinzufügen:** `op: "add"` mit `path: "members"` und einem Werte-Array aus `{ "value": "user-id" }`-Objekten. Duplikate werden ignoriert.
- **Mitglieder ersetzen:** `op: "replace"` mit `path: "members"` ersetzt die gesamte Mitgliedschaft durch das übergebene Array.
- **Ein bestimmtes Mitglied entfernen (Werte-Array):** `op: "remove"` mit `path: "members"` und einem Werte-Array der zu entfernenden Mitglieds-IDs (die Form, die Entra ID sendet).
- **Ein bestimmtes Mitglied entfernen (Pfadfilter):** `op: "remove"` mit `path: 'members[value eq "user-id"]'`, wobei die ID im Pfadfilter steht und kein Wert mitgegeben wird (die Form, die Okta zur Deprovisionierung sendet).
- **Alle Mitglieder entfernen:** `op: "remove"` mit `path: "members"` und ohne Wert leert die Gruppe.

### Zuordnung von Gruppen zu Rollen {#group-to-role-mapping}
Die Mitgliedschaft in einer SCIM-Gruppe kann Anwendungsrollen gewähren. Zuordnungen bestehen aus einer Zeile pro Paar (Gruppe, Rolle), und eine Gruppe kann mehrere Rollen gewähren. Sie werden bei der **Token-Ausstellung** aufgelöst: Die effektiven Rollen eines Benutzers sind seine direkt zugewiesenen Rollen plus die Rollen jeder zugeordneten Gruppe, der er angehört. Das Hinzufügen oder Entfernen eines Gruppenmitglieds wirkt sich also beim nächsten Token aus, ohne den Benutzerdatensatz anzufassen. Ein leerer Zuordnungsspeicher hat keine Wirkung.

Zuordnungen werden über `IScimGroupRoleMappingStore` gespeichert (implementiert von den Azure- und AWS-Speicherprovidern; andernfalls ist ein In-Memory-Standard registriert) und über die Administrationsoberfläche der hostenden Anwendung verwaltet, nicht über die SCIM-API selbst.

Optional erhält ein Client mit aktiviertem `IncludeGroupsInTokens` außerdem die Anzeigenamen der SCIM-Gruppen des Benutzers als Claim `groups` in den ausgestellten Tokens.

## Bekannte Einschränkungen {#known-limitations}

- **Keine Bulk-Operationen:** Benutzer und Gruppen müssen einzeln provisioniert werden.
- **Keine Sortierung:** Benutzerauflistungen liefern bei Cursor-Paginierung die Speicherreihenfolge; Gruppenauflistungen sind nach Erstellungsdatum sortiert.
- **Keine Passwortverwaltung:** Per SCIM provisionierte Benutzer authentifizieren sich nur über SSO.
- **Tombstone statt Löschung:** `DELETE` deaktiviert die Ressource und versieht sie mit einem Tombstone (ein späteres `GET` liefert 404, gemäß RFC 7644 §3.6), statt den lokalen Benutzerdatensatz dauerhaft zu entfernen. Für eine endgültige Löschung verwenden Sie die Admin-API.
