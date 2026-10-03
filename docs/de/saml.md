---
layout: default
title: SAML
locale: de
---

# SAML 2.0 SP

Authagonal enthält eine selbst entwickelte Implementierung eines SAML-2.0-Service-Providers. Keine SAML-Bibliothek eines Drittanbieters: Sie basiert auf `System.Security.Cryptography.Xml.SignedXml` (Teil von .NET).

## Umfang {#scope}

- **SP-initiiertes SSO** (der Benutzer beginnt bei Authagonal und wird zum IdP weitergeleitet)
- **HTTP-Redirect-Binding** für den AuthnRequest (optional signiert, siehe unten)
- **HTTP-POST-Binding** für die Response (ACS)
- **Verschlüsselte Assertions** (`EncryptedAssertion`), entschlüsselt mit einem SP-Schlüsselpaar pro Verbindung
- **Single Logout** (SP-initiiert und IdP-initiiert, Redirect- und POST-Binding)
- Azure AD / Entra ID ist das Hauptziel, aber jeder konforme IdP funktioniert (die Attributnamen von Okta, OneLogin, Ping, Google Workspace, ADFS und Shibboleth werden berücksichtigt)

### Nicht unterstützt {#not-supported}

- Artifact-Binding
- Assertion-Verschlüsselung mit AES-GCM (Einschränkung von .NET `EncryptedXml`; konfigurieren Sie beim IdP AES-CBC, siehe unten)

**IdP-initiierte Anmeldung funktioniert, und die Kachel muss nicht neu konfiguriert werden**, aber die unaufgeforderte Assertion ist nicht das, was den Benutzer anmeldet. Eine Response ohne `InResponseTo` wird verworfen, und der ACS leitet den Browser an `/saml/{connectionId}/login` weiter, das einen neuen, an diesen Browser gebundenen AuthnRequest ausstellt. Der Benutzer ist beim IdP bereits authentifiziert, daher antwortet dieser sofort, und der Umweg bleibt unsichtbar; der `RelayState` des IdP wird als Rücksprung-URL mitgenommen, sodass der Benutzer weiterhin auf dem Deep Link landet, mit dem die Kachel konfiguriert wurde.

Die Assertion muss verworfen werden, weil die Annahme einer unaufgeforderten Assertion es jedem mit einem Konto bei diesem IdP erlaubt, eine Sitzung in einen beliebigen User-Agent einzuschleusen (jede Regel aus §4.1.4.3 ist durch eine Assertion erfüllt, die der Angreifer rechtmäßig für sein eigenes Konto erhalten hat), und weil das Verlangen des Request-Cookies auf dem SP-initiierten Weg nichts wert ist, solange sich dieselbe Assertion mit entferntem `InResponseTo` erneut einspielen lässt. Der Neustart des Ablaufs hält die Kachel funktionsfähig, ohne irgendetwas davon zu akzeptieren: Angemeldet wird, wen der IdP im *neuen* Austausch benennt.

Der Neustart erfolgt pro Browser nur einmal. Ein IdP, der auf den AuthnRequest erneut mit einer unaufgeforderten Response antwortet, wird mit `error=saml_unsolicited` abgelehnt statt erneut umgeleitet, sodass ein falsch konfigurierter IdP keine Weiterleitungsschleife erzeugen kann.

Um die unaufgeforderte Assertion stattdessen so zu akzeptieren, wie sie ist, setzen Sie `allowUnsolicitedResponses: true` an der Verbindung (**standardmäßig aus**). Ist es aktiv, wird die Prüfung der Request-ID für unaufgeforderte Responses übersprungen, die einmalige Verwendung der Assertion-ID wird aber weiterhin durchgesetzt (siehe Sicherheit).

## Einrichtung mit Azure AD {#azure-ad-setup}

### 1. Einen SAML-Anbieter anlegen {#1-create-a-saml-provider}

**Option A: Konfiguration (empfohlen für statische Setups)**

