---
layout: default
title: OIDC-Föderation
locale: de
---

# OIDC-Föderation

Authagonal kann die Authentifizierung an externe OIDC-Identitätsanbieter (Google, Apple, Azure AD usw.) föderieren. Das ermöglicht Abläufe nach dem Muster "Mit Google anmelden", während Authagonal der zentrale Authentifizierungsserver bleibt.

## Funktionsweise {#how-it-works}

Es gibt zwei Einstiege in die Föderation:

**Domainbasiert (interaktive Anmeldung):**

1. Der Benutzer gibt auf der Login-Seite seine E-Mail-Adresse ein
2. Die SPA ruft `/api/auth/sso-check` auf; ist die E-Mail-Domain mit einem OIDC-Anbieter verknüpft, ist SSO erforderlich
3. Der Benutzer klickt auf "Weiter mit SSO" und wird zum externen IdP weitergeleitet (ist die E-Mail-Adresse der `login_hint` eines Authorize-Requests und wird ihre Domain an eine Verbindung geleitet, gelangt der Benutzer direkt zum IdP, wobei `login_hint` weitergegeben wird)
4. Nach der Authentifizierung leitet der IdP zurück an `/oidc/callback`
5. Authagonal validiert das id_token, verknüpft den Benutzer (oder legt ihn an, wenn die Verbindung JIT-Provisionierung erlaubt) und setzt ein Sitzungscookie

**Durch die RP vorgegeben (`idp_hint`):**

Die nachgelagerte Relying Party kann direkt an einen bestimmten Upstream-IdP leiten, ohne den Schritt über E-Mail und SSO-Domain. Hängen Sie `idp_hint={connectionId}` an `/connect/authorize` an:

```
/connect/authorize?client_id=my-rp&scope=openid+email&...&idp_hint=google
```

