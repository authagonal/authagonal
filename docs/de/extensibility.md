---
layout: default
title: Erweiterbarkeit
locale: de
---

# Erweiterbarkeit

Authagonal lässt sich als Bibliothek in Ihrem eigenen ASP.NET-Core-Projekt hosten, mit voller Kontrolle über die Implementierungen der Services.

## Erweiterungsmethoden {#extension-methods}

Drei Methoden binden Authagonal in eine beliebige ASP.NET-Core-App ein:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

### Mandantenfähiges Hosting {#multi-tenant-hosting}

Verwenden Sie für mandantenfähige Deployments stattdessen `AddAuthagonalCore()`. Es registriert Endpunkte, Middleware und die Kern-Services, lässt aber Speicher und Hintergrunddienste weg; diese stellen Sie pro Mandant bereit. Für die Verwaltung der Signaturschlüssel wird standardmäßig das Singleton `ProtocolKeyManager` aus `Authagonal.Protocol` verwendet, und ein Host, der vor `AddAuthagonalCore()` sein eigenes `IKeyManager` registriert, behält es:

```csharp
builder.Services.AddScoped<ITenantContext, MyTenantContext>();
builder.Services.AddScoped<IKeyManager, MyPerTenantKeyManager>();
builder.Services.AddAuthagonalCore(builder.Configuration);
```

`IKeyManager` und die Store-Schnittstellen (`IClientStore`, `IScimTokenStore` usw.) werden zur Laufzeit der Anfrage aus `HttpContext.RequestServices` aufgelöst, sodass Scoped-Registrierungen für die Isolation pro Mandant korrekt funktionieren.

### `Authagonal.Protocol` allein einbetten {#embedding-authagonalprotocol-alone}

Ein Host, der nur die OIDC-Protokolloberfläche möchte (eigene Authentifizierung, eigene Pipeline, direkt einsetzbare `/connect/*`-Endpunkte), ruft `AddAuthagonalProtocol()` + `MapAuthagonalProtocolEndpoints()` auf, ohne irgendetwas aus `Authagonal.Server`.

`/connect/authorize`, `/connect/token`, `/connect/userinfo` und `/connect/par` verweigern auch in dieser Form unverschlüsseltes http, gemäß RFC 6749 §3.1/§3.2. Weil das Paket in eine Pipeline eingehängt wird, die ihm nicht gehört, hängt die Anforderung als Filter an den Endpunkten statt als Middleware. Sie gilt also unabhängig davon, wie Sie Ihre Pipeline zusammensetzen und ob Sie die gesamte Oberfläche oder einzelne Endpunkte einhängen. Zwei Folgen, die Sie vor dem Upgrade kennen sollten:

- **Rufen Sie hinter einem TLS-terminierenden Proxy `UseForwardedHeaders` mit deklariertem Proxy auf.** Der Filter liest das Schema nach dem Routing, sodass ein weitergeleitetes `X-Forwarded-Proto: https` ihn erfüllt. Ohne diese Middleware sieht Ihr Host unverschlüsselten Verkehr, was auch bedeutet, dass Ihre Cookies nicht als `Secure` markiert werden und Ihre erzeugten absoluten URLs falsch sind; es lohnt sich also, das zu beheben, statt es zu umgehen. Befüllen Sie `KnownProxies` / `KnownNetworks`, wenn Sie sie registrieren: ASP.NET Core liest eine leere Vertrauensmenge als „jeder Aufrufer ist ein vertrauenswürdiger Proxy“, womit jeder, der Ihren Host erreicht, das Schema bestimmen kann. Erwähnt der Body der Ablehnung ein nicht angewendetes `X-Forwarded-Proto`, ist dies die Middleware, nach der gefragt wird.
- **Ein Host, der die Protokolloberfläche tatsächlich über http bereitstellt, setzt die Opt-in-Option**, so wie es auch der Server tut:

```csharp
builder.Services.AddAuthagonalProtocol(o =>
{
    o.AuthenticationScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    o.AllowInsecureHttp = builder.Environment.IsDevelopment();   // never in production
});
```

Discovery und JWKS sind bewusst nicht abgesichert: Sie sind öffentliche Metadaten, und ein Client, der sie nicht lesen kann, kann gar nicht erst erfahren, dass er https braucht.

Wenn Sie `AddAuthagonal()` (den vollständigen Server) verwenden, setzen Sie das nicht separat: `Auth:AllowInsecureHttp` wird automatisch in die Protokolloptionen übernommen, sodass ein einziger Schalter die gesamte Oberfläche steuert.

