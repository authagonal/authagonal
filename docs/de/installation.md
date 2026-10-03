---
layout: default
title: Installation
locale: de
---

# Installation

## Docker (empfohlen) {#docker-recommended}

Laden Sie das vorgefertigte Image und starten Sie es:

```bash
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  drawboardci/authagonal
```

## Docker Compose {#docker-compose}

Für die lokale Entwicklung mit Azurite (dem Azure-Storage-Emulator):

```yaml
services:
  azurite:
    image: mcr.microsoft.com/azure-storage/azurite
    ports:
      - "10000:10000"
      - "10001:10001"
      - "10002:10002"

  authagonal:
    build: .
    ports:
      - "8080:8080"
    environment:
      - Storage__ConnectionString=DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://azurite:10002/devstoreaccount1;
      - Issuer=http://localhost:8080
      # Local development only: the OAuth endpoints answer plain http. See below.
      - Auth__AllowInsecureHttp=true
    depends_on:
      - azurite
```

```bash
docker compose up
```

> ⚠️ **`Auth:AllowInsecureHttp` ist eine Entwicklungseinstellung.** RFC 6749 §3.1/§3.2 schreiben TLS an Autorisierungs- und Token-Endpunkt vor, daher lehnt Authagonal Anfragen an `/connect/*` ohne https ab, sofern diese Einstellung nicht gesetzt ist. Das Schema wird erst nach der Verarbeitung der Forwarded-Header gelesen. Ein Proxy, der TLS terminiert und `X-Forwarded-Proto: https` weiterreicht, erfüllt die Anforderung also auch bei ausgeschalteter Einstellung, und genau so sollte jedes Deployment arbeiten, das außer Ihnen irgendjemand erreichen kann. Ist sie eingeschaltet, liest ein Beobachter auf dem Übertragungsweg den Autorisierungscode, das Client-Secret im Header `Authorization: Basic` sowie Access und Refresh Tokens mit. Siehe [Konfiguration](configuration#authentication).

## Aus dem Quellcode bauen {#building-from-source}

### Voraussetzungen {#prerequisites}

- .NET 10 SDK
- Node.js 24+

Authagonal zielt auf `net9.0` und `net10.0` und benötigt zur Laufzeit ein **gepatchtes** Shared Framework: **mindestens 9.0.18 bzw. 10.0.10**. Die Begründung steht in der [Sicherheitscheckliste für die Produktion](#production-security-checklist); mit `Auth:RequireMinimumRuntime` wird aus der Prüfung beim Start eine Verweigerung des Starts.

### Bauen {#build}

```bash
# Build everything
dotnet build

# Build the login SPA
cd login-app
npm ci
npm run build

# Run the server
dotnet run --project src/Authagonal.Server
```

### Docker-Build {#docker-build}

```bash
# Server image (multi-stage: builds SPA + .NET in one image)
docker build -t authagonal .

# Migration tool
docker build -f Dockerfile.migration -t authagonal-migration .
```

## Als Bibliothek (NuGet) {#as-a-library-nuget}

Referenzieren Sie die Authagonal-Pakete in Ihrem eigenen ASP.NET-Core-Projekt:

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.AzureProvider" Version="x.y.z" />
```

Das Speicherprovider-Paket ist austauschbar: `Authagonal.AzureProvider` für Azure Table Storage (die Standardverdrahtung von `AddAuthagonal()`), `Authagonal.SqlProvider` für selbst gehostetes PostgreSQL oder SQLite (siehe [SQL-Backend](#sql-backend)) oder `Authagonal.AwsProvider` für DynamoDB / S3 / Secrets Manager (siehe [AWS-Backend](#aws-backend)).

> **Die Reihenfolge der Registrierung ist entscheidend.** Ein Speicherprovider muss **vor** `AddAuthagonal()` registriert werden. Genau diese bereits vorhandene `IUserStore`-Registrierung veranlasst `AddAuthagonal()`, seine eingebaute Verdrahtung für Azure Table Storage zu überspringen. Ein danach registrierter Provider verliert stillschweigend jede Schnittstelle, die `AddAuthagonal()` bereits belegt hat, weil diese Registrierungen `TryAdd` verwenden.
>
> Drei Schnittstellen (`IOrganizationStore`, `IOrganizationMembershipStore` und `IScimGroupRoleMappingStore`) haben leere, schreibgeschützte In-Memory-Fallbacks, damit die DI auch auf einem Host auflöst, der für sie überhaupt keinen Store verdrahtet. `AddAuthagonal()` räumt diese Fallbacks aus dem Weg, bevor sich der Speicherprovider registriert, und stellt sie danach per `TryAdd` wieder her. So gewinnt der dauerhafte Store eines Providers immer, und der Fallback deckt trotzdem einen Host ohne solchen Store ab. Wenn Sie eine eigene Implementierung einer dieser drei registrieren, registrieren Sie sie wie jeden anderen Store vor `AddAuthagonal()`.

Fügen Sie das Ganze dann in Ihrer `Program.cs` zusammen:

```csharp
builder.Services.AddSingleton<IAuthHook, MyAuditHook>();   // Custom hook
builder.Services.AddSingleton<IEmailService, MyEmailService>(); // Custom email
builder.Services.AddAuthagonal(builder.Configuration);

var app = builder.Build();
app.UseAuthagonal();
app.MapAuthagonalEndpoints();
app.MapFallbackToFile("index.html");
app.Run();
```

Alle Erweiterungspunkte beschreibt [Erweiterbarkeit](extensibility), ein vollständiges Beispiel finden Sie unter [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server).

### E-Mail {#email}

Der eingebaute Versand über [Resend](https://resend.com) wird automatisch aktiv, sobald `Email:ResendApiKey` und `Email:SenderEmail` konfiguriert sind; eine Service-Registrierung ist nicht nötig. Ohne jedes `IEmailService` werden Bestätigungs- und Passwort-Reset-E-Mails **stillschweigend verworfen**, und weil die Anmeldung standardmäßig eine bestätigte E-Mail-Adresse voraussetzt, können sich selbst registrierte Benutzer nie anmelden (`UseAuthagonal` protokolliert beim Start eine Warnung). Setzen Sie entweder die `Email:*`-Schlüssel, registrieren Sie vor `AddAuthagonal()` ein eigenes `IEmailService` oder führen Sie Ihre Domains in `Auth:AutoConfirmEmailDomains` auf, um die Bestätigung zu überspringen (nur für Entwicklung und Tests). Siehe [Konfiguration → E-Mail](configuration#email).

## SQL-Backend {#sql-backend}

Um auf Ihrer eigenen Datenbank statt auf einem Cloud-Dienst zu laufen, referenzieren Sie `Authagonal.SqlProvider` und registrieren es **vor** `AddAuthagonal()`; diese Registrierungen veranlassen `AddAuthagonal()`, seine Verdrahtung für Azure Table Storage zu überspringen:

```csharp
using Authagonal.SqlProvider;

// PostgreSQL: the production self-hosted backend
builder.Services.AddAuthagonalPostgres(
    "Host=db;Database=authagonal;Username=auth;Password=…;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/db-ca.pem");

// or SQLite: one file, no server. Suits embedded hosts, CI and small single-node deployments
builder.Services.AddAuthagonalSqlite("Data Source=authagonal.db");

builder.Services.AddAuthagonal(builder.Configuration);
```

Die Tabellen bilden die Azure- und DynamoDB-Layouts eins zu eins ab und werden beim Start angelegt, falls sie fehlen (jede Anweisung ist `IF NOT EXISTS`; parallele Starts mehrerer Pods sind also unbedenklich, und gegen ein Schema, das Sie selbst bereitgestellt haben, ist sie wirkungslos). Eine `Storage:*`-Konfiguration ist nicht nötig. Der DataProtection-Schlüsselring wird in derselben Datenbank gespeichert, sodass Cookies und Antiforgery-Tokens Neustarts überstehen und ohne zusätzlichen Dienst über mehrere Pods hinweg funktionieren.

SQLite serialisiert Schreibvorgänge und ist daher ein Backend für einen einzelnen Knoten: Die standardmäßig registrierte prozessinterne Lease und der Cluster-Event-Bus sind dort die richtige Kombination. Ein PostgreSQL-Deployment mit mehreren Pods braucht für die Leader-Wahl `clustering.UseSql(dataSource)`.

> **Kollation.** Unter PostgreSQL sind die Schlüsselspalten auf `COLLATE "C"` festgelegt. Das Schlüsselschema ist durchgängig byte-ordinal (Präfixgrenzen, Bereiche der Umgebungspartitionen, die Bereinigung abgelaufener Grants, Keyset-Paginierung), und eine Datenbank, die mit einer linguistischen Kollation angelegt wurde (`en_US.UTF-8` und ICU-Locales sind die üblichen Standards), würde Satzzeichen und Groß-/Kleinschreibung anders sortieren und stillschweigend die falschen Zeilen liefern. Die Festlegung macht das Layout unabhängig davon, wie die Datenbank angelegt wurde; Sie müssen sie nicht auf eine bestimmte Weise anlegen.

> ⚠️ **Schlüsselmaterial liegt in dieser Datenbank.** Auf Azure liegt der Token-Signaturschlüssel in Table Storage und der DataProtection-Ring in einem Blob-Container, jeweils mit unabhängig vergebbarem RBAC; auf AWS in DynamoDB und S3. Unter SQL sind beide Tabellen hinter demselben Connection String wie alles andere. Behandeln Sie den Connection String daher wie den Signaturschlüssel selbst: Ein `pg_dump`, ein Lesereplikat, eine Analytics-Rolle mit `SELECT` oder ein wiederhergestelltes Backup liefert sonst sowohl die Möglichkeit, Tokens für beliebige Subjekte auszustellen, als auch die Schlüssel hinter jedem Auth-Cookie. Registrieren Sie vor `AddAuthagonalPostgres()` ein `IFieldCipher`, um `SigningKeys.keyMaterialJson` im Ruhezustand zu verschlüsseln, und setzen Sie `DataProtection:KeyVaultKeyId` oder `DataProtection:CertificateThumbprint`, damit der Schlüsselring nicht mit einem ungeschützten `<masterKey>` gespeichert wird: Ein neues Deployment, das den Ring ohne einen dieser Schutzmechanismen speichert, wird beim Start abgelehnt, und für ein bestehendes wird bei jedem Start eine Warnung auf Stufe `Critical` protokolliert. Beides sowie das Ablegen des Schlüsselrings in einem separaten Schema mit eigener Rolle beschreibt die [README des Pakets](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider#dataprotection-keys).

Das Tabellenlayout, die Nebenläufigkeitsprimitive hinter jeder Einmalverwendungsgarantie und das Hinzufügen eines Dialekts für eine weitere Datenbank-Engine beschreibt die [README des Pakets](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider).

## AWS-Backend {#aws-backend}

Um auf AWS statt auf Azure zu laufen, referenzieren Sie `Authagonal.AwsProvider` und registrieren das AWS-Bündel **vor** `AddAuthagonal()`; diese Registrierungen veranlassen `AddAuthagonal()`, seine Verdrahtung für Azure Table Storage zu überspringen:

```csharp
using Authagonal.AwsProvider;

builder.Services.AddAuthagonalAwsStorage(
    dynamoDb,                // IAmazonDynamoDB: required
    secretsManager,          // IAmazonSecretsManager: optional; replaces the plaintext ISecretProvider
    s3,                      // IAmazonS3: optional; used for DataProtection keys
    "my-auth-keys-bucket");  // S3 bucket for the DataProtection key ring
builder.Services.AddAuthagonal(builder.Configuration);
```

Die DynamoDB-Tabellen bilden das Azure-Layout eins zu eins ab und werden beim Start sichergestellt (idempotent und wirkungslos, wenn sie bereits über Terraform bereitgestellt sind). Anmeldedaten werden über die übliche AWS-Kette aufgelöst (Umgebung / EC2-Instanzrolle / IRSA). Eine Unterscheidung zwischen Connection String und Managed Identity gibt es daher nicht, und eine `Storage:*`-Konfiguration ist nicht nötig.

> ⚠️ **DataProtection-Schlüssel in S3.** Ohne S3-Client und Bucket wird der Data-Protection-Schlüsselring von ASP.NET Core im Arbeitsspeicher gehalten. Für einen einzelnen Knoten in der Entwicklung ist das in Ordnung, in der Produktion funktionieren Cookies und Antiforgery-Tokens nach einem Neustart und über Knoten hinweg dann jedoch nicht mehr. Übergeben Sie für ein produktives AWS-Deployment immer den S3-Client und den Bucket.

## Login-SPA (npm) {#login-spa-npm}

Die Login-UI wird zur Anpassung als npm-Paket veröffentlicht:

```bash
npm install @authagonal/login react react-dom react-router
```

Das Paket enthält kompiliertes JS und CSS; importieren Sie Komponenten und Styles direkt in Ihre eigene React-App. Eine vollständige Anleitung finden Sie unter [Eigener Server](custom-server).

`react`, `react-dom` und `react-router` sind **Peer**-Abhängigkeiten: Der Build lagert sie aus, sodass die Komponenten die Kopien Ihrer Anwendung verwenden statt eigener. Nur deshalb können die exportierten Seiten `useNavigate` innerhalb Ihres `<BrowserRouter>` aufrufen und ihre Hooks gegen die React-Instanz ausführen, die sie rendert. Installieren Sie sie neben dem Paket, statt das Paket eigene mitbringen zu lassen.

## Backend-for-Frontend (BFF) {#backend-for-frontend-bff}

Wenn Ihre SPA APIs mit einem Bearer Token aufruft, verwahren Sie das Token in einem BFF statt im Browser. Das BFF wird als NuGet-Paket (`Authagonal.Bff`) und als npm-Paket (`@authagonal/bff`) veröffentlicht; keines von beiden ist Teil des Server-Images. Siehe [Backend-for-Frontend](bff).

## Sicherheitscheckliste für die Produktion {#production-security-checklist}

Bevor Sie Authagonal echtem Datenverkehr aussetzen, stellen Sie Folgendes sicher. Jeder Punkt wird auf der Seite [Konfiguration](configuration) im Detail beschrieben.

- **Betreiben Sie eine gepatchte .NET-Laufzeit: mindestens 9.0.18 bzw. 10.0.10.** Die Korrekturen für GHSA-37gx-xxp4-5rgx und GHSA-w3x6-4m5h-cxqf (eine Endlosschleife sowie ein Paar aus XXE und Ressourcenerschöpfung in `System.Security.Cryptography.Xml`, beide über den **anonymen** SAML-ACS-Endpunkt erreichbar) werden im Shared Framework ausgeliefert, nicht in einem Paket, das Authagonal referenzieren könnte. Nichts in Ihrem Abhängigkeitsgraphen kann sie daher garantieren. Authagonal protokolliert beim Start `Critical`, wenn die laufende Laufzeit unter der Mindestversion liegt; setzen Sie `Auth:RequireMinimumRuntime = true`, damit der Start stattdessen verweigert wird. Die veröffentlichten Container-Images laufen bereits auf einer Laufzeit auf oder über der Mindestversion.
- **Betreiben Sie den Server hinter einem TLS-terminierenden Proxy und deklarieren Sie diesen.** Authagonal muss hinter einem Reverse Proxy bzw. Ingress stehen, der TLS terminiert (oder TLS selbst terminieren). HSTS wird nur über HTTPS gesendet, und `/connect/*` lehnt Klartext ab. Der Proxy muss daher `X-Forwarded-Proto: https` weiterreichen, und dieser Header wird ignoriert, solange Sie `ForwardedHeaders:KnownNetworks` (oder `KnownProxies`) nicht auf das CIDR bzw. die Adresse Ihres Proxys setzen. Verwenden Sie `["0.0.0.0/0", "::/0"]`, wenn der Proxy keine feste Adresse hat und nichts anderes den Prozess erreichen kann. `ForwardedHeaders:ForwardLimit` ist standardmäßig `1` (nur dem letzten Hop vertrauen).
- **Setzen Sie `SecretProvider:VaultUri`.** Der Standard-Secret-Provider arbeitet im **Klartext**: Ohne Key Vault werden Client-Secrets von Upstream-OIDC-Providern sowie TOTP-/MFA-Seeds im Klartext in Table Storage (und in Backups) gespeichert. Konfigurieren Sie Key Vault für jedes produktive Deployment.
- **Sichern Sie die Admin-API ab.** `AdminApi:Enabled` ist standardmäßig **true**. Der Admin-Scope (`AdminApi:Scope`, Standard `authagonal-admin`) gewährt vollständige Verwaltungsrechte und die Impersonation von Benutzern. Beschränken Sie den Netzwerkzugriff auf die Admin-Routen unter `/api/v1/*` und kontrollieren Sie streng, wer den Admin-Scope erhält, oder setzen Sie `AdminApi:Enabled = false`, wenn Sie sie nicht nutzen.
- **Schützen Sie interne Endpunkte.** Setzen Sie `Cluster:Secret`, damit der interne Endpunkt `/_internal/backchannel-logout` den Header `X-Cluster-Secret` verlangt (in konstanter Zeit verglichen). Ohne Secret autorisiert der Endpunkt **niemanden** und antwortet mit 404: Eine Quelladresse ist kein Berechtigungsnachweis, und Loopback ist genau das, was ein Reverse Proxy auf demselben Host bei jeder weitergeleiteten Anfrage präsentiert. `Cluster:AllowLoopbackWithoutSecret` lässt einen Loopback-Peer vor der Weiterleitungsauswertung wieder zu, und zwar nur für die lokale Entwicklung. Nichts im ausgelieferten Produkt ruft den Endpunkt auf, das sichere Fehlschlagen bricht also keinen eigenen Ablauf. Setzen Sie das Secret, wenn Sie Ihre eigene Pod-zu-Pod-Verteilung darauf aufbauen.
- **Verschlüsseln Sie Backups.** Mit dem Klartext-Secret-Provider enthalten Backups Secrets. Die Tabelle `SigningKeys` ist standardmäßig von Backups ausgenommen; wenn Sie sie über `Backup:IncludeSigningKeys` einbeziehen, muss das Backup-Ziel im Ruhezustand verschlüsselt sein. Siehe [Sicherung und Wiederherstellung](backup-restore).

## Migrationswerkzeug {#migration-tool}

Für die Migration von Duende IdentityServer + SQL Server:

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

Einzelheiten finden Sie unter [Migration](migration).
