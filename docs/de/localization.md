---
layout: default
title: Lokalisierung
locale: de
---

# Lokalisierung

Die Login-UI bringt von Haus aus elf Locales mit: Englisch, vereinfachtes Chinesisch (`zh-Hans`), Deutsch (`de`), Französisch (`fr`), Spanisch (`es`), Vietnamesisch (`vi`), Portugiesisch (`pt`), Arabisch (`ar`), Afrikaans (`af`), Hindi (`hi`) und Japanisch (`ja`). Auch die Antworten der Server-API und die serverseitig gerenderten Seiten sind in allen elf Sprachen lokalisiert. Die Lokalisierung umfasst die Antworten der Server-API, die Login-UI und diese Dokumentationsseite.

## Unterstützte Sprachen {#supported-languages}

| Code | Sprache | Login-UI | Server-API |
|---|---|---|---|
| `en` | Englisch (Standard) | ✓ | ✓ |
| `zh-Hans` | Vereinfachtes Chinesisch | ✓ | ✓ |
| `de` | Deutsch | ✓ | ✓ |
| `fr` | Französisch | ✓ | ✓ |
| `es` | Spanisch | ✓ | ✓ |
| `vi` | Vietnamesisch | ✓ | ✓ |
| `pt` | Portugiesisch | ✓ | ✓ |
| `ar` | Arabisch (rechts nach links) | ✓ | ✓ |
| `af` | Afrikaans | ✓ | ✓ |
| `hi` | Hindi | ✓ | ✓ |
| `ja` | Japanisch | ✓ | ✓ |

## Server (API-Antworten) {#server-api-responses}

Der Server nutzt die eingebaute Lokalisierung von ASP.NET Core mit `IStringLocalizer<T>` und `.resx`-Ressourcendateien. Die Sprache wird anhand des HTTP-Headers `Accept-Language` ausgewählt.

### Was lokalisiert ist {#what-is-localized}

- Fehlermeldungen der Passwortvalidierung
- Beschriftungen der Passwortrichtlinie (`GET /api/auth/password-policy`)
- Meldungen im Ablauf zum Zurücksetzen des Passworts (Token-Fehler, Ablauf, Erfolg)
- Allgemeine Fehlerbeschreibungen aus der Middleware zur Ausnahmebehandlung
- Meldungen der administrativen Benutzerverwaltung (E-Mail-Bestätigung, Verifizierung usw.)
- Bestätigungsmeldung zum Beenden der Sitzung
- Serverseitig gerenderte Seiten (Ergebnis der E-Mail-Bestätigung, Bestätigung zum Beenden der Sitzung und Abmeldeseite), einschließlich `<html lang>` und `dir="rtl"` für Arabisch
- Von der Bibliothek versendete E-Mails (`EmailService`: Verifizierung, Passwort-Reset und der Hinweis „Konto existiert bereits“), in der gespeicherten Locale des Empfängers (`AuthUser.Locale`), andernfalls in der Kultur der Anfrage, andernfalls auf Englisch

### Was NICHT lokalisiert ist {#what-is-not-localized}

- Maschinenlesbare `error`-Codes (`"email_required"`, `"invalid_credentials"` usw.); sie sind Teil des API-Vertrags und bleiben konstant
- OAuth-/OIDC-Fehlercodes und an Entwickler gerichtete Fehlerbeschreibungen an Token-, Autorisierungs- und Widerrufsendpunkt
- Interne Protokollmeldungen und Ausnahmemeldungen

### Lokalisierung des Servers testen {#testing-server-localization}

Senden Sie einen `Accept-Language`-Header an einen beliebigen lokalisierten Endpunkt:

```bash
# English (default)
curl https://auth.example.com/api/auth/password-policy

# Simplified Chinese
curl -H "Accept-Language: zh-Hans" https://auth.example.com/api/auth/password-policy

# German
curl -H "Accept-Language: de" https://auth.example.com/api/auth/password-policy
```

### Ressourcendateien {#resource-files}

Alle Übersetzungen des Servers liegen in `.resx`-Dateien unter `src/Authagonal.Server/Resources/`:

```
Resources/
  SharedMessages.cs          # Marker class
  SharedMessages.resx        # English (default)
  SharedMessages.zh-Hans.resx
  SharedMessages.de.resx
  SharedMessages.fr.resx
  SharedMessages.es.resx
  SharedMessages.vi.resx
  SharedMessages.pt.resx
  SharedMessages.ja.resx
  SharedMessages.ar.resx
  SharedMessages.af.resx
  SharedMessages.hi.resx
```

## Login-UI {#login-ui}