## Services überschreiben {#overriding-services}

Registrieren Sie Ihre eigenen Implementierungen **vor** dem Aufruf von `AddAuthagonal()`. Authagonal verwendet intern `TryAdd`, daher haben Ihre Registrierungen Vorrang:

```csharp
// Custom implementations, registered first so they won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

`IAuthHook` ist ein Sonderfall: Es ist eine Pipeline mit Mehrfachregistrierung. Registrieren Sie beliebig viele Hooks (mit beliebiger Lebensdauer, auch `AddScoped`), und alle werden in der Reihenfolge der Registrierung ausgeführt. Der wirkungslose `NullAuthHook` wird nur hinzugefügt, wenn bis zur Ausführung von `AddAuthagonal()` / `AddAuthagonalCore()` kein Hook registriert wurde; registrieren Sie Ihre Hooks also immer zuerst.

### Erweiterungspunkte {#extensibility-points}

| Schnittstelle | Standard | Zweck |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (wirkungslos, nur hinzugefügt, wenn kein Hook registriert ist) | Lebenszyklus-Hooks für Auth-Ereignisse: Audit-Logging, eigene Validierung, Webhooks. Mehrere Hooks können registriert werden; alle laufen der Reihe nach |
| `IEmailService` | `NullEmailService` (wirkungslos) oder der eingebaute Resend-Versand, wenn `Email:ResendApiKey` konfiguriert ist | E-Mail-Versand für Verifizierung, Passwort-Reset und Hinweise auf bereits existierende Konten |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` (scoped) | Provisionierung von Benutzern in nachgelagerte Apps |
| `ISecretProvider` | `PlaintextSecretProvider` oder der eingebaute `KeyVaultSecretProvider`, wenn `SecretProvider:VaultUri` konfiguriert ist | Umkehrbare Speicherung von Secrets (Key Vault, AWS Secrets Manager, Vault Transit usw.) |
| `ITenantContext` | `DefaultTenantContext` (liest aus `IConfiguration`) | Auflösung des Mandanten bei mandantenfähigen Deployments |
| `IKeyManager` | `ProtocolKeyManager` (Singleton, aus `Authagonal.Protocol`) | Verwaltung der Signaturschlüssel; überschreiben Sie es für eine Schlüsselisolation pro Mandant |
| `IProvisioningAppProvider` | `ConfigProvisioningAppProvider` (scoped) | Löst die verfügbaren Provisionierungs-Apps auf; überschreiben Sie es für eine dynamische Auflösung oder eine pro Mandant |
| `IAuditLogger` | `NullAuditLogger` (wirkungslos) | Audit-Trail für Konfigurationsänderungen und sicherheitsrelevante Ereignisse |
| `IClientCredentialsClaimsTransformer` | `NullClientCredentialsClaimsTransformer` (Singleton, aus `Authagonal.Protocol`) | Vom Aufrufer gelieferten Kontext bei einer `client_credentials`-Ausstellung prüfen und Claims in das Token erzwingen oder die Ausstellung ablehnen |
| `ITokenExchangeSubjectTransformer` | `NullTokenExchangeSubjectTransformer` (Singleton, aus `Authagonal.Protocol`) | Zuordnung des Subjekts beim RFC 8693 Token Exchange; siehe [Agentic Auth](agentic-auth) |
| `ITurnstileKeyProvider` | `OptionsTurnstileKeyProvider` (scoped, liest `TurnstileOptions`) | Welcher Turnstile-Sitekey und welches Secret für diese Anfrage gelten |
| `IInteractiveCorsOriginPolicy` | `DenyInteractiveCorsOriginPolicy` (Singleton, lehnt jeden Origin ab) | Origins, die Cross-Origin-Aufrufe mit Anmeldedaten an `/api/auth/*` stellen dürfen |

Drei weitere Nahtstellen liegen auf **Store-Ebene** statt in der DI: `IFieldCipher`, `IIndexTokenizer` und `IChangeWriter` (alle in `Authagonal.Core.Services`). Die Speicher-Provider nehmen sie als optionale Konstruktorparameter entgegen; siehe die jeweiligen Abschnitte unten.

## IAuthHook {#iauthhook}

Die Schnittstelle `IAuthHook` bietet Hooks in den Lebenszyklus der Authentifizierung. Methoden auf dem kritischen Pfad (Authentifizierung, Anlegen von Benutzern, Ausstellung von Tokens) können eine Ausnahme auslösen, um den Vorgang abzubrechen; die neueren Methoden sind nachträgliche Benachrichtigungen. Mehrere `IAuthHook`-Implementierungen können registriert werden, und alle laufen in der Reihenfolge der Registrierung.

