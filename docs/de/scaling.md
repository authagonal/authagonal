---
layout: default
title: Skalierung
locale: de
---

# Skalierung

Authagonal ist darauf ausgelegt, ohne besondere Konfiguration sowohl vertikal als auch horizontal zu skalieren.

## Zustandslos durch Design {#stateless-by-design}

Der gesamte persistente Zustand liegt im zugrunde liegenden Speicher (Azure Table Storage, DynamoDB im AWS-Backend oder PostgreSQL im selbst gehosteten SQL-Backend). Es gibt keinen prozessinternen Zustand, der Sticky Sitzungen oder eine Abstimmung zwischen Instanzen erfordern würde:

- **Signaturschlüssel**: aus Table Storage geladen, stündlich aktualisiert
- **Autorisierungscodes und Refresh Tokens**: in Table Storage gespeichert, mit erzwungener Einmalverwendung
- **Schutz vor SAML-Replays**: Request-IDs werden in Table Storage mit atomarem Löschen nachverfolgt
- **OIDC-State und PKCE-Verifier**: in Table Storage gespeichert
- **Client- und Provider-Konfiguration**: pro Request aus Table Storage abgerufen

## Cookie-Verschlüsselung (Data Protection) {#cookie-encryption-data-protection}

Der Data-Protection-Schlüsselring von ASP.NET Core schützt das Auth-Cookie, daher müssen sich alle Instanzen einen gemeinsamen Ring teilen. Er wird automatisch persistiert, in dieser Reihenfolge:

1. `DataProtection:BlobUri`, falls gesetzt (ein expliziter Blob, authentifiziert mit `DefaultAzureCredential`).
2. Ein Container `dataprotection` in dem Konto, das `Storage:ConnectionString` benennt, sofern dies nicht Azurite ist.
3. Auf dem Managed-Identity-Pfad (`Storage:TableServiceUri`) der benachbarte Blob-Endpunkt desselben Kontos, `https://{account}.blob.…/dataprotection/keys.xml`. Die Identität benötigt die Rolle Storage Blob Data Contributor für das Konto.

Nur bei einem nicht erkannten Table-Endpunkt (Azurite, Emulatoren mit Pfad-Adressierung) wird auf den Dateispeicher pro Maschine zurückgegriffen, der flüchtig ist und pro Pod gilt: Neustarts melden alle Benutzer ab, und Replikate können die Cookies der jeweils anderen nicht lesen. Die Startprüfung protokolliert in diesem Fall `Critical`.

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

