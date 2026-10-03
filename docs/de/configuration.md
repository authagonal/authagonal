---
layout: default
title: Konfiguration
locale: de
---

# Konfiguration

Authagonal wird über `appsettings.json` oder Umgebungsvariablen konfiguriert. Umgebungsvariablen verwenden `__` als Trennzeichen zwischen Abschnitten (z. B. `Storage__ConnectionString`).

## Erforderliche Einstellungen {#required-settings}

Der Speicher lässt sich auf zwei Arten konfigurieren: Geben Sie **entweder** `Storage:ConnectionString` **oder** `Storage:TableServiceUri` an (den Weg über eine Managed Identity, in der Produktion bevorzugt).

| Einstellung | Umgebungsvariable | Beschreibung |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | Connection String für Azure Table Storage mit einem Kontoschlüssel. Geeignet für Entwicklung / Azurite. |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | Table-Storage-Endpunkt für eine Managed Identity, z. B. `https://{account}.table.core.windows.net/`. Alternative zu `Storage:ConnectionString` und **in der Produktion bevorzugt**: authentifiziert sich über `DefaultAzureCredential`, sodass nie ein Zugriffsschlüssel in einem Secret landet. Der Host muss der Workload-Identität die Rolle **Storage Table Data Contributor** zuweisen. |
| `Issuer` | `Issuer` | Die öffentliche Basis-URL dieses Servers (z. B. `https://auth.example.com`) |

## Speicher {#storage}

| Einstellung | Umgebungsvariable | Standard | Beschreibung |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *(keiner)* | Connection String mit Kontoschlüssel (siehe Erforderliche Einstellungen). |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *(keiner)* | Table-Storage-URI für eine Managed Identity (siehe Erforderliche Einstellungen). Hat Vorrang vor `Storage:ConnectionString`, wenn beide gesetzt sind. |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | Ob die Präfixsuch-Indextabellen `UserFirstNames` / `UserLastNames` gepflegt werden, auf denen die Namenspräfixsuche im Adminbereich beruht. Setzen Sie `false` auf Hosts, die keine Namenssuche im Adminbereich anbieten, um diese Schreibvorgänge einzusparen. **Hinweis zur Skalierung:** Diese Indizes verwenden eine einzige stark belastete Partition und begrenzen den Durchsatz bei großem Umfang auf etwa 2.000 Operationen/s; deaktivieren Sie sie, wenn Sie keine Namenssuche brauchen. |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | Basis-URL, auf die der Endpunkt `/connect/authorize` für die Login-SPA weiterleitet (Anmelde-, Step-up- und Zustimmungsbildschirme). Setzen Sie sie, wenn die Login-UI von einem anderen Origin als der Server ausgeliefert wird; standardmäßig ist es der relative Pfad `/login`, den die mitgelieferte SPA bereitstellt. |

## Authentifizierung {#authentication}