```csharp
public interface IAuthHook
{
    // Core lifecycle: implement these
    Task OnUserAuthenticatedAsync(string userId, string email, string method,
        string? clientId = null, CancellationToken ct = default);
    Task OnUserCreatedAsync(string userId, string email, string createdVia,
        CancellationToken ct = default);
    Task OnLoginFailedAsync(string email, string reason,
        CancellationToken ct = default);
    Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType,
        CancellationToken ct = default);
    Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email,
        MfaPolicy clientPolicy, string clientId, CancellationToken ct = default);
    Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default);
    Task OnUserUpdatedAsync(string userId, string email, string updatedVia,
        CancellationToken ct = default);
    Task OnUserDeletedAsync(string userId, string email, string deletedVia,
        CancellationToken ct = default);

    // Additive notifications: default no-op implementations, so existing
    // hooks keep compiling as the interface grows
    Task OnMfaVerifyFailedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnEmailConfirmedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaEnrolledAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaCredentialRemovedAsync(string userId, string email, string mfaMethod,
        bool mfaDisabled, CancellationToken ct = default) => Task.CompletedTask;
    Task OnRecoveryCodesRegeneratedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnPasswordChangedAsync(string userId, string email, string changedVia,
        CancellationToken ct = default) => Task.CompletedTask;

    // Token gate and agentic / consent notifications (also default no-ops)
    Task OnTokenIssuingAsync(TokenIssuanceContext context,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnDelegationMintedAsync(DelegationAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnApprovalRequestedAsync(ApprovalAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnApprovalResolvedAsync(ApprovalAudit audit,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnAgentConsentChangedAsync(string subjectId, string clientId, string change,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnConsentRevokedAsync(string subjectId, string clientId, int grantsRemoved,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnCapabilityTicketRedeemedAsync(string ticketId, string? subjectId, string clientId,
        CancellationToken ct = default) => Task.CompletedTask;
}
```

### Parameter {#parameters}

| Methode | Hinweise und Werte für `method` / `via` |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"passkey"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnUserUpdatedAsync` | `"admin"`, `"self"` (Hosts können eigene Werte übergeben, z. B. eine SCIM-Herkunft) |
| `OnUserDeletedAsync` | `"admin"`; nur eine Benachrichtigung, der Datensatz ist möglicherweise nicht mehr lesbar |
| `OnLoginFailedAsync` | `"user_not_found"`, `"invalid_password"` usw. |
| `OnTokenIssuedAsync` | Grant-Typen: `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | Wird nach der Passwortprüfung aufgerufen; liefert die für den Benutzer wirksame MFA-Richtlinie. Standard: `clientPolicy` unverändert zurückgeben. |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |
| `OnMfaVerifyFailedAsync` | Dieselben Methoden wie bei `OnMfaVerifiedAsync`. Wird nur nach gültigen Anmeldedaten für den ersten Faktor ausgelöst; gehäuftes Auftreten ist daher ein starkes Signal für einen Versuch, MFA zu umgehen (im Unterschied zu `OnLoginFailedAsync`, der Passwortstufe) |
| `OnEmailConfirmedAsync` | Der Benutzer hat seine E-Mail-Adresse über den Verifizierungslink bestätigt; bereits gespeichert |
| `OnMfaEnrolledAsync` | `"totp"`, `"webauthn"`; der Berechtigungsnachweis ist bereits aktiv |
| `OnMfaCredentialRemovedAsync` | `"totp"`, `"webauthn"`, `"recoverycode"`; `mfaDisabled` ist true, wenn nach dem Entfernen kein primärer Faktor übrig ist |
| `OnRecoveryCodesRegeneratedAsync` | Der vorherige Satz an Wiederherstellungscodes ist ungültig |
| `OnPasswordChangedAsync` | z. B. `"reset"`; die Änderung ist gespeichert, und bestehende Sitzungen sind ungültig |
| `OnTokenIssuingAsync` | Prüfung vor der Ausstellung, anders als `OnTokenIssuedAsync`. Wird bei `authorization_code`, `refresh_token` und `device_code` ausgelöst sowie bei den beiden agentischen Ausstellungen (delegierter Token Exchange und `client_credentials` für einen Client mit Agentenprofil). Lösen Sie eine Ausnahme aus, um abzulehnen: Eine einfache Ausnahme wird zu `access_denied` mit ihrer Meldung; lösen Sie `ProtocolTokenException` aus, um Ihren eigenen OAuth-Fehler zu benennen. Beim Refresh läuft sie vor der Rotation, sodass eine Ablehnung das vorgelegte Refresh Token verwendbar lässt. Der Kontext enthält `ClientId`, `SubjectId`, `GrantType`, `Scopes`, `RequestedAuthorityJson` sowie `OrganizationId` / `OrganizationSlug`, wenn die Anfrage eine Organisation ausgewählt hat |
| `OnDelegationMintedAsync` | Ein delegiertes Token (zusammengesetzte Identität) wurde über Token Exchange ausgestellt; nur eine Benachrichtigung |
| `OnApprovalRequestedAsync` | Ein delegierter Austausch wurde bei einer Aktion mit Rückfrage-Richtlinie angehalten, und eine ausstehende Freigabe wurde angelegt |
| `OnApprovalResolvedAsync` | Eine ausstehende Freigabe wurde vom Benutzer genehmigt oder abgelehnt |
| `OnAgentConsentChangedAsync` | `change` ist `"granted"` oder `"revoked"` (dauerhafte Zustimmung für einen Agenten) |
| `OnConsentRevokedAsync` | Ein Benutzer hat einer autorisierten App die Berechtigung entzogen; die Zustimmung und die sitzungsgebundenen Grants des Clients sind bereits entfernt. `grantsRemoved` gibt an, wie viele entfernt wurden (0 bedeutet keine) |
| `OnCapabilityTicketRedeemedAsync` | Ein Capability-Ticket wurde für sein gebundenes Token eingelöst |