Fügen Sie Folgendes zu `appsettings.json` hinzu:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "acme-azure",
      "ConnectionName": "Acme Corp Azure AD",
      "EntityId": "https://auth.example.com/saml/acme-azure",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
      "AllowedDomains": ["acme.com"]
    }
  ]
}
```

Anbieter werden beim Start per Seed-Konfiguration angelegt. `ConnectionId`, `EntityId` und `MetadataLocation` sind für eine neue Verbindung Pflicht (ohne sie schlägt der Start fehl). SSO-Domainzuordnungen werden automatisch aus `AllowedDomains` registriert, außer bei einer organisationsgebundenen Verbindung, deren Domains nur innerhalb ihrer Organisation abgeglichen werden. Ein neu per Seed-Konfiguration angelegter Anbieter erhält kein SP-Schlüsselpaar (also keine signierten AuthnRequests, keine verschlüsselten Assertions und keine signierten Logout-Nachrichten); verwenden Sie für diese Funktionen die Admin-API.

Die Seed-Konfiguration kann außerdem `OrganizationId`, `JitProvisioningEnabled` (Standard `false`), `ChallengeMfaAfterLogin` (Standard `true`), `ProvisioningAttributeParams`, `AllowUninvitedJit` und `AllowUnsolicitedResponses` setzen. Die Seed-Konfiguration liest die gespeicherte Verbindung und führt zusammen, sodass eine bestehende Verbindung ihr SP-Schlüsselpaar, eingefügte Metadaten, das NameID-Format, `signAuthnRequests` und das Symbol behält, für die die Seed-Konfiguration kein Feld hat. Die oben genannten Verhaltens-Flags werden bei jedem Start aus der Seed-Konfiguration geschrieben; ein Flag, das Sie weglassen, fällt also auf seinen Standardwert zurück.

`EntityId` ist **Ihre SP-Entity-ID** (die Kennung, die Sie beim IdP registrieren), nicht die Entity-ID des IdP.

> **Ein IdP in Ihrem eigenen privaten Netz.** `MetadataLocation` muss https verwenden und standardmäßig zu einer öffentlich routbaren Adresse auflösen: Das Metadatendokument enthält die Zertifikate, gegen die jede Assertion validiert wird, und Authagonal lehnt interne Ziele bei jeder abgerufenen URL ab. Um mit einem IdP im eigenen Rechenzentrum zu föderieren, tragen Sie ihn in [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard) ein. Veröffentlicht der IdP überhaupt keinen https-Metadatenendpunkt, fügen Sie das Dokument stattdessen über die Admin-API in `MetadataXml` ein.

**Option B: Admin-API (für die Verwaltung zur Laufzeit)**

```bash
curl -X POST https://auth.example.com/api/v1/saml/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Acme Corp Azure AD",
    "entityId": "https://auth.example.com/saml/acme-azure",
    "metadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
    "allowedDomains": ["acme.com"]
  }'
```

Die API erzeugt die `connectionId` (eine GUID) und gibt sie im Header `Location` und im Body der Antwort zurück. Weitere optionale Felder: `metadataXml` (eingefügte Metadaten, siehe unten), `nameIdFormat` (siehe unten), `signAuthnRequests` (signierte AuthnRequests erzwingen), `iconUrl` (Symbol der Login-Schaltfläche), `jitProvisioningEnabled` (unbekannte Benutzer bei der ersten Anmeldung automatisch anlegen; **standardmäßig aus**, sodass ein unbekannter Benutzer abgelehnt wird, bis Sie es setzen), `challengeMfaAfterLogin` (Standard `true`; `false` vertraut der eigenen MFA des IdP), `provisioningAttributeParams` und `allowUninvitedJit` (siehe [Self-Service-SSO](self-service-sso)), `organizationId` (bindet die Verbindung an eine Organisation, siehe [Self-Service-SSO](self-service-sso#organisation-scoped-connections)), `allowUnsolicitedResponses` (eine IdP-initiierte Assertion unverändert akzeptieren, statt den Ablauf neu zu starten; standardmäßig aus, siehe oben). Über die API angelegte Verbindungen erhalten außerdem ein automatisch erzeugtes SP-Schlüsselpaar (siehe SP-Schlüsselpaar unten).

Verbindungen werden über `POST` / `GET` / `PUT` / `DELETE` auf `/api/v1/saml/connections[/{connectionId}]` verwaltet. `PUT` ist eine Teilaktualisierung: Nur die übermittelten Felder werden geändert.

### 2. Azure AD konfigurieren {#2-configure-azure-ad}

1. In Azure AD → Enterprise Applications → New Application → Create your own
2. Set up Single Sign-On → SAML
3. **Identifier (Entity ID):** `https://auth.example.com/saml/acme-azure`
4. **Reply URL (ACS):** `https://auth.example.com/saml/acme-azure/acs`
5. **Sign on URL:** `https://auth.example.com/saml/acme-azure/login`

