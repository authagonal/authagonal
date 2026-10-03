---
layout: default
title: Self-Service-SSO
locale: de
---

# Self-Service-SSO-Onboarding

Sobald Sie [eine Verbindung zum IdP eines Kunden föderiert](oidc-federation) haben, stellt sich die nächste
Frage: **Was passiert, wenn jemand auftaucht, der sich noch nie angemeldet hat?** Authagonal bietet Ihnen
drei Varianten für einen solchen unbekannten Benutzer, von der strengsten bis zur offensten (jeden unbekannten Benutzer ablehnen, einen Einladungskontext
verlangen oder aus einer erlaubten Domain automatisch provisionieren), dazu die Einstellungen, mit denen Sie verhindern, dass ein *externer* IdP zum
Eigentor wird. Die ersten beiden werden gemeinsam unter Variante 1 behandelt, die dritte unter Variante 2. In diesem Leitfaden geht es darum, die gewünschte Variante auszuwählen und einzurichten.

All das ist Konfiguration pro Verbindung: die Seed-Abschnitte `OidcProviders` und `SamlProviders`, die gespeicherte
`OidcProviderConfig` / `SamlProviderConfig` und die Admin-API. Die relevanten Stellschrauben:

| Stellschraube | Wirkung | Protokolle |
|---|---|---|
| `JitProvisioningEnabled` | Darf ein unbekannter Benutzer überhaupt angelegt werden? | OIDC, SAML |
| `ProvisioningAttributeParams` | Verlangt vor dem Anlegen einen *Einladungskontext* im Request. | OIDC, SAML |
| `AllowUninvitedJit` | Erlaubt das Anlegen im Self-Service **ohne** Einladung (mit der Verbindung gekennzeichnet). | OIDC, SAML |
| `IsExternalConnection` | Kennzeichnet einen IdP eines Drittanbieters, sodass die Flags nur für eigene Verbindungen nicht greifen können. | Nur OIDC |
| `InteractionPath` | Zeigt *vor* der Föderation eine Seite der Login-App an (Name, Nutzungsbedingungen). | Nur OIDC |

Wo sich die einzelnen Werte setzen lassen, ist wichtig, weil die Admin-API nicht alle davon bereitstellt:

- **Seed-Konfiguration (`OidcProviders`, `SamlProviders`):** jede oben genannte Stellschraube, die es für das Protokoll gibt. Per Seed-Konfiguration angelegte
  Verbindungen werden bei jedem Start erneut aus der Konfiguration übernommen; bei einer per Seed-Konfiguration angelegten Verbindung
  stammen `JitProvisioningEnabled` und `AllowUninvitedJit` daher aus der Seed-Konfiguration, nicht aus dem zuletzt gespeicherten Stand.
- **SAML-Admin-API** (`POST` / `PUT /api/v1/saml/connections`): `JitProvisioningEnabled`,
  `ProvisioningAttributeParams` und `AllowUninvitedJit`.
- **OIDC-Admin-API** (`POST /api/v1/oidc/connections`): `JitProvisioningEnabled` und `InteractionPath`
  (muss mit `/` beginnen). `ProvisioningAttributeParams`, `AllowUninvitedJit` und `IsExternalConnection`
  lassen sich bei OIDC nur über die Seed-Konfiguration setzen, und für eine OIDC-Verbindung gibt es keine Route zum Aktualisieren. Siehe
  [Admin-API](admin-api) und [OIDC-Föderation](oidc-federation).

## Variante 1: nur mit Einladung (Nicht-Eingeladene ablehnen) {#posture-1-invite-only-reject-the-uninvited}

Der Standard. Mit `JitProvisioningEnabled: false` wird ein unbekannter SSO-Benutzer direkt abgelehnt
(`access_denied`, "contact your administrator"). Das ist richtig, wenn jeder Benutzer vorab von einem Administrator
oder über SCIM angelegt werden muss.