### Beispiel: Audit-Logger {#example-audit-logger}

```csharp
public sealed class AuditAuthHook(ILogger<AuditAuthHook> logger) : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Login: {Email} via {Method}", email, method);
        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email,
        string createdVia, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] User created: {Email} via {Via}", email, createdVia);
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct)
    {
        logger.LogWarning("[AUDIT] Login failed: {Email} ({Reason})", email, reason);
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId,
        string grantType, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Token issued: {ClientId} ({GrantType})",
            clientId, grantType);
        return Task.CompletedTask;
    }

    // ... remaining required methods return Task.CompletedTask
}
```

### Beispiel: Domain-Beschränkung {#example-domain-restriction}

```csharp
public sealed class DomainRestrictionHook : IAuthHook
{
    private static readonly HashSet<string> BlockedDomains = ["competitor.com"];

    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        var domain = email.Split('@').Last();
        if (BlockedDomains.Contains(domain))
            throw new InvalidOperationException($"Domain {domain} is not allowed");

        return Task.CompletedTask;
    }

    // ... other methods return Task.CompletedTask
}
```

## IClientCredentialsClaimsTransformer {#iclientcredentialsclaimstransformer}

Ein `client_credentials`-Token hat kein Subjekt, daher kann die Nahtstelle für den Token Exchange es nicht erreichen. Diese Nahtstelle ist für einen eigenen Service als Aufrufer gedacht, dessen Token den Kontext benennen muss, in dem er handelt (eine Organisation, einen Mandanten), ohne dass ein Benutzer beteiligt ist. Sie läuft, nachdem der Client, seine Scopes und alle RFC-8707-Ressourcen validiert sind, und bevor das Token ausgestellt wird.

```csharp
public interface IClientCredentialsClaimsTransformer
{
    Task<ClientCredentialsClaimsResult> TransformAsync(
        OAuthClient client,
        IReadOnlyList<string> grantedScopes,
        IReadOnlyDictionary<string, string> extraParameters,
        CancellationToken ct = default);
}
```

- `extraParameters` enthält die nicht zum Protokoll gehörenden Formularparameter der Token-Anfrage (einwertig, der erste gewinnt), zum Beispiel eine `organization_id`, die der Aufrufer gesendet hat.
- Geben Sie `ClientCredentialsClaimsResult.Allow(claims)` zurück, um `claims` in das Token zu erzwingen (null oder leer lässt es unverändert), oder `ClientCredentialsClaimsResult.Reject(error, description)`, um die Ausstellung mit diesem OAuth-Fehler abzulehnen.
- Reservierte Namen von Protokoll-Claims bleiben bei der Ausstellung weiterhin gesperrt.
- Prüfen Sie die vom Aufrufer gelieferte Bindung gegen Ihre eigene maßgebliche Quelle; übernehmen Sie sie nicht ungeprüft in das Token.
- Der Standard `NullClientCredentialsClaimsTransformer` wird mit `TryAddSingleton` registriert; registrieren Sie Ihren also zuerst, um ihn zu ersetzen.

