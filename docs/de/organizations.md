---
layout: default
title: Organisationen
nav_order: 14
locale: de
---

# Organisationen

Eine Organisation ist ein Kunde innerhalb Ihres Mandanten. Ein Deployment kann viele davon bedienen: Jede hat ihre eigene Identität, ihre eigenen Mitglieder und ihre eigene `org_id` in den Tokens, die Ihre Anwendungen erhalten.

## Überblick {#overview}

Vor den Organisationen trug ein Benutzerdatensatz einen `OrganizationId`-String (geschrieben von der TCC-Provisionierung oder durch die Bindung eines SCIM-Tokens), und dieser String wurde als Claim `org_id` ausgegeben. Es gab keinen Ort, an dem festgehalten wurde, was die Organisation *war*, wer zu ihr gehörte oder ob man sich überhaupt als sie authentifizieren konnte.

Eine `Organization` gibt ihr einen Datensatz: eine unveränderliche, opake ID, einen unveränderlichen, im Mandanten eindeutigen Slug, einen Anzeigenamen, ein Aktivierungsflag, eine Sammlung von Metadaten und eine Branding-Überschreibung. Eine `OrganizationMembership` hält fest, wer dazugehört, und ist das, was die Ausstellung eines Tokens für diese Organisation tatsächlich autorisiert.

**Wofür das gedacht ist.** Ein ISV, dessen Produkt pro Kunde bereitgestellt wird (eine App-Instanz, eine Datenbank, aufgelöst über den Hostnamen), registriert einen Mandanten und eine Organisation pro Kunde. Seine Anwendung liest `org_id` aus dem Access Token und weist alles zurück, was nicht die Instanz ist, die sie bedient. Die Routing-Entscheidung, die die Anwendung früher selbst getroffen hat, trifft jetzt der Autorisierungsserver, und sie wird durch einen signierten Claim belegt.

**Der Mandant bleibt die Isolationsgrenze.** Ein Signaturschlüssel, ein Issuer, ein Benutzerspeicher. Eine Organisation unterteilt Identitäten *innerhalb* dieser Grenze; sie schafft keine zweite. Zwei Organisationen in einem Mandanten teilen sich ein Benutzerverzeichnis, und ein Benutzer kann mehreren angehören.

**Noch nicht unterstützt:**

