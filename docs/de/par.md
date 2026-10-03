---
layout: default
title: Pushed Authorization Requests
locale: de
---

# Pushed Authorization Requests (PAR)

Mit [RFC 9126](https://www.rfc-editor.org/rfc/rfc9126) sendet ein Client die Parameter seines Authorize-Requests per POST direkt an den Server, authentifiziert sich dabei auf dem üblichen Weg und erhält eine kurzlebige, opake `request_uri`, die er an den Browser weitergibt. Der Browser ruft dann `/connect/authorize?request_uri=...&client_id=...` auf, statt jeden Parameter in der URL mitzuführen.

Gründe für den Einsatz:

- Authorize-Parameter tauchen nie im Browserverlauf, in Server-Logs oder in `Referer`-Headern auf.
- Der Server authentifiziert den Client bereits beim Push, sodass die Integrität der Parameter geprüft ist, bevor überhaupt eine Weiterleitung stattfindet.
- Umfangreiche Parametersätze (große `claims`-Requests, Abläufe mit mehreren Ressourcen) sprengen keine URL-Längenbegrenzungen.

## Endpunkt {#endpoint}

```
POST /connect/par
Content-Type: application/x-www-form-urlencoded
```

Die Authentifizierung funktioniert wie bei `/connect/token`: HTTP Basic mit `client_id`/`client_secret` oder formularkodierte Anmeldedaten. Vertrauliche Clients müssen sich authentifizieren; öffentliche Clients senden ohne Secret. Schlägt die Client-Authentifizierung fehl, antwortet der Endpunkt mit `401` (gemäß RFC 9126; anders als beim Token-Endpunkt, wo nur `invalid_client` eine 401 ist).

Der Formular-Body enthält dieselben Parameter, die sonst an `/connect/authorize` gehen würden (`response_type`, `redirect_uri`, `scope`, `state`, `code_challenge`, `code_challenge_method`, `nonce`, `resource` usw.). `request_uri` selbst wird abgelehnt: Das Verketten von PARs verbietet Abschnitt 2.1 der Spezifikation. Enthält der Body eine `client_id`, muss sie mit dem authentifizierten Client übereinstimmen. Wie der Token-Endpunkt lehnt die Route einen unverschlüsselten `http`-Request ab, sofern nicht `AuthagonalProtocolOptions.AllowInsecureHttp` gesetzt ist.

Der Request wird beim Push genauso validiert, wie `/connect/authorize` ihn validieren würde (registrierte `redirect_uri`, erlaubte Scopes, PKCE, `prompt`-Werte usw.). Ein ungültiger Request wird sofort mit `400 invalid_request` abgelehnt, und es wird keine `request_uri` ausgestellt. So zeigt sich der Fehler beim Client und nicht erst beim Endbenutzer mitten im Ablauf. `authorization_details` wird mit `invalid_authorization_details` abgelehnt (Rich Authorization Requests gehören an den Token-Endpunkt, nicht hierher).

### Grenzwerte {#limits}

- Der Body ist auf 32 KB begrenzt, mit höchstens 64 Formularfeldern, Namen bis 256 Zeichen und 8 KB pro Wert. Alles, was größer ist, wird mit `413 invalid_request` abgelehnt.
- Requests sind auf 60 pro Minute je Client und Quelladresse sowie auf insgesamt 300 pro Minute je Client begrenzt; darüber hinaus antwortet der Endpunkt mit `429 temporarily_unavailable`.

### Antwort {#response}

```
HTTP/1.1 201 Created
```
```json
{
  "request_uri": "urn:ietf:params:oauth:request_uri:abc123...",
  "expires_in": 90
}
```

Die `request_uri` ist nur einmal verwendbar. Sie wird aus dem Speicher entfernt, sobald der Autorisierungscode für sie ausgestellt ist. Wird sie nie eingelöst, läuft sie nach 90 Sekunden ab.

### Autorisierungsschritt {#authorization-step}

```
GET /connect/authorize?client_id=my-rp&request_uri=urn:ietf:params:oauth:request_uri:abc123...
```

Ist `request_uri` vorhanden, werden alle anderen Parameter aus dem gepushten Payload übernommen; alles andere in der URL wird ignoriert (außer `client_id`, die mit dem Client übereinstimmen muss, der den Payload gepusht hat, und dem Parameter `error`, den ein fehlgeschlagener Föderations-Roundtrip anhängt). Eine `request_uri`, die unbekannt, abgelaufen, bereits verbraucht oder von einem anderen Client gepusht ist, wird mit `invalid_request` abgelehnt. Akzeptiert werden nur die opaken URNs, die der eigene PAR-Endpunkt dieses Servers ausgestellt hat: Jeder andere `request_uri`-Wert wird mit `request_uri_not_supported` abgelehnt, der `request`-Parameter aus RFC 9101 mit `request_not_supported`.

Gepushte Werte für `prompt` und `max_age` werden berücksichtigt. Ein PAR-Request mit `prompt=login` (oder mit einem `max_age`, das die Sitzung überschritten hat) wird nur von einer Sitzung erfüllt, deren `auth_time` zum Zeitpunkt des Pushs oder danach liegt. Eine bereits bestehende Sitzung wird also einmal abgemeldet und neu authentifiziert, und der Rückweg vom Login stellt einen Code aus, statt in einer Schleife zu landen.

## PAR pro Client erzwingen {#requiring-par-per-client}

Setzen Sie `RequirePushedAuthorizationRequests = true` an einem Client, um einfache `/connect/authorize`-Requests dieses Clients abzulehnen. Jeder Authorize-Versuch ohne PAR liefert `invalid_request` mit der Beschreibung "This client requires requests to be pushed via /connect/par".

```csharp
new OAuthClient
{
    ClientId = "high-risk-rp",
    RequirePushedAuthorizationRequests = true,
    // ...
}
```

Das ist die empfohlene Einstellung für Clients, die mit sensiblen Scopes arbeiten: In Kombination mit PKCE entfällt die Adressleiste als Angriffsfläche.

## Lebensdauer und Speicherung {#lifetime-and-storage}

Das vom Push zurückgegebene `expires_in` beträgt 90 Sekunden, und dieses Zeitfenster deckt den Schritt vom Push bis zum ersten `/connect/authorize`-Request ab. Sobald der Datensatz zum ersten Mal abgerufen wird, wird er (einmalig) bis zu einer absoluten Frist von 15 Minuten ab dem Push verlängert, damit der Benutzer Login, MFA und Zustimmung abschließen kann. Die Werte 90 Sekunden und 15 Minuten sind Konstanten, keine Konfiguration. Gepushte Payloads werden über denselben `IGrantStore` gespeichert wie Autorisierungscodes und Refresh Tokens und übernehmen damit automatisch die Persistenz- und Replikationsstrategie des Hosts.

## Discovery {#discovery}

Der PAR-Endpunkt macht sich in `.well-known/openid-configuration` wie folgt bekannt:

```json
{
  "pushed_authorization_request_endpoint": "https://auth.example.com/connect/par"
}
```