### 3. SSO-Domain-Routing {#3-sso-domain-routing}

Ist `AllowedDomains` angegeben (in der Konfiguration oder über die API zum Anlegen), werden SSO-Domainzuordnungen automatisch registriert. Gibt ein Benutzer auf der Login-Seite `user@acme.com` ein, erkennt die SPA, dass SSO erforderlich ist, und zeigt "Weiter mit SSO" an. Eine Domain kann nur einer Verbindung zugeordnet sein; die API lehnt eine Domain ab, die bereits von einer anderen Verbindung beansprucht wird.

Domains lassen sich auch zur Laufzeit über die Admin-API verwalten; siehe [Admin-API](admin-api).

## Eingefügtes Metadaten-XML {#pasted-metadata-xml}

Manche IdPs veröffentlichen keine Metadaten-URL (Google Workspace), oder ihr Metadatenendpunkt ist vom SP aus nicht erreichbar (ADFS in einem privaten Netz). Fügen Sie in diesen Fällen stattdessen das Metadatendokument ein: Übergeben Sie `metadataXml` beim Anlegen bzw. Aktualisieren. Genau eines von `metadataLocation` oder `metadataXml` muss angegeben werden; wird beim Aktualisieren eines davon übergeben, wird das andere gelöscht.

Eingefügte Metadaten werden beim Speichern validiert und auf ein kanonisches, minimales `EntityDescriptor` **verdichtet** (`SamlMetadataParser.Condense`), das genau das enthält, was der SP verwendet: entityID, Signaturzertifikate, den SSO-Endpunkt, den SLO-Endpunkt, sofern vorhanden, und das Flag `WantAuthnRequestsSigned`. Dokumente von Herstellern können über 100 KB groß sein (ADFS `FederationMetadata.xml`) und damit die Grenze von 64 KB pro Eigenschaft in Azure Table überschreiten, während die vom SP genutzten Teile nur wenige KB umfassen. Nicht auswertbare Dokumente werden mit einer 400 abgelehnt; das Dokument muss einen `IDPSSODescriptor` mit einem Signaturzertifikat und einen `SingleSignOnService` enthalten.

## NameID-Format {#nameid-format}

Das Feld `nameIdFormat` steuert das Format der `NameIDPolicy`, das im AuthnRequest angefordert wird:

| Wert | Verhalten |
|---|---|
| weggelassen / null | `urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress` (der bisherige Standard) |
| `"none"` | Das Element `NameIDPolicy` wird ganz weggelassen. Die für ADFS sichere Einstellung: ADFS lässt die gesamte Anmeldung fehlschlagen (MSIS7070), wenn seine Claim-Regeln das angeforderte Format nicht ausgeben. |
| jeder andere Wert | Wird unverändert als Format-URN gesendet (muss mit `urn:` beginnen) |

Beim Aktualisieren setzt `""` auf den Standard emailAddress zurück. Die SP-Metadaten geben das von der Verbindung angeforderte Format an (und lassen `NameIDFormat` weg, wenn `"none"` gesetzt ist).

## Endpunkte {#endpoints}