## ITurnstileKeyProvider {#iturnstilekeyprovider}

Beide Turnstile-Schlüssel stammen aus einem Objekt, damit das Widget, das der Browser rendert, und das Secret, gegen das der Server prüft, nie voneinander abweichen können. Der Standard `OptionsTurnstileKeyProvider` liest `SiteKey` und `SecretKey` aus `TurnstileOptions`, was zu einem Host passt, der eine Domain bedient. Ein Host, der von Kunden bereitgestellte Domains bedient, bei denen Cloudflare die Hostnamen pro Widget begrenzt, registriert seine eigene Scoped-Implementierung, die das Schlüsselpaar des Widgets zurückgibt, das dem anfragenden Host zugeteilt ist.

```csharp
public interface ITurnstileKeyProvider
{
    string? SiteKey { get; }     // null when disabled
    string? SecretKey { get; }   // null or empty disables enforcement
}
```

Mit `TryAddScoped` registriert, daher gewinnt eine Registrierung, die vor `AddAuthagonal` erfolgt.

## IInteractiveCorsOriginPolicy {#iinteractivecorsoriginpolicy}

Die interaktive Auth-API (`/api/auth/*`) lehnt Cross-Origin-Aufrufe mit Anmeldedaten standardmäßig ab, weil sie von der Login-App gesteuert wird, die vom selben Origin ausgeliefert wird. Ein Host, der einem Mandanten erlaubt, einen eigenen Anmeldebildschirm auf einem anderen Origin zu bauen, implementiert diese Schnittstelle, um sich für bestimmte Origins zu verbürgen.

```csharp
public interface IInteractiveCorsOriginPolicy
{
    ValueTask<bool> IsAllowedAsync(HttpContext context, string origin, string path);
}
```

- Wird pro Anfrage und pro Origin abgefragt; die Auflösung des Mandanten ist beim Aufruf bereits erfolgt.
- Gibt sie true zurück, darf dieser Origin authentifizierte Antworten der Konto-, Sitzungs-, Profil- und MFA-Einrichtungsendpunkte für die jeweils angemeldete Person lesen. Antworten Sie nur für Origins, die der Host kontrolliert oder verifiziert hat, niemals für einen, der aus der Anfrage stammt.
- Der Standard (`DenyInteractiveCorsOriginPolicy`, `TryAddSingleton`) gibt für jeden Origin false zurück.

## ISecretProvider {#isecretprovider}

`ISecretProvider` (in `Authagonal.Core.Services`) ist die Nahtstelle für umkehrbare Verschlüsselung gespeicherter Secrets wie SSO-Client-Secrets, SMTP-Passwörter und TOTP-Seeds. `ProtectAsync` wandelt einen Klartext in eine Referenz um, die der Store speichert; `ResolveAsync` wandelt die Referenz wieder in den Klartext zurück. Der Standard `PlaintextSecretProvider` speichert Werte unverändert (die Referenz IST der Wert).

```csharp
public interface ISecretProvider
{
    Task<string> ResolveAsync(string secretReference, CancellationToken ct = default);
    Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default);
}
```

Wird `SecretProvider:VaultUri` gesetzt, wird automatisch der eingebaute `KeyVaultSecretProvider` verdrahtet (Azure Key Vault über `DefaultAzureCredential`). Für alles andere registrieren Sie vor `AddAuthagonal()` Ihre eigene Implementierung.

## Verschlüsselung von PII-Feldern: IFieldCipher {#pii-field-encryption-ifieldcipher}

`IFieldCipher` verschlüsselt einzelne PII-Feldwerte von Benutzern (Telefon, Firma, eigene Attribute, E-Mail-Adresse und Namen in der Profilzeile) im Ruhezustand. Es ist eine Nahtstelle auf Store-Ebene: Die Speicher-Provider nehmen sie als optionalen Konstruktorparameter entgegen (z. B. `TableUserStore`), und fehlt sie, gilt der durchreichende `NullFieldCipher`. Verschlüsselung ist also strikt Opt-in, und nicht konfigurierte Hosts speichern weiterhin Klartext.

```csharp
public interface IFieldCipher
{
    Task<string> ProtectAsync(string plaintext, CancellationToken ct = default);
    Task<string> ResolveAsync(string stored, CancellationToken ct = default);

    // Batch variants have default loop implementations; override for backends
    // with a one-round-trip batch primitive (e.g. Vault Transit)
    Task<IReadOnlyList<string>> ProtectManyAsync(IReadOnlyList<string> plaintexts,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResolveManyAsync(IReadOnlyList<string> stored,
        CancellationToken ct = default);
}
```

