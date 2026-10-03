---
layout: default
title: Agentic Auth
locale: de
---

# Agentic Auth

Authagonal liefert die Bausteine, um die Befugnisse eines Benutzers sicher an KI-Agenten (oder beliebige
nicht menschliche Workloads) zu delegieren: registrierte Agenten, feingranulare Befugnis-Grants, zusammengesetzte
Delegations-Tokens, dauerhafte Benutzerzustimmung, Just-in-Time-Genehmigungen, Capability-Tickets und eine
delegationsbewusste Audit-Schnittstelle. Die Bibliothek verantwortet die Primitive und die Invariante; die
Host-Anwendung setzt sie zu einem Produkt zusammen (Connector-Implementierungen, die UX für Genehmigungen, die
Zustellung von Benachrichtigungen und die Geschäftsregeln bleiben beim Host).

## Die Invariante {#the-invariant}

Jedes delegierte Token gehorcht:

```
effective authority = admin ceiling ∩ user consent ∩ task request ∩ subject-token authority
```

Nichts weiter unten in der Kette kann sie erweitern: Jeder weitere Delegationsschritt bildet erneut die
Schnittmenge, sodass Befugnisse immer nur enger werden. Die Schnittmengenbildung ist an genau einer Stelle
implementiert (`AuthoritySet.Intersect`) und wird überall verwendet.

## Entitäten {#entities}

| Entität | Typ | Hinweise |
|---|---|---|
| Agent | `AgentProfile` an einem vertraulichen `OAuthClient` | Erst das Registrieren eines Profils macht einen Client zum Agenten; wird es gelöscht, ist der Client wieder ein gewöhnlicher OAuth-Client. |
| Befugnis | `AuthoritySet` / `AuthorityGrant` | Struktur von `authorization_details` nach RFC 9396: Connector-`type`, `actions`, `locations`, Einschränkungen, Richtlinien `auto`/`ask`/`deny` pro Aktion. |
| Obergrenze | `AgentProfile.Ceiling` | Die weitestgehende Befugnis, die eine Delegation über diesen Agenten tragen kann. Vom Administrator verwaltet (`/api/v1/agents`). |
| Zustimmung (Basis) | `PersistedGrant` vom Typ `agent_consent` | Pro (Benutzer, Agent), verwaltet unter `/consent/agents`. Wird bereits mit der Obergrenze geschnitten gespeichert und bei jeder Ausstellung erneut geschnitten. |
| Delegation | Token Exchange nach RFC 8693 | Zusammengesetzte Identität: `sub` = Benutzer, `act` = Agent (pro Schritt verschachtelt), `authorization_details` = die effektive Schnittmenge. Kurzlebig, nie erneuerbar. |
| Genehmigung | `PersistedGrant` vom Typ `approval` | JIT-Sperre für Aktionen mit der Richtlinie `ask`; Polling-Semantik wie im Device Flow; einmalig verwendbar, an die Form des Requests gebunden. |
| Capability-Ticket | `ICapabilityTicketService` | Opakes, einmalig verwendbares Handle, das an ein Token gebunden ist: das verallgemeinerte BFF-ws-ticket, atomar über den Grant-Speicher. |
| Audit | `IAuthHook` | `OnDelegationMintedAsync`, `OnApprovalRequested/ResolvedAsync`, `OnAgentConsentChangedAsync`, `OnCapabilityTicketRedeemedAsync` sowie die Sperre `OnTokenIssuingAsync` vor der Ausstellung. |

## Einen Agenten registrieren {#registering-an-agent}

1. Legen Sie einen vertraulichen Client an, der `urn:ietf:params:oauth:grant-type:token-exchange`
   (delegierter Modus) und/oder `client_credentials` (Service-Modus) erlaubt.
2. `PUT /api/v1/agents/{clientId}`:

```json
{
  "mode": "delegated",
  "ceiling": [
    {
      "type": "email",
      "actions": ["send", "read"],
      "action_policies": { "send": "ask" },
      "recipient_domains": ["@acme.com", "*.partners.acme.com"]
    },
    { "type": "calendar", "actions": ["read"] }
  ],
  "maxDelegationDepth": 0,
  "maxTokenLifetimeSeconds": 300,
  "highRiskDefault": "ask"
}
```

`mode` ist `delegated`, `service` oder `both` (fehlt `mode` bei einer Aktualisierung, bleibt der bisherige Wert erhalten). `maxDelegationDepth` muss zwischen 0 und 8 liegen (Standard 0), `maxTokenLifetimeSeconds` zwischen 30 und 86400 (Standard 300), und `highRiskDefault` muss `auto`, `ask` oder `deny` sein; alles andere ergibt eine 400.

