---
layout: default
title: Upgrade eines Benutzers
locale: de
---

# Upgrade eines Benutzers (Übernahme eines passwortlosen Kontos)

Manche Konten entstehen ohne Passwort:

- ein **Gast**, der einen Freigabelink geöffnet hat und durch einen föderierten Login just in time angelegt wurde,
- ein Benutzer, der durch eine SSO-Anmeldung oder eine Einladung in eine Organisation **per JIT provisioniert** wurde,
- ein Verzeichnisbenutzer, der über **SCIM** übertragen wurde.

Jeder davon ist ein echter Authagonal-Benutzer (stabile ID, meist mit bereits provisioniertem Zugriff in
nachgelagerten Systemen), der lediglich keinen lokalen Berechtigungsnachweis hat. Der Zugang *war* die Föderation,
der Link oder die Einladung.

Ein **Upgrade** eines solchen Benutzers erlaubt es ihm, ein eigenes Passwort festzulegen, und hebt in der Regel
gleichzeitig seine Beziehung zu Ihrem Produkt an (Gast → reguläres Mitglied, Testphase → zahlend, "Organisation
anlegen"). Authagonal liefert das als vollwertigen, optionalen Ablauf: Die Person registriert sich erneut mit
derselben E-Mail-Adresse, weist nach, dass sie das Postfach kontrolliert, und ihr **bestehendes** Konto wird
*an Ort und Stelle* übernommen (dieselbe Benutzer-ID, sodass alle bisherigen Zugriffe erhalten bleiben), während Ihre
App die nötige Upgrade-Logik ausführt.

> Das ist bewusst nicht dasselbe wie "Passwort zurücksetzen". Ein Passwort-Reset setzt ein Konto mit
> Berechtigungsnachweis voraus und versendet einen Reset-Link. Eine Übernahme macht aus einem Konto *ohne*
> Berechtigungsnachweis eines mit Berechtigungsnachweis und führt die Provisionierung erneut aus, damit Ihre
> nachgelagerten Systeme auf die Höherstufung reagieren können.

## Wann es sinnvoll ist {#when-to-use-it}

Aktivieren Sie den Übernahmeablauf, wenn ein nachgelagertes Produkt "jemand registriert sich mit der E-Mail-Adresse
einer föderierten Identität" als legitimen Upgrade-Pfad behandelt; der klassische Fall ist ein Gast mit
Freigabelink, der sich entschließt, ein echtes Konto anzulegen. Gibt es in Ihrer Bereitstellung keinen solchen Pfad,
lassen Sie den Ablauf ausgeschaltet (der Standard): Dann gilt jede bereits vorhandene E-Mail-Adresse als Duplikat,
und die Registrierung liefert die übliche Antwort, die keine Rückschlüsse auf existierende Konten zulässt.

## 1. Aktivieren {#1-enable-it}

Die Übernahme wird über eine einzige Opt-in-Option im Konfigurationsabschnitt `Auth` gesteuert (gebunden an `AuthOptions`):

```json
{
  "Auth": {
    "AllowPasswordlessAccountClaim": true,
    "ClaimAllowedAttributeKeys": ["org_name", "plan"]
  }
}
```

- **`AllowPasswordlessAccountClaim`** (Standard `false`): schaltet den Ablauf ein.
- **`ClaimAllowedAttributeKeys`** (Standard leer = alle nicht reservierten Schlüssel erlaubt): eine Allowlist der
  Schlüssel benutzerdefinierter Attribute, die eine Übernahme auf das Konto übertragen darf (siehe
  [Upgrade-Kontext übergeben](#4-pass-upgrade-context-safely)). Führen Sie die Schlüssel auf, die Ihr Provisioner
  erwartet, damit eine Übernahme keine beliebigen Attribute einschleusen kann. Trotz ihres Namens filtert dieselbe
  Liste auch die `customAttributes` einer gewöhnlichen Selbstregistrierung.

Ist das Flag aus, ist eine vorhandene E-Mail-Adresse ein Duplikat. Ist es an, kann ein bestehendes Konto **ohne
Berechtigungsnachweis** (kein `PasswordHash`) übernommen werden; ein Konto, das bereits ein Passwort hat, wird
**nie** angetastet: Eine erneute Registrierung kann keinen echten Berechtigungsnachweis überschreiben.

## 2. Die Übernahme im Gesamtablauf {#2-the-claim-end-to-end}

Der Benutzer ruft den gewöhnlichen Registrierungsendpunkt mit der E-Mail-Adresse des Kontos auf, das er übernehmen möchte:

```bash
# 1. The user re-registers with the SAME email as their guest/SSO/invite account.
curl -X POST https://auth.example.com/api/auth/register \
  -H "Content-Type: application/json" \
  -d '{
    "email": "grace@acme.com",
    "password": "a-strong-passphrase",
    "firstName": "Grace",
    "lastName": "Hopper",
    "customAttributes": { "org_name": "Acme Inc" }
  }'
# → 201 Created (enumeration-neutral: the same response a brand-new signup returns)
```

Die Antwort ist `201` mit `{ "success": true, "userId": "..." }`. Im Übernahmepfad ist die `userId` ein
Wegwerfwert, nicht die echte Konto-ID, sodass sich anhand der Antwort eine Übernahme nicht von einer völlig neuen
Registrierung unterscheiden lässt.

Noch ist nichts wirksam. Der Server **hinterlegt** das Passwort sowie Profil und Attribute vorläufig und versendet
einen neuen Bestätigungslink per E-Mail. Der Benutzer öffnet ihn:

```
GET https://auth.example.com/api/auth/confirm-email?token=<from the email>
```

Dieses `GET` rendert nur eine Bestätigungsseite mit einem Klick (damit Mail-Scanner und Link-Prefetcher, die die
URL abrufen, das Token nicht verbrauchen). Erst **der Klick auf die Schaltfläche** dieser Seite sendet
`POST /api/auth/confirm-email` und hebt den hinterlegten Berechtigungsnachweis an bzw. führt das Upgrade aus. Dasselbe
`POST` akzeptiert das Token auch als Query-Parameter oder im JSON-Body (`{ "token": "..." }`); ein JSON-Aufrufer
erhält `{ "message": "Email confirmed successfully.", "appLink": ... }`, während das Formular-POST der Seite auf
`/login?email_confirmed=1` weiterleitet. Danach meldet sich der Benutzer ganz normal mit seinem neuen Passwort an.

### Was der Server tut {#what-the-server-does}

1. **Registrieren**: Weil das Konto existiert und kein Passwort hat, wird der Request als Übernahme behandelt. Das
   gewählte Passwort wird gehasht in `PendingPasswordHash` abgelegt (wirkungslos, kein Authentifizierungspfad liest
   es), und Vor- und Nachname sowie die erlaubten `customAttributes` werden in `PendingClaimJson` hinterlegt. Im
   selben Moment wird der Security Stamp des Kontos rotiert, wodurch jeder Bestätigungslink ungültig wird, der bereits
   in einem Postfach liegt. Ansonsten wird das Konto **nicht** verändert. Es wird eine Bestätigungs-E-Mail versendet,
   obwohl die E-Mail-Adresse des Kontos bereits durch den ursprünglichen Ablauf bestätigt wurde: Dieser frühere
   Nachweis stammte von einem *anderen* Akteur, und die Übernahme braucht einen eigenen. Der Link trägt einen
   `pc=`-Digest des für ihn hinterlegten Berechtigungsnachweises.
2. **Bestätigen**: Die Bestätigung ist der Nachweis der Inhaberschaft. Der Server prüft, ob der Link an den *aktuell*
   hinterlegten Berechtigungsnachweis gebunden ist, übernimmt das hinterlegte Profil und die Attribute, führt
   **`ReprovisionAsync`** aus (siehe nächster Abschnitt), hebt dann `PendingPasswordHash` zu `PasswordHash` an und
   rotiert den Security Stamp erneut. Lehnt die Provisionierung das Upgrade ab, werden der hinterlegte
   Berechtigungsnachweis und das Profil verworfen, und das Konto bleibt passwortlos und erneut übernehmbar; es bleibt
   also nichts halb Fertiges zurück.

Wird eine zweite Übernahme eingereicht, bevor die erste bestätigt ist, ersetzt sie den hinterlegten
Berechtigungsnachweis, und der erste Link funktioniert nicht mehr: Seine Bestätigung liefert `claim_superseded`
(JSON `400` bzw. von der Bestätigungsseite eine Weiterleitung auf `/login?error=claim_superseded`). Ein Link ohne
`pc=`-Digest, etwa aus der Admin-Aktion "Bestätigungs-E-Mail senden", scheitert auf dieselbe Weise, solange ein
Berechtigungsnachweis hinterlegt ist. In beiden Fällen fordert der Benutzer durch eine erneute Registrierung einen
neuen Link an.

Die Benutzer-ID ändert sich nie; Projektzugriffe als Gast, SCIM-Verknüpfungen, Gruppenmitgliedschaften und alles
andere überstehen das Upgrade also.

## 3. Das Upgrade im nachgelagerten System durchführen {#3-do-the-upgrade-downstream}

Die Bestätigung einer Übernahme ruft `ReprovisionAsync` auf, das, anders als die normale Provisionierung, den
[TCC-Zyklus Try/Confirm/Cancel](provisioning) **auch für Apps erneut ausführt, in denen der Benutzer bereits
provisioniert ist**. Genau darum geht es: Ihre App hat diesen Benutzer bereits als *Gast* provisioniert, eine
einfache Provisionierung würde ihn also überspringen; die erneute Provisionierung gibt Ihnen ein zweites Try, das jetzt
den Registrierungskontext mitbringt, sodass Sie ihn höherstufen können.

Ihr `Try`-Handler für die Provisionierung unterscheidet "erste Provisionierung" von "Upgrade" daran, ob er bereits
einen Datensatz für diese `userId` hat, und reagiert auf den Kontext, den die Übernahme mitgebracht hat (hier `org_name`):

```javascript
// POST {CallbackUrl}/try
app.post('/provisioning/try', async (req, res) => {
  const { transactionId, userId, email, customAttributes } = req.body;
  const existing = await db.members.findByAuthId(userId);

  if (!existing) {
    // First time we've seen this user: a plain new signup.
    stagePending(transactionId, { userId, email, role: 'member' });
    return res.json({ approved: true });
  }

  if (existing.kind === 'guest') {
    // UPGRADE: the guest is claiming a real account. Create their org from the signup context,
    // and stage the promotion (applied in /confirm). Reject to abort the whole claim if it can't proceed.
    const orgName = customAttributes?.org_name;
    if (!orgName) return res.json({ approved: false, reason: 'Organization name is required' });

    stagePending(transactionId, { userId, upgradeTo: 'standard', orgName });
    // Return org_id so Authagonal stamps it on the user's tokens (org_id claim).
    const orgId = deterministicOrgId(userId);
    return res.json({ approved: true, organizationId: orgId });
  }

  // Already a full member: nothing to do, but approve so the claim completes.
  res.json({ approved: true });
});

// POST {CallbackUrl}/confirm: all apps approved; commit the promotion.
app.post('/provisioning/confirm', async (req, res) => {
  const p = takePending(req.body.transactionId);
  if (p?.upgradeTo === 'standard') {
    await db.orgs.create({ id: deterministicOrgId(p.userId), name: p.orgName, ownerAuthId: p.userId });
    await db.members.promote(p.userId, { kind: 'standard' });
  }
  res.sendStatus(200);
});

// POST {CallbackUrl}/cancel: the claim failed elsewhere; drop the staged promotion.
app.post('/provisioning/cancel', (req, res) => { takePending(req.body.transactionId); res.sendStatus(200); });
```

Ein `approved: false` von irgendeiner App (oder ein fehlgeschlagener Callback) lässt die Bestätigung mit
`400 provisioning_rejected` scheitern (ein JSON-Body für API-Aufrufer bzw. von der Bestätigungsseite eine
Weiterleitung auf `/login?error=provisioning_rejected&error_description=...`) und lässt das Konto ohne Upgrade,
weiterhin passwortlos und weiterhin übernehmbar. Eine gewöhnliche Registrierung (keine Übernahme), die eine
provisionierende App ablehnt, ergibt eine `422`; siehe [TCC-Provisionierung](provisioning). Eine `organizationId`
(oder zusätzliche `customAttributes`) in der zustimmenden Antwort wird mit dem Benutzer zusammengeführt und in
seinen Tokens mitgeführt.

## 4. Upgrade-Kontext sicher übergeben {#4-pass-upgrade-context-safely}

Über die `customAttributes` des Registrierungsaufrufs bringt die Übernahme den Registrierungskontext
(Organisationsname, Tarif, Empfehlung) zu Ihrem Provisioner. Sie werden **vorläufig hinterlegt**, erst beim Klick auf
den Bestätigungslink übernommen und durch `ClaimAllowedAttributeKeys` gefiltert. Halten Sie diese Allowlist eng: Sie
ist die Grenze, die verhindert, dass jemand, der die E-Mail-Adresse eines föderierten Benutzers lediglich *kennt*,
Attribute einschleust, die in den Tokens des echten Inhabers landen würden. Eine leere Allowlist erlaubt jeden nicht
reservierten Schlüssel (praktisch für vertrauenswürdige eigene Abläufe); eine befüllte verwirft alles, was nicht
aufgeführt ist.

Unabhängig vom Inhalt der Allowlist wendet der Filter immer diese Grenzen an und verwirft stillschweigend alles, was
gegen sie verstößt (die Registrierung gelingt trotzdem):

- höchstens 32 Attribute, mit Schlüsseln bis 64 Zeichen und Werten bis 1024 Zeichen;
- diese reservierten Schlüssel werden nie akzeptiert, selbst wenn sie in `ClaimAllowedAttributeKeys` stehen: `federated_connection`,
  `org_id`, `roles`, `groups`, `sub`, `iss`, `aud`, `scope`, `client_id`, `sid`, `acr`, `amr`, `email`,
  `email_verified`.

Derselbe Filter läuft auch bei der gewöhnlichen Selbstregistrierung (ohne Übernahme).

## Sicherheitseigenschaften {#security-properties}

- **Die E-Mail-Adresse zu kennen reicht nicht.** Die Übernahme wird erst abgeschlossen, wenn das eigene Postfach des
  Kontos den Bestätigungslink erhält und bestätigt. Ein Angreifer, der die Adresse kennt, bekommt die E-Mail nie.
- **Immer nur ein hinterlegter Berechtigungsnachweis.** Der Link ist an den für ihn hinterlegten
  Berechtigungsnachweis gebunden, sodass eine spätere Übernahme nicht über einen früheren Link angehoben werden kann
  (`claim_superseded`).
- **Ein echter Berechtigungsnachweis wird nie überschrieben.** Übernehmbar ist nur ein Konto ohne `PasswordHash`;
  eine Übernahme eines Kontos mit Berechtigungsnachweis ergibt die übliche Duplikat-Antwort, die keine Rückschlüsse auf
  existierende Konten zulässt.
- **Nichts ist vor der Bestätigung wirksam.** Das hinterlegte Passwort kann sich nicht authentifizieren, und das
  hinterlegte Profil sowie die Attribute werden erst mit der Bestätigung übernommen. Ein abgelehntes Upgrade macht
  alles rückgängig.
- **Das Einschleusen von Attributen ist begrenzt** durch `ClaimAllowedAttributeKeys`.

## Siehe auch {#related}

- [TCC-Provisionierung](provisioning): der Try/Confirm/Cancel-Vertrag, den Ihr Handler implementiert.
- [Self-Service-SSO](self-service-sso): die JIT-Abläufe, die die passwortlosen Konten überhaupt erst anlegen.