Ist der Request nicht authentifiziert, leitet Authagonal an `/oidc/{connectionId}/login` weiter und bewahrt die ursprüngliche `/authorize`-URL als `returnUrl` auf. Nach Abschluss der Föderation landet der Benutzer mit einem Sitzungscookie wieder bei `/authorize`, und der Ablauf geht normal weiter. Setzt die Verbindung `InteractionPath`, wird der Benutzer zuerst auf diese Seite der Login-App geschickt (siehe [Vor der Föderation etwas abfragen](self-service-sso#collect-something-before-federating)). Eine Verbindung mit `ShowOnLogin: false` wird nie als Login-Schaltfläche angeboten und ist nur auf diesem Weg erreichbar.

## Einrichtung {#setup}

### 1. Einen OIDC-Anbieter anlegen {#1-create-an-oidc-provider}

**Option A, Konfiguration (empfohlen für statische Setups):**

Fügen Sie Folgendes zu `appsettings.json` hinzu:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

Anbieter werden beim Start per Seed-Konfiguration angelegt. `ConnectionId`, `MetadataLocation`, `ClientId` und `ClientSecret` sind Pflicht (ohne sie schlägt der Start fehl). `RedirectUrl` wird aus Kompatibilitätsgründen akzeptiert und ignoriert: Die Redirect-URI wird pro Request als `{Issuer}/oidc/callback` abgeleitet, da sie auf dem Origin liegen muss, auf dem sich der Browser befindet, und diese URI ist beim IdP zu registrieren (ein abweichender Wert in der Seed-Konfiguration wird als ignoriert protokolliert). Das `ClientSecret` wird über `ISecretProvider` geschützt (Key Vault, sofern konfiguriert, sonst Klartext). SSO-Domainzuordnungen werden automatisch aus `AllowedDomains` registriert, außer bei einer organisationsgebundenen Verbindung, deren Domains nur innerhalb ihrer Organisation abgeglichen werden.

Die Seed-Konfiguration kann auch jedes Verhaltens-Flag aus der folgenden Tabelle setzen. **Ein Eintrag aus der Seed-Konfiguration ersetzt die gespeicherte Verbindung bei jedem Start**: Ein Flag, das Sie weglassen, fällt auf seinen Standardwert zurück. Geben Sie daher in der Konfiguration jedes Flag an, das erhalten bleiben soll (`ConnectionName`, `IconUrl` und `OrganizationId` sind die einzigen Werte, die ein Weglassen überstehen, und `CreatedAt` bleibt erhalten).

| Feld | Standard | Wirkung |
|---|---|---|
| `JitProvisioningEnabled` | `false` | Legt einen unbekannten föderierten Benutzer bei der ersten Anmeldung an. Ist es aus, wird ein unbekannter Benutzer mit `access_denied` abgelehnt |
| `AllowUninvitedJit` | `false` | Wenn `ProvisioningAttributeParams` deklariert ist, wird auch ein Benutzer provisioniert, der ohne diesen Kontext ankommt. Siehe [Self-Service-SSO](self-service-sso) |
| `ProvisioningAttributeParams` | keine | Query-Schlüssel des Authorize-Requests, die bei einem per JIT provisionierten Benutzer als Provisionierungsattribute übernommen werden (das nach innen gerichtete Gegenstück zu `PassthroughParams`) |
| `PassthroughParams` | keine | Query-Schlüssel, die an die Upstream-Authorize-URL weitergegeben werden, siehe [Durchgereichte Query-Parameter](#passthrough-query-parameters) |
| `SessionExpClaim` | keiner | Siehe [Obergrenze der Sitzungslebensdauer](#session-lifetime-cap) |
| `ShowOnLogin` | `true` | `false` blendet die Schaltfläche "Weiter mit" aus; die Verbindung ist dann nur über `idp_hint` erreichbar |
| `ChallengeMfaAfterLogin` | `true` | `false` vertraut der eigenen MFA des Upstreams und überspringt die lokale Abfrage |
| `IsExternalConnection` | `false` | Kennzeichnet einen IdP eines Drittanbieters, der dem Kunden gehört. Hebt `UseUpstreamSubjectAsUserId` und `AutoLinkExistingByEmail` auf, auch wenn sie gesetzt sind |
| `UseUpstreamSubjectAsUserId` | `false` | Die lokale ID eines JIT-Benutzers ist das Upstream-`sub` statt einer neuen GUID. Nur für eigene (First-Party-)Verbindungen |
| `AutoLinkExistingByEmail` | `false` | Verknüpft anhand der E-Mail-Adresse mit einem bestehenden lokalen Konto, auch wenn `AllowedDomains` die Domain nicht abdeckt. Nur für eigene (First-Party-)Verbindungen |
| `RevalidateOnRefresh` | `false` | Siehe [Föderierte Sitzungen](federated-sessions) |
| `InteractionPath` | keiner | Pfad in der Login-App, der vor der Föderation eines `idp_hint`-Requests angezeigt wird (muss mit `/` beginnen) |
| `OrganizationId` | keine | Bindet die Verbindung an eine Organisation, siehe [Self-Service-SSO](self-service-sso#organisation-scoped-connections) |

> **Ein IdP in Ihrem eigenen privaten Netz.** `MetadataLocation` muss https verwenden und standardmäßig zu einer öffentlich routbaren Adresse auflösen: Authagonal lehnt interne Ziele bei jeder abgerufenen URL ab, sowohl bei der URL als auch erneut am Socket. Um mit einem IdP im eigenen Rechenzentrum zu föderieren, tragen Sie ihn in [`Auth:AllowedInternalTargets`](configuration#outbound-fetches-ssrf-guard) ein. Das deckt den gesamten Austausch ab, einschließlich `token_endpoint`, `userinfo_endpoint` und `jwks_uri`, die das Discovery-Dokument nennt. https bleibt Pflicht: Dieses Dokument liefert die Schlüssel, gegen die jedes Upstream-`id_token` validiert wird, und ein privates Netz ist kein sicherer Kanal.

**Option B, Admin-API (für die Verwaltung zur Laufzeit):**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"],
    "jitProvisioningEnabled": true
  }'
```

Der Body zum Anlegen akzeptiert `connectionName`, `metadataLocation`, `clientId` und `clientSecret` (alle Pflicht) sowie `iconUrl`, `redirectUrl` (ignoriert, optional), `organizationId`, `allowedDomains`, `passthroughParams`, `jitProvisioningEnabled` (Standard `false`), `challengeMfaAfterLogin` (Standard `true`) und `interactionPath`. Die Verbindungs-ID wird vom Server erzeugt und im Body der `201` zurückgegeben (das Client-Secret wird nie zurückgegeben). `metadataLocation` muss https verwenden und wird beim Anlegen gegen den Schutz für ausgehende Abrufe geprüft. Die übrigen Flags der obigen Tabelle (`SessionExpClaim`, `ShowOnLogin`, `IsExternalConnection`, `RevalidateOnRefresh` und der Rest) lassen sich über die Route zum Anlegen nicht setzen: Belegen Sie sie aus der Konfiguration vor oder schreiben Sie sie aus Hosting-Code über `IOidcProviderStore`. Für eine OIDC-Verbindung gibt es keine Route zum Aktualisieren; um eine zu ändern, löschen Sie sie und legen sie neu an (oder bearbeiten die Seed-Konfiguration). `GET /api/v1/oidc/connections/{connectionId}` und `DELETE` vervollständigen den Satz.

### 2. SSO-Domain-Routing {#2-sso-domain-routing}

Ist `AllowedDomains` angegeben (in der Konfiguration oder über die API zum Anlegen), werden SSO-Domainzuordnungen automatisch registriert. Ohne Domain-Routing können Benutzer weiterhin über `/oidc/{connectionId}/login` zur OIDC-Anmeldung geleitet werden.

## Endpunkte {#endpoints}

| Endpunkt | Beschreibung |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...&loginHint=...` | Startet die OIDC-Anmeldung. Erzeugt PKCE, State und Nonce, leitet den Upstream-Scope und die durchgereichten Parameter aus `returnUrl` ab und leitet an den Autorisierungsendpunkt des IdP weiter (`loginHint` wird, sofern vorhanden, als `login_hint` an den Upstream gesendet). `404` bei einer unbekannten Verbindung. |
| `GET /oidc/callback` | Verarbeitet den Callback des IdP. Tauscht den Code gegen Tokens, validiert das id_token, übernimmt jeden Claim, der kein Protokoll-Claim ist, als `federated:*` in das Cookie und legt den Benutzer an bzw. meldet ihn an. |

## Durchreichen von Scopes und Claims {#scope-and-claim-flow-through}

Die Scopes, die die nachgelagerte RP bei `/connect/authorize` anfordert, werden an den Upstream-IdP weitergegeben, **gefiltert auf die Standard-OIDC-Scopes**: `openid`, `profile`, `email`, `address`, `phone`, wobei `openid` immer enthalten ist. Alles andere, was die RP angefordert hat (eigene API-Scopes, `offline_access`, …), wird vor dem Upstream-Aufruf verworfen (einzige Ausnahme ist eine Verbindung mit `RevalidateOnRefresh`, die `offline_access` wieder hinzufügt, um ein Upstream-Refresh-Token zu erhalten). Ein strikter IdP wie Google gibt bei unbekannten Werten `invalid_scope` zurück, und der Upstream muss nur den Benutzer identifizieren; die eigenen Scopes der RP werden auf den von Authagonal ausgestellten Tokens berücksichtigt, nicht auf denen des Upstreams. Welche Claims der Upstream-IdP auch immer abhängig von den Scopes in das id_token aufnimmt, sie kommen zu Authagonal zurück, werden im Cookie-Ticket als Claims `federated:<name>` abgelegt und gelangen beim nächsten Durchlauf von `/connect/authorize` in `OidcSubject.FederationClaims`. Von dort gibt `ProtocolTokenService` sie auf den von Authagonal ausgestellten Tokens erneut aus, gefiltert durch dieselbe Whitelist `Scope.UserClaims`, die auch `CustomAttributes` filtert. Bei gleichem Schlüssel gewinnt der Wert aus dem eigenen Benutzerspeicher von Authagonal: Diese Claims kommen unverändert vom Upstream-IdP, und dürften sie überschreiben, könnte ein vom Kunden kontrollierter IdP jeden über einen Scope freigegebenen Claim über den eigenen Benutzer neu behaupten und damit den Datensatz dieses Servers übertrumpfen. Ein Upstream-Claim ohne gespeichertes Gegenstück wird trotzdem durchgereicht.

Im Ergebnis gibt es keine Allowlist pro Verbindung für zu erhaltende Claims. Jeder Claim, der kein Protokoll-Claim ist und den der Upstream in das id_token schreibt, wird übernommen; welche davon die nachgelagerten Tokens erreichen, steuert `UserClaims` des nachgelagerten Scopes. Deklarieren Sie den Claim dort, und der Wert wird durchgereicht.

`FederationClaims` übersteht die Rotationen bei der Erneuerung getrennt von `CustomAttributes`, sodass der Föderationskontext pro Sitzung (etwa ein beim ursprünglichen Authorize erfasstes Share-Link-Token) erhalten bleibt, während benutzerbezogene Attribute weiterhin frisch aus dem Benutzerspeicher gelesen werden.

## Durchgereichte Query-Parameter {#passthrough-query-parameters}

`OidcProviderConfig.PassthroughParams` ist eine Whitelist von Query-Schlüsseln pro Verbindung, die aus dem ursprünglichen `/authorize`-Request in die Authorize-URL des Upstream-IdP übernommen werden. Die Standardparameter (`scope`, `state`, `nonce`, PKCE) werden immer weitergegeben; dies ist für zusätzliche, von der RP vorgegebene Werte gedacht, etwa einen einmaligen Berechtigungsnachweis, den der Upstream zur Authentifizierung braucht (zum Beispiel `link_token` bei Share-Link-IdPs).

Steht ein Schlüssel auf der Whitelist, übernimmt Authagonal seinen Wert aus der ursprünglichen `/authorize`-Query (die über `returnUrl` mitgeführt wird) und hängt ihn an die Upstream-URL an. Alles, was nicht auf der Whitelist steht, wird stillschweigend verworfen.

## Obergrenze der Sitzungslebensdauer {#session-lifetime-cap}

`OidcProviderConfig.SessionExpClaim` ist der optionale Name eines Claims im id_token (Unix-Sekunden), dessen Wert die Lebensdauer der lokalen Sitzung begrenzt. Ist er vorhanden, wird der Upstream-Wert als `session_max_exp` im Cookie-Ticket und in den ausgestellten Autorisierungscode übernommen; Access, ID und Refresh Tokens werden so gekürzt, dass kein Token, auch keines aus einer Rotation, die Upstream-Sitzung überdauert. Nützlich, wenn der Upstream-IdP kürzere Sitzungsgrenzen durchsetzt, als Authagonal es standardmäßig täte.

## Sicherheitsfunktionen {#security-features}

- **PKCE**: code_challenge mit S256 bei jedem Autorisierungs-Request
- **Nonce-Validierung**: Die Nonce wird zusammen mit dem State gespeichert und muss im id_token vorhanden sein und übereinstimmen
- **State-Validierung**: einmalig verwendbar (atomar verbraucht über `IOidcStateStore`, mit Ablaufzeit gespeichert) **und an den Browser gebunden**: Beim Login wird ein auf `/oidc` beschränktes Cookie mit `SameSite=Lax` gesetzt, das beim Callback zum `state` passen muss. So kann ein Angreifer keinen selbst begonnenen Föderationsablauf abschließen, indem er die Callback-URL einem Opfer zuspielt (Login-CSRF)
- **Validierung der id_token-Signatur**: Schlüssel werden vom JWKS-Endpunkt des IdP abgerufen; Issuer, Audience und Lebensdauer werden validiert
- **Fallback auf Userinfo**: Enthält das id_token keine E-Mail-Adresse, wird der Userinfo-Endpunkt abgefragt. Das `sub` aus Userinfo muss mit dem `sub` des id_token übereinstimmen (OIDC Core 5.3.2), andernfalls wird die Antwort ignoriert
- **Stabile Verknüpfung der Identität**: Ein wiederkehrender Benutzer wird über Anbieter + `sub` aufgelöst, nie allein über die E-Mail-Adresse. Um eine föderierte Identität anhand der E-Mail-Adresse an ein **bereits bestehendes** lokales Konto zu binden, müssen die `AllowedDomains` der Verbindung die Domain dieser E-Mail-Adresse abdecken (die ausdrückliche Zusicherung des Administrators, dass die Domain dem IdP gehört), oder `AutoLinkExistingByEmail` muss bei einer eigenen Verbindung gesetzt sein; abgelehnt wird es, wenn die Domain an eine andere Verbindung geleitet wird. Ein Konto, das bereits an die föderierte Identität einer anderen Verbindung gebunden ist, wird nur übernommen, wenn diese Verbindung für die Domain maßgeblich ist; in diesem Fall wird die alte Bindung entfernt. Ein vom Upstream behauptetes `email_verified` reicht *nicht* aus, um ein bestehendes Konto zu übernehmen
- **Durchsetzung der Domains**: Ist `AllowedDomains` gesetzt, darf die Verbindung nur Identitäten innerhalb dieser Domains behaupten (andernfalls `access_denied`)
- **JIT muss ausdrücklich aktiviert werden**: Sofern die Verbindung nicht `JitProvisioningEnabled` setzt, wird ein unbekannter Benutzer mit `access_denied` abgelehnt. Greift JIT, kann ein Upstream, der kein `email_verified` behauptet, kein Konto anlegen, ebenso wenig eine Verbindung, deren E-Mail-Domain an eine andere Verbindung geleitet wird
- **Schutz vor offenen Weiterleitungen**: `returnUrl` muss ein relativer Pfad auf derselben Site sein; protokollrelative Formen (`//`) und Formen mit Backslash werden abgelehnt
- **Lokale MFA gilt standardmäßig weiterhin**: Die Föderation belegt nur den ersten Faktor. Ein Benutzer mit eingerichteter MFA (oder dessen Client-Richtlinie MFA verlangt) wird nach dem Callback über die lokalen Seiten für MFA-Abfrage bzw. MFA-Einrichtung geleitet, statt direkt angemeldet zu werden; erst danach trägt die Sitzung die MFA-Markierung. Eine Verbindung mit `ChallengeMfaAfterLogin: false` überspringt dies und meldet den Benutzer allein aufgrund der Föderation als MFA-authentifiziert an
- **Metadaten wird nur eng begrenzt vertraut**: Das Discovery-Dokument muss https verwenden und seine URL an den darin genannten Issuer gebunden sein, und Upstream-id_tokens werden nur mit asymmetrischen Signaturalgorithmen akzeptiert (RS/PS/ES 256, 384, 512)
- **Bindung an die Organisation**: Ein Benutzer, der sich über eine organisationsgebundene Verbindung anmeldet, wird Mitglied dieser Organisation, und die Sitzung trägt deren `org_id`

## Besonderheiten von Azure AD {#azure-ad-specifics}

Azure AD gibt E-Mail-Adressen manchmal als JSON-Array im Claim `emails` zurück (insbesondere bei B2C). Authagonal berücksichtigt das, indem es sowohl den Claim `email` als auch das Array `emails` prüft (ein JSON-Array oder ein einzelner String).

## Unterstützte Anbieter {#supported-providers}

Jeder OIDC-konforme Anbieter, der Folgendes unterstützt:
- Authorization-Code-Ablauf
- PKCE (S256)
- Discovery-Dokument (`.well-known/openid-configuration`)

Getestet mit:
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C

## Siehe auch {#related-guides}

- [Self-Service-SSO](self-service-sso): Varianten der JIT-Provisionierung (nur mit Einladung oder Self-Service), die Vertrauensstufe der Verbindung und Zwischenseiten vor der Föderation.
- [Föderierte Sitzungen](federated-sessions): einen Widerruf im Upstream mit `RevalidateOnRefresh` auf die lokale Sitzung durchschlagen lassen.
- [Upgrade eines Benutzers](user-upgrade): einem föderierten oder Gastkonto erlauben, ein eigenes Passwort zu übernehmen.