- **Keine Organisationsauswahl.** Ein Benutzer, der mehreren Organisationen angehört, erhält bei einer Anfrage, die keine nennt, `account_selection_required`, einen Fehler, auf den die Relying Party reagieren kann, indem sie es mit einem Parameter erneut versucht. Es gibt keine gehostete Seite, die ihn zur Auswahl auffordert.
- **Keine delegierte Verwaltung von Organisationen.** Es gibt keine Berechtigung, mit der der eigene Administrator eines Kunden dessen Mitglieder verwalten könnte.
- **Keine organisationsbezogene SCIM-Isolation.** Ein an eine Organisation gebundenes SCIM-Token (`ScimToken.OrganizationId`) kennzeichnet die Benutzer, die es anlegt, und macht sie, wenn die ID eine reale Organisation bezeichnet, zu aktiven Mitgliedern (siehe [Mitgliedschaft über ein SCIM-Token](#membership-from-a-scim-token)). Die Prüfungen der Zugehörigkeit richten sich weiterhin nach dem OAuth-Client, nicht nach der Organisation; die Bindung entscheidet also über die Kennzeichnung, nicht über den Zugriff.
- **Kein Einladungsablauf in dieser Bibliothek.** Es gibt keinen Einladungsendpunkt und keine Einladungs-E-Mail. Ein Host schreibt eine `invited`-Mitgliedschaft selbst; auf welchen Wegen sie `active` wird, steht unter [Einladungen](#invitations).
- **Keine organisationsbezogenen Gruppen.** Der Claim `groups` und die SCIM-Gruppenmitgliedschaft bleiben mandantenweit; nur Rollen sind organisationsbezogen.
- **Keine Webhook-Ereignisse für Organisationen und kein organisationsbezogenes Audit.** `IAuthHook` kennt keine Lebenszyklusereignisse von Organisationen (angelegt, Mitgliedschaft gewährt oder entzogen), bestehende Hook-Nutzlasten tragen kein `organizationId`, und das Audit-Log hat weder eine Organisationsspalte noch einen Organisationsindex.
- **Rollengebundene Scopes werden bei der Autorisierung gegen Mandantenrollen gefiltert.** `Scope.AllowedRoles` wird an `/connect/authorize` gegen die dem Konto direkt zugewiesenen Rollen angewendet, bevor die Organisation aufgelöst ist. Ein Scope, dessen `AllowedRoles` nur durch eine organisationsbezogene Rolle erfüllt wird, wird bei der Autorisierung daher verworfen, oder mit `access_denied` abgelehnt, falls kein angeforderter Scope übrig bleibt. Beim Refresh läuft dieselbe Prüfung gegen die Rollen des aufgelösten Subjekts, die die der Organisation durchaus enthalten. Solange beide nicht übereinstimmen, binden Sie Scopes an Mandantenrollen.
- **Kein Organisations-Branding in dieser Bibliothek.** `Organization.BrandingJson` wird gespeichert, damit der Host es über das Branding des Mandanten legen kann; nichts in dieser Bibliothek liest es. Die Login-App zeigt den Namen der Organisation („Anmeldung bei {name}“) an, wenn die Boot-Nutzlast des Hosts eine `organization` (`{ id, slug, name }`) enthält; die Bibliothek selbst löst vor der Authentifizierung keine Organisation auf, außer über den Parameter `organization`, eine Client-Beschränkung mit einem einzigen Eintrag, eine organisationsbezogene Verbindung oder das `ITenantContext.OrganizationId` eines Hosts.
- **Keine Admin-REST-API für Organisationen.** `IOrganizationStore` und `IOrganizationMembershipStore` sind die Schnittstelle; ein Host, der Endpunkte möchte, baut sie selbst. Ein Host, der Organisationen oder Mitglieder auflistet, sollte `ListPageAsync` / `ListByOrganizationPageAsync` verwenden (siehe unten).

## Eine Organisation anlegen {#creating-an-organization}

Organisationen werden über `IOrganizationStore` gespeichert. Bevor eine angelegt werden kann, ist eine dauerhafte Implementierung erforderlich. Der eingebaute Standard ist leer und schreibgeschützt und lehnt Schreibvorgänge mit einer Meldung ab, die die fehlende Registrierung benennt. Das ist Absicht: Organisationsdatensätze entscheiden über die Ausstellung von Tokens, und ein prozesslokales Dictionary würde auf jedem Knoten, der einen Widerruf nicht mitbekommen hat, weiter Tokens ausstellen.

```csharp
await organizationStore.UpsertAsync(new Organization
{
    Id = "org_7f3a",              // opaque, immutable, emitted as org_id
    Slug = "international-sos",   // tenant-unique, immutable, emitted as org_slug
    DisplayName = "International SOS",
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Slug` muss `^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$` entsprechen: 1 bis 64 Zeichen aus Kleinbuchstaben, Ziffern und innenliegenden Bindestrichen, ohne Bindestrich am Anfang oder Ende. Kleinbuchstaben deshalb, weil der Parameter `organization` vor der Slug-Suche in Kleinbuchstaben umgewandelt wird; ein Slug mit Großbuchstaben wäre also ein Wert, den keine Anfrage je auflösen könnte.

`Slug` muss innerhalb des Mandanten eindeutig sein, und **IDs und Slugs teilen sich einen Namensraum**: Ein Store lehnt einen Upsert ab, dessen Slug bereits von einer anderen Organisation belegt ist, ebenso einen, dessen Slug der ID einer anderen Organisation entspricht oder dessen ID dem Slug einer anderen entspricht. Zwei Datensätze, die auf denselben Wert antworten, würden dazu führen, dass der Parameter `organization` die eine Organisation meint, während jede gespeicherte ID die andere meint.

`Id` muss `^[A-Za-z0-9._~-]{1,200}$` entsprechen: dieselbe Form wie der Parameter `organization`, sodass sich jede ID immer als solcher senden lässt. Eine ID außerhalb dieser Form ist eine ID, die keine Anfrage auswählen kann, und ein darauf beschränkter Client würde jede Anfrage ablehnen.

`Id` **sollte** außerdem mindestens ein Zeichen enthalten, das in einem Slug nicht vorkommen darf: einen Großbuchstaben, `.`, `_` oder `~`. IDs und Slugs teilen sich einen Namensraum für die Suche, und ein Wert nur aus Kleinbuchstaben wird zuerst als Slug aufgelöst. Eine ID, die selbst die Form eines Slugs hat, könnte deshalb später beim Anlegen abgelehnt werden, weil jemand diesen Slug belegt hat, während eine ID mit einem Nicht-Slug-Zeichen das nie kann. Die empfohlene Form für neue IDs ist ein opaker Wert mit dem Präfix `org_` (`org_7f3a9c`): `_` ist in Slugs nicht zulässig, also garantiert das allein schon das Präfix. Eine Konvention wird nicht erzwungen, und die bereits im Feld stehenden Werte sind beliebig: Sie stammen aus der TCC-`/try`-Antwort einer nachgelagerten App (`TccProvisioningOrchestrator`) oder aus der SCIM-Token-Bindung eines Betreibers (`ScimToken.OrganizationId`, von `ScimUserEndpoints` auf neue Benutzer gestempelt).

`Id` und `Slug` sind in der Praxis beide unveränderlich. Relying Parties vergleichen sie mit der Instanz, die sie bedienen, und werden sie fest einprogrammieren; eine Änderung an einem der beiden ist daher ein Ausfall ohne Fehlermeldung. `DisplayName` ist frei änderbar und ist das, was eine Oberfläche anzeigt.

## Mitgliedschaft gewähren {#granting-membership}

```csharp
await membershipStore.UpsertAsync(new OrganizationMembership
{
    OrganizationId = "org_7f3a",
    UserId = user.Id,
    Status = MembershipStatus.Active,
    JoinedAt = DateTimeOffset.UtcNow,
    CreatedAt = DateTimeOffset.UtcNow,
});
```

`Status` ist `invited`, `active` oder `suspended`. **Nur `active` autorisiert die Ausstellung von Tokens.** Wer sperrt statt zu löschen, behält die Aufzeichnung darüber, wer wen eingeladen hat.

### Einladungen {#invitations}

Eine `invited`-Zeile trägt `InvitedByUserId`, `InvitedAt` und alle Rollen, die der eingeladenen Person angeboten wurden. Die Bibliothek versendet die Einladung nie; sie stuft die Zeile an zwei Stellen auf `active` hoch (behält dabei Rollen, einladende Person und Einladungszeitpunkt bei und setzt `JoinedAt`):

- **Anmeldung über eine organisationsbezogene SAML- oder OIDC-Verbindung.** Wenn der eigene IdP der Organisation für die Person bürgt, gilt die Einladung als angenommen (`FederatedOrganizationBinding`, 0.30.2). Ohne das würde eine eingeladene Person, die sich immer nur per SSO anmeldet, von der Mitgliedschaftsprüfung kein Token erhalten.
- **Automatische Mitgliedschaft** (unten), wenn der Benutzer die Voraussetzungen erfüllt.

Eine `suspended`-Zeile wird auf keinem der beiden Wege hochgestuft und durch eine Anmeldung nie verändert.

### Verifizierte Domains und automatische Mitgliedschaft {#verified-domains-and-automatic-membership}

`Organization.Domains` enthält die E-Mail-Domains, die die Organisation beansprucht hat, jede als `OrganizationDomain { Domain, VerificationToken, CreatedAt, VerifiedAt }`. `Domain` wird in Kleinbuchstaben, ohne umgebende Leerzeichen und ohne abschließenden Punkt gespeichert. Die Bibliothek speichert den Anspruch und liest `VerifiedAt`; die Kontrolle nachzuweisen (typischerweise über einen DNS-TXT-Eintrag mit dem `VerificationToken`) ist Aufgabe des Hosts, und der Host setzt `VerifiedAt`, wenn das gelingt.

`Organization.AllowAutoMembership` (standardmäßig aus) lässt einen Benutzer ohne Einladung beitreten. Wenn die Organisation **explizit** ausgewählt ist (ein mitgeführter Refresh-Grant, der Parameter `organization`, eine organisationsbezogene Verbindung oder ein Client, der auf genau diese Organisation beschränkt ist) und der Benutzer keine aktive Mitgliedschaft hat, wird er Mitglied, wenn **alle** folgenden Bedingungen erfüllt sind:

- die Organisation ist aktiviert und `AllowAutoMembership` ist eingeschaltet,
- `AuthUser.EmailConfirmed` ist true,
- der Teil der E-Mail-Adresse nach dem letzten `@`, in Kleinbuchstaben, ist **genau** gleich einer Domain, bei der `VerifiedAt` gesetzt ist. Eine verifizierte `acme.com` lässt `user@eu.acme.com` nicht zu.

Eine fehlende Zeile wird als `active` ohne Rollen angelegt; eine `invited`-Zeile wird hochgestuft; eine `suspended`-Zeile wird nie angefasst. Das gilt bei der Autorisierung und bei jedem Refresh: Solange das Flag eingeschaltet ist, hält das Löschen der Zeile eines Mitglieds, das die Voraussetzungen erfüllt, es nicht draußen (es tritt beim nächsten Token erneut bei); sperren Sie es stattdessen. Eine Organisation, die nur über `AuthUser.OrganizationId` geerbt ist, führt nie zu einem automatischen Beitritt. Jeder automatische Beitritt wird auf Stufe Information protokolliert.

### Mitgliedschaft über ein SCIM-Token {#membership-from-a-scim-token}

Ein mit `organizationId` erzeugtes SCIM-Token stempelt diesen Wert als `org_id` auf jeden Benutzer, den es anlegt. Bezeichnet die ID eine existierende Organisation, schreibt das Anlegen außerdem eine `active`-Mitgliedschaft ohne Rollen und protokolliert `scim.organization_member_added` im Audit-Log. Eine ID, die keine Organisation bezeichnet, bleibt eine bloße Kennzeichnung. Nur beim Anlegen: Eine spätere Synchronisierung kennzeichnet nicht neu und fügt keine Mitglieder hinzu. Siehe [SCIM](scim#tagging-a-connectors-users-with-an-organization).

### Auflisten und Löschen {#listing-and-deleting}

`IOrganizationStore.ListPageAsync(cursor, limit)` und `IOrganizationMembershipStore.ListByOrganizationPageAsync(organizationId, cursor, limit)` liefern eine Seite (`Items` und einen opaken `NextCursor`, auf der letzten Seite null). `limit` wird auf 1..200 begrenzt, und ein fehlerhafter Cursor löst `ArgumentException` aus. Der Cursor ist schlüsselbasiert (Keyset), sodass eine zwischen zwei Lesevorgängen hinzugefügte oder entfernte Zeile nie eine Seite verschiebt oder wiederholt. Beide haben Standardimplementierungen auf Basis der nicht paginierten Auflistungen, sodass ein eigener Store weiterhin kompiliert; die Azure-Table-Stores überschreiben sie mit einer serverseitigen Bereichsabfrage.

Das Löschen eines Benutzers über das Admin-`DELETE /api/v1/profile/{userId}`, SCIM-`DELETE /scim/v2/Users/{id}` oder den SCIM-Reclaim-Pfad löscht auch jede Mitgliedschaft, die er hält (`AccountArtefactPurge.PurgeAsync` mit einem `IOrganizationMembershipStore`; die Überladung mit drei Stores entfernt keine). Ein Host mit eigenem Löschpfad muss den Mitgliedschafts-Store ebenfalls übergeben, sonst listen Organisationen das gelöschte Mitglied weiterhin auf.

## Organisationsbezogene Rollen {#organization-scoped-roles}

`OrganizationMembership.Roles` enthält die Rollen, die ein Benutzer **innerhalb** dieser Organisation hat. Die Namen stammen aus dem bestehenden Rollenkatalog des Mandanten: Ein ISV deklariert „Auditor“ einmal, und jeder Kunde vergibt die Rolle an seine eigenen Leute.

```csharp
membership.Roles = ["Auditor", "Site Manager"];
```

Sie werden im Claim `roles` mit den dem Benutzer direkt zugewiesenen Rollen und den über eine SCIM-Gruppenmitgliedschaft gewährten Rollen vereinigt, unter derselben Prüfung des Scopes `roles`. Ein Resource Server muss nicht wissen, ob eine Rolle mandantenweit oder pro Organisation gewährt wurde, er **muss** aber `org_id` zusammen mit `roles` lesen, denn derselbe Rollenname bedeutet jetzt „in dieser Organisation“.

Vier Regeln begrenzen das:

- **Nur eine explizit ausgewählte Organisation steuert Rollen bei**: eine, die über den Parameter `organization` oder eine Client-Beschränkung mit einem einzigen Eintrag benannt wird. Eine über `AuthUser.OrganizationId` geerbte Organisation steuert keine bei, dieselbe Asymmetrie wie bei der Mitgliedschaftsprüfung.
- **Nur eine `active`-Mitgliedschaft steuert Rollen bei.** Ein eingeladenes, aber nicht angenommenes oder ein gesperrtes Mitglied gewährt nichts, genau wie es nichts autorisiert.
- **Rollen überschreiten nie Organisationsgrenzen.** Sie werden aus der Mitgliedschaftszeile gelesen, die über die ausgewählte Organisation adressiert ist; eine in einer Organisation gehaltene Rolle kann also kein Token erreichen, das für eine andere ausgestellt wird.
- **Reservierte Präfixe werden entfernt.** Eine Rolle, die mit `tenant:` oder `platform:` beginnt, wird bei der Vereinigung verworfen und auf Stufe Warning protokolliert. Eine Mitgliedschaftszeile sind kundenbezogene Daten; eine Mitgliedschaft, die `tenant:admin` gewähren könnte, würde aus „darf meine eigene Organisation verwalten“ ein „darf den Mandanten administrieren“ machen. Direkt zugewiesene Rollen und SCIM-Zuordnungen von Gruppen zu Rollen sind nicht betroffen: Diese schreibt ein Betreiber über eine authentifizierte Admin-Oberfläche, und das ist die Befugnis, die eine Mitgliedschaftszeile nicht hat.

Rollen werden bei jeder Refresh-Rotation erneut aus der Mitgliedschaftszeile gelesen; eine Änderung erreicht eine laufende Sitzung also beim nächsten Refresh.

Mandantenweite Rollen werden mit denen der Organisation **vereinigt**, nicht durch sie ersetzt: `tenant:admin` ist Portal-Befugnis und bleibt erhalten, wenn eine Organisation ausgewählt wird.

## Eine Organisation in einer Autorisierungsanfrage auswählen {#selecting-an-organization-on-an-authorization-request}

Senden Sie `organization` mit dem Slug oder der ID einer Organisation:

```http
GET /connect/authorize
  ?client_id=mobiom-web
  &response_type=code
  &redirect_uri=https://audit.example.com/callback
  &scope=openid%20profile
  &organization=international-sos
  &code_challenge=...&code_challenge_method=S256
```

Der Wert muss `^[A-Za-z0-9._~-]{1,200}$` entsprechen (der Zeichensatz „unreserved“ aus RFC 3986); alles andere ergibt `invalid_request`. Wie er aufgelöst wird, hängt von der Groß- und Kleinschreibung ab:

- **Irgendein Großbuchstabe → nur als ID aufgelöst, exakt.** Slugs bestehen nur aus Kleinbuchstaben, ein solcher Wert kann also keiner sein. Ihn in Kleinbuchstaben umzuwandeln und trotzdem den Slug-Index zu befragen, hieße zu fragen, ob der Slug irgendeiner Organisation die kleingeschriebene Form dieser ID ist; träfe das zu, bekäme ein Aufrufer, der eine ID nennt, einen anderen Kunden.
- **Nur Kleinbuchstaben → zuerst als Slug, dann als ID.** Der Wert könnte beides sein, und eine Relying Party sendet in der Regel einen Slug. Das ist eindeutig, weil ein Store nicht zulässt, dass sich eine ID und ein Slug einen Wert teilen.

`org_slug` und `org_id` werden als Aliasse akzeptiert: Beide sind bei anderen Anbietern verbreitet, und den nicht gewählten stillschweigend zu ignorieren, wäre schlimmer, als beide zu akzeptieren. Werden zwei gesendet, die *verschiedene* Organisationen bezeichnen, wird die Anfrage mit `invalid_request` abgelehnt: Die Anfrage bedeutet zwei Dinge, und welche der Server auch wählte, der Relying Party wäre die andere mitgeteilt worden. Die Wiederholung eines der drei wird aus demselben Grund abgelehnt wie bei `redirect_uri`.

Der Parameter übersteht den Umweg über die Login-UI, weil die gesamte Autorisierungs-URL als `returnUrl` mitreist. Er funktioniert auch ohne zusätzlichen Aufwand mit [Pushed Authorization Requests](par): Der PAR-Endpunkt speichert jedes Feld, das er erhält, und `/connect/authorize` liest statt der Query die übertragene Nutzlast.

### Rangfolge {#precedence}

Die Organisation wird in dieser Reihenfolge aufgelöst:

1. **Bei einem Refresh die Organisation, für die der Grant ausgestellt wurde.**
2. **Die Organisation, für die eine [organisationsbezogene SSO-Verbindung](self-service-sso#organisation-scoped-connections) diese Sitzung authentifiziert hat.** Die einzige Quelle hier, die *nachgewiesen* statt von einem Aufrufer behauptet wurde: Der Benutzer hat sich bei einem IdP angemeldet, der zu genau einer Organisation gehört. Eine Anfrage, die eine andere nennt, wird mit `access_denied` abgelehnt, statt stillschweigend für die andere ausgestellt zu werden.
3. **Der Parameter `organization`.**
4. **`OAuthClient.RestrictedToOrganizationIds`, wenn es genau einen Eintrag enthält.** Eine Anwendung pro Kunde benennt ihre Organisation einmal, bei der Registrierung, und ihre Relying Party sendet überhaupt keinen Parameter. Das ist die Form, die die meisten Produkte mit einer Instanz pro Kunde wollen.
5. **`AuthUser.OrganizationId`**: die eigene gespeicherte Organisation des Kontos.

Die Regeln 1-4 sind *explizite* Auswahlen und müssen die Mitgliedschaftsprüfung bestehen. Regel 5 ist es nicht: Der Kontodatensatz ist selbst die Behauptung der Zugehörigkeit, und eine zweite zu verlangen, würde jeden bestehenden Benutzer aussperren, sobald die passende Organisation angelegt wird.

Bevor sich jemand authentifiziert hat (Home-Realm-Discovery, die Provider-Liste der Anmeldeseite, `/sso-check`), gibt es weder Benutzer noch Grant; daher werden die Regeln 3, 4 und anschließend `ITenantContext.OrganizationId` für sich allein aufgelöst. Siehe [Organisationsbezogene Verbindungen](self-service-sso#organisation-scoped-connections).

## Einen Client auf eine Organisation beschränken {#restricting-a-client-to-an-organization}

```csharp
client.RestrictedToOrganizationIds = ["org_7f3a"];
```

Jeder Eintrag muss der Form einer Organisations-ID `^[A-Za-z0-9._~-]{1,200}$` entsprechen; die Admin-API antwortet auf einen leeren oder fehlerhaften Eintrag mit `400 invalid_request`, denn eine Beschränkung, die eine ID auflistet, die kein `organization`-Parameter senden kann, passt auf nichts, und eine Beschränkung, die auf nichts passt, lehnt jede Anfrage ab. Eine `null`-Liste wird zu einer leeren normalisiert.

Leer (was jeder bestehende Client hat) bedeutet uneingeschränkt. Eine Anfrage, deren Organisation nicht auf der Liste steht, wird mit `access_denied` abgelehnt. Eine Liste mit einem Eintrag wählt zusätzlich aus, gemäß Regel 4 oben. Eine Liste mit mehreren Einträgen beschränkt, wählt aber nicht aus: Die Anfrage muss dennoch eine benennen, sonst wird sie mit `account_selection_required` abgelehnt.

## Die Claims {#the-claims}

Sowohl im ID-Token als auch im Access Token:

| Claim | Wert | Scope |
|---|---|---|
| `org_id` | `Organization.Id` | keiner; immer vorhanden, wenn das Subjekt eine Organisation hat |
| `org_slug` | `Organization.Slug` | keiner; immer vorhanden, wenn die Organisation ein realer Datensatz ist |
| `org_name` | `Organization.DisplayName` | `profile` |

**`org_id` und `org_slug` sind bewusst nicht an einen Scope gebunden.** Sie sind Autorisierungskontext, keine Profildaten: Sie sagen, für welchen Kunden das Token handeln darf. Das ist das Erste, was ein Resource Server für mehrere Kunden prüft, noch bevor er entschieden hat, ob ihn ein Name interessiert, und oft bei einem Token, das überhaupt kein Profil angefordert hat. Unter einer `profile`-Bindung erhielt ein reiner API-Client, der nur `openid` anforderte, ein Token ohne Organisation, und das liest sich wie „gehört niemandem“: Der Resource Server weist entweder einen legitimen Aufrufer ab oder behandelt das Token als nicht eingegrenzt und liefert damit die Daten jedes Kunden aus. Der zweite Fehler ist stumm, und er ist der, auf den es ankommt.

Sie ohne Bindung herauszugeben, legt nichts offen, was der Client nicht bereits festgestellt hatte: Er hat die Organisation gewählt, oder er ist auf eine beschränkt. `org_name` behält die `profile`-Bindung, weil es Darstellung ist und nichts darauf autorisieren sollte.

Ein Konto ohne Organisation gibt keinen der drei aus; ein Token, das bisher keine Organisations-Claims trug, trägt also auch jetzt keine.

Alle drei sind reserviert: Weder die `UserClaims`-Liste eines Scopes noch ein eigenes Benutzerattribut kann sie erzeugen oder überschreiben. Das ist am wichtigsten für `org_slug`, den stabilen Schlüssel, den eine Relying Party mit der Kundeninstanz vergleicht, die sie bedient. Ein selbst behaupteter Wert wäre die Antwort auf genau diesen Vergleich.

Ein Konto, dessen Organisations-ID auf keinen Datensatz verweist, gibt nur `org_id` aus. Ein fehlendes `org_slug` bedeutet „es gibt keinen Slug“, niemals „zurückgehalten“.

**Prüfen Sie `org_id` in Ihrer Anwendung:**

```csharp
var orgId = User.FindFirst("org_id")?.Value;
if (!string.Equals(orgId, ThisInstanceOrganizationId, StringComparison.Ordinal))
    return Results.Forbid();
```

## Userinfo, Introspection und Token Exchange {#userinfo-introspection-and-token-exchange}

**`/connect/userinfo`** beantwortet `org_id`, `org_slug`, `org_name` und `roles` aus dem **vorgelegten Token**, nicht aus dem Benutzerdatensatz. `org_id` und `org_slug` werden immer dann zurückgegeben, wenn das Token sie trägt, ohne Scope-Bindung, aus demselben Grund, aus dem sie im Token selbst nicht gebunden sind; `org_name` erfordert `profile`. Das ist die einzige Quelle, die richtig sein kann, sobald ein Benutzer mehreren Organisationen angehören kann: Das Konto trägt einen Standardwert, während das Token die Organisation nennt, für die der Grant tatsächlich ausgestellt wurde. Profilfelder (`email`, `name`, `phone_number`) bleiben aktuell: Das sind die gegenwärtigen Angaben des Subjekts, und genau dafür ist Userinfo da.

Eine Neukennzeichnung eines Kontos ändert also nicht, was Userinfo über ein bereits ausgestelltes Token sagt, und ein Benutzer, der bei Organisation B angemeldet ist, bekommt von demselben Server, der B in sein ID-Token geschrieben hat, nie `org_id` A gemeldet.

**`/connect/introspect`** enthält `org_id` und `org_slug`, wenn das Token sie trägt. Ein Resource Server, der das JWT selbst validiert, liest sie aus dem Token; einer, der stattdessen Introspection verwendet, erhält jetzt dieselbe Antwort.

**RFC 8693 Token Exchange** überträgt `org_id`, `org_slug` und `org_name` vom Subject Token auf das getauschte Token und setzt die `RestrictedToOrganizationIds` des **tauschenden** Clients ihnen gegenüber durch: Ein Client, der für einen Kunden registriert ist, kann weder das Token eines anderen Kunden tauschen noch ein Token, das überhaupt keine Organisation trägt. Weil `org_id` nicht gebunden ist, funktioniert diese Prüfung auch für ein Resource-Server-Token, das ohne den Scope `profile` ausgestellt wurde: Unter der alten Bindung sah ein solches Token nicht zugeordnet aus, und einem beschränkten Client wurde sein eigener Datenverkehr verweigert. Eine Ablehnung lautet `invalid_target`, passend zu den anderen Ablehnungen durch Zielrichtlinien auf diesem Pfad. Ein Austausch ist eine Projektion einer bestehenden Sitzung, und eine Projektion, die die Organisation verlöre, für die sie handelte, wäre nicht enger, sondern nicht zugeordnet. Ein `ITokenExchangeSubjectTransformer` eines Hosts kann den Austausch weiterhin bewusst an eine andere Organisation binden (dafür sind kontextgebundene Austausche da), muss das aber ausdrücklich tun.

## Refresh {#refresh}

Die Organisation, für die ein Grant ausgestellt wurde, wird über jede Refresh-Rotation mitgeführt und bei jeder erneut geprüft. Drei Dinge werden daher bei der nächsten Rotation wirksam, statt den Ablauf der Refresh-Lebensdauer abzuwarten:

- das Entziehen oder Sperren einer Mitgliedschaft,
- das Deaktivieren einer Organisation (`Enabled = false`),
- das Einengen der `RestrictedToOrganizationIds` eines Clients.

**Jedes davon lehnt den Refresh ab; keines widerruft den Grant.** Das vorgelegte Refresh Token bleibt unverbraucht und die Familie intakt; die Kette bleibt also ablehnbar, solange die Bedingung besteht, und läuft weiter, sobald sie nicht mehr besteht: Wird eine Mitgliedschaft wiederhergestellt oder eine Organisation wieder aktiviert, kehrt die Sitzung ohne erneute Anmeldung zurück. Der Grant läuft weiterhin nach seiner eigenen absoluten Lebensdauer ab. Das ist dieselbe Form wie bei einem deaktivierten Benutzer, dessen Refreshes abgelehnt werden, solange `IsActive` false ist.

Um eine Sitzung tatsächlich zu beenden, widerrufen Sie den Grant: `POST /connect/revocation` mit dem Refresh Token oder `GrantRevocation` auf Host-Seite. Das Deaktivieren einer Organisation ist eine Sperre, kein Widerruf.

Ein Grant, der die Organisation des Kontos lediglich geerbt hat, wird stattdessen bei jeder Rotation neu abgeleitet, sodass eine Neukennzeichnung des Kontos weiterhin wirksam wird.

**Der Wechsel der Organisation ist eine neue Autorisierungsanfrage**, kein Refresh. Senden Sie `/connect/authorize` erneut mit einer anderen `organization`; die bestehende Sitzung wird wiederverwendet, es gibt also keine zweite Anmeldung, und ein neuer Grant beginnt. Erwarten Sie nicht, dass der Refresh-Endpunkt die Organisation wechselt: Er hat weder einen User Agent noch eine Zustimmung, und der Grant hält die Scopes fest, die für die Organisation genehmigt wurden, für die er ausgestellt wurde.

## Die Mitgliedschaftsprüfung abschalten {#turning-the-membership-gate-off}

```csharp
organization.RequireMembershipForTokens = false;
```

Standardmäßig eingeschaltet. Schalten Sie sie für ein Deployment ab, das Organisationen für Branding und Routing statt für die Zugriffssteuerung verwendet: Dann erhält jeder, der die Organisation benennen kann, ein Token für sie. Eine Organisation, deren Mitgliedschaft nur beratenden Charakter hat, ist keine Grenze; treffen Sie diese Entscheidung bewusst.

## Eine Ausstellung über einen Host-Hook ablehnen {#refusing-an-issuance-from-a-host-hook}

`IAuthHook.OnTokenIssuingAsync` wird unmittelbar ausgelöst, bevor die Grants `authorization_code`, `refresh_token` und `device_code` etwas ausstellen, mit dem aufgelösten Subjekt:

```csharp
public Task OnTokenIssuingAsync(TokenIssuanceContext context, CancellationToken ct = default)
{
    if (IsOffboarded(context.SubjectId, context.ClientId))
        throw new InvalidOperationException("This account is being offboarded.");
    return Task.CompletedTask;
}
```

Eine ausgelöste Ausnahme lehnt die Ausstellung mit `access_denied` ab, mit der Meldung der Ausnahme als `error_description`; lösen Sie stattdessen eine `ProtocolTokenException` aus, benennen Sie Ihren eigenen OAuth-Fehler. Auf dem Refresh-Pfad läuft die Prüfung **vor** der Rotation; eine Ablehnung lässt das vorgelegte Refresh Token also unverbraucht und die Familie intakt: „Jetzt gerade nicht“ heißt nicht „diese Sitzung beenden“.

Es ist ein Default-Interface-Member; ein bestehender `IAuthHook`, der ihn nicht überschreibt, ist also nicht betroffen. Die beiden agentischen Ausstellungen (`client_credentials` und Token Exchange, jeweils mit einem Agentenprofil) lösen ihn genau so aus wie zuvor.

## Ablehnungen {#refusals}

| Bedingung | Fehler |
|---|---|
| Zwei Selektoren, die verschiedene Organisationen benennen | `invalid_request` |
| Ein beliebiger Selektor wiederholt | `invalid_request` (direkt ausgeliefert, nicht an `redirect_uri` zurückgegeben) |
| Die benannte Organisation existiert nicht | `access_denied` |
| Die Organisation ist deaktiviert | `access_denied` |
| Der Client ist für diese Organisation nicht zugelassen | `access_denied` |
| Der Benutzer ist kein aktives Mitglied (explizite Auswahl) | `access_denied` |
| Der Client bedient mehrere Organisationen, die Anfrage hat keine benannt | `account_selection_required` |

## Device Flow {#device-flow}

Der Device-Grant hat keine Autorisierungsanfrage, die einen Parameter tragen könnte; er fällt daher auf die Client-Beschränkung und dann auf den Standardwert des Kontos zurück. Ein Device-Client, der an eine Organisation gebunden sein muss, sollte mit einem `RestrictedToOrganizationIds` mit einem einzigen Eintrag registriert werden.

## Ein bestehendes Deployment aktualisieren {#upgrading-an-existing-deployment}

Nichts ändert sich, solange keine Organisation existiert. Ohne Datensätze gilt:

- keine Anfrage kann eine Organisation auswählen,
- keine Mitgliedschaftsprüfung greift,
- ein Konto mit einer bestehenden `OrganizationId` gibt `org_id` weiterhin aus dem Benutzerdatensatz aus, genau wie bisher,
- ein Token für einen Benutzer ohne Organisation trägt keinen der drei Claims.

Sobald Sie Organisationen anlegen, gewähren Sie Mitgliedschaften, **bevor** Sie einen Client oder eine Relying Party auf eine davon ausrichten: Eine explizite Auswahl verlangt eine aktive Mitgliedschaft, und ein Kunde, dessen Benutzer Datensätze, aber keine Mitgliedschaften haben, wird abgelehnt.