| Endpunkt | Beschreibung |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...&loginHint=...` | Startet SP-initiiertes SSO. Erstellt einen AuthnRequest (signiert, wenn zutreffend) und leitet zum IdP weiter. `loginHint` wird als `login_hint` an IdPs übergeben, die ihn berücksichtigen (Entra, Google). |
| `POST /saml/{connectionId}/acs` | Assertion Consumer Service. Empfängt die SAML-Response, validiert sie und legt den Benutzer an bzw. meldet ihn an. |
| `GET /saml/{connectionId}/metadata` | SP-Metadaten-XML zur Konfiguration des IdP. |
| `GET /saml/{connectionId}/logout?returnUrl=...` | SP-initiierter Single Logout. Beendet die lokale Sitzung und sendet dann einen LogoutRequest an den IdP, sofern dieser SLO unterstützt. |
| `GET/POST /saml/{connectionId}/slo` | Single-Logout-Endpunkt. Empfängt IdP-initiierte LogoutRequests (Redirect- oder POST-Binding) und die LogoutResponse eines SP-initiierten SLO. |

Die Rücksprung-URL nach der Anmeldung wird serverseitig mit dem gespeicherten AuthnRequest (anhand der Request-ID) mitgeführt, nicht im RelayState: Die SAML-Spezifikation begrenzt RelayState auf 80 Byte, und manche IdPs kürzen ihn. RelayState wird nur bei IdP-initiierten Abläufen herangezogen.

## SP-Schlüsselpaar und verschlüsselte Assertions {#sp-keypair--encrypted-assertions}

Jede über die API angelegte Verbindung erhält ein automatisch erzeugtes SP-Schlüsselpaar: ein selbstsigniertes 2048-Bit-RSA-Zertifikat (10 Jahre gültig), gespeichert als PKCS#12 und im Ruhezustand durch den Secret-Provider des Hosts geschützt. Es verbleibt auf dem Server und wird von der API nie zurückgegeben. Das Schlüsselpaar ermöglicht:

- **Signierte AuthnRequests** (Signatur der Query über `SigAlg`/`Signature` beim Redirect-Binding). Die Signatur wird automatisch aktiviert, wenn die Metadaten des IdP `WantAuthnRequestsSigned` deklarieren, oder immer, wenn die Verbindung `signAuthnRequests: true` setzt.
- **Entschlüsselung verschlüsselter Assertions.** Geben die SP-Metadaten ein Verschlüsselungszertifikat an, beginnt ADFS standardmäßig, Assertions zu verschlüsseln; der ACS entschlüsselt sie mit dem privaten Schlüssel des SP und lässt die entschlüsselte Assertion durch dieselbe Prüfung von Signatur und Bedingungen laufen wie eine Klartext-Assertion. Unterstützt: RSA-OAEP (SHA-1/SHA-256) für den Schlüsseltransport; AES-128/192/256-CBC und 3DES für die Datenverschlüsselung. **Schlüsseltransport mit RSA-1.5 wird abgelehnt** (das Entpacken nach PKCS#1 v1.5 ist ein Bleichenbacher/ROBOT-Orakel), und **AES-GCM wird nicht unterstützt** (Einschränkung von .NET `EncryptedXml`). Konfigurieren Sie den IdP für RSA-OAEP und AES-CBC. Beide Fehler geben bewusst dieselbe feste Meldung zurück ("Could not decrypt the assertion."): Gerade die Nennung des Algorithmus oder der fehlgeschlagenen Stufe würde das Orakel bilden. Diagnostizieren Sie daher anhand der Konfiguration des IdP, nicht anhand der Fehlermeldung.
- **Signierte Logout-Nachrichten** (LogoutRequest/LogoutResponse beim Redirect-Binding).

Die SP-Metadaten veröffentlichen das Zertifikat sowohl als `signing`- als auch als `encryption`-`KeyDescriptor` und setzen `AuthnRequestsSigned="true"`, wenn die Verbindung die Signatur erzwingt.

## Single Logout {#single-logout}

Der ACS speichert die SAML-Sitzung im Auth-Cookie (Claims `saml_connection`, `saml_name_id`, `saml_name_id_format`, `saml_session_index`), damit der Logout der Sitzung beim IdP zugeordnet werden kann.

- **SP-initiiert:** `GET /saml/{connectionId}/logout` beendet immer zuerst die lokale Cookie-Sitzung (der Benutzer hat die Abmeldung verlangt; SLO beim IdP erfolgt nach bestem Bemühen). Stammt die Sitzung des Browsers von dieser Verbindung und geben die Metadaten des IdP einen `SingleLogoutService` an, wird ein LogoutRequest (NameID + SessionIndex, signiert, wenn der SP einen Schlüssel hat) über das Redirect-Binding gesendet; die LogoutResponse des IdP kommt an `/slo` zurück, das den Benutzer zur gespeicherten `returnUrl` führt. IdPs ohne SLO-Endpunkt (Google) erhalten nur die lokale Abmeldung.
- **IdP-initiiert:** Der IdP sendet einen LogoutRequest an `/saml/{connectionId}/slo` (Redirect-Binding per GET oder POST-Binding). Signierte Requests werden gegen die Zertifikate aus den Metadaten des IdP validiert. **Ein unsignierter oder nicht verifizierbarer LogoutRequest wird mit einer 400 abgelehnt**, bevor überhaupt eine Sitzung herangezogen wird. Es gibt keinen auf die Sitzung beschränkten Fallback: Eine fremde Seite, die den Browser des *Opfers* hierher navigiert, liefert die Sitzung des Opfers, nicht die des Angreifers, sodass eine Beschränkung des Fallbacks auf die aktuelle Sitzung nicht eingegrenzt hätte, wer abgemeldet werden kann. Profiles §4.4.3.1 verlangt ohnehin, dass der IdP einen LogoutRequest beim Redirect- oder POST-Binding signiert, und die Metadaten der Verbindung liefern die Zertifikate bereits, sodass die Ablehnung eines unsignierten Requests keinen konformen IdP etwas kostet. Hat der IdP einen SLO-Endpunkt, wird eine signierte LogoutResponse zurückgegeben. Nur Front-Channel: Die Nachricht kommt im Browser des Benutzers an, daher meldet das Beenden der Cookie-Sitzung genau diesen Browser ab.

## Caching der Metadaten und Zertifikatswechsel {#metadata-caching--cert-rollover}

- Von `MetadataLocation` abgerufene IdP-Metadaten werden 60 Minuten im Arbeitsspeicher zwischengespeichert (konfigurierbar über `Cache:SamlMetadataCacheMinutes`), mit der Metadaten-URL als Schlüssel (nicht der Verbindungs-ID, sodass keine Verwechslung im Cache zwischen Mandanten möglich ist).
- Eingefügte Metadaten werden inhaltsadressiert (Hash des XML) zwischengespeichert und nie neu abgerufen.
- **Neuabruf nach Signaturfehler:** Ein Fehler bei der Signaturprüfung direkt nach einem Zertifikatswechsel beim IdP bedeutet, dass die zwischengespeicherten Metadaten veraltet sind. Genau bei diesem Fehler wird der Cache-Eintrag entfernt, die Metadaten werden einmal neu abgerufen, und die Validierung wird wiederholt, mit einer Sperrfrist von 5 Minuten pro Metadatenquelle, damit sich mit einer unsinnigen Assertion nicht der Metadatenendpunkt des IdP überlasten lässt. Ohne dies würden Anmeldungen nach einem Zertifikatswechsel fehlschlagen, bis die TTL des Caches abgelaufen ist. (Nur für per URL abgerufene Metadaten; bei eingefügten Metadaten gibt es nichts neu abzurufen.)

## Kompatibilität mit Azure AD {#azure-ad-compatibility}

| Verhalten von Azure AD | Behandlung |
|---|---|
| Signiert nur die Assertion (Standard) | Validiert die Signatur am Element Assertion |
| Signiert nur die Response | Validiert die Signatur am Element Response |
| Signiert beides | Validiert beide Signaturen |
| SHA-256 (Standard) | Unterstützt SHA-256 und SHA-1 |
| NameID: emailAddress | Direkte Übernahme der E-Mail-Adresse |
| NameID: persistent (opak) | Fällt auf den E-Mail-Claim aus den Attributen zurück |
| NameID: unspecified | Fällt auf den E-Mail-Claim aus den Attributen zurück |
| NameID: transient | Wechselt bei jeder Anmeldung und wird daher nie als föderierter Schlüssel verwendet. Stattdessen wird das stabile Objekt-ID-Attribut des IdP verwendet; wird keines übermittelt, wird die Anmeldung mit einer Fehlermeldung abgelehnt, die beschreibt, was zu tun ist (eine NameID persistent oder emailAddress konfigurieren oder ein Objekt-ID-Attribut übermitteln). |

## Zuordnung der Attribute {#attribute-mapping}

Attribute werden ohne Beachtung der Groß- und Kleinschreibung sowohl unter ihrem `Name` als auch unter ihrem `FriendlyName` indiziert (Okta und Shibboleth geben OID-Namen mit lesbaren FriendlyNames aus; erst der Abgleich mit beiden lässt die Zuordnung herstellerübergreifend funktionieren). Für jedes Feld wird eine Liste von Aliasen der Reihe nach probiert; der erste Alias ist die Claim-URI von Microsoft, sodass sich das Verhalten bei Entra/ADFS nicht ändert, und die übrigen decken die lesbaren und OID-Namen ab, die Okta, OneLogin, Ping, Google und Shibboleth standardmäßig ausgeben:

| Feld | Akzeptierte Attributnamen |
|---|---|
| email | `.../claims/emailaddress`, `email`, `mail`, `emailaddress`, `urn:oid:0.9.2342.19200300.100.1.3` |
| firstName | `.../claims/givenname`, `givenName`, `given_name`, `firstName`, `first_name`, `urn:oid:2.5.4.42` |
| lastName | `.../claims/surname`, `sn`, `surname`, `lastName`, `last_name`, `familyName`, `family_name`, `urn:oid:2.5.4.4` |
| displayName | `http://schemas.microsoft.com/identity/claims/displayname`, `displayName`, `urn:oid:2.16.840.1.113730.3.1.241`, `cn`, `urn:oid:2.5.4.3` |
| objectId | `http://schemas.microsoft.com/identity/claims/objectidentifier`, `objectGUID`, `user.objectid` |
| groups | `.../claims/groups`, `groups`, `memberOf`, `.../claims/role`, `urn:oid:1.3.6.1.4.1.5923.1.5.1.1` |

