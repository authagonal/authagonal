---
layout: default
title: Föderierte Sitzungen
locale: de
---

# Föderierte Sitzungen mit dem Upstream synchron halten

Wenn sich ein Benutzer über einen [externen IdP](oidc-federation) anmeldet, stellt Authagonal eine *eigene*
Sitzung und eigene Tokens aus. Standardmäßig führt diese lokale Sitzung danach ein Eigenleben: Deaktiviert der
Kunde den Benutzer in seinem Verzeichnis oder wird der Freigabelink des Gasts im Upstream widerrufen, funktioniert
die lokale Authagonal-Sitzung weiter, bis ihr Cookie abläuft.

Damit Offboarding und Widerruf schnell wirksam werden, aktivieren Sie **`RevalidateOnRefresh`**. Dann löst Authagonal
bei jeder lokalen Token-Erneuerung das Upstream-Refresh-Token beim IdP ein. Meldet der Upstream, dass die
Berechtigung nicht mehr besteht, wird die lokale Erneuerung abgelehnt, und die Sitzung erhält innerhalb einer
Access-Token-Lebensdauer keine neuen Tokens mehr.

`RevalidateOnRefresh` ist ausschließlich eine Einstellung von **OIDC-Verbindungen**. SAML kennt kein Refresh Token,
das eingelöst werden könnte, daher kann eine SAML-Verbindung nicht revalidieren; begrenzen Sie solche Sitzungen
stattdessen über das Sitzungsablaufdatum der Assertion selbst (siehe [SAML](saml)).

## Aktivieren {#enable-it}