Zwei Punkte des Vertrags sind wichtig. `ProtectAsync` muss ein selbstbeschreibendes Chiffretext-Token zurückgeben (z. B. das `vault:v{n}:...` von Vault Transit), und `ResolveAsync` muss einen Wert, den es nicht als eigenen Chiffretext erkennt, unverändert durchreichen. Diese Durchreicheregel ermöglicht es, die Verschlüsselung schrittweise über bestehende Zeilen auszurollen: Das Lesen einer nicht migrierten Zeile liefert den alten Klartext, und der nächste Schreibvorgang schützt ihn neu.

## Blind-Index-Suche: IIndexTokenizer {#blind-index-search-iindextokenizer}

`IIndexTokenizer` hält verschlüsselte Felder durchsuchbar. Er wandelt einen normalisierten Klartextwert in ein deterministisches, als Tabellenschlüssel zulässiges Blind-Index-Token um, typischerweise ein schlüsselbasiertes HMAC, dessen Schlüssel außerhalb der Datenbank liegt. Der Determinismus bedeutet, dass eine Gleichheitssuche weiterhin funktioniert („email = x“ wird zu „token = HMAC(x)“), während ein Datenbank-Dump ein Token weder neu berechnen noch umkehren kann. Die Präfixsuche wird darübergelegt, indem jedes Präfix eines Werts einzeln tokenisiert wird, denn ein schlüsselbasiertes HMAC zerstört die Ordnung und damit Bereichsscans.

> **Was ein Dump dennoch preisgibt.** „Weder neu berechnen noch umkehren“ gilt für ein einzelnes Token, nicht für
> den Index als Ganzes. Drei Rückstände bleiben, und Sie sollten sie kennen, bevor Sie sich darauf verlassen:
>
>   *(Behoben.)* ~~**Struktur.** Der Präfixindex schreibt eine Zeile pro Präfix, sodass die Zeilenanzahl eines Datensatzes
>   der Länge des indizierten Felds entspricht.~~ Jeder indizierte Wert schreibt jetzt eine feste Anzahl von Zeilen,
>   aufgefüllt mit Ködern, die keine Abfrage erzeugen kann und die ein Dump nicht von echten Präfixen unterscheiden kann.
> - **Gleichheit und Häufigkeit.** Tokens sind konstruktionsbedingt deterministisch, und genau das macht die Suche
>   möglich; ein Dump zeigt daher, welche Datensätze einen Wert teilen und wie häufig jeder Wert ist. Der Domain-Index
>   teilt Ihre Population nach Arbeitgeber ein, was Personen oft identifiziert, ohne eine Adresse wiederherzustellen.
> - **Gewählter Klartext.** Ein Angreifer, der den Store lesen *und* veranlassen kann, dass Werte indiziert werden
>   (ein Konto registrieren, über SCIM provisioniert werden), kann einen Kandidaten einreichen und nach dessen Token suchen.
>   Das stellt jeden erratbaren Wert wieder her (verbreitete Domains, verbreitete Vornamen), ganz gleich, wo der Schlüssel
>   liegt, denn das Orakel ist der Schreibpfad und nicht die Chiffre.
>
> Die Tokenisierung schützt vor dem Fall, für den sie gebaut wurde: Jemand hat einen Dump und sonst nichts
> und versucht, Adressen zu lesen. Die beiden verbleibenden Rückstände sind genau das, was ein Registrierungsorakel
> ohnehin preisgibt. Sind sie inakzeptabel, lassen Sie die Tabellen für den Präfix- und den Domain-Index unkonfiguriert
> (eine Suche mit exakter Übereinstimmung hat keinen der beiden), statt anzunehmen, dass das HMAC sie abdeckt.

```csharp
public interface IIndexTokenizer
{
    Task<string> TokenizeAsync(string value, CancellationToken ct = default);
    Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values,
        CancellationToken ct = default);
}
```

Wie `IFieldCipher` ist er ein optionaler Konstruktorparameter des Stores mit einem durchreichenden Standard (`NullIndexTokenizer`), sodass Indexzeilen auf Klartext verschlüsselt bleiben, bis Sie sich dafür entscheiden. Zurückgegebene Tokens müssen als PartitionKey/RowKey-Werte in Azure Table zulässig sein (keines der Zeichen `/ \ # ?` und keine Steuerzeichen).

## Erfassung von Änderungen: IChangeWriter {#change-log-capture-ichangewriter}