(`.../claims/...` steht abgekürzt für die vollständige URI `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/...` bzw. `http://schemas.microsoft.com/ws/2008/06/identity/claims/...`.)

Reihenfolge bei der Ermittlung der E-Mail-Adresse: ausdrückliches E-Mail-Attribut (beliebiger Alias) → NameID, wenn ihr Format emailAddress ist → der Claim `name`, wenn er `@` enthält → Ablehnung (eine E-Mail-Adresse ist erforderlich).

**Gruppen sind mehrwertig:** Jedes Element `AttributeValue` wird übernommen (eines pro Gruppenmitgliedschaft), nicht nur das erste.

## JIT-Provisionierung {#jit-provisioning}

Die JIT-Provisionierung ist **standardmäßig aus**. Eine Verbindung mit `jitProvisioningEnabled: true` legt unbekannte Benutzer bei der ersten Anmeldung automatisch an (E-Mail-Adresse sowie Vor- und Nachname aus der Assertion, E-Mail-Adresse als bestätigt markiert) und verknüpft sie über ihre stabile föderierte Identität mit der Verbindung (`saml:{connectionId}` + NameID oder die Objekt-ID bei transienten NameIDs). Ohne diese Einstellung wird ein unbekannter Benutzer abgelehnt. Eine Verbindung, die `provisioningAttributeParams` deklariert, verlangt bei der Anmeldung zusätzlich diesen Einladungskontext, sofern `allowUninvitedJit` nicht gesetzt ist; siehe [Self-Service-SSO](self-service-sso). Wiederkehrende Benutzer werden zuerst über die föderierte Verknüpfung zugeordnet, nie allein über die E-Mail-Adresse; ein bestehendes lokales Konto wird nur dann anhand der E-Mail-Adresse verknüpft, wenn die `AllowedDomains` der Verbindung die Domain dieser E-Mail-Adresse abdecken (die ausdrückliche Aussage des Administrators, dass dieser IdP die Domain besitzt). Das verhindert eine Kontoübernahme über einen bösartigen IdP.