Übergeben Sie im AWS-Backend einen S3-Client samt Bucket an `AddAuthagonalAwsStorage`, um den Schlüsselring in S3 zu persistieren; ohne diese Angabe liegt der Schlüsselring nur im Arbeitsspeicher, und Cookies funktionieren nach einem Neustart und über Knoten hinweg nicht mehr. Siehe [Installation → AWS-Backend](installation#aws-backend). Im SQL-Backend persistiert `AddAuthagonalPostgres` / `AddAuthagonalSqlite` den Ring.

Persistieren ist nicht Verschlüsseln: Der Ring ist Klartext-XML, sofern nicht `DataProtection:KeyVaultKeyId` oder `DataProtection:CertificateThumbprint` gesetzt ist. Beim Start wird ein unverschlüsselter Ring, der noch keine Schlüssel enthält, abgelehnt; einer, der bereits Schlüssel enthält, startet mit einem `Critical`-Log (`DataProtection:AllowUnencryptedKeyRing=true` akzeptiert ihn bewusst). Die vollständige Tabelle der `DataProtection:*`-Einstellungen finden Sie unter [Konfiguration](configuration).

## Caches pro Instanz {#per-instance-caches}

Einige wenige häufig gelesene, sich selten ändernde Werte werden pro Instanz im Arbeitsspeicher zwischengespeichert, um Roundtrips zu Table Storage zu reduzieren:

| Daten | Cache-Dauer | Auswirkung veralteter Daten |
|---|---|---|
| OIDC-Discovery-Dokumente | 60 Minuten (konfigurierbar) | Schlüsselrotationen beim IdP werden verzögert bemerkt |
| SAML-IdP-Metadaten | 60 Minuten (konfigurierbar) | Ebenso |
| Erlaubte CORS-Origins | 60 Minuten (konfigurierbar) | Neue Origins werden erst nach bis zu einer Stunde wirksam |

Diese Caches sind für den Produktivbetrieb vertretbar. Alle Zeiträume lassen sich über den Konfigurationsabschnitt `Cache` einstellen, siehe [Konfiguration](configuration). Sollen Änderungen sofort wirksam werden, starten Sie die betroffenen Instanzen neu.

## Ratenbegrenzung {#rate-limiting}

Missbrauchsanfällige Endpunkte (Registrierung pro IP, Passwort-Reset pro Ziel-E-Mail, SCIM pro Client, dynamische Client-Registrierung pro IP, siehe [Konfiguration → Ratenbegrenzung](configuration#rate-limiting)) werden durch einen integrierten Rate Limiter geschützt.

Standardmäßig werden die Grenzen **prozessintern pro Knoten** hinter der Schnittstelle `IRateLimiter` durchgesetzt; bei N Instanzen liegt die effektive Obergrenze also beim N-Fachen des konfigurierten Werts. Das ist beabsichtigt: Der Limiter ist eine Absicherung gegen ausufernden Missbrauch eines einzelnen Knotens, und die maßgebliche globale Grenze gehört an den Rand des Netzes (WAF / Ingress / CDN), wo der gesamte Verkehr vor der Lastverteilung sichtbar ist.

Dieser Kompromiss passt für die Volumengrenzen, aber in einem Fall nicht: bei einem Budget, das ein **erratbares Geheimnis** schützt. Der `user_code` des Device Flow ist eine kurze Zeichenfolge aus einem kleinen Alphabet, und die Begrenzung der Versuche ist das Einzige, was zwischen einem Angreifer und einem Code steht, der eine aktive Sitzung gewährt. Eine Obergrenze, die sich mit der Zahl der Replikate vervielfacht, ist dort die falsche Form, und sie macht die tatsächliche Grenze zu einer Eigenschaft Ihrer Ingress-Konfiguration statt des Servers.

Setzen Sie **`Auth:DurableRateLimiting=true`**, um die Zähler in den Speicher zu verlagern, den Sie ohnehin betreiben; dann teilen sich alle Replikate ein gemeinsames Budget. Das kostet pro Prüfung einen Roundtrip zum Speicher, verwendet feste Zeitfenster (ein Budget von N erlaubt über eine Fenstergrenze hinweg bis zu 2N) und lässt Requests durch, wenn der Speicher nicht erreichbar ist (Fail-Open). Es ergänzt die Regel am Netzwerkrand also, statt sie zu ersetzen. Zählerzeilen werden in allen drei Backends automatisch bereinigt. Siehe [Konfiguration → Clusterweite Grenzen](configuration#cluster-wide-limits-authdurableratelimiting).

## Clustering {#clustering}

Mehrere Instanzen stimmen sich über eine **Leader-Wahl** und einen **knotenübergreifenden Event-Bus** ab, beide hinter austauschbaren Backends:

- **Leader-Wahl**: eine Lease-basierte Wahl (`Cluster:LeaseTtlSeconds`, Standard 30 s, erneuert etwa nach der Hälfte dieses Intervalls). Genau ein Knoten hält die Lease; fällt der Leader aus, geht die Führung automatisch über. An den Leader gebundene Aufgaben laufen nur auf dem Leader: die *Deaktivierung* von Signaturschlüsseln bei Ablauf (wenn `Auth:KeyRotationEnabled` aktiv ist), der Abgleichslauf für Grants (nur Azure-Backend), das Nachverschlüsseln ruhender Daten (wenn `Auth:AtRestBackfillEnabled` aktiv ist; ein Nicht-Leader wartet kurz auf die Führung und überspringt die Aufgabe dann) und die Bereinigung der Ratenbegrenzungs-Zähler (Azure-Backend mit `Auth:DurableRateLimiting`). Mit `Cluster:Enabled=false` ist der einzelne Knoten dauerhaft Leader, sodass auch eine eigenständige Bereitstellung all diese Aufgaben ausführt.
- **Event-Bus**: knotenübergreifende Benachrichtigungen (z. B. Cache-Invalidierung in mandantenfähigen Hosts), abgefragt alle `Cluster:PollIntervalSeconds` (Standard 3 s).

Jede Instanz erzeugt beim Start eine zufällige Knoten-ID aus 12 Hexadezimalzeichen, um sich zu identifizieren; sie wird nicht persistiert.

### Backends {#backends}

Der **Standard ist prozessintern**: Ein einzelner Knoten ist immer sein eigener Leader, und Events bleiben lokal. Für eine einzelne Instanz ist das ohne jede Konfiguration korrekt. Bereitstellungen mit mehreren Knoten setzen über den Callback `configureClustering` von `AddAuthagonal` ein echtes Backend ein:

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS: leadership + event bus via DynamoDB (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// PostgreSQL: leadership via a conditional-upsert lease row, event bus via an
// append-only log in the same database (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` registrieren nur den Event-Bus und behalten die prozessinterne Lease (immer Leader) bei. Verwenden Sie sie auf Knoten, die Cluster-Events empfangen müssen, aber nie um die Führung konkurrieren dürfen.

> **Hinweis:** Mit dem prozessinternen Standard auf mehreren Knoten hält sich *jeder* Knoten für den Leader. Für die meisten Workloads ist das unschädlich, aber aktivieren Sie ein echtes Lease-Backend, bevor Sie `Auth:KeyRotationEnabled` über mehrere Instanzen hinweg einschalten.

Die **Erzeugung** von Signaturschlüsseln ist von dieser an den Leader gebundenen Deaktivierung getrennt und wird nicht von ihr gesteuert: Jeder Knoten ruft `EnsureActiveKeyAsync` beim Start und bei jeder Aktualisierung gemäß `Auth:SigningKeyCacheRefreshMinutes` auf. Ist `KeyRotationEnabled` ausgeschaltet (der Standard), wird der Schlüsselwechsel beim Ablauf nach 90 Tagen also vollständig über diesen Pfad ausgelöst. Die Erzeugung nimmt eine eigene kurze Cluster-Lease, sodass es überall dort, wo ein echtes Lease-Backend konfiguriert ist, nur einen Schreiber gibt. Mit dem prozessinternen Standard auf mehreren Knoten gibt es keine solche Abstimmung, und zwei Knoten, die im selben Moment auf einen abgelaufenen Schlüssel stoßen, können jeweils einen erzeugen. Beide landen im JWKS, und mit beiden signierte Tokens lassen sich verifizieren, aber welcher Schlüssel als aktiv gemeldet wird, kann hin und her springen. Ein weiterer Grund, für Bereitstellungen mit mehreren Knoten ein echtes Lease-Backend zu konfigurieren.

Alle Cluster-Einstellungen finden Sie auf der Seite [Konfiguration](configuration#cluster).

### Mandantenfähige Bereitstellungen {#multi-tenant-deployments}

Im mandantenfähigen Modus (`AddAuthagonalCore()`) werden `TokenCleanupService`, `GrantReconciliationService`, `SigningKeyRotationService` und die Seed-Dienste (Clients, Provider, Scopes und Rollen aus der Konfiguration) nicht registriert: Sie gehören zur Single-Tenant-Komposition `AddAuthagonal()`, und der Host übernimmt diese Aufgaben pro Mandant.

## Hot Partition des Namensindex {#name-index-hot-partition}

Die Präfixsuche nach Namen in der Administration stützt sich auf die Indextabellen `UserFirstNames` / `UserLastNames`, die eine **einzige Hot Partition** verwenden. Bei großem Umfang begrenzt das den Schreibdurchsatz des Index auf etwa 2.000 Operationen pro Sekunde, was unter hoher Last beim Anlegen und Aktualisieren von Benutzern zum Engpass werden kann. Wenn Sie die Namenssuche in der Administration nicht anbieten, setzen Sie `Storage:NameIndexesEnabled = false`, um diese Schreibvorgänge ganz zu überspringen. Siehe [Konfiguration](configuration).

## Vertrauenswürdige Proxys und interne Endpunkte {#trusted-proxy-and-internal-endpoints}

Beim Betrieb mehrerer Instanzen hinter einem Load Balancer:

- **Forwarded Headers**: Ratenbegrenzung und Sperrung verwenden die Client-IP als Schlüssel, ermittelt aus `X-Forwarded-For`. Setzen Sie `ForwardedHeaders:KnownNetworks` auf das CIDR Ihres Ingress bzw. Ihrer Pods, damit die Client-IP nicht über Instanzen hinweg gefälscht werden kann. `ForwardedHeaders:ForwardLimit` ist standardmäßig `1`. Siehe [Konfiguration](configuration#forwarded-headers-trusted-proxy).
- **Interne Endpunkte**: `/_internal/backchannel-logout` erfordert `Cluster:Secret` im Header `X-Cluster-Secret` (in konstanter Zeit verglichen). Ohne das Secret autorisiert der Endpunkt niemanden und antwortet mit 404; die Quell-IP gilt nicht als Berechtigungsnachweis, weil ein Reverse Proxy auf demselben Host für jeden weitergeleiteten Request Loopback vorweist und ein privater Adressbereich jeden benachbarten Workload in einem gemeinsamen Cluster-Netz umfasst. `Cluster:AllowLoopbackWithoutSecret` ist eine nur für die Entwicklung gedachte Option, die einen Loopback-Peer ohne Weiterleitung wieder zulässt. Das ausgelieferte Produkt ruft diese Route nie auf (die Verteilung an Sitzungen erfolgt prozessintern über `SessionTermination`); sie ist also nur für eine Verteilung relevant, die Sie selbst bauen.

## Empfehlungen zur Skalierung {#scaling-recommendations}

**Vertikale Skalierung**: Erhöhen Sie CPU und Arbeitsspeicher einer einzelnen Instanz. Nützlich, um mehr gleichzeitige Requests pro Instanz zu bewältigen.

**Horizontale Skalierung**: Betreiben Sie mehrere Instanzen hinter einem Load Balancer. Weder Sticky Sitzungen noch gemeinsame Caches sind erforderlich. Jede Instanz ist vollständig unabhängig.

**Skalierung auf null**: Authagonal unterstützt Bereitstellungen mit Skalierung auf null (z. B. Azure Container Apps mit `minReplicas: 0`). Der erste Request nach einer Ruhephase hat einen Kaltstart von einigen Sekunden, während die .NET-Runtime initialisiert und die Signaturschlüssel aus dem Speicher geladen werden.