Wenn Sie JIT wollen, aber *nur* bei vorhandener Einladung, aktivieren Sie JIT **und** deklarieren Sie
`ProvisioningAttributeParams`. Diese benennen die auf der Whitelist stehenden `/authorize`-Query-Parameter, die den
Einladungskontext tragen (zum Beispiel `acceptKind`, `acceptToken`). Ein unbekannter Benutzer wird nur provisioniert, wenn mindestens einer dieser
Parameter tatsächlich mit einem Wert angekommen ist; eine bloße SSO-Anmeldung ohne Einladung wird mit `access_denied` abgelehnt
("This login requires an invitation"), sodass eine verirrte Anmeldung nicht stillschweigend ein neues Konto bzw. eine neue Organisation selbst provisionieren kann.
Die Parameter werden aus der Query der `/authorize`-URL gelesen, zu der der Benutzer zurückkehrt (bei SAML aus dem `RelayState`);
OIDC greift ersatzweise auch auf die Query des Callback-Requests selbst zurück.

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "ConnectionName": "Acme (Entra)",
      "MetadataLocation": "https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration",
      "ClientId": "…", "ClientSecret": "…",
      "AllowedDomains": ["acme.com"],
      "JitProvisioningEnabled": true,
      "ProvisioningAttributeParams": ["acceptKind", "acceptToken"]
    }
  ]
}
```

Eine `RedirectUrl` müssen Sie nicht setzen: Die Callback-`redirect_uri` wird pro Request als
`{issuer}/oidc/callback` abgeleitet; registrieren Sie diese URI also beim Upstream-IdP. Eine `RedirectUrl` in der Seed-Konfiguration wird ignoriert.

Die erfassten Parameter landen in den `CustomAttributes` des JIT-Benutzers und erreichen Ihren
[`Try`-Handler der Provisionierung](provisioning), der die eigentliche Prüfung der *Werte* vornimmt (zum Beispiel:
"Passt dieses Einladungstoken zu dieser E-Mail-Adresse?"). Authagonal erfasst die Schlüssel auf der Whitelist; Ihr Provisionierungsdienst entscheidet,
ob sie gültig sind. Antwortet `Try` mit `approved: false`, wird der soeben angelegte Benutzer gelöscht, und der Browser erhält
`400 provisioning_rejected`.

## Variante 2: Self-Service (Benutzer aus erlaubter Domain automatisch provisionieren) {#posture-2-self-service-auto-provision-an-allowed-domain-user}

Für "Jeder Mitarbeiter eines Kunden kann sich einfach anmelden und erhält ein Konto" setzen Sie `AllowUninvitedJit: true`. Dann wird ein
unbekannter Benutzer aus einer **erlaubten Domain** auch ohne Einladungskontext provisioniert, und Authagonal kennzeichnet ihn
mit der Verbindung, über die er gekommen ist, damit Ihr Provisionierungsdienst ihn dem richtigen Mandanten zuordnen kann, statt
einen neuen anzulegen. Die Domainprüfung greift nur, wenn `AllowedDomains` nicht leer ist: Eine Verbindung, die
keine Domains auflistet, akzeptiert jede Domain, die ihr IdP behauptet. Listen Sie die Domains daher bei jeder Self-Service-Verbindung auf.

```json
{
  "ConnectionId": "acme-entra",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true,
  "ProvisioningAttributeParams": ["acceptKind", "acceptToken"],
  "AllowUninvitedJit": true
}
```

Die Kennzeichnung kommt als benutzerdefiniertes Attribut `federated_connection` an. Ihr Wert ist der `ConnectionName` der Verbindung
(nicht ihre `ConnectionId`), und sie wird nur geschrieben, wenn der Benutzer ohne Einladungskontext angelegt wurde; ein
eingeladener Benutzer trägt stattdessen die erfassten Parameter. Ihr `Try`-Handler verzweigt danach:

```javascript
app.post('/provisioning/try', async (req, res) => {
  const { userId, email, customAttributes } = req.body;

  if (customAttributes?.acceptToken) {
    // Invited: validate the invite and add them to that org.
    const org = await validateInvite(customAttributes.acceptToken, email);
    if (!org) return res.json({ approved: false, reason: 'Invalid invite' });
    stage(userId, { orgId: org.id, role: customAttributes.acceptKind ?? 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  if (customAttributes?.federated_connection) {
    // Self-service: no invite, but they came through a known enterprise connection.
    const org = await orgForConnection(customAttributes.federated_connection);
    stage(userId, { orgId: org.id, role: 'member' });
    return res.json({ approved: true, organizationId: org.id });
  }

  return res.json({ approved: false, reason: 'No invite and no known connection' });
});
```

`AllowUninvitedJit` wird pro Verbindung ausdrücklich aktiviert: Eine Verbindung, die `ProvisioningAttributeParams` deklariert, es aber
**nicht** setzt, bleibt bei "nur mit Einladung".

Bevor ein unbekannter Benutzer angelegt wird, laufen unabhängig von der gewählten Variante zwei weitere Prüfungen:

- **Die Domain darf keiner anderen Verbindung gehören.** Leitet der SSO-Domainindex die E-Mail-Domain des Benutzers
  an eine andere Verbindung, wird die Anmeldung mit `access_denied` abgelehnt ("This email domain is
  managed by a different identity provider").
- **Nur OIDC: Der Upstream muss die E-Mail-Adresse verifiziert haben.** Meldet der Upstream `email_verified` nicht als true (gelesen aus dem id_token oder aus der
  Userinfo-Antwort, wenn die E-Mail-Adresse von dort stammt), wird die Anmeldung
  mit `access_denied` abgelehnt. Eine SAML-Assertion hat kein solches Flag, daher stützt sich SAML stattdessen auf `AllowedDomains`.

`federated_connection` ist ein reservierter Attributname. Er wird nie in einem Token ausgegeben, ein gleichnamiger Claim im id_token eines
OIDC-Upstreams wird verworfen, und die anonyme Selbstregistrierung kann ihn nicht setzen. Nur die SSO-Callbacks
können also festlegen, über welche Verbindung ein Konto gekommen ist.

## Verhindern, dass externe IdPs zum Eigentor werden {#keep-external-idps-from-becoming-foot-guns}

Einige Flags von OIDC-Verbindungen sind bei einer Verbindung, die **Sie** kontrollieren, unbedenklich, bei einem beliebigen IdP eines Drittanbieters aber
gefährlich:

- **`UseUpstreamSubjectAsUserId`**: Der Upstream wählt die lokale Benutzer-ID. Bei Ihrem eigenen Share-Link-Anbieter
  hält das die IDs abgeglichen; beim IdP eines Kunden lässt es *diesen* Ihre Benutzer-IDs bestimmen.
- **`AutoLinkExistingByEmail`**: verknüpft eine föderierte Anmeldung anhand der E-Mail-Adresse mit einem bereits bestehenden lokalen Konto
  und überspringt dabei die Prüfung des Domainbesitzes. Bei per Postfach verifizierten Adressen und eigener Verbindung ist das in Ordnung; bei einem externen IdP ist es ein
  Hebel zur Kontoübernahme.

Kennzeichnen Sie Verbindungen zu Drittanbietern als **extern**, dann werden diese Flags aufgehoben, selbst wenn sie gesetzt sind:

```json
{
  "ConnectionId": "acme-entra",
  "IsExternalConnection": true,
  "UseUpstreamSubjectAsUserId": false,
  "AutoLinkExistingByEmail": false
}
```

`IsExternalConnection` ist standardmäßig `false` (eigene Verbindung), sodass bestehende Verbindungen unverändert bleiben. Setzen Sie es bei
jeder OIDC-Verbindung, die auf den IdP eines anderen zeigt; eine spätere Fehlkonfiguration kann diesem IdP dann keine
Kontrolle über lokale Identitäten verschaffen. SAML-Verbindungen haben keines dieser Flags, dort gibt es also nichts aufzuheben.
(Um eine föderierte Identität an ein bereits bestehendes Konto zu binden, müssen zusätzlich weiterhin die
`AllowedDomains` der Verbindung die Domain der E-Mail-Adresse abdecken: siehe
[OIDC-Föderation: Sicherheit](oidc-federation).)

## Vor der Föderation etwas abfragen {#collect-something-before-federating}

Manchmal müssen Sie dem Benutzer eine Seite zeigen, **bevor** Sie ihn zum IdP weiterleiten: den Anzeigenamen eines Gasts, ein
Kontrollkästchen für Nutzungsbedingungen, eine Planauswahl. `InteractionPath` (nur bei OIDC-Verbindungen) benennt eine Route der Login-App, die
zuerst gerendert wird:

```json
{ "ConnectionId": "guest-link", "InteractionPath": "/guest" }
```

Trifft ein nicht authentifizierter `idp_hint={ConnectionId}`-Request auf `/connect/authorize`, leitet Authagonal an
`{LoginAppUrl}{InteractionPath}?returnUrl=<authorize url>&connection={id}` weiter statt direkt zum IdP
(`LoginAppUrl` ist standardmäßig `/login`, und der Pfad muss mit `/` beginnen). Dieselbe Weiterleitung erfolgt, wenn
die einzige oder per Domain zugeordnete Verbindung einer [Organisation](#organisation-scoped-connections) automatisch angesteuert wird,
und wenn `prompt=login` eine erneute Authentifizierung über einen `idp_hint` erzwingt. Ihre Seite erfasst, was sie braucht,
hängt die Werte an die Query der `returnUrl` an (aus der `PassthroughParams` /
`ProvisioningAttributeParams` sie lesen) und fährt selbst mit `/oidc/{id}/login` fort. Eine Seite, die
feststellt, dass keine Interaktion nötig ist, kann sofort fortfahren.

## Organisationsgebundene Verbindungen {#organisation-scoped-connections}

Alles Obige beschreibt eine Verbindung auf **Mandantenebene**: eine, die der ganze Mandant teilt und deren
`AllowedDomains` eine E-Mail-Domain für jede Login-Seite des Mandanten beanspruchen. Das ist die richtige
Form, wenn Sie pro Mandant mit einem Kunden föderieren. Es ist die falsche Form, wenn ein Mandant viele
Kunden-[Organisationen](organizations) bedient und jede ihren eigenen IdP mitbringt: Zwei Kunden können nicht beide
`contoso.com` beanspruchen, und die Schaltfläche "Weiter mit Contoso Entra" des einen Kunden hat auf der Login-Seite
eines anderen nichts verloren.

Setzen Sie `OrganizationId` an einer Verbindung, gehört sie stattdessen dieser Organisation (beim Anlegen und, bei SAML, auch beim
Aktualisieren über die Admin-API; eine nicht existierende Organisation ergibt `400 unknown_organization`):

```json
{
  "ConnectionId": "acme-entra",
  "OrganizationId": "org_7f3a9c",
  "AllowedDomains": ["acme.com"],
  "JitProvisioningEnabled": true
}
```

Drei Dinge ändern sich, sonst nichts.

**Sie wird nur angeboten, wenn diese Organisation ausgewählt ist.** Eine organisationsgebundene Verbindung erscheint nie auf
der eigenen Login-Seite des Mandanten und wird nie von einem Request erreicht, der zu keiner Organisation aufgelöst wurde,
selbst wenn dessen `login_hint` genau zu ihren Domains passt.

**Ihre Domains werden nur innerhalb dieser Organisation abgeglichen.** Eine organisationsgebundene Verbindung wird bewusst
*nicht* in den mandantenweiten SSO-Domainindex geschrieben, sodass eine Domain einmal auf Mandantenebene und
einmal pro Organisation beansprucht werden kann. Ein zweiter Anspruch innerhalb einer Organisation wird weiterhin mit `domain_claimed` abgelehnt,
über beide Protokolle hinweg, sodass eine Adresse nicht an zwei IdPs einer Organisation geleitet werden kann. Wird eine Verbindung
in eine Organisation verschoben, werden ihre Indexzeilen entfernt; wird sie zurückverschoben (senden Sie `"organizationId": ""` an den
Aktualisierungsendpunkt für SAML), werden sie neu registriert. OIDC-Verbindungen haben keine Route zum Aktualisieren, daher wird ihre Zuordnung beim Anlegen oder
in der Seed-Konfiguration `OidcProviders` festgelegt.

**Jeder, der sich darüber anmeldet, ist Mitglied dieser Organisation.** Der SAML-ACS und der OIDC-Callback setzen die
Organisation der Verbindung als `org_id` und überschreiben damit die eigene `AuthUser.OrganizationId` des Kontos (die
ein Ergebnis nachgelagerter Provisionierung ist und keine Aussage über diese Anmeldung), und sie legen eine
aktive Mitgliedschaft an, wenn der Benutzer noch keine hat. Eine **eingeladene** Mitgliedschaft wird angenommen: Sie wird `active` und
behält ihre Rollen, den Einladenden und den Einladungszeitpunkt, weil der eigene IdP der Organisation die Person nun bestätigt hat.
Jede andere bestehende Mitgliedschaft bleibt unverändert: Eine Zeile mit `suspended` bleibt gesperrt, sodass eine erneute
Anmeldung keinen Zugriff wiederherstellen kann, den ein Administrator entzogen hat.

### Für welche Organisation ein Request bestimmt ist, bevor sich jemand anmeldet {#which-organization-a-request-is-for-before-anyone-signs-in}

Die Home-Realm-Discovery muss diese Frage beantworten, bevor es einen Benutzer gibt. Sie wird daher getrennt vom
[Selektor nach der Authentifizierung](organizations#precedence) aufgelöst (aber in derselben Reihenfolge):

1. Der **Parameter `organization`** im Request (ein Slug oder eine ID).
2. **`OAuthClient.RestrictedToOrganizationIds`**, wenn es genau einen Eintrag enthält. Zwei oder mehr sind keine
   Auswahl: Der Client bedient mehrere, und der Request hat keine benannt.
3. **`ITenantContext.OrganizationId`**: ein Host, der pro Request eine festlegt, zum Beispiel über eine benutzerdefinierte Domain pro
   Organisation. `null` in jeder Bereitstellung mit nur einem Mandanten.

Die Organisation muss existieren und aktiviert sein, und die Einschränkung des Clients muss sie zulassen. Alles andere
ergibt *keine Organisation*, und der Request läuft genau wie bisher auf dem mandantenweiten Weg weiter. Insbesondere
wird ein Parameter, der eine Organisation nennt, von der der Client durch seine Einschränkung ausgeschlossen ist, hier nicht abgelehnt:
Die Ablehnung erfolgt bereits nach der Authentifizierung (`access_denied`), und sie vor die Login-Seite zu verlegen,
würde ändern, welche Requests ein nicht authentifizierter Aufrufer voneinander unterscheiden kann.

### Was `/connect/authorize` damit macht {#what-connectauthorize-does-with-it}

Ist eine Organisation aufgelöst, gilt vor jeder mandantenweiten Regel:

- Ein `idp_hint`, der eine **ihrer** Verbindungen nennt, führt direkt zu dieser Verbindung. Das schließt SAML ein, das
  der mandantenweite Hint-Weg (nur OIDC) nicht erreicht. Ein Hint, der etwas anderes nennt, wird durchgereicht.
- **Genau eine Verbindung und kein widersprechender `login_hint`** → direkt dorthin. Eine Verbindung ohne aufgelistete
  Domains beansprucht die ganze Organisation; eine mit aufgelisteten Domains wird weiterhin automatisch angesteuert, es sei denn, die
  Domain der im Hint genannten Adresse gehört nicht dazu.
- **Mehrere Verbindungen** → die Domain der im Hint genannten E-Mail-Adresse wählt zwischen ihnen.
- **Kein Treffer** → das mandantenweite Verhalten für `login_hint` und die Login-Karte, unverändert.

Eine fehlgeschlagene Föderation, die mit `error=` in der Query zurückkommt, gibt diesen Fehler an die Relying
Party zurück, statt erneut zu föderieren, sodass eine automatische Ansteuerung keine Schleife bilden kann.

### Was die Login-App sieht {#what-the-login-app-sees}

`/api/auth/providers` und `/api/auth/sso-check` nehmen beide einen Query-Parameter `organization` entgegen (und greifen ersatzweise
auf `ITenantContext.OrganizationId` zurück), aufgelöst nach denselben Regeln. Mit einer Organisation gilt:

- `providers` listet zuerst die Schaltflächen-Verbindungen **dieser Organisation** auf, dann die des Mandanten selbst. Eine Verbindung
  ist nur dann eine Schaltfläche, wenn sie keine `AllowedDomains` auflistet (und, bei OIDC, `ShowOnLogin` aktiviert hat); per Domain geleitete
  Verbindungen werden zuerst über die E-Mail-Adresse mittels `sso-check` erreicht. Organisationsgebundene Verbindungen sind ganz aus der Liste
  ausgeschlossen, wenn keine Organisation aufgelöst ist, und Verbindungen anderer Organisationen werden nie aufgelistet.
- `providers` enthält zusätzlich **`autoChallenge`**, wenn die Organisation genau eine Verbindung hat: einen vollständigen
  Anbieterdatensatz (`connectionId`, `name`, `type`, `loginUrl`, `iconUrl`) für die Verbindung, zu der die App
  direkt wechseln soll, unter Umgehung der Karte. Er enthält den ganzen Datensatz statt nur einer ID, weil
  diese Verbindung per Domain geleitet oder ausgeblendet und daher in `providers` nicht enthalten sein kann. Andernfalls fehlt das Feld,
  und es ist **nur ein Hinweis**: `/connect/authorize` führt dieselbe automatische Ansteuerung selbst aus,
  sodass auch eine App, die es ignoriert, beim selben IdP ankommt.
- `sso-check` gleicht die Domains der Verbindungen der Organisation **vor** dem mandantenweiten Index ab und
  greift auf diesen zurück, wenn die Organisation für diese Adresse nichts beansprucht. Eine einzige Verbindung ohne aufgelistete
  Domains beansprucht jede Adresse.

### Bei der Ausstellung von Tokens {#at-token-issuance}

Eine Sitzung, die über eine organisationsgebundene Verbindung begründet wurde, trägt deren Organisation als Quelle mit der höchsten Priorität
nach dem bei einer Erneuerung mitgeführten Wert (vor dem Parameter `organization` und vor der Einschränkung des Clients),
weil sie die einzige ist, die *nachgewiesen* wurde: Der Benutzer hat sich bei einem IdP authentifiziert, der genau
dieser Organisation gehört. Ein Request, der eine andere nennt, wird mit `access_denied` abgelehnt, statt
stillschweigend für die andere ausgestellt zu werden. `RequireMembershipForTokens` der Organisation gilt weiterhin, deshalb
legt der Callback die Mitgliedschaft an.

## Siehe auch {#related}

- [Organisationen](organizations): die Datensätze, Mitgliedschaften, Claims und Auswahlregeln.
- [OIDC-Föderation](oidc-federation): Einrichtung der Verbindung und das Sicherheitsmodell.
- [TCC-Provisionierung](provisioning): der `Try`-Handler, den diese Abläufe aufrufen.
- [Föderierte Sitzungen synchron halten](federated-sessions): lokale Sitzungen widerrufen, wenn der Upstream es tut.