## Lebensdauer der Sitzung {#session-lifetime}

Trägt das `AuthnStatement` der Assertion ein `SessionNotOnOrAfter`, ist das die eigene Obergrenze des IdP für die Sitzung, die er gerade begründet hat, und Authagonal hält sie ein. Das Login-Cookie läuft spätestens zu diesem Zeitpunkt ab (sofern er innerhalb von 30 Tagen liegt), und dieselbe Grenze wird als `session_max_exp` an der Sitzung mitgeführt, was jedes daraus ausgestellte Access, ID und Refresh Token entsprechend kürzt. Eine Assertion ohne `SessionNotOnOrAfter` setzt keine zusätzliche Grenze. SAML kennt kein Upstream-Refresh-Token, daher ist dies der einzige Weg, auf dem ein IdP eine Sitzung nach der Anmeldung begrenzen kann; für OIDC-Verbindungen siehe [Föderierte Sitzungen](federated-sessions).

## Sicherheit {#security}

- **Schutz vor Replay:** Bei SP-initiierten Abläufen wird `InResponseTo` gegen eine gespeicherte Request-ID geprüft (einmalig verwendbar). Unabhängig davon wird die ID jeder akzeptierten Assertion gespeichert und ihre einmalige Verwendung durchgesetzt. Das deckt auch IdP-initiierte Responses ab und Responses, deren `InResponseTo` entfernt wurde (die Assertion-ID steht in der signierten Assertion und lässt sich daher nicht ändern, ohne die Signatur zu brechen).
- **Uhrabweichung:** 5 Minuten Toleranz bei NotBefore/NotOnOrAfter
- **Höchstalter der Assertion:** Eine Assertion, die mehr als eine Stunde (plus Toleranz) nach ihrem eigenen `IssueInstant` vorgelegt wird, wird abgelehnt, unabhängig davon, was ihr `NotOnOrAfter` angibt, und ein `IssueInstant` in der Zukunft wird abgelehnt
- **Issuer, Destination und Audience:** Der `Issuer` von Response und Assertion muss der Entity-ID des IdP der Verbindung entsprechen, eine signierte Response muss eine `Destination` tragen, die zu dieser ACS-URL passt, und die Audience muss die SP-Entity-ID dieser Verbindung sein
- **Gültigkeit des IdP-Zertifikats:** Ein hinterlegtes Signaturzertifikat des IdP außerhalb seines eigenen Zeitraums `NotBefore`/`NotAfter` (5 Minuten Toleranz) wird übergangen, sowohl bei Assertions als auch bei den Logout-Signaturen des Redirect-Bindings. Aktualisieren Sie die Metadaten daher nach einem Zertifikatswechsel
- **Schutz vor Wrapping-Angriffen:** Die Reference-URI der Signatur muss der ID des signierten Elements entsprechen
- **Schutz vor offenen Weiterleitungen:** Die Rücksprung-URL nach der Anmeldung muss ein relativer Pfad ab der Wurzel sein (beginnend mit `/`, kein `//`, keine Backslashes, da Browser `\` als `/` behandeln)
- **Zusicherung der Domains:** Ist `AllowedDomains` konfiguriert, werden Assertions für E-Mail-Adressen außerhalb dieser Domains abgelehnt, sodass eine Verbindung weder die Domain einer anderen noch die E-Mail-Adresse eines lokalen Benutzers behaupten kann
- **MFA:** Die Föderation belegt nur den ersten Faktor. Verlangt die effektive Richtlinie des Benutzers MFA, führt die Anmeldung über die lokale MFA-Abfrage bzw. MFA-Einrichtung, statt eine vollständig authentifizierte Sitzung auszustellen, es sei denn, die Verbindung setzt `challengeMfaAfterLogin: false`.