Die Typen der Einschränkungsfelder ergeben sich aus ihrer JSON-Form: String/String-Array → Allowlist (Kombination
per Schnittmenge; Einträge unterstützen exakte Treffer, Wildcards der Form `*.host` und Suffixe der Form
`@suffix`), Zahl → Obergrenze (Kombination per Minimum), Bool → Schalter (Kombination per UND). Nicht
interpretierbare Felder bleiben unverändert erhalten und führen bei der Auswertung zur Ablehnung (fail closed).
`GET /api/v1/agents/{clientId}/effective-grant?subjectId=…` zeigt für die Admin-Oberfläche eine Vorschau von
Obergrenze ∩ Zustimmung.

## Benutzerzustimmung (die Basis) {#user-consent-the-floor}

- `GET /consent/agents/{clientId}/info`: die Obergrenze, dargestellt anhand des Connector-Katalogs
  (registrieren Sie einen `IConnectorCatalog` für Anzeigenamen, Aktionsbeschreibungen und Hochrisiko-Kennzeichen;
  seine Typen werden in der Discovery als `authorization_details_types_supported` angekündigt).
- `POST /consent/agents` `{ "clientId": …, "authority": […] }`: erteilt die Basis (lassen Sie
  `authority` weg, um der vollen Obergrenze zuzustimmen). Ein Benutzer kann eine Richtlinie verschärfen (`auto` → `ask`),
  sie aber nie lockern oder erweitern: Der Speicher bildet vorab die Schnittmenge mit der aktuellen Obergrenze.
- `GET /consent/agents` / `DELETE /consent/agents/{clientId}`: auflisten und widerrufen. Ein Widerruf
  verhindert die nächste Ausstellung; bereits ausgestellte Delegationen haben kein Refresh und laufen innerhalb
  ihrer (kurzen) Lebensdauer ab. Ohne Zustimmung schlägt der Austausch mit `invalid_grant` /
  `consent_required` fehl: Die Obergrenze allein gewährt nichts.

## Eine Delegation ausstellen {#minting-a-delegation}

Der Agent authentifiziert sich als er selbst und tauscht das Token des Benutzers aus:

```
POST /connect/token
grant_type=urn:ietf:params:oauth:grant-type:token-exchange
client_id=agent&client_secret=…            (or private_key_jwt, below)
subject_token={user access token}
subject_token_type=urn:ietf:params:oauth:token-type:access_token
authorization_details=[{"type":"email","actions":["read"]}]   (the task slice; omit = everything grantable)
```

Die Ausstellung erzwingt in dieser Reihenfolge: den Agentenmodus, die dauerhafte Zustimmung, die Tiefe der
Unterdelegation (jeder Akteur, der bereits in der `act`-Kette steht, braucht in `maxDelegationDepth` Spielraum für
einen weiteren Schritt), die Schnittmenge, die Ablehnung explizit angeforderter Befugnisse (`invalid_target`: Ein
Agent darf nicht glauben, eine Befugnis zu besitzen, die ihm fehlt), die Ask-Sperre und die Begrenzungen der
Lebensdauer (Client-Lebensdauer ∩ Restlaufzeit des Subject Tokens ∩ `maxTokenLifetimeSeconds`). Das Token trägt
`act` (RFC 8693; pro Schritt verschachtelt) und `authorization_details` (RFC 9396); die Antwort gibt die gewährten
Details zurück, und die Introspection liefert beides aus. Wird ein delegiertes Token weiter ausgetauscht, wird es
automatisch weiter eingeschränkt, weil der eigene Claim des Subject Tokens in die Schnittmenge eingeht.

Clients **ohne** Agentenprofil verhalten sich beim Austausch exakt wie bisher, mit einer Ausnahme: Ein
Request-Parameter `authorization_details` schränkt das ausgetauschte Token jetzt ein (erweitert es aber nie).

## Genehmigungen (Ask-Sperre) {#approvals-ask-gate}

Enthält der effektive Ausschnitt eine Aktion mit `ask`, wird der Austausch angehalten:

```json
{ "error": "authorization_pending", "approval_id": "…", "interval": 5 }
```

Der Host wird über `IAuthHook.OnApprovalRequestedAsync` benachrichtigt (die Zustellung per E-Mail, Push oder Chat
liegt beim Host). Der Benutzer entscheidet über `GET /approvals` und `POST /approvals/{id}`
`{ "decision": "approve" | "deny" }`, während der Agent den identischen Request zusammen mit
`approval_id` wiederholt, durchgehend mit dem Vokabular des Device Flow (`slow_down`, `access_denied`,
`expired_token`). Genehmigungen sind einmalig verwendbar (atomarer Verbrauch), laufen nach
`ApprovalLifetimeSeconds` ab (Standard 300) und sind an die exakte Form des Requests *und den aktuellen
Richtlinienstand* gebunden: Ändert ein Administrator die Obergrenze zwischen Anhalten und Polling, wird die
Genehmigung ungültig, statt veraltete Befugnisse auszustellen. Eine verbrauchte Genehmigung stellt das Token mit
ihren `ask`-Aktionen als `auto` aus (gefragt und beantwortet).