`IChangeWriter` (in 0.6.0 von `ITombstoneWriter` umbenannt) zeichnet den Schlüssel jeder geänderten Zeile in einer eigenen Änderungsprotokoll-Tabelle auf, sodass inkrementelle Backups finden können, was sich geändert hat, ohne die nicht indizierte Spalte `Timestamp` der Live-Tabellen zu scannen. Löschungen werden für jede Tabelle erfasst (ein Scan der Live-Zeilen kann eine nicht mehr vorhandene Zeile nicht sehen); Upserts werden für die Tabellen erfasst, die das Backup aus dem Protokoll liest, statt sie zu scannen. Eingebaute Implementierungen: `TableChangeWriter` (Azure Table Storage), `DynamoChangeWriter` (DynamoDB) und `SqlChangeWriter` (PostgreSQL / SQLite).

```csharp
public interface IChangeWriter
{
    // Deletes
    Task WriteAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);

    // Upserts
    Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteUpsertBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);
}
```

Reihenfolgevertrag für Implementierer und Aufrufer: Schreiben Sie den Lösch-Tombstone, BEVOR Sie die Datenzeile löschen. Ein Absturz in der umgekehrten Reihenfolge lässt die Löschung in jedem künftigen Backup fehlen, denn Löschungen sind die einzige Art von Änderung, die ein erneuter Scan nicht selbst heilen kann. Der umgekehrte Absturz ist unbedenklich: Ein späterer Schreibvorgang auf den Schlüssel setzt einen neueren Zeitstempel, und Merge/Restore behalten Zeilen, die nach dem Tombstone geschrieben wurden.

## Eigene Endpunkte {#custom-endpoints}

Fügen Sie neben denen von Authagonal Ihre eigenen Endpunkte hinzu:

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## Integration von HashiCorp Vault Transit {#hashicorp-vault-transit-integration}

> **Die JWT-Signierung wird nicht an Vault delegiert.** Dieser Abschnitt zeigte früher ein DI-Snippet, das sie scheinbar
> aktivierte. Die Registrierung von `VaultTransitCryptoProvider` hat **keine Auswirkung auf die Token-Signierung**:
> `ProtocolKeyManager` ruft `ProtocolSigningKeyOps.BuildSigningCredentials` auf, das einen
> `ECDsaSecurityKey` aus dem Material in `ISigningKeyStore` erzeugt, und nichts ersetzt ihn durch einen
> `VaultTransitSecurityKey`. Ein Host, der dem alten Snippet folgte, sah ES256-Tokens gegen JWKS verifizieren
> und schloss nachvollziehbarerweise, dass Vault sie signierte, während der private Schlüssel beim ersten Start lokal erzeugt
> und im primären Datenspeicher gespeichert wurde, im Klartext, sofern nicht zufällig ein `IFieldCipher` registriert war.
> Lesezugriff auf diesen Speicher bedeutet, sich vollständig als Issuer ausgeben zu können. Wenn Sie die Compliance-Anforderung haben,
> dass Signaturschlüssel ein HSM nie verlassen, wird diese damit nicht erfüllt.
>
> Der Server protokolliert jetzt beim Start einen Fehler, wenn er einen registrierten `VaultTransitCryptoProvider` findet, sodass
> sich dieses Missverständnis nicht unbemerkt halten kann.
>
> Um das tatsächlich umzusetzen, braucht es mehr als eine DI-Registrierung: `ISigningKeyStore` müsste einen Schlüssel darstellen können, der
> kein lokales Material hat (einen Transit-Schlüssel*namen* statt eines privaten Skalars), `BuildSigningCredentials` bräuchte eine
> Nahtstelle, um einen `VaultTransitSecurityKey` zurückzugeben, `BuildJwksAsync` müsste den aus Vault zurückgelesenen öffentlichen Schlüssel
> veröffentlichen, und Rotation sowie Vorabveröffentlichung müssten Transit-Schlüsselversionen anlegen und hochstufen, statt
> lokal zu erzeugen. `VaultTransitClient`, `VaultTransitSecurityKey`, `VaultTransitSignatureProvider` und
> `VaultTransitCryptoProvider` bleiben erhalten, weil sie die funktionierenden Teile sind; was fehlt, ist die Verdrahtung.

Wofür `VaultTransitClient` heute **tatsächlich** taugt, sind die Nahtstellen für Verschlüsselung und HMAC: ein auf Vault gestützter
`IFieldCipher` für PII im Ruhezustand oder ein `IIndexTokenizer` für verschlüsselte Blind-Indizes:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("Vault", client =>
{
    client.BaseAddress = new Uri("https://vault.example.com");
    client.DefaultRequestHeaders.Add("X-Vault-Token", "hvs.xxx");
});