Die Login-SPA verwendet [react-i18next](https://react.i18next.com/) für die clientseitige Lokalisierung. Die Sprache wird automatisch über die Browsereinstellung `navigator.language` erkannt.

Die registrierten Locales stehen in einer einzigen `LANGUAGES`-Registry in `login-app/src/i18n/index.ts`. Sie steuert sowohl die Registrierung der i18next-Ressourcen als auch jede Sprachauswahl, sodass beide nicht auseinanderlaufen können. Derzeit erscheint jede registrierte Locale in der Standardauswahl. `DEFAULT_LANGUAGES` wird getrennt von `LANGUAGES` exportiert, damit eine künftige eingeschränkte Locale aus den Auswahllisten herausgenommen werden kann, ohne die Aufrufstellen anzufassen; heute ist jedoch nichts ausgenommen. Mandanten können die Auswahl auf dieselbe Weise eingrenzen: Ein `languages`-Array in `branding.json` ersetzt die Standardliste vollständig (siehe [Branding](branding)).

Die aktive Sprache wird auf `<html lang>` und `<html dir>` gespiegelt, sodass Sprachen mit Schreibrichtung von rechts nach links (`ar`) die Anmeldekarte automatisch spiegeln, auch wenn die Sprache direkt über die Auswahl gewechselt wird.

### Spracherkennung {#language-detection}

Die Reihenfolge der Erkennung ist:

1. **localStorage**: gespeicherte Präferenz aus einem früheren Besuch
2. **Query-Parameter**: `?lng=de` übersteuert die Erkennung über den Browser
3. **Browsersprache**: `navigator.language` (automatisch)
4. **Fallback**: Englisch (`en`)

### Übersetzungsdateien {#translation-files}

Die JSON-Übersetzungsdateien werden mit der App unter `login-app/src/i18n/` gebündelt:

```
i18n/
  index.ts        # i18n initialization + the LANGUAGES registry
  en.json         # English
  zh-Hans.json    # Simplified Chinese
  de.json         # German
  fr.json         # French
  es.json         # Spanish
  vi.json         # Vietnamese
  pt.json         # Portuguese
  ar.json         # Arabic
  af.json         # Afrikaans
  hi.json         # Hindi
  ja.json         # Japanese
```

### Beschriftungen der Passwortrichtlinie {#password-policy-labels}

Die Seite zum Zurücksetzen des Passworts übersetzt ihre Checkliste der Passwortanforderungen clientseitig anhand des `rule`-Schlüssels, den `GET /api/auth/password-policy` zurückgibt (bei unbekannten Regeln greift sie auf das vom Server gelieferte `label` zurück). So folgen die Anforderungen der in der UI gewählten Sprache, auch wenn der `Accept-Language`-Header des Browsers davon abweicht. Die Registrierungsseite zeigt die vom Server gelieferten `label`-Werte an, die anhand von `Accept-Language` lokalisiert sind.

### Nutzung über das npm-Paket {#npm-package-consumers}

Wenn Sie die Login-App über `@authagonal/login` einbinden, wird die i18n-Instanz exportiert:

```typescript
import { i18n } from '@authagonal/login';

// Change language programmatically
i18n.changeLanguage('de');
```

## Dokumentation {#documentation}

Die Dokumentationsseite ist verzeichnisbasiert aufgebaut. Englische Seiten liegen im Stammverzeichnis, Übersetzungen in Unterverzeichnissen je Locale (`/zh-Hans/`, `/de/`, `/fr/`, `/es/`, `/vi/`, `/pt/`, `/ja/`). Über ein Auswahlmenü in der Seitenleiste lässt sich zwischen den Sprachen wechseln.

## Eine neue Sprache hinzufügen {#adding-a-new-language}

So fügen Sie Unterstützung für eine neue Sprache hinzu (z. B. Italienisch `it`):

### 1. Server {#1-server}

Legen Sie eine neue `.resx`-Datei an, indem Sie die englische kopieren und die Werte übersetzen:

```
src/Authagonal.Server/Resources/SharedMessages.it.resx
```

Fügen Sie `"it"` zu `SupportedLocales.All` in `src/Authagonal.Server/Services/SupportedLocales.cs` hinzu, der einzigen Liste, die sowohl die Middleware zur Lokalisierung von Anfragen als auch die serverseitig gerenderten Seiten lesen:

```csharp
public static readonly string[] All = ["en", "zh-Hans", "de", "fr", "es", "vi", "pt", "ja", "ar", "af", "hi", "it"];
```

### 2. Login-UI {#2-login-ui}

Legen Sie eine neue JSON-Übersetzungsdatei an, indem Sie `en.json` kopieren und die Werte übersetzen:

```
login-app/src/i18n/it.json
```

Registrieren Sie sie im `LANGUAGES`-Array in `login-app/src/i18n/index.ts`. Dieser eine Eintrag registriert die i18next-Ressource und fügt die Sprache jeder Auswahl hinzu:

```typescript
import it from './it.json';

// In the LANGUAGES array:
{ code: 'it', label: 'Italiano', resource: it },
```

### 3. Dokumentation {#3-documentation}

Legen Sie ein neues Verzeichnis mit übersetzten Markdown-Dateien an:

```
docs/it/
  index.md
  installation.md
  quickstart.md
  ...
```

Fügen Sie in `docs/_config.yml` einen Locale-Standardwert hinzu:

```yaml
defaults:
  - scope:
      path: "it"
    values:
      locale: "it"
```

Fügen Sie die Sprachoption der Auswahl in `docs/_layouts/default.html` hinzu.

## Neue Zeichenketten hinzufügen {#adding-new-strings}

### Server {#server}

1. Fügen Sie den Schlüssel und den englischen Wert zu `SharedMessages.resx` hinzu
2. Fügen Sie die übersetzten Werte zur `.resx`-Datei jeder Locale hinzu
3. Greifen Sie über `IStringLocalizer<SharedMessages>` auf die Zeichenkette zu:

```csharp
// Inject via parameter
IStringLocalizer<SharedMessages> localizer

// Use with key
localizer["MyNewKey"].Value

// With format parameters
string.Format(localizer["MyNewKey"].Value, param1)
```

### Login-UI {#login-ui-1}

1. Fügen Sie den Schlüssel und den englischen Wert zu `en.json` hinzu
2. Fügen Sie die übersetzten Werte zur JSON-Datei jeder Locale hinzu
3. Verwenden Sie in Komponenten die Funktion `t()`:

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```