Der Service-Modus (`client_credentials`) hat keinen Benutzer in der Schleife: Hier gilt allein die Obergrenze, und
`ask` wird zu `deny` herabgestuft.

## Durchsetzung auf Ressourcenseite {#resource-side-enforcement}

- `AuthorityEvaluator.Permits(user, type, action, context, location, strict)` in jedem Resource
  Server (Kontextschlüssel werden mit den Namen der Einschränkungen abgeglichen; übergeben Sie, was Sie ableiten
  können, z. B. `recipient_domains` beim Versenden von E-Mails). Ein Token ohne den Claim wird als
  uneingeschränkt ausgewertet (Kompatibilität mit Altbestand); ein unlesbarer Claim wird als vollständige Ablehnung
  ausgewertet.
  - `location` ist der `locations`-Wert nach RFC 9396, an dem Sie handeln. Ein Grant, der Orte nennt, gilt nur an
    diesen; ein gewährter Ort ist eine **Wurzel**, sodass
    `https://api.example.com/orders` zwar `/orders/17` abdeckt, aber nicht `/orders-admin`.
  - `strict: true` lehnt ab, wenn der Aufrufer für eine Einschränkung keinen Kontext geliefert hat, statt sie zu
    überspringen. Verwenden Sie es überall, wo Sie jeden unterstützten Schlüssel aufzählen können:
    `AuthoritySet.UncheckedConstraints(type, context)` nennt diejenigen, die Sie nicht geprüft haben.
- BFF-Engpass: `BffUpstream.RequiredAuthority = ["email:send"]` lässt den Proxy das ausgehende Bearer-Token vor
  der Weiterleitung prüfen; bei Misserfolg 403, keine anonyme Durchleitung. Als Ort legt er den Upstream vor, den
  der Request tatsächlich erreicht (`AuthorityLocation` überschreibt die Wurzel, wenn Befugnisse gegen einen
  öffentlichen Bezeichner statt gegen die interne Adresse ausgestellt werden); `StrictAuthority` lässt den Proxy
  eine Einschränkung ablehnen, die er nicht auswerten kann, statt sie dem Upstream zu überlassen.

## Capability-Tickets {#capability-tickets}

`ICapabilityTicketService` (Standard `GrantStoreCapabilityTicketService`, mit `TryAdd` von `AddAuthagonalCore`
registriert, sodass auch `AddAuthagonal` ihn erhält) stellt opake, einmalig verwendbare Handles aus, die an ein
Token gebunden sind und atomar über das bedingte Löschen des Grant-Speichers eingelöst werden: dauerhaft und über
Pods hinweg sicher gegen Replays, anders als das Lesen-dann-Entfernen eines einfachen Caches. Das ws-ticket des BFF
behält seinen bisherigen Vertrag über den verteilten Cache (`WsTicketKey` / `TryRedeemWsTicketAsync`), weil sein
Einlöser typischerweise ein separater Host ist, der nur Redis mitnutzt; im selben Host betriebene Broker sollten
den Capability-Ticket-Service bevorzugen.

## private_key_jwt {#private_key_jwt}

Agenten sind Workloads; gemeinsame Secrets sind das schwächste Glied der Kette. Setzen Sie
`OAuthClient.JwksJson` (JWKS inline) oder `JwksUri` (wird abgerufen und etwa 10 Minuten zwischengespeichert) und
authentifizieren Sie sich mit einer Client Assertion nach RFC 7523 (`client_assertion_type=…:jwt-bearer`).
Erzwungen werden: die Signatur gegen das registrierte JWKS, `iss` = `sub` = `client_id`, Audience = Issuer oder
Token-Endpunkt, ein begrenztes `exp` (≤ 10 Minuten) und eine einmalig verwendbare `jti` (Replay-Cache über
`IRevokedTokenStore`). Ist eine Assertion vorhanden, fällt die Authentifizierung nie auf das Secret zurück.

## Kompatibilität {#compatibility}

- Kein Agentenprofil → keine Verhaltensänderung in irgendeinem Ablauf. Alle neuen Tabellen und Spalten sind
  nullable mit Standardwert und werden bei beiden Storage-Providern automatisch angelegt (Tabelle `AgentProfiles`;
  `JwksJson`/`JwksUri` an Clients; Zustimmungen, Genehmigungen und Tickets nutzen die bestehende Grant-Tabelle).
- Neue `IAuthHook`-Member sind Default-Interface-Methoden; bestehende Hooks kompilieren unverändert.
- `ITokenExchangeSubjectTransformer` läuft weiterhin bei jedem Austausch und kann Kontext-Claims ablehnen oder
  binden; er kann die Delegation jedoch nie erweitern (seine Ausgabe wird erneut geschnitten) und die `act`-Kette
  nicht anfassen (reservierter Claim).