builder.Services.AddSingleton<VaultTransitClient>();

// Your own adapters over the client. These are the seams Authagonal actually consumes.
builder.Services.AddSingleton<IFieldCipher, MyVaultFieldCipher>();
builder.Services.AddSingleton<IIndexTokenizer, MyVaultIndexTokenizer>();

builder.Services.AddAuthagonal(builder.Configuration);
```

Die Registrierung eines `IFieldCipher` ist auch das, was `PlaintextSigningKeyWarning` zum Schweigen bringt, denn die Stores für
Signaturschlüssel leiten ihr Schlüsselmaterial über dieselbe Nahtstelle. Das ist das, was der ursprünglichen Behauptung heute
am nächsten kommt: Der private Schlüssel existiert weiterhin lokal, aber nicht im Klartext.

Der `VaultTransitClient` bietet diese Operationen:

| Methode | Beschreibung |
|---|---|
| `SignAsync(keyName, data)` | Daten mit einem Vault-Transit-Schlüssel signieren |
| `VerifyAsync(keyName, data, signature)` | Eine im JWS-Format serialisierte Signatur über den Transit-Verify-Endpunkt prüfen |
| `EncryptAsync` / `DecryptAsync` (+ `EncryptBatchAsync` / `DecryptBatchAsync`) | Symmetrische Verschlüsselung mit einem `aes256-gcm96`-Schlüssel; liefert `vault:v{n}:...`-Tokens, die unverändert zu speichern sind |
| `HmacAsync` / `HmacBatchAsync` | Schlüsselbasiertes HMAC mit einem `hmac`-Schlüssel (Blind-Index-Tokens) |
| `CreateKeyAsync(keyName, type)` | Einen neuen Transit-Schlüssel anlegen (Standard: `ecdsa-p256`) |
| `EnsureKeyTypeAsync(keyName, type)` | Idempotent sicherstellen, dass ein Schlüssel mit dem gewünschten Typ existiert (legt ihn bei abweichendem Typ neu an; der Typ von Transit-Schlüsseln lässt sich nicht nachträglich ändern) |
| `RotateKeyAsync(keyName)` | Einen Schlüssel auf eine neue Version rotieren |
| `DeleteKeyAsync(keyName)` | Einen Schlüssel löschen (aktiviert zuvor `deletion_allowed`) |
| `ReadKeyAsync(keyName)` | Metadaten, Versionen und öffentliche Schlüssel eines Schlüssels lesen |
| `KeyExistsAsync(keyName)` | Prüfen, ob ein Schlüssel existiert |

Der `VaultTransitCryptoProvider` integriert sich in den `JsonWebTokenHandler` von .NET, sodass die JWT-Signierung transparent Vault verwendet. `VaultTransitSecurityKey` und `VaultTransitSignatureProvider` übernehmen die Integration auf unterer Ebene.

## E-Mail {#email}

Der eingebaute Resend-Versand wird automatisch aktiv, wenn `Email:ResendApiKey` konfiguriert ist (setzen Sie auch `Email:SenderEmail`). Ohne ein `IEmailService` werden Mails über `NullEmailService` verworfen, und weil die Anmeldesperre für unbestätigte E-Mail-Adressen standardmäßig aktiv ist, könnten sich selbst registrierte Benutzer nie anmelden; `UseAuthagonal()` protokolliert in diesem Zustand beim Start eine deutliche Warnung.

Um einen anderen Anbieter zu verwenden, registrieren Sie vor `AddAuthagonal()` Ihr eigenes `IEmailService`:

```csharp
public sealed class SmtpEmailService(SmtpClient smtp) : IEmailService
{
    public async Task SendVerificationEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Verify your email", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }

    public async Task SendPasswordResetEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Reset your password", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }
}
```

`IEmailService` deklariert außerdem `SendAccountExistsEmailAsync` (wird gesendet, wenn jemand versucht, sich mit einer bereits registrierten E-Mail-Adresse zu registrieren; so bleibt die Antwort auf die Registrierung neutral gegenüber der Aufzählung von Konten). Es hat eine wirkungslose Standardimplementierung, sodass bestehende Implementierungen weiterhin kompilieren.

## Siehe auch {#see-also}

- [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server): vollständiges, lauffähiges Beispiel
- [demos/sample-app/](https://github.com/authagonal/authagonal/tree/master/demos/sample-app): Beispiel für eine Client-App