Pro Verbindung (wie unten in der Seed-Konfiguration gesetzt oder beim Anlegen der Verbindung über die
[Admin-API](admin-api) gesetzt); außerdem muss der Upstream tatsächlich ein Refresh Token ausstellen:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "acme-entra",
      "MetadataLocation": "https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration",
      "ClientId": "...", "ClientSecret": "...",
      "AllowedDomains": ["acme.com"],
      "RevalidateOnRefresh": true
    }
  ]
}
```

Das ist die gesamte Einrichtung. Ist das Flag gesetzt, ergänzt Authagonal den beim Upstream angeforderten Scope um
`offline_access` (sofern der nachgelagerte Request ihn nicht bereits enthält), legt das vom Upstream zurückgegebene
Refresh Token ab und löst es bei jeder lokalen Erneuerung von Server zu Server ein. Das Upstream-Refresh-Token wird
**nie** an einen Client ausgegeben. Es liegt verschlüsselt in einem dauerhaften Speicher pro Sitzung, wird beim
Login mit einer pauschalen Ablaufzeit von sieben Tagen (der absoluten Obergrenze der Sitzung) angelegt und dient
ausschließlich der Revalidierung.

Der Upstream muss mitspielen: Erhält dessen App-Registrierung nie `offline_access` (etwa weil keine Zustimmung
erteilt wurde), kommt kein Refresh Token zurück, und es gibt nichts einzulösen. Authagonal protokolliert in diesem
Zustand bei jeder Erneuerung eine Warnung (`RevalidateOnRefresh is enabled for connection ... but no upstream refresh token is held`),
und der Upstream wird **nicht** erneut geprüft.

## Was bei einer Erneuerung passiert {#what-happens-on-refresh}

1. Die RP erneuert wie gewohnt ein Authagonal-Token (`grant_type=refresh_token` an `/connect/token`).
2. Authagonal löst das Upstream-Refresh-Token am Token-Endpunkt des IdP ein:
   - **Erfolg** → Die lokale Erneuerung wird fortgesetzt; hat der Upstream sein Token rotiert, wird das neue
     gespeichert und von jedem RP-Grant der Sitzung gemeinsam genutzt.
   - **`invalid_grant`** → Die föderierte Berechtigung besteht nicht mehr (Benutzer deaktiviert, Sitzung widerrufen,
     Token abgelaufen). Die lokale Erneuerung wird **abgelehnt**: Die RP erhält `invalid_grant` von `/connect/token`,
     und das gespeicherte Upstream-Token wird gelöscht. Die Ablehnung lässt nur diesen einen Request scheitern; sie
     widerruft den Authagonal-Grant nicht. Das Refresh Token der RP bleibt also unverbraucht und wird so lange
     weiter abgelehnt, wie der Upstream widerrufen bleibt.
   - **Jeder andere 4xx-Fehler** (z. B. `invalid_client` wegen eines rotierten oder falsch konfigurierten Secrets oder
     ein 429), ein 5xx, ein nicht auswertbarer Fehler-Body, ein Transportfehler oder eine Verbindung, die sich nicht
     laden lässt (gelöscht, Fehler bei Discovery oder Secret) → wird als **vorübergehend** behandelt: Die Sitzung
     bleibt bestehen, damit ein Betriebsfehler nicht sämtliche föderierten Benutzer auf einen Schlag abmeldet.
     Korrigieren Sie die Konfiguration; es geht nichts verloren. Die Sitzung bleibt weiterhin durch die absolute
     Obergrenze der Sitzung begrenzt.

Weil es **ein** Upstream-Token pro Browsersitzung gibt (Schlüssel aus Benutzer + Verbindung + Sitzung), liest und
rotiert eine zweite RP, die der Benutzer öffnet, *dasselbe* Token. So kann die Erneuerung der einen App nicht dazu
führen, dass eine andere App eine ungültige Kopie in der Hand hält.

## Nichts zu implementieren {#nothing-to-implement}

Hier ist keine Schnittstelle zu schreiben. Der dauerhafte Speicher (`IUpstreamRefreshTokenStore`) wird von den
Storage-Providern für Azure, AWS und SQL automatisch registriert, und die Einlösung erfolgt intern. Sie setzen
lediglich `RevalidateOnRefresh` an den Verbindungen, deren Upstream eine widerrufbare Berechtigung verwaltet.

Das gespeicherte Token wird entfernt, wenn die Sitzung auf einem beliebigen Weg endet, der es erreicht:
Abmeldung, Widerruf einer Sitzung über die Kontoseite, "überall abmelden" und die Bereinigung abgelaufener
Einträge. Registriert ein Host keinen Speicher, dient die im Sitzungscookie mitgeführte Kopie als Fallback.

Jede per OIDC föderierte Sitzung vermerkt außerdem, welcher Verbindung sie gehört (der Claim
`upstream_connection_id`), unabhängig davon, ob diese Verbindung revalidiert. Das ist reine Buchführung: Für eine
Verbindung ohne das Flag wird nichts eingelöst.

> **Reichweite:** Diese Funktion ist überall aktiv, wo der Speicher registriert ist (bei den Providern für Azure
> Table, DynamoDB und SQL). Aktivieren Sie sie für Verbindungen zu einem **vertrauenswürdigen** Upstream,
> insbesondere zu einem, der seine Refresh Tokens bei jeder Verwendung rotiert (Entra, Auth0), und kombinieren Sie
> sie bei IdPs von Drittanbietern mit `IsExternalConnection` (siehe [Self-Service-SSO](self-service-sso)).

## Ergänzend: eine harte Obergrenze für die Sitzung {#complementary-a-hard-session-cap}

`RevalidateOnRefresh` hält eine Sitzung mit Widerrufen im Upstream in Einklang. Wenn die lokale Sitzung stattdessen
(oder zusätzlich) nie *länger leben* soll als die vom Upstream zugesicherte Sitzung, setzen Sie `SessionExpClaim` auf
den Namen eines id_token-Claims, der einen Ablaufzeitpunkt (Unix-Sekunden) enthält. Authagonal begrenzt die lokale
Sitzung und alle daraus ausgestellten Tokens (einschließlich Rotationen von Refresh Tokens und einschließlich eines
über diese Sitzung genehmigten Device-Code-Grants) auf diese Grenze. Siehe
[OIDC-Föderation → Obergrenze der Sitzungslebensdauer](oidc-federation).

Der Device Flow verdient eine eigene Erwähnung, weil er bis vor Kurzem die Ausnahme war: Der Device-Code-Datensatz
hatte keinen Platz, um die Grenze der genehmigenden Sitzung mitzuführen. Ein über eine föderierte Sitzung
genehmigtes Gerät stellte deshalb nach dem Ende dieser Sitzung weiter Tokens für die volle absolute
Refresh-Lebensdauer des Clients aus, und auch `RevalidateOnRefresh` fragte dafür nie erneut beim Upstream nach.
Beides richtet sich jetzt nach der genehmigenden Sitzung. Ein über eine *nicht föderierte* Sitzung genehmigtes
Gerät hat keine Grenze, die es übernehmen könnte; das entspricht dem Ergebnis des Authorize-Ablaufs.
