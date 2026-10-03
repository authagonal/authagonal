---
layout: default
title: Startseite
locale: de
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

Authentifizierungsserver für OAuth 2.0 / OpenID Connect / SAML 2.0 unter .NET mit austauschbarem Speicher: Ihr eigenes PostgreSQL oder SQLite, Azure Table Storage oder AWS (DynamoDB / S3 / Secrets Manager).

Ein einziges, in sich geschlossenes Deployment. Server und Login-UI werden als ein Docker-Image ausgeliefert, und die SPA wird vom selben Origin wie die API bereitgestellt. Cookie-Authentifizierung, Weiterleitungen und CSP funktionieren daher ohne die Komplexität von Cross-Origin-Szenarien.

> **Lieber ein verwalteter Dienst?** [Authagonal Cloud](https://authagonal.io) betreibt all das für Sie: mandantenfähig, jede Funktion in jedem Tarif, keine SSO-Gebühren pro Verbindung. → [authagonal.io](https://authagonal.io)

## Wichtige Funktionen {#key-features}

- **OIDC-Provider**: Grants authorization_code + PKCE, client_credentials, refresh_token und device_code mit einmaliger Rotation
- **SAML 2.0 SP**: eigene Implementierung mit voller Azure-AD-Unterstützung (signierte Response, signierte Assertion oder beides), einem SP-Schlüsselpaar pro Verbindung für signierte AuthnRequests und die Entschlüsselung von `EncryptedAssertion` sowie Single Logout (SP- und IdP-initiiert)
- **Dynamische OIDC-Föderation**: Anbindung an Google, Apple, Azure AD oder jeden OIDC-konformen IdP
- **Multi-Faktor-Authentifizierung**: TOTP, WebAuthn/Passkeys, Wiederherstellungscodes; Richtlinie pro Client (`Disabled` / `Enabled` / `Required`) mit benutzerbezogener Übersteuerung über `IAuthHook`, auch bei föderierten Anmeldungen durchgesetzt
- **SCIM-2.0-Provisionierung**: eingehende Benutzer- und Gruppenprovisionierung aus Entra ID, Okta und OneLogin; cursorbasiert paginierte Auflistung und `eq`-Filter auf Basis eines Blind Index
- **OAuth-Zustimmungsseite**: Zustimmung pro Client mit scope-abhängiger erneuter Abfrage und Verwaltung der Grants
- **Device Authorization Grant**: Ablauf nach RFC 8628 für Geräte mit eingeschränkter Eingabe (Smart-TVs, CLIs, IoT)
- **Token-Introspektion**: RFC 7662, damit Ressourcenserver die Gültigkeit eines Tokens prüfen können
- **Token-Signatur**: ausschließlich ES256. Access Tokens tragen das `typ: at+jwt` aus RFC 9068, damit ein Ressourcenserver
  sie von id_tokens und Logout-Tokens unterscheiden kann, **Konformität mit RFC 9068 wird jedoch nicht beansprucht**: §2.1
  verlangt RS256 unter den unterstützten Algorithmen, und dieser Server stellt es weder aus noch akzeptiert er es. Ein
  einziger Algorithmus ist eine bewusste Entscheidung: Jeder zusätzlich akzeptierte Algorithmus ist ein weiterer Weg, einen
  Prüfer zum falschen Algorithmus zu verleiten.
- **Back-Channel Logout**: Benachrichtigungen nach OIDC Back-Channel Logout 1.0 an Relying Parties
- **Serverseitige Sitzungen** *(optional)*: `AddAuthagonalServerSideSessions` hält das SSO-Ticket im Speicher, sodass das Auth-Cookie nur eine undurchsichtige ID trägt, und aktiviert die Auflistung in Selbstbedienung über `GET /api/auth/sessions` sowie den Widerruf pro Gerät ([Auth-API](auth-api#sessions-self-service))
- **Backend-for-Frontend**: `Authagonal.Bff` (.NET) und `@authagonal/bff` (Node), ein BFF als vertraulicher Client, sodass eine SPA nie ein Token hält ([BFF](bff))
- **DSGVO-Selbstbedienung** *(Authagonal Cloud)*: Datenexport und geplante Kontolöschung über die gehostete
  Kontoseite. Die Login-App liefert die Oberfläche dafür mit, die aufgerufenen Endpunkte
  (`GET /api/v1/account/export`, `POST /api/v1/account/erasure`) werden jedoch vom Cloud-Auth-Host bereitgestellt und sind
  **nicht** Teil der Schnittstelle dieser Bibliothek. Ein selbst gehostetes Deployment muss sie implementieren oder die beiden
  Schaltflächen von seiner Kontoseite weglassen: `MapFallbackToFile` beantwortet eine nicht implementierte Route mit 200 und dem
  HTML der SPA, sodass ein nicht implementierter Export als solcher erkannt werden muss, statt heruntergeladen zu werden.
- **TCC-Provisionierung**: Try-Confirm-Cancel-Provisionierung in nachgelagerte Anwendungen zum Zeitpunkt der Autorisierung
- **Anpassbare Login-UI**: zur Laufzeit über eine JSON-Datei konfigurierbar (Logo, Farben, CSS Custom Properties), ohne Neubau; in 11 Sprachen lokalisiert
- **Auth-Hooks**: Erweiterbarkeit über `IAuthHook` für Audit-Logging, eigene Validierung und Webhooks
- **Erweiterungspunkte für PII-Verschlüsselung**: `IFieldCipher` / `IIndexTokenizer` für die Verschlüsselung auf Feldebene im Ruhezustand mit Suche über einen schlüsselbasierten Blind Index (HMAC); Wiederherstellungscodes werden über `ISecretProvider` verschlüsselt
- **Client für HashiCorp Vault Transit**: Signieren/Prüfen, Ver-/Entschlüsseln und schlüsselbasiertes HMAC über die Transit-Engine von Vault, als Grundlage für ein `IFieldCipher` oder `IIndexTokenizer`. Entferntes Signieren von JWTs ist nicht angebunden: Der Token-Signaturschlüssel ist immer derjenige in `ISigningKeyStore`.
- **Zusammensetzbare Bibliothek**: `AddAuthagonal()` / `UseAuthagonal()`, um den Server in Ihrem eigenen Projekt mit eigenen Service-Überschreibungen zu hosten
- **Bereit für Native AOT**: IL-Trimming und quellgenerierte JSON-Serialisierung für einen schnellen Start
- **Austauschbarer Speicher**: selbst gehostetes PostgreSQL oder SQLite (kein Cloud-Konto nötig) oder Azure Table Storage / AWS (DynamoDB / S3 / Secrets Manager) als kostengünstige, serverless-freundliche Backends
- **Sicherung und Wiederherstellung**: inkrementelle Sicherungen (gesteuert über ein Änderungsprotokoll, abgesichert durch einen vollständigen Scan), Integritätsprüfung, Erfassung von Löschungen über Tombstones
- **Admin-APIs**: Benutzer-CRUD, Verwaltung von SAML-/OIDC-Providern, SSO-Domain-Routing, Token-Impersonation

## Häufige Integrationen {#common-integrations}

Aufgabenorientierte Anleitungen für die Abläufe, die Teams am häufigsten bauen:

- **[Einen Benutzer hochstufen](user-upgrade)**: ein Gast-, SSO- oder Einladungskonto über die passwortlose Übernahme des Kontos in ein Konto mit Anmeldedaten verwandeln und bei der Bestätigung Ihre Hochstufung vom Gast zum regulären Mitglied ausführen.
- **[SSO in Selbstbedienung](self-service-sso)**: JIT-Provisionierung für Enterprise-Verbindungen: Onboarding nur auf Einladung oder in Selbstbedienung, wie externe IdPs nicht zur Stolperfalle werden, und Zwischenseiten vor der Föderation.
- **[Föderierte Sitzungen](federated-sessions)**: die lokale Sitzung widerrufen, wenn der Upstream-IdP das tut (`RevalidateOnRefresh`).
- **[Backend-for-Frontend (BFF)](bff)**: Tokens aus dem Browser heraushalten: ein vertraulicher OIDC-Client in Ihrem Backend mit einem httpOnly-Sitzungscookie und einem API-Proxy, der Tokens einfügt, in .NET oder Node.
- **[WebSocket-Authentifizierung](websocket-auth)**: Browser-WebSockets über das BFF authentifizieren, ohne ein Token offenzulegen.
- **[Agentische Authentifizierung](agentic-auth)**: die Befugnisse eines Benutzers an KI-Agenten delegieren: registrierte Agenten, feingranulare Befugnisse nach RFC 9396, zusammengesetzte Delegierungstokens (RFC 8693 `act`), dauerhafte Zustimmung, Just-in-time-Freigaben, Capability-Tickets.
- **[Organisationen](organizations)**: viele Kunden aus einem Mandanten bedienen: `Organization`- und Mitgliedschaftsdatensätze, der Autorisierungsparameter `organization`, `org_id` / `org_slug` / `org_name` in den Tokens, organisationsbezogene Rollen und die Abweisung von Nichtmitgliedern.

## Architektur {#architecture}

```
Client App                    Authagonal                         IdP (Azure AD, etc.)
    │                             │                                    │
    ├─ GET /connect/authorize ──► │                                    │
    │                             ├─ 302 → /login (SPA)                │
    │                             │   ├─ SSO check                     │
    │                             │   └─ SAML/OIDC redirect ─────────► │
    │                             │                                    │
    │                             │ ◄── SAML Response / OIDC callback ─┤
    │                             │   └─ Create user + cookie          │
    │                             │                                    │
    │                             ├─ TCC provisioning (try/confirm)    │
    │                             ├─ Issue authorization code          │
    │ ◄─ 302 ?code=...&state=... ┤                                    │
    │                             │                                    │
    ├─ POST /connect/token ─────► │                                    │
    │ ◄─ { access_token, ... } ──┤                                    │
```

Beginnen Sie mit der Anleitung zur [Installation](installation) oder springen Sie direkt zum [Schnellstart](quickstart). Wie Sie Authagonal in Ihrem eigenen Projekt hosten, beschreibt [Erweiterbarkeit](extensibility). Zur Datenverwaltung siehe [Sicherung und Wiederherstellung](backup-restore). Die vollständige Änderungshistorie finden Sie im [Changelog](https://github.com/authagonal/authagonal/blob/master/CHANGELOG.md).