| Einstellung | Standard | Beschreibung |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Lebensdauer der Cookie-Sitzung (gleitend) |
| `Authentication:AllowInsecureCookie` | `false` | Erlaubt, dass das Sitzungscookie über unverschlüsseltes http gesendet wird (`SameAsRequest` statt `Always`). **Nur für die Entwicklung.** Das Cookie IST die Sitzung, und `SameAsRequest` wirkt nur hinter einem TLS-terminierenden Proxy gleichwertig: Es hängt davon ab, dass `X-Forwarded-Proto` ankommt und als vertrauenswürdig gilt. Ein falsch konfigurierter Ingress, eine Health-Probe über unverschlüsseltes HTTP oder ein Proxy, der den Header verwirft, erzeugt ein Cookie ohne Secure, das dann mit jeder unverschlüsselten Anfrage an denselben Host mitgeschickt wird. Der Fehler tritt stumm auf. |
| `Authentication:CookieDomain` | *(nicht gesetzt)* | Bindet das Sitzungscookie an eine übergeordnete Domain, sodass es an benachbarte Subdomains gesendet wird (`app.example.com` ebenso wie `auth.example.com`). **Das kostet die Bindung an den Origin:** Das Cookie kann dann nicht mehr das Präfix `__Host-` tragen, das den Browser veranlasst, es abzulehnen, sofern es nicht Secure ist, `Path=/` hat und keine `Domain` trägt. Damit ist jede Subdomain, die Cookies auf der übergeordneten Domain setzen kann, und alles, was eine solche übernehmen kann, betroffen. Lassen Sie die Einstellung ungesetzt, sofern nicht ein benachbarter Origin die Sitzung wirklich braucht. |
| `Auth:AllowInsecureHttp` | `false` | Erlaubt den OAuth-Endpunkten (`/connect/*`), auf unverschlüsselte http-Anfragen zu antworten. **Nur für die Entwicklung.** RFC 6749 §3.1/§3.2 verlangen TLS am Autorisierungs- und am Token-Endpunkt, daher wird eine Nicht-https-Anfrage an einen davon standardmäßig mit `invalid_request` abgelehnt. Das Schema wird *nach* der Verarbeitung der Forwarded-Header ausgewertet, sodass ein Proxy, der TLS terminiert und `X-Forwarded-Proto: https` weiterleitet, die Prüfung auch bei ausgeschalteter Option besteht, vorausgesetzt, dieser Proxy ist in [`ForwardedHeaders:KnownNetworks` / `KnownProxies`](#the-two-headers-are-not-trusted-on-the-same-terms) deklariert; andernfalls wird der Header ignoriert. Nur ein tatsächlich unverschlüsseltes Deployment (das mitgelieferte `docker-compose.yml`, die Custom-Server-Demo) braucht sie, und der Server protokolliert beim Start eine Warnung, wann immer sie eingeschaltet ist. Wird in `AuthagonalProtocolOptions.AllowInsecureHttp` übernommen und steuert daher auch die Endpunkte, die zu `Authagonal.Protocol` gehören (siehe [Erweiterbarkeit](extensibility#embedding-authagonalprotocol-alone)). |
| `Auth:RequireMinimumRuntime` | `false` | Verweigert den Start, wenn das gemeinsam genutzte .NET-Framework älter ist als die von Authagonal verlangte Sicherheitsuntergrenze (**9.0.18 / 10.0.10**). Die Untergrenze existiert, weil die Fixes für GHSA-37gx-xxp4-5rgx und GHSA-w3x6-4m5h-cxqf (eine Endlosschleife sowie ein XXE- bzw. Ressourcenerschöpfungs-Paar in `System.Security.Cryptography.Xml`, beide über den **anonymen** SAML-ACS-Endpunkt erreichbar) in der Laufzeitumgebung ausgeliefert werden, nicht in einem Paket, das diese Bibliothek festlegen kann; keine Ihrer Abhängigkeiten kann sie also garantieren. Bleibt die Option `false`, führt eine alte Laufzeitumgebung zu einem `Critical`-Logeintrag, und der Server startet: Eine standardmäßige Verweigerung würde ein Versionsupdate von Authagonal zu einem Ausfall in einer Flotte machen, deren Laufzeitumgebung einen Patch zurückliegt. Setzen Sie sie auf `true`, wo ein Nichtstarten besser ist, als auf einer ungepatchten Laufzeitumgebung nicht authentifiziertes XML zu verarbeiten. |
| `Auth:MaxFailedAttempts` | `5` | Fehlgeschlagene Anmeldeversuche bis zur Kontosperre |
| `Auth:LockoutDurationMinutes` | `10` | Dauer der Kontosperre nach Erreichen der maximalen Fehlversuche |
| `Auth:MaxLoginAttemptsPerIp` | `30` | Zulässige Passwortversuche pro Quelladresse innerhalb von `Auth:LoginWindowMinutes` und (separat) pro übermittelter E-Mail-Adresse im selben Zeitfenster. Eine Sperre pro Konto kann einen Spray-Angriff (je ein Versuch gegen Tausende Konten) nicht begrenzen, und jeder nicht authentifizierte Versuch kostet ein vollständiges PBKDF2; diese Einstellung begrenzt beides. Bei Überschreitung lautet die Antwort `429 too_many_attempts` (`AuthEndpoints.cs:107-119`). |
| `Auth:LoginWindowMinutes` | `5` | Zeitfenster für `Auth:MaxLoginAttemptsPerIp` |
| `Auth:MaxRegistrationsPerIp` | `5` | Maximale Anzahl von Registrierungen pro IP-Adresse innerhalb des Zeitfensters |
| `Auth:RegistrationWindowMinutes` | `60` | Zeitfenster für die Ratenbegrenzung der Registrierung |
| `Auth:MaxPasswordResetsPerEmail` | `3` | Maximale Anzahl von E-Mails zum Zurücksetzen des Passworts pro Zieladresse innerhalb des Zeitfensters (bezogen auf die E-Mail-Adresse, nicht auf die IP des Aufrufers, damit eine Adresse nicht mit E-Mails bombardiert werden kann) |
| `Auth:MaxPasswordResetsPerIp` | `15` | Maximale Anzahl von Anfragen „Passwort vergessen“ pro Quell-IP innerhalb des Zeitfensters. Die Obergrenze pro E-Mail-Adresse begrenzt die Mails an ein Opfer; diese hier begrenzt einen Aufrufer, der eine Adressliste abarbeitet, was sonst unbegrenzte anonyme Mails von Ihrer verifizierten Absenderdomain plus einen Lesevorgang im Store pro Adresse bedeutet. |
| `Auth:PasswordResetWindowMinutes` | `60` | Zeitfenster für die Ratenbegrenzung beim Zurücksetzen des Passworts |
| `Auth:DurableRateLimiting` | `false` | Hält die Zähler der Ratenbegrenzung im konfigurierten Store, sodass sich alle Replikate ein Kontingent teilen, statt dass jeder Knoten sein eigenes führt. Kostet pro Prüfung einen Roundtrip zum Store; ein Deployment mit einem einzigen Knoten gewinnt nichts. Erfordert einen Provider, der `IRateLimitCounterStore` bereitstellt (Azure, SQL, AWS). Andernfalls verweigert der Host den Start, statt stillschweigend auf Begrenzungen pro Knoten zurückzufallen. Siehe [Clusterweite Begrenzungen](#cluster-wide-limits-authdurableratelimiting). |
| `Auth:AutoConfirmEmailDomains` | *(leer)* | E-Mail-Domains (String-Array), deren Self-Service-Registrierungen automatisch bestätigt werden; sie überspringen die Verifizierungs-E-Mail. Leer (der Standard) bedeutet, dass jede Registrierung verifiziert werden muss. Nur für Entwicklung und Tests gedacht; tragen Sie nie eine Domain ein, die echte Mails empfangen kann. |
| `Auth:AllowPasswordlessAccountClaim` | `false` | Die Registrierung einer E-Mail-Adresse, die zu einem bestehenden Konto **ohne lokalen Berechtigungsnachweis** gehört (föderiert oder per JIT provisioniert), hinterlegt darauf vorläufig ein Passwort, statt die gegenüber Aufzählung neutrale Duplikatantwort zu liefern. Der vorläufige Berechtigungsnachweis und etwaige Attribute bleiben wirkungslos, bis die übernehmende Person auf eine neue Verifizierungs-E-Mail klickt; die Kenntnis der E-Mail-Adresse eines föderierten Kontos reicht also nicht aus, um es zu übernehmen. Ein Konto, das bereits ein Passwort hat, ist nie betroffen. Siehe [Benutzer-Upgrade](user-upgrade). |
| `Auth:ClaimAllowedAttributeKeys` | *(leer)* | Schlüssel eigener Attribute, die eine passwortlose Übernahme aus der Registrierungsanfrage auf das übernommene Konto übertragen darf. Leer erlaubt jeden Schlüssel (Abwärtskompatibilität); listen Sie Schlüssel auf, um einzuschränken, was eine Übernahme in die nachgelagerte Provisionierung und in Tokens einschleusen kann. |
| `Auth:EmailVerificationExpiryHours` | `24` | Lebensdauer des Links zur E-Mail-Verifizierung |
| `Auth:PasswordResetExpiryMinutes` | `60` | Lebensdauer des Links zum Zurücksetzen des Passworts |
| `Auth:MfaChallengeExpiryMinutes` | `5` | Lebensdauer des Tokens für die MFA-Abfrage |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | Lebensdauer des MFA-Setup-Tokens (für die erzwungene Einrichtung) |
| `Auth:WebAuthnAllowedHosts` | *(leer)* | Hosts, die als WebAuthn-Relying-Party auftreten dürfen. Leer akzeptiert jeden Host (bestehende Deployments funktionieren weiter) und ist eine Lücke: RP-ID und erwarteter Origin werden sonst aus der zu prüfenden Anfrage abgeleitet. Listen Sie bei einem mandantenfähigen Deployment jeden Mandanten-Host auf. Siehe [MFA](mfa). |
| `Auth:Pbkdf2Iterations` | `100000` | Anzahl der PBKDF2-Iterationen für das Passwort-Hashing |
| `Auth:FailedLoginMinimumMilliseconds` | `250` | Mindestdauer (Echtzeit), die eine fehlgeschlagene Anmeldung abwartet, bevor `invalid_credentials` zurückgegeben wird, gemessen ab Beginn der Anfrage. Schließt das Timing-Orakel für die Aufzählung von Benutzern: Ein fehlendes Konto wird gegen einen Dummy-Hash im nativen PBKDF2-Format geprüft, ein echtes Konto kann aber noch einen importierten bcrypt-, Scrypt.NET- oder ASP.NET-Identity-V3-Hash mit anderem Aufwand tragen. Gleicher Aufwand ist also unmöglich, durchgesetzt wird daher die gleiche verstrichene Zeit. Erhöhen Sie den Wert über den langsamsten Hash, den das Deployment enthält, z. B. wenn Sie bcrypt mit einem Kostenfaktor über 11, einen Scrypt.NET-`$s2$`-Hash mit hohem `N` importiert oder `Pbkdf2Iterations` deutlich über den Standard angehoben haben. Beim ersten Mal, dass eine fehlgeschlagene Anmeldung die Dauer überschreitet, wird eine einzige Warnung protokolliert. `0` deaktiviert die Auffüllung und öffnet das Orakel wieder. |
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | Optionales Kulanzfenster (Sekunden) für die gleichzeitige Wiederverwendung von Refresh Tokens. `0` (Standard) behält die strikte Haltung bei: Jede Wiederverwendung eines verbrauchten Refresh Tokens widerruft alle Tokens für diese Kombination aus Benutzer und Client. Setzen Sie `> 0`, um eine Wiederverwendung innerhalb des Fensters als idempotente Wiederholung zu behandeln (die Nachfolge-Tokens werden erneut ausgeliefert); nützlich für mobile Clients mit Verbindungsabbrüchen. |
| `Auth:DynamicClientRegistrationEnabled` | `false` | Aktiviert den Endpunkt `POST /connect/register` für die dynamische Client-Registrierung (RFC 7591). Standardmäßig aus, weil eine offene Registrierung in mandantenfähigen Deployments missbraucht werden kann. Siehe [Dynamische Client-Registrierung](client-registration). |
| `Auth:DynamicClientRegistrationScopes` | *(leer)* | Scopes, die sich ein anonymer Registrant zusätzlich zu den stets registrierbaren eingebauten OIDC-Scopes (`openid`, `profile`, `email`, `phone`, `offline_access`) selbst zuweisen darf. Leer bedeutet die eingebauten Scopes und sonst nichts: Dass ein Scope im Store existiert, ist keine Erlaubnis für einen selbst registrierten Client, ihn zu deklarieren. Rollengebundene Scopes sind in keinem Fall registrierbar. Siehe [Dynamische Client-Registrierung](client-registration). |
| `Auth:SigningKeyLifetimeDays` | `90` | Lebensdauer eines Signaturschlüssels bis zur automatischen Rotation (Schlüssel sind ES256 / P-256) |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Wie oft Signaturschlüssel aus dem Speicher neu geladen werden |
| `Auth:KeyRotationEnabled` | `false` | Aktiviert die automatische Rotation der Signaturschlüssel |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | Wie oft geprüft wird, ob der aktive Schlüssel rotiert werden muss |
| `Auth:KeyRotationLeadTimeDays` | `14` | Rotiert, wenn der aktive Schlüssel innerhalb dieser Anzahl von Tagen abläuft |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Intervall zwischen den Prüfungen des Security Stamps im Cookie |
| `Auth:AllowedInternalTargets` | *(leer)* | Interne Ziele, von denen Authagonal auf den Pfaden abrufen darf, auf denen **Sie** die URL angegeben haben: Upstream-SAML-Metadaten, Upstream-OIDC-Discovery, Provisionierungs-Callbacks. Leer bedeutet, dass jede interne Adresse abgelehnt wird. Siehe [Ausgehende Abrufe](#outbound-fetches-ssrf-guard). |
| `Auth:AllowOutboundProxy` | `false` | Leitet genau diese vom Betreiber konfigurierten Abrufe über den Umgebungs-HTTP-Proxy, wobei in Kauf genommen wird, dass die Adressprüfung nicht durch ihn hindurchsehen kann. Gilt nie für eine vom Client registrierte `jwks_uri` oder Back-Channel-Logout-URI. Siehe [Ausgehende Abrufe](#outbound-fetches-ssrf-guard). |
| `Auth:AtRestBackfillEnabled` | `false` | Führt das Nachfüllen für die Verschlüsselung im Ruhezustand einmal beim Start auf dem Cluster-Leader aus. Es schreibt jede bestehende Benutzerzeile und ihre aus dem Profil abgeleiteten Indexzeilen auf das aktuelle Schema für den Ruhezustand um; das ist der Migrationsweg, um `IFieldCipher` / `IIndexTokenizer` bei einem Deployment zu aktivieren, das bereits Daten hat (siehe [Erweiterbarkeit](extensibility#pii-field-encryption-ifieldcipher)). Die Registrierung einer Chiffre allein verschlüsselt nur danach geschriebene Zeilen. Es erzeugt echtes Schreibvolumen, ist idempotent und läuft einmal pro Prozess; schalten Sie es also ab, sobald das Log einen vollständigen Durchlauf meldet. |
| `Auth:MaxScimGroupsPerClient` | `5000` | Höchstzahl an SCIM-Gruppen, die ein Provisionierungs-Client besitzen darf; darüber hinaus wird das Anlegen abgelehnt. Die Gruppenspeicherung ist nicht indiziert, eine unbegrenzte Tabelle würde also jede Token-Ausstellung belasten. |
| `Auth:MaxScimGroupMembers` | `10000` | Höchstzahl an Mitgliedern, die eine SCIM-Gruppe haben darf; darüber hinaus werden Anlegen, Ersetzen und Patch abgelehnt. |

## Data Protection {#data-protection}

Die Schlüssel von ASP.NET Core Data Protection (die das Sitzungscookie verschlüsseln) müssen zwischen den Instanzen geteilt werden, siehe [Skalierung](scaling#cookie-encryption-data-protection). Optionen zur Persistierung, in der Reihenfolge ihres Vorrangs:

| Einstellung | Standard | Beschreibung |
|---|---|---|
| `DataProtection:BlobUri` | *(keiner)* | Explizite Azure-Blob-URI für den Schlüsselbund (z. B. `https://{account}.blob.core.windows.net/dataprotection/keys.xml`). Authentifiziert sich über `DefaultAzureCredential`; neben `Storage:TableServiceUri` der bevorzugte Weg in der Produktion. |
| *(Fallback)* | *(keiner)* | Ist `DataProtection:BlobUri` nicht gesetzt, wird der Schlüsselbund automatisch persistiert: in einem Container `dataprotection` des Kontos, das `Storage:ConnectionString` benennt (sofern das nicht Azurite ist), oder auf dem Weg über eine Managed Identity am Blob-Endpunkt, der aus `Storage:TableServiceUri` abgeleitet wird (`https://{account}.table.…` → `https://{account}.blob.…/dataprotection/keys.xml`), wofür Storage Blob Data Contributor auf demselben Konto nötig ist. Nur ein nicht erkannter Table-Endpunkt (Azurite, Emulatoren im Pfadstil) fällt auf den dateibasierten Speicher pro Maschine zurück, der flüchtig ist und pro Pod gilt; `KeyRingStartupCheck` protokolliert in diesem Fall auf Stufe Critical. |

Übergeben Sie beim AWS-Backend `AddAuthagonalAwsStorage` einen S3-Client und einen Bucket, um den Schlüsselbund in S3 zu persistieren, siehe [Installation → AWS-Backend](installation#aws-backend). Beim SQL-Backend wird der Schlüsselbund von `AddAuthagonalPostgres` / `AddAuthagonalSqlite` persistiert, siehe [Installation → SQL-Backend](installation#sql-backend).

Persistieren ist nicht Verschlüsseln. Welches Backend den Schlüsselbund auch hält, er wird als Klartext-XML geschrieben (einschließlich Master-Schlüssel), sofern nicht eine der folgenden Einstellungen gesetzt ist. Dieser Schlüsselbund schützt das Authentifizierungscookie; wer den Store lesen kann, kann also für jeden Benutzer eine Sitzung fälschen:

| Einstellung | Standard | Beschreibung |
|---|---|---|
| `DataProtection:KeyVaultKeyId` | *(keiner)* | URI eines Azure-Key-Vault-Schlüssels, mit dem der Schlüsselbund umhüllt wird. Authentifiziert sich über `DefaultAzureCredential`. |
| `DataProtection:CertificateThumbprint` | *(keiner)* | Fingerabdruck eines Zertifikats im Maschinenspeicher, mit dem der Schlüsselbund umhüllt wird. |
| `DataProtection:AllowUnencryptedKeyRing` | `false` | Akzeptiert bewusst einen Schlüsselbund im Klartext. Wird bei jedem Start auf Stufe `Critical` wiederholt, damit es in einem Audit auftaucht und nicht nur in einer Konfigurationsdatei steht. |

Der Start setzt dies anhand der *aufgelösten* Optionen für den Schlüsselbund durch und gilt daher gleichermaßen für Azure, AWS, SQL und jedes vom Host registrierte Repository. Ein Deployment, das den Schlüsselbund ohne Verschlüsselung persistiert und **noch keine Schlüssel** hat, wird abgelehnt, sodass der unsichere Zustand nie entsteht; eines, dessen Schlüsselbund **bereits Schlüssel** hat, startet und protokolliert auf Stufe `Critical`, denn eine Ablehnung würde dort ein laufendes Deployment bei einem Versionsupdate lahmlegen. In der Entwicklung wird nie abgelehnt.

## Cache und Timeouts {#cache-and-timeouts}

| Einstellung | Standard | Beschreibung |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | Wie lange die zulässigen CORS-Origins zwischengespeichert werden |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | Cache-Dauer für das OIDC-Discovery-Dokument |
| `Cache:SamlMetadataCacheMinutes` | `60` | Cache-Dauer für die Metadaten von SAML-IdPs |
| `Cache:OidcStateLifetimeMinutes` | `10` | Lebensdauer des state-Parameters bei der OIDC-Autorisierung |
| `Cache:SamlReplayLifetimeMinutes` | `10` | Lebensdauer der SAML-AuthnRequest-ID (Schutz vor Wiederholung) |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Timeout der Health-Prüfung für Table Storage |
| `Cache:HealthCheckCacheSeconds` | `5` | Wie lange die Antwort von `/health` wiederverwendet wird, bevor der Speicher erneut abgefragt wird (entspricht dem `Cache-Control: max-age`, das der Endpunkt angibt). `0` prüft bei jeder Anfrage, was die anonyme Verstärkung, die der Cache verhindert, wieder öffnet. |

## Hintergrunddienste {#background-services}

| Einstellung | Standard | Beschreibung |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | Anfängliche Verzögerung bis zur ersten Bereinigung abgelaufener Tokens |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | Intervall für die Bereinigung abgelaufener Tokens |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | Anfängliche Verzögerung bis zum ersten Abgleich der Grants |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | Intervall für den Abgleich der Grants |

### Bereinigung abgelaufener Einträge (Azure Table) {#expiry-sweeps-azure-table}

Azure Table Storage kennt kein TTL; daher führt der Server beim Azure-Backend pro Tabelle einen `TableExpirySweepService` aus (alle 15 Minuten, nur auf dem Cluster-Leader), der in `MfaChallenges`, `RevokedTokens` und `UpstreamRefreshTokens` Zeilen löscht, deren Ablaufzeit überschritten ist. Es geht nur um die Aufbewahrung: Jede dieser Zeilen wird beim Lesen bereits durch ihre eigene Ablaufprüfung abgelehnt. Eine Zeile ohne angegebene Ablaufzeit (bei `UpstreamRefreshTokens` möglich) wird bewusst nie bereinigt. DynamoDB und SQL bereinigen dieselben drei Tabellen nativ. Es gibt nichts zu konfigurieren.

## Bot-Schutz (Cloudflare Turnstile) {#bot-protection-cloudflare-turnstile}

Opt-in. Ist ein geheimer Schlüssel gesetzt, prüfen Anmeldung, Registrierung, „Passwort vergessen“ und das Zurücksetzen des Passworts ein `turnstileToken` bei Cloudflare, bevor sie irgendetwas tun; ohne geheimen Schlüssel ändert sich nichts, und es wird kein Widget gerendert.

| Einstellung | Standard | Beschreibung |
|---|---|---|
| `Turnstile:SiteKey` | *(nicht gesetzt)* | Öffentlicher Sitekey, der der Login-UI bereitgestellt wird (`turnstileSiteKey` bei `GET /api/auth/providers`), damit sie das Widget rendern kann |
| `Turnstile:SecretKey` | *(nicht gesetzt)* | Secret für die serverseitige Prüfung. Nicht gesetzt oder leer deaktiviert Turnstile vollständig |

Ein Host, der von Kunden bereitgestellte Domains bedient, kann kein einzelnes Schlüsselpaar verwenden (Cloudflare begrenzt die Hostnamen eines Widgets); er ersetzt [`ITurnstileKeyProvider`](extensibility#iturnstilekeyprovider). Zum Fehler `captcha_failed` siehe [Auth-API](auth-api#providers).

## Rollen {#roles}

Rollen werden im Array `Roles` definiert und beim Start angelegt, zusammen mit Clients, Scopes und
Providern. Das Anlegen ist vor allem dann wichtig, wenn ein Scope mit
[`AllowedRoles`](scopes#role-gated-scopes) abgesichert ist: Ein Scope, der an eine Rolle gebunden ist, die nichts anlegt, ist
gegen alle gesperrt, auch gegen den Betreiber, der ihn konfiguriert hat, und das schlägt stumm fehl: Der Scope
wird schlicht nie gewährt.

```json
{
  "Roles": [
    {
      "Name": "staff-admin",
      "Description": "Internal staff console",
      "Members": [ "ada@example.com", "grace@example.com" ]
    }
  ]
}
```

| Feld | Beschreibung |
|---|---|
| `Name` | Der Rollenname, wie er in `Scope.AllowedRoles` und im Token-Claim `roles` verwendet wird |
| `Description` | Für Menschen lesbar; wird bei späteren Starts aktualisiert, wenn die Seed-Konfiguration eine angibt |
| `Members` | E-Mail-Adressen, die bei jedem Start in die Rolle aufgenommen werden. Eine Adresse, zu der es noch keinen Benutzer gibt, wird mit einer Warnung übersprungen und beim nächsten Start erneut versucht, sodass der Start nie von einem Konto abhängt, das noch niemand angelegt hat |

Das Anlegen ist **additiv und idempotent**. Es entfernt nie eine Rolle und entzieht nie eine Mitgliedschaft: Die Konfiguration ist
nicht das maßgebliche System dafür, wer was innehat; eine über die Admin-API gewährte Rolle übersteht also den
nächsten Neustart.

## Clients {#clients}

Clients werden im Array `Clients` definiert und beim Start angelegt. Jeder Client kann Folgendes haben:

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "ClientName": "My Application",
      "SecretHashes": ["pbkdf2-hash-here"],
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email", "custom-scope"],
      "Audiences": ["https://api.example.com"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "AlwaysIncludeUserClaimsInIdToken": false,
      "AccessTokenLifetimeSeconds": 1800,
      "IdentityTokenLifetimeSeconds": 300,
      "AuthorizationCodeLifetimeSeconds": 300,
      "AbsoluteRefreshTokenLifetimeSeconds": 2592000,
      "SlidingRefreshTokenLifetimeSeconds": 1296000,
      "RefreshTokenUsage": "OneTime",
      "MfaPolicy": "Enabled",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "RestrictedToOrganizationIds": [],
      "InitiateLoginUri": "https://app.example.com/login",
      "ClientUri": "https://app.example.com",
      "IsDefaultApplication": false
    }
  ]
}
```

Das Anlegen erfolgt nach dem Muster **Lesen, Zusammenführen, Schreiben**: Ein Feld, das die Seed-Konfiguration nicht angibt, behält den gespeicherten Wert, sodass ein Neustart nie eine über die Admin-API vorgenommene Änderung rückgängig macht (ein deaktivierter Client bleibt deaktiviert, ein rotiertes Secret bleibt erhalten, `Audiences` und Client-JWKS bleiben bewahrt). Ein Feld, das die Seed-Konfiguration angibt, wird bei jedem Start überschrieben.

Hinweise zu den Feldern (aus `ClientSeedService.ClientSeedConfig`):

- **Aliasse.** `ClientId`/`Id`, `ClientName`/`Name`, `AllowedGrantTypes`/`GrantTypes`, `AllowedScopes`/`Scopes`, `AllowedCorsOrigins`/`CorsOrigins` und `RequireClientSecret`/`RequireSecret` sind austauschbar. Ein einzelnes `SeedClient`-Objekt wird ebenfalls als weiterer Eintrag gelesen.
- **Secrets.** Geben Sie entweder `SecretHashes` (bereits gehasht) oder `ClientSecret` an (Klartext, wird beim Start gehasht und nur verwendet, wenn keine Hashes angegeben sind). Die Seed-Konfiguration wird nur angewendet, wenn sie eines liefert, sodass ein über die Admin-API rotiertes Secret den nächsten Neustart übersteht. In der Form der Seed-Konfiguration gibt es keinen Schlüssel `ClientSecretHashes`.
- **`BackChannelLogoutUri`**: wohin Back-Channel-Logout-Tokens per POST gesendet werden; siehe [Back-Channel-Logout](#back-channel-logout).
- **`RestrictedToOrganizationIds`**: IDs der Organisationen, mit denen der Client verwendet werden darf. Leer bedeutet uneingeschränkt; ein einzelner Eintrag wählt diese Organisation außerdem für eine Anfrage aus, die keine nennt (siehe [Organisationen](organizations)).
- **`InitiateLoginUri`, `ClientUri`, `IsDefaultApplication`**: speisen die Liste von `/api/auth/apps` und die Schaltfläche „Weiter zur App“ des Anmeldebildschirms.
- **Nicht über die Seed-Konfiguration setzbar.** `RequireConsent`, `ProvisioningApps`, `RequirePushedAuthorizationRequests`, Client-JWKS und die Felder für den Front-Channel-Logout haben in der Form der Seed-Konfiguration keinen Schlüssel, die Konfiguration kann sie also nicht setzen. Unbekannte Schlüssel werden ohne Warnung ignoriert.
- Ein Seed-Eintrag, dessen Scopes oder Audiences gegen die Regeln für reservierte Scopes oder Audiences verstoßen, wird mit einem Fehlerlog abgelehnt und übersprungen.

### Audiences und Resource Indicators (RFC 8707) {#audiences-and-resource-indicators-rfc-8707}

`Audiences` ist die Positivliste des Clients für den Parameter `resource` (RFC 8707) und den Parameter `audience` eines Token Exchange (RFC 8693). Was diese Prüfung übersteht, wird zum Claim `aud` des ausgestellten Access Tokens; ohne `resource` in der Anfrage fällt `aud` auf `Audiences` zurück, und fehlt beides, ist es die `client_id`.

Eine leere `Audiences`-Liste bedeutet **„keine“** für jeden Client, der die Frage tatsächlich beantwortet hat: einen, dessen anlegende Anfrage das Feld `audiences` enthielt, sei es über die dynamische Registrierung (wo das Feld eine Authagonal-Erweiterung von RFC 7591 ist), die Admin-API oder die Seed-Konfiguration. Ein solcher Client darf auf keinem Pfad überhaupt eine `resource` benennen: Authorize, `client_credentials` und Token Exchange stimmen darin überein.

Eine dynamische Registrierung, die `audiences` **weglässt** (jeder Standard-Client nach RFC 7591, also jeder MCP-Client), wurde nie gefragt. Ihre Liste ist „nicht gesetzt“, und sie darf jede absolute URI als `resource` benennen; die MCP-Autorisierungsspezifikation ist darauf angewiesen. Dieselbe Lesart gilt für Clients, die gespeichert wurden, bevor es `AudiencesDeclared` gab, denn jeden gespeicherten Client beim Upgrade zu verschärfen, würde Abläufe brechen, die heute funktionieren.

| Client | Leere `Audiences` bedeutet |
|---|---|
| Anlegende Anfrage enthielt `audiences` (DCR-Erweiterungsfeld, Admin-API, Seed-Konfiguration) | **verweigern**: Es darf keine `resource` benannt werden |
| DCR-Registrierung, die `audiences` weggelassen hat | **„nicht gesetzt“**: jede absolute URI wird als `resource` akzeptiert |
| Gespeichert, bevor es `AudiencesDeclared` gab | **„nicht gesetzt“**: jede absolute URI wird als `resource` akzeptiert |

**Einen Altbestand-Client nachzurüsten** geschieht über ein `PUT` an die Admin-Client-API mit `audiencesDeclared: true` (und den `audiences`, auf die er festgelegt werden soll). Das Flag verschärft nur: Eine Aktualisierung kann es setzen, aber nicht löschen, sodass eine unabhängige Bearbeitung einen Client nie stillschweigend in die freizügige Lesart zurückversetzt.

Die Folge für die Altbestandszeilen sollte klar ausgesprochen und nicht versteckt werden:

> Ein bereits bestehender Client ohne konfigurierte `Audiences` kann am Autorisierungsendpunkt oder unter `client_credentials` **jede** absolute URI als `resource` benennen und ein Access Token erhalten, dessen `aud` dieser Wert ist, signiert mit dem Schlüssel dieses Mandanten, mit dem `sub` des anfragenden Benutzers und den Scopes, die dem Client erlaubt sind.

Eine deklarierte `audiences`-Liste wird dort validiert, wo sie geschrieben wird: höchstens 20 Einträge mit je höchstens 512 Zeichen, jeder eine absolute URI mit explizitem Schema und ohne Fragment. Für `resource`-Werte gilt dieselbe Form; beachten Sie, dass ein bloßer Pfad wie `/admin` **nicht** akzeptiert wird, obwohl der `Uri`-Parser von .NET ihn unter Linux als absolute `file:`-URI einstuft.

Eine Ressource zu benennen, ist kein Zugriff auf sie. Es bedeutet aber, dass der Autorisierungsserver nicht das Einzige sein kann, was zwischen einem Client und einer API steht, die er nie aufrufen sollte. Daher:

- **Ressourcenserver MÜSSEN anhand von `scope` autorisieren** (oder anhand ihres eigenen Modells), nicht allein anhand von `iss` + `aud` + `sub`. Ein Token, das Ihre API in `aud` nennt, belegt, dass der Client Ihre API angefragt hat. Es belegt nicht, dass der Client sie aufrufen darf, und dieser Server kann es nicht dazu bringen, das zu belegen.
- **Ressourcenserver MÜSSEN `aud` gegen ihre eigene Kennung prüfen**, nicht bloß darauf, dass „irgendein Wert vorhanden ist“.
- **Setzen Sie `Audiences` bei jedem Client, der auf eine feste Menge von APIs festgelegt sein soll.** Ist das konfiguriert, wird eine nicht aufgeführte `resource` am Autorisierungsendpunkt und bei `client_credentials` mit `invalid_target` abgelehnt. Nur an dieser Stelle lässt sich die Einschränkung durchsetzen.
- **Rüsten Sie `audiencesDeclared: true` bei Clients nach, die angelegt wurden, bevor es das Flag gab**, damit ihre leere Audience-Liste „keine“ bedeutet und nicht „beliebig“.
- **Ein selbst registrierter Client darf bei der Registrierung `audiences` deklarieren** und wird an das gebunden, was er deklariert, auch an eine leere Liste. `Auth:DynamicClientRegistrationEnabled` ist weiterhin standardmäßig aus; siehe [Dynamische Client-Registrierung](client-registration).

### Grant-Typen {#grant-types}

| Grant-Typ | Anwendungsfall |
|---|---|
| `authorization_code` | Interaktive Benutzeranmeldung (Web-Apps, SPAs, Mobilgeräte) |
| `client_credentials` | Kommunikation zwischen Diensten |
| `refresh_token` | Erneuerung von Tokens (erfordert `AllowOfflineAccess: true`) |
| `urn:ietf:params:oauth:grant-type:device_code` | Device Authorization Grant (RFC 8628) für Geräte mit eingeschränkter Eingabe |

### Verwendung von Refresh Tokens {#refresh-token-usage}

| Wert | Verhalten |
|---|---|
| `OneTime` (Standard) | Jede Erneuerung stellt ein neues Refresh Token aus und macht das alte ungültig. Standardmäßig (`Auth:RefreshTokenReuseGraceSeconds = 0`) widerruft jede Wiederverwendung eines verbrauchten Tokens sofort alle Tokens für diese Kombination aus Benutzer und Client; standardmäßig ist **kein** Kulanzfenster aktiv. Setzen Sie `Auth:RefreshTokenReuseGraceSeconds` auf einen positiven Wert, um ein Fenster zu aktivieren, das Wiederholungen toleriert. |
| `ReUse` | Dasselbe Refresh Token wird bis zu seinem Ablauf wiederverwendet. |

### Provisionierungs-Apps {#provisioning-apps}

Das Array `ProvisioningApps` eines Clients (wird zum Zeitpunkt der Autorisierung gelesen, `AuthorizeEndpoint.cs:578`; die Seed-Konfiguration bindet es nicht, und die Client-Routen der Admin-API führen es nicht, daher setzt der Host es am gespeicherten Client-Datensatz) verweist auf App-IDs, die im Konfigurationsabschnitt `ProvisioningApps` definiert sind. Autorisiert sich ein Benutzer über diesen Client, wird er per TCC in diese Apps provisioniert. Einzelheiten finden Sie unter [Provisionierung](provisioning).

## Scopes {#scopes}

Eigene [OAuth-Scopes](scopes) können aus dem Array `Scopes` angelegt werden. Jeder Eintrag wird beim Start anhand von `Name` angelegt oder aktualisiert (ein Eintrag ohne `Name` wird mit einer Warnung übersprungen):

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

Ein Feld, das Sie setzen, setzt sich bei jedem Start gegen den gespeicherten Wert durch; ein Feld, das Sie weglassen, behält den gespeicherten Wert. Die Konfiguration kann `UserClaims` und `AllowedRoles` daher ergänzen oder ändern, aber nicht leeren (verwenden Sie dafür `PUT /api/v1/scopes/{name}`). Die Bedeutung der Felder steht unter [Scope-Modell](scopes#scope-model).

## Provisionierungs-Apps {#provisioning-apps-1}

Definieren Sie nachgelagerte Anwendungen, in die Benutzer provisioniert werden sollen:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-api-key"
    },
    "analytics": {
      "CallbackUrl": "https://analytics.example.com/provisioning",
      "ApiKey": "another-key"
    }
  }
}
```

Die vollständige Spezifikation des TCC-Protokolls finden Sie unter [Provisionierung](provisioning).

## MFA-Richtlinie {#mfa-policy}

Die Multi-Faktor-Authentifizierung wird pro Client über die Eigenschaft `MfaPolicy` durchgesetzt:

| Wert | Verhalten |
|---|---|
| `Disabled` (Standard) | Keine MFA-Abfrage, auch wenn der Benutzer MFA eingerichtet hat |
| `Enabled` | Fragt Benutzer ab, die MFA eingerichtet haben; erzwingt keine Einrichtung |
| `Required` | Fragt Benutzer mit eingerichteter MFA ab; erzwingt die Einrichtung für Benutzer ohne MFA |

```json
{
  "Clients": [
    {
      "ClientId": "secure-app",
      "MfaPolicy": "Required"
    }
  ]
}
```

Ist `MfaPolicy` auf `Required` gesetzt und hat der Benutzer keine MFA eingerichtet, liefert die Anmeldung `{ mfaSetupRequired: true, setupToken: "..." }`. Das Setup-Token authentifiziert den Benutzer gegenüber den Endpunkten für die MFA-Einrichtung (über den Header `X-MFA-Setup-Token`), sodass er sie einrichten kann, bevor er eine Cookie-Sitzung erhält.

Föderierte Anmeldungen (SAML/OIDC) beachten die MFA-Richtlinie ebenfalls: Ein Benutzer mit eingerichteter MFA wird durch die MFA-Abfrage geleitet, nachdem der externe IdP ihn authentifiziert hat, und `Required` erzwingt die Einrichtung für föderierte Benutzer ohne MFA.

### Überschreiben per IAuthHook {#iauthhook-override}

Die Methode `IAuthHook.ResolveMfaPolicyAsync` kann die Client-Richtlinie pro Benutzer überschreiben:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    return Task.FromResult(clientPolicy);
}
```

## Passwortrichtlinie {#password-policy}

Passen Sie die Anforderungen an die Passwortstärke an:

```json
{
  "PasswordPolicy": {
    "MinLength": 10,
    "MinUniqueChars": 3,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": false
  }
}
```

| Eigenschaft | Standard | Beschreibung |
|---|---|---|
| `MinLength` | `8` | Mindestlänge des Passworts |
| `MinUniqueChars` | `2` | Mindestanzahl unterschiedlicher Zeichen |
| `RequireUppercase` | `true` | Verlangt mindestens einen Großbuchstaben |
| `RequireLowercase` | `true` | Verlangt mindestens einen Kleinbuchstaben |
| `RequireDigit` | `true` | Verlangt mindestens eine Ziffer |
| `RequireSpecialChar` | `true` | Verlangt mindestens ein nicht alphanumerisches Zeichen |

Die Richtlinie wird beim Zurücksetzen des Passworts und bei der Registrierung von Benutzern durch Administratoren durchgesetzt. Die Login-UI ruft die aktive Richtlinie über `GET /api/auth/password-policy` ab, um die Anforderungen dynamisch anzuzeigen.

## SAML-Provider {#saml-providers}

Definieren Sie SAML-Identity-Provider in der Konfiguration. Sie werden beim Start angelegt:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com", "example.org"]
    }
  ]
}
```

| Eigenschaft | Erforderlich | Beschreibung |
|---|---|---|
| `ConnectionId` | Ja | Stabile Kennung (verwendet in URLs wie `/saml/{connectionId}/login`) |
| `ConnectionName` | Nein | Anzeigename (Standard ist ConnectionId) |
| `EntityId` | Ja | Die SP-Entity-ID **dieses Servers**, also die Kennung, die Sie beim IdP registrieren, nicht die eigene Entity-ID des IdP |
| `MetadataLocation` | Ja | URL zum SAML-Metadaten-XML des IdP. Muss https sein und öffentlich routbar, sofern der Host nicht in [`Auth:AllowedInternalTargets`](#outbound-fetches-ssrf-guard) genannt ist: Dieses Dokument enthält die Zertifikate, gegen die jede Assertion geprüft wird. Veröffentlicht Ihr IdP keinen https-Endpunkt für Metadaten, setzen Sie stattdessen `metadataXml` über die [Admin-API](admin-api); die Seed-Konfiguration hat dafür keinen Schlüssel. |
| `AllowedDomains` | Nein | E-Mail-Domains, die per SSO an diesen Provider geleitet werden |
| `OrganizationId` | Nein | Beschränkt diese Verbindung auf eine [Organisation](organizations). Null (der Standard) macht sie zu einer Verbindung auf Mandantenebene; nur Verbindungen auf Mandantenebene registrieren ihre `AllowedDomains` als SSO-Domain-Routen |
| `JitProvisioningEnabled` | Nein | Legt bei der ersten Anmeldung einen Benutzer an. Standard `false` |
| `AllowUninvitedJit` | Nein | Erlaubt JIT, einen Benutzer in einer Organisation anzulegen, in die er nicht eingeladen wurde. Standard `false` |
| `ChallengeMfaAfterLogin` | Nein | Fragt nach der Anmeldung beim IdP gemäß der MFA-Richtlinie der App ab. Standard `true` |
| `ProvisioningAttributeParams` | Nein | Attribute der Assertion, die an die nachgelagerte Provisionierung weitergegeben werden |
| `AllowUnsolicitedResponses` | Nein | Akzeptiert auf dieser Verbindung vom IdP initiierte (unaufgeforderte) Antworten. Standard `false` |

Die booleschen Werte werden bei jedem Start aus der Seed-Konfiguration geschrieben, einschließlich des Standardwerts; eine angelegte Verbindung, die ein Betreiber über die Admin-API geändert hat, setzt sie also beim nächsten Neustart zurück. Felder, für die die Seed-Konfiguration keinen Schlüssel hat (`SpCertificate`, `SignAuthnRequests`, `NameIdFormat`, `MetadataXml`, `IconUrl`), bleiben erhalten.

## OIDC-Provider {#oidc-providers}

Definieren Sie OIDC-Identity-Provider in der Konfiguration. Sie werden beim Start angelegt:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

| Eigenschaft | Erforderlich | Beschreibung |
|---|---|---|
| `ConnectionId` | Ja | Stabile Kennung (verwendet in URLs wie `/oidc/{connectionId}/login`) |
| `ConnectionName` | Nein | Anzeigename (Standard ist ConnectionId) |
| `MetadataLocation` | Ja | URL zum OpenID-Connect-Discovery-Dokument des IdP |
| `ClientId` | Ja | Beim IdP registrierte OAuth2-Client-ID |
| `ClientSecret` | Ja | OAuth2-Client-Secret (beim Start über `ISecretProvider` geschützt) |
| `RedirectUrl` | Nein | **Wird ignoriert.** Die Redirect-URI wird pro Anfrage als `{Issuer}/oidc/callback` abgeleitet: Registrieren Sie *diese* beim IdP. Ein Wert hier hat keine Wirkung und wird als ignoriert protokolliert. |
| `AllowedDomains` | Nein | E-Mail-Domains, die per SSO an diesen Provider geleitet werden |
| `OrganizationId` | Nein | Beschränkt diese Verbindung auf eine [Organisation](organizations); null bedeutet Mandantenebene |
| `JitProvisioningEnabled` | Nein | Legt bei der ersten Anmeldung einen Benutzer an. Standard `false` |
| `AllowUninvitedJit` | Nein | Erlaubt JIT, einen Benutzer in einer Organisation anzulegen, in die er nicht eingeladen wurde. Standard `false` |
| `UseUpstreamSubjectAsUserId` | Nein | Verwendet das Upstream-`sub` als lokale Benutzer-ID. Standard `false` |
| `ShowOnLogin` | Nein | Zeigt auf dem Anmeldebildschirm eine Schaltfläche für diese Verbindung. Standard `true`; per Domain geleitete Verbindungen werden unabhängig davon über die vorangestellte E-Mail-Eingabe erreicht |
| `ChallengeMfaAfterLogin` | Nein | Fragt nach der Anmeldung beim IdP gemäß der MFA-Richtlinie der App ab. Standard `true` |
| `AutoLinkExistingByEmail` | Nein | Verknüpft eine erstmalige Anmeldung mit einem bestehenden lokalen Konto mit derselben E-Mail-Adresse. Standard `false` |
| `PassthroughParams`, `ProvisioningAttributeParams` | Nein | Parameter, die an den IdP bzw. an die nachgelagerte Provisionierung weitergereicht werden |
| `RevalidateOnRefresh` | Nein | Prüft die Upstream-Sitzung erneut, wenn ein Refresh Token eingelöst wird. Standard `false` |
| `IsExternalConnection`, `SessionExpClaim` | Nein | Einstellungen für föderierte Sitzungen; siehe [Föderierte Sitzungen](federated-sessions) |
| `InteractionPath` | Nein | Pfad der Login-App (zum Beispiel `/guest`), der angezeigt wird, bevor eine nicht authentifizierte `idp_hint`-Anfrage über diese Verbindung föderiert wird. Leer föderiert direkt |

Die OIDC-Seed-Konfiguration überschreibt bei jedem Start mehr als die SAML-Seed-Konfiguration. Die booleschen Werte werden aus der Seed-Konfiguration geschrieben, einschließlich des Standardwerts; eine angelegte Verbindung, die ein Betreiber über die Admin-API geändert hat, setzt sie also beim nächsten Neustart zurück. Dasselbe gilt für `AllowedDomains`, `PassthroughParams`, `ProvisioningAttributeParams`, `SessionExpClaim` und `InteractionPath`: Ein Schlüssel, den die Seed-Konfiguration weglässt, wird auf leer bzw. seinen Standardwert zurückgesetzt, statt den gespeicherten Wert zu behalten. Nur `IconUrl` und `CreatedAt` bleiben immer erhalten, `ConnectionName` und `OrganizationId` bleiben erhalten, wenn die Seed-Konfiguration sie weglässt.

> **Hinweis:** Provider lassen sich zur Laufzeit auch über die [Admin-API](admin-api) verwalten. Per Konfiguration angelegte Provider werden bei jedem Start angelegt oder aktualisiert, sodass Konfigurationsänderungen beim Neustart wirksam werden.

## Secret-Provider {#secret-provider}

Client-Secrets von Upstream-OIDC-Providern sowie TOTP-/MFA-Seeds können statt im Klartext in Azure Key Vault gespeichert werden:

| Einstellung | Beschreibung |
|---|---|
| `SecretProvider:VaultUri` | Key-Vault-URI (z. B. `https://my-vault.vault.azure.net/`). Ist sie nicht gesetzt, wird der **Klartext**-Provider verwendet, und Secrets werden unverändert in Table Storage gespeichert. |
| `SecretProvider:RequireVaultReferences` | Standardmäßig `false`. Bei `true` ist eine gespeicherte Referenz ohne Vault-Präfix (`kv:` für Key Vault, `sm:` für AWS Secrets Manager) ein **Fehler**, statt als Klartextwert anerkannt zu werden. Setzen Sie die Option, sobald eine Migration in den Vault abgeschlossen ist. |

Ist er konfiguriert, werden Secret-Werte, die wie Key-Vault-Referenzen aussehen, zur Laufzeit aufgelöst. Die Authentifizierung erfolgt über `DefaultAzureCredential`.

### In einen Vault migrieren und die Tür danach schließen {#migrating-into-a-vault-and-closing-the-door-afterwards}

Beide vault-gestützten Provider geben eine Referenz ohne Präfix unverändert zurück und behandeln sie als Klartextwert, der geschrieben wurde, bevor das Deployment einen Vault hatte. Das ermöglicht es, ein laufendes System Secret für Secret statt auf einmal zu migrieren; bleibt es aber offen, ist es ein dauerhafter Weg zur Herabstufung: Alles, was eine einzige Konfigurationsspalte schreiben kann (eine halb abgeschlossene Migration, ein Admin-Pfad, der einen Rohwert speichert, wo eine Referenz hingehört, ein Angreifer mit Zugriff auf den Speicher, aber nicht auf den Vault), ersetzt ein durch den Vault geschütztes Secret durch einen Wert eigener Wahl, und dieser wird einwandfrei geprüft, weil bei einer Referenz ohne Präfix die Referenz *der* Wert ist.

Setzen Sie `SecretProvider:RequireVaultReferences`, wenn die Migration abgeschlossen ist. Das Auflösen einer Referenz ohne Präfix wirft dann eine Ausnahme, statt stillschweigend Klartext zurückzugeben. Die Option zu setzen, während der aufgelöste Provider der Klartext-Provider ist, wird beim Start abgelehnt, da diese Kombination keinen funktionierenden Zustand hat: Jede Referenz, die der Klartext-Provider schreibt, hat kein Präfix.

Der Server protokolliert außerdem beim Start eine Warnung, wann immer ein Host außerhalb von Development beim Klartext-Provider landet.

> ⚠️ **Produktion: Setzen Sie `SecretProvider:VaultUri`.** Der Standard-Secret-Provider ist **Klartext**. Ist `SecretProvider:VaultUri` nicht gesetzt, werden Client-Secrets von Upstream-OIDC-Providern sowie TOTP-/MFA-Seeds im Klartext in Azure Table Storage geschrieben und erscheinen daher auch in jedem [Backup](backup-restore) im Klartext. Konfigurieren Sie für jedes Produktions-Deployment `SecretProvider:VaultUri`, damit diese Secrets in Key Vault gespeichert werden.

## Admin-API {#admin-api}

| Einstellung | Standard | Beschreibung |
|---|---|---|
| `AdminApi:Enabled` | `true` | **Standardmäßig aktiviert.** Setzen Sie `false`, um alle Admin-Endpunkte zu deaktivieren (sie werden dann nicht registriert). |
| `AdminApi:Scope` | `authagonal-admin` | JWT-Scope, der für den Zugriff auf die Admin-Endpunkte erforderlich ist. Ändern Sie ihn passend zu Ihrem bestehenden Scope-Namen (z. B. `projects-identity-admin` bei Migrationen von IdentityServer). |

> ⚠️ **Die Admin-API ist standardmäßig aktiviert und hoch privilegiert.** Der Admin-Scope gewährt die vollständige Verwaltung und das Auftreten als beliebiger Benutzer: Wer ein Token mit `AdminApi:Scope` hält, kann Tokens für jeden Benutzer ausstellen, Clients verwalten und die gesamte Konfiguration lesen und schreiben. Schränken Sie den Netzwerkzugriff auf die Admin-Endpunkte (die Admin-Routen unter `/api/v1/*`) ein und kontrollieren Sie streng, wem der Admin-Scope ausgestellt werden kann. Als zusätzliche Schutzebene ist der Scope *reserviert*: Er kann nie einem OAuth-Client gewährt werden (siehe [Admin-API](admin-api)) und kann nicht über den Impersonation-Endpunkt ausgestellt werden. Setzen Sie `AdminApi:Enabled = false` vollständig, wenn die Admin-API nicht genutzt wird.

## Zustimmung {#consent}

Die Zustimmung pro Client lässt sich mit der Eigenschaft `RequireConsent` aktivieren:

| Wert | Verhalten |
|---|---|
| `false` (Standard) | Die Autorisierung wird unmittelbar nach der Authentifizierung fortgesetzt |
| `true` | Dem Benutzer wird ein Zustimmungsbildschirm mit den angefragten Scopes angezeigt. Die Zustimmung wird 5 Jahre lang gespeichert und nur dann erneut abgefragt, wenn neue Scopes angefragt werden. |

Benutzer können ihre erteilten Zustimmungen unter `GET /consent/grants` einsehen und unter `DELETE /consent/grants/{clientId}` widerrufen.

## Back-Channel-Logout {#back-channel-logout}

Registrieren Sie bei einem Client eine `BackChannelLogoutUri`, um Benachrichtigungen nach OIDC Back-Channel Logout 1.0 zu empfangen. Meldet sich ein Benutzer ab, sendet Authagonal ein signiertes Logout-Token (JWT) an die registrierte URI jedes Clients.

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback"
    }
  ]
}
```

## E-Mail {#email}

Der eingebaute E-Mail-Versand verwendet [Resend](https://resend.com) und **wird automatisch aktiviert**, wenn `Email:ResendApiKey` konfiguriert ist; eine Registrierung des Dienstes ist nicht nötig. Um einen anderen Anbieter zu verwenden, registrieren Sie vor dem Aufruf von `AddAuthagonal()` Ihre eigene `IEmailService`-Implementierung (sie hat unabhängig von den `Email:*`-Schlüsseln Vorrang).

| Einstellung | Beschreibung |
|---|---|
| `Email:ResendApiKey` | Resend-API-Schlüssel. Ist er gesetzt, wird der eingebaute Resend-Versand verwendet. |
| `Email:SenderEmail` | E-Mail-Adresse des Absenders |
| `Email:SenderName` | Anzeigename des Absenders (Standard ist `"Authagonal"`) |

> ⚠️ **Ohne E-Mail-Versand funktioniert die Self-Service-Registrierung nicht.** Ist `Email:ResendApiKey` nicht gesetzt und kein eigener `IEmailService` registriert, verwirft ein wirkungsloser Dienst stumm alle Mails: Verifizierungs-E-Mails und E-Mails zum Zurücksetzen des Passworts kommen nie an, und weil die Anmeldung standardmäßig eine bestätigte E-Mail-Adresse verlangt, können sich selbst registrierte Benutzer nie anmelden. `UseAuthagonal` protokolliert in diesem Zustand beim Start eine Warnung. Ausweg für Entwicklung/Test: `Auth:AutoConfirmEmailDomains` bestätigt Registrierungen für die aufgeführten Domains automatisch.

E-Mails an Adressen unter `@example.com` werden stumm übersprungen (nützlich für Tests).

## Cluster {#cluster}

Die Clustering-Schicht stellt hinter austauschbaren Backends eine **Leader-Wahl** bereit (damit an den Leader gebundene Aufgaben wie die Rotation der Signaturschlüssel auf genau einem Knoten laufen) sowie einen **knotenübergreifenden Event-Bus**. Der Standard läuft im Prozess: Ein einzelner Knoten ist immer sein eigener Leader. Das ist die richtige Einstellung für Einzelknoten und lokale Entwicklung und erfordert keinerlei Konfiguration.

| Einstellung | Umgebungsvariable | Standard | Beschreibung |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Hauptschalter. Bei `false` läuft der Knoten eigenständig (immer Leader, Event-Bus im Prozess). |
| `Cluster:Secret` | `Cluster__Secret` | *(keiner)* | Gemeinsames Secret, das am nur intern genutzten Endpunkt `/_internal/backchannel-logout` verlangt wird. Ist es gesetzt, müssen Aufrufer es im Header `X-Cluster-Secret` vorlegen (Vergleich in konstanter Zeit). Ist es **nicht gesetzt, autorisiert der Endpunkt niemanden** und antwortet mit 404: Eine Quelladresse ist kein Berechtigungsnachweis, und Loopback ist genau das, was ein Reverse Proxy auf demselben Host für jede weitergeleitete Anfrage vorweist, auch für solche, die aus dem Internet stammen. |
| `Cluster:AllowLoopbackWithoutSecret` | `Cluster__AllowLoopbackWithoutSecret` | `false` | Opt-in für die Entwicklung: Ohne `Cluster:Secret` wird ein Aufrufer akzeptiert, dessen **Peer-Adresse vor der Weiterleitung** Loopback ist. Private Adressbereiche werden weiterhin abgelehnt: In einem gemeinsam genutzten Cluster-Netzwerk würde das jedem benachbarten Workload vertrauen. Setzen Sie die Option nicht auf einem Host hinter einem Reverse Proxy. |
| `Cluster:RunLeaderElection` | `Cluster__RunLeaderElection` | `true` | Ob dieser Knoten die Schleife zur Erneuerung der Lease ausführt und Leader werden kann. Bei `false` tritt er dem Cluster trotzdem bei und konsumiert den Event-Bus; er bewirbt sich nur nie um die Lease. Das eignet sich für einen Knoten, der Cluster-Ereignisse empfangen muss, aber nie die Leader-Rolle innehaben darf. |
| `Cluster:LeaseTtlSeconds` | `Cluster__LeaseTtlSeconds` | `30` | Dauer der Leader-Lease. Wird ungefähr nach der Hälfte dieses Intervalls erneuert. |
| `Cluster:PollIntervalSeconds` | `Cluster__PollIntervalSeconds` | `3` | Wie oft das Event-Bus-Backend nach Nachrichten fragt, die andere Knoten veröffentlicht haben. |

**Deployments mit mehreren Knoten** setzen über den Callback `configureClustering` bei `AddAuthagonal` / `AddAuthagonalCore` ein echtes Backend ein:

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS equivalent (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// Self-hosted PostgreSQL (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` registrieren nur den Event-Bus und behalten die Lease im Prozess bei, für Knoten, die Cluster-Ereignisse empfangen müssen, sich aber nie um die Leader-Rolle bewerben dürfen.

Wie sich Leader-Rolle und Event-Bus über mehrere Instanzen hinweg verhalten, erfahren Sie unter [Skalierung](scaling).

## Forwarded-Header (vertrauenswürdiger Proxy) {#forwarded-headers-trusted-proxy}

Authagonal bindet Ratenbegrenzung und Kontosperre an die Client-IP und sendet HSTS nur bei HTTPS-Anfragen. Hinter einem Reverse Proxy / Ingress kommen die echte Client-IP und das Schema in den Headern `X-Forwarded-For` / `X-Forwarded-Proto` an. Diese Einstellungen steuern, **welchen Proxy-Hops vertraut wird**, diese Werte zu setzen, damit ein Aufrufer die Client-IP nicht durch einen gefälschten `X-Forwarded-For` vortäuschen kann.

| Einstellung | Umgebungsvariable | Standard | Beschreibung |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | Anzahl der Proxy-Hops, die von rechts in der Kette `X-Forwarded-For` anerkannt werden. Der Standard `1` vertraut nur dem einen Hop, den Ihr Ingress anhängt, und ignoriert alles, was weiter links in der Kette steht. |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0` (Array) | *(leer)* | CIDR-Bereiche (String-Array, z. B. `"10.0.0.0/8"`), die Forwarded-Header setzen dürfen. Setzen Sie hier das CIDR Ihres Proxys / Ingress / Ihrer Pods. Erst diese Deklaration erlaubt es überhaupt, `X-Forwarded-Proto` anzuerkennen; siehe unten. |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies__0` (Array) | *(leer)* | Einzelne Proxy-IP-Adressen (String-Array), die Forwarded-Header setzen dürfen. Zusätzlich zu `KnownNetworks` oder stattdessen verwenden. |

```json
{
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"],
    "KnownProxies": []
  }
}
```

### Den beiden Headern wird nicht unter denselben Bedingungen vertraut {#the-two-headers-are-not-trusted-on-the-same-terms}

`X-Forwarded-For` verändert die **Client-IP**, an der Ratenbegrenzung, Kontosperre und der Schutz von `/_internal` hängen. Ist nichts deklariert, erkennt Authagonal den Header aus Loopback und den Bereichen nach RFC1918 an und protokolliert eine Warnung. Das ist ein Standard nach bestem Bemühen, und er ist besser als das Verhalten des Frameworks bei leerer Vertrauensmenge, das darin besteht, den Header von *jedem* beliebigen Aufrufer anzuerkennen.

`X-Forwarded-Proto` verändert das **Schema**, und das Schema entscheidet, ob `/connect/*` überhaupt antwortet (RFC 6749 §3.1/§3.2), ob Cookies als `Secure` markiert werden und ob erzeugte absolute URLs https verwenden. Der Header wird **nur** von einem Proxy anerkannt, den Sie in `KnownNetworks` / `KnownProxies` deklariert haben. Eine private Adresse ist keine Deklaration: Authagonal wird als Bibliothek ausgeliefert und kann das Netzwerk, in dem es betrieben wird, nicht sehen; „der Peer hat eine private Adresse“ ist also eine Vermutung über die Topologie. In einem flachen LAN, einer gemeinsam genutzten VPC oder einer gemeinsam genutzten Container-Bridge liegt jeder benachbarte Workload innerhalb dieser Bereiche und könnte für eine Anfrage, die unverschlüsselt ankam, `https` behaupten.

**Hat Ihr Proxy keine feste Adresse** (ein Kubernetes-Ingress, ein Load Balancer mit wechselnden Adressen, eine Plattform, die Ihnen das CIDR des Hops nicht nennt), deklarieren Sie jeden Peer als Proxy:

```json
{
  "ForwardedHeaders": {
    "KnownNetworks": ["0.0.0.0/0", "::/0"]
  }
}
```

Das ist genau dann sicher, wenn nichts außer dem Proxy den Prozess erreichen kann, und genau darauf verlässt sich ein solches Deployment ohnehin. Wenn Sie das ausdrücklich festhalten, steht es an einer Stelle, an der es überprüft werden kann, statt dass die Bibliothek es erschließen muss. Wenn andere Workloads Kestrel *doch* direkt erreichen können, können sie unter dieser Einstellung Schema und Client-IP fälschen; legen Sie dann stattdessen das tatsächliche CIDR fest.

### Nicht deklarierter Proxy: Jedes Kontingent pro Quelle wird geteilt {#undeclared-proxy-every-per-source-quota-is-shared}

Ratenbegrenzungen, die an der Adresse des Aufrufers hängen (Anmeldung, Registrierung, „Passwort vergessen“, dynamische Client-Registrierung, der SAML-ACS), müssen wissen, welcher Client die Anfrage gestellt hat. Hinter einem Reverse Proxy ist das die weitergeleitete Client-IP, und die weitergeleitete Client-IP ist nur dann ein Beleg, wenn Sie den Proxy deklariert haben, der sie geschrieben hat. Ist nichts deklariert, bindet Authagonal diese Kontingente an den Peer, den es tatsächlich sieht, und das ist hinter einem Proxy der Proxy: **Alle Clients teilen sich ein Budget, und jeder einzelne Aufrufer kann es für alle aufbrauchen** (der Standard für die Anmeldung sind 30 Versuche pro 5 Minuten).

Das ist Absicht und kein Fehler, und es lässt sich im Server nicht beheben. Die Alternative, die Kontingente trotzdem an den weitergeleiteten Wert zu binden, gibt einem Aufrufer pro Anfrage ein neues Budget, sobald er einen einzigen Header variiert, denn hinter einem L4-Load-Balancer *ist* der äußerste rechte weitergeleitete Hop der eigene Header des Aufrufers. In welcher der beiden Situationen Sie sich befinden, ist genau das, was die Deklaration dem Server mitteilt und was ihm nichts anderes mitteilen kann. Deklarieren Sie den Proxy, und die Kontingente gelten pro Client.

> ⚠️ **Ein TLS-terminierender Proxy ist erforderlich, und er muss deklariert sein.** Authagonal muss hinter einem TLS-terminierenden Reverse Proxy laufen (oder TLS selbst terminieren). HSTS (`Strict-Transport-Security`) wird nur bei HTTPS-Anfragen gesendet, und die OAuth-Endpunkte lehnen unverschlüsselte Anfragen rundweg ab, sofern `Auth:AllowInsecureHttp` nicht gesetzt ist. Der Proxy muss also `X-Forwarded-Proto: https` weiterleiten **und** in `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` genannt sein, damit HSTS gesendet wird und `/connect/*` überhaupt antwortet. Nichts zu deklarieren ist der häufigste Fehler beim Upgrade: Der Header kommt an, nichts ist berechtigt, ihn auszuwerten, und jede Anfrage an `/connect/*` antwortet mit 400, obwohl das Deployment tatsächlich TLS verwendet. Das Startlog weist darauf hin, ebenso der Body der Ablehnung.

## Ausgehende Abrufe (SSRF-Schutz) {#outbound-fetches-ssrf-guard}

Authagonal stellt vom Server initiierte HTTP-Anfragen an URLs, die es nicht selbst gewählt hat: an das SAML-Metadaten- oder OIDC-Discovery-Dokument eines Upstream-IdP, an die `jwks_uri` eines Clients bei der Authentifizierung per `private_key_jwt`, an eine Back-Channel-Logout-URI, an einen Provisionierungs-Callback. Einige dieser URLs stammen von demjenigen, der einen Client registriert hat, und eine URL, die `169.254.169.254` oder einen Host in Ihrem Cluster nennt, ist dann eine Anfrage, die Authagonal im Auftrag eines Angreifers stellt.

Jeder dieser Abrufe ist doppelt abgesichert. Die **URL-Prüfung** lehnt Schemata außer http(s), literale interne Adressen sowie die Namen `localhost` / `.local` / `.internal` ab, und zwar an der Stelle, an der die URL angenommen wird (ein Schreibvorgang über die Admin-API, eine dynamische Client-Registrierung), wo sich der Fehler demjenigen zuordnen lässt, der sie eingegeben hat. Die **Adressprüfung** läuft am Socket: Sie löst den Host auf, lehnt jede zurückgegebene interne Adresse ab und verbindet sich mit einer Adresse, die sie tatsächlich geprüft hat, statt den Namen an das Betriebssystem zurückzugeben. Genau das kann eine Textprüfung nicht leisten, denn ein Hostname ist kein Text, bei dem der Angreifer ehrlich sein muss: `logout.attacker.test` besteht jede Suffix- und Literalregel und antwortet dann mit der Adresse des Cloud-Metadatendienstes. Weil eine Weiterleitung eine neue Verbindung ist, läuft die Adressprüfung bei jedem Hop erneut.

Beide sind standardmäßig aktiv, und die meisten Deployments bemerken sie nie. Zwei Dinge machen sie sichtbar.

### Ein internes Ziel absichtlich erreichen {#reaching-an-internal-destination-on-purpose}

Die Föderation mit einem IdP, der nur über Ihr privates Netzwerk erreichbar ist, oder die Provisionierung einer App, die im selben Cluster läuft, wird durch genau dieselbe Regel abgelehnt, die den Angriff stoppt. Benennen Sie diese Ziele:

```json
{
  "Auth": {
    "AllowedInternalTargets": ["idp.corp.internal", "*.svc.corp.internal", "10.4.0.0/16"]
  }
}
```

| Form des Eintrags | Erlaubt |
|---|---|
| `idp.corp.internal` | Genau diesen Host und jede Adresse, in die er aufgelöst wird |
| `*.corp.internal` | Jeden Host unter dem Suffix und jede Adresse, in die diese aufgelöst werden |
| `10.4.0.0/16`, `fd00:1234::/48` | Dieses Netzwerk, unter jedem Namen |
| `10.4.1.7` | Diese einzelne Adresse, unter jedem Namen |

Als Umgebungsvariable lautet die Form `Auth__AllowedInternalTargets__0`, `__1` und so weiter. Ein fehlerhafter CIDR-Eintrag schlägt beim Start fehl, statt stumm gar nichts zu erlauben.

**Diese Liste erreicht nur die URLs, die Sie angegeben haben**: den Abruf von Upstream-SAML-Metadaten, die Upstream-OIDC-Discovery (einschließlich `token_endpoint`, `userinfo_endpoint` und `jwks_uri`, die dieses Dokument nennt) und Provisionierungs-Callbacks. Sie erreicht bewusst **nicht** eine vom Client registrierte `jwks_uri` oder Back-Channel-Logout-URI, wo ein interner Host nie zu einem Deployment gehört; das Öffnen eines Föderationsziels kann den Metadatendienst also nicht zugleich für eine anonyme Anfrage an `/connect/token` öffnen. Einen globalen Ausschalter gibt es nicht.

Beachten Sie, dass https für beide Metadaten-URLs der Föderation unabhängig von dieser Liste weiterhin erforderlich ist. Dieses Dokument enthält die Schlüssel und Zertifikate, gegen die jede Upstream-Assertion geprüft wird, und ein privates Netzwerk ist kein sicherer Kanal.

> ⚠️ **Mandantenfähige Hosts: Prüfen Sie, wer die Metadaten-URL schreibt, bevor Sie etwas eintragen.** Diese Liste gilt für Ziele, die *Sie* konfiguriert haben, und in einem Deployment mit einem einzigen Mandanten sind Sie selbst der Administrator der Verbindungen. Betreiben Sie Authagonal für andere (ein SaaS, in dem Mandanten-Administratoren ihre eigenen SAML-/OIDC-Verbindungen über das Portal oder die Admin-API konfigurieren), dann wird `MetadataLocation` vom **Kunden** angegeben, und jeder Eintrag, den Sie hier hinzufügen, ist für jeden Mandanten erreichbar, der eine Verbindung darauf richtet. Lassen Sie die Liste auf einem solchen Host leer (der Standard), und wenn ein Mandant wirklich einen On-Premises-IdP braucht, geben Sie ihm einen ausgehenden Pfad, der außerhalb Ihres Netzwerks endet, statt einen von innen heraus zu öffnen.

### Wenn Ihr ausgehender Verkehr einen HTTP-Proxy erfordert {#if-your-egress-requires-an-http-proxy}

Die Adressprüfung hängt an `SocketsHttpHandler.ConnectCallback`, und wenn ein Proxy aktiv ist, ruft .NET diesen Callback mit dem Endpunkt des **Proxys** auf und nie mit dem des Ziels. Die Prüfung würde also den Proxy untersuchen, ihn als einwandfrei routbar befinden und alles erlauben. Sie würde ausgerechnet in den Netzwerken, die am ehesten einen Proxy haben, im Fehlerfall alles durchlassen (Fail-Open). Deshalb setzen die abgesicherten Clients `UseProxy = false`, und in einem Netzwerk, das nur über einen Proxy nach außen kommt, schlagen ihre Abrufe fehl.

`Auth:AllowOutboundProxy` schickt die vom Betreiber konfigurierten Abrufe (SAML-Metadaten, OIDC-Discovery, Provisionierungs-Callbacks) wieder über den Proxy. Für sie behalten Sie die URL-Prüfung und verlieren die Adressprüfung: Ein Hostname, der in eine interne Adresse aufgelöst wird, wird nicht mehr abgefangen. Die Option erreicht **nicht** den Abruf der Client-`jwks_uri` oder die Zustellung des Back-Channel-Logouts: Diese Ziele wählt der Registrant, und sie sind aus anonymen Anfragen erreichbar, daher gibt es für sie keinen Schalter. Ein Netzwerk, das diese über einen Proxy leiten muss, braucht davor ein SSRF-filterndes Egress-Gateway.

`UseAuthagonal()` protokolliert beim Start eine Warnung, wenn `HTTPS_PROXY`, `HTTP_PROXY` oder `ALL_PROXY` gesetzt ist, und nennt die Clients, die den Proxy umgehen; andernfalls lautet das Symptom „SSO funktioniert nicht mehr“, ohne dass etwas auf die Ursache hindeutet.

### Was nicht abgesichert ist {#what-is-not-guarded}

Die ausgehenden Clients des BFF und der E-Mail-Versand. `AuthagonalBffOptions.Upstreams[].TargetBaseUrl` ist Ihre eigene Konfiguration, deren dokumentiertes Beispiel eine interne Adresse ist; der Token-Client des BFF spricht mit der Authority, die Sie konfiguriert haben, und der Proxy lehnt bereits jedes zusammengesetzte Ziel ab, das die konfigurierte Upstream-Authority verlassen hat, sodass ein Aufrufer diese Anfragen nicht umlenken kann. `Resend` sendet per POST an eine zur Kompilierzeit festgelegte Konstante. Alle drei nutzen den Umgebungs-Proxy ganz normal.

## Ratenbegrenzung {#rate-limiting}

Eingebaute Ratenbegrenzungen schützen die missbrauchsanfälligen Endpunkte:

| Endpunkt | Grenze | Zeitfenster | Bezogen auf |
|---|---|---|---|
| `POST /api/auth/login` | 30 (`Auth:MaxLoginAttemptsPerIp`) | 5 Minuten (`Auth:LoginWindowMinutes`) | Quelladresse und separat die übermittelte E-Mail-Adresse |
| `POST /api/auth/register` | 5 (`Auth:MaxRegistrationsPerIp`) | 1 Stunde (`Auth:RegistrationWindowMinutes`) | Client-IP |
| `POST /api/auth/forgot-password` | 3 (`Auth:MaxPasswordResetsPerEmail`) | 1 Stunde (`Auth:PasswordResetWindowMinutes`) | Ziel-E-Mail-Adresse |
| `POST /api/auth/forgot-password` | 15 (`Auth:MaxPasswordResetsPerIp`) | 1 Stunde (`Auth:PasswordResetWindowMinutes`) | Client-IP |
| `POST /connect/register` (wenn aktiviert) | 10 | 1 Stunde | Client-IP |
| SCIM-Endpunkte | 200 | 1 Minute | SCIM-Client |

Die Grenzen werden standardmäßig **im Prozess pro Knoten** durchgesetzt (hinter der Nahtstelle `IRateLimiter`); bei N Instanzen beträgt die effektive Obergrenze also das N-Fache des konfigurierten Werts. Betrachten Sie sie als Absicherung und setzen Sie die maßgebliche globale Grenze am Edge durch (WAF / Ingress / CDN). Siehe [Skalierung](scaling#rate-limiting).

### Clusterweite Begrenzungen (`Auth:DurableRateLimiting`) {#cluster-wide-limits-authdurableratelimiting}

Setzen Sie `Auth:DurableRateLimiting` auf `true`, um die Zähler in den Store zu verlagern, den das Deployment
ohnehin betreibt, sodass sich alle Replikate ein Budget teilen und sich die Obergrenze nicht mehr mit der Anzahl der Instanzen vervielfacht.

| | im Prozess (Standard) | dauerhaft |
|---|---|---|
| Obergrenze bei N Replikaten | das N-Fache des konfigurierten Werts | der konfigurierte Wert |
| Kosten pro Prüfung | keine | ein Roundtrip zum Store |
| Übersteht einen Pod-Neustart | nein | ja |
| Backends | alle | Azure Table, SQL, DynamoDB |

Lohnt sich, wenn ein Budget etwas Erratbares schützt, allen voran den `user_code` des Device Flow, bei dem
die Versuchsgrenze das Einzige ist, was zwischen einem Angreifer und einem Code steht, der eine aktive Sitzung gewährt, und ein
Budget, das mit der Anzahl der Replikate wächst, die falsche Form hat. Weniger nützlich für die Mengenbegrenzungen, bei denen der
Edge ohnehin die maßgebliche Grenze ist.

Details, die in der Produktion wichtig sind:

- **Es ist nicht kostenlos.** Jede Prüfung der Ratenbegrenzung wird zu einem Roundtrip zum Store, auch auf den Anmelde-, Token-
  und SCIM-Pfaden. Ein Deployment mit einem einzigen Knoten gewinnt nichts (dort *ist* pro Knoten gleich clusterweit) und sollte
  die Option ausgeschaltet lassen.
- **Feste Zeitfenster, Bursts können also eine Fenstergrenze überspannen.** Ein Budget von N bedeutet „N pro Zeitfenster und bis zu 2N
  über eine Fenstergrenze hinweg“, und die ausgelieferten Budgets haben diesen Spielraum. Nur so kann der Zähler auf jedem Backend
  ein einzelnes atomares Inkrement sein, und auf dieser Eigenschaft beruht die Korrektheit.
- **Im Fehlerfall lässt es Anfragen durch.** Ist der Store nicht erreichbar, wird die Anfrage zugelassen und ein Fehler protokolliert: Der
  Limiter schützt den Anmeldepfad und darf nicht zu einem Mittel werden, ihn lahmzulegen. Behalten Sie die Regel am Edge bei.
- **Der Host startet nicht**, wenn Sie die Option ohne einen Provider setzen, der `IRateLimitCounterStore` bereitstellt.
  Er verweigert den Start, statt stillschweigend auf die Begrenzung pro Knoten zurückzufallen, die Sie gerade abgeschaltet haben.
- **Zählerzeilen werden automatisch bereinigt**: bei DynamoDB per nativem TTL, bei SQL durch `SqlExpiryReaper`, bei Azure
  Table durch eine Bereinigung, die nur auf dem Leader läuft (Table Storage hat weder TTL noch serverseitige Arithmetik, daher ist es auch
  das Backend, bei dem ein Inkrement einen Lesevorgang plus einen bedingten Schreibvorgang kostet).

## CORS {#cors}

CORS wird dynamisch konfiguriert und ist **nach Pfad eingeschränkt**: Die frühere einzeilige Beschreibung („Origins aller
registrierten Clients werden automatisch zugelassen“) beschrieb erheblich mehr, als der Provider tut.

- **Vom Client registrierte Origins** (`AllowedCorsOrigins` bei einem Client) werden nur unter `/connect/` und
  `/.well-known/` anerkannt. Sie öffnen **nicht** `/api/auth/`, `/api/v1/` oder `/scim/`. Ein deaktivierter Client trägt
  nichts bei, und ein fehlerhafter Origin wird verworfen.
- **Credentials werden nie zugelassen** unter `/api/auth/`, `/api/v1/`, `/scim/`, `/consent` oder `/approvals`, für
  keinen Origin, ob vom Betreiber konfiguriert oder vom Client registriert. Ein Browser-Client, der diese mit
  `credentials: 'include'` von einem anderen Origin aufruft, scheitert unabhängig von der Konfiguration; verwenden Sie ein
  Backend-for-Frontend (siehe das Paket `@authagonal/bff`) statt Cross-Origin-Aufrufen mit Credentials.
- Aufgelöste Richtlinien werden 60 Minuten lang zwischengespeichert.

Ein Origin, der den `AllowedCorsOrigins` eines Clients hinzugefügt wird, lässt also `/connect/*` funktionieren, nicht aber `/api/v1/*`.
Das ist Absicht: Diese Pfade tragen das Sitzungscookie und die Admin-Oberfläche.

## HashiCorp Vault Transit {#hashicorp-vault-transit}

`VaultTransitClient` spricht mit der Transit-Secrets-Engine von Vault: Signieren, Prüfen, Verschlüsseln, Entschlüsseln und schlüsselbasiertes HMAC.
Er ist der Baustein für einen Vault-gestützten `IFieldCipher` oder `IIndexTokenizer`, den Sie selbst registrieren.

**Das Signieren von JWTs wird nicht an Vault delegiert.** `ProtocolKeyManager` signiert immer mit dem Schlüssel aus
`ISigningKeyStore`, und es gibt keine Nahtstelle, die stattdessen einen Vault-Schlüssel einsetzt. Was dafür nötig wäre, erfahren Sie unter
[Erweiterbarkeit](extensibility).

Beim Hosting als Bibliothek wird dies programmatisch konfiguriert.

## Vollständiges Beispiel {#full-example}

```json
{
  "Storage": {
    "TableServiceUri": "https://myaccount.table.core.windows.net/",
    "NameIndexesEnabled": true
  },
  "Issuer": "https://auth.example.com",
  "LoginAppUrl": "/login",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "RefreshTokenReuseGraceSeconds": 0,
    "DynamicClientRegistrationEnabled": false,
    "SigningKeyLifetimeDays": 90
  },
  "SecretProvider": {
    "VaultUri": "https://my-vault.vault.azure.net/"
  },
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"]
  },
  "Cluster": {
    "Enabled": true,
    "Secret": "shared-secret-here"
  },
  "AdminApi": {
    "Enabled": true,
    "Scope": "authagonal-admin"
  },
  "Authentication": {
    "CookieLifetimeHours": 48
  },
  "PasswordPolicy": {
    "MinLength": 8,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": true
  },
  "Email": {
    "ResendApiKey": "re_xxx",
    "SenderEmail": "noreply@example.com",
    "SenderName": "Example Auth"
  },
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com"]
    }
  ],
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "...",
      "ClientSecret": "...",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["gmail.com"]
    }
  ],
  "ProvisioningApps": {
    "backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret"
    }
  },
  "Clients": [
    {
      "ClientId": "web",
      "ClientName": "Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "MfaPolicy": "Enabled",
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
