---
layout: default
title: Schnellstart
locale: de
---

# Schnellstart

Bringen Sie Authagonal in 5 Minuten lokal zum Laufen.

## 1. Server starten {#1-start-the-server}

```bash
docker compose up
```

Damit läuft Authagonal unter `http://localhost:8080` mit Azurite als Speicher.

> Die Compose-Datei setzt `Auth__AllowInsecureHttp=true`, weil RFC 6749 §3.1/§3.2 TLS an Autorisierungs- und Token-Endpunkt vorschreiben und Authagonal Klartextanfragen an `/connect/*` andernfalls ablehnt. Dieser Schalter ist für den Laptop gedacht. Alles, was jemand anderes erreichen kann, gehört hinter einen TLS-terminierenden Proxy, der `X-Forwarded-Proto: https` weiterreicht, und der Schalter wird entfernt: siehe [Installation](installation).

## 2. Prüfen, ob der Server läuft {#2-verify-its-running}

```bash
# Health check
curl http://localhost:8080/health

# OIDC discovery
curl http://localhost:8080/.well-known/openid-configuration

# Login page (returns the SPA)
curl http://localhost:8080/login
```

## 3. Einen Client registrieren {#3-register-a-client}

Fügen Sie Ihrer `appsettings.json` einen Client hinzu (oder übergeben Sie ihn über Umgebungsvariablen):

```json
{
  "Clients": [
    {
      "ClientId": "my-web-app",
      "ClientName": "My Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["http://localhost:3000/callback"],
      "PostLogoutRedirectUris": ["http://localhost:3000"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["http://localhost:3000"],
      "RequirePkce": true,
      "RequireClientSecret": false
    }
  ]
}
```

Clients werden beim Start angelegt; das lässt sich gefahrlos bei jedem Deployment ausführen.

## 4. Eine Anmeldung starten {#4-initiate-a-login}

Leiten Sie Ihre Benutzer weiter an:

```
http://localhost:8080/connect/authorize
  ?client_id=my-web-app
  &redirect_uri=http://localhost:3000/callback
  &response_type=code
  &scope=openid profile email
  &state=random-state
  &code_challenge=...
  &code_challenge_method=S256
```

Der Benutzer sieht die Anmeldeseite, authentifiziert sich und wird mit einem Autorisierungscode zurückgeleitet.

> **Erster Benutzer:** Registrieren Sie einen unter `http://localhost:8080/login/register` oder legen Sie einen über die [Admin-API](admin-api) an. Die Selbstregistrierung verschickt eine Bestätigungs-E-Mail, und ohne konfigurierten E-Mail-Versand (der lokale Standard) wird diese Mail verworfen. Setzen Sie für lokale Tests daher `Auth__AutoConfirmEmailDomains__0=example.dev` (eine beliebige Domain, mit der Sie sich registrieren), um die Bestätigung zu überspringen, oder konfigurieren Sie `Email:ResendApiKey` + `Email:SenderEmail`. Siehe [Konfiguration → E-Mail](configuration#email).

## 5. Den Code einlösen {#5-exchange-the-code}

```bash
curl -X POST http://localhost:8080/connect/token \
  -d grant_type=authorization_code \
  -d code=THE_CODE \
  -d redirect_uri=http://localhost:3000/callback \
  -d client_id=my-web-app \
  -d code_verifier=THE_VERIFIER
```

Antwort:

```json
{
  "access_token": "eyJ...",
  "id_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 1800,
  "scope": "openid profile email"
}
```

`expires_in` ist die `AccessTokenLifetimeSeconds` des Clients (1800 für einen beim Start angelegten Client, sofern Sie nichts anderes festlegen). Hier erscheint kein `refresh_token`: Ein Client erhält nur dann eines, wenn er `AllowOfflineAccess` setzt und die Anfrage den Scope `offline_access` anfordert.

## Funktionierende Demo {#working-demo}

Das Verzeichnis `demos/sample-app/` enthält eine vollständige React-SPA samt API, die den oben beschriebenen OIDC-Ablauf vollständig implementiert. Anleitungen finden Sie in der [README der Demos](https://github.com/authagonal/authagonal/tree/master/demos).

## Nächste Schritte {#next-steps}

- [Konfiguration](configuration), vollständige Referenz aller Einstellungen
- [Erweiterbarkeit](extensibility), als Bibliothek hosten, eigene Hooks hinzufügen
- [Branding](branding), die Login-UI anpassen
- [SAML](saml), SAML-SSO-Provider hinzufügen
- [Provisionierung](provisioning), Benutzer in nachgelagerte Anwendungen provisionieren
