---
layout: default
title: Front-Channel Logout
locale: de
---

# Front-Channel Logout

Authagonal implementiert **OpenID Connect Front-Channel Logout 1.0**, einen browsergesteuerten Logout-Mechanismus, der den [Back-Channel Logout](index#key-features) ergänzt. Während der Back-Channel Logout ein POST von Server zu Server ist, rendert der Front-Channel Logout die Logout-URL jeder Relying Party in einem versteckten iframe, sodass die Browsersitzung jeder App (Cookies, Local Storage) direkt im Browser des Benutzers bereinigt wird.

## Wann welches Verfahren {#when-to-use-which}

| Aspekt | Back-Channel | Front-Channel |
|---|---|---|
| Serverseitige Sitzungen | ✅ | ❌ |
| Browser-Cookies / Local Storage | ❌ | ✅ |
| Funktioniert, wenn der Browser des Benutzers offline ist | ✅ | ❌ |
| Übersteht Netzwerkfehler (Wiederholung) | ✅ | ❌ (ein einziger Best-Effort-Versuch) |

Die meisten Apps profitieren davon, **beide** zu konfigurieren. Back-Channel stellt sicher, dass der Server benachrichtigt wird; Front-Channel räumt den Browser auf.

## Client-Konfiguration {#client-configuration}

Ergänzen Sie den `OAuthClient`-Datensatz um eine Front-Channel-Logout-URI:

```json
{
  "clientId": "myapp",
  "frontChannelLogoutUri": "https://myapp.example.com/oidc/frontchannel",
  "frontChannelLogoutSessionRequired": true
}
```

| Feld | Beschreibung |
|---|---|
| `FrontChannelLogoutUri` | Der im Browser aufrufbare Logout-Endpunkt des Clients |
| `FrontChannelLogoutSessionRequired` | Bei `true` (Standard) wird die URL mit den Query-Parametern `iss` und `sid` aufgerufen, damit der Client den Logout der konkreten Sitzung zuordnen kann |

## Funktionsweise {#how-it-works}

Wenn der Browser `/connect/endsession` aufruft (GET oder POST):

1. **Bestätigung (CSRF-Schutz).** Hat der Browser eine angemeldete Sitzung und trägt der Request kein `id_token_hint`, dessen `sub` zu dieser Sitzung passt, rendert der Server zunächst eine Seite "Abmelden?" mit einer Bestätigungsschaltfläche, statt den Benutzer abzumelden. Die Schaltfläche sendet per POST zurück, zusammen mit einem kurzlebigen (15 Minuten) Token, das an diese Sitzung gebunden ist. Das verhindert, dass eine fremde Seite die Sitzung eines Benutzers beendet, indem sie einfach zu dem Endpunkt navigiert (das Sitzungscookie ist `SameSite=Lax` und wird daher bei einem seitenübergreifenden Top-Level-GET mitgesendet). Ein passendes `id_token_hint` ersetzt die Bestätigung.
2. Der Server ermittelt alle Clients, für die der Benutzer aktuell Grants besitzt.
3. Für jeden Client mit einer `FrontChannelLogoutUri`, die die Prüfung ausgehender URLs besteht (Loopback ist erlaubt, weil der eigene Browser des Benutzers den Request stellt, private Adressbereiche und Link-Local-Adressen dagegen nicht), baut der Server eine URL und hängt `iss=<issuer>` an (sowie `sid=<session_id>`, sofern die Sitzung eine hat), wenn `FrontChannelLogoutSessionRequired` den Wert `true` hat.
4. Der Server widerruft die für die Sitzung ausgestellten Grants, meldet den Benutzer vom Cookie des Autorisierungsservers ab, stößt im Hintergrund die Back-Channel-Logout-Benachrichtigungen an und gibt, sofern mindestens eine Front-Channel-URL erzeugt wurde, eine HTML-Seite mit je einem versteckten `<iframe>` zurück:
   ```html
   <iframe src="https://myapp.example.com/oidc/frontchannel?iss=https%3A%2F%2Fauth.example.com&sid=abc123" style="display:none"></iframe>
   ```
   Die Seite trägt eine `Content-Security-Policy`, deren `frame-src` auf die Origins dieser URLs beschränkt ist, und enthält keine Skripte.
5. Das Ziel nach dem Logout wird auf dieselbe Weise bestimmt, ob iframes beteiligt sind oder nicht. Die `post_logout_redirect_uri` wird nur berücksichtigt, wenn der Request den Client identifiziert (über die Audience des `id_token_hint` oder den Parameter `client_id`) und die URI zu den registrierten `PostLogoutRedirectUris` dieses Clients gehört (ein mitgelieferter `state`-Parameter wird angehängt). Mit iframes wartet die Seite 2 Sekunden (per `meta refresh`) und leitet dann weiter oder zeigt eine Meldung "abgemeldet", wenn es kein gültiges Ziel gibt. Ohne Front-Channel-URLs leitet der Server sofort weiter (`302`) oder antwortet mit `200` und einer JSON-`message`, wenn es kein gültiges Ziel gibt.

`id_token_hint` wird nur akzeptiert, wenn es ein von diesem Server signiertes ID Token (ES256, `typ: JWT`) mit genau einer Audience ist. Abgelaufene Tokens werden akzeptiert. Access Tokens und Logout Tokens werden als Hint abgelehnt. Werden sowohl `client_id` als auch `id_token_hint` gesendet und benennen sie unterschiedliche Clients, schlägt der Request mit `400 invalid_request` fehl.

Der JSON-Endpunkt `POST /api/auth/logout` (den die Schaltfläche "Abmelden" der Login-App verwendet) führt dieselben Widerrufs- und Benachrichtigungsschritte aus. Er rendert keine iframes, sondern gibt die URLs in `frontchannel_logout_uris` zurück, damit der Aufrufer sie lädt (siehe die [Auth-API](auth-api#logout)).

## Clientseitiger Logout-Handler {#client-side-logout-handler}

Jede Relying Party sollte die URL implementieren, auf die `FrontChannelLogoutUri` verweist. Ein minimaler Handler:

```http
GET /oidc/frontchannel?iss=https://auth.example.com&sid=abc123
```

1. Prüfen Sie, ob `iss` dem erwarteten Autorisierungsserver entspricht.
2. Wird `sid` mitgeliefert, prüfen Sie, ob es zur Sitzungs-ID im Sitzungscookie passt.
3. Löschen Sie die lokale Sitzung (Cookies, serverseitige Sitzung, SPA-Speicher).
4. Antworten Sie mit `200 OK` und leerem Body (oder einer winzigen Seite); die Antwort ist für den Benutzer nie sichtbar.

```csharp
app.MapGet("/oidc/frontchannel", (HttpContext ctx) =>
{
    var iss = ctx.Request.Query["iss"].ToString();
    var sid = ctx.Request.Query["sid"].ToString();
    // Validate iss/sid, then clear local session
    ctx.SignOutAsync();
    return Results.Ok();
});
```

## Discovery-Dokument {#discovery-document}

Front-Channel Logout wird in `/.well-known/openid-configuration` angekündigt:

```json
{
  "frontchannel_logout_supported": true,
  "frontchannel_logout_session_supported": true
}
```

## Dynamische Client-Registrierung {#dynamic-client-registration}

Clients, die über die [dynamische Client-Registrierung](client-registration) registriert werden, können Folgendes angeben:

```json
{
  "frontchannel_logout_uri": "https://myapp.example.com/oidc/frontchannel",
  "frontchannel_logout_session_required": true
}
```

Die Registrierung lehnt eine Logout-URI ab, die keine externe Adresse ist (Loopback, Link-Local, private Adressbereiche und Namen unter `.localhost`/`.local`/`.internal` werden mit `invalid_client_metadata` abgelehnt).

## Einschränkungen {#limitations}

- **Best Effort**: iframes werden einmal geladen. Blockiert ein Netzwerkfehler oder eine Browser-Erweiterung sie, gibt es keine Wiederholung. Kombinieren Sie das Verfahren für mehr Zuverlässigkeit mit dem Back-Channel Logout.
- **Drittanbieter-Cookies**: Manche Browser blockieren Cookies in seitenübergreifenden iframes standardmäßig. Wenn Ihre RP auf First-Party-Cookies angewiesen ist, stellen Sie sicher, dass der Logout-Handler nicht davon abhängt, dass Cookies mitgesendet werden.
- **Zeitlimit**: Die Seite wartet etwa 2 Sekunden, bevor sie weiterleitet. Aufwendige Logout-Handler einer RP werden in dieser Zeit möglicherweise nicht fertig.

## Siehe auch {#related}

- [Dynamische Client-Registrierung](client-registration): Front-Channel-Parameter im Registrierungs-Request
- [OAuth-Scopes](scopes): Scope-bezogene Zustimmung ergänzt den Logout-Ablauf
